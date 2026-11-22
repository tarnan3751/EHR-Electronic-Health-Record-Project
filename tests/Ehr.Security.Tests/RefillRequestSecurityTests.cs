using System.Data;
using Ehr.Data.Accounts;
using Ehr.Testing;
using Npgsql;
using Xunit;

namespace Ehr.Security.Tests;

// The expansion plan's step 13d: what the database lets each account do with a refill request, as the runtime roles, in
// SQL of its own (db/policies/10-grants.sql, 30-rls.sql, 40-audit.sql, 70-inbox.sql and 77-refill-requests.sql). The
// patient, or a proxy whose access includes messages, asks about an active medication of the record they are looking
// at, coded as a product the clinic orders, that they may read themselves, one open request a medication, while the
// patient has a care team to tell; the database sets the rest and puts an item in each care-team clinician's inbox. A
// clinician with the patient on their panel answers it once: approves it with the order they signed for it in the same
// transaction, for the same product as the medication on the chart, or declines it with a message; every item is then
// done. The items follow the care team as it changes. Its sensitivity is its medication's. The portal reads no row, only
// what ehr.portal_refill_requests gives. Nothing else changes and nothing is deleted.
public class RefillRequestSecurityTests(MigratedDatabase database) : IClassFixture<MigratedDatabase>
{
    static CancellationToken Cancel => TestContext.Current.CancellationToken;

    string App => database.ConnectionStringFor("ehr_app", MigratedDatabase.AppPassword);

    string Read => database.ConnectionStringFor("ehr_read", MigratedDatabase.ReadPassword);

    // Lisinopril 10 mg, as the chart's coded entry writes it, and the catalog's item for that product, under the same
    // code. Not the same product: the clinic's common item for it (SYN-MED-001, an item of its own), the same ingredient
    // at 20 mg (314077), lisinopril with hydrochlorothiazide (197885) and atorvastatin (SYN-MED-003).
    const string Lisinopril = "314076";

    // Asked as the portal asks, with forged values for everything the database sets.
    const string Ask = """
        INSERT INTO ehr.refill_requests (id, patient_id, medication_statement_id, medication_display, note, requested_by, requested_at, status,
            order_id, ordered_display, decline_message, decided_by, decided_at, sensitive)
        VALUES (@id, @patient, @medication, 'Forged name', @note, gen_random_uuid(), '2000-01-01', 'completed', NULL, 'Forged', NULL, NULL, '2000-01-01', NOT @sensitive)
        """;

    // A patient with a portal account of their own, on a clinician's panel. The clinician, the patient and the account.
    async Task<(Guid Clinician, Guid Patient, Guid Account)> OnPanelAsync()
    {
        var clinician = await Accounts.StaffAsync(database, StaffRoles.Clinician);
        var patient = await Accounts.PatientAsync(database, clinician);
        await Accounts.AssignAsync(database, clinician, patient);
        return (clinician, patient, await Accounts.PatientAccountAsync(database, patient));
    }

    // A medication on the patient's chart, as the chart's coded entry writes it. Its ID.
    async Task<Guid> MedicationAsync(Guid patient, string? code = Lisinopril, string status = "active", bool sensitive = false,
        string display = "lisinopril 10 MG Oral Tablet")
    {
        var id = Guid.CreateVersion7();
        await Accounts.ExecuteAsync(database.SuperuserConnectionString, null,
            "INSERT INTO ehr.medication_statements (id, patient_id, code, display, dosage, status, sensitive) VALUES (@id, @patient, @code, @display, '1 tablet by mouth daily', @status, @sensitive)",
            ("id", id), ("patient", patient), ("code", (object?)code ?? DBNull.Value), ("display", display), ("status", status), ("sensitive", sensitive));
        return id;
    }

    // A proxy for the patient, with a portal account of their own, given access by the patient. The account and grant.
    async Task<(Guid Account, Guid Grant)> ProxyAsync(Guid patientAccount, Guid patient, string scope = "record-and-messages", bool includeSensitive = false)
    {
        var (account, email) = await Accounts.ProxyOnlyAccountAsync(database);
        var grant = Guid.CreateVersion7();
        await Accounts.ExecuteAsync(App, patientAccount,
            """
            INSERT INTO ehr.proxy_grants (id, patient_id, proxy_email, scope, include_sensitive, expires_at, granted_by, granted_at, granted_by_kind)
            VALUES (@id, @patient, @email, @scope, @sensitive, now() + interval '30 days', gen_random_uuid(), '2000-01-01', 'patient')
            """, ("id", grant), ("patient", patient), ("email", email), ("scope", scope), ("sensitive", includeSensitive));
        return (account, grant);
    }

    // Asked for, as the account, under a grant when one is given. Its ID.
    async Task<Guid> AskAsync(Guid? account, Guid patient, Guid medication, string? note = null, Guid? grant = null, string? connection = null)
    {
        var id = Guid.CreateVersion7();
        using (grant is { } acting ? Accounts.ActingUnder(acting) : null)
        {
            await Accounts.ExecuteAsync(connection ?? App, account, Ask, ("id", id), ("patient", patient), ("medication", medication),
                ("note", (object?)note ?? DBNull.Value), ("sensitive", false));
        }

        return id;
    }

    // One transaction as the user, at the given isolation level: the statements run in order, and their rows changed are
    // returned. The transaction commits.
    async Task<int> RunAsync(string connectionString, Guid? user, string sql, IsolationLevel isolation, params (string Name, object Value)[] parameters)
    {
        var (connection, transaction) = await BeginAsync(connectionString, user, isolation);
        await using (connection)
        {
            await using var command = new NpgsqlCommand(sql, connection, transaction);
            foreach (var (name, value) in parameters)
            {
                command.Parameters.AddWithValue(name, value);
            }

            var changed = await command.ExecuteNonQueryAsync(Cancel);
            await transaction.CommitAsync(Cancel);
            return changed;
        }
    }

    Task<int> RunAsync(Guid? user, string sql, params (string Name, object Value)[] parameters) => RunAsync(App, user, sql, IsolationLevel.ReadCommitted, parameters);

