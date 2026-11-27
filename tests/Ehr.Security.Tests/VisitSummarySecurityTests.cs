using Ehr.Data.Accounts;
using Ehr.Testing;
using Npgsql;
using Xunit;
using static Ehr.Testing.TestRun;

namespace Ehr.Security.Tests;

// The expansion plan's step 13a: what the database lets each account do with a visit summary, as the runtime roles, in
// SQL of its own (db/policies/10-grants.sql, 30-rls.sql, 40-audit.sql and 52-visit-summaries.sql). A clinician with
// the patient on their panel releases one, as themselves, for a signed note of the same patient, one standing at a time,
// with no internal code or identifier in it; it is withdrawn once, with a reason, and never otherwise changed or
// deleted, but for its sensitivity, which follows its note and the note's addenda or is marked on its own under the
// addenda's rules; its releaser is told when someone else withdraws it; and the portal reads the standing ones of the
// patient it is looking at, a sensitive one only where the patient's own rule for sensitive items lets it, and of a
// withdrawn one only its date (Tahseen's decisions of 21 November 2026).
public class VisitSummarySecurityTests(MigratedDatabase database) : IClassFixture<MigratedDatabase>
{
    string App => database.ConnectionStringFor("ehr_app", MigratedDatabase.AppPassword);

    string Read => database.ConnectionStringFor("ehr_read", MigratedDatabase.ReadPassword);

    // A note's sections, all but the plan long enough for the whole-section rule (40 characters).
    const string Subjective = "Synthetic subjective: a dry cough for two days, worse at night.";
    const string Objective = "Synthetic objective: temperature 37.9, a few crackles at the left base.";
    const string Assessment = "Synthetic assessment: a viral chest infection, no pneumonia found.";
    const string Plan = "Synthetic plan: rest and fluids.";

    async Task<(Guid Clinician, Guid Patient, Guid Note)> SignedAsync(bool sensitive = false)
    {
        var clinician = await Accounts.StaffAsync(database, StaffRoles.Clinician);
        var patient = await Accounts.PatientAsync(database, clinician);
        await Accounts.AssignAsync(database, clinician, patient);
        return (clinician, patient, await NoteAsync(clinician, patient, sensitive));
    }

    async Task<Guid> NoteAsync(Guid clinician, Guid patient, bool sensitive = false)
    {
        var (draft, note) = (Guid.CreateVersion7(), Guid.CreateVersion7());
        await Accounts.ExecuteAsync(App, clinician,
            """
            INSERT INTO ehr.clinical_drafts
                (id, patient_id, clinician_id, body, version, subjective, objective, assessment, plan, structured_version, has_unreviewed_proposals)
            VALUES (@id, @patient, @owner, 'Synthetic plain text', 1, @subjective, @objective, @assessment, @plan, 1, false)
            """, ("id", draft), ("patient", patient), ("owner", clinician), ("subjective", Subjective), ("objective", Objective), ("assessment", Assessment), ("plan", Plan));
        await Accounts.ExecuteAsync(App, clinician,
            "INSERT INTO ehr.clinical_notes (id, draft_id, patient_id, draft_version, subjective, objective, assessment, plan, sensitive) VALUES (@note, @draft, @patient, 1, @subjective, @objective, @assessment, @plan, @sensitive)",
            ("note", note), ("draft", draft), ("patient", patient), ("subjective", Subjective), ("objective", Objective), ("assessment", Assessment), ("plan", Plan), ("sensitive", sensitive));
        return note;
    }

    const string Insert = """
        INSERT INTO ehr.visit_summaries (id, note_id, patient_id, talked_about, found, next_steps, note_signed_at, released_by, released_at, sensitive)
        VALUES (@id, @note, @patient, @talked, @found, @next, '2000-01-01', @by, '2000-01-01', @sensitive)
        """;

    // Released as the app releases one, sensitive on its own or not; forged values for who, when and the note's date,
    // which the database replaces. Its ID.
    async Task<Guid> ReleaseAsync(Guid? user, Guid patient, Guid note, string talked = "We talked about your cough.",
        string found = "Your chest sounded clear.", string next = "Rest, drink fluids, and call us if it gets worse.", bool sensitive = false, string? connection = null)
    {
        var id = Guid.CreateVersion7();
        await Accounts.ExecuteAsync(connection ?? App, user, Insert, ("id", id), ("note", note), ("patient", patient), ("talked", talked), ("found", found),
            ("next", next), ("by", Guid.NewGuid()), ("sensitive", sensitive));
        return id;
    }

    Task WithdrawAsync(Guid? user, Guid summary, string reason = "  Written for the wrong visit.  ", string? connection = null) =>
        Accounts.ExecuteAsync(connection ?? App, user, "UPDATE ehr.visit_summaries SET withdrawn_reason = @reason WHERE id = @id", ("id", summary), ("reason", reason));

    Task<List<string>> AsSuperuserAsync(string sql, params (string Name, object Value)[] parameters) =>
        Accounts.QueryAsync(database.SuperuserConnectionString, null, sql, parameters);

    static async Task<PostgresException> RefusedAsync(string state, Func<Task> attempt)
    {
        var refused = await Assert.ThrowsAsync<PostgresException>(attempt);
        Assert.Equal(state, refused.SqlState);
        return refused;
    }

    Task<List<string>> SummariesAsync(Guid patient) => AsSuperuserAsync(
        "SELECT talked_about FROM ehr.visit_summaries WHERE patient_id = @patient ORDER BY released_at, id", ("patient", patient));

    [Fact]
    public async Task A_clinician_on_the_panel_releases_one_as_themselves_and_the_database_sets_the_rest()
    {
        var (clinician, patient, note) = await SignedAsync();

        // Sent as released by someone else, long ago and for another date: the database replaces all three.
        var id = await ReleaseAsync(clinician, patient, note);

        Assert.Equal([$"{clinician} true false true null"], await AsSuperuserAsync(
            """
            SELECT s.released_by || ' ' || (s.released_at > now() - interval '1 minute') || ' ' || s.sensitive || ' ' || (s.note_signed_at = n.signed_at)
                || ' ' || coalesce(s.withdrawn_at::text, 'null')
            FROM ehr.visit_summaries s JOIN ehr.clinical_notes n ON n.id = s.note_id WHERE s.id = @id
            """, ("id", id)));
        // The write audit has it, by the clinician.
        Assert.Equal([$"insert {clinician}"], await AsSuperuserAsync(
            "SELECT action || ' ' || actor_id FROM ehr.audit_events WHERE table_name = 'visit_summaries' AND row_id = @id", ("id", id)));
    }

    [Fact]
    public async Task Only_a_clinician_with_the_patient_on_their_panel_releases_one_for_a_note_of_that_patient()
    {
        var (clinician, patient, note) = await SignedAsync();
        var outsider = await Accounts.StaffAsync(database, StaffRoles.Clinician);
        var admin = await Accounts.StaffAsync(database, StaffRoles.Admin);
        var account = await Accounts.PatientAccountAsync(database, patient);

        // Off the panel: the policy. An admin, the patient and nobody can't read the note: the trigger finds none.
        await RefusedAsync(PostgresErrorCodes.InsufficientPrivilege, () => ReleaseAsync(outsider, patient, note));
        foreach (var user in new Guid?[] { admin, account, null })
        {
            await Assert.ThrowsAsync<PostgresException>(() => ReleaseAsync(user, patient, note));
        }

        // The read role has no INSERT at all.
        await RefusedAsync(PostgresErrorCodes.InsufficientPrivilege, () => ReleaseAsync(clinician, patient, note, connection: Read));

        // A note of another patient, named with this patient; a note that doesn't exist.
        var (other, otherPatient, otherNote) = await SignedAsync();
        await RefusedAsync(PostgresErrorCodes.CheckViolation, () => ReleaseAsync(clinician, patient, otherNote));
        await RefusedAsync(PostgresErrorCodes.CheckViolation, () => ReleaseAsync(clinician, patient, Guid.NewGuid()));
        // Named with the other patient, for whom the clinician isn't on the panel.
        await RefusedAsync(PostgresErrorCodes.InsufficientPrivilege, () => ReleaseAsync(clinician, otherPatient, otherNote));
        Assert.Empty(await SummariesAsync(patient));
        Assert.Empty(await SummariesAsync(otherPatient));
        Assert.NotEqual(Guid.Empty, other);
    }

