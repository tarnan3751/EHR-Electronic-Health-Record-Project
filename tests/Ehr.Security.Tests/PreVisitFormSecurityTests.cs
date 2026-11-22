using System.Text.Json;
using System.Text.RegularExpressions;
using Ehr.Data.Accounts;
using Ehr.Testing;
using Npgsql;
using Xunit;

namespace Ehr.Security.Tests;

// The expansion plan's step 13e: what the database lets each account do with pre-visit forms, as the runtime roles, in
// SQL of its own (db/policies/10-grants.sql, 30-rls.sql, 40-audit.sql and 85-pre-visit-forms.sql). A clinician asks,
// for a patient on their panel, before a booked appointment ends; the patient, or a proxy allowed sensitive items,
// answers once, from 7 days before the appointment until its scheduled end, or, checked in, while its visit is in
// progress, up to 12 hours after; the database scores the answers whatever the client
// sent, and a refusal never says why; a proxy without sensitive items finds no forms at all; nothing changes a form;
// an answer that needs review reaches the care team's inbox and no one else, and no admin sees a form's values.
public partial class PreVisitFormSecurityTests(MigratedDatabase database) : IClassFixture<MigratedDatabase>
{
    const string Phq9 = "44249-1";
    const string Gad7 = "69737-5";

    // The answer codes of both questionnaires' scored questions, by score (LOINC LL358-3).
    static readonly string[] Frequency = ["LA6568-5", "LA6569-3", "LA6570-1", "LA6571-9"];

    static readonly string[] PhqItems = ["44250-9", "44255-8", "44259-0", "44254-1", "44251-7", "44258-2", "44252-5", "44253-3", "44260-8"];

    static readonly string[] GadItems = ["69725-0", "68509-9", "69733-4", "69734-2", "69735-9", "69689-8", "69736-7"];

    string App => database.ConnectionStringFor("ehr_app", MigratedDatabase.AppPassword);

    string Read => database.ConnectionStringFor("ehr_read", MigratedDatabase.ReadPassword);

    static CancellationToken Cancel => TestContext.Current.CancellationToken;

    // Answers with each scored question given the score at its place (and the PHQ-9's difficulty question, when given).
    static string Answers(string[] items, IReadOnlyList<int> scores, string? difficulty = null)
    {
        var answers = items.Select((item, place) => (item, Frequency[scores[place]])).ToDictionary(pair => pair.item, pair => pair.Item2);
        if (difficulty is not null)
        {
            answers["69722-7"] = difficulty;
        }

        return JsonSerializer.Serialize(answers);
    }

    static string Phq(params int[] scores) => Answers(PhqItems, scores);

    sealed record Booking(Guid Clinician, Guid Patient, Guid Account, Guid Appointment, Guid AppointmentClinician);

    // A patient on a clinician's panel, with a portal account and an appointment with another clinician, starting
    // `start` from now, booked by the first.
    async Task<Booking> BookedAsync(TimeSpan start, int minutes = 20, Guid? clinician = null, Guid? patient = null)
    {
        var asking = clinician ?? await Accounts.StaffAsync(database, StaffRoles.Clinician);
        var who = patient ?? await Accounts.PatientAsync(database, asking);
        if (patient is null)
        {
            await Accounts.AssignAsync(database, asking, who);
        }

        var account = patient is null ? await Accounts.PatientAccountAsync(database, who) : Guid.Empty;
        var seeing = await Accounts.StaffAsync(database, StaffRoles.Clinician);
        var appointment = await BookAsync(asking, who, seeing, start, minutes);
        return new Booking(asking, who, account, appointment, seeing);
    }

    async Task<Guid> BookAsync(Guid by, Guid patient, Guid with, TimeSpan start, int minutes = 20)
    {
        var id = Guid.CreateVersion7();
        await Accounts.ExecuteAsync(App, by,
            """
            INSERT INTO ehr.appointments (id, patient_id, clinician_id, starts_at, minutes, reason, status)
            VALUES (@id, @patient, @clinician, now() + @start, @minutes, 'Synthetic: check-up', 'booked')
            """,
            ("id", id), ("patient", patient), ("clinician", with), ("start", start), ("minutes", minutes));
        return id;
    }

    // Asked for as `user`, with a patient, a clinician and a definition the database replaces.
    async Task<Guid> AskAsync(Guid? user, Guid appointment, string code = Phq9, string? connection = null)
    {
        var id = Guid.CreateVersion7();
        await Accounts.ExecuteAsync(connection ?? App, user,
            """
            INSERT INTO ehr.questionnaire_requests (id, patient_id, appointment_id, clinician_id, questionnaire_code, definition, requested_by, requested_at)
            VALUES (@id, gen_random_uuid(), @appointment, gen_random_uuid(), @code, '{"items": []}', gen_random_uuid(), '2000-01-01')
            """,
            ("id", id), ("appointment", appointment), ("code", code));
        return id;
    }

    // Answered as `user` (under `grant`, for a proxy), with a score, a band, who and when the database replaces.
    async Task<Guid> AnswerAsync(Guid? user, Guid request, string answers, Guid? grant = null, string? connection = null)
    {
        var id = Guid.CreateVersion7();
        const string Insert = """
            INSERT INTO ehr.questionnaire_responses (id, request_id, patient_id, questionnaire_code, clinician_id, answers, score, band, follow_up,
                alert, answered_by, answered_at)
            VALUES (@id, @request, gen_random_uuid(), 'forged', gen_random_uuid(), @answers::jsonb, 0, 'Minimal', false, false, gen_random_uuid(), '2000-01-01')
            """;
        (string, object)[] parameters = [("id", id), ("request", request), ("answers", answers)];
        if (grant is { } acting)
        {
            using (Accounts.ActingUnder(acting))
            {
                await Accounts.ExecuteAsync(connection ?? App, user, Insert, parameters);
            }
        }
        else
        {
            await Accounts.ExecuteAsync(connection ?? App, user, Insert, parameters);
        }

        return id;
    }

    Task<List<string>> AsSuperuserAsync(string sql, params (string Name, object Value)[] parameters) =>
        Accounts.QueryAsync(database.SuperuserConnectionString, null, sql, parameters);

    async Task<List<string>> AsAsync(string connection, Guid? user, string sql, Guid? grant = null, params (string Name, object Value)[] parameters)
    {
        if (grant is { } acting)
        {
            using (Accounts.ActingUnder(acting))
            {
                return await Accounts.QueryAsync(connection, user, sql, parameters);
            }
        }

        return await Accounts.QueryAsync(connection, user, sql, parameters);
    }

    // A proxy grant to `email` for the booking's patient, with or without sensitive items: given by the patient, who
    // alone allows sensitive items, or else by the clinician.
    async Task<Guid> GrantAsync(Booking booking, string email, bool sensitive)
    {
        var id = Guid.CreateVersion7();
        await Accounts.ExecuteAsync(App, sensitive ? booking.Account : booking.Clinician,
            """
            INSERT INTO ehr.proxy_grants (id, patient_id, proxy_email, scope, include_sensitive, expires_at, granted_by, granted_at, granted_by_kind)
            VALUES (@id, @patient, @email, 'record', @sensitive, now() + interval '30 days', gen_random_uuid(), '2000-01-01', 'staff')
            """,
            ("id", id), ("patient", booking.Patient), ("email", email), ("sensitive", sensitive));
        return id;
    }

    static async Task<PostgresException> RefusedAsync(string state, Func<Task> attempt)
    {
        var refused = await Assert.ThrowsAsync<PostgresException>(attempt);
        Assert.Equal(state, refused.SqlState);
        return refused;
    }

    // The response guard's one refusal: whatever the reason, the same code and the same words.
    static async Task NotAnswerableAsync(Func<Task> attempt)
    {
        var refused = await RefusedAsync(PostgresErrorCodes.InsufficientPrivilege, attempt);
        Assert.Equal("The form can't be answered", refused.MessageText);
    }

    [Fact]
    public async Task A_clinician_asks_for_a_patient_on_their_panel_and_the_database_fills_in_the_rest()
    {
        var booking = await BookedAsync(TimeSpan.FromDays(2));

        var request = await AskAsync(booking.Clinician, booking.Appointment);

        // The patient and the clinician from the appointment, who asked and when, and a copy of the questionnaire as it
        // is now, whatever was sent; always sensitive.
        Assert.Equal([$"{booking.Patient} {booking.AppointmentClinician} {booking.Clinician} true true true"], await AsSuperuserAsync(
            """
            SELECT r.patient_id || ' ' || r.clinician_id || ' ' || r.requested_by || ' ' || (r.requested_at > now() - interval '1 minute')
                || ' ' || (r.definition = q.definition) || ' ' || r.sensitive
            FROM ehr.questionnaire_requests r JOIN ehr.questionnaires q ON q.code = r.questionnaire_code WHERE r.id = @id
            """, ("id", request)));
    }