    static async Task<(NpgsqlConnection, NpgsqlTransaction)> BeginAsync(string connectionString, Guid? user, IsolationLevel isolation = IsolationLevel.ReadCommitted)
    {
        var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(Cancel);
        var transaction = await connection.BeginTransactionAsync(isolation, Cancel);
        await using var set = new NpgsqlCommand("SELECT set_config('app.user_id', @user, true)", connection, transaction);
        set.Parameters.AddWithValue("user", user?.ToString() ?? "");
        await set.ExecuteNonQueryAsync(Cancel);
        return (connection, transaction);
    }

    // Approved as the order form approves one: the order signed, then the request answered with it, in one transaction.
    const string Approve = """
        INSERT INTO ehr.orders (id, patient_id, type, catalog_code, details, status, duplicate_reason) VALUES (@order, @patient, @type, @code, '{}', 'active', @reason);
        UPDATE ehr.refill_requests SET order_id = @order WHERE id = @request;
        """;

    Task<int> ApproveAsync(Guid clinician, Guid patient, Guid request, string code = Lisinopril, string type = "medication", Guid? order = null,
        string? duplicateReason = null) =>
        RunAsync(clinician, Approve, ("order", order ?? Guid.CreateVersion7()), ("patient", patient), ("type", type), ("code", code), ("request", request),
            ("reason", (object?)duplicateReason ?? DBNull.Value));

    Task<int> DeclineAsync(Guid? clinician, Guid request, string message = "  Synthetic: please book a visit first.  ", string? connection = null) =>
        RunAsync(connection ?? App, clinician, "UPDATE ehr.refill_requests SET decline_message = @message WHERE id = @id", IsolationLevel.ReadCommitted,
            ("id", request), ("message", message));

    Task<List<string>> AsSuperuserAsync(string sql, params (string Name, object Value)[] parameters) =>
        Accounts.QueryAsync(database.SuperuserConnectionString, null, sql, parameters);

    // The request as the database keeps it.
    Task<List<string>> RowAsync(Guid request) => AsSuperuserAsync(
        """
        SELECT status || '|' || medication_display || '|' || coalesce(note, '-') || '|' || requested_by || '|' || (requested_at > now() - interval '1 minute')
            || '|' || coalesce(ordered_display, '-') || '|' || coalesce(decline_message, '-') || '|' || coalesce(decided_by::text, '-') || '|' || sensitive
        FROM ehr.refill_requests WHERE id = @id
        """, ("id", request));

    // The inbox items a request put in, by recipient: whether each is still open.
    Task<List<string>> ItemsAsync(Guid request) => AsSuperuserAsync(
        "SELECT recipient_id || ' ' || kind || ' ' || summary || ' ' || (done_at IS NULL) FROM ehr.inbox_items WHERE source_table = 'refill_requests' AND source_id = @id ORDER BY recipient_id::text COLLATE \"C\"",
        ("id", request));

    static async Task<PostgresException> RefusedAsync(string state, Func<Task> attempt, string? constraint = null)
    {
        var refused = await Assert.ThrowsAsync<PostgresException>(attempt);
        Assert.Equal(state, refused.SqlState);
        if (constraint is not null)
        {
            Assert.Equal(constraint, refused.ConstraintName);
        }

        return refused;
    }

    [Fact]
    public async Task The_patient_asks_for_an_active_coded_medication_and_the_database_sets_the_rest_and_tells_the_care_team()
    {
        var (clinician, patient, account) = await OnPanelAsync();
        var colleague = await Accounts.StaffAsync(database, StaffRoles.Clinician);
        await Accounts.AssignAsync(database, colleague, patient);
        // On the panel, but disabled: nobody to read an item.
        var disabled = await Accounts.StaffAsync(database, StaffRoles.Clinician);
        await Accounts.AssignAsync(database, disabled, patient);
        await Accounts.LockOutAsync(database, disabled);
        var medication = await MedicationAsync(patient);

        var request = await AskAsync(account, patient, medication, "  Synthetic: running out on Friday.\n  ");

        Assert.Equal([$"requested|lisinopril 10 MG Oral Tablet|Synthetic: running out on Friday.|{account}|true|-|-|-|false"], await RowAsync(request));
        Assert.Equal([.. new[] { clinician, colleague }.Select(to => to.ToString()).Order(StringComparer.Ordinal).Select(to => $"{to} refill-request A patient asked to renew a medication true")],
            await ItemsAsync(request));
        Assert.Equal(["insert"], await AsSuperuserAsync(
            "SELECT action FROM ehr.audit_events WHERE table_name = 'refill_requests' AND row_id = @id AND actor_id = @actor", ("id", request), ("actor", account)));
    }

