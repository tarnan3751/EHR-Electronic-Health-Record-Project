using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Ehr.Data.Accounts;
using Ehr.Testing;
using Ehr.Web.Api.Dictation;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Xunit;
using static Ehr.Web.Tests.Browser;

namespace Ehr.Web.Tests;

// The clinician dictation API (Api/Dictation/DictationApi.cs) as the note screens will call it, with the staff cookie
// and a page's form token, against the on-prem app's stub dictation service (DictationSidecarStub). The tests in this
// class run one at a time, so each can tell what reached the service.
public class DictationApiTests(WebApps apps) : IClassFixture<WebApps>
{
    const string Sessions = DictationApi.Path;

    // An ID the service could have issued, for requests that must never reach it.
    const string SomeSessionId = "AbCdEfGhIjKlMnOpQrStUvWxYz012345";

    readonly DictationSidecarStub service = apps.Dictation.Reset();

    static CancellationToken Cancel => TestContext.Current.CancellationToken;

    // TC-UC10-01 (FR-08, FR-11, FR-12, FR-13): the proposed fields come back awaiting review, with their sources. Also
    // TC-UC9-01 (FR-07, FR-10, FR-32, NFR-14), through the web app: the transcript grows as the clinician speaks.
    [Fact]
    public async Task A_clinician_dictates_and_gets_the_fields_as_pending_proposals()
    {
        var page = await OpenAsync(apps, apps.OnPrem);

        var session = await StartAsync(page);
        using var first = await SendAsync(page, HttpMethod.Post, $"{Sessions}/{session.SessionId}/audio?seq=0", Audio([1, 2, 3]));
        using var second = await SendAsync(page, HttpMethod.Post, $"{Sessions}/{session.SessionId}/audio?seq=1", Audio([4, 5, 6]));
        using var stopped = await SendAsync(page, HttpMethod.Post, $"{Sessions}/{session.SessionId}/stop");
        using var sorted = await SendAsync(page, HttpMethod.Post, $"{Sessions}/{session.SessionId}/result");

        Assert.Equal((180, 1024 * 1024), (session.MaxSeconds, session.MaxChunkBytes));
        // The transcript comes back while the clinician speaks.
        Assert.Equal("Cough for three days.", (await ReadJsonAsync<AudioReceived>(first)).Transcript.Text);
        var received = await ReadJsonAsync<AudioReceived>(second);
        Assert.Equal(1, received.ReceivedSeq);
        Assert.Equal("Cough for three days. No fever.", received.Transcript.Text);
        Assert.False(received.Transcript.Final);
        Assert.Equal([new TranscriptSegment(0, 21, 0, 1800, true), new TranscriptSegment(22, 31, 2000, 3800, false)], received.Transcript.Segments);

        var atStop = await ReadJsonAsync<DictationResult>(stopped);
        Assert.Equal(Sorting.Pending, atStop.Sorting);
        Assert.True(atStop.Transcript.Final);
        Assert.Null(atStop.Fields);

        var result = await ReadJsonAsync<DictationResult>(sorted);
        Assert.Equal(Sorting.Done, result.Sorting);
        var subjective = Assert.IsType<PendingField>(result.Fields!.Subjective);
        Assert.Equal("Cough for three days.", subjective.Value);
        Assert.Equal([new SourceSpan(0, 21)], subjective.Sources);
        Assert.Empty(subjective.Warnings);
        Assert.IsType<EmptyField>(result.Fields.Objective);
        Assert.Null(result.Fields.History);
        var assessment = Assert.IsType<PendingField>(result.Fields.Assessment);
        Assert.Equal("No fever.", assessment.Value);
        Assert.Equal([new SourceSpan(22, 31)], assessment.Sources);
        Assert.Equal(
            [new FieldWarning(WarningCode.PossibleCorrection, 0, 5), new FieldWarning(WarningCode.NearUnclearPassage, 6, 9)],
            assessment.Warnings);
        Assert.Equal(["not_dictated"], Assert.IsType<EmptyField>(result.Fields.Plan).Reasons);
        Assert.Equal("google/medasr", result.Provenance.Speech.Repository);
        Assert.Equal("K2-Horizon-7B-Q4_K_M.gguf", result.Provenance.Language?.File);
        Assert.StartsWith("sha256:", result.Provenance.Code);
        Assert.Equal([new NotCarriedOver(Instruction.Order, 0, 5)], result.NotCarriedOver);
        Assert.False((await JsonAsync(sorted)).AsObject().ContainsKey("message"));
        // The result waits for the fields, up to 10 seconds.
        Assert.Contains(service.Received, request => request.PathAndQuery == $"/v1/sessions/{session.SessionId}/result?wait=10");
    }