    [Fact]
    public async Task Asking_is_refused_off_the_panel_after_the_appointment_twice_or_by_anyone_but_a_clinician()
    {
        var booking = await BookedAsync(TimeSpan.FromDays(2));
        var outsider = await Accounts.StaffAsync(database, StaffRoles.Clinician);
        var admin = await Accounts.StaffAsync(database, StaffRoles.Admin);

        // Off the panel: the policy. The patient, an admin and nobody can't read the appointment: the guard finds none.
        await RefusedAsync(PostgresErrorCodes.InsufficientPrivilege, () => AskAsync(outsider, booking.Appointment));
        foreach (var user in new Guid?[] { booking.Account, admin, null })
        {
            await Assert.ThrowsAsync<PostgresException>(() => AskAsync(user, booking.Appointment));
        }

        // The read role has no INSERT.
        await RefusedAsync(PostgresErrorCodes.InsufficientPrivilege, () => AskAsync(booking.Clinician, booking.Appointment, connection: Read));

        // Once for each questionnaire: the GAD-7 beside the PHQ-9 is fine, a second PHQ-9 isn't.
        await AskAsync(booking.Clinician, booking.Appointment);
        await AskAsync(booking.Clinician, booking.Appointment, Gad7);
        await RefusedAsync(PostgresErrorCodes.UniqueViolation, () => AskAsync(booking.Clinician, booking.Appointment));

        // Not a questionnaire of the clinic's.
        var other = await BookAsync(booking.Clinician, booking.Patient, booking.AppointmentClinician, TimeSpan.FromDays(3));
        await RefusedAsync(PostgresErrorCodes.ForeignKeyViolation, () => AskAsync(booking.Clinician, other, "12345-6"));

        // An appointment that has ended, and one cancelled.
        var ended = await BookAsync(booking.Clinician, booking.Patient, booking.AppointmentClinician, TimeSpan.FromHours(-2));
        await RefusedAsync(PostgresErrorCodes.CheckViolation, () => AskAsync(booking.Clinician, ended));
        await Accounts.ExecuteAsync(App, booking.Clinician, "UPDATE ehr.appointments SET status = 'cancelled', cancel_reason = 'Synthetic' WHERE id = @id", ("id", other));
        await RefusedAsync(PostgresErrorCodes.CheckViolation, () => AskAsync(booking.Clinician, other));
    }

    [Fact]
    public async Task The_patient_answers_once_and_the_database_scores_it_whatever_the_client_sent()
    {
        var booking = await BookedAsync(TimeSpan.FromDays(2));
        var request = await AskAsync(booking.Clinician, booking.Appointment);

        // 3 + 2 + 2 + 1 + 1 + 1 + 1 + 1 + 0 = 12: Moderate, needs follow-up, no alert. Sent as 0, Minimal, by someone
        // else, long ago, for another patient.
        var response = await AnswerAsync(booking.Account, request, Phq(3, 2, 2, 1, 1, 1, 1, 1, 0));

        Assert.Equal([$"12 Moderate true false {booking.Account} none {booking.Patient} {Phq9} {booking.AppointmentClinician} true true"], await AsSuperuserAsync(
            """
            SELECT score || ' ' || band || ' ' || follow_up || ' ' || alert || ' ' || answered_by || ' ' || coalesce(proxy_grant_id::text, 'none')
                || ' ' || patient_id || ' ' || questionnaire_code || ' ' || clinician_id || ' ' || (answered_at > now() - interval '1 minute') || ' ' || sensitive
            FROM ehr.questionnaire_responses WHERE id = @id
            """, ("id", response)));

        // Once: a second answer to the same request is refused, by the unique index.
        await RefusedAsync(PostgresErrorCodes.UniqueViolation, () => AnswerAsync(booking.Account, request, Phq(0, 0, 0, 0, 0, 0, 0, 0, 0)));

        // The read role has no INSERT; a clinician, an admin and nobody answer nothing.
        var other = await AskAsync(booking.Clinician, booking.Appointment, Gad7);
        await RefusedAsync(PostgresErrorCodes.InsufficientPrivilege, () => AnswerAsync(booking.Account, other, Answers(GadItems, [0, 0, 0, 0, 0, 0, 0]), connection: Read));
        var admin = await Accounts.StaffAsync(database, StaffRoles.Admin);
        await RefusedAsync(PostgresErrorCodes.InsufficientPrivilege, () => AnswerAsync(booking.Clinician, other, Answers(GadItems, [0, 0, 0, 0, 0, 0, 0])));
        await NotAnswerableAsync(() => AnswerAsync(admin, other, Answers(GadItems, [0, 0, 0, 0, 0, 0, 0])));
        await NotAnswerableAsync(() => AnswerAsync(null, other, Answers(GadItems, [0, 0, 0, 0, 0, 0, 0])));
    }

    [Fact]
    public async Task Answers_that_arent_the_forms_are_refused_and_the_difficulty_question_is_optional_and_unscored()
    {
        var booking = await BookedAsync(TimeSpan.FromDays(2));
        var request = await AskAsync(booking.Clinician, booking.Appointment);
        var all = PhqItems.ToDictionary(item => item, _ => (object)Frequency[1]);

        // A question left out; a question the form hasn't; an answer that isn't one of its question's (a GAD-7 code
        // where only the difficulty list's will do, an unknown one, a number, an empty one); not an object at all.
        var missing = new Dictionary<string, object>(all);
        missing.Remove("44260-8");
        foreach (var answers in new object[]
                 {
                     missing,
                     new Dictionary<string, object>(all) { ["69725-0"] = Frequency[0] },
                     new Dictionary<string, object>(all) { ["69722-7"] = Frequency[0] },
                     new Dictionary<string, object>(all) { ["44250-9"] = "LA0000-0" },
                     new Dictionary<string, object>(all) { ["44250-9"] = 3 },
                     new Dictionary<string, object>(all) { ["44250-9"] = "" },
                     new[] { Frequency[0] },
                 })
        {
            await RefusedAsync(PostgresErrorCodes.CheckViolation, () => AnswerAsync(booking.Account, request, JsonSerializer.Serialize(answers)));
        }

        Assert.Empty(await AsSuperuserAsync("SELECT id FROM ehr.questionnaire_responses WHERE request_id = @id", ("id", request)));

        // The difficulty question answered "Extremely difficult": the total is the nine questions' alone.
        await AnswerAsync(booking.Account, request, Answers(PhqItems, [1, 1, 1, 1, 1, 1, 1, 1, 0], "LA6574-3"));
        Assert.Equal(["8 Mild false"], await AsSuperuserAsync(
            "SELECT score || ' ' || band || ' ' || follow_up FROM ehr.questionnaire_responses WHERE request_id = @id", ("id", request)));
    }

    // Every total each questionnaire can have, from the database's own scoring of the clinic's definitions: the band
    // it falls in, follow-up at 10 and over, and an alert only for the PHQ-9's question 9 answered other than "Not at
    // all", whatever the total.
    [Fact]
    public async Task Every_total_gets_its_band_and_follow_up_and_only_question_9_alerts()
    {
        string[] phqBands = [.. Enumerable.Range(0, 28).Select(total => total switch
        {
            <= 4 => "Minimal", <= 9 => "Mild", <= 14 => "Moderate", <= 19 => "Moderately severe", _ => "Severe",
        })];
        string[] gadBands = [.. Enumerable.Range(0, 22).Select(total => total switch { <= 4 => "Below mild", <= 9 => "Mild", <= 14 => "Moderate", _ => "Severe" })];

        foreach (var (code, items, bands) in new[] { (Phq9, PhqItems, phqBands), (Gad7, GadItems, gadBands) })
        {
            for (var total = 0; total < bands.Length; total++)
            {
                // The total spread from the first question on, so the last question (the PHQ-9's 9) is 0 until 25.
                var scores = items.Select((_, place) => Math.Clamp(total - (3 * place), 0, 3)).ToArray();
                Assert.Equal([$"{total} {bands[total]} {total >= 10} {code == Phq9 && scores[^1] > 0}"],
                    await ScoreAsync(code, Answers(items, scores)));
            }
        }

        // Question 9 alone, at each answer: an alert at every one but "Not at all", even at a total of 1.
        for (var score = 0; score <= 3; score++)
        {
            Assert.Equal([$"{score} {phqBands[score]} False {score > 0}"], await ScoreAsync(Phq9, Phq(0, 0, 0, 0, 0, 0, 0, 0, score)));
        }
    }

    async Task<List<string>> ScoreAsync(string code, string answers) => await AsAsync(App, null,
        """
        SELECT s.score || ' ' || s.band || ' ' || initcap(s.follow_up::text) || ' ' || initcap(s.alert::text)
        FROM ehr.questionnaires q, ehr.questionnaire_score(q.definition, @answers::jsonb) s WHERE q.code = @code
        """, null, ("code", code), ("answers", answers));

