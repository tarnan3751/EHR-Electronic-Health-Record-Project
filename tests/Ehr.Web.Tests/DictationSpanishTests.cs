using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Ehr.Web.Api.Dictation;
using Xunit;
using static Ehr.Web.Tests.Browser;

namespace Ehr.Web.Tests;

// Choosing the language of a dictation (the expansion plan's step 17; UC 9 and UC 10, Samir Mukshe), through the API
// as the Dictate page's script (js/dictation.js) calls it, against the stub dictation service: what the service is
// asked to start, what comes back, and what is refused. The page itself, in a browser, is DictationSpanishBrowserTests;
// the service's side, the sidecar's test_sessions.py and test_api.py.
public class DictationSpanishTests(WebApps apps) : IClassFixture<WebApps>
{
    readonly DictationSidecarStub service = apps.Dictation.Reset();

    const string Api = DictationApi.Path;

    sealed record Note(Session Session, Guid Patient, string Token);

    async Task<Note> OpenAsync()
    {
        var clinician = await apps.ClinicianAsync();
        var patient = await apps.InsertPatientAsync("Synthetic", "Dictado");
        await apps.AssignAsync(clinician, patient);
        var session = await SignInAsync(apps.OnPrem, clinician);
        return new(session, patient, Token(await session.Client.GetStringAsync($"/patients/{patient}/dictate", Cancel)));
    }

    // As js/dictation.js starts a note's dictation; the language left out when null, or written as given.
    static async Task<HttpResponseMessage> StartAsync(HttpClient client, string token, Guid patient, string? language)
    {
        var body = $"{{\"reviewId\":\"{Guid.NewGuid()}\",\"patientId\":\"{patient}\",\"draftId\":null,\"version\":null"
            + (language is null ? "" : $",\"spokenLanguage\":{language}") + "}";
        using var request = new HttpRequestMessage(HttpMethod.Post, Api + "/for-note") { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        request.Headers.Add("RequestVerificationToken", token);
        return await client.SendAsync(request, Cancel);
    }

    static async Task<HttpResponseMessage> PostAsync(Note note, string path, HttpContent? content = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = content };
        request.Headers.Add("RequestVerificationToken", note.Token);
        return await note.Session.Client.SendAsync(request, Cancel);
    }

    static ByteArrayContent Audio() => new([1, 2, 3]) { Headers = { ContentType = MediaTypeHeaderValue.Parse("audio/webm") } };

    static async Task<JsonElement> JsonAsync(HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync(Cancel)).RootElement.Clone();

    // What the service was asked to start a session with: its body, as JSON.
    string StartBody() =>
        Encoding.UTF8.GetString(Assert.Single(service.Received, r => r.Method == "POST" && r.PathAndQuery == "/v1/sessions").Body);

    // TC-UC9-07 (proposed FR-61): Spanish chosen, the service starts a dictation in Spanish, with its 2 minutes; the
    // transcript comes back in Spanish; and the result says it was in Spanish and names Whisper (TC-UC10-17, NFR-15).
    [Fact]
    public async Task TC_UC9_07_a_dictation_started_in_spanish_is_one_in_spanish()
    {
        var note = await OpenAsync();

        using var started = await StartAsync(note.Session.Client, note.Token, note.Patient, "\"es\"");

        Assert.Equal(HttpStatusCode.Created, started.StatusCode);
        var session = await JsonAsync(started);
        Assert.Equal(120, session.GetProperty("maxSeconds").GetInt32());
        Assert.Equal("""{"spokenLanguage":"es"}""", StartBody());
        var id = session.GetProperty("sessionId").GetString();
        using var chunk = await PostAsync(note, $"{Api}/{id}/audio?seq=0", Audio());
        Assert.Equal(DictationSidecarStub.SpanishSentences[0], (await JsonAsync(chunk)).GetProperty("transcript").GetProperty("text").GetString());
        using var stopped = await PostAsync(note, $"{Api}/{id}/stop");
        var result = await JsonAsync(stopped);
        Assert.Equal("es", result.GetProperty("spokenLanguage").GetString());
        Assert.Equal("openai/whisper-large-v3-turbo", result.GetProperty("provenance").GetProperty("speech").GetProperty("repository").GetString());
    }

