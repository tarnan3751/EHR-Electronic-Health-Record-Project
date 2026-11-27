// Fills the local server stack (this folder) with a believable clinic day, for a demonstration: a panel of patients
// for the test clinician, a week's schedule, a lived-in chart and portal for the test patient, and work in the inbox.
// For the local stack with synthetic data only: it refuses any address but the stack's. It signs in with the accounts
// test-accounts.cs set up and drives the web app's own pages, as the clinician and the patient would, so everything it
// adds passes the same authorization, row-level security, audit trail and write triggers as a person's work. It never
// connects to the database.
//
//   dotnet run demo-data.cs [options]
//
// Safe to run again: each step looks at what the pages show and adds only what is missing; it never deletes or changes
// what is there. Everything is dated as the app dates it: a note is signed, a message sent and a result filed when the
// script runs; only vital signs and a result's collection carry a time of their own, and the app refuses an
// appointment that has passed. Portal accounts it enrolls for other patients are kept in demo-accounts.json beside this
// file, gitignored and, on macOS and Linux, readable only by its owner, like test-accounts.json.

// Run, never published: without this, file-based apps default to native AOT and restore its compiler.
#:property PublishAot=false

using System.Buffers.Binary;
using System.Globalization;
using System.Net;
using System.Net.Security;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

if (args.Contains("--help") || args.Contains("-h"))
{
    Console.WriteLine(Options.Usage);
    return 0;
}

Options options;
try
{
    options = Options.Parse(args);
}
catch (ArgumentException e)
{
    Console.Error.WriteLine(e.Message);
    Console.Error.WriteLine(Options.Usage);
    return 2;
}

return await new Demo(options).RunAsync();

sealed class Options
{
    public const string Usage = """
        Usage: dotnet run demo-data.cs [options]
          --clinical-url <url>          default https://ehr.example.com:8443
          --portal-url <url>            default https://portal.example.com:9443
          --mailpit-url <url>           default http://127.0.0.1:8025
          --ca <path>                   default certs/rootCA.pem beside this script
          --accounts <path>             test-accounts.cs's accounts file, default test-accounts.json beside this script
          --demo-accounts <path>        the portal accounts this script enrolls, default demo-accounts.json beside this script
        """;

    // The local stack's names (the hosts-file line) and this machine.
    static readonly string[] StackHosts = ["ehr.example.com", "ehr-remote.example.com", "portal.example.com", "localhost", "127.0.0.1"];

    public Uri Clinical { get; private set; } = new("https://ehr.example.com:8443");
    public Uri Portal { get; private set; } = new("https://portal.example.com:9443");
    public Uri Mailpit { get; private set; } = new("http://127.0.0.1:8025");
    public string RootCertificate { get; private set; } = Path.Combine(Here(), "certs", "rootCA.pem");
    public string AccountsFile { get; private set; } = Path.Combine(Here(), "test-accounts.json");
    public string DemoAccountsFile { get; private set; } = Path.Combine(Here(), "demo-accounts.json");

    public static Options Parse(string[] args)
    {
        var options = new Options();
        for (var i = 0; i < args.Length; i += 2)
        {
            var value = i + 1 < args.Length ? args[i + 1] : throw new ArgumentException($"{args[i]} needs a value.");
            switch (args[i])
            {
                case "--clinical-url": options.Clinical = Local(args[i], value, https: true); break;
                case "--portal-url": options.Portal = Local(args[i], value, https: true); break;
                case "--mailpit-url": options.Mailpit = Local(args[i], value, https: false); break;
                case "--ca": options.RootCertificate = Path.GetFullPath(value); break;
                case "--accounts": options.AccountsFile = Path.GetFullPath(value); break;
                case "--demo-accounts": options.DemoAccountsFile = Path.GetFullPath(value); break;
                default: throw new ArgumentException($"Unknown option {args[i]}.");
            }
        }

        return options;
    }

    // The sites are HTTPS only; Mailpit's API is plain HTTP on this machine.
    static Uri Local(string option, string value, bool https) =>
        !Uri.TryCreate(value, UriKind.Absolute, out var uri) || !StackHosts.Contains(uri.Host, StringComparer.OrdinalIgnoreCase)
            ? throw new ArgumentException($"{option}: {value} isn't the local stack. This script is for the local stack only.")
            : https && uri.Scheme != Uri.UriSchemeHttps
                ? throw new ArgumentException($"{option}: the stack's sites are HTTPS only.")
                : uri;

    // This script's folder, wherever it is run from.
    static string Here([CallerFilePath] string path = "") => Path.GetDirectoryName(path)!;
}

// What the script adds. Synthetic, and invented for this script: no real people, places or records.
static class Content
{
    // The test patient (test-accounts.cs), and the panel: the patients after it in the synthetic clinic.
    public const string PatientMrn = "SYN0000001";
    public const int PanelSize = 25;

    // Recorded with the emergency access the chart asks for before a patient joins a clinician's panel.
    public const string PanelReason = "Adding to my patients for the demonstration clinic on the local stack";

    public static string Mrn(int number) => $"SYN{number:D7}";

    // Visit types for the schedule: a reason and a length in minutes.
    public static readonly (string Reason, int Minutes)[] Visits =
    [
        ("Hypertension follow-up", 20),
        ("Diabetes check, review A1c", 30),
        ("Cough and congestion for four days", 15),
        ("Medication review", 20),
        ("Annual physical", 45),
        ("Blood pressure recheck", 10),
        ("Sore throat and fever", 15),
        ("Follow-up on recent lab results", 15),
        ("Low back pain after lifting", 20),
        ("Rash on forearm", 15),
        ("Fatigue, review thyroid labs", 30),
        ("Knee pain when climbing stairs", 20),
        ("Cholesterol follow-up", 20),
        ("Sinus pressure and headache", 15),
        ("Anxiety and sleep follow-up", 30),
        ("Pre-operative clearance", 30),
        ("Asthma check before winter", 20),
        ("Ear pain", 10),
    ];

    // The day's plan, the same for the same day whenever the script runs: 8 to 12 visits from 08:00, a lunch hour, and
    // the panel's patients (the test patient aside: it gets its own visit) taken in turn from a point the day sets.
    public static List<(TimeOnly Start, int Minutes, string Reason, int Patient)> Day(DateOnly day)
    {
        var count = 8 + day.DayNumber % 5;
        var plan = new List<(TimeOnly, int, string, int)>();
        var start = new TimeOnly(8, 0);
        for (var i = 0; i < count; i++)
        {
            var (reason, minutes) = Visits[(day.DayNumber * 7 + i * 5) % Visits.Length];
            plan.Add((start, minutes, reason, 2 + (day.DayNumber * 5 + i) % (PanelSize - 1)));
            start = start.AddMinutes(minutes + (i % 3 == 2 ? 10 : 0));
            if (start >= new TimeOnly(11, 45) && start < new TimeOnly(13, 0))
            {
                start = new TimeOnly(13, 0);
            }
        }

        return plan;
    }

    // How many of today's visits are checked in.
    public const int CheckedIn = 3;

    // The test patient's visit, if it has none coming up: on the third working day, so its pre-visit forms are open.
    public static readonly (TimeOnly Start, int Minutes, string Reason) PatientVisit = (new TimeOnly(16, 0), 20, "Follow-up on blood pressure and lab results");

    // Added to the test patient's chart only where it has fewer than three of a kind: a name from the clinic's lists.
    public static readonly (string List, string Name, (string Field, string Value)[] Fields)[] PatientLists =
    [
        ("allergies", "Sulfamethoxazole", [("Status", "active"), ("Reaction", "Rash"), ("Severity", "moderate"), ("Criticality", "low")]),
        ("medications", "cetirizine hydrochloride 10 MG Oral Tablet", [("Status", "active"), ("Dosage", "One tablet by mouth once a day as needed for allergies")]),
        ("diagnoses", "Other seasonal allergic rhinitis", [("Status", "active")]),
    ];

    // The test patient's vital signs over the past year: on the 10th of the month, so a run in the same month finds them.
    public static IEnumerable<(DateTime TakenAt, (string Code, string Value)[] Values)> PatientVitals(DateOnly today)
    {
        (int MonthsBack, int Hour, string Systolic, string Diastolic, string Pulse, string Weight)[] sets =
        [
            (11, 9, "138", "88", "80", "73.4"),
            (8, 10, "134", "86", "78", "72.8"),
            (5, 14, "128", "82", "74", "71.9"),
            (2, 11, "124", "78", "70", "71.2"),
        ];
        foreach (var set in sets)
        {
            var day = new DateOnly(today.Year, today.Month, 10).AddMonths(-set.MonthsBack);
            yield return (day.ToDateTime(new TimeOnly(set.Hour, 20)),
            [
                ("8480-6", set.Systolic), ("8462-4", set.Diastolic), ("8867-4", set.Pulse), ("8310-5", "36.7"), ("9279-1", "14"),
                ("59408-5", "98"), ("29463-7", set.Weight), ("8302-2", "172"),
            ]);
        }
    }

    // A lab order: the catalogue item, the reason for the test, and the result for each test it reports.
    public sealed record Lab(string Item, string Indication, Dictionary<string, string> Values);