    [Fact]
    public async Task Only_the_records_own_session_or_a_proxy_with_messages_asks_and_only_about_an_active_coded_medication_it_may_read()
    {
        var (clinician, patient, account) = await OnPanelAsync();
        var admin = await Accounts.StaffAsync(database, StaffRoles.Admin);
        var (_, elsewhere, elsewhereAccount) = await OnPanelAsync();
        var theirs = await MedicationAsync(elsewhere);
        var (recordOnly, recordOnlyGrant) = await ProxyAsync(account, patient, scope: "record");
        var (without, withoutGrant) = await ProxyAsync(account, patient);
        var (allowed, allowedGrant) = await ProxyAsync(account, patient, includeSensitive: true);
        var (revoked, revokedGrant) = await ProxyAsync(account, patient);
        await Accounts.ExecuteAsync(App, account, "UPDATE ehr.proxy_grants SET revoked_at = now() WHERE id = @id", ("id", revokedGrant));

        // Not this session's to ask for: another patient's medication, or this one's asked for by someone else.
        await RefusedAsync(PostgresErrorCodes.InsufficientPrivilege, () => AskAsync(account, patient, theirs));
        await RefusedAsync(PostgresErrorCodes.InsufficientPrivilege, async () => await AskAsync(elsewhereAccount, patient, await MedicationAsync(patient)));
        await RefusedAsync(PostgresErrorCodes.InsufficientPrivilege, () => AskAsync(account, elsewhere, theirs));
        // A clinician, an administrator, nobody, a proxy whose access is the record alone, one whose access was removed,
        // and a proxy not acting under their access.
        var ours = await MedicationAsync(patient);
        foreach (var (user, grant) in new (Guid?, Guid?)[] { (clinician, null), (admin, null), (null, null), (recordOnly, recordOnlyGrant), (revoked, revokedGrant), (without, null) })
        {
            await RefusedAsync(PostgresErrorCodes.InsufficientPrivilege, () => AskAsync(user, patient, ours, grant: grant));
        }

        // The cloud app's role asks nothing: it has no INSERT.
        await RefusedAsync(PostgresErrorCodes.InsufficientPrivilege, async () => await AskAsync(account, patient, await MedicationAsync(patient), connection: Read));

        // An entry in error, which the portal can't read; stopped; with no drug code; a sensitive one, for a proxy the
        // patient didn't allow sensitive items.
        await RefusedAsync(PostgresErrorCodes.InsufficientPrivilege, async () => await AskAsync(account, patient, await MedicationAsync(patient, status: "entered-in-error")));
        await RefusedAsync(PostgresErrorCodes.CheckViolation, async () => await AskAsync(account, patient, await MedicationAsync(patient, status: "stopped")), "refill_requests_medication_active");
        await RefusedAsync(PostgresErrorCodes.CheckViolation, async () => await AskAsync(account, patient, await MedicationAsync(patient, code: null)), "refill_requests_medication_coded");
        var sensitive = await MedicationAsync(patient, sensitive: true);
        await RefusedAsync(PostgresErrorCodes.InsufficientPrivilege, () => AskAsync(without, patient, sensitive, grant: withoutGrant));
        // A note longer than 500 characters.
        await RefusedAsync(PostgresErrorCodes.StringDataRightTruncation, async () => await AskAsync(account, patient, await MedicationAsync(patient), new string('x', 501)));
        Assert.Equal(["0"], await AsSuperuserAsync("SELECT count(*)::text FROM ehr.refill_requests WHERE patient_id IN (@patient, @elsewhere)",
            ("patient", patient), ("elsewhere", elsewhere)));

        // A proxy whose access includes messages, for a medication they may read; one allowed sensitive items, for that.
        var asked = await AskAsync(without, patient, await MedicationAsync(patient), grant: withoutGrant);
        var askedSensitive = await AskAsync(allowed, patient, sensitive, grant: allowedGrant);
        const string Asked = "SELECT status || '|' || requested_by || '|' || sensitive FROM ehr.refill_requests WHERE id = @id";
        Assert.Equal([$"requested|{without}|false"], await AsSuperuserAsync(Asked, ("id", asked)));
        Assert.Equal([$"requested|{allowed}|true"], await AsSuperuserAsync(Asked, ("id", askedSensitive)));
    }

    [Fact]
    public async Task One_open_request_a_medication_and_none_while_the_patient_has_no_care_team_to_tell()
    {
        var (clinician, patient, account) = await OnPanelAsync();
        var medication = await MedicationAsync(patient);
        var first = await AskAsync(account, patient, medication);

        await RefusedAsync(PostgresErrorCodes.UniqueViolation, () => AskAsync(account, patient, medication));
        Assert.Equal(1, await DeclineAsync(clinician, first));
        // Answered, it no longer waits: the next one may be asked for.
        await AskAsync(account, patient, medication);

        // Nobody has the patient on their panel; or only a clinician whose account is disabled.
        var noTeam = await Accounts.PatientAsync(database, clinician);
        var noTeamAccount = await Accounts.PatientAccountAsync(database, noTeam);
        await RefusedAsync(PostgresErrorCodes.CheckViolation, async () => await AskAsync(noTeamAccount, noTeam, await MedicationAsync(noTeam)), "refill_requests_care_team");
        var disabled = await Accounts.StaffAsync(database, StaffRoles.Clinician);
        await Accounts.AssignAsync(database, disabled, noTeam);
        await Accounts.LockOutAsync(database, disabled);
        await RefusedAsync(PostgresErrorCodes.CheckViolation, async () => await AskAsync(noTeamAccount, noTeam, await MedicationAsync(noTeam)), "refill_requests_care_team");
        Assert.Equal(["0 0"], await AsSuperuserAsync(
            "SELECT (SELECT count(*) FROM ehr.refill_requests WHERE patient_id = @p) || ' ' || (SELECT count(*) FROM ehr.inbox_items WHERE patient_id = @p)", ("p", noTeam)));
    }