    [Fact]
    public async Task Audio_reaches_the_service_as_sent_with_the_clinicians_id_and_the_key()
    {
        var clinician = await apps.ClinicianAsync();
        var page = await OpenAsync(apps, apps.OnPrem);
        var session = await StartAsync(page);
        var audio = RandomNumberGenerator.GetBytes(64 * 1024);
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{Sessions}/{session.SessionId}/audio?seq=0")
        {
            Content = Audio(audio, "audio/mp4; codecs=\"mp4a.40.2\""),
        };
        request.Headers.Add("RequestVerificationToken", page.Token);
        // Another account's ID, which must not become the owner.
        request.Headers.Add(DictationSidecar.OwnerHeader, Guid.NewGuid().ToString());

        using var response = await page.Client.SendAsync(request, Cancel);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var sent = Assert.Single(service.Received, received => received.PathAndQuery == $"/v1/sessions/{session.SessionId}/audio?seq=0");
        Assert.Equal("POST", sent.Method);
        Assert.Equal(audio, sent.Body);
        Assert.Equal("audio/mp4; codecs=\"mp4a.40.2\"", sent.Headers["Content-Type"]);
        Assert.Equal(clinician.Id.ToString(), sent.Headers[DictationSidecar.OwnerHeader]);
        Assert.Equal($"Bearer {DictationSidecarStub.ApiKey}", sent.Headers["Authorization"]);
        // And nothing of the browser's: no cookie, no form token, no other header.
        Assert.Equal(
            ["Authorization", "Content-Type", DictationSidecar.OwnerHeader],
            sent.Headers.Keys.Where(name => name != "Content-Length").Order(StringComparer.Ordinal));
        Assert.All(service.Received, received => Assert.Equal(clinician.Id.ToString(), received.Headers[DictationSidecar.OwnerHeader]));
    }

    [Fact]
    public async Task The_chunk_limit_given_is_never_over_the_apis_own()
    {
        service.Answer = (HttpStatusCode.Created, $$"""{"sessionId":"{{SomeSessionId}}","maxSeconds":600,"maxChunkBytes":{{4 * 1024 * 1024}}}""");
        var page = await OpenAsync(apps, apps.OnPrem);

        var session = await StartAsync(page);

        Assert.Equal(DictationApi.MaxChunkBytes, session.MaxChunkBytes);
    }

    [Theory]
    [InlineData("POST", "")]
    [InlineData("POST", "/" + SomeSessionId + "/audio?seq=0")]
    [InlineData("POST", "/" + SomeSessionId + "/stop")]
    [InlineData("POST", "/" + SomeSessionId + "/result")]
    [InlineData("DELETE", "/" + SomeSessionId)]
    public async Task An_anonymous_request_is_refused_with_401_and_reaches_nothing(string method, string route)
    {
        using var client = Client(apps.OnPrem, new CookieContainer(), followRedirects: false);
        using var request = new HttpRequestMessage(new HttpMethod(method), Sessions + route) { Content = Audio([1, 2, 3]) };

        using var response = await client.SendAsync(request, Cancel);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Null(response.Headers.Location);
        Assert.Empty(service.Received);
    }

    [Fact]
    public async Task A_patient_is_refused_with_401()
    {
        var (email, password) = await apps.CreatePatientAccountAsync(await apps.InsertPatientAsync("Sam", "Portal"));
        var client = Client(apps.OnPrem, new CookieContainer());
        using var portal = await PostFormAsync(client, "/portal/sign-in", "/portal/sign-in", ("Input.Email", email), ("Input.Password", password));
        Assert.Equal("/portal", PathOf(portal));
        // With the patient's own form token: it's the account that is refused.
        var page = new Page(client, Token(await ReadAsync(portal)));

        using var response = await SendAsync(page, HttpMethod.Post, Sessions);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Empty(service.Received);
    }

    [Fact]
    public async Task An_admin_is_refused_with_403()
    {
        var page = await OpenAsAsync(apps.OnPrem, await apps.CreateStaffAsync("Dictation Admin", StaffRoles.Admin));

        using var response = await SendAsync(page, HttpMethod.Post, Sessions);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Null(response.Headers.Location);
        Assert.Empty(service.Received);
    }

    // The staff cookie itself is only checked every few minutes; dictation checks the account on every call.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_clinician_locked_out_or_whose_password_changed_is_refused_on_the_next_call(bool passwordChanged)
    {
        var account = await apps.CreateStaffAsync("Dr. Dictation Elsewhere", StaffRoles.Clinician);
        var page = await OpenAsAsync(apps.OnPrem, account);
        var session = await StartAsync(page);
        var received = service.Received.Count;

        if (passwordChanged)
        {
            await apps.UpdateSecurityStampAsync(account);
        }
        else
        {
            await LockOutAsync(account);
        }

        foreach (var (method, path, content) in new (HttpMethod, string, HttpContent?)[]
                 {
                     (HttpMethod.Post, Sessions, null),
                     (HttpMethod.Post, $"{Sessions}/{session.SessionId}/audio?seq=0", Audio([1, 2, 3])),
                     (HttpMethod.Post, $"{Sessions}/{session.SessionId}/stop", null),
                     (HttpMethod.Post, $"{Sessions}/{session.SessionId}/result", null),
                     (HttpMethod.Delete, $"{Sessions}/{session.SessionId}", null),
                 })
        {
            using var refused = await SendAsync(page, method, path, content);
            Assert.Equal(HttpStatusCode.Unauthorized, refused.StatusCode);
            Assert.Null(refused.Headers.Location);
        }

        Assert.Equal(received, service.Received.Count);
    }