    // A clinic's own questionnaire is scored by its own definition: data, not code.
    [Fact]
    public async Task A_questionnaire_the_clinic_defines_is_scored_by_its_own_definition()
    {
        const string Definition = """
            {"prompt": "Synthetic", "items": [
                {"code": "A", "text": "First", "answers": "yesNo", "scored": true},
                {"code": "B", "text": "Second", "answers": "yesNo", "scored": true, "alert": true},
                {"code": "C", "text": "Third", "answers": "yesNo", "optional": true}],
             "answerLists": {"yesNo": [{"code": "N", "text": "No", "score": 0}, {"code": "Y", "text": "Yes", "score": 5}]},
             "total": {"code": "T", "max": 10}, "bands": [{"from": 0, "to": 4, "band": "Low"}, {"from": 5, "to": 10, "band": "High"}],
             "followUpAt": 10}
            """;

        async Task<List<string>> Score(string answers) => await AsAsync(App, null,
            "SELECT s.score || ' ' || s.band || ' ' || s.follow_up || ' ' || s.alert FROM ehr.questionnaire_score(@definition::jsonb, @answers::jsonb) s",
            null, ("definition", Definition), ("answers", answers));

        Assert.Equal(["5 High false false"], await Score("""{"A": "Y", "B": "N"}"""));
        Assert.Equal(["10 High true true"], await Score("""{"A": "Y", "B": "Y", "C": "N"}"""));
        await RefusedAsync(PostgresErrorCodes.CheckViolation, () => Score("""{"A": "Y"}"""));
    }

    [Fact]
    public async Task A_form_opens_seven_days_before_the_appointment_and_closes_at_its_scheduled_end_or_a_late_visits_on_the_databases_clock()
    {
        var booking = await BookedAsync(TimeSpan.FromDays(7).Subtract(TimeSpan.FromMinutes(2)));
        var answers = Phq(0, 0, 0, 0, 0, 0, 0, 0, 0);

        // Each appointment with a clinician of its own, so none overlaps another.
        Task<Guid> With() => Accounts.StaffAsync(database, StaffRoles.Clinician);

        // Just inside seven days: open.
        await AnswerAsync(booking.Account, await AskAsync(booking.Clinician, booking.Appointment), answers);

        // Just outside: asked for, not open yet.
        var early = await BookAsync(booking.Clinician, booking.Patient, await With(), TimeSpan.FromDays(7).Add(TimeSpan.FromMinutes(2)));
        await NotAnswerableAsync(async () => await AnswerAsync(booking.Account, await AskAsync(booking.Clinician, early), answers));

        // Under way, ten minutes into twenty: open. Checked in: still open until its end.
        var now = await BookAsync(booking.Clinician, booking.Patient, await With(), TimeSpan.FromMinutes(-10));
        var nowRequest = await AskAsync(booking.Clinician, now);
        var visit = Guid.CreateVersion7();
        await Accounts.ExecuteAsync(App, booking.Clinician,
            "INSERT INTO ehr.encounters (id, patient_id, status, class_code, period_start) VALUES (@id, @patient, 'in-progress', 'AMB', now())",
            ("id", visit), ("patient", booking.Patient));
        await Accounts.ExecuteAsync(App, booking.Clinician, "UPDATE ehr.appointments SET status = 'fulfilled', encounter_id = @visit WHERE id = @id",
            ("id", now), ("visit", visit));
        await AnswerAsync(booking.Account, nowRequest, answers);

        // Checked in, and its scheduled end passed while the visit is still in progress: open for 12 hours after the
        // scheduled end, and closed after them; and closed once the visit has ended. (The scheduled end is moved back as
        // the superuser, with the appointment's guard off for the statement, since nothing changes an appointment once it
        // is checked in.)
        async Task<(Guid First, Guid Second, Guid Visit)> CheckedInAsync(TimeSpan endedAgo)
        {
            var appointment = await BookAsync(booking.Clinician, booking.Patient, await With(), TimeSpan.FromMinutes(-5));
            var first = await AskAsync(booking.Clinician, appointment);
            var second = await AskAsync(booking.Clinician, appointment, Gad7);
            var started = Guid.CreateVersion7();
            await Accounts.ExecuteAsync(App, booking.Clinician,
                "INSERT INTO ehr.encounters (id, patient_id, status, class_code, period_start) VALUES (@id, @patient, 'in-progress', 'AMB', now())",
                ("id", started), ("patient", booking.Patient));
            await Accounts.ExecuteAsync(App, booking.Clinician, "UPDATE ehr.appointments SET status = 'fulfilled', encounter_id = @visit WHERE id = @id",
                ("id", appointment), ("visit", started));
            await Accounts.ExecuteAsync(database.SuperuserConnectionString, null,
                "ALTER TABLE ehr.appointments DISABLE TRIGGER appointments_guard; "
                + "UPDATE ehr.appointments SET starts_at = now() - @ago - make_interval(mins => minutes) WHERE id = @id; "
                + "ALTER TABLE ehr.appointments ENABLE TRIGGER appointments_guard",
                ("id", appointment), ("ago", endedAgo));
            return (first, second, started);
        }

        var late = await CheckedInAsync(TimeSpan.FromHours(2));
        await AnswerAsync(booking.Account, late.First, answers);
        await Accounts.ExecuteAsync(database.SuperuserConnectionString, null, "UPDATE ehr.encounters SET status = 'finished', period_end = now() WHERE id = @id",
            ("id", late.Visit));
        await NotAnswerableAsync(() => AnswerAsync(booking.Account, late.Second, Answers(GadItems, [0, 0, 0, 0, 0, 0, 0])));

        // Still in progress: open just inside 12 hours after the scheduled end, closed just outside.
        await AnswerAsync(booking.Account, (await CheckedInAsync(TimeSpan.FromHours(12).Subtract(TimeSpan.FromMinutes(5)))).First, answers);
        var over = await CheckedInAsync(TimeSpan.FromHours(12).Add(TimeSpan.FromMinutes(5)));
        await NotAnswerableAsync(() => AnswerAsync(booking.Account, over.First, answers));

        // Rescheduled so that it has ended; cancelled; a no-show: closed.
        foreach (var change in new[]
                 {
                     "UPDATE ehr.appointments SET starts_at = now() - interval '1 hour' WHERE id = @id",
                     "UPDATE ehr.appointments SET status = 'cancelled', cancel_reason = 'Synthetic' WHERE id = @id",
                     "UPDATE ehr.appointments SET status = 'noshow' WHERE id = @id",
                 })
        {
            var appointment = await BookAsync(booking.Clinician, booking.Patient, await With(), TimeSpan.FromMinutes(-5));
            var request = await AskAsync(booking.Clinician, appointment);
            await Accounts.ExecuteAsync(App, booking.Clinician, change, ("id", appointment));
            await NotAnswerableAsync(() => AnswerAsync(booking.Account, request, answers));
        }
    }

    // A refusal says nothing of why: a request that doesn't exist, another patient's, one a proxy without sensitive
    // items may not see, one not open yet and one closed all get the same code and the same words.
    [Fact]
    public async Task Every_refusal_to_answer_is_the_same_refusal()
    {
        var booking = await BookedAsync(TimeSpan.FromDays(2));
        var request = await AskAsync(booking.Clinician, booking.Appointment);
        var stranger = await BookedAsync(TimeSpan.FromDays(2));
        var theirs = await AskAsync(stranger.Clinician, stranger.Appointment);
        var proxy = await Accounts.ProxyOnlyAccountAsync(database);
        var grant = await GrantAsync(booking, proxy.Email, sensitive: false);
        var later = await AskAsync(booking.Clinician, await BookAsync(booking.Clinician, booking.Patient, booking.AppointmentClinician, TimeSpan.FromDays(30)));
        var closed = await BookAsync(booking.Clinician, booking.Patient, booking.AppointmentClinician, TimeSpan.FromDays(3));
        var closedRequest = await AskAsync(booking.Clinician, closed);
        await Accounts.ExecuteAsync(App, booking.Clinician, "UPDATE ehr.appointments SET status = 'cancelled', cancel_reason = 'Synthetic' WHERE id = @id", ("id", closed));
        var answers = Phq(0, 0, 0, 0, 0, 0, 0, 0, 0);

        await NotAnswerableAsync(() => AnswerAsync(booking.Account, Guid.CreateVersion7(), answers));
        await NotAnswerableAsync(() => AnswerAsync(booking.Account, theirs, answers));
        await NotAnswerableAsync(() => AnswerAsync(proxy.Id, request, answers, grant));
        await NotAnswerableAsync(() => AnswerAsync(booking.Account, later, answers));
        await NotAnswerableAsync(() => AnswerAsync(booking.Account, closedRequest, answers));
    }