    public static readonly Lab[] PatientLabs =
    [
        new("Basic metabolic panel (BMP)", "Kidney function and potassium on blood pressure medicine",
            new() { ["2345-7"] = "92", ["3094-0"] = "14", ["2160-0"] = "0.9", ["2951-2"] = "140", ["2823-3"] = "4.3", ["17861-6"] = "9.5" }),
        new("Lipid panel", "Cardiovascular risk with hypertension",
            new() { ["2093-3"] = "176", ["13457-7"] = "94", ["2085-9"] = "56", ["2571-8"] = "118" }),
        new("Thyroid-stimulating hormone (TSH)", "Tiredness for two months", new() { ["3016-3"] = "2.1" }),
    ];

    // Results the clinician hasn't acknowledged yet, one each on the first panel patients the inbox step uses.
    public static readonly Lab[] InboxLabs =
    [
        new("Hemoglobin A1c", "Type 2 diabetes, three-month check", new() { ["4548-4"] = "7.4" }),
        new("Lipid panel", "Cholesterol follow-up on atorvastatin",
            new() { ["2093-3"] = "226", ["13457-7"] = "142", ["2085-9"] = "44", ["2571-8"] = "198" }),
        new("Thyroid-stimulating hormone (TSH)", "Fatigue and weight gain", new() { ["3016-3"] = "5.6" }),
        new("Basic metabolic panel (BMP)", "Medication review, kidney function",
            new() { ["2345-7"] = "104", ["3094-0"] = "18", ["2160-0"] = "1.1", ["2951-2"] = "138", ["2823-3"] = "4.6", ["17861-6"] = "9.2" }),
        new("Hemoglobin A1c", "Prediabetes, yearly check", new() { ["4548-4"] = "6.1" }),
    ];

    // A note's four sections, and the visit summary released to the patient, if any.
    public sealed record Note(string Subjective, string Objective, string Assessment, string Plan, Summary? Summary = null);

    public sealed record Summary(string TalkedAbout, string Found, string NextSteps);

    public static readonly Note[] PatientNotes =
    [
        new("Here for blood pressure follow-up. Taking the blood pressure medicine every day without missed doses. Home readings mostly in the 120s over 70s. No headaches, chest pain or ankle swelling.",
            "BP 126/80, pulse 70. Alert and well appearing. Heart regular without murmur. Lungs clear. No edema.",
            "Hypertension, controlled on the current dose.",
            "Continue the current dose. Basic metabolic panel and lipid panel today. Recheck in three months.",
            new("Your blood pressure, how your medicine is working for you, and your readings at home.",
                "Your blood pressure was 126 over 80 today, which is a good range. Your heart and lungs sounded normal.",
                "Keep taking your blood pressure medicine every day. We are checking your kidneys, potassium and cholesterol with a blood test; the results will show here. Come back in three months.")),
        new("Three days of runny nose, sore throat and a mild cough. No fever at home. Eating and drinking well. A housemate had a cold last week.",
            "Temperature 37.2 C, pulse 76, oxygen 98 percent. Throat mildly red without exudate. Ears clear. Lungs clear.",
            "Viral upper respiratory infection.",
            "Rest, fluids, honey for the cough, saline nasal spray. Acetaminophen as needed. Return if fever above 38.5 C, trouble breathing, or not better in ten days.",
            new("Your cold: the runny nose, sore throat and cough you have had for three days.",
                "You had no fever and your lungs were clear. This is a common cold caused by a virus, so an antibiotic would not help.",
                "Rest, drink plenty of fluids and try honey for the cough. Call us if you get a high fever, have trouble breathing, or are not better in ten days.")),
    ];

    // One signed note each on the other panel patients the last step fills, taken in turn.
    public static readonly Note[] PanelNotes =
    [
        new("Hypertension follow-up. Taking medication daily. Occasional mild headache in the morning. No chest pain or shortness of breath.",
            "BP 134/84, pulse 72. Heart regular. Lungs clear. No edema.",
            "Hypertension, near goal.",
            "Continue current medication. Reduce salt and walk thirty minutes most days. Recheck in three months."),
        new("Diabetes check. Checking sugars at home, fasting readings 120 to 150. Walking after dinner. No low sugars, no numbness in the feet.",
            "BP 128/78, weight stable. Foot exam: skin intact, sensation normal to monofilament.",
            "Type 2 diabetes, fair control.",
            "Continue metformin. A1c ordered. Diabetic eye exam due this year. Follow up in three months."),
        new("Two days of sore throat, congestion and dry cough. No fever. Able to work.",
            "Temperature 37.0 C. Throat mildly red, no exudate. Lungs clear.",
            "Viral upper respiratory infection.",
            "Fluids, rest, throat lozenges. Return if fever or trouble breathing."),
        new("Medication review. Taking all medications as listed. No side effects reported. Uses a weekly pill organizer.",
            "BP 122/76, pulse 68. Medication list reviewed line by line with the patient.",
            "Medication list reconciled. No interactions of concern.",
            "No changes. Renewed prescriptions for ninety days. Follow up in six months."),
        new("Low back pain for one week after lifting boxes. No leg weakness, numbness or bladder problems.",
            "Tender over the lower back muscles. Straight leg raise negative. Strength and reflexes normal in both legs.",
            "Acute low back strain.",
            "Stay active, heat, ibuprofen with food for one week. Back exercises handout given. Return if not improving in four weeks."),
        new("Annual physical. Feels well. Exercises twice a week. Non-smoker. Sleeping seven hours.",
            "BP 118/74, pulse 64. Normal exam of heart, lungs and abdomen. Skin without concerning lesions.",
            "Healthy adult. Preventive care up to date except for the flu vaccine.",
            "Flu vaccine offered. Screening labs as due. Return in one year."),
    ];

    // Drafts left unsigned on two charts: the visit written up as far as the exam.
    public static readonly Note Draft = new(
        "Follow-up visit. Doing better since the last visit. Questions about the new medication.",
        "BP 130/82, pulse 74. No distress.",
        "",
        "");

    // The test patient's two conversations, one started from each side, each with its reply.
    public static readonly (string Subject, string First, string Reply) PatientAsks = (
        "Home blood pressure readings",
        "Hello, I have been checking my blood pressure at home in the mornings. Most readings are around 125 over 80, with one at 138 over 88 after a poor night of sleep. Is that all right, or should I change anything?",
        "Thank you for sending these. The numbers look good overall, and one higher reading after a short night is common. Keep taking your medicine and bring your log to your next visit.");

    public static readonly (string Subject, string First, string Reply) ClinicTells = (
        "Your kidney and cholesterol results",
        "Your recent blood tests are back. Your kidney function and potassium are normal on your medicine, and your cholesterol is in a healthy range. No changes are needed. You can see the full results under Test results.",
        "Thank you, that is a relief. I will keep taking it as prescribed.");

    // The test patient's answered refill request: a medication sold without a prescription, declined with a message.
    public const string DeclinedMedication = "cetirizine hydrochloride 10 MG Oral Tablet";
    public const string DeclinedNote = "Almost out, please renew.";
    public const string DeclineMessage = "Cetirizine is sold over the counter, so you can buy it at any pharmacy without a prescription. Let us know if it stops helping.";
    public const string SeenFirstMessage = "We would like to see you before renewing this one. Please book a visit, and we will review it then.";
    public const string RefillNote = "Running low, about a week left.";

    // The pre-visit forms: the PHQ-9 left to fill in, and the GAD-7 answered, with low scores.
    public const string Phq9 = "44249-1";
    public const string Gad7 = "69737-5";
    public const string Gad7FirstQuestion = "69725-0";
    public static readonly string[] Gad7Answers = ["LA6569-3", "LA6568-5", "LA6569-3", "LA6568-5", "LA6568-5", "LA6569-3", "LA6568-5"];

    // Portal accounts for a few more panel patients, so the inbox has their messages and refill requests.
    public const int Enrolled = 3;
    public const string EnrolledPassword = "local-demo-patient-1234";

    public static string EnrolledEmail(string mrn) => $"demo-{mrn.ToLowerInvariant()}@example.com";

    public static readonly (string Subject, string Body)[] EnrolledAsks =
    [
        ("Question about my new prescription", "Should I take the new tablet with food or on an empty stomach? The label does not say."),
        ("Rash after starting medication", "I noticed a mild itchy rash on my arms two days after starting the new pill. No swelling or trouble breathing. Should I stop it?"),
        ("Can I get a flu shot at my next visit?", "I have an appointment next week. Can I get my flu vaccine at the same time?"),
    ];
}

sealed class Demo(Options options)
{
    X509Certificate2 root = null!;
    Browser clinician = null!;
    bool failed;

    // The panel, by MRN number: each patient's chart ID, and the adults among them, for the adults' content.
    readonly SortedDictionary<int, Guid> panel = [];
    readonly List<int> adults = [];

    // The clinic's today and working days (the schedule's), and the clinician's own ID.
    DateOnly today;
    readonly List<DateOnly> days = [];
    Guid clinicianId;

    string FileName => Path.GetFileName(options.AccountsFile);