    [Fact]
    public async Task A_request_without_the_form_token_is_refused()
    {
        var page = await OpenAsync(apps, apps.OnPrem);

        using var missing = await page.Client.PostAsync(Sessions, null, Cancel);
        using var wrong = await SendAsync(page, HttpMethod.Post, Sessions, token: "not-a-form-token");

        Assert.Equal(HttpStatusCode.BadRequest, missing.StatusCode);
        Assert.Equal("bad_form_token", await ErrorAsync(missing));
        Assert.Equal(HttpStatusCode.BadRequest, wrong.StatusCode);
        Assert.Empty(service.Received);
    }

    // Also TC-UC9-03 (FR-10): anything but audio is refused, 415. The primary test is in the sidecar's test_sessions.py.
    [Theory]
    [InlineData("text/plain")]
    [InlineData("application/json")]
    [InlineData("video/webm")]
    [InlineData(null)]
    public async Task Anything_but_audio_is_refused_with_415(string? type)
    {
        var page = await OpenAsync(apps, apps.OnPrem);
        var content = new ByteArrayContent([1, 2, 3]);
        if (type is not null)
        {
            content.Headers.ContentType = MediaTypeHeaderValue.Parse(type);
        }

        using var response = await SendAsync(page, HttpMethod.Post, $"{Sessions}/{SomeSessionId}/audio?seq=0", content);

        Assert.Equal(HttpStatusCode.UnsupportedMediaType, response.StatusCode);
        Assert.Equal("unsupported_type", await ErrorAsync(response));
        Assert.Empty(service.Received);
    }