    [Fact]
    public async Task Every_part_is_written_and_none_is_longer_than_4000_characters()
    {
        var (clinician, patient, note) = await SignedAsync();
        foreach (var (talked, found, next) in new[] { ("", "Found.", "Next."), ("Talked.", "   \n  ", "Next."), ("Talked.", "Found.", new string('x', 4001)) })
        {
            await RefusedAsync(PostgresErrorCodes.CheckViolation, () => ReleaseAsync(clinician, patient, note, talked, found, next));
        }

        // 4,000 characters, line breaks kept, is fine.
        var longest = string.Concat(Enumerable.Repeat("Rest now.\n", 400));
        Assert.Equal(4000, longest.Length);
        var id = await ReleaseAsync(clinician, patient, note, next: longest);
        Assert.Equal([longest], await AsSuperuserAsync("SELECT next_steps FROM ehr.visit_summaries WHERE id = @id", ("id", id)));
    }

    // The rule for internal codes and identifiers (52-visit-summaries.sql): each of these is refused, with what was
    // found and its kind.
    public static TheoryData<string, string, string> Refused => new()
    {
        { "Your test showed E11.9 again.", "E11.9", "diagnosis-code" },
        { "We coded it as e11.9 today.", "e11.9", "diagnosis-code" },
        { "Billing code E119.", "E119", "diagnosis-code" },
        { "Your blood pressure (I10) is better.", "I10", "diagnosis-code" },
        { "Not U07.1 this time.", "U07.1", "diagnosis-code" },
        { "Keep taking 860975 every morning.", "860975", "medication-code" },
        { "Your allergy, 29046, is on file.", "29046", "medication-code" },
        { "We will repeat 4548-4 in three months.", "4548-4", "lab-code" },
        { "Avoid QE1QX6B99R.", "QE1QX6B99R", "substance-code" },
        { "Avoid qe1qx6b99r.", "qe1qx6b99r", "substance-code" },
        { "We ordered SYN-LAB-001 for you.", "SYN-LAB-001", "order-code" },
        { "Reference 0193a5c2-7e4b-7c1d-9f2e-3b4a5c6d7e8f.", "0193a5c2-7e4b-7c1d-9f2e-3b4a5c6d7e8f", "identifier" },
        { "Reference 0193A5C27E4B7C1D9F2E3B4A5C6D7E8F.", "0193A5C27E4B7C1D9F2E3B4A5C6D7E8F", "identifier" },
        // Look-alikes, read as they look: found as read, with what shows nothing taken out (a zero-width space, a soft
        // hyphen, a word joiner), compatibility forms folded (fullwidth letters, digits and stop) and dashes read as "-".
        { "Your test showed E1\u200B1.9 again.", "E11.9", "diagnosis-code" },
        { "Your test showed \uFF25\uFF11\uFF11\uFF0E\uFF19 again.", "E11.9", "diagnosis-code" },
        { "Keep taking 860\u00AD975 every morning.", "860975", "medication-code" },
        { "Keep taking \uFF18\uFF16\uFF10\uFF19\uFF17\uFF15 every morning.", "860975", "medication-code" },
        { "We will repeat 4548\u20134 in three months.", "4548-4", "lab-code" },
        { "We will repeat 4548\u20114 in three months.", "4548-4", "lab-code" },
        { "We will repeat 4548\u20144 in three months.", "4548-4", "lab-code" },
        { "We will repeat 4548\u22124 in three months.", "4548-4", "lab-code" },
        { "We will repeat 4548-\u20604 in three months.", "4548-4", "lab-code" },
        { "Avoid QE1Q\u200DX6B99R.", "QE1QX6B99R", "substance-code" },
        { "We ordered SYN\u2010LAB\u2010001 for you.", "SYN-LAB-001", "order-code" },
        { "Reference 0193a5c2-7e4b-\u200B7c1d-9f2e-3b4a5c6d7e8f.", "0193a5c2-7e4b-7c1d-9f2e-3b4a5c6d7e8f", "identifier" },
    };

    [Theory]
    [MemberData(nameof(Refused))]
    public async Task A_summary_with_an_internal_code_or_identifier_is_refused(string text, string found, string kind)
    {
        var (clinician, patient, note) = await SignedAsync();

        var refused = await RefusedAsync(PostgresErrorCodes.CheckViolation, () => ReleaseAsync(clinician, patient, note, found: text));

        Assert.Equal("visit_summaries_plain", refused.ConstraintName);
        Assert.Equal([$"2 {found} {kind}"], await ProblemsAsync(clinician, patient, note, "Talked.", text, "Next."));
        Assert.Empty(await SummariesAsync(patient));
    }

    // And these pass: ordinary words, doses, dates and numbers, a category the reference data doesn't hold as a code,
    // and short runs of digits that happen to be codes (1998 is RxNorm's captopril).
    [Theory]
    [InlineData("Your vitamin B12 is normal, and so is your A1c.")]
    [InlineData("Wear an N95 mask on the bus. Your diabetes (the E11 family) is steady.")]
    [InlineData("Take metformin 500 mg twice a day, and vitamin D 50,000 units each week.")]
    [InlineData("Come back on 2026-11-21. Take 1-2 tablets when you need them.")]
    [InlineData("You have had this since 1998. Call 972-555-0100, or 3.14159 if you like pi.")]
    [InlineData("Synthetic plan: rest and fluids.")]
    public async Task Ordinary_words_doses_dates_and_numbers_pass(string text)
    {
        var (clinician, patient, note) = await SignedAsync();

        Assert.Empty(await ProblemsAsync(clinician, patient, note, text, text, text));
        await ReleaseAsync(clinician, patient, note, text, text, text);
        Assert.Single(await SummariesAsync(patient));
    }

    [Fact]
    public async Task The_patients_own_record_number_and_a_whole_section_of_the_note_are_refused()
    {
        var (clinician, patient, note) = await SignedAsync();
        var mrn = (await AsSuperuserAsync("SELECT mrn FROM ehr.patients WHERE id = @id", ("id", patient))).Single();
        var (_, other, _) = await SignedAsync();
        var otherMrn = (await AsSuperuserAsync("SELECT mrn FROM ehr.patients WHERE id = @id", ("id", other))).Single();

        // The patient's own MRN, in any case, as a whole word.
        Assert.Equal([$"1 {mrn.ToUpperInvariant()} record-number"], await ProblemsAsync(clinician, patient, note, $"Your number is {mrn.ToUpperInvariant()}.", "Found.", "Next."));
        await RefusedAsync(PostgresErrorCodes.CheckViolation, () => ReleaseAsync(clinician, patient, note, $"Your number is {mrn}."));
        // Another patient's isn't looked for (decided: this patient's only), nor one inside a longer word.
        Assert.Empty(await ProblemsAsync(clinician, patient, note, $"Not {otherMrn}, nor x{mrn}x.", "Found.", "Next."));

        // The note's whole Assessment pasted in, in other case and spacing; Subjective likewise, and Objective (step 14).
        // Its plan is too short to count (under 40 characters), and a fragment of a section passes: the rule's known limit
        // (docs/UC14.md).
        Assert.Equal(["2 Objective note-section", "3 Assessment note-section", "3 Subjective note-section"], await ProblemsAsync(clinician, patient, note, "Talked.",
            Objective.ToLowerInvariant(),
            "Here is what I wrote:\n" + Assessment.ToUpperInvariant().Replace(" ", "  \n", StringComparison.Ordinal) + " And " + Subjective));
        await RefusedAsync(PostgresErrorCodes.CheckViolation, () => ReleaseAsync(clinician, patient, note, found: Assessment));
        Assert.Empty(await ProblemsAsync(clinician, patient, note, Plan, "a viral chest infection, no pneumonia found", "Next."));
        Assert.Empty(await SummariesAsync(patient));
    }

    // A note signed before the expansion plan's step 14 is compared by the sections it was signed with: its History too.
    [Fact]
    public async Task A_note_signed_before_four_sections_is_compared_by_its_history()
    {
        var clinician = await Accounts.StaffAsync(database, StaffRoles.Clinician);
        var patient = await Accounts.PatientAsync(database, clinician);
        await Accounts.AssignAsync(database, clinician, patient);
        const string history = "Synthetic history: a dry cough for two days, worse at night, and a temperature of 37.9.";
        var (_, note) = await EarlierNotes.SignedAsync(database, clinician, patient, history, assessment: Assessment);

        Assert.Equal(["2 History note-section", "3 Assessment note-section"], await ProblemsAsync(clinician, patient, note, "Talked.",
            "We found: " + history.Replace("37.9", "\uFF13\uFF17.9", StringComparison.Ordinal), Assessment.ToUpperInvariant()));
        await RefusedAsync(PostgresErrorCodes.CheckViolation, () => ReleaseAsync(clinician, patient, note, found: history));
        await ReleaseAsync(clinician, patient, note, "We talked about your cough.", "Your chest sounded a little noisy.", "Rest and drink fluids.");
        Assert.Single(await SummariesAsync(patient));
    }