    [Fact]
    public async Task Clinicians_read_every_request_and_the_portal_no_row_only_what_its_function_gives_under_the_patients_rules()
    {
        var (clinician, patient, account) = await OnPanelAsync();
        var outsider = await Accounts.StaffAsync(database, StaffRoles.Clinician);
        var admin = await Accounts.StaffAsync(database, StaffRoles.Admin);
        var (allowed, allowedGrant) = await ProxyAsync(account, patient, includeSensitive: true);
        var (without, withoutGrant) = await ProxyAsync(account, patient);
        var (recordOnly, recordOnlyGrant) = await ProxyAsync(account, patient, scope: "record");
        var (revoked, revokedGrant) = await ProxyAsync(account, patient);
        await Accounts.ExecuteAsync(App, account, "UPDATE ehr.proxy_grants SET revoked_at = now() WHERE id = @id", ("id", revokedGrant));
        var (_, elsewhere, otherPatient) = await OnPanelAsync();
        var (proxyOnly, _) = await Accounts.ProxyOnlyAccountAsync(database);

        await AskAsync(account, patient, await MedicationAsync(patient, display: "plain"), "Mine.");
        var byProxy = await AskAsync(without, patient, await MedicationAsync(patient, display: "by-proxy"), grant: withoutGrant);
        await AskAsync(account, patient, await MedicationAsync(patient, sensitive: true, display: "sensitive"));
        await DeclineAsync(clinician, byProxy, "Synthetic: we need to see you.");
        await AskAsync(otherPatient, elsewhere, await MedicationAsync(elsewhere, display: "elsewhere"));

        const string Rows = "SELECT coalesce(string_agg(medication_display, ' ' ORDER BY medication_display), '') FROM ehr.refill_requests WHERE patient_id IN (@patient, @elsewhere)";
        const string Given = """
            SELECT coalesce(string_agg(medication_display || ':' || coalesce(asked, '-') || ':' || status || ':' || coalesce(decline_message, '-') || ':' || sensitive,
                ' ' ORDER BY medication_display), '') FROM ehr.portal_refill_requests(100)
            """;
        foreach (var connection in new[] { App, Read })
        {
            async Task<string> QueryAsAsync(string sql, Guid? user, Guid? grant)
            {
                using (grant is { } acting ? Accounts.ActingUnder(acting) : null)
                {
                    return (await Accounts.QueryAsync(connection, user, sql, ("patient", patient), ("elsewhere", elsewhere))).Single();
                }
            }

            foreach (var (user, grant, rows, given) in new (Guid?, Guid?, string, string)[]
            {
                (clinician, null, "by-proxy elsewhere plain sensitive", ""),
                (outsider, null, "by-proxy elsewhere plain sensitive", ""),
                (admin, null, "", ""),
                (null, null, "", ""),
                (account, null, "", "by-proxy:proxy:rejected:Synthetic: we need to see you.:false plain:you:requested:-:false sensitive:you:requested:-:true"),
                (allowed, allowedGrant, "", "by-proxy:-:rejected:Synthetic: we need to see you.:false plain:-:requested:-:false sensitive:-:requested:-:true"),
                (without, withoutGrant, "", "by-proxy:you:rejected:Synthetic: we need to see you.:false plain:-:requested:-:false"),
                (recordOnly, recordOnlyGrant, "", ""),
                (revoked, revokedGrant, "", ""),
                (otherPatient, allowedGrant, "", ""),
                (proxyOnly, null, "", ""),
            })
            {
                Assert.Equal(rows, await QueryAsAsync(Rows, user, grant));
                Assert.Equal(given, await QueryAsAsync(Given, user, grant));
            }
        }

        Assert.Equal(["Mine."], await Accounts.QueryAsync(App, account, "SELECT note FROM ehr.portal_refill_requests(100) WHERE medication_display = 'plain'"));
        // The function returns these columns and no other: nothing of who answered, the order, or which account asked.
        Assert.Equal(
            ["medication_statement_id uuid", "medication_display text", "note text", "asked text", "requested_at timestamp with time zone", "status text",
                "ordered_display text", "decline_message text", "decided_at timestamp with time zone", "sensitive boolean"],
            await AsSuperuserAsync(
                """
                SELECT a.name || ' ' || format_type(a.type, NULL) FROM unnest(
                    (SELECT p.proallargtypes FROM pg_proc p WHERE p.oid = 'ehr.portal_refill_requests'::regproc),
                    (SELECT p.proargnames FROM pg_proc p WHERE p.oid = 'ehr.portal_refill_requests'::regproc),
                    (SELECT p.proargmodes FROM pg_proc p WHERE p.oid = 'ehr.portal_refill_requests'::regproc)) WITH ORDINALITY AS a(type, name, mode, position)
                WHERE a.mode = 't' ORDER BY a.position
                """));
    }

    [Fact]
    public async Task A_clinician_on_the_panel_approves_only_with_an_order_for_the_same_product_signed_now_and_every_item_is_done()
    {
        var (clinician, patient, account) = await OnPanelAsync();
        var colleague = await Accounts.StaffAsync(database, StaffRoles.Clinician);
        await Accounts.AssignAsync(database, colleague, patient);
        var same = await AskAsync(account, patient, await MedicationAsync(patient));
        var again = await AskAsync(account, patient, await MedicationAsync(patient));
        var metoprolol = await AskAsync(account, patient, await MedicationAsync(patient, code: "866436", display: "24 HR metoprolol succinate 50 MG Extended Release Oral Tablet"));

        // Not the same product: another strength, the clinic's common item for the drug, which is an item of its own,
        // and (the review's probe) the same ingredient's injection for an extended-release tablet.
        foreach (var (request, code) in new[] { (same, "314077"), (same, "SYN-MED-001"), (metoprolol, "866508") })
        {
            await RefusedAsync(PostgresErrorCodes.CheckViolation, () => ApproveAsync(clinician, patient, request, code), "refill_requests_same_medication");
        }

        var order = Guid.CreateVersion7();
        Assert.Equal(2, await ApproveAsync(clinician, patient, same, order: order));
        // A second order of the product needs the duplicate's reason, as any order does.
        Assert.Equal(2, await ApproveAsync(colleague, patient, again, duplicateReason: "Synthetic: a second supply."));
        Assert.Equal(2, await ApproveAsync(clinician, patient, metoprolol, code: "866436"));

        Assert.Equal([$"completed|lisinopril 10 MG Oral Tablet|-|{account}|true|lisinopril 10 MG Oral Tablet|-|{clinician}|false"], await RowAsync(same));
        Assert.Equal([$"completed|lisinopril 10 MG Oral Tablet|-|{account}|true|lisinopril 10 MG Oral Tablet|-|{colleague}|false"], await RowAsync(again));
        Assert.Equal([order.ToString()], await AsSuperuserAsync("SELECT order_id::text FROM ehr.refill_requests WHERE id = @id", ("id", same)));
        Assert.All((await ItemsAsync(same)).Concat(await ItemsAsync(again)), item => Assert.EndsWith(" false", item));
        Assert.Equal(4, (await ItemsAsync(same)).Count + (await ItemsAsync(again)).Count);
    }