    // TC-UC9-08 (proposed FR-61): English is the page's choice until Spanish is picked, and the page never remembers it;
    // a note's dictation started without a language is English, and so, explicitly, is every start of the plain route
    // and of the desktop app's offline recordings, which are never transcribed in Spanish.
    [Fact]
    public async Task TC_UC9_08_english_is_the_default_and_the_plain_and_desktop_starts_are_english()
    {
        var note = await OpenAsync();
        var html = await note.Session.Client.GetStringAsync($"/patients/{note.Patient}/dictate", Cancel);
        Assert.Contains("""<select id="dictation-language" class="sc-field__input" autocomplete="off" data-dictation-language>""", html);
        Assert.Contains("""<option value="en" selected>English</option>""", html);
        Assert.Contains("""<option value="es">Spanish, experimental (proposals in English)</option>""", html);
        Assert.Contains("""<label for="dictation-language">Language</label>""", html);

        using (var started = await StartAsync(note.Session.Client, note.Token, note.Patient, null))
        {
            Assert.Equal(HttpStatusCode.Created, started.StatusCode);
            Assert.Equal(180, (await JsonAsync(started)).GetProperty("maxSeconds").GetInt32());
        }

        Assert.Equal("""{"spokenLanguage":"en"}""", StartBody());

        service.Reset();
        using (var plain = await PostAsync(note, Api))
        {
            Assert.Equal(HttpStatusCode.Created, plain.StatusCode);
        }

        Assert.Equal("""{"spokenLanguage":"en"}""", StartBody());

        service.Reset();
        var clinician = await apps.ClinicianAsync();
        var patient = await apps.InsertPatientAsync("Synthetic", "Offline");
        await apps.AssignAsync(clinician, patient);
        var desktop = CreateClient(apps.OnPrem);
        desktop.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", await apps.DesktopTokenAsync());
        using (var offline = await desktop.PostAsync($"/api/desktop/offline-dictation/start?patient={patient}", null, Cancel))
        {
            Assert.Equal(HttpStatusCode.Created, offline.StatusCode);
        }

        Assert.Equal("""{"spokenLanguage":"en"}""", StartBody());
    }

    // TC-UC9-09 (invalid input; proposed FR-61): a language that isn't exactly "en" or "es" is refused before the service
    // is asked; and Spanish on a server without Whisper is refused with words the page shows, nothing bound.
    [Theory]
    [InlineData("\"fr\"")]
    [InlineData("\"ES\"")]
    [InlineData("\" es\"")]
    [InlineData("\"Spanish\"")]
    [InlineData("1")]
    [InlineData("null")]
    [InlineData("[\"es\"]")]
    public async Task TC_UC9_09_a_language_other_than_en_or_es_is_refused(string language)
    {
        var note = await OpenAsync();

        using var refused = await StartAsync(note.Session.Client, note.Token, note.Patient, language);

        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        Assert.Empty(service.Received);
    }

    [Fact]
    public async Task TC_UC9_09_spanish_without_whisper_installed_says_so()
    {
        service.SpanishUnavailable = true;
        var note = await OpenAsync();

        using var refused = await StartAsync(note.Session.Client, note.Token, note.Patient, "\"es\"");

        Assert.Equal(HttpStatusCode.ServiceUnavailable, refused.StatusCode);
        var body = await JsonAsync(refused);
        Assert.Equal("language_unavailable", body.GetProperty("error").GetString());
        Assert.Equal("Spanish dictation isn't installed on this server.", body.GetProperty("message").GetString());
        using var english = await StartAsync(note.Session.Client, note.Token, note.Patient, "\"en\"");
        Assert.Equal(HttpStatusCode.Created, english.StatusCode);
    }