    // A note signed before step 14 still takes an addendum and a summary, as the app role signs and releases them for any
    // note: the note stays exactly as it was signed, and an addendum signed sensitive after the summary makes the summary
    // sensitive, inherited, as it does for a note of four sections.
    [Fact]
    public async Task A_note_signed_before_four_sections_takes_an_addendum_and_a_summary()
    {
        var clinician = await Accounts.StaffAsync(database, StaffRoles.Clinician);
        var patient = await Accounts.PatientAsync(database, clinician);
        await Accounts.AssignAsync(database, clinician, patient);
        var (_, note) = await EarlierNotes.SignedAsync(database, clinician, patient, "Synthetic history: a dry cough for two days.");
        const string row = "SELECT to_jsonb(n)::text FROM ehr.clinical_notes n WHERE id = @note";
        var signed = await AsSuperuserAsync(row, ("note", note));

        await AddendumAsync(clinician, patient, note);
        var summary = await ReleaseAsync(clinician, patient, note);
        Assert.Equal(["false false none none"], await MarkOfAsync(summary));
        await AddendumAsync(clinician, patient, note, sensitive: true);

        Assert.Equal([$"true true none {Inherited}"], await MarkOfAsync(summary));
        Assert.Equal([$"{clinician} false", $"{clinician} true"], await AsSuperuserAsync(
            "SELECT author_id || ' ' || sensitive FROM ehr.note_addenda WHERE note_id = @note ORDER BY signed_at", ("note", note)));
        Assert.Equal([$"{clinician}"], await AsSuperuserAsync("SELECT released_by::text FROM ehr.visit_summaries WHERE id = @id", ("id", summary)));
        Assert.Equal(signed, await AsSuperuserAsync(row, ("note", note)));
    }

    // The patient's MRN and a section of the note are looked for as they look too, and what is stored is what was typed:
    // a summary with dashes, fullwidth letters and characters that show nothing, and no code, is released as it was
    // sent.
    [Fact]
    public async Task Look_alikes_are_read_as_what_they_look_like_and_stored_as_typed()
    {
        var (clinician, patient, note) = await SignedAsync();
        var mrn = (await AsSuperuserAsync("SELECT mrn FROM ehr.patients WHERE id = @id", ("id", patient))).Single();
        var fullwidth = string.Concat(mrn.Select(c => (char)(c + 0xFEE0)));

        Assert.Equal([$"1 {mrn} record-number"], await ProblemsAsync(clinician, patient, note, $"Your number is {fullwidth}.", "Found.", "Next."));
        Assert.Equal([$"1 {mrn} record-number"], await ProblemsAsync(clinician, patient, note, $"Your number is {mrn[..6]}\u200B{mrn[6..]}.", "Found.", "Next."));
        Assert.Equal(["2 Assessment note-section"], await ProblemsAsync(clinician, patient, note, "Talked.",
            Assessment.Replace(" ", " \u200B", StringComparison.Ordinal).Replace("no pneumonia", "no \uFF50neumonia", StringComparison.Ordinal), "Next."));
        // The sections step 14 added are read the same way: Objective with characters that show nothing, a fullwidth
        // digit and a non-breaking hyphen's look-alike; Subjective with a soft hyphen and a direction mark.
        Assert.Equal(["1 Objective note-section", "3 Subjective note-section"], await ProblemsAsync(clinician, patient, note,
            Objective.Replace("37.9", "\uFF13\uFF17.9", StringComparison.Ordinal).Replace(" a few", "\u2060 a\u200C few", StringComparison.Ordinal), "Found.",
            Subjective.Replace("dry", "d\u00ADry", StringComparison.Ordinal).Replace("night", "\u200Enight", StringComparison.Ordinal)));

        const string typed = "Rest \u2014 and drink fluids.\u200D Your \uFF22\uFF30 is fine: check it 1\u20132 times a day.";
        Assert.Empty(await ProblemsAsync(clinician, patient, note, typed, typed, typed));
        var id = await ReleaseAsync(clinician, patient, note, typed, typed, typed);
        Assert.Equal([$"{typed}|{typed}|{typed}"], await AsSuperuserAsync("SELECT talked_about || '|' || found || '|' || next_steps FROM ehr.visit_summaries WHERE id = @id", ("id", id)));
    }

    async Task<List<string>> ProblemsAsync(Guid clinician, Guid patient, Guid note, string talked, string found, string next) =>
        await Accounts.QueryAsync(App, clinician,
            "SELECT part || ' ' || found || ' ' || kind FROM ehr.visit_summary_problems(@patient, @note, ARRAY[@talked, @found, @next])",
            ("patient", patient), ("note", note), ("talked", talked), ("found", found), ("next", next));

    [Fact]
    public async Task Only_the_app_role_may_ask_the_rule()
    {
        var (clinician, patient, note) = await SignedAsync();
        await RefusedAsync(PostgresErrorCodes.InsufficientPrivilege, () => Accounts.QueryAsync(Read, clinician,
            "SELECT count(*) FROM ehr.visit_summary_problems(@patient, @note, ARRAY['a', 'b', 'c'])", ("patient", patient), ("note", note)));
    }

    [Fact]
    public async Task A_note_has_one_standing_summary_and_a_new_one_once_it_is_withdrawn()
    {
        var (clinician, patient, note) = await SignedAsync();
        var first = await ReleaseAsync(clinician, patient, note, "First.");

        await RefusedAsync(PostgresErrorCodes.UniqueViolation, () => ReleaseAsync(clinician, patient, note, "Second."));
        await WithdrawAsync(clinician, first);
        await ReleaseAsync(clinician, patient, note, "Second.");

        Assert.Equal(["First.", "Second."], await SummariesAsync(patient));
        Assert.Equal(["1"], await AsSuperuserAsync("SELECT count(*)::text FROM ehr.visit_summaries WHERE note_id = @note AND withdrawn_at IS NULL", ("note", note)));
    }

    [Fact]
    public async Task A_summary_is_withdrawn_once_with_a_reason_by_a_clinician_on_the_panel()
    {
        var (clinician, patient, note) = await SignedAsync();
        var colleague = await Accounts.StaffAsync(database, StaffRoles.Clinician);
        await Accounts.AssignAsync(database, colleague, patient);
        var outsider = await Accounts.StaffAsync(database, StaffRoles.Clinician);
        var admin = await Accounts.StaffAsync(database, StaffRoles.Admin);
        var account = await Accounts.PatientAccountAsync(database, patient);
        var summary = await ReleaseAsync(clinician, patient, note);
        Task<List<string>> StateAsync() => AsSuperuserAsync(
            "SELECT coalesce(withdrawn_by::text, 'none') || ' ' || coalesce(withdrawn_reason, 'none') || ' ' || coalesce((withdrawn_at > now() - interval '1 minute')::text, 'none') FROM ehr.visit_summaries WHERE id = @id",
            ("id", summary));

        // No row to change for anyone off the panel, an admin, the patient or nobody; no privilege on the read role.
        foreach (var user in new Guid?[] { outsider, admin, account, null })
        {
            await WithdrawAsync(user, summary);
        }

        await RefusedAsync(PostgresErrorCodes.InsufficientPrivilege, () => WithdrawAsync(clinician, summary, connection: Read));
        // Who and when are the database's: no privilege on either.
        await RefusedAsync(PostgresErrorCodes.InsufficientPrivilege, () => Accounts.ExecuteAsync(App, colleague,
            "UPDATE ehr.visit_summaries SET withdrawn_reason = 'x', withdrawn_by = @by, withdrawn_at = '2000-01-01' WHERE id = @id", ("id", summary), ("by", clinician)));
        // A reason is required, of up to 500 characters: the trigger, and the column's own length.
        await RefusedAsync(PostgresErrorCodes.CheckViolation, () => WithdrawAsync(colleague, summary, "   "));
        await RefusedAsync(PostgresErrorCodes.StringDataRightTruncation, () => WithdrawAsync(colleague, summary, new string('x', 501)));

        Assert.Equal(["none none none"], await StateAsync());

        // Any clinician with the patient on their panel, not only the one who released it.
        await WithdrawAsync(colleague, summary);
        Assert.Equal([$"{colleague} Written for the wrong visit. true"], await StateAsync());
        // Once.
        await RefusedAsync(PostgresErrorCodes.CheckViolation, () => WithdrawAsync(clinician, summary, "Again."));
        Assert.Equal([$"{colleague} Written for the wrong visit. true"], await StateAsync());
        Assert.Equal([$"insert {clinician}", $"update {colleague}"], await AsSuperuserAsync(
            "SELECT action || ' ' || actor_id FROM ehr.audit_events WHERE table_name = 'visit_summaries' AND row_id = @id ORDER BY occurred_at", ("id", summary)));
    }