    public async Task<int> RunAsync()
    {
        if (!File.Exists(options.RootCertificate))
        {
            Console.Error.WriteLine($"No certificate authority at {options.RootCertificate}. Make the certificates first " +
                "(infra/compose/README.md, One-time setup), or pass --ca <path to rootCA.pem>.");
            return 1;
        }

        var accounts = AccountsFile.Load(options.AccountsFile);
        var staff = accounts.Find("clinician");
        var patient = accounts.All("patient").FirstOrDefault(a => a.Mrn == Content.PatientMrn);
        if (staff?.AuthenticatorSecret is null || patient is null)
        {
            Console.Error.WriteLine($"{options.AccountsFile} has no clinician with an authenticator, or no portal account for MRN " +
                $"{Content.PatientMrn}. Set up the test accounts first: dotnet run test-accounts.cs -- setup");
            return 1;
        }

        root = X509Certificate2.CreateFromPem(File.ReadAllText(options.RootCertificate));
        Console.WriteLine($"Stack: {options.Clinical.GetLeftPart(UriPartial.Authority)} (clinical), {options.Portal.GetLeftPart(UriPartial.Authority)} (portal)");
        var started = DateTimeOffset.UtcNow;
        try
        {
            clinician = await StaffSignInAsync(staff);
        }
        catch (Exception e) when (e is Failure or HttpRequestException or TaskCanceledException)
        {
            Console.Error.WriteLine($"Can't sign in as the clinician {staff.Email}: {(e is Failure ? e.Message : Explain(e))}");
            return 1;
        }

        using (clinician)
        {
            await StepAsync("panel", PanelAsync);
            await StepAsync("schedule", ScheduleAsync);
            await StepAsync("patient", () => PatientAsync(patient));
            await StepAsync("inbox", InboxAsync);
            await StepAsync("charts", ChartsAsync);
        }

        Console.WriteLine(failed
            ? $"Not everything was added; see above. Running the script again finishes it. ({(DateTimeOffset.UtcNow - started).TotalSeconds:F0} s)"
            : $"Done in {(DateTimeOffset.UtcNow - started).TotalSeconds:F0} s.");
        return failed ? 1 : 0;
    }

    // One step: what it added, or why it couldn't. A failure stops the step, not the others.
    async Task StepAsync(string step, Func<Task<string>> run)
    {
        try
        {
            Console.WriteLine($"{step,-10} {await run()}");
        }
        catch (Skipped e)
        {
            Console.WriteLine($"{step,-10} skipped: {e.Message}");
        }
        catch (Exception e) when (e is Failure or HttpRequestException or TaskCanceledException)
        {
            failed = true;
            Console.WriteLine($"{step,-10} FAILED: {(e is Failure ? e.Message : Explain(e))}");
        }
    }

    // 1. The clinician's panel: the test patient and the patients after it, each added from its chart, as a clinician
    // does: the chart first asks for a reason (an emergency access, in the audit log), then "Add to my patients".
    async Task<string> PanelAsync()
    {
        var added = 0;
        for (var number = 1; number <= Content.PanelSize; number++)
        {
            var mrn = Content.Mrn(number);
            var results = await clinician.GetAsync(Clinical($"/patients?mrn={mrn}"));
            Expect(results, "/patients", "searching for a patient by MRN");
            var row = SearchRow(results, mrn) ?? throw new Skipped($"the clinic has no patient with MRN {mrn}. Seed the synthetic " +
                "clinic first (README, \"Synthetic data and the benchmark\").");
            panel[number] = row.Id;
            if (number > 1 && row.Born.AddYears(18) <= DateOnly.FromDateTime(DateTime.Today))
            {
                adults.Add(number);
            }

            if (row.Yours)
            {
                continue;
            }

            var chart = await clinician.GetAsync(Clinical($"/patients/{row.Id}"));
            if (chart.Path.EndsWith("/reason", StringComparison.Ordinal))
            {
                chart = await clinician.PostAsync(chart.Url, chart, ("Reason", Content.PanelReason));
            }

            var assign = FormAction(chart, "Assign") ?? throw Unexpected(chart, $"opening the chart of MRN {mrn}");
            chart = await clinician.PostAsync(Clinical(assign), chart);
            if (FormAction(chart, "Unassign") is null)
            {
                throw Unexpected(chart, $"adding MRN {mrn} to my patients");
            }

            added++;
        }

        return $"{panel.Count} patients on My patients ({Content.Mrn(1)} to {Content.Mrn(Content.PanelSize)}): {added} added, " +
            $"{panel.Count - added} there already";
    }

    // 2. The schedule: today and the next four working days, each day's plan booked where its time is free; then a few
    // of today's visits checked in. The app refuses a time that has passed, so nothing is booked in the past.
    async Task<string> ScheduleAsync()
    {
        RequirePanel();
        await ClinicDaysAsync();
        var lines = new List<string>();
        foreach (var day in days)
        {
            var (booked, passed, taken) = (0, 0, 0);
            var shown = await ScheduleDayAsync(day);
            foreach (var (start, minutes, reason, patient) in Content.Day(day))
            {
                var mrn = Content.Mrn(patient);
                if (shown.Any(a => a.Start == start && a.Mrn == mrn))
                {
                    continue;
                }

                // A time taken already, by hand, is left as it is.
                switch (await BookAsync(patient, day, start, minutes, reason))
                {
                    case "booked": booked++; break;
                    case "passed": passed++; break;
                    default: taken++; break;
                }
            }

            var after = await ScheduleDayAsync(day);
            var line = $"{day:ddd yyyy-MM-dd}: {after.Count(a => a.Status != "cancelled")} appointments, {booked} booked now";
            if (passed > 0)
            {
                line += $", {passed} not booked as their time had passed";
            }

            if (taken > 0)
            {
                line += $", {taken} not booked as the time was taken";
            }

            if (day == today)
            {
                var checkedIn = 0;
                foreach (var appointment in after.Where(a => a.Status == "booked").Take(Math.Max(0, Content.CheckedIn - after.Count(a => a.Status == "fulfilled"))))
                {
                    var open = await clinician.GetAsync(Clinical($"/appointments/{appointment.Id}"));
                    var done = await clinician.PostAsync(Clinical($"/appointments/{appointment.Id}?handler=CheckIn"), open, ("Version", Version(open)));
                    if (!done.Html.Contains("Checked in.", StringComparison.Ordinal))
                    {
                        throw Unexpected(done, $"checking in the {appointment.Start:HH:mm} appointment");
                    }

                    checkedIn++;
                }

                line += $", {Math.Min(Content.CheckedIn, after.Count(a => a.Status == "fulfilled") + checkedIn)} checked in ({checkedIn} now)";
            }

            lines.Add(line);
        }

        return string.Join(Environment.NewLine + new string(' ', 11), lines);
    }

    // 3. The test patient, the account a visitor signs in to the portal as: the clinician's side first, then the
    // patient's. Whatever the chart has already, by hand or from the seed, stays as it is.
    async Task<string> PatientAsync(Account account)
    {
        RequirePanel();
        await ClinicDaysAsync();
        var id = panel[1];
        using var portal = await PortalSignInAsync(account);
        var lines = new List<string>
        {
            await ListsAsync(id),
            $"vital signs: {Content.PatientVitals(today).Count()} sets over the past year ({await VitalsAsync(id, Content.PatientVitals(today))} added now)",
            await LabsAsync(id, Content.PatientLabs, acknowledge: true),
        };
        var (signed, summaries) = (0, 0);
        foreach (var note in Content.PatientNotes)
        {
            var (isNew, summary) = await NoteAsync(id, note, sign: true);
            signed += isNew ? 1 : 0;
            summaries += summary ? 1 : 0;
        }

        lines.Add($"notes: {Content.PatientNotes.Length} signed with visit summaries released ({signed} notes and {summaries} summaries added now)");
        lines.Add(await ConversationsAsync(portal, id));
        lines.Add(await RefillsAsync(portal, id));
        lines.Add(await VisitAndFormsAsync(portal));
        return string.Join(Environment.NewLine + new string(' ', 11), lines);
    }

    // The chart's allergies, medications and diagnoses: one added to each kind that has fewer than three.
    async Task<string> ListsAsync(Guid id)
    {
        var chart = await ChartAsync(id);
        var said = new List<string>();
        foreach (var (list, name, fields) in Content.PatientLists)
        {
            var count = Regex.Matches(chart.Html, $@"data-change-entry=""{list}""").Count;
            if (count >= 3 || WebUtility.HtmlDecode(chart.Html).Contains($"<strong>{name}</strong>", StringComparison.OrdinalIgnoreCase))
            {
                said.Add($"{count} {list}");
                continue;
            }

            var url = Clinical($"/patients/{id}/{list}");
            var form = await clinician.GetAsync(url);
            var entry = Regex.Match(form.Html, @"name=""NewEntryId"" value=""([^""]+)""");
            if (!entry.Success)
            {
                throw Unexpected(form, $"opening the form to add to {list}");
            }

            var done = await clinician.PostAsync(url, form, [("Name", name), ("NewEntryId", entry.Groups[1].Value), .. fields]);
            if (done.Path != $"/patients/{id}")
            {
                throw Unexpected(done, $"adding {name} to {list}");
            }

            said.Add($"{count + 1} {list} ({name} added)");
        }

        return "chart lists: " + string.Join(", ", said);
    }

