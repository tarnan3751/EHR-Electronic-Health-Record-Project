using System.Globalization;
using Ehr.Data.Accounts;
using Ehr.Testing;
using Xunit;

namespace Ehr.Security.Tests;

// The expansion plan's step 13c: what a portal session may learn from the audit log about who viewed the record it is
// looking at, and nothing more, as the runtime roles, in SQL of its own (db/policies/42-record-views.sql). The log is
// still the admins' alone to read (30-rls.sql); ehr.record_views is the one door, for the session's own record only:
// the reads and the lab's hand-overs of the last 12 months, by who, when and which part, emergency access marked, never
// an ID, a table name, a reason or a write. The patient sees every view; a proxy only staff members', the lab's and
// their own: every staff member's, emergency access marked, when the patient allowed them sensitive items, and otherwise
// only those made while the patient was on the staff member's panel, without sensitive views, note reads or a part the
// rule doesn't name; and without conversation reads unless they may read messages. A page holds whole moments and is the
// same whether or not there are views a proxy is not shown.
public class RecordViewSecurityTests(MigratedDatabase database) : IClassFixture<MigratedDatabase>
{
    static CancellationToken Cancel => TestContext.Current.CancellationToken;

    string App => database.ConnectionStringFor("ehr_app", MigratedDatabase.AppPassword);

    string Read => database.ConnectionStringFor("ehr_read", MigratedDatabase.ReadPassword);

    // Every line of a page, as "who|name|role|part|emergency|more", in the function's order.
    const string Lines = """
        SELECT who || '|' || coalesce(name, '') || '|' || coalesce(role, '') || '|' || part || '|' || emergency || '|' || more
        FROM ehr.record_views(@older::timestamptz, @newer::timestamptz)
        """;

    // Every line of a page with its moment, to the microsecond, in UTC.
    const string Timed = """
        SELECT to_char(occurred_at AT TIME ZONE 'UTC', 'YYYY-MM-DD"T"HH24:MI:SS.US"Z"') || '|' || who || '|' || part || '|' || more
        FROM ehr.record_views(@older::timestamptz, @newer::timestamptz)
        """;

    Task<List<string>> AsSuperuserAsync(string sql, params (string Name, object Value)[] parameters) =>
        Accounts.QueryAsync(database.SuperuserConnectionString, null, sql, parameters);

    async Task<List<string>> ViewsAsync(string connection, Guid? user, Guid? grant, DateTimeOffset? older = null, DateTimeOffset? newer = null, string sql = Lines)
    {
        using (grant is { } acting ? Accounts.ActingUnder(acting) : null)
        {
            return await Accounts.QueryAsync(connection, user, sql, ("older", (object?)older ?? DBNull.Value), ("newer", (object?)newer ?? DBNull.Value));
        }
    }

    // A staff account with a name of its own.
    async Task<Guid> StaffAsync(string name, params string[] roles)
    {
        var id = await Accounts.StaffAsync(database, roles);
        await Accounts.ExecuteAsync(database.SuperuserConnectionString, null, "UPDATE ehr.staff_users SET display_name = @name WHERE id = @id", ("name", name), ("id", id));
        return id;
    }

    // An event of the log, as the PHI reader, the triggers or the lab's door would have written it, so many minutes ago.
    Task EventAsync(Guid patient, Guid? actor, string action, string? table, double minutesAgo, string source = "onprem", bool sensitive = false, string? reason = null) =>
        Accounts.ExecuteAsync(database.SuperuserConnectionString, null,
            """
            INSERT INTO ehr.audit_events (id, occurred_at, actor_id, action, table_name, row_id, patient_id, source, sensitive, reason)
            VALUES (uuidv7(), now() - make_interval(secs => @minutes * 60), @actor, @action, @table, gen_random_uuid(), @patient, @source, @sensitive, @reason)
            """,
            ("patient", patient), ("actor", (object?)actor ?? DBNull.Value), ("action", action), ("table", (object?)table ?? DBNull.Value), ("minutes", minutesAgo),
            ("source", source), ("sensitive", sensitive), ("reason", (object?)reason ?? DBNull.Value));

    // A read of the patient's row at an exact moment.
    Task EventAtAsync(Guid patient, Guid actor, DateTimeOffset at) =>
        Accounts.ExecuteAsync(database.SuperuserConnectionString, null,
            """
            INSERT INTO ehr.audit_events (id, occurred_at, actor_id, action, table_name, row_id, patient_id, source, sensitive)
            VALUES (uuidv7(), @at, @actor, 'read', 'patients', @patient, @patient, 'onprem', false)
            """, ("patient", patient), ("actor", actor), ("at", at));

    // The patient on the clinician's panel from so many minutes ago, until so many minutes ago or still.
    Task PanelAsync(Guid clinician, Guid patient, double fromMinutesAgo, double? untilMinutesAgo = null) =>
        Accounts.ExecuteAsync(database.SuperuserConnectionString, null,
            """
            INSERT INTO ehr.patient_assignments (id, patient_id, clinician_id, assigned_at, ended_at)
            VALUES (gen_random_uuid(), @patient, @clinician, now() - make_interval(secs => @from * 60),
                    now() - make_interval(secs => @until::float8 * 60))
            """, ("patient", patient), ("clinician", clinician), ("from", fromMinutesAgo), ("until", (object?)untilMinutesAgo ?? DBNull.Value));

    // An emergency-access window of the clinician's for the patient, from and until so many minutes ago (negative: ahead).
    Task WindowAsync(Guid clinician, Guid patient, double fromMinutesAgo, double untilMinutesAgo) =>
        Accounts.ExecuteAsync(database.SuperuserConnectionString, null,
            """
            INSERT INTO ehr.break_glass_accesses (id, patient_id, clinician_id, reason, granted_at, expires_at)
            VALUES (gen_random_uuid(), @patient, @clinician, 'Synthetic reason: brought in unconscious.',
                    now() - make_interval(secs => @from * 60), now() - make_interval(secs => @until * 60))
            """, ("patient", patient), ("clinician", clinician), ("from", fromMinutesAgo), ("until", untilMinutesAgo));

    // A proxy for the patient with a portal account of their own, given access by the patient. The account, its
    // address and the grant.
    async Task<(Guid Account, string Email, Guid Grant)> ProxyAsync(Guid patientAccount, Guid patient, bool includeSensitive, string scope = "record-and-messages")
    {
        var (account, email) = await Accounts.ProxyOnlyAccountAsync(database);
        var grant = Guid.CreateVersion7();
        await Accounts.ExecuteAsync(App, patientAccount,
            """
            INSERT INTO ehr.proxy_grants (id, patient_id, proxy_email, scope, include_sensitive, expires_at, granted_by, granted_at, granted_by_kind)
            VALUES (@id, @patient, @email, @scope, @sensitive, now() + interval '30 days', gen_random_uuid(), '2000-01-01', 'patient')
            """, ("id", grant), ("patient", patient), ("email", email), ("scope", scope), ("sensitive", includeSensitive));
        return (account, email, grant);
    }

