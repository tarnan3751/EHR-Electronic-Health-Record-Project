using System.Globalization;
using System.Net;
using System.Text.Json;
using Ehr.Domain.Time;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using static Ehr.Web.Tests.Browser;
using static Ehr.Web.Tests.OrderTests;
using static Ehr.Web.Tests.VitalTests;

namespace Ehr.Web.Tests.TestCases.UC14;

// Test Case ID: TC-UC14-30
// Purpose / scenario: When the clinician asks, the editor offers the patient's vital signs as text for Objective: those
//   of the latest day one was taken that stands, the newest of each sign that day, a blood pressure as one reading, each
//   with its time on the clinic's clock, and none entered in error. Nothing is added unless the clinician adds it, and
//   the note keeps the text.
// Type: Functional (alternative flow)
// Requirements: FR-31, proposed FR-48, proposed FR-50, proposed FR-60
// Use case: UC 14, Review and sign a clinical note
// Preconditions: The clinician is signed in on the on-site app (clinic time UTC, as the tests run it) with three
//   patients on their panel, each with a new draft. One has vital signs taken three days ago and today, some of today's
//   marked entered in error; one only three days ago; one none. A fourth patient isn't on the panel.
// Inputs and system conditions: GET /patients/{id}/notes/drafts/{draft}?handler=Vitals; POST the draft (Save, Review)
//   and its /confirm.
// Test data: three days ago, weight 82.4; today, a minute apart: 126/80, heart rate 72 and temperature 37.9, then 150/95
//   and heart rate 95, marked entered in error ("Synthetic: cuff too small."). For the second patient, three days
//   ago, weight 70 and height 170. Times by the database's clock.
// Steps:
//   1. Ask for the first patient's vital signs, and look at the saved draft.
//   2. Ask for the second's, the third's, and the fourth's.
//   3. Put the first patient's text in Objective, and sign.
// Expected result: Step 1: "Vital signs, <today's time, labeled>: blood pressure 126/80 mmHg, heart rate 72 per minute,
//   temperature 37.9 °C." with that day and whether it is today; not the values in error nor the weight of three days
//   ago; one read of the patient recorded, with the part "vitals"; the draft unchanged. Step 2: the second patient's
//   weight, height and body mass index of three days ago, with that day, not today; for the third no text and no day;
//   the fourth isn't found. Step 3: the signed note's Objective is that text.
// Creator: Tahseen Arnan
public class TC_UC14_30_VitalSignsForObjective(WebApps apps) : IClassFixture<WebApps>
{
    async Task<(string Path, string Token)> DraftAsync(HttpClient client, Guid patient)
    {
        using var created = await PostFormAsync(client, $"/patients/{patient}?handler=NewNote", $"/patients/{patient}");
        return (PathOf(created), Token(await ReadAsync(created)));
    }