    // The coordinator's addition to the plan: a request can't be closed against an order signed before, by the same
    // clinician for the same patient and drug, without anything new being ordered.
    [Fact]
    public async Task An_order_signed_in_an_earlier_transaction_never_answers_a_request()
    {
        var (clinician, patient, account) = await OnPanelAsync();
        var request = await AskAsync(account, patient, await MedicationAsync(patient));
        var earlier = Guid.CreateVersion7();
        await RunAsync(clinician, "INSERT INTO ehr.orders (id, patient_id, type, catalog_code, details, status) VALUES (@id, @patient, 'medication', '314076', '{}', 'active')",
            ("id", earlier), ("patient", patient));

        await RefusedAsync(PostgresErrorCodes.CheckViolation,
            () => RunAsync(clinician, "UPDATE ehr.refill_requests SET order_id = @order WHERE id = @id", ("order", earlier), ("id", request)), "refill_requests_order");
        Assert.Equal([$"requested|lisinopril 10 MG Oral Tablet|-|{account}|true|-|-|-|false"], await RowAsync(request));
    }

    [Fact]
    public async Task Only_the_new_order_for_the_same_medication_by_a_clinician_on_the_panel_approves_and_never_twice()
    {
        var (clinician, patient, account) = await OnPanelAsync();
        var outsider = await Accounts.StaffAsync(database, StaffRoles.Clinician);
        var admin = await Accounts.StaffAsync(database, StaffRoles.Admin);
        var (_, elsewhere, _) = await OnPanelAsync();
        var request = await AskAsync(account, patient, await MedicationAsync(patient));

        // Another drug, lisinopril combined with another ingredient, a lab order, or another patient's order.
        foreach (var (code, type, constraint) in new[]
        {
            ("SYN-MED-003", "medication", "refill_requests_same_medication"), ("197885", "medication", "refill_requests_same_medication"),
            ("314077", "medication", "refill_requests_same_medication"), ("SYN-MED-001", "medication", "refill_requests_same_medication"),
            ("SYN-LAB-001", "lab", "refill_requests_order"),
        })
        {
            await RefusedAsync(PostgresErrorCodes.CheckViolation, () => ApproveAsync(clinician, patient, request, code, type), constraint);
        }

        await Accounts.AssignAsync(database, clinician, elsewhere);
        await RefusedAsync(PostgresErrorCodes.CheckViolation, () => RunAsync(clinician,
            "INSERT INTO ehr.orders (id, patient_id, type, catalog_code, details, status) VALUES (@order, @elsewhere, 'medication', '314076', '{}', 'active'); UPDATE ehr.refill_requests SET order_id = @order WHERE id = @id",
            ("order", Guid.CreateVersion7()), ("elsewhere", elsewhere), ("id", request)), "refill_requests_order");

        // Off the panel, an administrator, the patient: no row to answer. The cloud app's role: no UPDATE.
        Assert.Equal(0, await RunAsync(outsider, "UPDATE ehr.refill_requests SET decline_message = 'No.' WHERE id = @id", ("id", request)));
        Assert.Equal(0, await RunAsync(admin, "UPDATE ehr.refill_requests SET decline_message = 'No.' WHERE id = @id", ("id", request)));
        Assert.Equal(0, await RunAsync(account, "UPDATE ehr.refill_requests SET decline_message = 'No.' WHERE id = @id", ("id", request)));
        await RefusedAsync(PostgresErrorCodes.InsufficientPrivilege, () => DeclineAsync(clinician, request, connection: Read));
        Assert.Equal(["requested"], await AsSuperuserAsync("SELECT status FROM ehr.refill_requests WHERE id = @id", ("id", request)));

        // One order answers one request: a second request of the same drug can't take it.
        var second = await AskAsync(account, patient, await MedicationAsync(patient));
        var order = Guid.CreateVersion7();
        await RefusedAsync(PostgresErrorCodes.UniqueViolation, () => RunAsync(clinician, Approve + "UPDATE ehr.refill_requests SET order_id = @order WHERE id = @second;",
            ("order", order), ("patient", patient), ("type", "medication"), ("code", Lisinopril), ("request", request), ("second", second), ("reason", DBNull.Value)));

        // Answered once: approved, then neither declined nor approved again; declined, then not approved.
        Assert.Equal(2, await ApproveAsync(clinician, patient, request));
        await RefusedAsync(PostgresErrorCodes.CheckViolation, () => DeclineAsync(clinician, request), "refill_requests_answered");
        await RefusedAsync(PostgresErrorCodes.CheckViolation, () => ApproveAsync(clinician, patient, request, code: "314077"), "refill_requests_answered");
        Assert.Equal(1, await DeclineAsync(clinician, second));
        await RefusedAsync(PostgresErrorCodes.CheckViolation, () => ApproveAsync(clinician, patient, second, code: "314077"), "refill_requests_answered");
    }

    [Fact]
    public async Task A_decline_needs_a_message_and_nothing_else_changes_and_nothing_is_deleted_even_by_the_owner()
    {
        var (clinician, patient, account) = await OnPanelAsync();
        var request = await AskAsync(account, patient, await MedicationAsync(patient), "Synthetic note.");

        foreach (var message in new[] { "", "   " })
        {
            await RefusedAsync(PostgresErrorCodes.CheckViolation, () => DeclineAsync(clinician, request, message), "refill_requests_decline_message");
        }

        await RefusedAsync(PostgresErrorCodes.StringDataRightTruncation, () => DeclineAsync(clinician, request, new string('x', 1001)));
        // An order and a message together.
        await RefusedAsync(PostgresErrorCodes.CheckViolation, () => RunAsync(clinician,
            "INSERT INTO ehr.orders (id, patient_id, type, catalog_code, details, status) VALUES (@order, @patient, 'medication', '314076', '{}', 'active'); UPDATE ehr.refill_requests SET order_id = @order, decline_message = 'Both.' WHERE id = @id",
            ("order", Guid.CreateVersion7()), ("patient", patient), ("id", request)));
        // Columns the apps may not update, and deleting.
        foreach (var sql in new[]
        {
            "UPDATE ehr.refill_requests SET note = 'changed' WHERE id = @id",
            "UPDATE ehr.refill_requests SET status = 'rejected' WHERE id = @id",
            "UPDATE ehr.refill_requests SET sensitive = true WHERE id = @id",
            "UPDATE ehr.refill_requests SET medication_display = 'changed' WHERE id = @id",
            "DELETE FROM ehr.refill_requests WHERE id = @id",
        })
        {
            await RefusedAsync(PostgresErrorCodes.InsufficientPrivilege, () => RunAsync(clinician, sql, ("id", request)));
        }

        // Answered outside READ COMMITTED.
        await RefusedAsync(PostgresErrorCodes.FeatureNotSupported, () => RunAsync(App, clinician,
            "UPDATE ehr.refill_requests SET decline_message = 'Synthetic.' WHERE id = @id", IsolationLevel.RepeatableRead, ("id", request)));
        // The owner may not change anything but its sensitivity, nor delete it.
        await RefusedAsync(PostgresErrorCodes.InsufficientPrivilege,
            () => Accounts.ExecuteAsync(database.SuperuserConnectionString, null, "UPDATE ehr.refill_requests SET note = 'changed' WHERE id = @id", ("id", request)));
        await RefusedAsync(PostgresErrorCodes.CheckViolation,
            () => Accounts.ExecuteAsync(database.SuperuserConnectionString, null, "DELETE FROM ehr.refill_requests WHERE id = @id", ("id", request)));

        Assert.Equal(1, await DeclineAsync(clinician, request));
        Assert.Equal([$"rejected|lisinopril 10 MG Oral Tablet|Synthetic note.|{account}|true|-|Synthetic: please book a visit first.|{clinician}|false"], await RowAsync(request));
    }