    // A patient with a portal account of their own, and the clinician who added them, who has had them on their panel
    // for a year and more.
    async Task<(Guid Clinician, Guid Patient, Guid Account)> PatientAsync()
    {
        var clinician = await StaffAsync("Dr. Alder", StaffRoles.Clinician);
        var patient = await Accounts.PatientAsync(database, clinician);
        await PanelAsync(clinician, patient, fromMinutesAgo: 60 * 24 * 370);
        return (clinician, patient, await Accounts.PatientAccountAsync(database, patient));
    }

    [Fact]
    public async Task The_patient_is_shown_every_view_of_their_record_and_nothing_else()
    {
        var (clinician, patient, own) = await PatientAsync();
        var admin = await StaffAsync("Avery Admin", StaffRoles.Admin);
        var (allowed, allowedEmail, _) = await ProxyAsync(own, patient, includeSensitive: true);
        var (without, withoutEmail, _) = await ProxyAsync(own, patient, includeSensitive: false);
        var (_, otherPatient, otherAccount) = await PatientAsync();

        // Shown, newest first: each read, by whom, of which part.
        await EventAsync(patient, clinician, "read", "patients", 1);
        await EventAsync(patient, clinician, "read_sensitive", "patients", 2);
        await EventAsync(patient, clinician, "read", "clinical_notes", 3);
        await EventAsync(patient, clinician, "read", "clinical_drafts", 4);
        await EventAsync(patient, clinician, "read", "ai_note_reviews", 5);
        await EventAsync(patient, clinician, "read", "message_threads", 6);
        await EventAsync(patient, own, "read", "visit_summaries", 7);
        await EventAsync(patient, own, "read", "audit_events", 8);
        await EventAsync(patient, allowed, "read", "patients", 9);
        await EventAsync(patient, without, "read", "patients", 10);
        await EventAsync(patient, null, "sent_to_lab", "orders", 11, source: "lab");
        await EventAsync(patient, admin, "read", "patients", 12);
        // Step 13b's downloads, whichever lane lands first.
        await EventAsync(patient, clinician, "download", "patients", 13);
        await EventAsync(patient, own, "download_sensitive", "patients", 14);
        // A read of a note marked sensitive since; a read of a table the part rule doesn't name: the record, to the patient.
        await EventAsync(patient, clinician, "read", "clinical_notes", 15, sensitive: true);
        await EventAsync(patient, clinician, "read", "made_up_table", 16);
        // Never shown: a write, a sign-in, the emergency-access reason, account and system events, a hand-over in any
        // other shape, a read older than 12 months, another patient's.
        await EventAsync(patient, clinician, "insert", "conditions", 20);
        await EventAsync(patient, clinician, "update", "patients", 21);
        await EventAsync(patient, own, "sign_in", null, 22);
        await EventAsync(patient, clinician, "break_glass", "break_glass_accesses", 23, reason: "Synthetic reason: seen in the emergency department.");
        await EventAsync(patient, null, "read", "patients", 24);
        await EventAsync(patient, clinician, "ai_review_staged", "ai_note_reviews", 25);
        await EventAsync(patient, clinician, "sent_to_lab", "orders", 26, source: "lab");
        await EventAsync(patient, null, "sent_to_lab", "orders", 27);
        await EventAsync(patient, null, "enrollment_issued", null, 28);
        await EventAsync(patient, clinician, "read", "patients", 60 * 24 * 400);
        await EventAsync(otherPatient, clinician, "read", "patients", 30);
        await EventAsync(otherPatient, otherAccount, "read", "patients", 31);

        string[] expected =
        [
            "staff|Dr. Alder|clinician|record|false|false",
            "staff|Dr. Alder|clinician|record-sensitive|false|false",
            "staff|Dr. Alder|clinician|note|false|false",
            "staff|Dr. Alder|clinician|note|false|false",
            "staff|Dr. Alder|clinician|note|false|false",
            "staff|Dr. Alder|clinician|messages|false|false",
            "you|||summary|false|false",
            "you|||views|false|false",
            $"proxy|{allowedEmail}||record|false|false",
            $"proxy|{withoutEmail}||record|false|false",
            "lab|||lab-order|false|false",
            "staff|Avery Admin|administrator|record|false|false",
            "staff|Dr. Alder|clinician|download|false|false",
            "you|||download-sensitive|false|false",
            "staff|Dr. Alder|clinician|note|false|false",
            "staff|Dr. Alder|clinician|record|false|false",
        ];
        foreach (var connection in new[] { App, Read })
        {
            Assert.Equal(expected, await ViewsAsync(connection, own, null));
        }

        // The other patient sees their own two views, none of these.
        Assert.Equal(["staff|Dr. Alder|clinician|record|false|false", "you|||record|false|false"], await ViewsAsync(App, otherAccount, null));
    }