    // TC-UC9-10 (FR-10, proposed FR-61), through the API: a passage the service didn't hear as Spanish comes back marked
    // other_language, as a passage too noisy comes back in English. Whisper's verdicts themselves are the sidecar's
    // test_whisper_asr.py (other_language, low_confidence) and test_transcriber.py; the page's marks,
    // DictationSpanishBrowserTests.
    [Fact]
    public async Task TC_UC9_10_a_passage_not_heard_as_spanish_comes_back_marked()
    {
        var note = await OpenAsync();
        using var started = await StartAsync(note.Session.Client, note.Token, note.Patient, "\"es\"");
        var id = (await JsonAsync(started)).GetProperty("sessionId").GetString();

        JsonElement last = default;
        for (var seq = 0; seq < 3; seq++)
        {
            using var chunk = await PostAsync(note, $"{Api}/{id}/audio?seq={seq}", Audio());
            last = await JsonAsync(chunk);
        }

        var segments = last.GetProperty("transcript").GetProperty("segments").EnumerateArray().ToArray();
        Assert.Equal([null, null, "other_language"], segments.Select(s => s.GetProperty("unclear").GetString()));
    }

    // Decision 20 (proposed FR-61): a dictation in Spanish stops at its limit, as one in English stops at 3 minutes: the
    // service refuses the chunk past it, and the API says the dictation reached its time limit.
    [Fact]
    public async Task A_dictation_in_spanish_is_refused_past_its_limit()
    {
        service.SpanishMaxSeconds = 4;
        var note = await OpenAsync();
        using var started = await StartAsync(note.Session.Client, note.Token, note.Patient, "\"es\"");
        var id = (await JsonAsync(started)).GetProperty("sessionId").GetString();
        using (var first = await PostAsync(note, $"{Api}/{id}/audio?seq=0", Audio()))
        using (var second = await PostAsync(note, $"{Api}/{id}/audio?seq=1", Audio()))
        {
            Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        }

        using var past = await PostAsync(note, $"{Api}/{id}/audio?seq=2", Audio());

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, past.StatusCode);
        var refusal = await JsonAsync(past);
        Assert.Equal("too_long", refusal.GetProperty("error").GetString());
        Assert.Equal("This dictation has reached its time limit. Stop it and start another.", refusal.GetProperty("message").GetString());
    }

    // TC-UC10-16 (FR-11, FR-13, proposed FR-61), through the API: the model too slow or unavailable on a dictation in
    // Spanish keeps the Spanish transcript as plain text and says why, as for English. The service's side is
    // test_sessions.py's test_when_the_model_fails_a_spanish_transcript_stays_and_says_why.
    [Theory]
    [InlineData(StubSorting.Timeout, "model_timeout")]
    [InlineData(StubSorting.Unavailable, "model_unavailable")]
    public async Task TC_UC10_16_with_the_model_failing_a_spanish_transcript_stays_and_says_why(StubSorting sorting, string reason)
    {
        service.Sorting = sorting;
        var note = await OpenAsync();
        using var started = await StartAsync(note.Session.Client, note.Token, note.Patient, "\"es\"");
        var id = (await JsonAsync(started)).GetProperty("sessionId").GetString();
        using (var chunk = await PostAsync(note, $"{Api}/{id}/audio?seq=0", Audio()))
        {
            Assert.Equal(HttpStatusCode.OK, chunk.StatusCode);
        }

        using var stopped = await PostAsync(note, $"{Api}/{id}/stop");

        var result = await JsonAsync(stopped);
        Assert.Equal(("unavailable", reason, "es"), (result.GetProperty("sorting").GetString(), result.GetProperty("reason").GetString(),
            result.GetProperty("spokenLanguage").GetString()));
        Assert.Equal(DictationResult.UnsortedMessage, result.GetProperty("message").GetString());
        Assert.Equal(DictationSidecarStub.SpanishSentences[0], result.GetProperty("transcript").GetProperty("text").GetString());
    }
}