    [Fact]
    public async Task A_proxy_answers_only_with_sensitive_items_and_is_recorded_as_themselves_and_without_them_finds_no_forms()
    {
        var booking = await BookedAsync(TimeSpan.FromDays(2));
        var request = await AskAsync(booking.Clinician, booking.Appointment);
        var allowed = await Accounts.ProxyOnlyAccountAsync(database);
        var withSensitive = await GrantAsync(booking, allowed.Email, sensitive: true);
        var notAllowed = await Accounts.ProxyOnlyAccountAsync(database);
        var withoutSensitive = await GrantAsync(booking, notAllowed.Email, sensitive: false);
        const string Forms = "SELECT (SELECT count(*) FROM ehr.questionnaire_requests) || ' ' || (SELECT count(*) FROM ehr.questionnaire_responses)";

        // Without sensitive items the record has no forms, before and after it has answers, on both servers.
        foreach (var connection in new[] { App, Read })
        {
            Assert.Equal(["1 0"], await AsAsync(connection, allowed.Id, Forms, withSensitive));
            Assert.Equal(["0 0"], await AsAsync(connection, notAllowed.Id, Forms, withoutSensitive));
        }

        var response = await AnswerAsync(allowed.Id, request, Phq(1, 1, 0, 0, 0, 0, 0, 0, 0), withSensitive);
        Assert.Equal([$"{allowed.Id} {withSensitive}"], await AsSuperuserAsync(
            "SELECT answered_by || ' ' || proxy_grant_id FROM ehr.questionnaire_responses WHERE id = @id", ("id", response)));
        foreach (var connection in new[] { App, Read })
        {
            Assert.Equal(["1 1"], await AsAsync(connection, allowed.Id, Forms, withSensitive));
            Assert.Equal(["0 0"], await AsAsync(connection, notAllowed.Id, Forms, withoutSensitive));
        }

        // Naming no grant, the proxy-only account is nobody's patient; naming the grant of the other proxy, nothing.
        Assert.Equal(["0 0"], await AsAsync(App, allowed.Id, Forms));
        Assert.Equal(["0 0"], await AsAsync(App, notAllowed.Id, Forms, withSensitive));

        // A grant that is removed shows nothing more and answers nothing.
        var other = await AskAsync(booking.Clinician, booking.Appointment, Gad7);
        await Accounts.ExecuteAsync(App, booking.Clinician, "UPDATE ehr.proxy_grants SET revoked_at = now() WHERE id = @id", ("id", withSensitive));
        Assert.Equal(["0 0"], await AsAsync(App, allowed.Id, Forms, withSensitive));
        await NotAnswerableAsync(() => AnswerAsync(allowed.Id, other, Answers(GadItems, [0, 0, 0, 0, 0, 0, 0]), withSensitive));
    }

    [Fact]
    public async Task Clinicians_read_every_form_the_patient_their_own_and_nobody_else_any()
    {
        var booking = await BookedAsync(TimeSpan.FromDays(2));
        await AnswerAsync(booking.Account, await AskAsync(booking.Clinician, booking.Appointment), Phq(0, 0, 0, 0, 0, 0, 0, 0, 0));
        var other = await BookedAsync(TimeSpan.FromDays(2));
        await AnswerAsync(other.Account, await AskAsync(other.Clinician, other.Appointment), Phq(0, 0, 0, 0, 0, 0, 0, 0, 0));
        var outsider = await Accounts.StaffAsync(database, StaffRoles.Clinician);
        var admin = await Accounts.StaffAsync(database, StaffRoles.Admin);
        const string Mine = """
            SELECT (SELECT count(*) FROM ehr.questionnaire_requests WHERE patient_id = @patient)
                || ' ' || (SELECT count(*) FROM ehr.questionnaire_responses WHERE patient_id = @patient)
            """;

        foreach (var connection in new[] { App, Read })
        {
            // Every clinician, as they read charts.
            Assert.Equal(["1 1"], await AsAsync(connection, outsider, Mine, null, ("patient", booking.Patient)));
            // The patient: their own and no one else's.
            Assert.Equal(["1 1"], await AsAsync(connection, booking.Account, Mine, null, ("patient", booking.Patient)));
            Assert.Equal(["0 0"], await AsAsync(connection, booking.Account, Mine, null, ("patient", other.Patient)));
            // An admin and nobody: none.
            Assert.Equal(["0 0"], await AsAsync(connection, admin, Mine, null, ("patient", booking.Patient)));
            Assert.Equal(["0 0"], await AsAsync(connection, null, Mine, null, ("patient", booking.Patient)));
        }
    }

    [Fact]
    public async Task A_form_never_changes_for_anyone_the_owner_included()
    {
        var booking = await BookedAsync(TimeSpan.FromDays(2));
        var request = await AskAsync(booking.Clinician, booking.Appointment);
        var response = await AnswerAsync(booking.Account, request, Phq(0, 0, 0, 0, 0, 0, 0, 0, 0));

        foreach (var (table, id) in new[] { ("questionnaire_requests", request), ("questionnaire_responses", response) })
        {
            // The runtime roles have no UPDATE or DELETE.
            await RefusedAsync(PostgresErrorCodes.InsufficientPrivilege,
                () => Accounts.ExecuteAsync(App, booking.Clinician, $"UPDATE ehr.{table} SET sensitive = false WHERE id = @id", ("id", id)));
            await RefusedAsync(PostgresErrorCodes.InsufficientPrivilege,
                () => Accounts.ExecuteAsync(App, booking.Clinician, $"DELETE FROM ehr.{table} WHERE id = @id", ("id", id)));

            // The owner and the superuser are refused by the trigger: the sensitive mark never comes off.
            foreach (var connection in new[] { database.OwnerConnectionString, database.SuperuserConnectionString })
            {
                await RefusedAsync(PostgresErrorCodes.InsufficientPrivilege,
                    () => Accounts.ExecuteAsync(connection, null, $"ALTER TABLE ehr.{table} NO FORCE ROW LEVEL SECURITY; UPDATE ehr.{table} SET sensitive = false WHERE id = @id", ("id", id)));
                await RefusedAsync(PostgresErrorCodes.InsufficientPrivilege,
                    () => Accounts.ExecuteAsync(connection, null, $"ALTER TABLE ehr.{table} NO FORCE ROW LEVEL SECURITY; DELETE FROM ehr.{table} WHERE id = @id", ("id", id)));
            }
        }

        Assert.Equal(["true"], await AsSuperuserAsync("SELECT sensitive::text FROM ehr.questionnaire_responses WHERE id = @id", ("id", response)));
    }

    [Fact]
    public async Task An_answer_that_needs_review_reaches_the_appointments_clinician_and_the_care_team_and_no_one_else()
    {
        var booking = await BookedAsync(TimeSpan.FromDays(2));
        var colleague = await Accounts.StaffAsync(database, StaffRoles.Clinician);
        await Accounts.AssignAsync(database, colleague, booking.Patient);
        // On the panel but disabled: nothing. Locked for a few minutes after wrong passwords: still told.
        var disabled = await Accounts.StaffAsync(database, StaffRoles.Clinician);
        await Accounts.AssignAsync(database, disabled, booking.Patient);
        await Accounts.LockOutAsync(database, disabled);
        var locked = await Accounts.StaffAsync(database, StaffRoles.Clinician);
        await Accounts.AssignAsync(database, locked, booking.Patient);
        await Accounts.ExecuteAsync(database.SuperuserConnectionString, null, "UPDATE ehr.staff_users SET lockout_end = now() + interval '5 minutes' WHERE id = @id", ("id", locked));
        // Not a clinician, and a clinician with nothing to do with the patient: nothing.
        var admin = await Accounts.StaffAsync(database, StaffRoles.Admin);
        var outsider = await Accounts.StaffAsync(database, StaffRoles.Clinician);
        // Once on the panel, and no longer: nothing.
        var left = await Accounts.StaffAsync(database, StaffRoles.Clinician);
        await Accounts.AssignAsync(database, left, booking.Patient);
        await Accounts.ExecuteAsync(App, left, "UPDATE ehr.patient_assignments SET ended_at = now() WHERE clinician_id = @me AND ended_at IS NULL", ("me", left));

        // No alert: no item.
        await AnswerAsync(booking.Account, await AskAsync(booking.Clinician, booking.Appointment, Gad7), Answers(GadItems, [3, 3, 3, 3, 3, 3, 3]));
        Assert.Empty(await AsSuperuserAsync("SELECT id FROM ehr.inbox_items WHERE patient_id = @patient", ("patient", booking.Patient)));

        var response = await AnswerAsync(booking.Account, await AskAsync(booking.Clinician, booking.Appointment), Phq(0, 0, 0, 0, 0, 0, 0, 0, 1));

        var expected = new[] { booking.Clinician, booking.AppointmentClinician, colleague, locked }
            .Select(recipient => $"{recipient} form-alert true questionnaire_responses {response} A pre-visit form has an answer that needs review").Order(StringComparer.Ordinal);
        Assert.Equal(expected, await AsSuperuserAsync(
            """
            SELECT recipient_id || ' ' || kind || ' ' || sensitive || ' ' || source_table || ' ' || source_id || ' ' || summary
            FROM ehr.inbox_items WHERE patient_id = @patient ORDER BY 1
            """, ("patient", booking.Patient)));
        Assert.DoesNotContain(await AsSuperuserAsync("SELECT recipient_id FROM ehr.inbox_items WHERE patient_id = @patient", ("patient", booking.Patient)),
            recipient => recipient == disabled.ToString() || recipient == admin.ToString() || recipient == outsider.ToString() || recipient == left.ToString());

        // Each recipient sees their own item and marks it done; the others' stay.
        Assert.Equal(["1"], await AsAsync(App, colleague, "SELECT count(*) FROM ehr.inbox_items"));
        await Accounts.ExecuteAsync(App, colleague, "UPDATE ehr.inbox_items SET done_at = now() WHERE kind = 'form-alert'");
        Assert.Equal(["3"], await AsSuperuserAsync("SELECT count(*) FROM ehr.inbox_items WHERE patient_id = @patient AND done_at IS NULL", ("patient", booking.Patient)));

        // Nobody can make an item sensitive or not: the apps update only done_at, and create none.
        await RefusedAsync(PostgresErrorCodes.InsufficientPrivilege,
            () => Accounts.ExecuteAsync(App, booking.Clinician, "UPDATE ehr.inbox_items SET sensitive = false WHERE kind = 'form-alert'"));
    }