    [Fact]
    public async Task A_refill_item_is_done_by_answering_never_marked_done_by_the_apps()
    {
        var (clinician, patient, account) = await OnPanelAsync();
        var colleague = await Accounts.StaffAsync(database, StaffRoles.Clinician);
        await Accounts.AssignAsync(database, colleague, patient);
        var request = await AskAsync(account, patient, await MedicationAsync(patient));
        var item = (await AsSuperuserAsync("SELECT id::text FROM ehr.inbox_items WHERE source_id = @id AND recipient_id = @to", ("id", request), ("to", clinician))).Single();

        await RefusedAsync(PostgresErrorCodes.CheckViolation, () => RunAsync(clinician, "UPDATE ehr.inbox_items SET done_at = now() WHERE id = @id", ("id", Guid.Parse(item))));

        await DeclineAsync(colleague, request);
        Assert.All(await ItemsAsync(request), recipient => Assert.EndsWith(" false", recipient));
    }

    [Fact]
    public async Task A_stopped_medication_leaves_its_request_open_to_be_declined_and_never_approved()
    {
        var (clinician, patient, account) = await OnPanelAsync();
        var medication = await MedicationAsync(patient);
        var request = await AskAsync(account, patient, medication);

        await RunAsync(clinician, "UPDATE ehr.medication_statements SET status = 'stopped' WHERE id = @id", ("id", medication));

        Assert.Equal(["requested"], await AsSuperuserAsync("SELECT status FROM ehr.refill_requests WHERE id = @id", ("id", request)));
        await RefusedAsync(PostgresErrorCodes.CheckViolation, () => ApproveAsync(clinician, patient, request), "refill_requests_medication_active");
        Assert.Equal(1, await DeclineAsync(clinician, request, "Synthetic: we stopped this medicine at your visit."));
    }

    // Stopping the medication while a colleague approves: whichever comes first, the other sees it. An approval locks the
    // medication until it commits, so a stop waits for it; a stop that came first is seen, and the approval refused with
    // its order. Two connections.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task An_approval_and_a_stop_at_the_same_moment_never_renew_a_stopped_medication(bool approvedFirst)
    {
        var (clinician, patient, account) = await OnPanelAsync();
        var medication = await MedicationAsync(patient);
        var request = await AskAsync(account, patient, medication);
        var order = Guid.CreateVersion7();

        var (approving, approvingTransaction) = await BeginAsync(App, clinician);
        var (stopping, stoppingTransaction) = await BeginAsync(App, clinician);
        await using (approving)
        await using (stopping)
        {
            await using var approve = new NpgsqlCommand(Approve, approving, approvingTransaction);
            foreach (var (name, value) in new (string, object)[] { ("order", order), ("patient", patient), ("type", "medication"), ("code", Lisinopril), ("request", request), ("reason", DBNull.Value) })
            {
                approve.Parameters.AddWithValue(name, value);
            }

            await using var stop = new NpgsqlCommand("UPDATE ehr.medication_statements SET status = 'stopped' WHERE id = @id", stopping, stoppingTransaction);
            stop.Parameters.AddWithValue("id", medication);
            var (first, firstTransaction, second, secondTransaction) = approvedFirst
                ? (approve, approvingTransaction, stop, stoppingTransaction)
                : (stop, stoppingTransaction, approve, approvingTransaction);

            await first.ExecuteNonQueryAsync(Cancel);
            var waiting = second.ExecuteNonQueryAsync(Cancel);
            Assert.NotSame(waiting, await Task.WhenAny(waiting, Task.Delay(TimeSpan.FromMilliseconds(500), Cancel)));
            await firstTransaction.CommitAsync(Cancel);
            if (approvedFirst)
            {
                await waiting;
                await secondTransaction.CommitAsync(Cancel);
            }
            else
            {
                var refused = await Assert.ThrowsAsync<PostgresException>(() => waiting);
                Assert.Equal("refill_requests_medication_active", refused.ConstraintName);
            }
        }

        Assert.Equal([approvedFirst ? "completed 1" : "requested 0"], await AsSuperuserAsync(
            "SELECT r.status || ' ' || (SELECT count(*) FROM ehr.orders o WHERE o.id = @order) FROM ehr.refill_requests r WHERE r.id = @id", ("id", request), ("order", order)));
    }

