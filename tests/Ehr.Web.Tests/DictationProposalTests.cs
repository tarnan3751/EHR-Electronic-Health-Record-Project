using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Ehr.Testing;
using Ehr.Web.Api.Dictation;
using Ehr.Web.CloudAudit;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Xunit;
using static Ehr.Web.Tests.Browser;

namespace Ehr.Web.Tests;

// The expansion plan's step 15 (FR-14 as reworded): the orders and allergies to add a dictation proposes, through the web
// app as each site. The service's proposals reach the review only as its listed instructions, staged once; the review
// shows each in the clinician's own words, marked in the sentence they said it in, to open in the order form or the
// allergy form or to dismiss; nothing is ordered, added or changed on the chart by any of it. The stub proposes both
// kinds, so each test follows an order and an allergy (step 14's lesson: a stub that proposes nothing keeps regressions
// green). The forms opened from a proposal are UC 1's and UC 4's test cases (TestCases/UC1, TestCases/UC4).
public class DictationProposalTests(WebApps apps) : IClassFixture<WebApps>
{
    readonly DictationSidecarStub sidecar = apps.Dictation.Reset();

    // A dictation staged for review: the clinician's session and page token, the patient, the draft, the review, its page,
    // and its two proposals: the order's and the allergy's.
    internal sealed record Dictated(Session Session, string Token, Guid Patient, Guid Draft, Guid Review, string ReviewPath, Guid Order, Guid Allergy);

    // A dictation stopped, not yet staged: the clinician's session and page token, the patient, the review it is bound
    // to, its draft and the service's session.
    internal sealed record Stopped(Session Session, string Token, Guid Patient, Guid Review, Guid Draft, string SessionId);

    // A clinician dictates a new note for a patient on their panel, three sentences, and stops; the stub, unless told
    // otherwise first, then proposes an order and an allergy to add.
    internal static async Task<Stopped> StopAsync(WebApps apps, Session? session = null, Guid? patient = null)
    {
        apps.Dictation.Proposals = true;
        if (session is null)
        {
            var clinician = await apps.ClinicianAsync();
            patient = await apps.InsertPatientAsync("Synthetic", "Proposed");
            await apps.AssignAsync(clinician, patient.Value);
            session = await SignInAsync(apps.OnPrem, clinician);
        }

        var token = Token(await session.Client.GetStringAsync($"/patients/{patient}/dictate", Cancel));
        var review = Guid.NewGuid();
        using var started = await JsonPostAsync(session.Client, token, DictationApi.Path + "/for-note", new { reviewId = review, patientId = patient });
        Assert.Equal(HttpStatusCode.Created, started.StatusCode);
        var binding = JsonDocument.Parse(await started.Content.ReadAsStringAsync(Cancel)).RootElement;
        var sessionId = binding.GetProperty("sessionId").GetString()!;
        for (var seq = 0; seq < 3; seq++)
        {
            using var chunk = new HttpRequestMessage(HttpMethod.Post, $"{DictationApi.Path}/{sessionId}/audio?seq={seq}") { Content = new ByteArrayContent([1, 2, 3]) };
            chunk.Headers.Add("RequestVerificationToken", token);
            chunk.Content.Headers.ContentType = new("audio/webm");
            using var taken = await session.Client.SendAsync(chunk, Cancel);
            Assert.Equal(HttpStatusCode.OK, taken.StatusCode);
        }

        using (var stopped = await JsonPostAsync(session.Client, token, $"{DictationApi.Path}/{sessionId}/stop", new { }))
        {
            Assert.Equal(HttpStatusCode.OK, stopped.StatusCode);
        }

        return new(session, token, patient!.Value, review, binding.GetProperty("draftId").GetGuid(), sessionId);
    }

    // The result as the service gives it to the clinician's browser.
    internal static async Task<JsonNode> ResultAsync(Stopped stopped)
    {
        using var result = await JsonPostAsync(stopped.Session.Client, stopped.Token, $"{DictationApi.Path}/{stopped.SessionId}/result", new { });
        Assert.Equal(HttpStatusCode.OK, result.StatusCode);
        return JsonNode.Parse(await result.Content.ReadAsStringAsync(Cancel))!;
    }

    internal static Task<HttpResponseMessage> StageAsync(Stopped stopped) =>
        JsonPostAsync(stopped.Session.Client, stopped.Token, $"{DictationApi.Path}/review/{stopped.Review}/stage", new { version = 1 });

    // A dictation proposing an order and an allergy to add, staged as the dictation page stages it after Stop; with the stub
    // as it was set, when not reset.
    internal static async Task<Dictated> DictateAsync(WebApps apps, Session? session = null, Guid? patient = null, bool reset = true)
    {
        if (reset)
        {
            apps.Dictation.Reset();
        }

        var stopped = await StopAsync(apps, session, patient);
        using (var staged = await StageAsync(stopped))
        {
            Assert.Equal(HttpStatusCode.OK, staged.StatusCode);
        }

        var rows = await QueryAsync(apps, "SELECT id::text || ' ' || kind FROM ehr.ai_chart_proposals WHERE review_id = @id ORDER BY ordinal", stopped.Review);
        Assert.Equal(2, rows.Count);
        Assert.EndsWith(" order", rows[0], StringComparison.Ordinal);
        Assert.EndsWith(" allergy_addition", rows[1], StringComparison.Ordinal);
        return new(stopped.Session, stopped.Token, stopped.Patient, stopped.Draft, stopped.Review,
            $"/patients/{stopped.Patient}/notes/drafts/{stopped.Draft}/review/{stopped.Review}", Guid.Parse(rows[0][..36]), Guid.Parse(rows[1][..36]));
    }