    // An admin sees that a form changed and who changed it, never its values: every write of a request, an answer and
    // an alert's inbox item is marked sensitive, and the alert's summary names no questionnaire, answer or score.
    [Fact]
    public async Task Writes_of_a_form_and_its_alert_are_audited_as_sensitive_and_show_an_admin_no_values()
    {
        var booking = await BookedAsync(TimeSpan.FromDays(2));
        var request = await AskAsync(booking.Clinician, booking.Appointment);
        await AnswerAsync(booking.Account, request, Phq(3, 3, 3, 3, 3, 3, 3, 3, 3));
        await Accounts.ExecuteAsync(App, booking.Clinician, "UPDATE ehr.inbox_items SET done_at = now() WHERE kind = 'form-alert'");
        var admin = await Accounts.StaffAsync(database, StaffRoles.Admin);

        var events = await AsAsync(App, admin,
            """
            SELECT e.table_name || ' ' || e.action || ' ' || e.sensitive || ' ' || coalesce((SELECT d.details::text FROM ehr.shown_audit_details(ARRAY[e.id]) d), 'hidden')
            FROM ehr.audit_events e WHERE e.patient_id = @patient AND e.table_name IN ('questionnaire_requests', 'questionnaire_responses', 'inbox_items')
            ORDER BY e.occurred_at, e.id
            """, null, ("patient", booking.Patient));
        Assert.Equal(
            ["questionnaire_requests insert true hidden", "questionnaire_responses insert true hidden", "inbox_items insert true hidden",
             "inbox_items insert true hidden", "inbox_items update true hidden"],
            events);

        var summaries = await AsSuperuserAsync("SELECT DISTINCT summary FROM ehr.inbox_items WHERE patient_id = @patient", ("patient", booking.Patient));
        Assert.Equal(["A pre-visit form has an answer that needs review"], summaries);
        Assert.DoesNotMatch(Words(), summaries[0]);
    }

    // A questionnaire's name, a score, a band or an answer's words: none of them in an alert's summary.
    [GeneratedRegex("PHQ|GAD|27|Severe|severe|Nearly|Several|dead|hurt", RegexOptions.CultureInvariant)]
    private static partial Regex Words();

    // An answer that needs review isn't stored with nobody told while any clinician can be (the step's review). The
    // reviewer's case: the clinician who asked has left the patient's panel, nobody else is on it, and the appointment's
    // clinician's account is disabled. Then every clinician whose account isn't disabled gets the item, and no admin or
    // disabled account does.
    [Fact]
    public async Task With_nobody_on_the_team_an_answer_to_review_goes_to_every_clinician_who_can_act()
    {
        var booking = await BookedAsync(TimeSpan.FromDays(2));
        var request = await AskAsync(booking.Clinician, booking.Appointment);
        await Accounts.ExecuteAsync(App, booking.Clinician, "UPDATE ehr.patient_assignments SET ended_at = now() WHERE clinician_id = @me AND ended_at IS NULL",
            ("me", booking.Clinician));
        await Accounts.LockOutAsync(database, booking.AppointmentClinician);
        var admin = await Accounts.StaffAsync(database, StaffRoles.Admin);

        var before = await EnabledCliniciansAsync();
        var response = await AnswerAsync(booking.Account, request, Phq(0, 0, 0, 0, 0, 0, 0, 0, 2));
        var after = await EnabledCliniciansAsync();

        var holders = await OpenHoldersAsync(response);
        ToldEveryone(before, after, holders);
        Assert.Contains(booking.Clinician.ToString(), holders);
        Assert.DoesNotContain(booking.AppointmentClinician.ToString(), holders);
        Assert.DoesNotContain(admin.ToString(), holders);
        Assert.Equal(["A pre-visit form has an answer that needs review"], await AsSuperuserAsync(
            "SELECT DISTINCT summary FROM ehr.inbox_items WHERE source_id = @response", ("response", response)));
    }

    // A clinician who joins the patient's panel while an answer to review has no item open with anyone on the team (the
    // panel now, or the appointment's clinician) gets one; while one is open, nobody new does.
    [Fact]
    public async Task A_clinician_joining_the_panel_gets_an_answer_to_review_that_nobody_on_the_team_holds_open()
    {
        var booking = await BookedAsync(TimeSpan.FromDays(2));
        var response = await AnswerAsync(booking.Account, await AskAsync(booking.Clinician, booking.Appointment), Phq(0, 0, 0, 0, 0, 0, 0, 0, 1));
        Task<List<string>> HoldersAsync() => AsSuperuserAsync(
            "SELECT recipient_id::text || ' ' || (done_at IS NULL)::text FROM ehr.inbox_items WHERE source_id = @response ORDER BY created_at, recipient_id",
            ("response", response));

        // Open with the asking clinician and the appointment's: a new member of the team gets nothing.
        var early = await Accounts.StaffAsync(database, StaffRoles.Clinician);
        await Accounts.AssignAsync(database, early, booking.Patient);
        Assert.DoesNotContain(await HoldersAsync(), holder => holder.StartsWith(early.ToString(), StringComparison.Ordinal));

        // Both mark theirs done; the next clinician to join is given it, and theirs is the one open.
        foreach (var holder in new[] { booking.Clinician, booking.AppointmentClinician })
        {
            await Accounts.ExecuteAsync(App, holder, "UPDATE ehr.inbox_items SET done_at = now() WHERE kind = 'form-alert'");
        }

        var later = await Accounts.StaffAsync(database, StaffRoles.Clinician);
        await Accounts.AssignAsync(database, later, booking.Patient);
        Assert.Equal([$"{later} true"], (await HoldersAsync()).Where(holder => holder.EndsWith("true", StringComparison.Ordinal)));
        Assert.Equal(["true"], await AsSuperuserAsync(
            "SELECT sensitive::text FROM ehr.inbox_items WHERE source_id = @response AND recipient_id = @later", ("response", response), ("later", later)));
    }

