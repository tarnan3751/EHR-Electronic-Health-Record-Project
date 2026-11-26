using Ehr.Data.Accounts;
using Ehr.Testing;
using Npgsql;
using Xunit;

namespace Ehr.Security.Tests;

// The expansion plan's step 8: ordering safety checks, as the runtime roles, in SQL of its own (db/policies/
// 60-orders.sql and 61-order-safety.sql). The database finds what is known against a medication order itself, whatever
// the client sent: an allergy to the drug, to a drug of its class or of a cross-reactive class, an interaction with
// something the patient takes. It keeps what it found on the order, and signs a seriously warned order only with a
// reason. Only a clinician gets an answer from the function that finds them.
public class OrderSafetySecurityTests(MigratedDatabase database) : IClassFixture<MigratedDatabase>
{
    string App => database.ConnectionStringFor("ehr_app", MigratedDatabase.AppPassword);

    string Read => database.ConnectionStringFor("ehr_read", MigratedDatabase.ReadPassword);

    // The clinic's common items: amoxicillin (SYN-MED-005), sertraline (SYN-MED-007), lisinopril (SYN-MED-001).
    const string Order = """
        INSERT INTO ehr.orders (id, patient_id, type, catalog_code, details, status, safety_override_reason, safety_warnings)
        VALUES (@id, @patient, 'medication', @code, '{}', 'active', @reason, '[{"kind": "forged"}]')
        """;

    const string Warnings = "SELECT kind || '|' || severity || '|' || lower(ordered) || '|' || lower(against) || '|' || source FROM ehr.order_safety_warnings(@patient, @code) ORDER BY 1";

    async Task<(Guid Clinician, Guid Patient)> OnPanelAsync()
    {
        var clinician = await Accounts.StaffAsync(database, StaffRoles.Clinician);
        var patient = await Accounts.PatientAsync(database, clinician);
        await Accounts.AssignAsync(database, clinician, patient);
        return (clinician, patient);
    }

    Task AsSuperuserAsync(string sql, params (string Name, object Value)[] parameters) =>
        Accounts.ExecuteAsync(database.SuperuserConnectionString, null, sql, parameters);

    // An active, coded allergy to the drug of that name, as the chart's coded entry writes it.
    Task AllergyAsync(Guid patient, string drug, string status = "active") => AsSuperuserAsync(
        """
        INSERT INTO ehr.allergy_intolerances (id, patient_id, code, code_system, display, clinical_status, recorded_at)
        SELECT gen_random_uuid(), @patient, c.code, 'rxnorm', c.display, @status, now()
        FROM ehr.clinical_codes c WHERE c.kind = 'allergen' AND c.system = 'rxnorm' AND lower(c.display) = @drug
        """, ("patient", patient), ("drug", drug), ("status", status));

    // An active medication on the chart, coded with the ingredient of that name.
    Task TakingAsync(Guid patient, string drug, string status = "active") => AsSuperuserAsync(
        """
        INSERT INTO ehr.medication_statements (id, patient_id, code, display, status)
        SELECT gen_random_uuid(), @patient, c.code, c.display, @status
        FROM ehr.clinical_codes c WHERE c.kind = 'allergen' AND c.system = 'rxnorm' AND lower(c.display) = @drug
        """, ("patient", patient), ("drug", drug), ("status", status));

    Task SignAsync(Guid clinician, Guid patient, string code, string? reason, Guid? id = null) =>
        Accounts.ExecuteAsync(App, clinician, Order, ("id", id ?? Guid.CreateVersion7()), ("patient", patient), ("code", code), ("reason", (object?)reason ?? DBNull.Value));

    async Task NeedsAReasonAsync(Guid clinician, Guid patient, string code)
    {
        foreach (var reason in new[] { null, "", "   " })
        {
            var refused = await Assert.ThrowsAsync<PostgresException>(() => SignAsync(clinician, patient, code, reason));
            Assert.Equal(PostgresErrorCodes.CheckViolation, refused.SqlState);
            Assert.Equal("orders_safety_override", refused.ConstraintName);
        }
    }

    Task<List<string>> OrderRowAsync(Guid order) => Accounts.QueryAsync(database.SuperuserConnectionString, null,
        "SELECT coalesce(safety_override_reason, '-') || ' ' || coalesce((SELECT string_agg(w->>'kind' || ':' || (w->>'severity') || ':' || (w->>'source'), ',' ORDER BY w->>'kind', w->>'source') FROM jsonb_array_elements(safety_warnings) w), '-') FROM ehr.orders WHERE id = @id",
        ("id", order));