    internal static async Task<HttpResponseMessage> JsonPostAsync(HttpClient client, string token, string path, object body)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = JsonContent.Create(body) };
        request.Headers.Add("RequestVerificationToken", token);
        return await client.SendAsync(request, Cancel);
    }

    // The first column of each row, as text, for a query with one ID.
    internal static Task<List<string>> QueryAsync(WebApps apps, string sql, Guid id) => apps.QueryAsync(sql, ("id", id));

    // "state|order or -|allergy or -" of a proposal.
    internal static async Task<string> StateAsync(WebApps apps, Guid proposal) => (await QueryAsync(apps,
        "SELECT state || '|' || coalesce(order_id::text, '-') || '|' || coalesce(allergy_id::text, '-') FROM ehr.ai_chart_proposals WHERE id = @id",
        proposal)).Single();

    // How many orders, and allergies, the patient has: none from a dictation unless a clinician signed or saved them.
    internal static async Task<string> ChartAsync(WebApps apps, Guid patient) => (await QueryAsync(apps,
        "SELECT (SELECT count(*) FROM ehr.orders WHERE patient_id = @id) || ' orders, ' || (SELECT count(*) FROM ehr.allergy_intolerances WHERE patient_id = @id) || ' allergies'",
        patient)).Single();

    internal static string OrderFormPath(Guid patient, Guid proposal, string? type = null) =>
        $"/patients/{patient}/orders/new?{(type is null ? "" : $"type={type}&")}proposal={proposal}";

    internal static string AllergyFormPath(Guid patient, Guid proposal) => $"/patients/{patient}/allergies?proposal={proposal}";

    // The note signed as UC 13 and UC 14 sign a dictated one: each field accepted on the review, the review completed, the
    // draft saved and signed. The signed note's page.
    internal static async Task<string> SignNoteAsync(WebApps apps, Dictated dictated)
    {
        async Task<long> VersionAsync() => long.Parse((await QueryAsync(apps, "SELECT version FROM ehr.clinical_drafts WHERE id = @id", dictated.Draft)).Single(),
            System.Globalization.CultureInfo.InvariantCulture);
        async Task<JsonElement> PostAsync(string path, params (string Name, object Value)[] fields)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = Form([("__RequestVerificationToken", dictated.Token), .. fields]) };
            request.Headers.Add("HX-Request", "true");
            using var response = await dictated.Session.Client.SendAsync(request, Cancel);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            return JsonDocument.Parse(await response.Content.ReadAsStringAsync(Cancel)).RootElement.Clone();
        }

        foreach (var field in await QueryAsync(apps, "SELECT id FROM ehr.ai_field_proposals WHERE review_id = @id ORDER BY field", dictated.Review))
        {
            await PostAsync(dictated.ReviewPath + "?handler=Decide", ("ProposalId", field), ("Version", await VersionAsync()), ("DecisionKey", Guid.NewGuid()),
                ("Decision", "accepted"), ("EditedValue", ""));
        }

        await PostAsync(dictated.ReviewPath + "?handler=Complete", ("Version", await VersionAsync()));
        var editor = $"/patients/{dictated.Patient}/notes/drafts/{dictated.Draft}";
        (string, object)[] sections = [("Subjective", "Synthetic subjective"), ("Objective", "Synthetic objective"), ("Assessment", "Synthetic assessment"),
            ("Plan", "Synthetic plan")];
        await PostAsync(editor + "?handler=Review", [("Version", await VersionAsync()), .. sections]);
        var signed = await PostAsync(editor + "/confirm", [("Version", await VersionAsync()), ("Confirmed", true), .. sections]);
        return signed.GetProperty("url").GetString()!.Split('?')[0];
    }

    // Dismiss on the review, as the page posts it.
    internal static Task<HttpResponseMessage> DismissAsync(Dictated dictated, Guid proposal, HttpClient? client = null) =>
        PostFormAsync(client ?? dictated.Session.Client, dictated.ReviewPath + "?handler=DismissProposal", dictated.ReviewPath, ("ChartProposalId", proposal));

    // TC-UC10-09, through the web app: what the service proposes comes back with the result as it sent it, at its listed
    // instructions' places, with what was heard after it, in no field; staging keeps each as a waiting proposal of its review, in its order, and puts
    // nothing on the chart.
    [Fact]
    public async Task TC_UC10_09_a_dictated_order_and_allergy_come_back_proposed_in_no_field_and_are_staged_waiting()
    {
        var stopped = await StopAsync(apps);
        var json = await ResultAsync(stopped);
        var text = (string)json["transcript"]!["text"]!;
        var proposals = json["proposals"]!.AsArray();
        Assert.Equal(["order", "allergy_addition"], proposals.Select(p => (string)p!["kind"]!));
        Assert.Equal([DictationSidecarStub.OrderSaid, DictationSidecarStub.AllergySaid], proposals.Select(p => text[(int)p!["start"]!..(int)p!["end"]!]));
        Assert.Equal(proposals.Select(p => $"{p!["start"]}:{p["end"]}"), json["notCarriedOver"]!.AsArray().Select(i => $"{i!["start"]}:{i["end"]}"));
        Assert.Equal(["end", "heardAfter", "kind", "start", "warnings"], proposals[0]!.AsObject().Select(property => property.Key).Order(StringComparer.Ordinal));
        Assert.Equal(DictationSidecarStub.AllergySaid + ".", text[(int)proposals[0]!["heardAfter"]!["start"]!..(int)proposals[0]!["heardAfter"]!["end"]!]);
        Assert.DoesNotContain("lipid", json["fields"]!.ToJsonString(), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("penicillin", json["fields"]!.ToJsonString(), StringComparison.OrdinalIgnoreCase);

        using (var staged = await StageAsync(stopped))
        {
            Assert.Equal(HttpStatusCode.OK, staged.StatusCode);
        }

        Assert.Equal(["0 order pending", "1 allergy_addition pending"], await QueryAsync(apps,
            "SELECT ordinal || ' ' || kind || ' ' || state FROM ehr.ai_chart_proposals WHERE review_id = @id ORDER BY ordinal", stopped.Review));
        Assert.Equal(["order allergy_addition"], await QueryAsync(apps,
            "SELECT string_agg(p ->> 'kind', ' ') FROM ehr.ai_note_reviews r, jsonb_array_elements(r.result_json -> 'proposals') p WHERE r.id = @id", stopped.Review));
        Assert.Equal("0 orders, 0 allergies", await ChartAsync(apps, stopped.Patient));
    }

    // TC-UC10-09, from the service's own answer: the pinned result with proposals (src/Ehr.Dictation/contract, written
    // by the sidecar's tests from its real code) is staged with each proposal it sends, in its order, and none that it
    // holds back; the unclear passage's two instructions stay listed only.
    [Fact]
    public async Task TC_UC10_09_the_services_pinned_answer_with_proposals_is_staged_as_it_proposes()
    {
        var contract = JsonNode.Parse(await File.ReadAllTextAsync(
            RepositoryRoot.Combine("src/Ehr.Dictation/contract/result-200-done-not_carried_over.json"), Cancel))!;
        var body = contract["body"]!;
        Assert.Equal(6, body["notCarriedOver"]!.AsArray().Count);
        var stopped = await StopAsync(apps);
        sidecar.Answer = (HttpStatusCode.OK, body.ToJsonString());

        using (var staged = await StageAsync(stopped))
        {
            Assert.Equal(HttpStatusCode.OK, staged.StatusCode);
        }

        Assert.Equal(["0 order pending", "1 allergy_addition pending", "2 order pending", "3 allergy_addition pending"],
            await QueryAsync(apps, "SELECT ordinal || ' ' || kind || ' ' || state FROM ehr.ai_chart_proposals WHERE review_id = @id ORDER BY ordinal",
                stopped.Review));
    }

    // TC-UC13-10 with the service's own answer (step 15.1, at the owner's word): a plain order said with a refusal after
    // it that the service's rules don't read, one of the trick sentences the owner accepted by name, pinned as the
    // sidecar's real code answers it (src/Ehr.Dictation/contract). The review shows what was heard after each proposal,
    // exactly where the service found it, as said and beside the way into its form, with nothing that judges it.
    [Fact]
    public async Task TC_UC13_10_a_refusal_heard_after_a_proposal_is_shown_beside_it_as_the_service_found_it()
    {
        var body = JsonNode.Parse(await File.ReadAllTextAsync(
            RepositoryRoot.Combine("src/Ehr.Dictation/contract/result-200-done-proposal_heard_after.json"), Cancel))!["body"]!;
        var text = (string)body["transcript"]!["text"]!;
        var heard = body["proposals"]!.AsArray().Select(p => text[(int)p!["heardAfter"]!["start"]!..(int)p["heardAfter"]!["end"]!]).ToList();
        Assert.Equal(["She turned down the blood test.", "She turned down the blood test."], heard);
        var stopped = await StopAsync(apps);
        sidecar.Answer = (HttpStatusCode.OK, body.ToJsonString());
        using (var staged = await StageAsync(stopped))
        {
            Assert.Equal(HttpStatusCode.OK, staged.StatusCode);
        }

        var html = await stopped.Session.Client.GetStringAsync($"/patients/{stopped.Patient}/notes/drafts/{stopped.Draft}/review/{stopped.Review}", Cancel);
        var section = html[html.IndexOf("<section data-chart-proposals", StringComparison.Ordinal)..];
        var said = RefillRequestTests.Said(section[..section.IndexOf("</section>", StringComparison.Ordinal)]);
        Assert.Contains("As heard: Order a CBC. Heard after it: She turned down the blood test. Open in the order form", said);
        Assert.Equal(2, CountOf(said, "Heard after it: She turned down the blood test."));
        Assert.DoesNotContain("retract", said, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("refus", said, StringComparison.OrdinalIgnoreCase);
    }

    // TC-UC10-11: a result that proposes anything but its listed instructions, each of its kind, once, from a final
    // transcript, with a proposal's warnings and what was heard after it in the transcript after it, at most twenty, is
    // never staged: the service's answer is refused, and neither a proposal nor a field is kept. A kind the contract
    // doesn't know fails the read itself.
    [Theory]
    [InlineData("not-listed")]
    [InlineData("other-kind")]
    [InlineData("twice")]
    [InlineData("too-many")]
    [InlineData("field-warning")]
    [InlineData("warning-range")]
    [InlineData("heard-after-range")]
    [InlineData("heard-after-before")]
    [InlineData("not-final")]
    [InlineData("unknown-kind")]
    public async Task TC_UC10_11_a_proposal_that_isnt_one_of_the_listed_instructions_is_never_staged(string fault)
    {
        var stopped = await StopAsync(apps);
        var json = await ResultAsync(stopped);
        var proposals = json["proposals"]!.AsArray();
        var listed = json["notCarriedOver"]!.AsArray();
        switch (fault)
        {
            case "not-listed":
                proposals[0]!["start"] = (int)proposals[0]!["start"]! + 1;
                break;
            case "other-kind":
                proposals[0]!["kind"] = "allergy_addition";
                break;
            case "twice":
                proposals.Add(proposals[0]!.DeepClone());
                break;
            case "too-many":
                // Twenty-one listed instructions, each proposed once: one more than a dictation may propose.
                listed.Clear();
                proposals.Clear();
                for (var i = 0; i < 21; i++)
                {
                    listed.Add(new JsonObject { ["reason"] = "order", ["start"] = i, ["end"] = i + 1 });
                    proposals.Add(new JsonObject { ["kind"] = "order", ["start"] = i, ["end"] = i + 1, ["warnings"] = new JsonArray() });
                }

                break;
            case "field-warning":
                proposals[0]!["warnings"]!.AsArray().Add(new JsonObject { ["code"] = "one_field_holds_dictation", ["start"] = 0, ["end"] = 5 });
                break;
            case "warning-range":
                proposals[1]!["warnings"]![0]!["end"] = 1_000_000;
                break;
            case "heard-after-range":
                proposals[0]!["heardAfter"]!["end"] = 1_000_000;
                break;
            case "heard-after-before":
                // What was heard after it, starting before the instruction ends.
                proposals[0]!["heardAfter"]!["start"] = (int)proposals[0]!["end"]! - 1;
                break;
            case "not-final":
                // Speech recognition failed on the rest of the recording: the transcript is as it was live, not final.
                json["sorting"] = "unavailable";
                json["reason"] = "transcription_failed";
                json["fields"] = null;
                json["transcript"]!["final"] = false;
                break;
            default:
                proposals[0]!["kind"] = "discontinuation";
                break;
        }

        sidecar.Answer = (HttpStatusCode.OK, json.ToJsonString());
        using var stage = await StageAsync(stopped);
        Assert.Equal(HttpStatusCode.BadGateway, stage.StatusCode);
        Assert.Equal(["0 0 recording"], await QueryAsync(apps,
            """
            SELECT (SELECT count(*) FROM ehr.ai_chart_proposals WHERE review_id = @id) || ' ' || (SELECT count(*) FROM ehr.ai_field_proposals WHERE review_id = @id)
                || ' ' || state FROM ehr.ai_note_reviews WHERE id = @id
            """, stopped.Review));
    }

    // TC-UC13-10: the review lists each proposal under "Proposed orders and allergies": its kind, awaiting review, the
    // clinician's words labelled as heard and marked in the sentence they said them in and the one before, what was heard
    // after it (step 15.1; the allergy's sentence after the order, nothing after the allergy), its warnings with the words they point
    // at, and the way into its form; the order's into the order form, the allergy's into the allergy form. Only the
    // dictating clinician sees them: not a colleague with the patient on their panel (TC-UC1-24 opens the order form
    // with one as the colleague).
    [Fact]
    public async Task TC_UC13_10_the_review_lists_each_proposal_in_its_sentence_with_the_way_into_its_form()
    {
        var dictated = await DictateAsync(apps);
        var html = await dictated.Session.Client.GetStringAsync(dictated.ReviewPath, Cancel);
        var said = RefillRequestTests.Said(html);
        Assert.Contains("Proposed orders and allergies", said);
        Assert.Contains("Proposed from what you said. Dictation doesn't place or save any of them.", said);
        Assert.Contains("Each shows what you said after it, in case you corrected it.", said);
        Assert.Contains($"Order Awaiting review As heard: Chest is clear. {DictationSidecarStub.OrderSaid}. Heard after it: {DictationSidecarStub.AllergySaid}. Open in the order form", said);
        Assert.Equal(1, CountOf(html, "data-chart-proposal-heard-after"));
        Assert.Contains($"Allergy to add Awaiting review As heard: {DictationSidecarStub.OrderSaid}. {DictationSidecarStub.AllergySaid}.", said);
        Assert.Contains($"<mark class=\"sc-transcript__source\">{DictationSidecarStub.OrderSaid}</mark>", html);
        Assert.Contains("Check. You may have corrected this just after it: “penicillin”", said);
        Assert.Contains($"href=\"{OrderFormPath(dictated.Patient, dictated.Order)}\"", html.Replace("&amp;", "&", StringComparison.Ordinal));
        Assert.Contains($"href=\"/patients/{dictated.Patient}/allergies?proposal={dictated.Allergy}\"", html);
        Assert.Contains("Open in the order form", said);
        Assert.Contains("Open in the allergy form", said);
        Assert.Equal(2, CountOf(html, "Dismiss proposal"));

        // Nor the colleague with the patient on their panel: not its review.
        var colleague = await apps.CreateStaffAsync("Dr. Colleague", Ehr.Data.Accounts.StaffRoles.Clinician);
        await apps.AssignAsync(colleague, dictated.Patient);
        var theirs = await SignInAsync(apps.OnPrem, colleague);
        using var review = await theirs.Client.GetAsync(dictated.ReviewPath, Cancel);
        Assert.Equal(HttpStatusCode.NotFound, review.StatusCode);
    }

    // A sign-in drops a link's query (step 13b), `proposal` with the rest: through a link to the order form opened from a
    // proposal, the clinician signs in to an ordinary order form, and the note being written leads back to the review,
    // where the proposal waits with the way into its form.
    [Fact]
    public async Task A_sign_in_drops_the_proposal_and_the_note_leads_back_to_its_review()
    {
        var dictated = await DictateAsync(apps);
        var clinician = await apps.ClinicianAsync();
        var link = OrderFormPath(dictated.Patient, dictated.Order);
        var client = Client(apps.OnPrem, new CookieContainer());
        string signIn;
        using (var challenged = await client.GetAsync(link, Cancel))
        {
            Assert.Equal("/sign-in", PathOf(challenged));
            signIn = challenged.RequestMessage!.RequestUri!.PathAndQuery;
        }

        using var codePage = await PostFormAsync(client, signIn, signIn, ("Input.Email", clinician.Email), ("Input.Password", clinician.Password),
            ("ReturnUrl", link));
        Assert.Equal("/sign-in/code", PathOf(codePage));
        using var landed = await PostFormAsync(client, codePage.RequestMessage!.RequestUri!.PathAndQuery, codePage,
            ("Code", Ehr.Testing.Totp.Code(clinician.AuthenticatorKey)), ("ReturnUrl", link));
        Assert.Equal($"/patients/{dictated.Patient}/orders/new", PathOf(landed));
        Assert.Equal("", landed.RequestMessage!.RequestUri!.Query);
        var form = await ReadAsync(landed);
        Assert.DoesNotContain("Proposed from dictation", form);
        Assert.DoesNotContain(DictationSidecarStub.OrderSaid, form);

        var editor = await client.GetStringAsync($"/patients/{dictated.Patient}/notes/drafts/{dictated.Draft}", Cancel);
        Assert.NotEmpty(LinkTo(editor, dictated.ReviewPath));
        Assert.Contains("Awaiting review. 2 orders or allergies proposed from dictation. Review them.", RefillRequestTests.Said(editor));
        var review = await client.GetStringAsync(dictated.ReviewPath, Cancel);
        Assert.NotEmpty(LinkTo(review.Replace("&amp;", "&", StringComparison.Ordinal), link));
        Assert.Equal("pending|-|-", await StateAsync(apps, dictated.Order));
    }

    // The link to a path, as the page writes it, its attributes in whatever order.
    internal static string LinkTo(string html, string path) =>
        System.Text.RegularExpressions.Regex.Match(html, $"<a [^>]*href=\"{System.Text.RegularExpressions.Regex.Escape(path)}\"[^>]*>").Value;

    internal static int CountOf(string text, string part) => (text.Length - text.Replace(part, "", StringComparison.Ordinal).Length) / part.Length;

    // TC-UC13-11: Dismiss decides a proposal once, with nothing written to the chart: the review shows it dismissed, with
    // when, and the cursor goes to the next one waiting; dismissing it again changes nothing; a form opened before it was
    // dismissed saves nothing; one decided otherwise can't be dismissed.
    [Fact]
    public async Task TC_UC13_11_dismissing_decides_a_proposal_once_and_puts_nothing_on_the_chart()
    {
        var dictated = await DictateAsync(apps);
        using (var dismissed = await DismissAsync(dictated, dictated.Order))
        {
            Assert.Equal(HttpStatusCode.OK, dismissed.StatusCode);
            Assert.Equal(dictated.ReviewPath, PathOf(dismissed));
            Assert.Contains($"decided={dictated.Order}", dismissed.RequestMessage!.RequestUri!.Query, StringComparison.Ordinal);
            var html = await ReadAsync(dismissed);
            Assert.Contains("autofocus", LinkTo(html, AllergyFormPath(dictated.Patient, dictated.Allergy)));
            Assert.Contains("Order Dismissed", RefillRequestTests.Said(html));
            Assert.DoesNotContain("data-chart-proposal-made", html);
            Assert.Equal(1, CountOf(html, "Dismiss proposal"));
        }

        Assert.StartsWith("dismissed|-|-", await StateAsync(apps, dictated.Order), StringComparison.Ordinal);
        using (var again = await DismissAsync(dictated, dictated.Order))
        {
            Assert.Equal(HttpStatusCode.OK, again.StatusCode);
        }

        // A proposal of another review of the clinician's, named on this one's page: not this review's to dismiss.
        var other = await DictateAsync(apps, dictated.Session, dictated.Patient);
        using (var elsewhere = await DismissAsync(dictated, other.Order))
        {
            Assert.Equal(HttpStatusCode.NotFound, elsewhere.StatusCode);
        }

        Assert.Equal("pending|-|-", await StateAsync(apps, other.Order));

        using (var last = await DismissAsync(dictated, dictated.Allergy))
        {
            // Nothing waits: the cursor goes to the section's heading.
            Assert.Contains("id=\"chart-proposals\" tabindex=\"-1\" autofocus", await ReadAsync(last));
        }

        Assert.Equal("0 orders, 0 allergies", await ChartAsync(apps, dictated.Patient));

        // A form opened before its proposal was dismissed saves nothing, its order or allergy included.
        var opened = await DictateAsync(apps, dictated.Session, dictated.Patient);
        var orderForm = OrderFormPath(opened.Patient, opened.Order, "lab");
        var allergyForm = AllergyFormPath(opened.Patient, opened.Allergy);
        var (orderToken, allergyToken) = (Token(await opened.Session.Client.GetStringAsync(orderForm, Cancel)),
            Token(await opened.Session.Client.GetStringAsync(allergyForm, Cancel)));
        foreach (var proposal in new[] { opened.Order, opened.Allergy })
        {
            using var dismissed = await DismissAsync(opened, proposal);
            Assert.Equal(HttpStatusCode.OK, dismissed.StatusCode);
        }

        using (var orderRefused = await opened.Session.Client.PostAsync(orderForm, Form([("__RequestVerificationToken", orderToken), ("Item", "Lipid panel"),
            ("Details[indication]", "Synthetic fatigue"), ("SafetySeen", "true")]), Cancel))
        {
            Assert.Equal(HttpStatusCode.Conflict, orderRefused.StatusCode);
        }

        using (var allergyRefused = await opened.Session.Client.PostAsync(allergyForm, Form([("__RequestVerificationToken", allergyToken), ("Name", "Penicillin G"),
            ("Status", "active")]), Cancel))
        {
            Assert.Equal(HttpStatusCode.Conflict, allergyRefused.StatusCode);
        }

        Assert.Equal("0 orders, 0 allergies", await ChartAsync(apps, dictated.Patient));

        // An order's proposal ordered in its form can't be dismissed after.
        var second = await DictateAsync(apps, dictated.Session, dictated.Patient);
        using (var signed = await UC1OrderAsync(second, "Lipid panel"))
        {
            Assert.Equal(second.ReviewPath, PathOf(signed));
        }

        using var refused = await DismissAsync(second, second.Order);
        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        Assert.Contains("Not saved. This proposal was already decided", RefillRequestTests.Said(await refused.Content.ReadAsStringAsync(Cancel)));
        Assert.StartsWith("ordered|", await StateAsync(apps, second.Order), StringComparison.Ordinal);
    }

    // A decided proposal shows no warning, there being nothing left to check it for, and leads its clinician to what it
    // was decided into, on the chart: the order signed for it, the allergy added for it.
    [Fact]
    public async Task A_decided_proposal_drops_its_warnings_and_leads_to_its_order_or_allergy_on_the_chart()
    {
        var dictated = await DictateAsync(apps);
        var allergyForm = AllergyFormPath(dictated.Patient, dictated.Allergy);
        using (var recorded = await PostFormAsync(dictated.Session.Client, allergyForm, allergyForm, ("Name", "Penicillin G"), ("Status", "active")))
        {
            var html = await ReadAsync(recorded);
            var said = RefillRequestTests.Said(html);
            var allergy = (await StateAsync(apps, dictated.Allergy)).Split('|')[2];
            Assert.Contains("Allergy to add Allergy added", said);
            Assert.DoesNotContain("Check. You may have corrected", said);
            Assert.Contains("data-chart-proposal-made", LinkTo(html, $"/patients/{dictated.Patient}#allergy-{allergy}"));
            Assert.Contains("See the allergy on the chart", said);
        }

        using (var ordered = await UC1OrderAsync(dictated, "Lipid panel"))
        {
            var html = await ReadAsync(ordered);
            var order = (await StateAsync(apps, dictated.Order)).Split('|')[1];
            Assert.Contains("data-chart-proposal-made", LinkTo(html, $"/patients/{dictated.Patient}#order-{order}"));
            Assert.Contains("See the order on the chart", RefillRequestTests.Said(html));
            var chart = await dictated.Session.Client.GetStringAsync($"/patients/{dictated.Patient}", Cancel);
            Assert.Contains($"id=\"order-{order}\"", chart);
            Assert.Contains($"id=\"allergy-{(await StateAsync(apps, dictated.Allergy)).Split('|')[2]}\"", chart);
        }
    }

    // "Sign order" for a lab test on the order form opened from the dictation's order proposal, as the form posts it
    // without JavaScript, with any reasons it asks for: the page it ends on.
    internal static async Task<HttpResponseMessage> UC1OrderAsync(Dictated dictated, string item, string type = "lab",
        (string Name, string Value)[]? details = null, string? duplicateReason = null, string? safetyReason = null, HttpClient? client = null)
    {
        client ??= dictated.Session.Client;
        var path = OrderFormPath(dictated.Patient, dictated.Order, type);
        var token = Token(await client.GetStringAsync(path, Cancel));
        (string, object)[] reasons =
        [
            .. duplicateReason is null ? [] : new[] { ("DuplicateReason", (object)duplicateReason) },
            .. safetyReason is null ? [] : new[] { ("SafetyReason", (object)safetyReason) },
        ];
        return await client.PostAsync(path, Form([("__RequestVerificationToken", token), ("Item", item), ("SafetySeen", "true"),
            .. (details ?? OrderTests.Cbc).Select(d => ($"Details[{d.Name}]", (object)d.Value)), .. reasons]), Cancel);
    }

    // TC-UC13-13: off-site, the review is read from the cloud copy with its proposals and their reads accepted for the
    // clinic's audit log; with the clinic's server reachable, Dismiss is forwarded to it, and with it cut off the
    // proposals are still shown, their ways into a form and Dismiss hidden, with the reason, and a Dismiss sent anyway
    // changes nothing.
    [Fact]
    public async Task TC_UC13_13_off_site_the_review_shows_its_proposals_and_their_actions_only_while_the_clinics_server_is_reachable()
    {
        var dictated = await DictateAsync(apps);
        var cloud = Client(apps.Cloud, dictated.Session.Jar);
        var reachable = await cloud.GetStringAsync(dictated.ReviewPath, Cancel);
        Assert.Contains("Open in the order form", reachable);
        using (var dismissed = await DismissAsync(dictated, dictated.Order, cloud))
        {
            Assert.Equal(HttpStatusCode.OK, dismissed.StatusCode);
        }

        Assert.StartsWith("dismissed|", await StateAsync(apps, dictated.Order), StringComparison.Ordinal);

        var cutOff = Client(apps.CloudCutOff, dictated.Session.Jar);
        using var page = await cutOff.GetAsync(dictated.ReviewPath, Cancel);
        Assert.Equal(HttpStatusCode.OK, page.StatusCode);
        var said = RefillRequestTests.Said(await ReadAsync(page));
        Assert.Contains("Opening or dismissing a proposal needs the clinic's server. The review remains readable.", said);
        Assert.Contains($"Allergy to add Awaiting review As heard: {DictationSidecarStub.OrderSaid}. {DictationSidecarStub.AllergySaid}.", said);
        Assert.DoesNotContain("Open in the allergy form", said);
        Assert.DoesNotContain("Dismiss proposal", said);
        using (var refused = await cutOff.PostAsync(dictated.ReviewPath + "?handler=DismissProposal",
            Form([("__RequestVerificationToken", Token(await ReadAsync(page))), ("ChartProposalId", dictated.Allergy)]), Cancel))
        {
            Assert.NotEqual(HttpStatusCode.OK, refused.StatusCode);
        }

        Assert.Equal("pending|-|-", await StateAsync(apps, dictated.Allergy));
    }

    // As intended (D8, D17): a result whose fields couldn't be sorted still proposes what the rules found in its final
    // transcript, since proposals don't come from the model. It is staged with them, and its review offers each, to open
    // or dismiss, beside the transcript kept as plain text.
    [Fact]
    public async Task A_result_with_proposals_and_no_fields_is_staged_and_its_review_offers_them()
    {
        apps.Dictation.Reset();
        apps.Dictation.Sorting = StubSorting.Unavailable;
        var dictated = await DictateAsync(apps, reset: false);

        var html = await dictated.Session.Client.GetStringAsync(dictated.ReviewPath, Cancel);

        Assert.Equal(["0 0 completed"], await QueryAsync(apps,
            """
            SELECT (SELECT count(*) FROM ehr.ai_field_proposals WHERE review_id = @id) || ' '
                || (SELECT count(*) FROM ehr.ai_chart_proposals WHERE review_id = @id AND state <> 'pending') || ' ' || state
            FROM ehr.ai_note_reviews WHERE id = @id
            """, dictated.Review));
        Assert.NotEmpty(LinkTo(html.Replace("&amp;", "&", StringComparison.Ordinal), OrderFormPath(dictated.Patient, dictated.Order)));
        Assert.NotEmpty(LinkTo(html, AllergyFormPath(dictated.Patient, dictated.Allergy)));
        Assert.Equal(2, CountOf(html, "Dismiss proposal"));
    }

    // Off-site with the clinic's server cut off, the note being written is shown as last saved, without the proposals still
    // waiting, which can't be decided then: so they aren't read, and no read of them is queued for the clinic's audit log.
    // The review and the signed note, which show them, record theirs (TC-UC13-13, AuditPartTests).
    [Fact]
    public async Task Cut_off_the_note_editor_neither_shows_nor_reads_the_proposals_waiting()
    {
        var dictated = await DictateAsync(apps);
        var cutOff = Client(apps.CloudCutOff, dictated.Session.Jar);

        var editor = RefillRequestTests.Said(await cutOff.GetStringAsync($"/patients/{dictated.Patient}/notes/drafts/{dictated.Draft}", Cancel));

        Assert.Contains("This draft can't be changed or signed until the clinic's server is back.", editor);
        Assert.DoesNotContain("proposed from dictation", editor);
        var queued = apps.CloudCutOff.Services.GetRequiredService<CloudAuditQueue>().ReadPending(int.MaxValue).Events
            .Where(e => e.PatientId == dictated.Patient).Select(e => e.TableName).ToList();
        Assert.Contains("clinical_drafts", queued);
        Assert.DoesNotContain("ai_chart_proposals", queued);
    }

    // Two tabs signing, or saving, one proposal at once, each having read it waiting: the database holds the second behind
    // the first on the patient's row, finds the proposal decided when its turn comes, and its form goes back to its
    // savepoint, so it leaves no order or allergy behind and says so. The test holds the patient's row itself until both
    // are waiting there, so the two run in that order every time.
    [Theory]
    [InlineData("order")]
    [InlineData("allergy")]
    public async Task Two_tabs_deciding_one_proposal_at_once_leave_one_decision_and_nothing_else(string kind)
    {
        var dictated = await DictateAsync(apps);
        var (path, proposal, table) = kind == "order"
            ? (OrderFormPath(dictated.Patient, dictated.Order, "lab"), dictated.Order, "orders")
            : (AllergyFormPath(dictated.Patient, dictated.Allergy), dictated.Allergy, "allergy_intolerances");
        (string Name, object Value)[][] posts = kind == "order"
            ? [[("Item", "Lipid panel"), ("Details[indication]", "Synthetic fatigue")], [("Item", "Hemoglobin A1c"), ("Details[indication]", "Synthetic fatigue")]]
            : [[("Name", "Penicillin G"), ("Status", "active")], [("Name", "Amoxicillin"), ("Status", "active")]];
        var tokens = new[] { Token(await dictated.Session.Client.GetStringAsync(path, Cancel)), Token(await dictated.Session.Client.GetStringAsync(path, Cancel)) };

        await using var holding = await apps.OpenDatabaseAsync();
        await using var held = await holding.BeginTransactionAsync(Cancel);
        await using (var hold = new NpgsqlCommand("SELECT 1 FROM ehr.patients WHERE id = @id FOR NO KEY UPDATE", holding, held))
        {
            hold.Parameters.AddWithValue("id", dictated.Patient);
            await hold.ExecuteNonQueryAsync(Cancel);
        }

        var sent = tokens.Select((token, tab) => dictated.Session.Client.PostAsync(path,
            Form([("__RequestVerificationToken", token), ("SafetySeen", "true"), .. posts[tab]]), Cancel)).ToArray();
        // Watched from a connection of its own: a transaction sees the server's activity as it was when it first looked.
        await using var watching = await apps.OpenDatabaseAsync();
        for (var tries = 0; ; tries++)
        {
            await using var count = new NpgsqlCommand(
                $"SELECT count(*) FROM pg_stat_activity WHERE wait_event_type = 'Lock' AND query LIKE 'INSERT INTO ehr.{table}%'", watching);
            if ((long)(await count.ExecuteScalarAsync(Cancel))! >= 2)
            {
                break;
            }

            Assert.True(tries < 200, "The two tabs never both waited for the patient's row.");
            await Task.Delay(50, Cancel);
        }

        await held.CommitAsync(Cancel);
        var responses = await Task.WhenAll(sent);
        Assert.Equal([HttpStatusCode.OK, HttpStatusCode.Conflict], responses.Select(r => r.StatusCode).Order());
        var refused = responses.Single(r => r.StatusCode == HttpStatusCode.Conflict);
        Assert.Contains("Not saved. This proposal was decided first, in another tab or window.", RefillRequestTests.Said(await refused.Content.ReadAsStringAsync(Cancel)));
        var written = await QueryAsync(apps, $"SELECT id FROM ehr.{table} WHERE patient_id = @id", dictated.Patient);
        Assert.Single(written);
        Assert.Equal(kind == "order" ? $"ordered|{written[0]}|-" : $"recorded|-|{written[0]}", await StateAsync(apps, proposal));
        foreach (var response in responses)
        {
            response.Dispose();
        }
    }

    // Two tabs dismissing one proposal at once, each having read it waiting: the second finds it dismissed when its turn
    // comes and says so, and it is dismissed once. The test holds the proposal's row until both wait on it.
    [Fact]
    public async Task Two_tabs_dismissing_one_proposal_at_once_dismiss_it_once()
    {
        var dictated = await DictateAsync(apps);
        var tokens = new[] { Token(await dictated.Session.Client.GetStringAsync(dictated.ReviewPath, Cancel)),
            Token(await dictated.Session.Client.GetStringAsync(dictated.ReviewPath, Cancel)) };
        await using var holding = await apps.OpenDatabaseAsync();
        await using var held = await holding.BeginTransactionAsync(Cancel);
        await using (var hold = new NpgsqlCommand("SELECT 1 FROM ehr.ai_chart_proposals WHERE id = @id FOR UPDATE", holding, held))
        {
            hold.Parameters.AddWithValue("id", dictated.Order);
            await hold.ExecuteNonQueryAsync(Cancel);
        }

        var sent = tokens.Select(token => dictated.Session.Client.PostAsync(dictated.ReviewPath + "?handler=DismissProposal",
            Form([("__RequestVerificationToken", token), ("ChartProposalId", dictated.Order)]), Cancel)).ToArray();
        await using var watching = await apps.OpenDatabaseAsync();
        for (var tries = 0; ; tries++)
        {
            await using var count = new NpgsqlCommand(
                "SELECT count(*) FROM pg_stat_activity WHERE wait_event_type = 'Lock' AND query LIKE 'UPDATE ehr.ai_chart_proposals%'", watching);
            if ((long)(await count.ExecuteScalarAsync(Cancel))! >= 2)
            {
                break;
            }

            Assert.True(tries < 200, "The two tabs never both waited for the proposal's row.");
            await Task.Delay(50, Cancel);
        }

        await held.CommitAsync(Cancel);
        var responses = await Task.WhenAll(sent);
        Assert.Equal([HttpStatusCode.OK, HttpStatusCode.Conflict], responses.Select(r => r.StatusCode).Order());
        var refused = responses.Single(r => r.StatusCode == HttpStatusCode.Conflict);
        Assert.Contains("Not saved. This proposal was already decided", RefillRequestTests.Said(await refused.Content.ReadAsStringAsync(Cancel)));
        Assert.StartsWith("dismissed|-|-", await StateAsync(apps, dictated.Order), StringComparison.Ordinal);
        Assert.Equal(["1"], await QueryAsync(apps,
            "SELECT count(*) FROM ehr.audit_events WHERE table_name = 'ai_chart_proposals' AND row_id = @id AND action = 'update'", dictated.Order));
        foreach (var response in responses)
        {
            response.Dispose();
        }
    }
}