    // Vital signs at the times given, each set once: a set already on the flowsheet at that time is left as it is.
    async Task<int> VitalsAsync(Guid id, IEnumerable<(DateTime TakenAt, (string Code, string Value)[] Values)> sets)
    {
        var url = Clinical($"/patients/{id}/vitals");
        var flowsheet = await clinician.GetAsync(Clinical($"/patients/{id}/vitals?earlier=true"));
        Expect(flowsheet, url.AbsolutePath, "opening the vital signs");
        var added = 0;
        foreach (var (takenAt, values) in sets)
        {
            if (flowsheet.Html.Contains($">{takenAt:yyyy-MM-dd HH:mm} ", StringComparison.Ordinal))
            {
                continue;
            }

            var done = await clinician.PostAsync(url, flowsheet,
                [.. values.Select(v => ($"Values[{v.Code}]", v.Value)), ("TakenAt", takenAt.ToString("yyyy-MM-ddTHH:mm", CultureInfo.InvariantCulture))]);
            if (done.Url.Query.Contains("saved=true", StringComparison.OrdinalIgnoreCase))
            {
                added++;
            }
            else if (!done.Html.Contains("already recorded for that time", StringComparison.Ordinal))
            {
                throw Unexpected(done, $"recording vital signs taken {takenAt:yyyy-MM-dd HH:mm}");
            }
        }

        return added;
    }

    // Lab orders, each signed once, its results entered on its results page as the lab reported them (the stack's
    // simulated lab files them itself when it runs; then only what it left waiting is entered), and acknowledged or not.
    async Task<string> LabsAsync(Guid id, IEnumerable<Content.Lab> labs, bool acknowledge)
    {
        var (ordered, filed, acknowledged, count) = (0, 0, 0, 0);
        foreach (var lab in labs)
        {
            count++;
            var chart = await ChartAsync(id);
            var html = WebUtility.HtmlDecode(chart.Html);
            var resulted = Regex.Match(html, $@"<li data-resulted=""[\w-]+"">\s*<a href=""/patients/{id}/orders/([0-9a-f-]{{36}})/results"">{Regex.Escape(lab.Item)}</a>");
            var active = Regex.Match(html, $@"<li id=""order-([0-9a-f-]{{36}})"">\s*<strong>{Regex.Escape(lab.Item)}</strong>");
            string orderId;
            if (resulted.Success || active.Success)
            {
                orderId = (resulted.Success ? resulted : active).Groups[1].Value;
            }
            else
            {
                var url = Clinical($"/patients/{id}/orders/new?type=lab");
                var form = await clinician.GetAsync(url);
                var done = await clinician.PostAsync(url, form, ("Item", lab.Item), ("Details[indication]", lab.Indication), ("Details[notes]", ""));
                var signed = Regex.Match(done.Url.Query, @"ordered=([0-9a-f-]{36})");
                if (done.Path != $"/patients/{id}" || !signed.Success)
                {
                    throw Unexpected(done, $"ordering {lab.Item}");
                }

                orderId = signed.Groups[1].Value;
                ordered++;
            }

            var resultsUrl = Clinical($"/patients/{id}/orders/{orderId}/results");
            var page = await clinician.GetAsync(resultsUrl);
            Expect(page, resultsUrl.AbsolutePath, $"opening the results of {lab.Item}");
            var waiting = Regex.Matches(page.Html, @"name=""Values\[([^\]]+)\]""").Select(m => m.Groups[1].Value).ToList();
            if (waiting.Count > 0 && !page.Html.Contains("data-acknowledged", StringComparison.Ordinal))
            {
                var missing = waiting.Where(code => !lab.Values.ContainsKey(code)).ToList();
                if (missing.Count > 0)
                {
                    throw new Failure($"{lab.Item} reports tests this script has no value for: {string.Join(", ", missing)}.");
                }

                page = await clinician.PostAsync(resultsUrl, page,
                    [.. waiting.Select(code => ($"Values[{code}]", lab.Values[code])), ("CollectedAt", Collected().ToString("yyyy-MM-ddTHH:mm", CultureInfo.InvariantCulture))]);
                if (!page.Url.Query.Contains("done=filed", StringComparison.Ordinal))
                {
                    throw Unexpected(page, $"entering the results of {lab.Item}");
                }

                filed++;
            }

            if (acknowledge && !page.Html.Contains("data-acknowledged", StringComparison.Ordinal))
            {
                var seen = Regex.Match(page.Html, @"name=""Seen"" value=""([^""]*)""");
                if (!seen.Success)
                {
                    throw Unexpected(page, $"reading the results of {lab.Item} to acknowledge them");
                }

                page = await clinician.PostAsync(Clinical($"{resultsUrl.AbsolutePath}?handler=Acknowledge"), page, ("Seen", WebUtility.HtmlDecode(seen.Groups[1].Value)));
                if (!page.Html.Contains("data-acknowledged", StringComparison.Ordinal))
                {
                    throw Unexpected(page, $"acknowledging the results of {lab.Item}");
                }

                acknowledged++;
            }
        }

        return $"lab results: {count} orders signed, resulted and {(acknowledge ? "acknowledged" : "left to acknowledge")} " +
            $"({ordered} ordered, {filed} resulted{(acknowledge ? $", {acknowledged} acknowledged" : "")} now)";
    }

    // When the results were collected: yesterday morning, on the clinic's clock.
    DateTime Collected() => today.AddDays(-1).ToDateTime(new TimeOnly(9, 15));

    // A note found by its subjective section, or written: signed, with its visit summary, or left as a draft. Whether
    // the note and its summary were added now.
    async Task<(bool Note, bool Summary)> NoteAsync(Guid id, Content.Note note, bool sign)
    {
        var chart = await ChartAsync(id);
        string? noteId = null;
        foreach (Match signed in Regex.Matches(chart.Html, $@"href=""/patients/{id}/notes/([0-9a-f-]{{36}})"""))
        {
            var view = await clinician.GetAsync(Clinical($"/patients/{id}/notes/{signed.Groups[1].Value}"));
            if (WebUtility.HtmlDecode(view.Html).Contains(note.Subjective, StringComparison.Ordinal))
            {
                noteId = signed.Groups[1].Value;
                break;
            }
        }

        var isNew = false;
        if (noteId is null)
        {
            Page? draft = null;
            foreach (Match link in Regex.Matches(chart.Html, $@"href=""(/patients/{id}/notes/drafts/[0-9a-f-]{{36}})"""))
            {
                var page = await clinician.GetAsync(Clinical(link.Groups[1].Value));
                if (WebUtility.HtmlDecode(page.Html).Contains(note.Subjective, StringComparison.Ordinal))
                {
                    draft = page;
                    break;
                }
            }

            if (draft is null)
            {
                draft = await clinician.PostAsync(Clinical($"/patients/{id}?handler=NewNote"), chart);
                if (!Regex.IsMatch(draft.Path, $@"^/patients/{id}/notes/drafts/[0-9a-f-]{{36}}$"))
                {
                    throw Unexpected(draft, "starting a note");
                }

                draft = await clinician.PostAsync(Clinical($"{draft.Path}?handler=Save"), draft, ("Version", Version(draft)),
                    ("Subjective", note.Subjective), ("Objective", note.Objective), ("Assessment", note.Assessment), ("Plan", note.Plan));
                if (draft.Status != HttpStatusCode.OK || !WebUtility.HtmlDecode(draft.Html).Contains(note.Subjective, StringComparison.Ordinal))
                {
                    throw Unexpected(draft, "saving a draft note");
                }
            }

            if (!sign)
            {
                return (true, false);
            }

            var confirm = await clinician.PostAsync(Clinical($"{draft.Path}?handler=Review"), draft, ("Version", Version(draft)),
                ("Subjective", note.Subjective), ("Objective", note.Objective), ("Assessment", note.Assessment), ("Plan", note.Plan));
            if (!confirm.Path.EndsWith("/confirm", StringComparison.Ordinal))
            {
                throw Unexpected(confirm, "reviewing a note to sign it");
            }

            var view = await clinician.PostAsync(confirm.Url, confirm, ("Version", Version(confirm)), ("Confirmed", "true"));
            var signedNote = Regex.Match(view.Path, $@"^/patients/{id}/notes/([0-9a-f-]{{36}})$");
            if (!signedNote.Success)
            {
                throw Unexpected(view, "signing a note");
            }

            noteId = signedNote.Groups[1].Value;
            isNew = true;
        }

        if (note.Summary is not { } summary)
        {
            return (isNew, false);
        }

        var url = Clinical($"/patients/{id}/notes/{noteId}/summary");
        var noteView = await clinician.GetAsync(Clinical($"/patients/{id}/notes/{noteId}"));
        if (noteView.Html.Contains(@"data-summary=""standing""", StringComparison.Ordinal))
        {
            return (isNew, false);
        }

        var form = await clinician.GetAsync(url);
        var released = await clinician.PostAsync(url, form, ("TalkedAbout", summary.TalkedAbout), ("Found", summary.Found), ("NextSteps", summary.NextSteps));
        if (!released.Html.Contains(@"data-summary=""standing""", StringComparison.Ordinal))
        {
            throw Unexpected(released, "releasing a visit summary");
        }

        return (isNew, true);
    }