    // A clinician joining a patient's panel fires two owner-run triggers (this step's, then step 13d's for refill
    // requests: AFTER triggers fire in name order), after step 13c's guard has set the assignment's time to the
    // database's clock: each delivers what is waiting, exactly once. The join takes two advisory locks: this step's
    // alerts' lock, shared, and step 13d's care-team lock for the patient (85-pre-visit-forms.sql says why they close no
    // circle). And marking done still goes as before: a form-alert item by its recipient, never a refill item.
    [Fact]
    public async Task A_clinician_joining_the_panel_gets_a_waiting_refill_and_an_unreceived_form_alert_once_each_under_two_locks()
    {
        var booking = await BookedAsync(TimeSpan.FromDays(2));
        var medication = Guid.CreateVersion7();
        await Accounts.ExecuteAsync(database.SuperuserConnectionString, null,
            "INSERT INTO ehr.medication_statements (id, patient_id, code, display, dosage, status, sensitive) "
            + "VALUES (@id, @patient, '314076', 'lisinopril 10 MG Oral Tablet', '1 tablet by mouth daily', 'active', false)",
            ("id", medication), ("patient", booking.Patient));
        var refill = Guid.CreateVersion7();
        await Accounts.ExecuteAsync(App, booking.Account,
            """
            INSERT INTO ehr.refill_requests (id, patient_id, medication_statement_id, medication_display, requested_by, requested_at, status, sensitive)
            VALUES (@id, @patient, @medication, '', gen_random_uuid(), now(), 'requested', false)
            """, ("id", refill), ("patient", booking.Patient), ("medication", medication));
        var response = await AnswerAsync(booking.Account, await AskAsync(booking.Clinician, booking.Appointment), Phq(0, 0, 0, 0, 0, 0, 0, 0, 1));

        // The care team leaves (closing its refill item; its form alert stays its own), and the appointment's clinician
        // marks theirs done: nobody on the team holds either.
        await Accounts.ExecuteAsync(App, booking.Clinician, "UPDATE ehr.patient_assignments SET ended_at = now() WHERE clinician_id = @me AND ended_at IS NULL",
            ("me", booking.Clinician));
        await Accounts.ExecuteAsync(App, booking.AppointmentClinician, "UPDATE ehr.inbox_items SET done_at = now() WHERE kind = 'form-alert'");

        // A clinician joins, with a time far from the database's clock, which 13c's guard replaces.
        var joining = await Accounts.StaffAsync(database, StaffRoles.Clinician);
        await using (var connection = new NpgsqlConnection(App))
        {
            await connection.OpenAsync(Cancel);
            await using var transaction = await connection.BeginTransactionAsync(Cancel);
            async Task<object?> RunAsync(string sql)
            {
                await using var command = new NpgsqlCommand(sql, connection, transaction);
                command.Parameters.AddWithValue("me", joining.ToString());
                command.Parameters.AddWithValue("patient", booking.Patient);
                return await command.ExecuteScalarAsync(Cancel);
            }

            await RunAsync("SELECT set_config('app.user_id', @me, true)");
            await RunAsync("INSERT INTO ehr.patient_assignments (id, patient_id, clinician_id, assigned_at) VALUES (gen_random_uuid(), @patient, @me::uuid, '2000-01-01')");
            Assert.Equal(2L, await RunAsync("SELECT count(*) FROM pg_locks WHERE locktype = 'advisory' AND pid = pg_backend_pid()"));
            Assert.Equal("ShareLock", await RunAsync(AlertsLock));
            Assert.Equal(1L, await RunAsync(
                """
                SELECT count(*) FROM pg_locks
                WHERE locktype = 'advisory' AND pid = pg_backend_pid() AND objsubid = 1
                  AND ((classid::bigint << 32) | objid::bigint) = hashtextextended('ehr.refill_care_team:' || @patient::text, 0)
                """));
            await transaction.CommitAsync(Cancel);
        }

        Assert.Equal(["true"], await AsSuperuserAsync(
            "SELECT (assigned_at > now() - interval '1 minute')::text FROM ehr.patient_assignments WHERE clinician_id = @joining", ("joining", joining)));
        Assert.Equal([$"form-alert {response} true", $"refill-request {refill} true"], await AsSuperuserAsync(
            "SELECT kind || ' ' || source_id || ' ' || (done_at IS NULL) FROM ehr.inbox_items WHERE recipient_id = @joining ORDER BY kind", ("joining", joining)));

        // Their form alert they mark done themselves; the refill item only an answer closes.
        await RefusedAsync(PostgresErrorCodes.CheckViolation,
            () => Accounts.ExecuteAsync(App, joining, "UPDATE ehr.inbox_items SET done_at = now() WHERE kind = 'refill-request'"));
        await Accounts.ExecuteAsync(App, joining, "UPDATE ehr.inbox_items SET done_at = now() WHERE kind = 'form-alert'");
        Assert.Equal(["form-alert false", "refill-request true"], await AsSuperuserAsync(
            "SELECT kind || ' ' || (done_at IS NULL) FROM ehr.inbox_items WHERE recipient_id = @joining ORDER BY kind", ("joining", joining)));
    }

    // Every form-alert item is a sensitive item's, whoever writes it: the check holds the tables' owner too.
    [Fact]
    public async Task A_form_alert_item_is_sensitive_whoever_writes_it()
    {
        var booking = await BookedAsync(TimeSpan.FromDays(2));
        await RefusedAsync(PostgresErrorCodes.CheckViolation, () => Accounts.ExecuteAsync(database.OwnerConnectionString, null,
            "INSERT INTO ehr.inbox_items (id, recipient_id, kind, patient_id, summary, created_at, sensitive) "
            + "VALUES (gen_random_uuid(), @clinician, 'form-alert', @patient, 'A pre-visit form has an answer that needs review', now(), false)",
            ("clinician", booking.Clinician), ("patient", booking.Patient)));
    }

    // Every clinician whose account isn't disabled, as the database has them now.
    Task<List<string>> EnabledCliniciansAsync() => AsSuperuserAsync(
        "SELECT id::text FROM ehr.staff_users WHERE roles @> ARRAY['Clinician'] AND (lockout_end IS NULL OR lockout_end < now() + interval '10 years') ORDER BY 1");

    // Who holds an open item for an answer.
    Task<List<string>> OpenHoldersAsync(Guid response) => AsSuperuserAsync(
        "SELECT recipient_id::text FROM ehr.inbox_items WHERE source_id = @response AND kind = 'form-alert' AND done_at IS NULL ORDER BY 1", ("response", response));

    // Told to every clinician who could be: each one enabled both before and after holds it, and each holder was enabled
    // before or after (other test classes add and disable accounts meanwhile), but those named besides.
    static void ToldEveryone(List<string> before, List<string> after, List<string> holders, params Guid[] besides)
    {
        var also = besides.Select(id => id.ToString()).ToHashSet();
        Assert.Superset(before.Intersect(after).Except(also).ToHashSet(), holders.ToHashSet());
        Assert.Subset(before.Union(after).Union(also).ToHashSet(), holders.ToHashSet());
    }

    // An admin disables an account as the Staff page does (StaffAccounts.DisableAsync): locked out for good, through the
    // app's role, which Npgsql writes as infinity; or enables it again.
    async Task SetDisabledAsync(Guid admin, Guid staff, bool disabled) =>
        await Accounts.ExecuteAsync(App, admin, $"UPDATE ehr.staff_users SET lockout_end = {(disabled ? "'infinity'" : "NULL")} WHERE id = @id", ("id", staff));

    // One answer's open form-alert items, as a recipient sees them through each runtime role.
    async Task SeesOpenAsync(Guid recipient, Guid response, int count)
    {
        foreach (var connection in new[] { App, Read })
        {
            Assert.Equal([count.ToString(System.Globalization.CultureInfo.InvariantCulture)], await AsAsync(connection, recipient,
                "SELECT count(*) FROM ehr.inbox_items WHERE source_id = @response AND kind = 'form-alert' AND done_at IS NULL", null, ("response", response)));
        }
    }

    // The re-review's case: the clinician who asked leaves the panel, the answer's only item goes to the appointment's
    // clinician, and that account is then disabled. The answer is told again at once, to everyone who can act, since
    // nobody is on the panel; and a clinician joining the panel afterwards is given it, since a disabled account's item
    // tells nobody. The disabled account keeps its own.
    [Fact]
    public async Task An_answer_held_only_by_an_account_later_disabled_goes_to_everyone_and_to_whoever_joins()
    {
        var booking = await BookedAsync(TimeSpan.FromDays(2));
        var request = await AskAsync(booking.Clinician, booking.Appointment);
        await Accounts.ExecuteAsync(App, booking.Clinician, "UPDATE ehr.patient_assignments SET ended_at = now() WHERE clinician_id = @me AND ended_at IS NULL",
            ("me", booking.Clinician));
        var admin = await Accounts.StaffAsync(database, StaffRoles.Admin);
        var response = await AnswerAsync(booking.Account, request, Phq(0, 0, 0, 0, 0, 0, 0, 0, 1));
        Assert.Equal([booking.AppointmentClinician.ToString()], await OpenHoldersAsync(response));

        var before = await EnabledCliniciansAsync();
        await SetDisabledAsync(admin, booking.AppointmentClinician, disabled: true);
        var after = await EnabledCliniciansAsync();
        var holders = await OpenHoldersAsync(response);
        ToldEveryone(before, after, holders, booking.AppointmentClinician);
        Assert.Contains(booking.AppointmentClinician.ToString(), holders);
        Assert.DoesNotContain(admin.ToString(), holders);

        var joining = await Accounts.StaffAsync(database, StaffRoles.Clinician);
        await SeesOpenAsync(joining, response, 0);
        await Accounts.AssignAsync(database, joining, booking.Patient);
        await SeesOpenAsync(joining, response, 1);
    }

    // Disabling an answer's only holder tells the patient's panel when it has someone who can act (here, a colleague who
    // had marked theirs done); enabling the account again, and disabling it once more, gives nobody a second item.
    [Fact]
    public async Task Disabling_an_answers_only_holder_tells_the_panel_once_and_enabling_it_again_changes_nothing()
    {
        var booking = await BookedAsync(TimeSpan.FromDays(2));
        var admin = await Accounts.StaffAsync(database, StaffRoles.Admin);
        var response = await AnswerAsync(booking.Account, await AskAsync(booking.Clinician, booking.Appointment), Phq(0, 0, 0, 0, 0, 0, 0, 0, 1));
        await Accounts.ExecuteAsync(App, booking.Clinician, "UPDATE ehr.inbox_items SET done_at = now() WHERE kind = 'form-alert' AND source_id = @response",
            ("response", response));
        Assert.Equal([booking.AppointmentClinician.ToString()], await OpenHoldersAsync(response));

        await SetDisabledAsync(admin, booking.AppointmentClinician, disabled: true);
        Assert.Equal(new[] { booking.Clinician, booking.AppointmentClinician }.Select(id => id.ToString()).Order(StringComparer.Ordinal),
            await OpenHoldersAsync(response));
        await SeesOpenAsync(booking.Clinician, response, 1);
        Task<List<string>> AllAsync() => AsSuperuserAsync("SELECT count(*)::text FROM ehr.inbox_items WHERE source_id = @response", ("response", response));
        Assert.Equal(["3"], await AllAsync());

        await SetDisabledAsync(admin, booking.AppointmentClinician, disabled: false);
        Assert.Equal(["3"], await AllAsync());
        await SetDisabledAsync(admin, booking.AppointmentClinician, disabled: true);
        Assert.Equal(["3"], await AllAsync());
    }