    [Fact]
    public async Task Nothing_else_about_a_released_summary_changes_and_it_is_never_deleted_even_by_the_owner()
    {
        var (clinician, patient, note) = await SignedAsync();
        var summary = await ReleaseAsync(clinician, patient, note);
        var before = await AsSuperuserAsync("SELECT to_jsonb(s)::text FROM ehr.visit_summaries s WHERE id = @id", ("id", summary));

        foreach (var column in new[] { "talked_about = 'Changed'", "found = 'Changed'", "next_steps = 'Changed'", "note_signed_at = now()", "released_by = gen_random_uuid()",
            "released_at = now()", "patient_id = gen_random_uuid()", "note_id = gen_random_uuid()", "withdrawn_at = now()", "sensitive_inherited = true",
            "sensitive_marked_by = gen_random_uuid()", "sensitive_changed_at = now()" })
        {
            // No privilege for the apps; the trigger for the tables' owner and the superuser, who have every one.
            await RefusedAsync(PostgresErrorCodes.InsufficientPrivilege, () => Accounts.ExecuteAsync(App, clinician, $"UPDATE ehr.visit_summaries SET {column} WHERE id = @id", ("id", summary)));
            await RefusedAsync(PostgresErrorCodes.CheckViolation, () => Accounts.ExecuteAsync(database.OwnerConnectionString, clinician, $"UPDATE ehr.visit_summaries SET {column} WHERE id = @id", ("id", summary)));
            await RefusedAsync(PostgresErrorCodes.CheckViolation, () => Accounts.ExecuteAsync(database.SuperuserConnectionString, clinician, $"UPDATE ehr.visit_summaries SET {column} WHERE id = @id", ("id", summary)));
        }

        // Withdrawing or marking with another change beside it: refused too. Marking with no reason: refused.
        await RefusedAsync(PostgresErrorCodes.CheckViolation, () => Accounts.ExecuteAsync(database.SuperuserConnectionString, clinician,
            "UPDATE ehr.visit_summaries SET withdrawn_reason = 'Wrong.', found = 'Changed' WHERE id = @id", ("id", summary)));
        await RefusedAsync(PostgresErrorCodes.CheckViolation, () => Accounts.ExecuteAsync(database.SuperuserConnectionString, clinician,
            "UPDATE ehr.visit_summaries SET sensitive = true, sensitive_reason = 'Private.', found = 'Changed' WHERE id = @id", ("id", summary)));
        await RefusedAsync(PostgresErrorCodes.CheckViolation, () => Accounts.ExecuteAsync(App, clinician, "UPDATE ehr.visit_summaries SET sensitive = true WHERE id = @id", ("id", summary)));
        await RefusedAsync(PostgresErrorCodes.InsufficientPrivilege, () => Accounts.ExecuteAsync(App, clinician, "DELETE FROM ehr.visit_summaries WHERE id = @id", ("id", summary)));
        await RefusedAsync(PostgresErrorCodes.CheckViolation, () => Accounts.ExecuteAsync(database.SuperuserConnectionString, null, "DELETE FROM ehr.visit_summaries WHERE id = @id", ("id", summary)));
        Assert.Equal(before, await AsSuperuserAsync("SELECT to_jsonb(s)::text FROM ehr.visit_summaries s WHERE id = @id", ("id", summary)));
    }

    // Who reads which summaries, through both roles: clinicians every one, from the table; no portal session any row of
    // it, whoever it is acting for. The portal is given the standing ones of the patient it is looking at, a sensitive one
    // for the patient themselves and for a proxy the patient allowed sensitive items, through ehr.standing_visit_summaries,
    // and of a withdrawn one its date and whether it is sensitive, under the same rule, through
    // ehr.withdrawn_visit_summaries: never a mark's reason, who made it, the note or the withdrawal. Nobody else any.
    [Fact]
    public async Task Clinicians_read_every_summary_and_the_portal_no_row_only_what_the_two_functions_give_under_the_patients_rule()
    {
        var (clinician, patient, plain) = await SignedAsync();
        var replaced = await NoteAsync(clinician, patient);
        var sensitive = await NoteAsync(clinician, patient, sensitive: true);
        var withdrawnSensitive = await NoteAsync(clinician, patient, sensitive: true);
        var marked = await NoteAsync(clinician, patient);
        await ReleaseAsync(clinician, patient, plain, "plain");
        await WithdrawAsync(clinician, await ReleaseAsync(clinician, patient, replaced, "withdrawn"));
        await ReleaseAsync(clinician, patient, replaced, "replacement");
        await ReleaseAsync(clinician, patient, sensitive, "sensitive");
        await WithdrawAsync(clinician, await ReleaseAsync(clinician, patient, withdrawnSensitive, "withdrawn-sensitive"));
        // Marked on its own after it was released, with a reason the portal must never be given.
        await ChangeAsync(clinician, "visit_summaries", await ReleaseAsync(clinician, patient, marked, "marked"), sensitive: true, "Synthetic private reason.");

        var outsider = await Accounts.StaffAsync(database, StaffRoles.Clinician);
        var admin = await Accounts.StaffAsync(database, StaffRoles.Admin);
        var own = await Accounts.PatientAccountAsync(database, patient);
        var (_, elsewhere, _) = await SignedAsync();
        var otherPatient = await Accounts.PatientAccountAsync(database, elsewhere);
        var (allowed, allowedGrant) = await ProxyAsync(own, patient, includeSensitive: true);
        var (without, withoutGrant) = await ProxyAsync(own, patient, includeSensitive: false);
        var (revoked, revokedGrant) = await ProxyAsync(own, patient, includeSensitive: true);
        await Accounts.ExecuteAsync(App, own, "UPDATE ehr.proxy_grants SET revoked_at = now() WHERE id = @id", ("id", revokedGrant));
        var (proxyOnly, _) = await Accounts.ProxyOnlyAccountAsync(database);
        const string Seen = "SELECT coalesce(string_agg(talked_about || coalesce(withdrawn_reason, '') || coalesce(sensitive_reason, ''), ' ' ORDER BY talked_about), '') FROM ehr.visit_summaries WHERE patient_id = @patient";
        // Every row of the table, any patient's.
        const string Rows = "SELECT count(*)::text FROM ehr.visit_summaries";
        const string Given = "SELECT coalesce(string_agg(talked_about || ' ' || found || ' ' || next_steps || ' ' || released_by || ' ' || sensitive, ' / ' ORDER BY talked_about), '') FROM ehr.standing_visit_summaries(100)";
        const string Placeholders = "SELECT count(*) || ' ' || count(*) FILTER (WHERE sensitive) FROM ehr.withdrawn_visit_summaries()";
        var name = (await AsSuperuserAsync("SELECT display_name FROM ehr.staff_users WHERE id = @id", ("id", clinician))).Single();
        string GivenOf(params string[] parts) => string.Join(" / ", parts.Select(part =>
            $"{part} Your chest sounded clear. Rest, drink fluids, and call us if it gets worse. {name} {(part is "plain" or "replacement" ? "false" : "true")}"));
        const string All = "markedSynthetic private reason. plain replacement sensitive withdrawnWritten for the wrong visit. withdrawn-sensitiveWritten for the wrong visit.";

        foreach (var connection in new[] { App, Read })
        {
            async Task<string> AskAsync(string sql, Guid? user, Guid? grant)
            {
                using (grant is { } acting ? Accounts.ActingUnder(acting) : null)
                {
                    return (await Accounts.QueryAsync(connection, user, sql, ("patient", patient))).Single();
                }
            }

            foreach (var (user, grant, seen, given, placeholders) in new (Guid?, Guid?, string, string, string)[]
            {
                (clinician, null, All, "", "0 0"),
                (outsider, null, All, "", "0 0"),
                (admin, null, "", "", "0 0"),
                (null, null, "", "", "0 0"),
                (own, null, "", GivenOf("marked", "plain", "replacement", "sensitive"), "2 1"),
                (allowed, allowedGrant, "", GivenOf("marked", "plain", "replacement", "sensitive"), "2 1"),
                (without, withoutGrant, "", GivenOf("plain", "replacement"), "1 0"),
                (revoked, revokedGrant, "", "", "0 0"),
                // A grant named by someone it isn't addressed to; another patient; a proxy-only account on its own.
                (otherPatient, allowedGrant, "", "", "0 0"),
                (otherPatient, null, "", "", "0 0"),
                (proxyOnly, null, "", "", "0 0"),
            })
            {
                Assert.Equal(seen, await AskAsync(Seen, user, grant));
                Assert.Equal(given, await AskAsync(Given, user, grant));
                Assert.Equal(placeholders, await AskAsync(Placeholders, user, grant));
                if (user != clinician && user != outsider)
                {
                    Assert.Equal("0", await AskAsync(Rows, user, grant));
                }
            }
        }

        // The portal's two functions return these columns and no other: nothing of a mark but whether there is one, and
        // nothing of the note or a withdrawal.
        const string Columns = """
            SELECT a.name || ' ' || format_type(a.type, NULL) FROM unnest(
                (SELECT p.proallargtypes FROM pg_proc p WHERE p.oid = @function::regproc),
                (SELECT p.proargnames FROM pg_proc p WHERE p.oid = @function::regproc),
                (SELECT p.proargmodes FROM pg_proc p WHERE p.oid = @function::regproc)) WITH ORDINALITY AS a(type, name, mode, position)
            WHERE a.mode = 't' ORDER BY a.position
            """;
        Assert.Equal(["id uuid", "note_signed_at timestamp with time zone", "released_by text", "talked_about text", "found text", "next_steps text", "sensitive boolean"],
            await AsSuperuserAsync(Columns, ("function", "ehr.standing_visit_summaries")));
        Assert.Equal(["note_signed_at timestamp with time zone", "sensitive boolean"], await AsSuperuserAsync(Columns, ("function", "ehr.withdrawn_visit_summaries")));
    }