    // Two clinicians answer at once: one answer stands, and the other's order goes with its refusal. Two connections.
    [Fact]
    public async Task Two_clinicians_answering_at_once_give_one_answer_and_one_order_at_most()
    {
        var (clinician, patient, account) = await OnPanelAsync();
        var colleague = await Accounts.StaffAsync(database, StaffRoles.Clinician);
        await Accounts.AssignAsync(database, colleague, patient);
        var request = await AskAsync(account, patient, await MedicationAsync(patient));
        var order = Guid.CreateVersion7();

        var (declining, decliningTransaction) = await BeginAsync(App, clinician);
        var (approving, approvingTransaction) = await BeginAsync(App, colleague);
        await using (declining)
        await using (approving)
        {
            await using var decline = new NpgsqlCommand("UPDATE ehr.refill_requests SET decline_message = 'Synthetic: no.' WHERE id = @id", declining, decliningTransaction);
            decline.Parameters.AddWithValue("id", request);
            Assert.Equal(1, await decline.ExecuteNonQueryAsync(Cancel));

            await using var approve = new NpgsqlCommand(Approve, approving, approvingTransaction);
            foreach (var (name, value) in new (string, object)[] { ("order", order), ("patient", patient), ("type", "medication"), ("code", Lisinopril), ("request", request), ("reason", DBNull.Value) })
            {
                approve.Parameters.AddWithValue(name, value);
            }

            var waiting = approve.ExecuteNonQueryAsync(Cancel);
            Assert.NotSame(waiting, await Task.WhenAny(waiting, Task.Delay(TimeSpan.FromMilliseconds(500), Cancel)));
            await decliningTransaction.CommitAsync(Cancel);
            var refused = await Assert.ThrowsAsync<PostgresException>(() => waiting);
            Assert.Equal("refill_requests_answered", refused.ConstraintName);
        }

        Assert.Equal([$"rejected 0 {clinician}"], await AsSuperuserAsync(
            "SELECT r.status || ' ' || (SELECT count(*) FROM ehr.orders o WHERE o.id = @order) || ' ' || r.decided_by FROM ehr.refill_requests r WHERE r.id = @id",
            ("id", request), ("order", order)));
    }

    [Fact]
    public async Task A_request_follows_its_medications_sensitivity_and_its_events_are_marked()
    {
        var (clinician, patient, account) = await OnPanelAsync();
        var (without, withoutGrant) = await ProxyAsync(account, patient);
        var medication = await MedicationAsync(patient);
        var request = await AskAsync(without, patient, medication, grant: withoutGrant);
        async Task<string> ProxySeesAsync()
        {
            using (Accounts.ActingUnder(withoutGrant))
            {
                return (await Accounts.QueryAsync(App, without, "SELECT count(*)::text FROM ehr.portal_refill_requests(100)")).Single();
            }
        }

        Assert.Equal("1", await ProxySeesAsync());
        await RunAsync(clinician, "UPDATE ehr.medication_statements SET sensitive = true WHERE id = @id", ("id", medication));

        Assert.Equal(["true"], await AsSuperuserAsync("SELECT sensitive::text FROM ehr.refill_requests WHERE id = @id", ("id", request)));
        Assert.Equal("0", await ProxySeesAsync());
        // Its earlier events, written before it was sensitive, are marked: an administrator sees no value of any.
        Assert.Equal(["0"], await AsSuperuserAsync(
            "SELECT count(*)::text FROM ehr.audit_events WHERE table_name = 'refill_requests' AND row_id = @id AND NOT sensitive", ("id", request)));

        await RunAsync(clinician, "UPDATE ehr.medication_statements SET sensitive = false WHERE id = @id", ("id", medication));
        Assert.Equal(["false"], await AsSuperuserAsync("SELECT sensitive::text FROM ehr.refill_requests WHERE id = @id", ("id", request)));
        Assert.Equal("1", await ProxySeesAsync());
    }

    // A request asked for while the medication is marked sensitive ends with the mark that stands, whichever came first:
    // both take the medication's lock. Two connections.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_request_asked_while_its_medication_is_marked_ends_sensitive(bool askedFirst)
    {
        var (clinician, patient, account) = await OnPanelAsync();
        var medication = await MedicationAsync(patient);
        var request = Guid.CreateVersion7();

        var (asking, askingTransaction) = await BeginAsync(App, account);
        var (marking, markingTransaction) = await BeginAsync(App, clinician);
        await using (asking)
        await using (marking)
        {
            await using var ask = new NpgsqlCommand(Ask, asking, askingTransaction);
            foreach (var (name, value) in new (string, object)[] { ("id", request), ("patient", patient), ("medication", medication), ("note", DBNull.Value), ("sensitive", false) })
            {
                ask.Parameters.AddWithValue(name, value);
            }

            await using var mark = new NpgsqlCommand("UPDATE ehr.medication_statements SET sensitive = true WHERE id = @id", marking, markingTransaction);
            mark.Parameters.AddWithValue("id", medication);
            var (first, firstTransaction, second, secondTransaction) = askedFirst
                ? (ask, askingTransaction, mark, markingTransaction)
                : (mark, markingTransaction, ask, askingTransaction);

            Assert.Equal(1, await first.ExecuteNonQueryAsync(Cancel));
            var waiting = second.ExecuteNonQueryAsync(Cancel);
            Assert.NotSame(waiting, await Task.WhenAny(waiting, Task.Delay(TimeSpan.FromMilliseconds(500), Cancel)));
            await firstTransaction.CommitAsync(Cancel);
            Assert.Equal(1, await waiting);
            await secondTransaction.CommitAsync(Cancel);
        }

        Assert.Equal(["true"], await AsSuperuserAsync("SELECT sensitive::text FROM ehr.refill_requests WHERE id = @id", ("id", request)));
    }

    [Fact]
    public async Task An_administrator_never_sees_a_requests_values_in_the_audit_log()
    {
        var (clinician, patient, account) = await OnPanelAsync();
        var admin = await Accounts.StaffAsync(database, StaffRoles.Admin);
        var request = await AskAsync(account, patient, await MedicationAsync(patient), "Synthetic note for the care team.");
        await DeclineAsync(clinician, request);
        var events = (await AsSuperuserAsync("SELECT id::text FROM ehr.audit_events WHERE table_name = 'refill_requests' AND row_id = @id", ("id", request)))
            .Select(Guid.Parse).ToArray();

        Assert.Equal(2, events.Length);
        Assert.Equal(["0"], await Accounts.QueryAsync(App, admin, "SELECT count(*)::text FROM ehr.shown_audit_details(@ids)", ("ids", events)));
    }

