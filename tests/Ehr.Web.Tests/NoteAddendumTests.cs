using System.Text.Json;
using Xunit;
using static Ehr.Web.Tests.Browser;

namespace Ehr.Web.Tests;

// Addenda to signed notes and sensitive notes (the expansion plan's step 10): helpers for the test cases TC-UC14-07 to
// 12. What the database refuses whatever the app does is in Ehr.Security.Tests (NoteAddendumSecurityTests).
static class NoteAddendumTests
{
    static CancellationToken Cancel => TestContext.Current.CancellationToken;

    public const string History = "Synthetic history: cough for two days.";

    public static string NotePath(Guid patient, Guid note) => $"/patients/{patient}/notes/{note}";

    public static string AddendumPath(Guid patient, Guid note) => NotePath(patient, note) + "/addendum";

    // A note written and signed the way the pages do it: a draft from the chart, reviewed, then confirmed and signed,
    // marked sensitive or not as it is signed. Returns the signed note's ID.
    public static async Task<Guid> SignedNoteAsync(HttpClient client, Guid patient, bool sensitive = false)
    {
        using var created = await PostFormAsync(client, $"/patients/{patient}?handler=NewNote", $"/patients/{patient}");
        var (path, token) = (PathOf(created), Token(await ReadAsync(created)));
        async Task<JsonElement> PostAsync(string target, long version, params (string Name, object Value)[] extra)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, target);
            request.Headers.Add("HX-Request", "true");
            request.Content = Form([("__RequestVerificationToken", token), ("Version", version), ("History", History),
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

    // "author sensitive text" for each addendum of the patient's notes, in the order signed.
    public static Task<List<string>> AddendaAsync(WebApps apps, Guid patient) => ProxyTests.QueryAsync(apps,
        "SELECT author_id || ' ' || sensitive || ' ' || text FROM ehr.note_addenda WHERE patient_id = @patient ORDER BY signed_at, id", patient);
}