    // A conversation started from each side, each with the other side's reply.
    async Task<string> ConversationsAsync(Browser portal, Guid id)
    {
        var added = 0;
        var asks = await PortalThreadAsync(portal, Content.PatientAsks.Subject);
        if (asks is null)
        {
            asks = await PortalStartAsync(portal, Content.PatientAsks.Subject, Content.PatientAsks.First);
            added++;
        }

        // The clinician opens it to answer, which marks it read.
        var thread = Clinical($"/patients/{id}/messages/{asks}");
        var page = await clinician.GetAsync(thread);
        if (!page.Html.Contains(@"data-message=""staff""", StringComparison.Ordinal))
        {
            Sent(await clinician.PostAsync(thread, page, ("Body", Content.PatientAsks.Reply)), "answering the patient's message");
            added++;
        }

        var tells = await PortalThreadAsync(portal, Content.ClinicTells.Subject);
        if (tells is null)
        {
            var url = Clinical($"/patients/{id}/messages/new");
            var form = await clinician.GetAsync(url);
            var sent = Sent(await clinician.PostAsync(url, form, ("Subject", Content.ClinicTells.Subject), ("Body", Content.ClinicTells.First)),
                "messaging the patient");
            tells = Regex.Match(sent.Path, @"/messages/([0-9a-f-]{36})$").Groups[1].Value;
            added++;
        }

        var reply = Portal($"/portal/messages/{tells}");
        page = await PortalWaitAsync(portal, reply, p => p.Status == HttpStatusCode.OK);
        if (!page.Html.Contains(@"data-message=""patient""", StringComparison.Ordinal))
        {
            Sent(await portal.PostAsync(reply, page, ("Body", Content.ClinicTells.Reply)), "answering the clinician's message");
            added++;
        }

        return $"messages: \"{Content.PatientAsks.Subject}\" from the patient, answered; \"{Content.ClinicTells.Subject}\" from the " +
            $"clinician, answered and not yet read by the clinician ({added} messages sent now)";
    }

    // The ID of the patient's conversation with this subject, or null.
    async Task<string?> PortalThreadAsync(Browser portal, string subject)
    {
        var list = await portal.GetAsync(Portal("/portal/messages"));
        return Regex.Matches(list.Html, @"href=""/portal/messages/([0-9a-f-]{36})""[^>]*>([^<]*)</a>")
            .Where(m => WebUtility.HtmlDecode(m.Groups[2].Value).Trim() == subject)
            .Select(m => m.Groups[1].Value)
            .FirstOrDefault();
    }

    async Task<string> PortalStartAsync(Browser portal, string subject, string body)
    {
        var url = Portal("/portal/messages/new");
        var sent = Sent(await portal.PostAsync(url, await portal.GetAsync(url), ("Subject", subject), ("Body", body)), "sending a message from the portal");
        return Regex.Match(sent.Path, @"/portal/messages/([0-9a-f-]{36})$").Groups[1].Value;
    }

    // A portal page read after the clinician changed what it shows. The portal reads the cloud replica, which can be a
    // moment behind the clinic's server: it is read again, for up to ten seconds, until it shows the change.
    static async Task<Page> PortalWaitAsync(Browser portal, Uri url, Func<Page, bool> shows)
    {
        var page = await portal.GetAsync(url);
        for (var attempt = 0; attempt < 10 && !shows(page); attempt++)
        {
            await Task.Delay(TimeSpan.FromSeconds(1));
            page = await portal.GetAsync(url);
        }

        return page;
    }

    static Page Sent(Page page, string step) =>
        page.Url.Query.Contains("sent=True", StringComparison.OrdinalIgnoreCase) ? page : throw Unexpected(page, step);

    // A refill request answered (declined, with a message) and one waiting, unless the patient has each already.
    async Task<string> RefillsAsync(Browser portal, Guid id)
    {
        var said = new List<string>();
        var (requests, offered) = await PortalRefillsAsync(portal);
        if (!requests.Any(r => r.Status is "completed" or "rejected"))
        {
            // The chart lists, above, may have added the medication just now: the replica the portal reads can be a
            // moment behind, so the portal is read again until it shows it, for up to ten seconds.
            for (var attempt = 0; attempt < 10 && !offered.ContainsKey(Content.DeclinedMedication) &&
                !requests.Any(r => r.Medication == Content.DeclinedMedication); attempt++)
            {
                await Task.Delay(TimeSpan.FromSeconds(1));
                (requests, offered) = await PortalRefillsAsync(portal);
            }

            // The medication sold without a prescription if the chart has it (the lists, above, add it where there is
            // room); otherwise the first the portal offers, declined until the patient is seen.
            var (medication, message) = requests.Any(r => r.Status == "requested" && r.Medication == Content.DeclinedMedication) ||
                offered.ContainsKey(Content.DeclinedMedication)
                    ? (Content.DeclinedMedication, Content.DeclineMessage)
                    : (offered.Keys.FirstOrDefault(), Content.SeenFirstMessage);
            if (medication is null)
            {
                said.Add("none answered, and none of the patient's medications can be asked for");
            }
            else
            {
                if (!requests.Any(r => r.Status == "requested" && r.Medication == medication))
                {
                    await AskRefillAsync(portal, offered, medication, Content.DeclinedNote);
                }

                await DeclineAsync(id, medication, message);
                said.Add($"{medication} asked for and declined now");
                for (var attempt = 0; attempt < 10 && !requests.Any(r => r.Status is "completed" or "rejected"); attempt++)
                {
                    // The portal reads the cloud replica, which can be a moment behind the clinic's server.
                    await Task.Delay(TimeSpan.FromSeconds(attempt == 0 ? 0 : 1));
                    (requests, offered) = await PortalRefillsAsync(portal);
                }
            }
        }

        if (!requests.Any(r => r.Status == "requested"))
        {
            var medication = offered.Keys.FirstOrDefault();
            if (medication is null)
            {
                said.Add("no request waiting, and none of the patient's medications can be asked for");
            }
            else
            {
                await AskRefillAsync(portal, offered, medication, Content.RefillNote);
                said.Add($"{medication} asked for now");
                (requests, _) = await PortalRefillsAsync(portal);
            }
        }

        return $"refills: {requests.Count(r => r.Status == "requested")} waiting, {requests.Count(r => r.Status is "completed" or "rejected")} answered" +
            (said.Count > 0 ? $" ({string.Join("; ", said)})" : "");
    }

    // The patient's refill requests, and the medications the portal offers to ask for, by name: their form values.
    async Task<(List<(string Medication, string Status)> Requests, Dictionary<string, string> Offered)> PortalRefillsAsync(Browser portal)
    {
        var page = await portal.GetAsync(Portal("/portal/refills"));
        Expect(page, "/portal/refills", "opening the refills page");
        var requests = Regex.Matches(page.Html, @"data-refill=""(\w+)"">\s*<h3>([^<]*)</h3>")
            .Select(m => (WebUtility.HtmlDecode(m.Groups[2].Value).Trim(), m.Groups[1].Value)).ToList();
        var offered = new Dictionary<string, string>();
        foreach (Match m in Regex.Matches(page.Html, @"name=""Medication"" value=""([^""]+)""[^>]*>\s*<span>([^<]*)"))
        {
            offered.TryAdd(WebUtility.HtmlDecode(m.Groups[2].Value).Trim(), WebUtility.HtmlDecode(m.Groups[1].Value));
        }

        return (requests, offered);
    }

    async Task AskRefillAsync(Browser portal, Dictionary<string, string> offered, string medication, string note)
    {
        if (!offered.TryGetValue(medication, out var value))
        {
            throw new Failure($"the portal doesn't offer {medication} for a refill (it offers: {string.Join(", ", offered.Keys)}).");
        }

        var url = Portal("/portal/refills");
        var done = await portal.PostAsync(url, await portal.GetAsync(url), ("Medication", value), ("Note", note));
        if (!done.Url.Query.Contains("sent=True", StringComparison.OrdinalIgnoreCase))
        {
            throw Unexpected(done, $"asking for a refill of {medication}");
        }
    }

    // Declines the patient's waiting request for the medication, from the clinician's inbox, where requests arrive.
    async Task DeclineAsync(Guid id, string medication, string message)
    {
        var inbox = await clinician.GetAsync(Clinical("/inbox"));
        foreach (Match link in Regex.Matches(inbox.Html, $@"href=""(/patients/{id}/refills/[0-9a-f-]{{36}})"""))
        {
            var review = await clinician.GetAsync(Clinical(link.Groups[1].Value));
            if (!review.Html.Contains("data-refill-decline", StringComparison.Ordinal) ||
                !WebUtility.HtmlDecode(review.Html).Contains($"<dd>{medication}", StringComparison.Ordinal))
            {
                continue;
            }

            var done = await clinician.PostAsync(Clinical($"{link.Groups[1].Value}?handler=Decline"), review, ("Message", message));
            if (!done.Html.Contains("data-refill-answered", StringComparison.Ordinal))
            {
                throw Unexpected(done, $"declining the refill of {medication}");
            }

            return;
        }

        throw new Failure($"the inbox has no waiting refill request for {medication} to answer.");
    }