    [Fact]
    public async Task An_order_for_a_drug_the_patient_is_allergic_to_needs_a_reason_which_is_kept_with_what_was_found()
    {
        var (clinician, patient) = await OnPanelAsync();
        await AllergyAsync(patient, "amoxicillin");
        Assert.Equal(["allergy|major|amoxicillin|amoxicillin|chart"], await Accounts.QueryAsync(App, clinician, Warnings, ("patient", patient), ("code", "SYN-MED-005")));

        await NeedsAReasonAsync(clinician, patient, "SYN-MED-005");
        var order = Guid.CreateVersion7();
        await SignAsync(clinician, patient, "SYN-MED-005", "Synthetic: allergy ruled out by testing", order);
        // The reason, and what the database found, not what the client claimed it found.
        Assert.Equal(["Synthetic: allergy ruled out by testing allergy:major:chart"], await OrderRowAsync(order));
        // Both are in the write audit with the order.
        Assert.Equal(["true true"], await Accounts.QueryAsync(database.SuperuserConnectionString, null,
            "SELECT (details::text LIKE '%allergy ruled out by testing%') || ' ' || (details::text LIKE '%safety_warnings%') FROM ehr.audit_events WHERE table_name = 'orders' AND row_id = @id AND action = 'insert'",
            ("id", order)));
    }

    [Fact]
    public async Task A_drug_of_the_same_class_or_of_a_cross_reactive_class_is_warned_about()
    {
        // Another penicillin: a class the cross-reactivity table names, so it stops the order.
        var (clinician, patient) = await OnPanelAsync();
        await AllergyAsync(patient, "ampicillin");
        Assert.Equal(["allergy-class|moderate|amoxicillin|ampicillin|cross-reactivity"],
            await Accounts.QueryAsync(App, clinician, Warnings, ("patient", patient), ("code", "SYN-MED-005")));
        await NeedsAReasonAsync(clinician, patient, "SYN-MED-005");

        // A cephalosporin allergy and a penicillin: the curated table's row, with its note and its source.
        var (other, second) = await OnPanelAsync();
        await AllergyAsync(second, "cephalexin");
        Assert.Equal(["allergy-class|cross-reactivity|true|true"], await Accounts.QueryAsync(App, other,
            "SELECT kind || '|' || source || '|' || (detail LIKE '%cephalosporin%') || '|' || (citation LIKE 'Khan DA%') FROM ehr.order_safety_warnings(@patient, 'SYN-MED-005')",
            ("patient", second)));
        await NeedsAReasonAsync(other, second, "SYN-MED-005");

        // Another drug of an FDA class the table doesn't name (two ACE inhibitors): a note, kept on the order, and no
        // reason needed. FDA classes group drugs by how they act, which is not how most allergies run.
        var (fourth, classmate) = await OnPanelAsync();
        await AllergyAsync(classmate, "enalapril");
        Assert.Equal(["allergy-class|minor|lisinopril|enalapril|fda-class"],
            await Accounts.QueryAsync(App, fourth, Warnings, ("patient", classmate), ("code", "SYN-MED-001")));
        var noted = Guid.CreateVersion7();
        await SignAsync(fourth, classmate, "SYN-MED-001", null, noted);
        Assert.Equal(["- allergy-class:minor:fda-class"], await OrderRowAsync(noted));

        // An allergy that is no longer active, or to something else, warns about nothing.
        var (third, unaffected) = await OnPanelAsync();
        await AllergyAsync(unaffected, "amoxicillin", "resolved");
        await AllergyAsync(unaffected, "ibuprofen");
        Assert.Empty(await Accounts.QueryAsync(App, third, Warnings, ("patient", unaffected), ("code", "SYN-MED-005")));
        var order = Guid.CreateVersion7();
        await SignAsync(third, unaffected, "SYN-MED-005", "Synthetic: a reason nothing called for", order);
        Assert.Equal(["- -"], await OrderRowAsync(order));
    }

    // A medication marked sensitive is named to the clinician who is ordering, and not in what the order keeps: an
    // order's audit details are open to admins, who never see sensitive items (step 2b).
    [Fact]
    public async Task A_sensitive_medication_is_not_named_in_what_the_order_and_its_audit_keep()
    {
        var (clinician, patient) = await OnPanelAsync();
        await TakingAsync(patient, "phenelzine");
        await AsSuperuserAsync("UPDATE ehr.medication_statements SET sensitive = true WHERE patient_id = @patient", ("patient", patient));
        Assert.Contains("interaction|high-priority|sertraline|phenelzine|onc", await Accounts.QueryAsync(App, clinician, Warnings, ("patient", patient), ("code", "SYN-MED-007")));

        var order = Guid.CreateVersion7();
        await SignAsync(clinician, patient, "SYN-MED-007", "Synthetic: washout complete", order);
        Assert.Equal(["a medication marked sensitive"], (await Accounts.QueryAsync(database.SuperuserConnectionString, null,
            "SELECT DISTINCT w->>'against' FROM ehr.orders o, jsonb_array_elements(o.safety_warnings) w WHERE o.id = @id", ("id", order))));
        var admin = await Accounts.StaffAsync(database, StaffRoles.Admin);
        Assert.Equal(["false"], await Accounts.QueryAsync(App, admin,
            """
            SELECT bool_or(d.details::text ILIKE '%phenelzine%')::text
            FROM ehr.audit_events e, ehr.shown_audit_details(ARRAY[e.id]) d WHERE e.table_name = 'orders' AND e.row_id = @id
            """, ("id", order)));
    }

