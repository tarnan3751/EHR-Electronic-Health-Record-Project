using Npgsql;
using Xunit;
using static Ehr.Web.Tests.Browser;

namespace Ehr.Web.Tests;

// Ordering safety checks (the expansion plan's step 8): helpers for the test cases TC-UC1-18 to 20. What the database
// requires whatever the app does is in Ehr.Security.Tests (OrderSafetySecurityTests).
static class SafetyTests
{
    static CancellationToken Cancel => TestContext.Current.CancellationToken;

    public static readonly (string Name, string Value)[] Details = [("dose", "50 mg"), ("route", "By mouth"), ("frequency", "Once a day")];

    // An active, coded allergy to the drug of that name, as the chart's coded entry writes it.
    public static Task AllergyAsync(WebApps apps, Guid patient, string drug) => InsertAsync(apps,
        """
        INSERT INTO ehr.allergy_intolerances (id, patient_id, code, code_system, display, clinical_status, recorded_at)
        SELECT gen_random_uuid(), @patient, c.code, 'rxnorm', c.display, 'active', now()
        FROM ehr.clinical_codes c WHERE c.kind = 'allergen' AND c.system = 'rxnorm' AND lower(c.display) = @drug
        """, patient, drug);

    // An active medication on the chart, coded with the ingredient of that name.
    public static Task TakingAsync(WebApps apps, Guid patient, string drug) => InsertAsync(apps,
        """
        INSERT INTO ehr.medication_statements (id, patient_id, code, display, status)
        SELECT gen_random_uuid(), @patient, c.code, c.display, 'active'
        FROM ehr.clinical_codes c WHERE c.kind = 'allergen' AND c.system = 'rxnorm' AND lower(c.display) = @drug
        """, patient, drug);

    static async Task InsertAsync(WebApps apps, string sql, Guid patient, string drug)
    {
        await using var db = await apps.OpenDatabaseAsync();
        await using var command = new NpgsqlCommand(sql, db);
        command.Parameters.AddWithValue("patient", patient);
        command.Parameters.AddWithValue("drug", drug);
        Assert.Equal(1, await command.ExecuteNonQueryAsync(Cancel));
    }

    // "Sign order" for a medication as the form posts it without JavaScript, with the reason for overriding when given.
    public static async Task<HttpResponseMessage> SignAsync(HttpClient client, Guid patient, string item, string? safetyReason = null, bool seen = false)
    {
        var path = OrderTests.FormPath(patient, "medication");
        var token = Token(await client.GetStringAsync(path, Cancel));
        return await client.PostAsync(path, Form([("__RequestVerificationToken", token), ("Item", item),
            .. Details.Select(d => ($"Details[{d.Name}]", (object)d.Value)), .. safetyReason is null ? [] : new[] { ("SafetyReason", (object)safetyReason) }, .. seen ? new[] { ("SafetySeen", (object)"true") } : []]), Cancel);
    }

    // "reason|kinds" of the patient's medication orders, oldest first: "-" where there is none.
    public static Task<List<string>> OrdersAsync(WebApps apps, Guid patient) => ProxyTests.QueryAsync(apps,
        """
        SELECT coalesce(safety_override_reason, '-') || '|' || coalesce((SELECT string_agg(w->>'kind' || ':' || (w->>'severity') || ':' || (w->>'source'), ',' ORDER BY w->>'kind', w->>'source')
            FROM jsonb_array_elements(safety_warnings) w), '-')
        FROM ehr.orders WHERE patient_id = @patient AND type = 'medication' ORDER BY id
        """, patient);
}