    // An appointment coming up (booked if the chart has none), a pre-visit form to fill in for it, and one answered.
    async Task<string> VisitAndFormsAsync(Browser portal)
    {
        var id = panel[1];
        var said = new List<string>();
        var chart = await ChartAsync(id);
        if (chart.Html.Contains("No upcoming appointments.", StringComparison.Ordinal))
        {
            var (start, minutes, reason) = Content.PatientVisit;
            said.Add($"appointment {await BookAsync(1, days[2], start, minutes, reason)} on {days[2]:yyyy-MM-dd} at {start:HH:mm}");
        }

        var forms = await portal.GetAsync(Portal("/portal/forms"));
        var open = Regex.Matches(forms.Html, @"data-form=""open""").Count;
        var sent = Regex.Matches(forms.Html, @"data-form=""sent""").Count;
        if (sent == 0)
        {
            said.Add($"GAD-7 {await AskFormAsync(id, Content.Gad7)}");
            forms = await PortalWaitAsync(portal, Portal("/portal/forms"), p => p.Html.Contains(@"data-form=""open""", StringComparison.Ordinal));
            foreach (Match link in Regex.Matches(forms.Html, @"href=""(/portal/forms/[0-9a-f-]{36})"""))
            {
                var form = await portal.GetAsync(Portal(link.Groups[1].Value));
                if (!form.Html.Contains($@"name=""Answers[{Content.Gad7FirstQuestion}]""", StringComparison.Ordinal) ||
                    !form.Html.Contains(@"type=""radio""", StringComparison.Ordinal))
                {
                    continue;
                }

                var questions = Regex.Matches(form.Html, @"name=""Answers\[([^\]]+)\]""").Select(m => m.Groups[1].Value).Distinct().ToList();
                var done = await portal.PostAsync(Portal(link.Groups[1].Value), form,
                    [.. questions.Select((code, i) => ($"Answers[{code}]", Content.Gad7Answers[i % Content.Gad7Answers.Length]))]);
                if (!done.Html.Contains("Sent.", StringComparison.Ordinal))
                {
                    throw Unexpected(done, "answering the GAD-7");
                }

                said.Add("GAD-7 answered");
                break;
            }
        }

        if (open == 0)
        {
            said.Add($"PHQ-9 {await AskFormAsync(id, Content.Phq9)}");
        }

        forms = await PortalWaitAsync(portal, Portal("/portal/forms"),
            p => p.Html.Contains(@"data-form=""open""", StringComparison.Ordinal) && p.Html.Contains(@"data-form=""sent""", StringComparison.Ordinal));
        return $"appointments and forms: {(chart.Html.Contains("No upcoming appointments.", StringComparison.Ordinal) ? "" : "an appointment coming up, ")}" +
            $"{Regex.Matches(forms.Html, @"data-form=""open""").Count} pre-visit forms to fill in, {Regex.Matches(forms.Html, @"data-form=""sent""").Count} answered" +
            (said.Count > 0 ? $" ({string.Join("; ", said)})" : "");
    }

    // Asks the patient to fill in a questionnaire before their soonest appointment: "asked", or "asked already".
    async Task<string> AskFormAsync(Guid id, string questionnaire)
    {
        var url = Clinical($"/patients/{id}/forms");
        var page = await clinician.GetAsync(url);
        var appointment = Regex.Match(page.Html, @"name=""AppointmentId""[^>]*>\s*<option value=""([0-9a-f-]{36})""");
        if (!appointment.Success)
        {
            throw Unexpected(page, "opening the pre-visit forms, which need an appointment coming up");
        }

        var done = await clinician.PostAsync(Clinical($"/patients/{id}/forms?handler=Ask"), page, ("AppointmentId", appointment.Groups[1].Value),
            ("Questionnaire", questionnaire));
        return done.Html.Contains("Form asked for.", StringComparison.Ordinal) ? "asked"
            : done.Html.Contains("already asked for", StringComparison.Ordinal) ? "asked already"
            : throw Unexpected(done, $"asking for questionnaire {questionnaire}");
    }

    // 4. Work in the inbox: results to acknowledge on the first adult panel patients, messages and refill requests from
    // a few panel patients given portal accounts here, and drafts on two charts.
    async Task<string> InboxAsync()
    {
        RequirePanel();
        await ClinicDaysAsync();
        var lines = new List<string>();
        var labs = adults.Take(Content.InboxLabs.Length).Zip(Content.InboxLabs).ToList();
        var resulted = new List<string>();
        foreach (var (patient, lab) in labs)
        {
            await LabsAsync(panel[patient], [lab], acknowledge: false);
            resulted.Add($"{Content.Mrn(patient)} {lab.Item}");
        }

        lines.Add($"results to acknowledge: {string.Join(", ", resulted)}");

        var demo = AccountsFile.Load(options.DemoAccountsFile);
        var asks = 0;
        foreach (var (patient, (subject, body)) in adults.Take(Content.Enrolled).Zip(Content.EnrolledAsks))
        {
            var mrn = Content.Mrn(patient);
            using var portal = await EnrolledAsync(demo, patient);
            if (portal is null)
            {
                lines.Add($"{mrn}: has a portal account this script didn't make, so it sends nothing");
                continue;
            }

            if (await PortalThreadAsync(portal, subject) is null)
            {
                await PortalStartAsync(portal, subject, body);
                asks++;
            }

            var (requests, offered) = await PortalRefillsAsync(portal);
            var refill = "no medication the portal offers for a refill";
            if (requests.Any(r => r.Status == "requested"))
            {
                refill = "a refill request waiting";
            }
            else if (offered.Keys.FirstOrDefault() is { } medication)
            {
                await AskRefillAsync(portal, offered, medication, Content.RefillNote);
                refill = $"a refill request for {medication} sent now";
            }

            lines.Add($"{mrn} ({Content.EnrolledEmail(mrn)}): portal account, message \"{subject}\" unread, {refill}");
        }

        var drafts = 0;
        foreach (var patient in adults.Skip(Content.PanelNotes.Length).Take(2))
        {
            var chart = await ChartAsync(panel[patient]);
            if (!chart.Html.Contains("<h3>Your drafts</h3>", StringComparison.Ordinal))
            {
                await NoteAsync(panel[patient], Content.Draft, sign: false);
                drafts++;
            }
        }

        lines.Add($"drafts: on {string.Join(" and ", adults.Skip(Content.PanelNotes.Length).Take(2).Select(Content.Mrn))} ({drafts} started now); " +
            $"{asks} portal messages sent now");
        var inbox = await clinician.GetAsync(Clinical("/inbox"));
        var kinds = Regex.Matches(inbox.Html, @"data-inbox-item=""([\w-]+)""").GroupBy(m => m.Groups[1].Value).Select(g => $"{g.Count()} {g.Key}");
        lines.Add($"inbox now: {string.Join(", ", kinds)}");
        return string.Join(Environment.NewLine + new string(' ', 11), lines);
    }

    // The panel patient's portal account from demo-accounts.json, signed in; or a new one, enrolled from the chart, as
    // test-accounts.cs does; or null when the patient has an account this script didn't make.
    async Task<Browser?> EnrolledAsync(AccountsFile demo, int patient)
    {
        var mrn = Content.Mrn(patient);
        var email = Content.EnrolledEmail(mrn);
        // One the stack no longer has (after a reset, down -v) is enrolled again, with the same address and password.
        if (demo.Find("patient", email) is { } saved && await DemoSignInAsync(saved) is { } signedIn)
        {
            return signedIn;
        }

        var before = await MailAsync(email);
        var chart = await ChartAsync(panel[patient]);
        var enroll = FormAction(chart, "Enroll") ?? throw Unexpected(chart, $"opening the chart of MRN {mrn} to enroll it");
        var issued = await clinician.PostAsync(Clinical(enroll), chart);
        var code = Regex.Match(issued.Html, @"<p class=""mono-code"">\s*([^<]+?)\s*</p>");
        if (!code.Success)
        {
            throw Unexpected(issued, "issuing an enrollment code");
        }

        var portal = new Browser(root);
        var page = await portal.PostAsync(Portal("/portal/register"), await portal.GetAsync(Portal("/portal/register")),
            ("Input.Code", WebUtility.HtmlDecode(code.Groups[1].Value)), ("Input.Email", email),
            ("Input.Password", Content.EnrolledPassword), ("Input.Confirm", Content.EnrolledPassword));
        if (!page.Html.Contains("Check your email", StringComparison.Ordinal))
        {
            portal.Dispose();
            return page.Html.Contains("enrollment code is not valid", StringComparison.Ordinal) ? null : throw Unexpected(page, "registering at the portal");
        }

        var account = new Account { Role = "patient", Email = email, Password = Content.EnrolledPassword, Mrn = mrn };
        demo.Accounts.RemoveAll(a => a.Email == email);
        demo.Accounts.Add(account);
        demo.Save(options.DemoAccountsFile);
        await ConfirmAsync(portal, email, before);
        portal.Dispose();
        return await DemoSignInAsync(account) ?? throw new Failure($"can't sign in to the portal as {email} just after enrolling it.");
    }

    // Signs in with an account this script enrolled, confirming its address first if an earlier run stopped before; null
    // when the stack refuses its password.
    async Task<Browser?> DemoSignInAsync(Account account)
    {
        var portal = new Browser(root);
        var page = await portal.PostAsync(Portal("/portal/sign-in"), await portal.GetAsync(Portal("/portal/sign-in")),
            ("Input.Email", account.Email), ("Input.Password", account.Password));
        if (page.Html.Contains("Confirm your email address first", StringComparison.Ordinal))
        {
            await ConfirmAsync(portal, account.Email, new HashSet<string>());
            page = await portal.PostAsync(Portal("/portal/sign-in"), await portal.GetAsync(Portal("/portal/sign-in")),
                ("Input.Email", account.Email), ("Input.Password", account.Password));
        }

        if (page.Path != "/portal")
        {
            portal.Dispose();
            return null;
        }

        return portal;
    }

    // The confirmation link from the newest registration email in Mailpit that wasn't there before, then its button.
    async Task ConfirmAsync(Browser portal, string email, IReadOnlySet<string> before)
    {
        for (var attempt = 0; attempt < 30; attempt++)
        {
            foreach (var id in (await MailAsync(email)).Where(id => !before.Contains(id)))
            {
                using var message = JsonDocument.Parse(await MailpitAsync($"api/v1/message/{id}"));
                var link = Regex.Match(message.RootElement.GetProperty("Text").GetString() ?? "", @"https?://\S+/portal/confirm\?\S+");
                if (link.Success)
                {
                    var confirm = Portal(new Uri(link.Value).PathAndQuery);
                    var page = await portal.PostAsync(confirm, await portal.GetAsync(confirm));
                    if (!page.Html.Contains("Email address confirmed.", StringComparison.Ordinal))
                    {
                        throw Unexpected(page, "confirming the email address");
                    }

                    return;
                }
            }

            await Task.Delay(TimeSpan.FromSeconds(1));
        }

        throw new Failure($"no confirmation email for {email} reached Mailpit ({options.Mailpit}) within 30 seconds.");
    }

    // The IDs of the emails Mailpit holds for an address.
    async Task<HashSet<string>> MailAsync(string email)
    {
        string json;
        try
        {
            json = await MailpitAsync("api/v1/messages?limit=100");
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException)
        {
            throw new Failure($"Mailpit doesn't answer at {options.Mailpit} ({Explain(e)}), so the confirmation email can't be read.");
        }

        using var list = JsonDocument.Parse(json);
        return [.. list.RootElement.GetProperty("messages").EnumerateArray()
            .Where(m => m.GetProperty("To").EnumerateArray().Any(to => string.Equals(to.GetProperty("Address").GetString(), email, StringComparison.OrdinalIgnoreCase)))
            .Select(m => m.GetProperty("ID").GetString()!)];
    }

    async Task<string> MailpitAsync(string path)
    {
        using var client = new HttpClient(new SocketsHttpHandler { UseProxy = false }) { Timeout = TimeSpan.FromSeconds(10) };
        return await client.GetStringAsync(new Uri(options.Mailpit, path));
    }

    // 5. Lived-in charts: vital signs and a signed note on the first adult panel patients after the test patient.
    async Task<string> ChartsAsync()
    {
        RequirePanel();
        await ClinicDaysAsync();
        var lines = new List<string>();
        foreach (var (patient, i) in adults.Take(Content.PanelNotes.Length).Select((p, i) => (p, i)))
        {
            var id = panel[patient];
            var vitals = await VitalsAsync(id, PanelVitals(i));
            var (note, _) = await NoteAsync(id, Content.PanelNotes[i], sign: true);
            lines.Add($"{Content.Mrn(patient)}: 2 sets of vital signs ({vitals} added now), a signed note ({(note ? "added now" : "there already")})");
        }

        return string.Join(Environment.NewLine + new string(' ', 11), lines);
    }

    // Two sets for each patient, on the clinic's clock: on the 10th of the month (of the month before, until the 10th
    // has passed), so a run in the same month finds them, and four months before that.
    IEnumerable<(DateTime TakenAt, (string Code, string Value)[] Values)> PanelVitals(int i)
    {
        string[] systolic = ["134", "128", "118", "122", "126", "118"];
        string[] diastolic = ["84", "78", "76", "76", "80", "74"];
        string[] pulse = ["72", "80", "88", "68", "76", "64"];
        string[] weight = ["88.2", "96.5", "64.0", "79.3", "82.1", "70.6"];
        string[] height = ["178", "170", "165", "182", "174", "168"];
        var recent = new DateOnly(today.Year, today.Month, 10).AddMonths(today.Day > 10 ? 0 : -1);
        foreach (var (taken, change) in new[] { (recent.AddMonths(-4).ToDateTime(new TimeOnly(10, 0).AddMinutes(20 * i)), 4), (recent.ToDateTime(new TimeOnly(9, 0).AddMinutes(20 * i)), 0) })
        {
            yield return (taken,
            [
                ("8480-6", (int.Parse(systolic[i], CultureInfo.InvariantCulture) + change).ToString(CultureInfo.InvariantCulture)), ("8462-4", diastolic[i]),
                ("8867-4", pulse[i]), ("8310-5", "36.8"), ("9279-1", "16"), ("59408-5", "98"), ("29463-7", weight[i]), ("8302-2", height[i]),
            ]);
        }
    }

    void RequirePanel()
    {
        if (panel.Count < Content.PanelSize)
        {
            throw new Skipped("needs the whole panel (above).");
        }
    }

    async Task<Page> ChartAsync(Guid id)
    {
        var chart = await clinician.GetAsync(Clinical($"/patients/{id}"));
        Expect(chart, $"/patients/{id}", "opening a chart");
        return chart;
    }

    // The clinic's today, from the schedule, which opens on it; the working days from today; and the clinician's ID.
    async Task ClinicDaysAsync()
    {
        if (days.Count > 0)
        {
            return;
        }

        var page = await clinician.GetAsync(Clinical("/schedule"));
        Expect(page, "/schedule", "opening the schedule");
        today = DateOnly.ParseExact(Text(Regex.Match(page.Html, @"<h2>(.*?)</h2>").Groups[1].Value), "dddd, MMMM d, yyyy", CultureInfo.InvariantCulture);
        clinicianId = Guid.Parse(Regex.Match(page.Html, @"<option value=""([0-9a-f-]{36})"" selected=""selected""").Groups[1].Value);
        for (var day = today; days.Count < 5; day = day.AddDays(1))
        {
            if (day.DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday))
            {
                days.Add(day);
            }
        }
    }

    // Books an appointment on the clinician's own schedule: "booked", "passed" or "taken" (the time overlaps another).
    async Task<string> BookAsync(int patient, DateOnly day, TimeOnly start, int minutes, string reason)
    {
        var url = Clinical($"/patients/{panel[patient]}/appointments/new");
        var form = await clinician.GetAsync(url);
        Expect(form, url.AbsolutePath, $"opening the booking form for MRN {Content.Mrn(patient)}");
        var done = await clinician.PostAsync(url, form, ("Clinician", clinicianId.ToString()), ("Day", day.ToString("yyyy-MM-dd")),
            ("Time", start.ToString("HH:mm")), ("Minutes", minutes.ToString(CultureInfo.InvariantCulture)), ("Reason", reason));
        return done.Path == $"/patients/{panel[patient]}" ? "booked"
            : done.Problems.Any(p => p.Contains("hasn't passed", StringComparison.Ordinal)) ? "passed"
            : done.Status == HttpStatusCode.Conflict ? "taken"
            : throw Unexpected(done, $"booking MRN {Content.Mrn(patient)} on {day:yyyy-MM-dd} at {start:HH:mm}");
    }

    // The clinician's own appointments on a day, as the schedule shows them.
    async Task<List<(TimeOnly Start, string Mrn, string Status, Guid Id)>> ScheduleDayAsync(DateOnly day)
    {
        var page = await clinician.GetAsync(Clinical($"/schedule?clinician={clinicianId}&day={day:yyyy-MM-dd}"));
        Expect(page, "/schedule", $"opening the schedule for {day:yyyy-MM-dd}");
        return [.. Regex.Matches(page.Html, @"<tr data-appointment=""(\w+)"">\s*<td class=""sc-table__mono"">(\d\d:\d\d).*?<span class=""sc-table__mono"">([^<]+)</span>.*?href=""/appointments/([0-9a-f-]{36})""", RegexOptions.Singleline)
            .Select(m => (TimeOnly.ParseExact(m.Groups[2].Value, "HH:mm", CultureInfo.InvariantCulture), WebUtility.HtmlDecode(m.Groups[3].Value), m.Groups[1].Value, Guid.Parse(m.Groups[4].Value)))];
    }

    async Task<Browser> StaffSignInAsync(Account account)
    {
        var browser = new Browser(root);
        var page = await browser.PostAsync(Clinical("/sign-in"), await browser.GetAsync(Clinical("/sign-in")),
            ("Input.Email", account.Email), ("Input.Password", account.Password));
        if (page.Path != "/sign-in/code")
        {
            browser.Dispose();
            throw new Failure($"the stack didn't ask for its authenticator code ({page.Path}, {string.Join(" ", page.Problems)}). " +
                "Run dotnet run test-accounts.cs -- setup, which says what is wrong with the account.");
        }

        page = await browser.PostAsync(Clinical("/sign-in/code"), page, ("Code", Totp.Code(account.AuthenticatorSecret!, DateTimeOffset.UtcNow)));
        if (page.Path.StartsWith("/sign-in", StringComparison.Ordinal))
        {
            browser.Dispose();
            throw new Failure($"the authenticator code from {FileName} was refused. Check this computer's clock, or run " +
                "dotnet run test-accounts.cs -- setup.");
        }

        return browser;
    }

    async Task<Browser> PortalSignInAsync(Account account)
    {
        var portal = new Browser(root);
        var page = await portal.PostAsync(Portal("/portal/sign-in"), await portal.GetAsync(Portal("/portal/sign-in")),
            ("Input.Email", account.Email), ("Input.Password", account.Password));
        if (page.Path != "/portal" || (account.Mrn is { } mrn && !page.Html.Contains($"<dd>{mrn}</dd>", StringComparison.Ordinal)))
        {
            portal.Dispose();
            throw new Failure($"can't sign in to the portal as {account.Email} ({page.Path}, {string.Join(" ", page.Problems)}). " +
                "Run dotnet run test-accounts.cs -- setup, which says what is wrong with the account.");
        }

        return portal;
    }

    Uri Clinical(string pathAndQuery) => new(options.Clinical, pathAndQuery);

    Uri Portal(string pathAndQuery) => new(options.Portal, pathAndQuery);

    static void Expect(Page page, string path, string step)
    {
        if (page.Path != path)
        {
            throw Unexpected(page, step);
        }
    }

    static Failure Unexpected(Page page, string step) =>
        new($"{step} landed on {page.Path} ({(int)page.Status}, \"{page.Heading}\"){(page.Problems.Count > 0 ? ": " + string.Join(" ", page.Problems) : "")}, not the page expected.");

    // The search result whose MRN is exactly this one: its chart's ID, and whether it is on the clinician's panel.
    static (Guid Id, bool Yours, DateOnly Born)? SearchRow(Page results, string mrn) => Regex
        .Matches(results.Html, @"<tr>(.*?)</tr>", RegexOptions.Singleline)
        .Where(row => Regex.IsMatch(row.Groups[1].Value, $@"<td[^>]*>{Regex.Escape(mrn)}</td>"))
        .Select(row => ((Guid Id, bool Yours, DateOnly Born)?)(Guid.Parse(Regex.Match(row.Groups[1].Value, @"href=""/patients/([0-9a-f-]{36})").Groups[1].Value),
            row.Groups[1].Value.Contains(@"<span class=""sc-tag"">Yours</span>", StringComparison.Ordinal),
            DateOnly.ParseExact(Regex.Match(row.Groups[1].Value, @">(\d{4}-\d\d-\d\d)</td>").Groups[1].Value, "yyyy-MM-dd", CultureInfo.InvariantCulture)))
        .FirstOrDefault();

    // Where a page's form for a page handler posts, or null when the page doesn't offer it.
    static string? FormAction(Page page, string handler)
    {
        var form = Regex.Match(page.Html, $@"<form[^>]*action=""([^""]*handler={handler}(?:&[^""]*)?)""");
        return form.Success ? WebUtility.HtmlDecode(form.Groups[1].Value) : null;
    }

    // The row version a page's change form carries.
    static string Version(Page page) => Regex.Match(page.Html, @"name=""Version""[^>]*value=""(\d+)""") is { Success: true } version
        ? version.Groups[1].Value
        : throw Unexpected(page, "reading the record's version");

    static string Text(string html) => WebUtility.HtmlDecode(Regex.Replace(Regex.Replace(html, "<[^>]+>", " "), @"\s+", " ")).Trim();

    static string Explain(Exception e) => e switch
    {
        HttpRequestException { HttpRequestError: HttpRequestError.NameResolutionError } =>
            "the name doesn't resolve. Add the hosts-file line (infra/compose/README.md, One-time setup).",
        HttpRequestException { HttpRequestError: HttpRequestError.ConnectionError } =>
            "nothing answers there. Is the stack running? (docker compose up -d, in infra/compose)",
        HttpRequestException { HttpRequestError: HttpRequestError.SecureConnectionError } =>
            "its certificate isn't one the stack's rootCA.pem vouches for (infra/compose/README.md, One-time setup).",
        TaskCanceledException => "it didn't answer in time.",
        _ => e.Message,
    };
}