    static async Task<JsonElement> OfferAsync(HttpClient client, string path)
    {
        using var response = await client.GetAsync(path + "?handler=Vitals", Cancel);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync(Cancel)).RootElement.Clone();
    }

    [Fact]
    public async Task The_latest_days_standing_vital_signs_are_offered_as_text_and_added_only_by_the_clinician()
    {
        var (clinician, first, session) = await OnPanelAsync(apps);
        var clinic = apps.OnPrem.Services.GetRequiredService<ClinicTime>();
        var now = await apps.Database.NowAsync();
        string At(int minutesAgo) => now.AddMinutes(-minutesAgo).ToString("yyyy-MM-ddTHH:mm", CultureInfo.InvariantCulture);
        foreach (var (taken, values) in new (string, (string, string)[])[]
        {
            (At(3 * 24 * 60), [(Weight, "82.4")]),
            (At(2), [(Systolic, "126"), (Diastolic, "80"), (HeartRate, "72"), (Temperature, "37.9")]),
            (At(1), [(Systolic, "150"), (Diastolic, "95"), (HeartRate, "95")]),
        })
        {
            using var recorded = await RecordAtAsync(session.Client, first, taken, values);
            Assert.Contains("Saved.", await ReadAsync(recorded));
        }

        using (var marked = await MarkAsync(apps, session.Client, first, At(1) + ":00", "Synthetic: cuff too small.", Systolic, HeartRate))
        {
            Assert.Contains("Entered in error.", Words(await ReadAsync(marked)));
        }

        var (path, token) = await DraftAsync(session.Client, first);
        var draft = await ProxyTests.QueryAsync(apps, $"SELECT to_jsonb(d)::text FROM ehr.clinical_drafts d WHERE d.patient_id = @patient", first);
        var since = await apps.Database.NowAsync();
        var offer = await OfferAsync(session.Client, path);
        // The newest that stand: the set before the one marked in error.
        var latest = new DateTimeOffset(DateTime.SpecifyKind(DateTime.ParseExact(At(2), "yyyy-MM-ddTHH:mm", CultureInfo.InvariantCulture), DateTimeKind.Utc));
        var text = $"Vital signs, {clinic.Format(latest)}: blood pressure 126/80 mmHg, heart rate 72 per minute, temperature 37.9 °C.";
        Assert.Equal(text, offer.GetProperty("text").GetString());
        Assert.Equal(clinic.Plain(latest, "MMM d, yyyy"), offer.GetProperty("day").GetString());
        Assert.Equal(clinic.Day(latest) == clinic.Day(DateTimeOffset.UtcNow), offer.GetProperty("today").GetBoolean());
        Assert.Equal(["1"], await ProxyTests.QueryAsync(apps,
            $"SELECT count(*)::text FROM ehr.audit_events WHERE patient_id = @patient AND actor_id = '{clinician.Id}' AND action = 'read' AND table_name = 'patients' AND part = 'vitals' AND occurred_at >= '{since:O}'", first));
        Assert.Equal(draft, await ProxyTests.QueryAsync(apps, $"SELECT to_jsonb(d)::text FROM ehr.clinical_drafts d WHERE d.patient_id = @patient", first));
        Assert.DoesNotContain("126/80", await session.Client.GetStringAsync(path, Cancel));

        var second = await apps.InsertPatientAsync("Synthetic", "Weighed");
        await apps.AssignAsync(clinician, second);
        using (var recorded = await RecordAtAsync(session.Client, second, At(3 * 24 * 60), (Weight, "70"), (Height, "170")))
        {
            Assert.Contains("Saved.", await ReadAsync(recorded));
        }

        var earlier = new DateTimeOffset(DateTime.SpecifyKind(DateTime.ParseExact(At(3 * 24 * 60), "yyyy-MM-ddTHH:mm", CultureInfo.InvariantCulture), DateTimeKind.Utc));
        var older = await OfferAsync(session.Client, (await DraftAsync(session.Client, second)).Path);
        Assert.Matches($"^Vital signs, {System.Text.RegularExpressions.Regex.Escape(clinic.Format(earlier))}: weight 70 kg, height 170 cm, body mass index [0-9.]+ kg/m²\\.$", older.GetProperty("text").GetString());
        Assert.Equal(clinic.Plain(earlier, "MMM d, yyyy"), older.GetProperty("day").GetString());
        Assert.False(older.GetProperty("today").GetBoolean());

        var third = await apps.InsertPatientAsync("Synthetic", "Unmeasured");
        await apps.AssignAsync(clinician, third);
        var none = await OfferAsync(session.Client, (await DraftAsync(session.Client, third)).Path);
        Assert.Equal("", none.GetProperty("text").GetString());
        Assert.Equal(JsonValueKind.Null, none.GetProperty("day").ValueKind);

        var stranger = await apps.InsertPatientAsync("Synthetic", "Elsewhere");
        using (var refused = await session.Client.GetAsync($"/patients/{stranger}/notes/drafts/{Guid.NewGuid()}?handler=Vitals", Cancel))
        {
            Assert.Equal(HttpStatusCode.NotFound, refused.StatusCode);
        }

        async Task<HttpResponseMessage> PostAsync(string target, long version, params (string Name, object Value)[] extra)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, target);
            request.Headers.Add("HX-Request", "true");
            request.Content = Form([("__RequestVerificationToken", token), ("Version", version), ("Subjective", "Synthetic subjective: headache."),
                ("Objective", text), ("Assessment", "Synthetic assessment: tension headache."), ("Plan", "Synthetic plan: rest."), .. extra]);
            return await session.Client.SendAsync(request, Cancel);
        }

        using (var review = await PostAsync(path + "?handler=Review", 1))
        {
            Assert.Equal(HttpStatusCode.OK, review.StatusCode);
        }

        using (var signed = await PostAsync(path + "/confirm", 2, ("Confirmed", true)))
        {
            Assert.Equal(HttpStatusCode.OK, signed.StatusCode);
        }

        Assert.Equal([text], await ProxyTests.QueryAsync(apps, "SELECT objective FROM ehr.clinical_notes WHERE patient_id = @patient", first));
    }
}