    // A proxy for the patient, with a portal account of their own, given access by the patient. The account and grant.
    async Task<(Guid Account, Guid Grant)> ProxyAsync(Guid patientAccount, Guid patient, bool includeSensitive)
    {
        var (account, email) = await Accounts.ProxyOnlyAccountAsync(database);
        var grant = Guid.CreateVersion7();
        await Accounts.ExecuteAsync(App, patientAccount,
            """
            INSERT INTO ehr.proxy_grants (id, patient_id, proxy_email, scope, include_sensitive, expires_at, granted_by, granted_at, granted_by_kind)
            VALUES (@id, @patient, @email, 'record', @sensitive, now() + interval '30 days', gen_random_uuid(), '2000-01-01', 'patient')
            """, ("id", grant), ("patient", patient), ("email", email), ("sensitive", includeSensitive));
        return (account, grant);
    }

    async Task<Guid> AddendumAsync(Guid author, Guid patient, Guid note, bool sensitive = false)
    {
        var id = Guid.CreateVersion7();
        await Accounts.ExecuteAsync(App, author, "INSERT INTO ehr.note_addenda (id, note_id, patient_id, author_id, text, sensitive) VALUES (@id, @note, @patient, @author, 'Synthetic addendum.', @sensitive)",
            ("id", id), ("note", note), ("patient", patient), ("author", author), ("sensitive", sensitive));
        return id;
    }

    Task ChangeAsync(Guid user, string table, Guid id, bool sensitive, string reason = "Synthetic reason.") =>
        Accounts.ExecuteAsync(App, user, $"UPDATE ehr.{table} SET sensitive = @sensitive, sensitive_reason = @reason WHERE id = @id",
            ("id", id), ("sensitive", sensitive), ("reason", reason));

    // The reasons the database gives an inherited mark and its clearing: never the words given for the note's or the
    // addendum's change.
    const string Inherited = "Its note or an addendum to the note is sensitive.";
    const string Cleared = "Neither its note nor any addendum to the note is sensitive now.";

    // "sensitive inherited marked-by reason" of a summary.
    Task<List<string>> MarkOfAsync(Guid summary) => AsSuperuserAsync(
        "SELECT sensitive || ' ' || sensitive_inherited || ' ' || coalesce(sensitive_marked_by::text, 'none') || ' ' || coalesce(sensitive_reason, 'none') FROM ehr.visit_summaries WHERE id = @id",
        ("id", summary));

    // Tahseen's decision of 21 November 2026: a summary starts sensitive when its note or any of its addenda is
    // (inherited), or when the clinician marks it as they release it (its own mark).
    [Fact]
    public async Task A_summary_starts_sensitive_from_its_note_an_addendum_or_its_own_mark()
    {
        var (clinician, patient, sensitiveNote) = await SignedAsync(sensitive: true);
        var fromNote = await ReleaseAsync(clinician, patient, sensitiveNote);
        Assert.Equal(["true true none none"], await MarkOfAsync(fromNote));

        var plainNote = await NoteAsync(clinician, patient);
        await AddendumAsync(clinician, patient, plainNote, sensitive: true);
        var fromAddendum = await ReleaseAsync(clinician, patient, plainNote);
        Assert.Equal(["true true none none"], await MarkOfAsync(fromAddendum));

        var ownNote = await NoteAsync(clinician, patient);
        var own = await ReleaseAsync(clinician, patient, ownNote, sensitive: true);
        Assert.Equal([$"true false {clinician} none"], await MarkOfAsync(own));

        var ordinaryNote = await NoteAsync(clinician, patient);
        var ordinary = await ReleaseAsync(clinician, patient, ordinaryNote);
        Assert.Equal(["false false none none"], await MarkOfAsync(ordinary));
    }