// RFC 6238 time-based codes, as authenticator apps make them: HMAC-SHA1, 30-second steps, six digits.
static class Totp
{
    public static string Code(string secret, DateTimeOffset at)
    {
        Span<byte> step = stackalloc byte[8];
        BinaryPrimitives.WriteInt64BigEndian(step, at.ToUnixTimeSeconds() / 30);
        var hash = HMACSHA1.HashData(Base32(secret), step);
        var offset = hash[^1] & 0xF;
        return ((BinaryPrimitives.ReadInt32BigEndian(hash.AsSpan(offset)) & 0x7FFFFFFF) % 1_000_000).ToString("D6");
    }

    static byte[] Base32(string text)
    {
        const string Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";
        var bytes = new List<byte>();
        int buffer = 0, bits = 0;
        foreach (var c in text.ToUpperInvariant().Where(c => c is not ('=' or ' ')))
        {
            buffer = (buffer << 5) | Alphabet.IndexOf(c);
            bits += 5;
            if (bits >= 8)
            {
                bits -= 8;
                bytes.Add((byte)(buffer >> bits));
                buffer &= (1 << bits) - 1;
            }
        }

        return [.. bytes];
    }
}

// A browser for the script: cookies, redirects followed, no proxy, and HTTPS checked against the stack's own root
// certificate, whatever the operating system trusts. The host name must match, as always.
sealed class Browser : IDisposable
{
    readonly HttpClient client;