    [Fact]
    public async Task A_chunk_over_2_MB_is_refused_with_413()
    {
        var page = await OpenAsync(apps, apps.OnPrem);
        var session = await StartAsync(page);

        using var tooLarge = await SendAsync(page, HttpMethod.Post, $"{Sessions}/{session.SessionId}/audio?seq=0", Audio(new byte[DictationApi.MaxChunkBytes + 1]));
        using var largest = await SendAsync(page, HttpMethod.Post, $"{Sessions}/{session.SessionId}/audio?seq=0", Audio(new byte[DictationApi.MaxChunkBytes]));

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, tooLarge.StatusCode);
        Assert.Equal("too_large", await ErrorAsync(tooLarge));
        Assert.Equal(HttpStatusCode.OK, largest.StatusCode);
        Assert.Equal(DictationApi.MaxChunkBytes, Assert.Single(service.Received, request => request.PathAndQuery.Contains("/audio")).Body.Length);
    }

    // Also TC-UC9-05 (NFR-07): the web app saves nothing of a dictation. The primary test is the sidecar's test_logging.py.
    [Fact]
    public async Task Nothing_is_saved_by_a_whole_dictation()
    {
        var page = await OpenAsync(apps, apps.OnPrem);
        var before = await RowCountsAsync();

        var session = await StartAsync(page);
        var routes = new (HttpMethod Method, string Path, HttpContent? Content)[]
        {
            (HttpMethod.Post, $"{Sessions}/{session.SessionId}/audio?seq=0", Audio([1, 2, 3])),
            (HttpMethod.Post, $"{Sessions}/{session.SessionId}/audio?seq=1", Audio([4, 5, 6])),
            (HttpMethod.Post, $"{Sessions}/{session.SessionId}/stop", null),
            (HttpMethod.Post, $"{Sessions}/{session.SessionId}/result", null),
            (HttpMethod.Delete, $"{Sessions}/{session.SessionId}", null),
        };
        var statuses = new List<HttpStatusCode>();
        foreach (var (method, path, content) in routes)
        {
            using var response = await SendAsync(page, method, path, content);
            statuses.Add(response.StatusCode);
        }

        // Cancelled, the session is gone.
        using var afterCancel = await SendAsync(page, HttpMethod.Post, $"{Sessions}/{session.SessionId}/stop");

        Assert.Equal([HttpStatusCode.OK, HttpStatusCode.OK, HttpStatusCode.OK, HttpStatusCode.OK, HttpStatusCode.NoContent], statuses);
        Assert.Equal(HttpStatusCode.NotFound, afterCancel.StatusCode);
        Assert.NotEmpty(before);
        Assert.Equal(before, await RowCountsAsync());
    }

    [Fact]
    public async Task A_model_timeout_returns_the_transcript_and_says_the_fields_could_not_be_sorted()
    {
        service.Sorting = StubSorting.Timeout;
        var page = await OpenAsync(apps, apps.OnPrem);

        var json = await DictateAndStopAsync(page);

        Assert.Equal("unavailable", (string?)json["sorting"]);
        Assert.Equal("model_timeout", (string?)json["reason"]);
        Assert.Equal("The fields couldn't be sorted, so the transcript is kept as plain text.", (string?)json["message"]);
        Assert.Equal("Cough for three days. No fever.", (string?)json["transcript"]!["text"]);
        Assert.True(json.AsObject().ContainsKey("fields"));
        Assert.Null(json["fields"]);
    }

    // TC-UC10-04 (FR-11, FR-13): the language model is down, so the transcript comes back as plain text and the
    // fields aren't sorted. Also covered by the sidecar's test_sessions.py and DictationPageTests.
    [Fact]
    public async Task An_unavailable_model_returns_the_transcript_and_says_the_fields_could_not_be_sorted()
    {
        service.Sorting = StubSorting.Unavailable;
        var page = await OpenAsync(apps, apps.OnPrem);

        var json = await DictateAndStopAsync(page);

        Assert.Equal("unavailable", (string?)json["sorting"]);
        Assert.Equal("model_unavailable", (string?)json["reason"]);
        Assert.Equal("The fields couldn't be sorted, so the transcript is kept as plain text.", (string?)json["message"]);
        Assert.Equal("Cough for three days. No fever.", (string?)json["transcript"]!["text"]);
        Assert.All(["subjective", "objective", "assessment", "plan"], field => Assert.Equal("empty", (string?)json["fields"]![field]!["status"]));
    }

    [Fact]
    public async Task Discarded_values_and_anything_else_the_service_adds_never_reach_the_browser()
    {
        service.Sorting = StubSorting.Discarded;
        var page = await OpenAsync(apps, apps.OnPrem);
        var session = await StartAsync(page);
        for (var seq = 0; seq < 3; seq++)
        {
            using var chunk = await SendAsync(page, HttpMethod.Post, $"{Sessions}/{session.SessionId}/audio?seq={seq}", Audio([1, 2, 3]));
            Assert.Equal(HttpStatusCode.OK, chunk.StatusCode);
        }

        using var stopped = await SendAsync(page, HttpMethod.Post, $"{Sessions}/{session.SessionId}/stop");

        var text = await ReadAsync(stopped);
        Assert.DoesNotContain(DictationSidecarStub.DiscardedText, text);
        var json = JsonNode.Parse(text)!;
        Assert.Equal(["fields", "notCarriedOver", "provenance", "sorting", "transcript"], Names(json));
        Assert.Equal(["end", "reason", "start"], Names(json["notCarriedOver"]![0]!));
        Assert.Equal(["final", "segments", "text"], Names(json["transcript"]!));
        Assert.Equal(["end", "endMs", "final", "start", "startMs", "unclear"], Names(json["transcript"]!["segments"]![0]!));
        Assert.Equal("too_noisy", (string?)json["transcript"]!["segments"]![2]!["unclear"]);
        Assert.Equal(["assessment", "objective", "plan", "subjective"], Names(json["fields"]!));
        Assert.Equal(["sources", "status", "value", "warnings"], Names(json["fields"]!["subjective"]!));
        Assert.Equal(["code", "end", "start"], Names(json["fields"]!["subjective"]!["warnings"]![0]!));
        Assert.Equal(["reasons", "status"], Names(json["fields"]!["plan"]!));
        Assert.Equal("not_in_transcript", (string?)json["fields"]!["plan"]!["reasons"]![0]);
    }

    [Fact]
    public async Task An_unreachable_service_gives_503()
    {
        service.Unreachable = true;
        var page = await OpenAsync(apps, apps.OnPrem);

        using var response = await SendAsync(page, HttpMethod.Post, Sessions);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal("unavailable", await ErrorAsync(response));
    }

    [Fact]
    public async Task A_service_that_does_not_answer_in_time_gives_504()
    {
        service.Slow = true;
        var page = await OpenAsync(apps, apps.OnPrem);

        using var response = await SendAsync(page, HttpMethod.Post, Sessions);

        Assert.Equal(HttpStatusCode.GatewayTimeout, response.StatusCode);
        Assert.Equal("timed_out", await ErrorAsync(response));
    }

    [Fact]
    public async Task Without_its_settings_dictation_gives_503()
    {
        await using var unconfigured = apps.OnPrem.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
            services.AddSingleton(DictationSidecar.FromConfiguration(new ConfigurationBuilder().Build()))));
        var page = await OpenAsync(apps, unconfigured);

        using var response = await SendAsync(page, HttpMethod.Post, Sessions);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Empty(service.Received);
    }

    [Fact]
    public async Task Off_site_dictation_is_forwarded_to_the_on_prem_app()
    {
        var clinician = await apps.ClinicianAsync();
        var page = await OpenAsync(apps, apps.Cloud);
        var forwarded = apps.Forwarded.Count;

        var session = await StartAsync(page);
        using var audio = await SendAsync(page, HttpMethod.Post, $"{Sessions}/{session.SessionId}/audio?seq=0", Audio([1, 2, 3]));
        using var cancelled = await SendAsync(page, HttpMethod.Delete, $"{Sessions}/{session.SessionId}");

        Assert.Equal(HttpStatusCode.OK, audio.StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, cancelled.StatusCode);
        Assert.Equal(
            [$"POST {Sessions}", $"POST {Sessions}/{session.SessionId}/audio?seq=0", $"DELETE {Sessions}/{session.SessionId}"],
            apps.Forwarded.Skip(forwarded));
        Assert.Equal(3, service.Received.Count);
        Assert.All(service.Received, received => Assert.Equal(clinician.Id.ToString(), received.Headers[DictationSidecar.OwnerHeader]));
    }

    [Fact]
    public async Task Off_site_with_the_link_down_dictation_says_the_server_cannot_be_reached()
    {
        var page = await OpenAsync(apps, apps.CloudCutOff, via: apps.Cloud);

        using var response = await SendAsync(page, HttpMethod.Post, Sessions);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Contains("can't be reached", await response.Content.ReadAsStringAsync(Cancel));
        Assert.Empty(service.Received);
    }

    [Fact]
    public async Task Another_clinicians_session_is_not_found()
    {
        var owner = await OpenAsync(apps, apps.OnPrem);
        var session = await StartAsync(owner);
        var other = await OpenAsAsync(apps.OnPrem, await apps.CreateStaffAsync("Dr. Elsewhere", StaffRoles.Clinician));

        foreach (var (method, path, content) in new (HttpMethod, string, HttpContent?)[]
                 {
                     (HttpMethod.Post, $"{Sessions}/{session.SessionId}/audio?seq=0", Audio([1, 2, 3])),
                     (HttpMethod.Post, $"{Sessions}/{session.SessionId}/stop", null),
                     (HttpMethod.Post, $"{Sessions}/{session.SessionId}/result", null),
                     (HttpMethod.Delete, $"{Sessions}/{session.SessionId}", null),
                 })
        {
            using var refused = await SendAsync(other, method, path, content);
            Assert.Equal(HttpStatusCode.NotFound, refused.StatusCode);
            Assert.Equal("not_found", await ErrorAsync(refused));
        }

        // Still the owner's, untouched.
        using var owners = await SendAsync(owner, HttpMethod.Post, $"{Sessions}/{session.SessionId}/audio?seq=0", Audio([1, 2, 3]));
        Assert.Equal(HttpStatusCode.OK, owners.StatusCode);
    }

    [Theory]
    [InlineData("short")]
    [InlineData("AbCdEfGhIjKlMnOp.QrSt")]
    [InlineData("AbCdEfGhIjKlMnOp%2F..%2Fsessions")]
    [InlineData("AbCdEfGhIjKlMnOpQrStUvWx%0A")]
    [InlineData("AbCdEfGhIjKlMnOpQrStUvWxYz012345AbCdEfGhIjKlMnOpQrStUvWxYz0123456")]
    public async Task A_malformed_session_id_is_not_found_and_never_sent(string id)
    {
        var page = await OpenAsync(apps, apps.OnPrem);

        using var audio = await SendAsync(page, HttpMethod.Post, $"{Sessions}/{id}/audio?seq=0", Audio([1, 2, 3]));
        using var stop = await SendAsync(page, HttpMethod.Post, $"{Sessions}/{id}/stop");
        using var result = await SendAsync(page, HttpMethod.Post, $"{Sessions}/{id}/result");
        using var cancel = await SendAsync(page, HttpMethod.Delete, $"{Sessions}/{id}");

        Assert.All([audio, stop, result, cancel], response => Assert.Equal(HttpStatusCode.NotFound, response.StatusCode));
        Assert.Empty(service.Received);
    }

    // How transcription resumes after a dropped connection: the service says which chunk it needs next.
    // Also TC-UC9-04 (FR-07, FR-10), through the web app. The primary test is the sidecar's test_sessions.py.
    [Fact]
    public async Task A_chunk_out_of_order_is_answered_with_the_one_to_send_next()
    {
        var page = await OpenAsync(apps, apps.OnPrem);
        var session = await StartAsync(page);
        using var first = await SendAsync(page, HttpMethod.Post, $"{Sessions}/{session.SessionId}/audio?seq=0", Audio([1, 2, 3]));

        using var skipped = await SendAsync(page, HttpMethod.Post, $"{Sessions}/{session.SessionId}/audio?seq=2", Audio([7, 8, 9]));
        using var resent = await SendAsync(page, HttpMethod.Post, $"{Sessions}/{session.SessionId}/audio?seq=1", Audio([4, 5, 6]));

        Assert.Equal(HttpStatusCode.Conflict, skipped.StatusCode);
        var refusal = await JsonAsync(skipped);
        Assert.Equal(["error", "expectedSeq", "message"], Names(refusal));
        Assert.Equal("out_of_order", (string?)refusal["error"]);
        Assert.Equal(1, (long?)refusal["expectedSeq"]);
        Assert.Equal(1, (await ReadJsonAsync<AudioReceived>(resent)).ReceivedSeq);
    }

    [Fact]
    public async Task A_result_before_stop_and_a_chunk_after_it_are_conflicts()
    {
        var page = await OpenAsync(apps, apps.OnPrem);
        var session = await StartAsync(page);

        using var early = await SendAsync(page, HttpMethod.Post, $"{Sessions}/{session.SessionId}/result");
        using var stopped = await SendAsync(page, HttpMethod.Post, $"{Sessions}/{session.SessionId}/stop");
        using var late = await SendAsync(page, HttpMethod.Post, $"{Sessions}/{session.SessionId}/audio?seq=0", Audio([1, 2, 3]));

        Assert.Equal(HttpStatusCode.Conflict, early.StatusCode);
        Assert.Equal("not_stopped", await ErrorAsync(early));
        Assert.Equal(HttpStatusCode.OK, stopped.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, late.StatusCode);
        Assert.Equal("stopped", await ErrorAsync(late));
        // A dictation's words; a visit's recording says it was stopped (TC-UC23-04).
        Assert.Equal("This dictation has stopped, so it takes no more audio.", (string?)(await JsonAsync(late))["message"]);
    }

    // Also TC-UC9-03 (FR-10): audio that can't be decoded is refused, 422. The primary test is the sidecar's test_sessions.py.
    [Fact]
    public async Task Audio_that_cannot_be_decoded_is_refused_with_422()
    {
        service.Undecodable = true;
        var page = await OpenAsync(apps, apps.OnPrem);
        var session = await StartAsync(page);
        using var chunk = await SendAsync(page, HttpMethod.Post, $"{Sessions}/{session.SessionId}/audio?seq=0", Audio([1, 2, 3]));

        using var stopped = await SendAsync(page, HttpMethod.Post, $"{Sessions}/{session.SessionId}/stop");

        Assert.Equal(HttpStatusCode.UnprocessableEntity, stopped.StatusCode);
        var refusal = await JsonAsync(stopped);
        Assert.Equal(["error", "message"], Names(refusal));
        Assert.Equal("not_audio", (string?)refusal["error"]);
        Assert.Equal("The audio couldn't be decoded.", (string?)refusal["message"]);
    }

    // The service's own refusals become the API's, typed: its detail never reaches the browser.
    [Theory]
    [InlineData(404, """{"error":"not_found","detail":"no such session"}""", 404, "not_found")]
    [InlineData(409, """{"error":"something_new","detail":"a code the API doesn't know"}""", 502, "bad_answer")]
    [InlineData(413, """{"error":"too_large","detail":"over the session's length"}""", 413, "too_large")]
    [InlineData(422, """{"error":"not_audio","detail":"the decoder failed"}""", 422, "not_audio")]
    [InlineData(429, """{"error":"busy","detail":"every worker is dictating"}""", 429, "busy")]
    [InlineData(401, """{"error":"unauthorized","detail":"wrong key"}""", 503, "unavailable")]
    [InlineData(403, """{"error":"forbidden","detail":"wrong key"}""", 503, "unavailable")]
    [InlineData(500, """{"error":"internal","detail":"a stack trace"}""", 502, "bad_answer")]
    [InlineData(503, """{"error":"starting","detail":"loading the model"}""", 503, "unavailable")]
    [InlineData(200, """{"transcript":"not the contract's shape"}""", 502, "bad_answer")]
    public async Task The_services_refusals_become_the_apis_own(int status, string body, int expected, string error)
    {
        service.Answer = ((HttpStatusCode)status, body);
        var page = await OpenAsync(apps, apps.OnPrem);

        using var response = await SendAsync(page, HttpMethod.Post, $"{Sessions}/{SomeSessionId}/stop");

        Assert.Equal(expected, (int)response.StatusCode);
        var text = await response.Content.ReadAsStringAsync(Cancel);
        Assert.DoesNotContain("detail", text);
        Assert.Equal(error, (string?)JsonNode.Parse(text)!["error"]);
    }

    // Every answer the real service gives, pinned in src/Ehr.Dictation/contract by the service's own tests from its own
    // code, is read and passed on: none is refused as a bad answer, each keeps its status and code (a refused key is
    // this API's 503), and a result keeps its sorting. A visit's (the expansion plan's step 16) through its own routes.
    [Theory]
    [MemberData(nameof(ContractAnswers))]
    public async Task Every_answer_the_real_service_gives_is_read_and_passed_on(string name)
    {
        var contract = JsonNode.Parse(await File.ReadAllTextAsync(ContractFile(name), Cancel))!;
        var status = (int)contract["status"]!;
        var body = contract["body"];
        var answer = ((HttpStatusCode)status, body?.ToJsonString() ?? "");
        var route = (string?)contract["route"] ?? "";

        using var response = route.EndsWith("-ambient", StringComparison.Ordinal)
            ? await AskForAVisitAsync(route, answer)
            : await AskAsync(name, route, answer);

        Assert.NotEqual(HttpStatusCode.BadGateway, response.StatusCode);
        Assert.Equal(status == 401 ? 503 : status, (int)response.StatusCode);
        if (body is null)
        {
            Assert.Empty(await response.Content.ReadAsStringAsync(Cancel));
            return;
        }

        var answered = await JsonAsync(response);
        if (status >= 400)
        {
            Assert.Equal(status == 401 ? "unavailable" : (string?)body["error"], (string?)answered["error"]);
        }
        else if (body["sorting"] is { } sorting)
        {
            Assert.Equal((string?)sorting, (string?)answered["sorting"]);
            Assert.Equal((string?)body["transcript"]!["text"], (string?)answered["transcript"]!["text"]);
        }
    }

    async Task<HttpResponseMessage> AskAsync(string name, string route, (HttpStatusCode, string) answer)
    {
        service.Answer = answer;
        var page = await OpenAsync(apps, apps.OnPrem);
        return route switch
        {
            "start" => await SendAsync(page, HttpMethod.Post, Sessions),
            "audio" => await SendAsync(page, HttpMethod.Post, $"{Sessions}/{SomeSessionId}/audio?seq=0", Audio([1, 2, 3])),
            "stop" => await SendAsync(page, HttpMethod.Post, $"{Sessions}/{SomeSessionId}/stop"),
            "result" => await SendAsync(page, HttpMethod.Post, $"{Sessions}/{SomeSessionId}/result"),
            "cancel" => await SendAsync(page, HttpMethod.Delete, $"{Sessions}/{SomeSessionId}"),
            _ => throw new InvalidOperationException($"{name}.json has a route this test doesn't know: {route}."),
        };
    }

    // A visit's routes (AmbientApi.cs) take a visit the clinician is listening to, with its consent and binding, so the
    // visit starts first and the pinned answer is set after; a start's is the answer to the start.
    async Task<HttpResponseMessage> AskForAVisitAsync(string route, (HttpStatusCode, string) answer)
    {
        const string Listen = DictationApi.AmbientPath;
        if (route == "start-ambient")
        {
            var (session, patient, token) = await AmbientFlows.OpenAsync(apps);
            service.Answer = answer;
            return await AmbientFlows.Post(session.Client, token, $"{Listen}/start", AmbientFlows.Consent(Guid.NewGuid(), patient));
        }

        if (route == "cancel-ambient")
        {
            var page = await OpenAsync(apps, apps.OnPrem);
            service.Answer = answer;
            return await SendAsync(page, HttpMethod.Delete, $"{Listen}/{SomeSessionId}");
        }

        var visit = await AmbientFlows.StartAsync(apps);
        service.Answer = answer;
        var path = $"{Listen}/{visit.SessionId}";
        return route switch
        {
            "audio-ambient" => await AmbientFlows.Audio(visit, 0),
            "stop-ambient" => await AmbientFlows.Post(visit.Session.Client, visit.Token, $"{path}/stop"),
            "speakers-ambient" => await AmbientFlows.Post(visit.Session.Client, visit.Token, $"{path}/speakers",
                new { speakers = new Dictionary<string, string> { ["1"] = "clinician", ["2"] = "patient" } }),
            "result-ambient" => await AmbientFlows.Post(visit.Session.Client, visit.Token, $"{path}/result"),
            _ => throw new InvalidOperationException($"A contract file has a route this test doesn't know: {route}."),
        };
    }

    // An empty field's reason codes are one list in three places, so none drifts: the service's (validation.py's REASONS),
    // the contract's (EmptyField.Codes, which a visit's are held to as it is staged), and the database's guard on a
    // visit's stored result (db/policies/56-ambient-listening.sql).
    [Fact]
    public async Task An_empty_fields_reasons_are_the_services_own_codes_in_the_contract_and_the_database()
    {
        static List<string> Quoted(string text) => [.. System.Text.RegularExpressions.Regex.Matches(text, "\"([a-z_]+)\"").Select(m => m.Groups[1].Value)];
        var service = await File.ReadAllTextAsync(RepositoryRoot.Combine("src/Ehr.Dictation/sidecar/ehr_dictation/validation.py"), Cancel);
        var reasons = System.Text.RegularExpressions.Regex.Match(service, @"(?m)^REASONS = \((.*?)^\)", System.Text.RegularExpressions.RegexOptions.Singleline);
        Assert.True(reasons.Success, "validation.py has no REASONS.");
        Assert.Equal(Quoted(reasons.Groups[1].Value), EmptyField.Codes);

        var policies = await File.ReadAllTextAsync(RepositoryRoot.Combine("db/policies/56-ambient-listening.sql"), Cancel);
        var guarded = System.Text.RegularExpressions.Regex.Match(policies, @"WHERE r NOT IN \((.*?)\)\)", System.Text.RegularExpressions.RegexOptions.Singleline);
        Assert.True(guarded.Success, "56-ambient-listening.sql holds no list of reasons.");
        Assert.Equal(EmptyField.Codes, Quoted(guarded.Groups[1].Value));
    }

    // Why fields couldn't be sorted, in the contract (SortingFailure) and in the database's guard on a visit's stored
    // result: the same list, so a reason the service adds can't be staged by one and refused by the other.
    [Fact]
    public async Task The_reasons_fields_couldnt_be_sorted_are_the_contracts_in_the_database_too()
    {
        var policies = await File.ReadAllTextAsync(RepositoryRoot.Combine("db/policies/56-ambient-listening.sql"), Cancel);
        var guarded = System.Text.RegularExpressions.Regex.Match(policies, @"result -> 'reason' IN \((.*?)\)\)", System.Text.RegularExpressions.RegexOptions.Singleline);
        Assert.True(guarded.Success, "56-ambient-listening.sql holds no list of sorting failures.");
        Assert.Equal(
            Enum.GetValues<SortingFailure>().Select(reason => JsonSerializer.Serialize(reason).Trim('"')),
            System.Text.RegularExpressions.Regex.Matches(guarded.Groups[1].Value, "\"([a-z_]+)\"").Select(m => m.Groups[1].Value));
    }

    public static TheoryData<string> ContractAnswers() =>
        [.. Directory.EnumerateFiles(ContractFile(""), "*.json").Select(path => System.IO.Path.GetFileNameWithoutExtension(path.AsSpan()).ToString()).Order(StringComparer.Ordinal)];

    static string ContractFile(string name) =>
        RepositoryRoot.Combine(name.Length == 0 ? "src/Ehr.Dictation/contract" : $"src/Ehr.Dictation/contract/{name}.json");

    static ByteArrayContent Audio(byte[] bytes, string type = "audio/webm; codecs=opus") =>
        new(bytes) { Headers = { ContentType = MediaTypeHeaderValue.Parse(type) } };

    // As the note screen's script will send it: the browser's cookies, and the page's form token in the header.
    static async Task<HttpResponseMessage> SendAsync(Page page, HttpMethod method, string path, HttpContent? content = null, string? token = null)
    {
        using var request = new HttpRequestMessage(method, path) { Content = content };
        request.Headers.Add("RequestVerificationToken", token ?? page.Token);
        return await page.Client.SendAsync(request, Cancel);
    }

    static async Task<DictationSession> StartAsync(Page page)
    {
        using var response = await SendAsync(page, HttpMethod.Post, Sessions);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<DictationSession>(JsonSerializerOptions.Web, Cancel))!;
    }

    // Two chunks and stop, and what stop answered.
    static async Task<JsonNode> DictateAndStopAsync(Page page)
    {
        var session = await StartAsync(page);
        using var first = await SendAsync(page, HttpMethod.Post, $"{Sessions}/{session.SessionId}/audio?seq=0", Audio([1, 2, 3]));
        using var second = await SendAsync(page, HttpMethod.Post, $"{Sessions}/{session.SessionId}/audio?seq=1", Audio([4, 5, 6]));
        using var stopped = await SendAsync(page, HttpMethod.Post, $"{Sessions}/{session.SessionId}/stop");
        return await JsonAsync(stopped);
    }

    // A page of the app signed in as the account, for its form token.
    static async Task<Page> OpenAsAsync(WebApplicationFactory<Program> app, StaffAccount account)
    {
        var session = await SignInAsync(app, account);
        return new Page(session.Client, Token(await session.Client.GetStringAsync(QuickTexts, Cancel)));
    }

    // Locks the account out for an hour, as too many wrong passwords do. Its security stamp stays as it was, so only the
    // lockout can refuse it.
    async Task LockOutAsync(StaffAccount account)
    {
        await using var scope = apps.OnPrem.Services.CreateAsyncScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<StaffUser>>();
        var user = (await users.FindByIdAsync(account.Id.ToString()))!;
        var stamp = user.SecurityStamp;
        Assert.True((await users.SetLockoutEndDateAsync(user, DateTimeOffset.UtcNow.AddHours(1))).Succeeded);
        Assert.Equal(stamp, (await users.FindByIdAsync(account.Id.ToString()))!.SecurityStamp);
    }

    static async Task<T> ReadJsonAsync<T>(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<T>(JsonSerializerOptions.Web, Cancel))!;
    }

    static async Task<JsonNode> JsonAsync(HttpResponseMessage response) =>
        JsonNode.Parse(await response.Content.ReadAsStringAsync(Cancel))!;

    static async Task<string?> ErrorAsync(HttpResponseMessage response) => (string?)(await JsonAsync(response))["error"];

    static IEnumerable<string> Names(JsonNode node) => node.AsObject().Select(property => property.Key).Order(StringComparer.Ordinal);

    // Every table in the ehr schema and its row count, read as the superuser.
    async Task<Dictionary<string, long>> RowCountsAsync()
    {
        await using var connection = await apps.OpenDatabaseAsync();
        var tables = new List<string>();
        await using (var command = new NpgsqlCommand(
            "SELECT table_name FROM information_schema.tables WHERE table_schema = 'ehr' AND table_type = 'BASE TABLE'", connection))
        await using (var reader = await command.ExecuteReaderAsync(Cancel))
        {
            while (await reader.ReadAsync(Cancel))
            {
                tables.Add(reader.GetString(0));
            }
        }

        var counts = new Dictionary<string, long>();
        foreach (var table in tables)
        {
            await using var count = new NpgsqlCommand($"SELECT count(*) FROM ehr.\"{table}\"", connection);
            counts[table] = (long)(await count.ExecuteScalarAsync(Cancel))!;
        }

        return counts;
    }
}