    // A product with two ingredients that interact with each other, ordered again while the patient takes it: a
    // refill is not an interaction with itself.
    [Fact]
    public async Task A_combination_product_does_not_interact_with_itself()
    {
        var (clinician, patient) = await OnPanelAsync();
        var product = (await Accounts.QueryAsync(database.SuperuserConnectionString, null,
            """
            SELECT i.code FROM ehr.order_catalog_items i
            JOIN ehr.drug_ingredients a ON a.product_code = i.standard_code
            JOIN ehr.drug_ingredients b ON b.product_code = i.standard_code AND b.ingredient_code > a.ingredient_code
            JOIN ehr.drug_interactions x ON x.ingredient_a = a.ingredient_code AND x.ingredient_b = b.ingredient_code AND x.level = 'major'
            WHERE i.type = 'medication' ORDER BY i.code LIMIT 1
            """)).Single();
        await SignAsync(clinician, patient, product, null);

        Assert.Empty(await Accounts.QueryAsync(App, clinician, Warnings, ("patient", patient), ("code", product)));
    }

    [Fact]
    public async Task Interactions_come_from_each_source_and_only_serious_ones_need_a_reason()
    {
        // An FDA label's pair (major), with what the label says and where to read it.
        var (clinician, patient) = await OnPanelAsync();
        await TakingAsync(patient, "linezolid");
        Assert.Equal(["interaction|major|fda-label|true|true"], await Accounts.QueryAsync(App, clinician,
            "SELECT kind || '|' || severity || '|' || source || '|' || (detail LIKE 'The sertraline label contraindicates%') || '|' || (citation LIKE 'FDA label: SERTRALINE%dailymed.nlm.nih.gov%') FROM ehr.order_safety_warnings(@patient, 'SYN-MED-007')",
            ("patient", patient)));
        await NeedsAReasonAsync(clinician, patient, "SYN-MED-007");

        // The ONC list's pair (high priority).
        var (second, onMaoi) = await OnPanelAsync();
        await TakingAsync(onMaoi, "phenelzine");
        Assert.Contains("interaction|high-priority|sertraline|phenelzine|onc", await Accounts.QueryAsync(App, second, Warnings, ("patient", onMaoi), ("code", "SYN-MED-007")));
        await NeedsAReasonAsync(second, onMaoi, "SYN-MED-007");

        // DDInter's moderate pair, against another active medication order: found and kept, and no reason needed.
        var (third, onMetformin) = await OnPanelAsync();
        await SignAsync(third, onMetformin, "SYN-MED-002", null);
        Assert.Equal(["interaction|moderate|lisinopril|metformin|ddinter"], await Accounts.QueryAsync(App, third, Warnings, ("patient", onMetformin), ("code", "SYN-MED-001")));
        var order = Guid.CreateVersion7();
        await SignAsync(third, onMetformin, "SYN-MED-001", "Synthetic: not needed", order);
        Assert.Equal(["- interaction:moderate:ddinter"], await OrderRowAsync(order));

        // A medication that was stopped, and an order that was discontinued, are not things the patient takes.
        var (fourth, stopped) = await OnPanelAsync();
        await TakingAsync(stopped, "linezolid", "stopped");
        Assert.Empty(await Accounts.QueryAsync(App, fourth, Warnings, ("patient", stopped), ("code", "SYN-MED-007")));
    }

    [Fact]
    public async Task Only_a_clinician_is_told_what_is_known_against_an_order()
    {
        var (clinician, patient) = await OnPanelAsync();
        await AllergyAsync(patient, "amoxicillin");
        await TakingAsync(patient, "linezolid");
        var offPanel = await Accounts.StaffAsync(database, StaffRoles.Clinician);
        var admin = await Accounts.StaffAsync(database, StaffRoles.Admin);
        var other = await Accounts.PatientAccountAsync(database, await Accounts.PatientAsync(database, clinician));

        foreach (var connection in new[] { App, Read })
        {
            // Clinicians read every chart, so every clinician is answered; off-site too.
            foreach (var reader in new[] { clinician, offPanel })
            {
                Assert.Single(await Accounts.QueryAsync(connection, reader, Warnings, ("patient", patient), ("code", "SYN-MED-005")));
                Assert.Single(await Accounts.QueryAsync(connection, reader, Warnings, ("patient", patient), ("code", "SYN-MED-007")));
            }

            // An admin, another patient and nobody see no allergies and no medications, so they learn nothing.
            foreach (var refused in new Guid?[] { admin, other, null })
            {
                Assert.Empty(await Accounts.QueryAsync(connection, refused, Warnings, ("patient", patient), ("code", "SYN-MED-005")));
                Assert.Empty(await Accounts.QueryAsync(connection, refused, Warnings, ("patient", patient), ("code", "SYN-MED-007")));
            }
        }

        // A lab order is not checked, whatever the patient is allergic to.
        Assert.Empty(await Accounts.QueryAsync(App, clinician, Warnings, ("patient", patient), ("code", "SYN-LAB-001")));
    }
}