    // Disabling one holder of several changes nothing: someone who can act holds it still. A lockout of fifteen minutes
    // after wrong passwords isn't a disable: the only holder locked so still counts, nothing is told again, and a
    // clinician joining the panel is given nothing.
    [Fact]
    public async Task Disabling_one_holder_of_several_or_locking_the_only_one_for_minutes_tells_nobody_new()
    {
        var admin = await Accounts.StaffAsync(database, StaffRoles.Admin);
        var several = await BookedAsync(TimeSpan.FromDays(2));
        var shared = await AnswerAsync(several.Account, await AskAsync(several.Clinician, several.Appointment), Phq(0, 0, 0, 0, 0, 0, 0, 0, 1));
        await SetDisabledAsync(admin, several.AppointmentClinician, disabled: true);
        Assert.Equal(new[] { several.Clinician, several.AppointmentClinician }.Select(id => id.ToString()).Order(StringComparer.Ordinal),
            await AsSuperuserAsync("SELECT recipient_id::text FROM ehr.inbox_items WHERE source_id = @response ORDER BY 1", ("response", shared)));

        var only = await BookedAsync(TimeSpan.FromDays(2));
        var request = await AskAsync(only.Clinician, only.Appointment);
        await Accounts.ExecuteAsync(App, only.Clinician, "UPDATE ehr.patient_assignments SET ended_at = now() WHERE clinician_id = @me AND ended_at IS NULL",
            ("me", only.Clinician));
        var response = await AnswerAsync(only.Account, request, Phq(0, 0, 0, 0, 0, 0, 0, 0, 1));
        await Accounts.ExecuteAsync(App, admin, "UPDATE ehr.staff_users SET lockout_end = now() + interval '15 minutes' WHERE id = @id", ("id", only.AppointmentClinician));
        var joining = await Accounts.StaffAsync(database, StaffRoles.Clinician);
        await Accounts.AssignAsync(database, joining, only.Patient);
        Assert.Equal([only.AppointmentClinician.ToString()], await AsSuperuserAsync(
            "SELECT recipient_id::text FROM ehr.inbox_items WHERE source_id = @response", ("response", response)));
        await SeesOpenAsync(joining, response, 0);
    }

    // An answer stored at the moment its only recipient's account is disabled still reaches someone who can act, in
    // either order: the disable waits for the answer and tells it again, or the answer waits for the disable and leaves
    // that account out. (The alerts' lock, 85-pre-visit-forms.sql.)
    [Fact]
    public async Task An_answer_stored_as_its_only_recipient_is_disabled_reaches_someone_who_can_act_in_either_order()
    {
        var admin = await Accounts.StaffAsync(database, StaffRoles.Admin);
        foreach (var disableFirst in new[] { true, false })
        {
            var booking = await BookedAsync(TimeSpan.FromDays(2));
            var request = await AskAsync(booking.Clinician, booking.Appointment);
            await Accounts.ExecuteAsync(App, booking.Clinician, "UPDATE ehr.patient_assignments SET ended_at = now() WHERE clinician_id = @me AND ended_at IS NULL",
                ("me", booking.Clinician));
            var response = Guid.CreateVersion7();
            await using var disabling = await BeginAsync(admin);
            await using var answering = await BeginAsync(booking.Account);
            var disable = new NpgsqlCommand("UPDATE ehr.staff_users SET lockout_end = 'infinity' WHERE id = @id", disabling.Connection, disabling.Transaction);
            disable.Parameters.AddWithValue("id", booking.AppointmentClinician);
            var answer = new NpgsqlCommand(
                """
                INSERT INTO ehr.questionnaire_responses (id, request_id, patient_id, questionnaire_code, clinician_id, answers)
                VALUES (@id, @request, gen_random_uuid(), 'forged', gen_random_uuid(), @answers::jsonb)
                """, answering.Connection, answering.Transaction);
            answer.Parameters.AddWithValue("id", response);
            answer.Parameters.AddWithValue("request", request);
            answer.Parameters.AddWithValue("answers", Phq(0, 0, 0, 0, 0, 0, 0, 0, 1));
            var (first, firstCommand, second, secondCommand) = disableFirst
                ? (disabling, disable, answering, answer)
                : (answering, answer, disabling, disable);

            await firstCommand.ExecuteNonQueryAsync(Cancel);
            var waiting = secondCommand.ExecuteNonQueryAsync(Cancel);
            await Task.Delay(TimeSpan.FromMilliseconds(500), Cancel);
            Assert.False(waiting.IsCompleted, "The second waits for the first.");
            await first.Transaction.CommitAsync(Cancel);
            await waiting;
            await second.Transaction.CommitAsync(Cancel);

            var holders = await OpenHoldersAsync(response);
            var enabled = await EnabledCliniciansAsync();
            Assert.Contains(holders, holder => enabled.Contains(holder));
            if (disableFirst)
            {
                Assert.DoesNotContain(booking.AppointmentClinician.ToString(), holders);
            }
        }
    }

    // How this transaction holds the alerts' lock ('form-alert holders', 85-pre-visit-forms.sql): "ShareLock",
    // "ExclusiveLock" or "none".
    const string AlertsLock = """
        SELECT coalesce(string_agg(mode, ',' ORDER BY mode), 'none') FROM pg_locks
        WHERE locktype = 'advisory' AND pid = pg_backend_pid() AND objsubid = 1
          AND ((classid::bigint << 32) | objid::bigint) = hashtextextended('form-alert holders', 0)
        """;

    static async Task<object?> ScalarAsync(Open open, string sql, params (string Name, object Value)[] parameters)
    {
        await using var command = new NpgsqlCommand(sql, open.Connection, open.Transaction);
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        return await command.ExecuteScalarAsync(Cancel);
    }

    // A lockout of minutes after wrong passwords takes no clinic-wide lock: answers with an alert never wait behind it.
    // A real disable takes the alerts' lock alone, until its transaction ends.
    [Fact]
    public async Task A_lockout_of_minutes_takes_no_clinic_wide_lock_and_a_disable_takes_the_alerts_lock_alone()
    {
        var admin = await Accounts.StaffAsync(database, StaffRoles.Admin);
        var clinician = await Accounts.StaffAsync(database, StaffRoles.Clinician);
        foreach (var (until, held) in new[] { ("now() + interval '15 minutes'", "none"), ("'infinity'", "ExclusiveLock") })
        {
            await using var open = await BeginAsync(admin);
            await ScalarAsync(open, $"UPDATE ehr.staff_users SET lockout_end = {until} WHERE id = @id", ("id", clinician));
            Assert.Equal(held, await ScalarAsync(open, AlertsLock));
            await open.Transaction.RollbackAsync(Cancel);
        }
    }

    // An admin's disable left open doesn't hold up a patient's answer for longer than five seconds: the answer is then
    // stored and told by the rule without the lock, and the answering transaction's lock_timeout is as it was.
    [Fact]
    public async Task An_answer_waits_at_most_five_seconds_behind_an_open_disable_and_is_still_stored_and_told()
    {
        var admin = await Accounts.StaffAsync(database, StaffRoles.Admin);
        var idle = await Accounts.StaffAsync(database, StaffRoles.Clinician);
        var booking = await BookedAsync(TimeSpan.FromDays(2));
        var request = await AskAsync(booking.Clinician, booking.Appointment);

        await using var disabling = await BeginAsync(admin);
        await ScalarAsync(disabling, "UPDATE ehr.staff_users SET lockout_end = 'infinity' WHERE id = @id", ("id", idle));
        Assert.Equal("ExclusiveLock", await ScalarAsync(disabling, AlertsLock));

        var response = Guid.CreateVersion7();
        await using (var answering = await BeginAsync(booking.Account))
        {
            var started = System.Diagnostics.Stopwatch.StartNew();
            await ScalarAsync(answering,
                """
                INSERT INTO ehr.questionnaire_responses (id, request_id, patient_id, questionnaire_code, clinician_id, answers)
                VALUES (@id, @request, gen_random_uuid(), 'forged', gen_random_uuid(), @answers::jsonb)
                """, ("id", response), ("request", request), ("answers", Phq(0, 0, 0, 0, 0, 0, 0, 0, 1)));
            started.Stop();
            Assert.InRange(started.Elapsed, TimeSpan.FromSeconds(4.5), TimeSpan.FromSeconds(30));
            Assert.Equal("0", await ScalarAsync(answering, "SELECT current_setting('lock_timeout')"));
            Assert.Equal("none", await ScalarAsync(answering, AlertsLock));
            await answering.Transaction.CommitAsync(Cancel);
        }

        await disabling.Transaction.RollbackAsync(Cancel);
        Assert.Equal(new[] { booking.Clinician, booking.AppointmentClinician }.Select(id => id.ToString()).Order(StringComparer.Ordinal),
            await OpenHoldersAsync(response));
    }