    [Fact]
    public async Task A_medication_not_coded_as_a_product_the_clinic_orders_is_not_renewed()
    {
        var (_, patient, account) = await OnPanelAsync();

        // Coded as its ingredient, and as a branded product the catalog doesn't hold.
        foreach (var (code, display) in new[] { ("29046", "lisinopril"), ("104377", "lisinopril 10 MG Oral Tablet [Zestril]") })
        {
            var medication = await MedicationAsync(patient, code: code, display: display);
            await RefusedAsync(PostgresErrorCodes.CheckViolation, () => AskAsync(account, patient, medication), "refill_requests_medication_coded");
        }

        Assert.Equal(["0"], await AsSuperuserAsync("SELECT count(*)::text FROM ehr.refill_requests WHERE patient_id = @patient", ("patient", patient)));
    }

    // The review's finding: a request asked while one care team was on the panel waited for good once they had all left and
    // someone else joined. Items follow the panel: a clinician who joins gets the patient's waiting requests, one who leaves
    // has theirs done, and a request left with nobody on the panel goes to whoever joins next.
    [Fact]
    public async Task A_waiting_request_follows_the_care_team_when_it_changes()
    {
        var (first, patient, account) = await OnPanelAsync();
        var second = await Accounts.StaffAsync(database, StaffRoles.Clinician);
        await Accounts.AssignAsync(database, second, patient);
        var request = await AskAsync(account, patient, await MedicationAsync(patient));
        Task<List<string>> OpenAsync(Guid clinician) => AsSuperuserAsync(
            "SELECT source_id::text FROM ehr.inbox_items WHERE recipient_id = @to AND kind = 'refill-request' AND done_at IS NULL", ("to", clinician));
        Task LeaveAsync(Guid clinician) => RunAsync(clinician,
            "UPDATE ehr.patient_assignments SET ended_at = now() WHERE patient_id = @patient AND clinician_id = @clinician AND ended_at IS NULL",
            ("patient", patient), ("clinician", clinician));

        // Both leave: their items are done, and the request still waits, with nobody to answer it.
        await LeaveAsync(first);
        await LeaveAsync(second);
        Assert.Empty(await OpenAsync(first));
        Assert.Empty(await OpenAsync(second));
        Assert.Equal(["requested"], await AsSuperuserAsync("SELECT status FROM ehr.refill_requests WHERE id = @id", ("id", request)));
        // Off the panel, neither can answer it.
        Assert.Equal(0, await DeclineAsync(first, request));

        // A third joins: the request is theirs to answer, and they answer it.
        var third = await Accounts.StaffAsync(database, StaffRoles.Clinician);
        await Accounts.AssignAsync(database, third, patient);
        Assert.Equal([request.ToString()], await OpenAsync(third));
        Assert.Equal(1, await DeclineAsync(third, request));
        Assert.Empty(await OpenAsync(third));

        // Rejoining: an item for each request still waiting, once, and none for an answered one.
        var later = await AskAsync(account, patient, await MedicationAsync(patient));
        await LeaveAsync(third);
        Assert.Empty(await OpenAsync(third));
        await Accounts.AssignAsync(database, third, patient);
        await Accounts.AssignAsync(database, first, patient);
        Assert.Equal([later.ToString()], await OpenAsync(third));
        Assert.Equal([later.ToString()], await OpenAsync(first));
        // An item held already isn't given twice: one of the third's, open, before they join again.
        await LeaveAsync(third);
        await Accounts.ExecuteAsync(database.SuperuserConnectionString, null,
            "INSERT INTO ehr.inbox_items (id, recipient_id, kind, patient_id, source_table, source_id, summary, created_at) VALUES (gen_random_uuid(), @to, 'refill-request', @patient, 'refill_requests', @id, 'A patient asked to renew a medication', now())",
            ("to", third), ("patient", patient), ("id", later));
        await Accounts.AssignAsync(database, third, patient);
        Assert.Equal([later.ToString()], await OpenAsync(third));
    }

    // A clinician locked out for a while after failed sign-ins is on the care team still; one an admin disabled (locked
    // out for good) is not, as for appointments (80-appointments.sql).
    [Fact]
    public async Task A_clinician_locked_for_a_while_is_told_and_a_disabled_one_is_not()
    {
        var (locked, patient, account) = await OnPanelAsync();
        await Accounts.ExecuteAsync(database.SuperuserConnectionString, null,
            "UPDATE ehr.staff_users SET lockout_end = now() + interval '15 minutes' WHERE id = @id", ("id", locked));
        var request = await AskAsync(account, patient, await MedicationAsync(patient));
        Assert.Equal([$"{locked} refill-request A patient asked to renew a medication true"], await ItemsAsync(request));

        var joining = await Accounts.StaffAsync(database, StaffRoles.Clinician);
        var disabled = await Accounts.StaffAsync(database, StaffRoles.Clinician);
        await Accounts.AssignAsync(database, joining, patient);
        await Accounts.AssignAsync(database, disabled, patient);
        await Accounts.ExecuteAsync(database.SuperuserConnectionString, null,
            "UPDATE ehr.staff_users SET lockout_end = now() + interval '15 minutes' WHERE id = @id", ("id", joining));
        await Accounts.LockOutAsync(database, disabled);
        var asked = await AskAsync(account, patient, await MedicationAsync(patient));
        Assert.Equal([.. new[] { locked, joining }.Select(id => id.ToString()).Order(StringComparer.Ordinal)
            .Select(id => $"{id} refill-request A patient asked to renew a medication true")], await ItemsAsync(asked));

        // A patient whose only clinician is disabled has no care team.
        var (alone, lonely, lonelyAccount) = await OnPanelAsync();
        await Accounts.LockOutAsync(database, alone);
        await RefusedAsync(PostgresErrorCodes.CheckViolation, async () => await AskAsync(lonelyAccount, lonely, await MedicationAsync(lonely)), "refill_requests_care_team");
    }
}