    [Fact]
    public async Task A_proxy_is_shown_staff_views_the_labs_and_their_own_as_their_access_allows()
    {
        var (clinician, patient, own) = await PatientAsync();
        var full = await ProxyAsync(own, patient, includeSensitive: true);
        var plain = await ProxyAsync(own, patient, includeSensitive: false);
        var recordOnly = await ProxyAsync(own, patient, includeSensitive: true, scope: "record");
        var revoked = await ProxyAsync(own, patient, includeSensitive: true);
        await Accounts.ExecuteAsync(App, own, "UPDATE ehr.proxy_grants SET revoked_at = now() WHERE id = @id", ("id", revoked.Grant));
        var expired = await ProxyAsync(own, patient, includeSensitive: true);
        await Accounts.ExecuteAsync(database.SuperuserConnectionString, null, "UPDATE ehr.proxy_grants SET expires_at = now() - interval '1 second' WHERE id = @id", ("id", expired.Grant));
        var (_, _, otherAccount) = await PatientAsync();
        var outsider = await StaffAsync("Dr. Birch", StaffRoles.Clinician);
        await Accounts.AssignAsync(database, outsider, patient);
        var admin = await StaffAsync("Avery Admin", StaffRoles.Admin);

        await EventAsync(patient, clinician, "read", "patients", 1);
        await EventAsync(patient, clinician, "read_sensitive", "patients", 2);
        await EventAsync(patient, clinician, "read", "clinical_notes", 3);
        await EventAsync(patient, clinician, "read", "message_threads", 4);
        await EventAsync(patient, clinician, "read", "visit_summaries", 5, sensitive: true);
        await EventAsync(patient, clinician, "download_sensitive", "patients", 6);
        await EventAsync(patient, null, "sent_to_lab", "orders", 7, source: "lab");
        await EventAsync(patient, own, "read", "patients", 8);
        foreach (var proxy in new[] { full, plain, recordOnly })
        {
            await EventAsync(patient, proxy.Account, "read", "patients", 9);
        }

        await EventAsync(patient, full.Account, "read", "audit_events", 10);
        // From outside the care team: Dr. Birch before the patient was on their panel; Dr. Cedar through emergency access.
        await EventAsync(patient, outsider, "read", "patients", 11);
        var emergency = await StaffAsync("Dr. Cedar", StaffRoles.Clinician);
        await WindowAsync(emergency, patient, fromMinutesAgo: 60, untilMinutesAgo: -60);
        await EventAsync(patient, emergency, "read", "patients", 12);
        // An addendum, a message, and a table the part rule doesn't name.
        await EventAsync(patient, clinician, "read", "note_addenda", 13);
        await EventAsync(patient, clinician, "read", "messages", 14);
        await EventAsync(patient, clinician, "read", "made_up_table", 15);

        const string Mine = "you|||record|false|false";
        string[] staff = ["staff|Dr. Alder|clinician|record|false|false"];
        string[] sensitive = ["staff|Dr. Alder|clinician|record-sensitive|false|false", "staff|Dr. Alder|clinician|note|false|false"];
        string[] messages = ["staff|Dr. Alder|clinician|messages|false|false"];
        string[] markedAndDownloaded = ["staff|Dr. Alder|clinician|summary|false|false", "staff|Dr. Alder|clinician|download-sensitive|false|false"];
        string[] lab = ["lab|||lab-order|false|false"];
        string[] outside = ["staff|Dr. Birch|clinician|record|false|false", "staff|Dr. Cedar|clinician|record|true|false"];
        const string Addendum = "staff|Dr. Alder|clinician|note|false|false";
        const string Unnamed = "staff|Dr. Alder|clinician|record|false|false";
        foreach (var connection in new[] { App, Read })
        {
            Assert.Equal([.. staff, .. sensitive, .. messages, .. markedAndDownloaded, .. lab, Mine, "you|||views|false|false", .. outside, Addendum, .. messages, Unnamed],
                await ViewsAsync(connection, full.Account, full.Grant));
            // No sensitive items: no line for a view of them, nor for a note or an addendum, nor for anything marked
            // sensitive, nor for a staff member who didn't have the patient on their panel then (emergency access
            // included), nor for a part the rule doesn't name.
            Assert.Equal([.. staff, .. messages, .. lab, Mine, .. messages], await ViewsAsync(connection, plain.Account, plain.Grant));
            // No messages: no line for a conversation or a message.
            Assert.Equal([.. staff, .. sensitive, .. markedAndDownloaded, .. lab, Mine, .. outside, Addendum, Unnamed],
                await ViewsAsync(connection, recordOnly.Account, recordOnly.Grant));
            // Nothing at all for access that ended, a grant named by someone it isn't addressed to, another patient
            // acting under it, a proxy-only account on its own, a clinician (even with the patient on their panel), an
            // admin, or nobody.
            foreach (var (user, grant) in new (Guid?, Guid?)[]
            {
                (revoked.Account, revoked.Grant), (expired.Account, expired.Grant), (plain.Account, full.Grant), (otherAccount, full.Grant),
                (full.Account, null), (clinician, null), (outsider, null), (admin, null), (null, null), (null, full.Grant),
            })
            {
                Assert.Empty(await ViewsAsync(connection, user, grant));
            }
        }

        // The other patient's own page has nothing of this record.
        Assert.Empty(await ViewsAsync(App, otherAccount, null));
    }