    // A clinician whose own account is disabled at the moment they join a panel, for an answer nobody on the team holds
    // open: in either order, they don't end up holding its only open item. Join first: the disable waits, then tells it
    // again to the team. Disable first: the join waits, then gives them nothing.
    [Fact]
    public async Task A_clinician_disabled_as_they_join_never_holds_an_answers_only_open_item()
    {
        var admin = await Accounts.StaffAsync(database, StaffRoles.Admin);
        foreach (var joinFirst in new[] { true, false })
        {
            var booking = await BookedAsync(TimeSpan.FromDays(2));
            var response = await AnswerAsync(booking.Account, await AskAsync(booking.Clinician, booking.Appointment), Phq(0, 0, 0, 0, 0, 0, 0, 0, 1));
            foreach (var holder in new[] { booking.Clinician, booking.AppointmentClinician })
            {
                await Accounts.ExecuteAsync(App, holder, "UPDATE ehr.inbox_items SET done_at = now() WHERE kind = 'form-alert' AND source_id = @response",
                    ("response", response));
            }

            var joining = await Accounts.StaffAsync(database, StaffRoles.Clinician);
            await using var join = await BeginAsync(joining);
            await using var disable = await BeginAsync(admin);
            const string Join = "INSERT INTO ehr.patient_assignments (id, patient_id, clinician_id, assigned_at) VALUES (gen_random_uuid(), @patient, @joining, now())";
            const string Disable = "UPDATE ehr.staff_users SET lockout_end = 'infinity' WHERE id = @joining";
            var (first, firstSql, second, secondSql) = joinFirst ? (join, Join, disable, Disable) : (disable, Disable, join, Join);
            (string, object)[] parameters = [("patient", booking.Patient), ("joining", joining)];

            await ScalarAsync(first, firstSql, firstSql == Join ? parameters : parameters[1..]);
            var waiting = ScalarAsync(second, secondSql, secondSql == Join ? parameters : parameters[1..]);
            await Task.Delay(TimeSpan.FromMilliseconds(500), Cancel);
            Assert.False(waiting.IsCompleted, "The second waits for the first.");
            await first.Transaction.CommitAsync(Cancel);
            await waiting;
            await second.Transaction.CommitAsync(Cancel);

            var holders = await OpenHoldersAsync(response);
            if (joinFirst)
            {
                // They were given it, and their disable told it again to the team, who can act.
                Assert.Contains(joining.ToString(), holders);
                Assert.Contains(booking.Clinician.ToString(), holders);
                Assert.Contains(booking.AppointmentClinician.ToString(), holders);
            }
            else
            {
                Assert.Empty(holders);
            }
        }
    }

    // Joining, asking for a refill, answering a form, disabling the appointment's clinician and leaving the panel, all at
    // once for one patient, round after round: never a deadlock, and the answer always held open by someone who can
    // act. (Asking may be refused when the leave and the join leave nobody on the panel at that moment.)
    [Fact]
    public async Task Joining_asking_answering_disabling_and_leaving_at_once_never_deadlock()
    {
        var admin = await Accounts.StaffAsync(database, StaffRoles.Admin);
        for (var round = 0; round < 10; round++)
        {
            var booking = await BookedAsync(TimeSpan.FromDays(2));
            var request = await AskAsync(booking.Clinician, booking.Appointment);
            var medication = Guid.CreateVersion7();
            await Accounts.ExecuteAsync(database.SuperuserConnectionString, null,
                "INSERT INTO ehr.medication_statements (id, patient_id, code, display, dosage, status, sensitive) "
                + "VALUES (@id, @patient, '314076', 'lisinopril 10 MG Oral Tablet', '1 tablet by mouth daily', 'active', false)",
                ("id", medication), ("patient", booking.Patient));
            var joining = await Accounts.StaffAsync(database, StaffRoles.Clinician);
            var response = Guid.CreateVersion7();
            (Guid User, string Sql, (string Name, object Value)[] Parameters)[] steps =
            [
                (joining, "INSERT INTO ehr.patient_assignments (id, patient_id, clinician_id, assigned_at) VALUES (gen_random_uuid(), @patient, @joining, now())",
                    [("patient", booking.Patient), ("joining", joining)]),
                (booking.Account, "INSERT INTO ehr.refill_requests (id, patient_id, medication_statement_id, medication_display, requested_by, requested_at, status, sensitive) "
                    + "VALUES (gen_random_uuid(), @patient, @medication, '', gen_random_uuid(), now(), 'requested', false)",
                    [("patient", booking.Patient), ("medication", medication)]),
                (booking.Account, "INSERT INTO ehr.questionnaire_responses (id, request_id, patient_id, questionnaire_code, clinician_id, answers) "
                    + "VALUES (@response, @request, gen_random_uuid(), 'forged', gen_random_uuid(), @answers::jsonb)",
                    [("response", response), ("request", request), ("answers", Phq(0, 0, 0, 0, 0, 0, 0, 0, 1))]),
                (admin, "UPDATE ehr.staff_users SET lockout_end = 'infinity' WHERE id = @disabled", [("disabled", booking.AppointmentClinician)]),
                (booking.Clinician, "UPDATE ehr.patient_assignments SET ended_at = now() WHERE clinician_id = @leaving AND ended_at IS NULL",
                    [("leaving", booking.Clinician)]),
            ];
            var opened = new List<Open>();
            foreach (var step in steps)
            {
                opened.Add(await BeginAsync(step.User));
            }

            var outcomes = await Task.WhenAll(steps.Select(async (step, i) =>
            {
                try
                {
                    await ScalarAsync(opened[i], step.Sql, step.Parameters);
                    await opened[i].Transaction.CommitAsync(Cancel);
                    return "ok";
                }
                catch (PostgresException e)
                {
                    return $"{e.SqlState} {e.ConstraintName}";
                }
            }));
            foreach (var open in opened)
            {
                await open.DisposeAsync();
            }

            Assert.DoesNotContain(outcomes, outcome => outcome.StartsWith(PostgresErrorCodes.DeadlockDetected, StringComparison.Ordinal));
            Assert.All(outcomes.Where((_, i) => i != 1), outcome => Assert.Equal("ok", outcome));
            Assert.Contains(outcomes[1], new[] { "ok", $"{PostgresErrorCodes.CheckViolation} refill_requests_care_team" });
            var enabled = await EnabledCliniciansAsync();
            Assert.Contains(await OpenHoldersAsync(response), holder => enabled.Contains(holder));
        }
    }

    sealed record Open(NpgsqlConnection Connection, NpgsqlTransaction Transaction) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            await Transaction.DisposeAsync();
            await Connection.DisposeAsync();
        }
    }

    // A transaction as the user on the app's role, the user context set first.
    async Task<Open> BeginAsync(Guid user)
    {
        var connection = new NpgsqlConnection(App);
        await connection.OpenAsync(Cancel);
        var transaction = await connection.BeginTransactionAsync(Cancel);
        await using var set = new NpgsqlCommand("SELECT set_config('app.user_id', @user, true)", connection, transaction);
        set.Parameters.AddWithValue("user", user.ToString());
        await set.ExecuteNonQueryAsync(Cancel);
        return new Open(connection, transaction);
    }

    // The alerts are told by the tables' owner, as the inbox's other deliveries are: each function names only what it must
    // (the inbox it writes; the panels and the staff accounts; the answers, to follow the team and a disabled account),
    // pins its search_path, and can't be run by the runtime roles. The rule itself runs as its caller, only ever the
    // owner's triggers.
    [Fact]
    public async Task The_alert_delivery_names_only_the_inbox_the_panels_and_the_staff()
    {
        foreach (var (function, definer, tables) in new[]
                 {
                     ("tell_care_team_of_answer", false, new[] { "inbox_items", "patient_assignments", "staff_users" }),
                     ("deliver_questionnaire_alert", true, []),
                     // Following the care team, and a disabled account, read the answers too.
                     ("follow_questionnaire_alert_care_team", true, ["inbox_items", "patient_assignments", "questionnaire_responses", "staff_users"]),
                     ("follow_disabled_form_alert_holder", true, ["inbox_items", "questionnaire_responses"]),
                 })
        {
            var definition = Assert.Single(await AsSuperuserAsync(
                "SELECT pg_get_functiondef(p.oid) FROM pg_proc p WHERE p.proname = @name AND p.prosecdef = @definer AND p.proowner = 'ehr_owner'::regrole",
                ("name", function), ("definer", definer)));
            Assert.Equal(tables, Tables().Matches(definition).Select(match => match.Groups[1].Value).Distinct().Order(StringComparer.Ordinal));
            Assert.Contains("SET search_path TO 'pg_catalog', 'pg_temp'", definition, StringComparison.Ordinal);
            Assert.Equal(["false"], await AsSuperuserAsync(
                "SELECT has_function_privilege('public', p.oid, 'EXECUTE')::text FROM pg_proc p WHERE p.proname = @name", ("name", function)));
        }
    }

    [GeneratedRegex(@"\behr\.(\w+)\b(?!\()", RegexOptions.CultureInvariant)]
    private static partial Regex Tables();
}