    // And it becomes sensitive afterwards, inherited, when its note is marked, an addendum is marked on its own, or an
    // addendum is signed sensitive; the inherited mark is cleared when the last of them is unmarked, and only then. A
    // clinician marks one on its own, with a reason; only that person or the note's signer unmarks it, and never while
    // the note or an addendum is sensitive. Withdrawn summaries follow too. A proxy without sensitive items loses the
    // summary the moment the mark arrives.
    [Fact]
    public async Task A_summary_follows_its_note_and_addenda_and_is_marked_and_unmarked_under_the_addenda_rules()
    {
        var (signer, patient, note) = await SignedAsync();
        var colleague = await Accounts.StaffAsync(database, StaffRoles.Clinician);
        await Accounts.AssignAsync(database, colleague, patient);
        var third = await Accounts.StaffAsync(database, StaffRoles.Clinician);
        await Accounts.AssignAsync(database, third, patient);
        var own = await Accounts.PatientAccountAsync(database, patient);
        var (without, withoutGrant) = await ProxyAsync(own, patient, includeSensitive: false);
        var withdrawn = await ReleaseAsync(colleague, patient, note, "Withdrawn one.");
        await WithdrawAsync(colleague, withdrawn);
        var summary = await ReleaseAsync(colleague, patient, note);
        var addendum = await AddendumAsync(signer, patient, note);
        async Task<List<string>> ProxySeesAsync()
        {
            using (Accounts.ActingUnder(withoutGrant))
            {
                return await Accounts.QueryAsync(App, without, "SELECT count(*)::text FROM ehr.standing_visit_summaries(100)");
            }
        }

        Assert.Equal(["1"], await ProxySeesAsync());

        // 1. The note is marked: the summaries follow, inherited, in the same statement, with the database's reason and
        //    never the words given for the note, and the proxy without sensitive items is given it no more.
        await ChangeAsync(signer, "clinical_notes", note, sensitive: true, "Patient asked for it.");
        Assert.Equal([$"true true none {Inherited}"], await MarkOfAsync(summary));
        Assert.Equal([$"true true none {Inherited}"], await MarkOfAsync(withdrawn));
        Assert.Equal(["0"], await ProxySeesAsync());
        // An inherited mark can't be taken off on its own, by anyone.
        foreach (var user in new[] { signer, colleague })
        {
            await RefusedAsync(PostgresErrorCodes.CheckViolation, () => ChangeAsync(user, "visit_summaries", summary, sensitive: false));
        }

        // 2. Unmarked: cleared, as nothing else of the note is sensitive.
        await ChangeAsync(signer, "clinical_notes", note, sensitive: false, "Marked the wrong note.");
        Assert.Equal([$"false false none {Cleared}"], await MarkOfAsync(summary));
        Assert.Equal([$"false false none {Cleared}"], await MarkOfAsync(withdrawn));
        Assert.Equal(["1"], await ProxySeesAsync());

        // 3. An addendum marked on its own while the note isn't sensitive: the summary follows it.
        await ChangeAsync(colleague, "note_addenda", addendum, sensitive: true, "Names a relative.");
        Assert.Equal([$"true true none {Inherited}"], await MarkOfAsync(summary));
        Assert.Equal(["0"], await ProxySeesAsync());
        // The note marked and unmarked meanwhile: the addendum's own mark stands, so the summary's inherited one does.
        await ChangeAsync(signer, "clinical_notes", note, sensitive: true, "Patient asked for it.");
        await ChangeAsync(signer, "clinical_notes", note, sensitive: false, "Marked the wrong note.");
        Assert.StartsWith("true true ", (await MarkOfAsync(summary)).Single());
        // The addendum unmarked: the last source, so cleared.
        await ChangeAsync(colleague, "note_addenda", addendum, sensitive: false, "Not about a relative.");
        Assert.Equal([$"false false none {Cleared}"], await MarkOfAsync(summary));

        // 4. An addendum signed sensitive afterwards: the summary follows.
        var later = await AddendumAsync(colleague, patient, note, sensitive: true);
        Assert.Equal([$"true true none {Inherited}"], await MarkOfAsync(summary));
        await ChangeAsync(colleague, "note_addenda", later, sensitive: false, "Signed so by mistake.");
        Assert.Equal([$"false false none {Cleared}"], await MarkOfAsync(summary));

        // 5. Marked on its own by a clinician on the panel, with a reason; refused with none. Off the panel, an admin,
        //    the patient and nobody find no row to mark.
        foreach (var user in new Guid?[] { await Accounts.StaffAsync(database, StaffRoles.Clinician), await Accounts.StaffAsync(database, StaffRoles.Admin), own, null })
        {
            await Accounts.ExecuteAsync(App, user, "UPDATE ehr.visit_summaries SET sensitive = true, sensitive_reason = 'x' WHERE id = @id", ("id", summary));
        }

        await RefusedAsync(PostgresErrorCodes.CheckViolation, () => ChangeAsync(colleague, "visit_summaries", summary, sensitive: true, "   "));
        Assert.Equal([$"false false none {Cleared}"], await MarkOfAsync(summary));
        await ChangeAsync(colleague, "visit_summaries", summary, sensitive: true, "Discussed something private.");
        Assert.Equal([$"true false {colleague} Discussed something private."], await MarkOfAsync(summary));
        Assert.Equal(["0"], await ProxySeesAsync());
        // Its own mark outlives a source's: the note marked and unmarked leaves it marked by the colleague.
        await ChangeAsync(signer, "clinical_notes", note, sensitive: true, "Patient asked for it.");
        await RefusedAsync(PostgresErrorCodes.CheckViolation, () => ChangeAsync(colleague, "visit_summaries", summary, sensitive: false));
        await ChangeAsync(signer, "clinical_notes", note, sensitive: false, "Marked the wrong note.");
        Assert.Equal([$"true false {colleague} Discussed something private."], await MarkOfAsync(summary));
        // Unmarked by the person who marked it or the note's signer only: a third clinician is refused.
        await RefusedAsync(PostgresErrorCodes.InsufficientPrivilege, () => ChangeAsync(third, "visit_summaries", summary, sensitive: false));
        await ChangeAsync(signer, "visit_summaries", summary, sensitive: false, "Reviewed with the patient.");
        Assert.Equal(["false false none Reviewed with the patient."], await MarkOfAsync(summary));
        Assert.Equal(["1"], await ProxySeesAsync());
        // Marked on its own once more, it is unmarked by the person who marked it, too.
        await ChangeAsync(third, "visit_summaries", summary, sensitive: true, "Private after all.");
        await ChangeAsync(third, "visit_summaries", summary, sensitive: false, "Not private.");
        Assert.Equal(["false false none Not private."], await MarkOfAsync(summary));
    }

    // A note with no addendum: nothing changes an addendum to run the addenda's trigger, so only the notes' own
    // (clinical_notes_follow_summaries) carries the note's mark to its summaries, the withdrawn one too, and takes it off.
    [Fact]
    public async Task A_note_with_no_addenda_carries_its_mark_to_its_summaries_and_takes_it_off()
    {
        var (signer, patient, note) = await SignedAsync();
        var withdrawn = await ReleaseAsync(signer, patient, note, "Withdrawn one.");
        await WithdrawAsync(signer, withdrawn);
        var summary = await ReleaseAsync(signer, patient, note);
        Assert.Equal(["0"], await AsSuperuserAsync("SELECT count(*)::text FROM ehr.note_addenda WHERE note_id = @note", ("note", note)));

        await ChangeAsync(signer, "clinical_notes", note, sensitive: true, "Patient asked for it.");
        Assert.Equal([$"true true none {Inherited}"], await MarkOfAsync(summary));
        Assert.Equal([$"true true none {Inherited}"], await MarkOfAsync(withdrawn));

        await ChangeAsync(signer, "clinical_notes", note, sensitive: false, "Marked the wrong note.");
        Assert.Equal([$"false false none {Cleared}"], await MarkOfAsync(summary));
        Assert.Equal([$"false false none {Cleared}"], await MarkOfAsync(withdrawn));
    }

    [Fact]
    public async Task Marks_hide_a_summarys_values_from_an_administrator_in_the_audit_log()
    {
        var (clinician, patient, note) = await SignedAsync();
        var summary = await ReleaseAsync(clinician, patient, note);
        var admin = await Accounts.StaffAsync(database, StaffRoles.Admin);
        const string shown = """
            SELECT e.action || ' ' || e.sensitive || ' ' || CASE WHEN (SELECT d.id FROM ehr.shown_audit_details(ARRAY[e.id]) d) IS NULL THEN 'hidden' ELSE 'shown' END
            FROM ehr.audit_events e WHERE e.table_name = 'visit_summaries' AND e.row_id = @id ORDER BY e.occurred_at
            """;
        Assert.Equal(["insert false shown"], await Accounts.QueryAsync(App, admin, shown, ("id", summary)));

        await ChangeAsync(clinician, "visit_summaries", summary, sensitive: true, "Private.");

        // Its earlier values hidden from the administrator, and the marking itself shows none.
        Assert.Equal(["insert true hidden", "update true hidden"], await Accounts.QueryAsync(App, admin, shown, ("id", summary)));
        Assert.Equal([$"{clinician}"], await AsSuperuserAsync(
            "SELECT actor_id::text FROM ehr.audit_events WHERE table_name = 'visit_summaries' AND row_id = @id AND action = 'update'", ("id", summary)));
    }

    // When someone other than the clinician who released a summary withdraws it, the releaser gets an inbox item, as a
    // note's signer does for another clinician's addendum: no text of the summary or the reason. Nothing when the
    // releaser withdraws it, or when the releaser can no longer sign in.
    [Fact]
    public async Task The_releaser_is_told_when_someone_else_withdraws_their_summary()
    {
        var (releaser, patient, note) = await SignedAsync();
        var colleague = await Accounts.StaffAsync(database, StaffRoles.Clinician);
        await Accounts.AssignAsync(database, colleague, patient);
        const string items = "SELECT recipient_id || ' ' || kind || ' ' || source_table || ' ' || source_id || ' ' || summary || ' ' || patient_id FROM ehr.inbox_items WHERE patient_id = @patient ORDER BY created_at";

        await WithdrawAsync(releaser, await ReleaseAsync(releaser, patient, note));
        Assert.Empty(await AsSuperuserAsync(items, ("patient", patient)));

        await WithdrawAsync(colleague, await ReleaseAsync(releaser, patient, note), "A private reason.");
        Assert.Equal([$"{releaser} summary-withdrawn clinical_notes {note} A visit summary you released was withdrawn {patient}"], await AsSuperuserAsync(items, ("patient", patient)));
        Assert.Single(await Accounts.QueryAsync(App, releaser, "SELECT id::text FROM ehr.inbox_items WHERE kind = 'summary-withdrawn'"));
        Assert.Empty(await Accounts.QueryAsync(App, colleague, "SELECT id::text FROM ehr.inbox_items WHERE kind = 'summary-withdrawn'"));
        // The releaser marks it done.
        await Accounts.ExecuteAsync(App, releaser, "UPDATE ehr.inbox_items SET done_at = now() WHERE kind = 'summary-withdrawn' AND recipient_id = @me", ("me", releaser));

        // A releaser who can no longer sign in is not told.
        var gone = await ReleaseAsync(colleague, patient, note);
        await Accounts.ExecuteAsync(database.SuperuserConnectionString, null, "UPDATE ehr.staff_users SET lockout_end = now() + interval '10 years' WHERE id = @id", ("id", colleague));
        await WithdrawAsync(releaser, gone);
        Assert.Single(await AsSuperuserAsync(items, ("patient", patient)));
    }