    [Fact]
    public async Task A_staff_read_is_marked_as_emergency_access_only_inside_a_window_and_off_the_panel()
    {
        var (_, patient, own) = await PatientAsync();
        var away = await StaffAsync("Dr. Cedar", StaffRoles.Clinician);
        var onPanel = await StaffAsync("Dr. Dogwood", StaffRoles.Clinician);
        var joined = await StaffAsync("Dr. Elm", StaffRoles.Clinician);
        const string Reason = "Synthetic reason: brought in unconscious.";

        // A window from two hours ago to two hours ahead for each; Dr. Dogwood has had the patient on their panel all
        // along; Dr. Elm added them 30 minutes ago, and had them on the panel once before, a day ago, for an hour.
        foreach (var clinician in new[] { away, onPanel, joined })
        {
            await Accounts.ExecuteAsync(database.SuperuserConnectionString, null,
                "INSERT INTO ehr.break_glass_accesses (id, patient_id, clinician_id, reason, granted_at, expires_at) VALUES (gen_random_uuid(), @patient, @clinician, @reason, now() - interval '2 hours', now() + interval '2 hours')",
                ("patient", patient), ("clinician", clinician), ("reason", Reason));
            await EventAsync(patient, clinician, "break_glass", "break_glass_accesses", 120, reason: Reason);
        }

        await Accounts.ExecuteAsync(database.SuperuserConnectionString, null,
            """
            INSERT INTO ehr.patient_assignments (id, patient_id, clinician_id, assigned_at, ended_at) VALUES
                (gen_random_uuid(), @patient, @onPanel, now() - interval '1 day', NULL),
                (gen_random_uuid(), @patient, @joined, now() - interval '1 day', now() - interval '23 hours'),
                (gen_random_uuid(), @patient, @joined, now() - interval '30 minutes', NULL)
            """, ("patient", patient), ("onPanel", onPanel), ("joined", joined));

        await EventAsync(patient, away, "read", "patients", 10);
        await EventAsync(patient, away, "read", "clinical_notes", 11);
        // Before the window: no emergency access then.
        await EventAsync(patient, away, "read", "patients", 180);
        await EventAsync(patient, onPanel, "read", "patients", 12);
        // Dr. Elm: on the panel now, not an hour ago.
        await EventAsync(patient, joined, "read", "patients", 13);
        await EventAsync(patient, joined, "read", "patients", 60);
        // The patient's own read is never marked.
        await EventAsync(patient, own, "read", "patients", 14);
        // Dr. Fir has no window of their own, and reads inside the others'; Dr. Gum's window is for another patient.
        var noWindow = await StaffAsync("Dr. Fir", StaffRoles.Clinician);
        await EventAsync(patient, noWindow, "read", "patients", 15);
        var elsewhere = await StaffAsync("Dr. Gum", StaffRoles.Clinician);
        await WindowAsync(elsewhere, await Accounts.PatientAsync(database, elsewhere), fromMinutesAgo: 120, untilMinutesAgo: -120);
        await EventAsync(patient, elsewhere, "read", "patients", 16);
        // Dr. Hazel's window ends at a moment: a read a microsecond before it is inside, a read at it is not.
        var edge = await StaffAsync("Dr. Hazel", StaffRoles.Clinician);
        var ends = DateTimeOffset.UtcNow.AddMinutes(-40);
        ends = ends.AddTicks(-(ends.UtcTicks % 10));
        await Accounts.ExecuteAsync(database.SuperuserConnectionString, null,
            "INSERT INTO ehr.break_glass_accesses (id, patient_id, clinician_id, reason, granted_at, expires_at) VALUES (gen_random_uuid(), @patient, @clinician, @reason, @from, @until)",
            ("patient", patient), ("clinician", edge), ("reason", Reason), ("from", ends.AddHours(-2)), ("until", ends));
        await EventAtAsync(patient, edge, ends.AddTicks(-10));
        await EventAtAsync(patient, edge, ends);
        // Dr. Elm again, inside a window of a day ago: while the patient was on their panel the first time, and after.
        await WindowAsync(joined, patient, fromMinutesAgo: 25 * 60, untilMinutesAgo: 21 * 60);
        await EventAsync(patient, joined, "read", "patients", 23.5 * 60);
        await EventAsync(patient, joined, "read", "patients", 22 * 60);

        Assert.Equal(
            [
                "staff|Dr. Cedar|clinician|record|true|false",
                "staff|Dr. Cedar|clinician|note|true|false",
                "staff|Dr. Dogwood|clinician|record|false|false",
                "staff|Dr. Elm|clinician|record|false|false",
                "you|||record|false|false",
                "staff|Dr. Fir|clinician|record|false|false",
                "staff|Dr. Gum|clinician|record|false|false",
                "staff|Dr. Hazel|clinician|record|false|false",
                "staff|Dr. Hazel|clinician|record|true|false",
                "staff|Dr. Elm|clinician|record|true|false",
                "staff|Dr. Cedar|clinician|record|false|false",
                "staff|Dr. Elm|clinician|record|true|false",
                "staff|Dr. Elm|clinician|record|false|false",
            ], await ViewsAsync(App, own, null));
        // The reason is the clinic's: no line holds it, in any column.
        var whole = await ViewsAsync(App, own, null, sql: "SELECT r::text FROM ehr.record_views(@older::timestamptz, @newer::timestamptz) r");
        Assert.Equal(13, whole.Count);
        Assert.DoesNotContain(whole, line => line.Contains("unconscious", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_staff_member_is_named_as_their_account_is_now_even_when_it_is_disabled()
    {
        var (clinician, patient, own) = await PatientAsync();
        await EventAsync(patient, clinician, "read", "patients", 5);
        await Accounts.ExecuteAsync(database.SuperuserConnectionString, null, "UPDATE ehr.staff_users SET display_name = 'Dr. Alder-Fir' WHERE id = @id", ("id", clinician));
        await Accounts.LockOutAsync(database, clinician);

        Assert.Equal(["staff|Dr. Alder-Fir|clinician|record|false|false"], await ViewsAsync(App, own, null));
    }

    [Fact]
    public async Task Pages_hold_whole_moments_reach_every_view_once_and_answer_every_argument()
    {
        var (clinician, patient, own) = await PatientAsync();
        // 120 views a minute apart, and three more at one moment, 49.5 minutes ago: the newest page's 50th, 51st and
        // 52nd lines.
        for (var minute = 1; minute <= 120; minute++)
        {
            await EventAsync(patient, clinician, "read", "patients", minute);
        }

        await Accounts.ExecuteAsync(database.SuperuserConnectionString, null,
            """
            INSERT INTO ehr.audit_events (id, occurred_at, actor_id, action, table_name, row_id, patient_id, source, sensitive)
            SELECT uuidv7(), m.at, @actor, 'read', 'patients', @patient, @patient, 'onprem', false
            FROM (SELECT now() - interval '49.5 minutes' AS at) m, generate_series(1, 3)
            """, ("actor", clinician), ("patient", patient));

        // Older, from the newest, to the end; then newer, from the oldest, back to the start.
        var older = await PagesAsync(own, newest: true);
        var newer = await PagesAsync(own, newest: false);
        foreach (var pages in new[] { older, newer })
        {
            var times = pages.SelectMany(page => page).Select(line => line.Split('|')[0]).ToList();
            Assert.Equal(123, times.Count);
            // Every moment on one page only: the three at one moment together.
            Assert.All(pages.SelectMany((page, index) => page.Select(line => (Time: line.Split('|')[0], index))).GroupBy(line => line.Time),
                moment => Assert.Single(moment.Select(line => line.index).Distinct()));
            Assert.Contains(pages, page => page.Count(line => line.StartsWith(Moment(pages, 3), StringComparison.Ordinal)) == 3);
        }

        Assert.Equal(older.SelectMany(page => page).Select(line => line.Split('|')[0]), newer.AsEnumerable().Reverse().SelectMany(page => page).Select(line => line.Split('|')[0]));
        // The newest page stops before the three, which can't all fit: 49 lines, and more to come.
        Assert.Equal(49, older[0].Count);
        Assert.All(older[0], line => Assert.EndsWith("|true", line, StringComparison.Ordinal));
        Assert.All(older[^1], line => Assert.EndsWith("|false", line, StringComparison.Ordinal));

        var newest = await ViewsAsync(App, own, null, sql: Timed);
        // Both moments given: older wins.
        Assert.Equal(await ViewsAsync(App, own, null, older: Time(newest[^1]), sql: Timed),
            await ViewsAsync(App, own, null, older: Time(newest[^1]), newer: Time(newest[0]), sql: Timed));
        // Older than the end of time is the newest page; newer than its start, the oldest.
        Assert.Equal(newest, await ViewsAsync(App, own, null, older: DateTimeOffset.MaxValue.ToUniversalTime(), sql: Timed));
        Assert.Equal(newer[0], await ViewsAsync(App, own, null, newer: DateTimeOffset.MinValue.ToUniversalTime(), sql: Timed));
        // Nothing before the window, nor after now.
        Assert.Empty(await ViewsAsync(App, own, null, older: DateTimeOffset.UtcNow.AddMonths(-13)));
        Assert.Empty(await ViewsAsync(App, own, null, newer: DateTimeOffset.UtcNow.AddMinutes(5)));
        // Infinity either way, as PostgreSQL spells it.
        Assert.Equal(newest, await AsInfinityAsync(own, "older", "infinity"));
        Assert.Equal(newer[0], await AsInfinityAsync(own, "newer", "-infinity"));
        Assert.Empty(await AsInfinityAsync(own, "older", "-infinity"));
        Assert.Empty(await AsInfinityAsync(own, "newer", "infinity"));
    }

    // The moment of the first line on any page that has that many lines at it.
    static string Moment(List<List<string>> pages, int count) =>
        pages.SelectMany(page => page).GroupBy(line => line.Split('|')[0]).First(moment => moment.Count() == count).Key;

    static DateTimeOffset Time(string line) =>
        DateTimeOffset.Parse(line.Split('|')[0], CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal);

    // The page for one moment given as PostgreSQL spells infinity, the other moment not given.
    Task<List<string>> AsInfinityAsync(Guid user, string argument, string value) =>
        Accounts.QueryAsync(App, user, Timed.Replace($"@{argument}::timestamptz", $"'{value}'::timestamptz", StringComparison.Ordinal),
            (argument == "older" ? "newer" : "older", DBNull.Value));

    // Every page of a record's views, by "Older" from the newest or by "Newer" from the oldest, with their moments, as
    // the portal would follow them; each page in the function's order, newest first.
    async Task<List<List<string>>> PagesAsync(Guid user, bool newest, Guid? grant = null)
    {
        var pages = new List<List<string>>();
        DateTimeOffset? older = null;
        DateTimeOffset? newer = newest ? null : DateTimeOffset.UtcNow.AddMonths(-13);
        while (true)
        {
            var page = await ViewsAsync(App, user, grant, older, newer, Timed);
            if (page.Count == 0)
            {
                return pages;
            }

            pages.Add(page);
            if (page[0].EndsWith("|false", StringComparison.Ordinal))
            {
                return pages;
            }

            older = newest ? Time(page[^1]) : null;
            newer = newest ? null : Time(page[0]);
            Assert.True(pages.Count < 10, "Paging didn't end.");
        }
    }

    [Fact]
    public async Task A_proxys_pages_are_the_same_whether_or_not_there_are_views_it_is_not_shown()
    {
        var (clinician, patient, own) = await PatientAsync();
        var proxy = await ProxyAsync(own, patient, includeSensitive: false, scope: "record");
        var other = await ProxyAsync(own, patient, includeSensitive: true);
        for (var minute = 1; minute <= 110; minute++)
        {
            await EventAsync(patient, minute % 10 == 0 ? proxy.Account : clinician, "read", "patients", minute);
        }

        var before = await PagesAsync(proxy.Account, newest: true, proxy.Grant);
        var beforeNewer = await PagesAsync(proxy.Account, newest: false, proxy.Grant);

        // Views this proxy is not shown, around and between theirs, at the same moments as some, and past the last
        // page: the patient's own, another proxy's, sensitive ones, note reads, addenda and conversations, writes, a
        // staff member who never had the patient on their panel, and one through emergency access.
        var offPanel = await StaffAsync("Dr. Ivy", StaffRoles.Clinician);
        var emergency = await StaffAsync("Dr. Juniper", StaffRoles.Clinician);
        await WindowAsync(emergency, patient, fromMinutesAgo: 60 * 24, untilMinutesAgo: -60);
        await Accounts.ExecuteAsync(database.SuperuserConnectionString, null,
            """
            INSERT INTO ehr.audit_events (id, occurred_at, actor_id, action, table_name, row_id, patient_id, source, sensitive)
            SELECT uuidv7(), now() - make_interval(secs => n * 13), k.actor, k.action, k.table_name, gen_random_uuid(), @patient, 'onprem', k.sensitive
            FROM generate_series(1, 700) AS n
            CROSS JOIN LATERAL (VALUES
                (@own, 'read', 'patients', false), (@other, 'read', 'patients', false), (@clinician, 'read_sensitive', 'patients', false),
                (@clinician, 'read', 'clinical_notes', false), (@clinician, 'read', 'note_addenda', false), (@clinician, 'read', 'message_threads', false),
                (@clinician, 'read', 'messages', false), (@clinician, 'read', 'visit_summaries', true), (@clinician, 'insert', 'conditions', false),
                (@offPanel, 'read', 'patients', false), (@emergency, 'read', 'patients', false)) AS k(actor, action, table_name, sensitive)
            WHERE n % 7 = (CASE k.action WHEN 'insert' THEN 6 ELSE 0 END) OR k.actor IN (@own, @offPanel, @emergency)
            """, ("patient", patient), ("own", own), ("other", other.Account), ("clinician", clinician), ("offPanel", offPanel), ("emergency", emergency));
        // And at each moment the proxy is shown a view: the patient's own, the off-panel staff member's, and one through
        // emergency access.
        await Accounts.ExecuteAsync(database.SuperuserConnectionString, null,
            """
            INSERT INTO ehr.audit_events (id, occurred_at, actor_id, action, table_name, row_id, patient_id, source, sensitive)
            SELECT uuidv7(), e.occurred_at, k.actor, 'read', 'patients', @patient, @patient, 'onprem', false
            FROM ehr.audit_events e CROSS JOIN (VALUES (@own), (@offPanel), (@emergency)) AS k(actor)
            WHERE e.patient_id = @patient AND e.actor_id IN (@clinician, @proxy)
            """, ("patient", patient), ("own", own), ("offPanel", offPanel), ("emergency", emergency), ("clinician", clinician), ("proxy", proxy.Account));
        Assert.True(int.Parse((await AsSuperuserAsync("SELECT count(*)::text FROM ehr.audit_events WHERE patient_id = @patient", ("patient", patient))).Single(),
            CultureInfo.InvariantCulture) > 3000);
        // The patient, and a proxy with sensitive items, are shown the emergency access marked.
        Assert.Contains(await ViewsAsync(App, own, null), line => line.StartsWith("staff|Dr. Juniper|clinician|record|true|", StringComparison.Ordinal));
        Assert.Contains(await ViewsAsync(App, other.Account, other.Grant), line => line.StartsWith("staff|Dr. Juniper|clinician|record|true|", StringComparison.Ordinal));

        Assert.Equal(before, await PagesAsync(proxy.Account, newest: true, proxy.Grant));
        Assert.Equal(beforeNewer, await PagesAsync(proxy.Account, newest: false, proxy.Grant));
        Assert.Equal(3, before.Count);
    }

    // The same for a proxy with sensitive items, who is shown every staff member's view, emergency access marked: what
    // they are not shown is the patient's own views and other proxies'. Only the who-can-see filter, before the cut,
    // keeps those from shaping their pages (the test above proves the filters for a proxy without sensitive items).
    [Fact]
    public async Task A_proxy_with_sensitive_items_pages_the_same_whether_or_not_the_patient_and_others_viewed()
    {
        var (clinician, patient, own) = await PatientAsync();
        var allowed = await ProxyAsync(own, patient, includeSensitive: true);
        var second = await ProxyAsync(own, patient, includeSensitive: false);
        var outside = await StaffAsync("Dr. Kapok", StaffRoles.Clinician);
        await WindowAsync(outside, patient, fromMinutesAgo: 60 * 24, untilMinutesAgo: -60);
        // What they are shown: the care team's, a view through emergency access, sensitive ones and notes, and their own.
        for (var minute = 1; minute <= 110; minute++)
        {
            var (actor, action, table) = (minute % 10) switch
            {
                0 => (allowed.Account, "read", "patients"),
                1 => (outside, "read", "patients"),
                2 => (clinician, "read_sensitive", "patients"),
                3 => (clinician, "read", "clinical_notes"),
                _ => (clinician, "read", "patients"),
            };
            await EventAsync(patient, actor, action, table, minute);
        }

        var before = await PagesAsync(allowed.Account, newest: true, allowed.Grant);
        var beforeNewer = await PagesAsync(allowed.Account, newest: false, allowed.Grant);
        Assert.Contains(before.SelectMany(page => page), line => line.Contains("|staff|", StringComparison.Ordinal));

        // The patient's own views and another proxy's, around and between theirs, and at each moment they are shown one.
        await Accounts.ExecuteAsync(database.SuperuserConnectionString, null,
            """
            INSERT INTO ehr.audit_events (id, occurred_at, actor_id, action, table_name, row_id, patient_id, source, sensitive)
            SELECT uuidv7(), now() - make_interval(secs => n * 13), k.actor, k.action, 'patients', @patient, @patient, 'onprem', false
            FROM generate_series(1, 700) AS n
            CROSS JOIN (VALUES (@own, 'read'), (@own, 'read_sensitive'), (@second, 'read'), (@own, 'download')) AS k(actor, action)
            """, ("patient", patient), ("own", own), ("second", second.Account));
        await Accounts.ExecuteAsync(database.SuperuserConnectionString, null,
            """
            INSERT INTO ehr.audit_events (id, occurred_at, actor_id, action, table_name, row_id, patient_id, source, sensitive)
            SELECT uuidv7(), e.occurred_at, k.actor, 'read', 'patients', @patient, @patient, 'onprem', false
            FROM ehr.audit_events e CROSS JOIN (VALUES (@own), (@second)) AS k(actor)
            WHERE e.patient_id = @patient AND e.actor_id IN (@clinician, @outside, @allowed)
            """, ("patient", patient), ("own", own), ("second", second.Account), ("clinician", clinician), ("outside", outside), ("allowed", allowed.Account));

        Assert.Equal(before, await PagesAsync(allowed.Account, newest: true, allowed.Grant));
        Assert.Equal(beforeNewer, await PagesAsync(allowed.Account, newest: false, allowed.Grant));
        Assert.Equal(3, before.Count);
    }

    // A proxy without sensitive items is shown a staff member's view from the moment the patient was put on their panel,
    // to the moment, not included, it ended: by the microsecond, for each of the staff member's assignments, which may be
    // ended and begun again, or overlap.
    [Fact]
    public async Task A_proxy_without_sensitive_items_is_shown_views_while_the_patient_was_on_the_panel_to_the_microsecond()
    {
        var (_, patient, own) = await PatientAsync();
        var proxy = await ProxyAsync(own, patient, includeSensitive: false);
        var clinician = await StaffAsync("Dr. Larch", StaffRoles.Clinician);
        var start = DateTimeOffset.UtcNow.AddHours(-10);
        start = start.AddTicks(-(start.UtcTicks % 10));
        var micro = TimeSpan.FromTicks(10);
        // On the panel from start to start + 1 hour; again from start + 2 hours to start + 4, overlapped by another
        // assignment from start + 3 hours, still open.
        foreach (var (from, until) in new (DateTimeOffset, DateTimeOffset?)[]
        {
            (start, start.AddHours(1)), (start.AddHours(2), start.AddHours(4)), (start.AddHours(3), null),
        })
        {
            await Accounts.ExecuteAsync(database.SuperuserConnectionString, null,
                "INSERT INTO ehr.patient_assignments (id, patient_id, clinician_id, assigned_at, ended_at) VALUES (gen_random_uuid(), @patient, @clinician, @from, @until)",
                ("patient", patient), ("clinician", clinician), ("from", from), ("until", (object?)until ?? DBNull.Value));
        }

        var shown = new[]
        {
            start, start + micro, start.AddHours(1) - micro,
            start.AddHours(2), start.AddHours(3.5), start.AddHours(4), start.AddHours(5),
        };
        var hidden = new[] { start - micro, start.AddHours(1), start.AddHours(1) + micro, start.AddHours(2) - micro };
        foreach (var at in shown.Concat(hidden))
        {
            await EventAtAsync(patient, clinician, at);
        }

        static string Stamp(DateTimeOffset at) => at.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.ffffffZ", CultureInfo.InvariantCulture);
        var times = (await ViewsAsync(App, proxy.Account, proxy.Grant, sql: Timed)).Select(line => line.Split('|')[0]).ToList();
        Assert.Equal(shown.OrderDescending().Select(Stamp), times);
        // The patient is shown all of them.
        Assert.Equal(shown.Length + hidden.Length, (await ViewsAsync(App, own, null)).Count);
    }

    // The panel history is read for every page of a proxy without sensitive items, whatever the page holds: so a page's
    // time doesn't tell such a proxy whether there are staff views they aren't shown. It isn't read for the patient's.
    [Fact]
    public async Task A_proxy_without_sensitive_items_reads_the_panel_history_whatever_the_page_holds()
    {
        var (_, empty, emptyOwn) = await PatientAsync();
        var emptyProxy = await ProxyAsync(emptyOwn, empty, includeSensitive: false);
        var (_, busy, busyOwn) = await PatientAsync();
        var busyProxy = await ProxyAsync(busyOwn, busy, includeSensitive: false);
        var outside = await StaffAsync("Dr. Maple", StaffRoles.Clinician);
        await WindowAsync(outside, busy, fromMinutesAgo: 60 * 24, untilMinutesAgo: -60);
        var offPanel = await StaffAsync("Dr. Nutmeg", StaffRoles.Clinician);
        for (var minute = 1; minute <= 40; minute++)
        {
            await EventAsync(busy, minute % 2 == 0 ? outside : offPanel, "read", "patients", minute);
        }

        foreach (var connection in new[] { App, Read })
        {
            // No staff view at all, or forty they aren't shown: the history is read once, either way.
            Assert.Equal("0 1", await PanelScansAsync(connection, emptyProxy.Account, emptyProxy.Grant));
            Assert.Equal("0 1", await PanelScansAsync(connection, busyProxy.Account, busyProxy.Grant));
            Assert.Equal("0 0", await PanelScansAsync(connection, emptyOwn, null));
        }
    }

    // A page's lines, and how many times the panels' table was scanned to make it: the scans the session's transaction
    // counts after the page, less those it counted before (a pooled session's counts carry over from earlier work).
    async Task<string> PanelScansAsync(string connectionString, Guid user, Guid? grant)
    {
        await using var connection = new Npgsql.NpgsqlConnection(connectionString);
        await connection.OpenAsync(Cancel);
        await using var transaction = await connection.BeginTransactionAsync(Cancel);
        async Task<long> ScalarAsync(string sql)
        {
            await using var command = new Npgsql.NpgsqlCommand(sql, connection, transaction);
            command.Parameters.AddWithValue("user", user.ToString());
            command.Parameters.AddWithValue("grant", grant?.ToString() ?? "");
            return Convert.ToInt64(await command.ExecuteScalarAsync(Cancel), CultureInfo.InvariantCulture);
        }

        await ScalarAsync("SELECT length(set_config('app.user_id', @user, true) || set_config('app.acting_grant', @grant, true))");
        const string Scans = "SELECT pg_stat_get_xact_numscans('ehr.patient_assignments'::regclass)";
        var before = await ScalarAsync(Scans);
        var lines = await ScalarAsync("SELECT count(*) FROM ehr.record_views(NULL, NULL)");
        return $"{lines} {await ScalarAsync(Scans) - before}";
    }

    // The panel's history decides what a proxy without sensitive items is shown, so the apps can't rewrite it: an
    // assignment starts now and open, and only ends, now, once (42-record-views.sql), through either runtime role.
    [Fact]
    public async Task A_panel_assignment_starts_now_and_only_ends_now_once()
    {
        var clinician = await StaffAsync("Dr. Oakes", StaffRoles.Clinician);
        var patient = await Accounts.PatientAsync(database, clinician);
        var other = await Accounts.PatientAsync(database, clinician);
        const string Insert = "INSERT INTO ehr.patient_assignments (id, patient_id, clinician_id, assigned_at, ended_at) VALUES (@id, @patient, @clinician, now() + @shift::interval, @ended::timestamptz)";

        Task InsertAsync(string connection, Guid id, string shift, object? ended = null) =>
            Accounts.ExecuteAsync(connection, clinician, Insert, ("id", id), ("patient", patient), ("clinician", clinician), ("shift", shift), ("ended", ended ?? DBNull.Value));

        async Task RefusedAsync(string state, Func<Task> attempt) =>
            Assert.Equal(state, (await Assert.ThrowsAsync<Npgsql.PostgresException>(attempt)).SqlState);

        // Backdated, ahead, or already ended: refused. The read role writes no assignment at all.
        await RefusedAsync(Npgsql.PostgresErrorCodes.CheckViolation, () => InsertAsync(App, Guid.CreateVersion7(), "-1 hour"));
        await RefusedAsync(Npgsql.PostgresErrorCodes.CheckViolation, () => InsertAsync(App, Guid.CreateVersion7(), "2 minutes"));
        await RefusedAsync(Npgsql.PostgresErrorCodes.CheckViolation, () => InsertAsync(App, Guid.CreateVersion7(), "0 seconds", DateTimeOffset.UtcNow));
        await RefusedAsync(Npgsql.PostgresErrorCodes.InsufficientPrivilege, () => InsertAsync(Read, Guid.CreateVersion7(), "0 seconds"));
        var assignment = Guid.CreateVersion7();
        await InsertAsync(App, assignment, "0 seconds");

        // While open: its start, patient and ID never change, and it ends only now.
        foreach (var change in new[]
        {
            "SET assigned_at = assigned_at - interval '1 hour'", "SET patient_id = @other", "SET ended_at = now() - interval '1 hour'",
            "SET ended_at = now() + interval '2 minutes'",
        })
        {
            await RefusedAsync(Npgsql.PostgresErrorCodes.CheckViolation, () =>
                Accounts.ExecuteAsync(App, clinician, $"UPDATE ehr.patient_assignments {change} WHERE id = @id", ("id", assignment), ("other", other)));
        }

        await RefusedAsync(Npgsql.PostgresErrorCodes.InsufficientPrivilege, () =>
            Accounts.ExecuteAsync(Read, clinician, "UPDATE ehr.patient_assignments SET ended_at = now() WHERE id = @id", ("id", assignment)));
        await Accounts.ExecuteAsync(App, clinician, "UPDATE ehr.patient_assignments SET ended_at = now() WHERE id = @id", ("id", assignment));

        // Once ended, never again.
        await RefusedAsync(Npgsql.PostgresErrorCodes.CheckViolation, () =>
            Accounts.ExecuteAsync(App, clinician, "UPDATE ehr.patient_assignments SET ended_at = now() WHERE id = @id", ("id", assignment)));
        await RefusedAsync(Npgsql.PostgresErrorCodes.CheckViolation, () =>
            Accounts.ExecuteAsync(App, clinician, "UPDATE ehr.patient_assignments SET ended_at = NULL WHERE id = @id", ("id", assignment)));
        Assert.Equal(["1 1"], await AsSuperuserAsync(
            "SELECT count(*) FILTER (WHERE ended_at IS NOT NULL) || ' ' || count(*) FROM ehr.patient_assignments WHERE patient_id = @patient", ("patient", patient)));
    }

    // The panel history is read with the patient's ID worked out once: the session's context functions never run for each
    // row of the panels' table (a review measured 0.6 s a page when they did, at 60,000 rows), nor for each row of any other
    // table the function reads. Read from the plan auto_explain gives for the statement inside the function, as a proxy
    // without sensitive items, on a table of some thousands of rows: no filter or index condition names a function of the
    // schema, so each is a value worked out once.
    [Fact]
    public async Task The_panel_history_is_read_without_the_session_worked_out_for_each_row()
    {
        var (_, patient, own) = await PatientAsync();
        var proxy = await ProxyAsync(own, patient, includeSensitive: false);
        var colleague = await StaffAsync("Dr. Olive", StaffRoles.Clinician);
        var elsewhere = await Accounts.PatientAsync(database, colleague);
        await Accounts.ExecuteAsync(database.SuperuserConnectionString, null,
            """
            INSERT INTO ehr.patient_assignments (id, patient_id, clinician_id, assigned_at, ended_at)
            SELECT gen_random_uuid(), @patient, @clinician, now() - make_interval(days => 400 + n), now() - make_interval(days => 399 + n)
            FROM generate_series(1, 3000) AS n
            """, ("patient", elsewhere), ("clinician", colleague));

        var plans = new List<string>();
        await using (var connection = new Npgsql.NpgsqlConnection(database.SuperuserConnectionString))
        {
            await connection.OpenAsync(Cancel);
            connection.Notice += (_, notice) => plans.Add(notice.Notice.MessageText);
            await using var transaction = await connection.BeginTransactionAsync(Cancel);
            foreach (var sql in new[]
            {
                "LOAD 'auto_explain'", "SET LOCAL auto_explain.log_min_duration = 0", "SET LOCAL auto_explain.log_nested_statements = on",
                "SET LOCAL auto_explain.log_analyze = on", "SET LOCAL auto_explain.log_timing = off", "SET LOCAL auto_explain.log_level = notice",
                "SET LOCAL client_min_messages = notice", "SET LOCAL ROLE ehr_app",
                $"SELECT set_config('app.user_id', '{proxy.Account}', true), set_config('app.acting_grant', '{proxy.Grant}', true)",
                "SELECT count(*) FROM ehr.record_views(NULL, NULL)",
            })
            {
                await using var command = new Npgsql.NpgsqlCommand(sql, connection, transaction);
                await command.ExecuteNonQueryAsync(Cancel);
            }
        }

        // Each scan of the panels' table with the lines that describe it, down to the next node.
        var scans = plans.SelectMany(plan =>
        {
            var lines = plan.Split('\n');
            return lines.Select((line, index) => (line, index)).Where(node => node.line.Contains(" on patient_assignments ", StringComparison.Ordinal))
                .Select(node => string.Join('\n', lines.Skip(node.index).TakeWhile((line, offset) => offset == 0 || !line.Contains("->", StringComparison.Ordinal))));
        }).ToList();
        var history = Assert.Single(scans, scan => scan.Contains("Seq Scan", StringComparison.Ordinal) && !scan.Contains("never executed", StringComparison.Ordinal));
        Assert.True(int.Parse(System.Text.RegularExpressions.Regex.Match(history, @"Rows Removed by Filter: (\d+)").Groups[1].Value, CultureInfo.InvariantCulture) >= 3000, history);
        Assert.All(scans, scan => Assert.DoesNotMatch(@"ehr\.\w+\(", scan));
        var body = Assert.Single(plans, plan => plan.Contains(" on audit_events ", StringComparison.Ordinal));
        Assert.All(body.Split('\n').Where(line => line.Contains("Filter:", StringComparison.Ordinal) || line.Contains("Cond:", StringComparison.Ordinal)),
            line => Assert.DoesNotMatch(@"ehr\.\w+\(", line));
    }

    // The twelve months bound what a page reads before it is cut: a record with more than a page of views from before
    // them, and three inside them, gives the three and no more to come.
    [Fact]
    public async Task Views_from_before_the_twelve_months_never_reach_a_page()
    {
        var (clinician, patient, own) = await PatientAsync();
        await Accounts.ExecuteAsync(database.SuperuserConnectionString, null,
            """
            INSERT INTO ehr.audit_events (id, occurred_at, actor_id, action, table_name, row_id, patient_id, source, sensitive)
            SELECT uuidv7(), now() - make_interval(days => 370 + n), @actor, 'read', 'patients', @patient, @patient, 'onprem', false
            FROM generate_series(1, 60) AS n
            """, ("actor", clinician), ("patient", patient));
        for (var minute = 1; minute <= 3; minute++)
        {
            await EventAsync(patient, clinician, "read", "patients", minute);
        }

        Assert.Equal(Enumerable.Repeat("staff|Dr. Alder|clinician|record|false|false", 3), await ViewsAsync(App, own, null));
    }

    // Every table of patient data, and the log itself, is named by the part rule, so a new one is a decision: which part,
    // and whether a proxy without sensitive items may see a view of it. A table the rule doesn't name is the record to the
    // patient and hidden from such a proxy (the proxy test above). The list is the one ProtectionTests keeps.
    [Fact]
    public async Task Every_table_of_patient_data_has_a_part()
    {
        string[] tables = [.. ProtectionTests.ExpectedProtection
            .Where(table => table.Value is ProtectionTests.Protection.Phi or ProtectionTests.Protection.Restricted)
            .Select(table => table.Key).Order(StringComparer.Ordinal)];
        Assert.Contains("audit_events", tables);
        Assert.Contains("note_addenda", tables);

        var unnamed = await AsSuperuserAsync("SELECT t FROM unnest(@tables) AS t WHERE ehr.record_view_part('read', t) IS NULL ORDER BY t", ("tables", tables));
        Assert.True(unnamed.Count == 0, "Name these tables in ehr.record_view_part (db/policies/42-record-views.sql): " + string.Join(", ", unnamed));
        Assert.Equal(["none"], await AsSuperuserAsync("SELECT coalesce(ehr.record_view_part('read', 'made_up_table'), 'none')"));
        Assert.Equal(["note messages"], await AsSuperuserAsync("SELECT ehr.record_view_part('read', 'note_addenda') || ' ' || ehr.record_view_part('read', 'messages')"));

        // Only its owner runs it, inside ehr.record_views.
        Assert.Equal(["false false false"], await AsSuperuserAsync(
            """
            SELECT has_function_privilege('ehr_app', p.oid, 'EXECUTE')::text || ' ' || has_function_privilege('ehr_read', p.oid, 'EXECUTE')::text || ' '
                || EXISTS (SELECT FROM aclexplode(p.proacl) WHERE grantee = 0)::text
            FROM pg_proc p WHERE p.oid = 'ehr.record_view_part'::regproc
            """));
    }

    [Fact]
    public async Task The_function_is_the_one_door_and_says_nothing_but_its_columns()
    {
        var (clinician, patient, own) = await PatientAsync();
        await EventAsync(patient, clinician, "read", "patients", 1);

        // The log itself is still the admins' alone: no row of it for the patient, through either role, and no policy
        // on it but those it had.
        foreach (var connection in new[] { App, Read })
        {
            Assert.Equal(["0"], await Accounts.QueryAsync(connection, own, "SELECT count(*)::text FROM ehr.audit_events"));
        }

        Assert.Equal(["audit_events_admin_select", "audit_events_insert", "audit_events_owner_mark", "audit_events_owner_select"],
            await AsSuperuserAsync("SELECT policyname::text FROM pg_policies WHERE schemaname = 'ehr' AND tablename = 'audit_events' ORDER BY policyname"));

        // Two moments in; a time, words and marks out; no ID of any kind.
        Assert.Equal(
            ["emergency boolean o", "more boolean o", "name text o", "newer_than timestamp with time zone i", "occurred_at timestamp with time zone o",
                "older_than timestamp with time zone i", "part text o", "role text o", "who text o"],
            await AsSuperuserAsync(
                """
                SELECT a.name || ' ' || format_type(a.type, NULL) || ' ' || CASE a.mode WHEN 'i' THEN 'i' ELSE 'o' END FROM unnest(
                    (SELECT p.proallargtypes FROM pg_proc p WHERE p.oid = 'ehr.record_views'::regproc),
                    (SELECT p.proargnames FROM pg_proc p WHERE p.oid = 'ehr.record_views'::regproc),
                    (SELECT p.proargmodes FROM pg_proc p WHERE p.oid = 'ehr.record_views'::regproc)) AS a(type, name, mode) ORDER BY a.name
                """));

        // Runs as its owner, for the runtime roles only.
        Assert.Equal(["true true true false"], await AsSuperuserAsync(
            """
            SELECT p.prosecdef::text || ' ' || has_function_privilege('ehr_app', p.oid, 'EXECUTE')::text || ' '
                || has_function_privilege('ehr_read', p.oid, 'EXECUTE')::text || ' '
                || EXISTS (SELECT FROM aclexplode(p.proacl) WHERE grantee = 0)::text
            FROM pg_proc p WHERE p.oid = 'ehr.record_views'::regproc
            """));
    }
}
