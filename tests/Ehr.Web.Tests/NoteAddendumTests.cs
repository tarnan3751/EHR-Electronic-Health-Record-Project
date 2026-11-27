using System.Text.Json;
using Xunit;
using static Ehr.Web.Tests.Browser;

namespace Ehr.Web.Tests;

// Addenda to signed notes and sensitive notes (the expansion plan's step 10): helpers for the test cases TC-UC14-07 to
// 12. What the database refuses whatever the app does is in Ehr.Security.Tests (NoteAddendumSecurityTests).
static class NoteAddendumTests
{
    public const string Subjective = "Synthetic subjective: cough for two days.";

    public const string Objective = "Synthetic objective: chest clear.";

    public static string NotePath(Guid patient, Guid note) => $"/patients/{patient}/notes/{note}";

    public static string AddendumPath(Guid patient, Guid note) => NotePath(patient, note) + "/addendum";

    // A note written and signed the way the pages do it: a draft from the chart, reviewed, then confirmed and signed,
    // marked sensitive or not as it is signed. Returns the signed note's ID.
    public static async Task<Guid> SignedNoteAsync(HttpClient client, Guid patient, bool sensitive = false, string subjective = Subjective)
    {
        using var created = await PostFormAsync(client, $"/patients/{patient}?handler=NewNote", $"/patients/{patient}");
        var (path, token) = (PathOf(created), Token(await ReadAsync(created)));
        async Task<JsonElement> PostAsync(string target, long version, params (string Name, object Value)[] extra)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, target);
            request.Headers.Add("HX-Request", "true");
            request.Content = Form([("__RequestVerificationToken", token), ("Version", version), ("Subjective", subjective), ("Objective", Objective),
                ("Assessment", "Synthetic assessment: viral illness."), ("Plan", "Synthetic plan: rest and follow-up."), .. extra]);
            using var response = await client.SendAsync(request, Cancel);
            Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);
            return JsonDocument.Parse(await response.Content.ReadAsStringAsync(Cancel)).RootElement.Clone();
        }

        await PostAsync(path + "?handler=Review", 1);
        var signed = await PostAsync(path + "/confirm", 2, ("Confirmed", true), ("Sensitive", sensitive));
        var url = signed.GetProperty("url").GetString()!;
        return Guid.Parse(url.Split('?')[0].Split('/')[^1]);
    }

    // "Sign addendum", as the addendum page posts it.
    public static Task<HttpResponseMessage> AddAsync(HttpClient client, Guid patient, Guid note, string text, bool sensitive = false) =>
        PostFormAsync(client, AddendumPath(patient, note), AddendumPath(patient, note), ("Text", text), ("Sensitive", sensitive));

    public static string SensitivityPath(Guid patient, Guid note, Guid? addendum = null) =>
        NotePath(patient, note) + "/sensitivity" + (addendum is null ? "" : $"?addendum={addendum}");

    // "Mark sensitive" (mark: true) or "Unmark as sensitive" (false), as the page posts it: the form says which.
    public static Task<HttpResponseMessage> ChangeSensitivityAsync(HttpClient client, Guid patient, Guid note, bool mark, string? reason, Guid? addendum = null) =>
        PostFormAsync(client, SensitivityPath(patient, note, addendum), SensitivityPath(patient, note, addendum),
            reason is null ? [("Mark", mark)] : [("Mark", mark), ("Reason", reason)]);

    // "sensitive marked-by reason" for the patient's notes, then their addenda, each in the order signed.
    public static Task<List<string>> SensitivityAsync(WebApps apps, Guid patient) => ProxyTests.QueryAsync(apps,
        """
        SELECT kind || ' ' || sensitive || ' ' || coalesce(sensitive_marked_by::text, 'none') || ' ' || coalesce(sensitive_reason, 'none') FROM (
            SELECT 'note' AS kind, sensitive, sensitive_marked_by, sensitive_reason, signed_at, id FROM ehr.clinical_notes WHERE patient_id = @patient
            UNION ALL
            SELECT 'addendum', sensitive, sensitive_marked_by, sensitive_reason, signed_at, id FROM ehr.note_addenda WHERE patient_id = @patient) rows
        ORDER BY kind DESC, signed_at, id
        """, patient);

    // "author sensitive text" for each addendum of the patient's notes, in the order signed.
    public static Task<List<string>> AddendaAsync(WebApps apps, Guid patient) => ProxyTests.QueryAsync(apps,
        "SELECT author_id || ' ' || sensitive || ' ' || text FROM ehr.note_addenda WHERE patient_id = @patient ORDER BY signed_at, id", patient);
}