    // Whether a summary just withdrawn had been viewed in the portal (Tahseen's decision of 21 November 2026: then the
    // patient is emailed): by the portal's record of a read of that summary by the patient's account or a proxy's. A
    // clinician's read doesn't count. Answered only for the summary the caller's own transaction withdrew, a savepoint
    // of it included, as the app withdraws one.
    [Fact]
    public async Task A_withdrawn_summary_was_viewed_only_when_the_portal_recorded_a_read_of_it()
    {
        var (clinician, patient, note) = await SignedAsync();
        var own = await Accounts.PatientAccountAsync(database, patient);
        var (proxy, grant) = await ProxyAsync(own, patient, includeSensitive: false);
        async Task<string> WithdrawAndAskAsync(Guid summary, Guid? asker = null, string? savepoint = null)
        {
            await using var connection = new NpgsqlConnection(App);
            await connection.OpenAsync(Cancel);
            await using var transaction = await connection.BeginTransactionAsync(Cancel);
            await using (var set = new NpgsqlCommand("SELECT set_config('app.user_id', @user, true)", connection, transaction))
            {
                set.Parameters.AddWithValue("user", clinician.ToString());
                await set.ExecuteNonQueryAsync(Cancel);
            }

            if (savepoint is not null)
            {
                await transaction.SaveAsync("withdrawing", Cancel);
            }

            await using (var withdraw = new NpgsqlCommand("UPDATE ehr.visit_summaries SET withdrawn_reason = 'Wrong visit.' WHERE id = @id", connection, transaction))
            {
                withdraw.Parameters.AddWithValue("id", summary);
                Assert.Equal(1, await withdraw.ExecuteNonQueryAsync(Cancel));
            }

            switch (savepoint)
            {
                case "released":
                    await transaction.ReleaseAsync("withdrawing", Cancel);
                    break;
                case "rolled back":
                    await transaction.RollbackAsync("withdrawing", Cancel);
                    break;
            }

            if (asker is { } other)
            {
                await using var set = new NpgsqlCommand("SELECT set_config('app.user_id', @user, true)", connection, transaction);
                set.Parameters.AddWithValue("user", other.ToString());
                await set.ExecuteNonQueryAsync(Cancel);
            }

            await using var ask = new NpgsqlCommand("SELECT ehr.withdrawn_summary_was_viewed(@id)::text", connection, transaction);
            ask.Parameters.AddWithValue("id", summary);
            var viewed = (string)(await ask.ExecuteScalarAsync(Cancel))!;
            await transaction.CommitAsync(Cancel);
            return viewed;
        }

        Task ViewAsync(Guid account, Guid? acting, Guid summary)
        {
            using (acting is { } chosen ? Accounts.ActingUnder(chosen) : null)
            {
                // As the portal records a summary it showed (PhiReader.VisitSummariesAsync).
                return Accounts.ExecuteAsync(App, account,
                    "INSERT INTO ehr.audit_events (id, occurred_at, actor_id, action, table_name, row_id, patient_id, source) VALUES (uuidv7(), now(), @actor, 'read', 'visit_summaries', @summary, @patient, 'onprem')",
                    ("actor", account), ("summary", summary), ("patient", patient));
            }
        }

        // Not viewed; viewed by a clinician only (their reads don't count).
        var unseen = await ReleaseAsync(clinician, patient, note);
        await Accounts.ExecuteAsync(App, clinician,
            "INSERT INTO ehr.audit_events (id, occurred_at, actor_id, action, table_name, row_id, patient_id, source) VALUES (uuidv7(), now(), @actor, 'read', 'visit_summaries', @summary, @patient, 'onprem')",
            ("actor", clinician), ("summary", unseen), ("patient", patient));
        Assert.Equal("false", await WithdrawAndAskAsync(unseen));

        var byPatient = await ReleaseAsync(clinician, patient, note);
        await ViewAsync(own, null, byPatient);
        Assert.Equal("true", await WithdrawAndAskAsync(byPatient));

        var byProxy = await ReleaseAsync(clinician, patient, note);
        await ViewAsync(proxy, grant, byProxy);
        Assert.Equal("true", await WithdrawAndAskAsync(byProxy));

        // Withdrawn under a savepoint, as the app withdraws one: once it is released, answered; rolled back, not withdrawn.
        var underSavepoint = await ReleaseAsync(clinician, patient, note);
        await ViewAsync(own, null, underSavepoint);
        Assert.Equal("true", await WithdrawAndAskAsync(underSavepoint, savepoint: "released"));
        var rolledBack = await ReleaseAsync(clinician, patient, note, "Rolled back.");
        await ViewAsync(own, null, rolledBack);
        Assert.Equal("false", await WithdrawAndAskAsync(rolledBack, savepoint: "rolled back"));
        await WithdrawAsync(clinician, rolledBack);

        // Somebody else asking about it, in the same transaction: no answer.
        var asked = await ReleaseAsync(clinician, patient, note);
        await ViewAsync(own, null, asked);
        Assert.Equal("false", await WithdrawAndAskAsync(asked, await Accounts.StaffAsync(database, StaffRoles.Clinician)));
        // Asked later, in another transaction, even by the clinician who withdrew it: no answer.
        Assert.Equal(["false"], await Accounts.QueryAsync(App, clinician, "SELECT ehr.withdrawn_summary_was_viewed(@id)::text", ("id", byPatient)));

        // Nor in a transaction of the same clinician's that was open while another of theirs withdrew it and committed:
        // the row isn't this transaction's.
        var elsewhere = await ReleaseAsync(clinician, patient, note);
        await ViewAsync(own, null, elsewhere);
        var other = await ReleaseAsync(clinician, patient, await NoteAsync(clinician, patient));
        await using (var connection = new NpgsqlConnection(App))
        {
            await connection.OpenAsync(Cancel);
            await using var transaction = await connection.BeginTransactionAsync(Cancel);
            await using (var set = new NpgsqlCommand("SELECT set_config('app.user_id', @user, true)", connection, transaction))
            {
                set.Parameters.AddWithValue("user", clinician.ToString());
                await set.ExecuteNonQueryAsync(Cancel);
            }

            // This transaction withdraws a summary of its own, so it has an ID of its own.
            await using (var withdraw = new NpgsqlCommand("UPDATE ehr.visit_summaries SET withdrawn_reason = 'Wrong visit.' WHERE id = @id", connection, transaction))
            {
                withdraw.Parameters.AddWithValue("id", other);
                Assert.Equal(1, await withdraw.ExecuteNonQueryAsync(Cancel));
            }

            await WithdrawAsync(clinician, elsewhere);
            await using var ask = new NpgsqlCommand("SELECT ehr.withdrawn_summary_was_viewed(@id)::text", connection, transaction);
            ask.Parameters.AddWithValue("id", elsewhere);
            Assert.Equal("false", (string)(await ask.ExecuteScalarAsync(Cancel))!);
            await transaction.CommitAsync(Cancel);
        }

        // The read role may not ask.
        await RefusedAsync(PostgresErrorCodes.InsufficientPrivilege, () => Accounts.QueryAsync(Read, clinician, "SELECT ehr.withdrawn_summary_was_viewed(@id)::text", ("id", byPatient)));
    }