    public Browser(X509Certificate2 root)
    {
        // The stack is on this machine: a system proxy would only get in the way.
        var handler = new SocketsHttpHandler { CookieContainer = new CookieContainer(), ConnectTimeout = TimeSpan.FromSeconds(10), UseProxy = false };
        handler.SslOptions.RemoteCertificateValidationCallback = (_, certificate, _, errors) =>
        {
            if (certificate is not X509Certificate2 presented || (errors & ~SslPolicyErrors.RemoteCertificateChainErrors) != 0)
            {
                return false;
            }

            using var chain = new X509Chain();
            chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
            chain.ChainPolicy.CustomTrustStore.Add(root);
            chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
            return chain.Build(presented);
        };
        client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(60) };
    }

    public async Task<Page> GetAsync(Uri url)
    {
        using var response = await client.GetAsync(url);
        return await Page.ReadAsync(response);
    }

    // Posts a page's form, with the page's antiforgery token.
    public async Task<Page> PostAsync(Uri url, Page form, params (string Name, string Value)[] fields)
    {
        var token = Regex.Match(form.Html, @"name=""__RequestVerificationToken""[^>]*value=""([^""]+)""");
        if (!token.Success)
        {
            throw new Failure($"{form.Path} has no form to post (\"{form.Heading}\").");
        }

        using var content = new FormUrlEncodedContent(
            [.. fields.Select(f => KeyValuePair.Create(f.Name, f.Value)), KeyValuePair.Create("__RequestVerificationToken", token.Groups[1].Value)]);
        using var response = await client.PostAsync(url, content);
        return await Page.ReadAsync(response);
    }

    public void Dispose() => client.Dispose();
}

// Where a request ended up, after redirects, and what it showed.
sealed record Page(Uri Url, HttpStatusCode Status, string Html)
{
    public string Path => Url.AbsolutePath;

    public string Heading => Text(Regex.Match(Html, @"<h1[^>]*>(.*?)</h1>", RegexOptions.Singleline).Groups[1].Value);

    // The errors the page shows: the validation summary's, each field's, and an error notice.
    public IReadOnlyList<string> Problems =>
        [.. Regex.Matches(Html, @"validation-summary-errors[^>]*>(.*?)</div>|field-validation-error[^>]*>(.*?)</span>|sc-field__error[^>]*>(.*?)</p>|class=""sc-notice sc-notice--danger""[^>]*>(.*?)</(?:p|ul)>", RegexOptions.Singleline)
            .Select(m => Text(m.Groups[1].Value + m.Groups[2].Value + m.Groups[3].Value + m.Groups[4].Value))
            .Where(text => text.Length > 0)];

    public static async Task<Page> ReadAsync(HttpResponseMessage response) =>
        new(response.RequestMessage!.RequestUri!, response.StatusCode, await response.Content.ReadAsStringAsync());

    static string Text(string html) => WebUtility.HtmlDecode(Regex.Replace(Regex.Replace(html, "<[^>]+>", " "), @"\s+", " ")).Trim();
}

sealed class Failure(string message) : Exception(message);

// Not a failure: the stack lacks what the step needs, and says what to do.
sealed class Skipped(string message) : Exception(message);

// test-accounts.json (test-accounts.cs), read only; and demo-accounts.json, the portal accounts this script enrolls.
sealed class AccountsFile
{
    public List<Account> Accounts { get; set; } = [];

    public Account? Find(string role, string? email = null) =>
        Accounts.FirstOrDefault(a => a.Role == role && (email is null || string.Equals(a.Email, email, StringComparison.OrdinalIgnoreCase)));

    public IEnumerable<Account> All(string role) => Accounts.Where(a => a.Role == role);

    public static AccountsFile Load(string path) =>
        File.Exists(path) ? JsonSerializer.Deserialize(File.ReadAllText(path), AccountsJson.Default.AccountsFile) ?? new() : new();

    // Written whole to a new file, readable only by its owner on macOS and Linux, which then replaces the old one.
    public void Save(string path)
    {
        var temporary = path + ".tmp";
        File.Delete(temporary);
        var create = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write };
        if (!OperatingSystem.IsWindows())
        {
            create.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        }

        using (var stream = new FileStream(temporary, create))
        {
            JsonSerializer.Serialize(stream, this, AccountsJson.Default.AccountsFile);
        }

        File.Move(temporary, path, overwrite: true);
    }
}

sealed class Account
{
    public string Role { get; set; } = "";
    public string Email { get; set; } = "";
    public string Password { get; set; } = "";
    public string? AuthenticatorSecret { get; set; }
    public List<string>? RecoveryCodes { get; set; }
    public string? Mrn { get; set; }
}

[JsonSourceGenerationOptions(WriteIndented = true, PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(AccountsFile))]
sealed partial class AccountsJson : JsonSerializerContext
{
}