    // A summary released at the very moment its note, or an addendum, changes: the two wait for each other under the
    // note's lock, so the summary ends with the sensitivity its sources have, whichever goes first. Two connections.
    [Theory]
    [InlineData("mark-note", true)]
    [InlineData("mark-note", false)]
    [InlineData("unmark-note", true)]
    [InlineData("unmark-note", false)]
    [InlineData("mark-addendum", true)]
    [InlineData("mark-addendum", false)]
    [InlineData("sign-sensitive-addendum", true)]
    [InlineData("sign-sensitive-addendum", false)]
    public async Task A_summary_released_while_its_note_or_an_addendum_changes_ends_with_their_sensitivity(string change, bool releasedFirst)
    {
        var (clinician, patient, note) = await SignedAsync(sensitive: change == "unmark-note");
        var addendum = await AddendumAsync(clinician, patient, note);
        var cancel = Cancel;
        async Task<(NpgsqlConnection, NpgsqlTransaction)> BeginAsync()
        {
            var connection = new NpgsqlConnection(App);
            await connection.OpenAsync(cancel);
            var transaction = await connection.BeginTransactionAsync(cancel);
            await using var set = new NpgsqlCommand("SELECT set_config('app.user_id', @user, true)", connection, transaction);
            set.Parameters.AddWithValue("user", clinician.ToString());
            await set.ExecuteNonQueryAsync(cancel);
            return (connection, transaction);
        }

        var (releasing, releasingTransaction) = await BeginAsync();
        var (changing, changingTransaction) = await BeginAsync();
        await using (releasing)
        await using (changing)
        {
            await using var release = new NpgsqlCommand(Insert, releasing, releasingTransaction);
            foreach (var (name, value) in new (string, object)[] { ("id", Guid.CreateVersion7()), ("note", note), ("patient", patient), ("talked", "Talked."), ("found", "Found."),
                ("next", "Next."), ("by", clinician), ("sensitive", false) })
            {
                release.Parameters.AddWithValue(name, value);
            }

            await using var alter = new NpgsqlCommand(change switch
            {
                "mark-note" => "UPDATE ehr.clinical_notes SET sensitive = true, sensitive_reason = 'Synthetic' WHERE id = @note",
                "unmark-note" => "UPDATE ehr.clinical_notes SET sensitive = false, sensitive_reason = 'Synthetic' WHERE id = @note",
                "mark-addendum" => "UPDATE ehr.note_addenda SET sensitive = true, sensitive_reason = 'Synthetic' WHERE id = @addendum",
                _ => "INSERT INTO ehr.note_addenda (id, note_id, patient_id, author_id, text, sensitive) VALUES (gen_random_uuid(), @note, @patient, @author, 'Synthetic sensitive addendum.', true)",
            }, changing, changingTransaction);
            foreach (var (name, value) in new (string, object)[] { ("note", note), ("addendum", addendum), ("patient", patient), ("author", clinician) })
            {
                alter.Parameters.AddWithValue(name, value);
            }

            var (first, firstTransaction, second, secondTransaction) = releasedFirst
                ? (release, releasingTransaction, alter, changingTransaction)
                : (alter, changingTransaction, release, releasingTransaction);

            Assert.Equal(1, await first.ExecuteNonQueryAsync(cancel));
            var waiting = second.ExecuteNonQueryAsync(cancel);
            // The second waits for the first's transaction.
            Assert.NotSame(waiting, await Task.WhenAny(waiting, Task.Delay(TimeSpan.FromMilliseconds(500), cancel)));
            await firstTransaction.CommitAsync(cancel);
            Assert.Equal(1, await waiting);
            await secondTransaction.CommitAsync(cancel);
        }

        var expected = change == "unmark-note" ? "false false" : "true true";
        Assert.Equal([expected], await AsSuperuserAsync(
            "SELECT s.sensitive || ' ' || s.sensitive_inherited FROM ehr.visit_summaries s WHERE s.note_id = @note", ("note", note)));
    }

    // A colleague withdraws the note's summary, and commits, while the signer's transaction is open; the signer then
    // marks the note, marks an addendum, signs a sensitive addendum or marks the summary itself. The change is theirs to
    // make: the withdrawn summary follows it, or is marked, and stays withdrawn by the colleague. Two connections.
    [Theory]
    [InlineData("mark-note")]
    [InlineData("mark-addendum")]
    [InlineData("sign-sensitive-addendum")]
    [InlineData("mark-summary")]
    public async Task A_change_after_a_colleagues_withdrawal_committed_while_the_transaction_was_open_is_made(string change)
    {
        var (signer, patient, note) = await SignedAsync();
        var colleague = await Accounts.StaffAsync(database, StaffRoles.Clinician);
        await Accounts.AssignAsync(database, colleague, patient);
        var addendum = await AddendumAsync(signer, patient, note);
        var summary = await ReleaseAsync(signer, patient, note);

        await using (var connection = new NpgsqlConnection(App))
        {
            await connection.OpenAsync(Cancel);
            await using var transaction = await connection.BeginTransactionAsync(Cancel);
            await using (var set = new NpgsqlCommand("SELECT set_config('app.user_id', @user, true)", connection, transaction))
            {
                set.Parameters.AddWithValue("user", signer.ToString());
                await set.ExecuteNonQueryAsync(Cancel);
            }

            // Withdrawn by the colleague, committed after the signer's transaction began.
            await WithdrawAsync(colleague, summary);

            await using var alter = new NpgsqlCommand(change switch
            {
                "mark-note" => "UPDATE ehr.clinical_notes SET sensitive = true, sensitive_reason = 'Synthetic' WHERE id = @note",
                "mark-addendum" => "UPDATE ehr.note_addenda SET sensitive = true, sensitive_reason = 'Synthetic' WHERE id = @addendum",
                "sign-sensitive-addendum" => "INSERT INTO ehr.note_addenda (id, note_id, patient_id, author_id, text, sensitive) VALUES (gen_random_uuid(), @note, @patient, @author, 'Synthetic sensitive addendum.', true)",
                _ => "UPDATE ehr.visit_summaries SET sensitive = true, sensitive_reason = 'Synthetic' WHERE id = @summary",
            }, connection, transaction);
            foreach (var (name, value) in new (string, object)[] { ("note", note), ("addendum", addendum), ("patient", patient), ("author", signer), ("summary", summary) })
            {
                alter.Parameters.AddWithValue(name, value);
            }

            Assert.Equal(1, await alter.ExecuteNonQueryAsync(Cancel));
            await transaction.CommitAsync(Cancel);
        }

        Assert.Equal([change == "mark-summary" ? $"true false {signer} Synthetic" : $"true true none {Inherited}"], await MarkOfAsync(summary));
        Assert.Equal([$"{colleague}"], await AsSuperuserAsync("SELECT withdrawn_by::text FROM ehr.visit_summaries WHERE id = @id", ("id", summary)));
    }

    // Under a snapshot older than the note's lock, the sensitivity of the note and its addenda could be read as it was
    // before a change the lock waited for: releasing a summary, and changing a note's or an addendum's sensitivity or
    // signing a sensitive addendum, are refused outside READ COMMITTED.
    [Fact]
    public async Task Releasing_and_changing_a_sources_sensitivity_need_READ_COMMITTED()
    {
        var (clinician, patient, note) = await SignedAsync();
        var addendum = await AddendumAsync(clinician, patient, note);
        async Task UnderRepeatableReadAsync(string sql, params (string Name, object Value)[] parameters)
        {
            await using var connection = new NpgsqlConnection(App);
            await connection.OpenAsync(Cancel);
            await using var transaction = await connection.BeginTransactionAsync(System.Data.IsolationLevel.RepeatableRead, Cancel);
            await using (var user = new NpgsqlCommand("SELECT set_config('app.user_id', @user, true)", connection, transaction))
            {
                user.Parameters.AddWithValue("user", clinician.ToString());
                await user.ExecuteNonQueryAsync(Cancel);
            }

            await using var command = new NpgsqlCommand(sql, connection, transaction);
            foreach (var (name, value) in parameters)
            {
                command.Parameters.AddWithValue(name, value);
            }

            await command.ExecuteNonQueryAsync(Cancel);
            await transaction.CommitAsync(Cancel);
        }

        await RefusedAsync(PostgresErrorCodes.FeatureNotSupported, () => UnderRepeatableReadAsync(Insert,
            ("id", Guid.CreateVersion7()), ("note", note), ("patient", patient), ("talked", "Talked."), ("found", "Found."), ("next", "Next."), ("by", clinician), ("sensitive", false)));
        await RefusedAsync(PostgresErrorCodes.FeatureNotSupported, () => UnderRepeatableReadAsync(
            "UPDATE ehr.clinical_notes SET sensitive = true, sensitive_reason = 'Synthetic' WHERE id = @note", ("note", note)));
        await RefusedAsync(PostgresErrorCodes.FeatureNotSupported, () => UnderRepeatableReadAsync(
            "UPDATE ehr.note_addenda SET sensitive = true, sensitive_reason = 'Synthetic' WHERE id = @addendum", ("addendum", addendum)));
        await RefusedAsync(PostgresErrorCodes.FeatureNotSupported, () => UnderRepeatableReadAsync(
            "INSERT INTO ehr.note_addenda (id, note_id, patient_id, author_id, text, sensitive) VALUES (gen_random_uuid(), @note, @patient, @author, 'Synthetic.', true)",
            ("note", note), ("patient", patient), ("author", clinician)));
        Assert.Equal(["false 0 1"], await AsSuperuserAsync(
            "SELECT n.sensitive || ' ' || (SELECT count(*) FROM ehr.visit_summaries s WHERE s.note_id = n.id) || ' ' || (SELECT count(*) FROM ehr.note_addenda a WHERE a.note_id = n.id) FROM ehr.clinical_notes n WHERE n.id = @note",
            ("note", note)));
    }
}
