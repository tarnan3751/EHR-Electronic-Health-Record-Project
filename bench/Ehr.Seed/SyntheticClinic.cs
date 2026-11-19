using System.Buffers.Binary;
using Ehr.Data.Patients;
using Ehr.Data.Reference;

namespace Ehr.Seed;

// A synthetic clinic, the same every time (NFR-13: synthetic data only). Patient number i is always the same patient,
// with the same allergies, medications, diagnoses and visits, whatever the clinic's size, so the benchmark knows whom
// to search for and whose chart to open without reading anyone. Obviously synthetic: every name carries a number, as
// the Synthea generator's do ("Larkin807"), and every MRN starts SYN.
//
// Clinically coherent, on Synthea's model but with this project's own code lists (Synthea codes conditions in SNOMED
// CT, which isn't used here): a patient's conditions, by age, decide the rest of the chart. Someone with type 2
// diabetes is on metformin, has hemoglobin A1c results above the range, and comes in for diabetes checks. Every code is
// a real one from the reference data (db/reference): ICD-10-CM for diagnoses, RxNorm for medications and drug
// allergens, FDA UNII for other allergens, LOINC for results. The people are not real.
public sealed class SyntheticClinic
{
    // The clinic's fixed "today", so the data doesn't change with the day it is made: visits run up to it.
    public static readonly DateTimeOffset Today = new(2026, 11, 1, 0, 0, 0, TimeSpan.Zero);

    static readonly DateTimeOffset Made = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    static readonly DateOnly EarliestBirth = new(1930, 1, 1);

    static readonly DateOnly LatestBirth = new(2025, 12, 31);

    public SyntheticClinic(int patients) =>
        Patients = patients > 0 ? patients : throw new ArgumentOutOfRangeException(nameof(patients), "A clinic has at least one patient.");

    public int Patients { get; }

    public IEnumerable<SyntheticChart> Charts() => Enumerable.Range(0, Patients).Select(Chart);

    // Patient number index (from 0), and everything on their chart.
    public static SyntheticChart Chart(int index)
    {
        var random = new SplitMix64(0x5EED_0000_0000_0000UL + (ulong)index);
        var made = Made.AddMilliseconds(index);
        var patientId = Uuid7(made, random);
        var patient = new Patient
        {
            Id = patientId,
            Mrn = Mrn(index),
            GivenName = Pick(GivenNames, random) + random.Next(100, 1000),
            FamilyName = Pick(FamilyNames, random) + random.Next(100, 1000),
            BirthDate = EarliestBirth.AddDays(random.Next(LatestBirth.DayNumber - EarliestBirth.DayNumber + 1)),
            Gender = random.NextDouble() switch
            {
                < 0.49 => AdministrativeGender.Female,
                < 0.98 => AdministrativeGender.Male,
                < 0.99 => AdministrativeGender.Other,
                _ => AdministrativeGender.Unknown,
            },
            Version = 1,
            UpdatedAt = made,
        };

        // What the patient has decides the rest of the chart: each condition brings the medications that treat it, the
        // tests that follow it and their likely values, what the blood pressure looks like, and why they come in.
        var age = Today.Year - patient.BirthDate.Year;
        var problems = Problems.Where(problem => age >= problem.FromAge && random.NextDouble() < problem.Share * (age >= 50 ? problem.Older : 1)).ToList();

        var allergies = new List<AllergyIntolerance>();
        var allergyRoll = random.NextDouble();
        if (allergyRoll is >= 0.25 and < 0.60)
        {
            allergies.Add(new AllergyIntolerance
            {
                Id = Uuid7(made, random),
                PatientId = patientId,
                Code = AllergyIntolerance.NoKnownAllergy,
                Display = "No known allergy",
                ClinicalStatus = AllergyClinicalStatus.Active,
                RecordedAt = RecentDay(random),
            });
        }
        else if (allergyRoll >= 0.60)
        {
            foreach (var substance in Distinct(Substances, random.Next(1, 4), random))
            {
                allergies.Add(new AllergyIntolerance
                {
                    Id = Uuid7(made, random),
                    PatientId = patientId,
                    Code = substance.Code,
                    CodeSystem = substance.System,
                    Display = substance.Display,
                    ClinicalStatus = random.NextDouble() < 0.9 ? AllergyClinicalStatus.Active : AllergyClinicalStatus.Resolved,
                    Category = substance.Category,
                    Criticality = substance.Criticality,
                    Reaction = substance.Reaction,
                    Severity = substance.Criticality == AllergyCriticality.High ? AllergySeverity.Severe : AllergySeverity.Mild,
                    RecordedAt = RecentDay(random),
                });
            }
        }

        var conditions = new List<Condition>();
        var medications = new List<MedicationStatement>();
        foreach (var problem in problems)
        {
            var onset = DateOnly.FromDateTime(RecentDay(random).UtcDateTime);
            var current = !problem.Passing && random.NextDouble() < 0.9;
            conditions.Add(new Condition
            {
                Id = Uuid7(made, random),
                PatientId = patientId,
                Code = problem.Code,
                Display = problem.Display,
                ClinicalStatus = current ? ConditionClinicalStatus.Active : ConditionClinicalStatus.Resolved,
                OnsetDate = onset,
            });

            // The treatment: one of the condition's usual medications, unless the patient is allergic to what is in it.
            if (problem.Treatments.Length > 0 && random.NextDouble() < 0.85)
            {
                var treatment = Pick(problem.Treatments, random);
                var allergic = allergies.Any(allergy => allergy.ClinicalStatus == AllergyClinicalStatus.Active && allergy.Code == treatment.Ingredient);
                if (!allergic && medications.All(medication => medication.Code != treatment.Code))
                {
                    medications.Add(new MedicationStatement
                    {
                        Id = Uuid7(made, random),
                        PatientId = patientId,
                        Code = treatment.Code,
                        Display = treatment.Display,
                        Dosage = treatment.Dosage,
                        Status = current ? MedicationStatementStatus.Active : MedicationStatementStatus.Stopped,
                        EffectiveStart = onset.AddDays(random.Next(0, 30)),
                        EffectiveEnd = current ? null : onset.AddDays(random.Next(30, 365)),
                        // A medication for a sensitive diagnosis says as much as the diagnosis does.
                        Sensitive = problem.Sensitive,
                    });
                }
            }
        }

        var has = problems.Select(problem => problem.Code).ToHashSet();
        var visitsWithVitals = Enumerable.Range(0, Math.Min(12, random.Next(1, 4) + (2 * problems.Count(problem => !problem.Passing))))
            .Select(_ =>
            {
                var start = Today.AddMinutes(-random.Next(1, 5 * 365 * 24 * 60));
                var vitals = random.NextDouble() < 0.9;
                // Higher with hypertension, even treated.
                var systolic = has.Contains("I10") ? random.Next(128, 166) : random.Next(102, 131);
                var visit = new Encounter
                {
                    Id = Uuid7(made, random),
                    PatientId = patientId,
                    Status = random.NextDouble() < 0.95 ? EncounterStatus.Finished : EncounterStatus.Cancelled,
                    ClassCode = random.NextDouble() switch
                    {
                        < 0.85 => EncounterClass.Ambulatory,
                        < 0.97 => EncounterClass.Virtual,
                        _ => EncounterClass.Emergency,
                    },
                    Reason = problems.Count > 0 && random.NextDouble() < 0.7 ? problems[random.Next(problems.Count)].Visit : Pick(VisitReasons, random),
                    PeriodStart = start,
                    PeriodEnd = start.AddMinutes(20),
                };
                // The visit's vital signs, drawn as they always were, so the rest of the chart comes out the same.
                // Drawn only when taken, in this order, as before.
                var diastolic = vitals ? systolic - random.Next(38, 56) : 0;
                var heartRate = vitals ? random.Next(55, 101) : 0;
                // A child's blood pressure is lower and heart rate faster (step 9b): what was drawn is shifted for the
                // age band on the day of the visit, with nothing more drawn, so the rest of the chart comes out the same.
                var (pressureDown, gapDown, rateUp) = AgeBand.Of(AgeBand.Months(patient.BirthDate, DateOnly.FromDateTime(start.UtcDateTime))) switch
                {
                    AgeBand.Infant => (30, 12, 55),
                    AgeBand.YoungChild => (18, 8, 35),
                    AgeBand.OlderChild => (10, 4, 20),
                    AgeBand.Adolescent => (4, 0, 5),
                    _ => (0, 0, 0),
                };
                (systolic, diastolic, heartRate) = (systolic - pressureDown, diastolic - pressureDown + gapDown, heartRate + rateUp);
                return (Visit: visit, Vitals: vitals ? new[] { (VitalSign.Systolic, systolic), (VitalSign.Diastolic, diastolic), (VitalSign.HeartRate, heartRate) } : []);
            })
            .ToList();
        // Vital signs are observations (step 9): one for each sign taken at a visit, released with the visit. Their
        // IDs come from a generator of their own, seeded by the visit, so no other part of the chart shifts.
        var vitalSigns = visitsWithVitals.SelectMany(visit =>
        {
            var ids = new SplitMix64(BitConverter.ToUInt64(visit.Visit.Id.ToByteArray(), 8));
            return visit.Vitals.Select(vital => new Observation
            {
                Id = Uuid7(visit.Visit.PeriodStart, ids),
                PatientId = patientId,
                EncounterId = visit.Visit.Id,
                Category = ObservationCategory.VitalSigns,
                Code = vital.Item1,
                Display = VitalSigns[vital.Item1].Display,
                Status = ObservationStatus.Final,
                Value = vital.Item2,
                Unit = VitalSigns[vital.Item1].Unit,
                // The range for the patient's age on the day (step 9b), as the database gives a recorded one.
                ReferenceLow = VitalRanges.For(vital.Item1, AgeBand.Months(patient.BirthDate, DateOnly.FromDateTime(visit.Visit.PeriodStart.UtcDateTime)))?.Low,
                ReferenceHigh = VitalRanges.For(vital.Item1, AgeBand.Months(patient.BirthDate, DateOnly.FromDateTime(visit.Visit.PeriodStart.UtcDateTime)))?.High,
                EffectiveAt = visit.Visit.PeriodStart,
                IssuedAt = visit.Visit.PeriodStart,
            });
        }).ToList();
        var visits = visitsWithVitals.Select(visit => visit.Visit).ToList();

        // Test results (UC6): the tests each condition is followed with, more than once over the years, and a routine
        // one or two. Most are final and released; about one in ten is still preliminary and not released, which the
        // portal must not show. A value sits where the condition puts it, such as a raised hemoglobin A1c with
        // diabetes; anything else is mostly inside the normal range and sometimes outside it.
        var followed = problems.SelectMany(problem => problem.Tests).Distinct().ToList();
        var tests = followed.SelectMany(code => Enumerable.Repeat(code, random.Next(1, 4)))
            .Concat(Distinct(LabTests, random.Next(0, 3), random).Select(test => test.Code))
            .Take(12).ToList();
        var results = tests
            .Select(code =>
            {
                var test = LabTests.First(candidate => candidate.Code == code);
                var taken = Today.AddMinutes(-random.Next(60, 2 * 365 * 24 * 60));
                var released = random.NextDouble() < 0.9;
                // A range with no lower limit, such as LDL's "below 100", draws from zero.
                var low = test.Low ?? 0m;
                var raised = problems.Any(problem => problem.Raises.Contains(code));
                var lowered = problems.Any(problem => problem.Lowers.Contains(code));
                var spread = test.High - low;
                var value = raised ? test.High + (spread * (decimal)(0.05 + (random.NextDouble() * 0.6)))
                    : lowered ? Math.Max(0m, low - (spread * (decimal)(0.05 + (random.NextDouble() * 0.5))))
                    : Math.Max(0m, low + (spread * (decimal)((random.NextDouble() * 1.2) - 0.1)));
                var outOfRange = value < low || value > test.High;
                return new Observation
                {
                    Id = Uuid7(made, random),
                    PatientId = patientId,
                    Code = test.Code,
                    Display = test.Display,
                    Status = released ? ObservationStatus.Final : ObservationStatus.Preliminary,
                    Value = Math.Round(value, test.Decimals),
                    Unit = test.Unit,
                    ReferenceLow = test.Low,
                    ReferenceHigh = test.High,
                    EffectiveAt = taken,
                    IssuedAt = released ? taken.AddHours(random.Next(4, 72)) : null,
                    Note = released && outOfRange && random.NextDouble() < 0.5 ? Pick(ResultNotes, random) : null,
                };
            })
            .ToList();

        return new SyntheticChart(patient, allergies, medications, conditions, visits, results, vitalSigns);
    }

    // The three vital signs a seeded visit records, as db/reference names them (ehr.observation_codes).
    // Their reference ranges go by age: Ehr.Data.Reference.VitalRanges.
    static readonly Dictionary<string, (string Display, string Unit)> VitalSigns = new()
    {
        [VitalSign.Systolic] = ("Systolic blood pressure", "mm[Hg]"),
        [VitalSign.Diastolic] = ("Diastolic blood pressure", "mm[Hg]"),
        [VitalSign.HeartRate] = ("Heart rate", "/min"),
    };

    public static string Mrn(int index) => $"SYN{index + 1:D7}";

    // A day in the five years before Today.
    static DateTimeOffset RecentDay(SplitMix64 random) => Today.AddDays(-random.Next(1, 5 * 365));

    static T Pick<T>(T[] items, SplitMix64 random) => items[random.Next(items.Length)];

    // Up to count different items, in a random order.
    static IEnumerable<T> Distinct<T>(T[] items, int count, SplitMix64 random)
    {
        var order = Enumerable.Range(0, items.Length).ToArray();
        for (var i = 0; i < Math.Min(count, order.Length); i++)
        {
            var j = random.Next(i, order.Length);
            (order[i], order[j]) = (order[j], order[i]);
            yield return items[order[i]];
        }
    }

    // A version 7 UUID for the time, its random bits from the patient's own sequence.
    static Guid Uuid7(DateTimeOffset time, SplitMix64 random)
    {
        Span<byte> bytes = stackalloc byte[16];
        var milliseconds = (ulong)time.ToUnixTimeMilliseconds();
        for (var i = 0; i < 6; i++)
        {
            bytes[i] = (byte)(milliseconds >> (8 * (5 - i)));
        }

        BinaryPrimitives.WriteUInt16LittleEndian(bytes[6..8], (ushort)random.NextUInt64());
        BinaryPrimitives.WriteUInt64LittleEndian(bytes[8..], random.NextUInt64());
        bytes[6] = (byte)(0x70 | (bytes[6] & 0x0F));
        bytes[8] = (byte)(0x80 | (bytes[8] & 0x3F));
        return new Guid(bytes, bigEndian: true);
    }

    // The random numbers behind one patient. SplitMix64, written out here, so the clinic stays the same across .NET
    // versions, which promise no particular sequence from a seeded System.Random.
    sealed class SplitMix64(ulong seed)
    {
        ulong state = seed;

        public ulong NextUInt64()
        {
            var z = state += 0x9E3779B97F4A7C15UL;
            z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
            z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
            return z ^ (z >> 31);
        }

        public int Next(int maxExclusive) => (int)(NextUInt64() % (ulong)maxExclusive);

        public int Next(int minInclusive, int maxExclusive) => minInclusive + Next(maxExclusive - minInclusive);

        public double NextDouble() => (NextUInt64() >> 11) * (1.0 / (1UL << 53));
    }

    static readonly string[] GivenNames =
    [
        "Ada", "Alder", "Arlo", "Aspen", "Beatrix", "Bodhi", "Bramble", "Briar", "Calla", "Cedar", "Clover", "Cora",
        "Dahlia", "Dax", "Delphine", "Ellis", "Elm", "Ember", "Ezra", "Fern", "Finch", "Flint", "Greer", "Hazel",
        "Heath", "Indigo", "Iris", "Jasper", "Juniper", "Kestrel", "Larch", "Laurel", "Linden", "Lowen", "Maple",
        "Marlow", "Moss", "Nova", "Oakley", "Odette", "Orrin", "Pax", "Pearl", "Quill", "Rowan", "Rue", "Sage", "Sorrel",
        "Story", "Tamsin", "Thatcher", "Tove", "Umber", "Vale", "Wren", "Wynn", "Xan", "Yarrow", "Zephyr", "Zinnia",
    ];

    static readonly string[] FamilyNames =
    [
        "Abernathy", "Ashbury", "Ashgrove", "Barrowe", "Bellweather", "Birchall", "Blackwood", "Brambleton", "Calloway",
        "Cindergate", "Clearwater", "Copperfield", "Crane", "Dewhurst", "Driftwood", "Duskwood", "Elderberry", "Emberly",
        "Everhart", "Fairweather", "Fenwick", "Foxglove", "Frostwick", "Galloway", "Glenmoor", "Greywater", "Hallowell",
        "Harrowgate", "Hawthorne", "Heathcote", "Holloway", "Inglewood", "Ironwood", "Ivesdale", "Jessamine", "Juniperra",
        "Kettleby", "Kilbride", "Kingsmere", "Larkin", "Lindqvist", "Lockhart", "Lowmoor", "Mapleton", "Marchbank",
        "Meadowcroft", "Mossley", "Netherby", "Nettlefold", "Northcott", "Oakhurst", "Oldcastle", "Orchardson",
        "Pemberton", "Pennywhistle", "Pinecrest", "Quarrington", "Quillfeather", "Rainsford", "Ravenscroft", "Redfern",
        "Rookwood", "Saltmarsh", "Sedgewick", "Silverthorn", "Stonebridge", "Thistlewood", "Thornbury", "Underhill",
        "Upwood", "Valemont", "Vantreight", "Wakefield", "Whitlock", "Willowmere", "Winterbourne", "Wrexley",
        "Yarborough", "Yewdale", "Zellweger",
    ];

    // What patients are allergic to, coded as the reference data codes it (db/reference): a drug by its RxNorm
    // ingredient, anything else by its FDA substance (UNII). The names are the reference data's own; a test checks that.
    internal static readonly (string System, string Code, string Display, string Category, string Criticality, string Reaction)[] Substances =
    [
        ("rxnorm", "7980", "Penicillin G", AllergyCategory.Medication, AllergyCriticality.High, "Hives"),
        ("rxnorm", "723", "Amoxicillin", AllergyCategory.Medication, AllergyCriticality.High, "Rash"),
        ("rxnorm", "10180", "Sulfamethoxazole", AllergyCategory.Medication, AllergyCriticality.Low, "Rash"),
        ("rxnorm", "1191", "Aspirin", AllergyCategory.Medication, AllergyCriticality.Low, "Wheezing"),
        ("rxnorm", "2670", "Codeine", AllergyCategory.Medication, AllergyCriticality.Low, "Nausea"),
        ("rxnorm", "5640", "Ibuprofen", AllergyCategory.Medication, AllergyCriticality.Low, "Hives"),
        ("unii", "QE1QX6B99R", "Peanut", AllergyCategory.Food, AllergyCriticality.High, "Anaphylaxis"),
        ("unii", "6T362S6JF6", "American lobster", AllergyCategory.Food, AllergyCriticality.High, "Swelling of the lips"),
        ("unii", "291P45F896", "Egg", AllergyCategory.Food, AllergyCriticality.Low, "Hives"),
        ("unii", "917J3173FT", "Cow milk", AllergyCategory.Food, AllergyCriticality.Low, "Stomach cramps"),
        ("unii", "2LQ0UUW8IN", "Natural latex rubber", AllergyCategory.Environment, AllergyCriticality.Low, "Contact dermatitis"),
        ("unii", "65M88RW2EG", "Phleum pratense pollen", AllergyCategory.Environment, AllergyCriticality.Low, "Sneezing"),
        ("unii", "76013O881M", "Apis mellifera venom", AllergyCategory.Environment, AllergyCriticality.High, "Anaphylaxis"),
    ];

    // A medication that treats a condition: its RxNorm clinical drug, the reference data's name for it, the RxNorm
    // ingredient it is made of (so a patient allergic to that isn't given it), and how it is taken.
    internal sealed record Treatment(string Code, string Display, string Ingredient, string Dosage);

    // A condition and what comes with it: its ICD-10-CM code and the reference data's name; how common it is, from what
    // age, and how much more so from 50; the medications that treat it; the tests it is followed with (LOINC), which of
    // them it raises or lowers; why its patients come in; whether it passes by itself, like a cold; and whether it is
    // sensitive, which the database decides again from the code (a medication for it is marked sensitive here).
    internal sealed record Problem(
        string Code, string Display, double Share, int FromAge, double Older, Treatment[] Treatments, string[] Tests, string[] Raises, string[] Lowers,
        string Visit, bool Passing = false, bool Sensitive = false);

    static readonly Treatment Sertraline = new("312941", "sertraline 50 MG Oral Tablet", "36437", "1 tablet by mouth daily");

    static readonly Treatment Escitalopram = new("349332", "escitalopram 10 MG Oral Tablet", "321988", "1 tablet by mouth daily");

    internal static readonly Problem[] Problems =
    [
        new("I10", "Essential (primary) hypertension", 0.14, 30, 3.0,
            [
                new("314076", "lisinopril 10 MG Oral Tablet", "29046", "1 tablet by mouth daily"),
                new("197361", "amlodipine 5 MG Oral Tablet", "17767", "1 tablet by mouth daily"),
                new("979492", "losartan potassium 50 MG Oral Tablet", "52175", "1 tablet by mouth daily"),
                new("310798", "hydrochlorothiazide 25 MG Oral Tablet", "5487", "1 tablet by mouth daily"),
            ],
            ["2160-0", "2823-3", "2951-2"], [], [], "Blood pressure check"),
        new("E11.9", "Type 2 diabetes mellitus without complications", 0.07, 30, 2.5,
            [
                new("861007", "metformin hydrochloride 500 MG Oral Tablet", "6809", "1 tablet by mouth twice daily with meals"),
                new("861004", "metformin hydrochloride 1000 MG Oral Tablet", "6809", "1 tablet by mouth twice daily with meals"),
            ],
            ["4548-4", "2345-7", "2160-0"], ["4548-4", "2345-7"], [], "Diabetes check"),
        new("E78.5", "Hyperlipidemia, unspecified", 0.12, 30, 2.5,
            [new("617310", "atorvastatin 20 MG Oral Tablet", "83367", "1 tablet by mouth at bedtime")],
            ["2093-3", "13457-7", "2085-9", "2571-8"], ["2093-3", "13457-7"], [], "Cholesterol follow-up"),
        new("J45.909", "Unspecified asthma, uncomplicated", 0.08, 5, 1.0,
            [
                new("630208", "albuterol 0.83 MG/ML Inhalation Solution", "435", "1 vial by nebulizer every 4 hours as needed"),
                new("200224", "montelukast 10 MG Oral Tablet", "88249", "1 tablet by mouth at bedtime"),
            ],
            [], [], [], "Asthma follow-up"),
        new("F41.1", "Generalized anxiety disorder", 0.07, 14, 1.0, [Sertraline, Escitalopram], [], [], [], "Anxiety follow-up", Sensitive: true),
        new("F32.A", "Depression, unspecified", 0.08, 14, 1.0, [Sertraline, Escitalopram], [], [], [], "Mood follow-up", Sensitive: true),
        new("M54.50", "Low back pain, unspecified", 0.10, 18, 1.5,
            [new("197805", "ibuprofen 400 MG Oral Tablet", "5640", "1 tablet by mouth every 6 hours as needed")], [], [], [], "Back pain"),
        new("K21.9", "Gastro-esophageal reflux disease without esophagitis", 0.09, 18, 1.5,
            [new("198051", "omeprazole 20 MG Delayed Release Oral Capsule", "7646", "1 capsule by mouth before breakfast")], [], [], [], "Heartburn"),
        new("E03.9", "Hypothyroidism, unspecified", 0.05, 18, 1.5,
            [new("966221", "levothyroxine sodium 0.05 MG Oral Tablet", "10582", "1 tablet by mouth every morning")],
            ["3016-3"], ["3016-3"], [], "Thyroid check"),
        new("J30.9", "Allergic rhinitis, unspecified", 0.10, 5, 1.0,
            [new("1797907", "fluticasone propionate 0.05 MG/ACTUAT Metered Dose Nasal Spray", "41126", "1 spray in each nostril daily")],
            [], [], [], "Allergy symptoms"),
        new("E66.9", "Obesity, unspecified", 0.10, 12, 1.2, [], ["2345-7", "2093-3"], [], [], "Weight check"),
        new("E55.9", "Vitamin D deficiency, unspecified", 0.06, 12, 1.3,
            [new("199362", "cholecalciferol 0.025 MG Oral Tablet", "2418", "1 tablet by mouth daily")], ["1989-3"], [], ["1989-3"], "Lab results"),
        new("F11.20", "Opioid dependence, uncomplicated", 0.01, 18, 1.0,
            [new("351267", "buprenorphine 8 MG / naloxone 2 MG Sublingual Tablet", "1819", "1 tablet under the tongue daily")],
            [], [], [], "Medication review", Sensitive: true),
        new("J06.9", "Acute upper respiratory infection, unspecified", 0.15, 0, 0.8, [], [], [], [], "Cough", Passing: true),
    ];

    // Common primary-care lab tests: LOINC code, name, unit, the normal range and how many decimals results have.
    static readonly (string Code, string Display, string Unit, decimal? Low, decimal High, int Decimals)[] LabTests =
    [
        ("4548-4", "Hemoglobin A1c", "%", 4.0m, 5.6m, 1),
        ("2345-7", "Glucose", "mg/dL", 70m, 99m, 0),
        ("2093-3", "Cholesterol, total", "mg/dL", 125m, 199m, 0),
        ("13457-7", "LDL cholesterol", "mg/dL", null, 99m, 0),
        ("2085-9", "HDL cholesterol", "mg/dL", 40m, 90m, 0),
        ("2571-8", "Triglycerides", "mg/dL", null, 149m, 0),
        ("718-7", "Hemoglobin", "g/dL", 12.0m, 17.5m, 1),
        ("6690-2", "White blood cell count", "10*3/uL", 4.5m, 11.0m, 1),
        ("2160-0", "Creatinine", "mg/dL", 0.6m, 1.3m, 2),
        ("2823-3", "Potassium", "mmol/L", 3.5m, 5.1m, 1),
        ("2951-2", "Sodium", "mmol/L", 135m, 145m, 0),
        ("3016-3", "Thyroid stimulating hormone", "mIU/L", 0.4m, 4.0m, 2),
        ("1989-3", "Vitamin D, 25-hydroxy", "ng/mL", 30m, 100m, 0),
    ];

    // What a clinician writes to the patient on a result out of its range.
    static readonly string[] ResultNotes =
    [
        "We'll talk about this at your next visit.",
        "Please book a follow-up appointment in the next month.",
        "This is a little outside the range and not a concern. No action needed.",
        "Let's repeat this test in 3 months.",
    ];

    static readonly string[] VisitReasons =
    [
        "Annual physical", "Follow-up", "Blood pressure check", "Medication review", "Cough", "Back pain", "Headache",
        "Vaccination", "Diabetes check", "Rash", "Sore throat", "Lab results",
    ];
}

// One synthetic patient and everything on their chart.
public sealed record SyntheticChart(
    Patient Patient,
    IReadOnlyList<AllergyIntolerance> Allergies,
    IReadOnlyList<MedicationStatement> Medications,
    IReadOnlyList<Condition> Conditions,
    IReadOnlyList<Encounter> Visits,
    IReadOnlyList<Observation> Results,
    // The vital signs taken at the visits: observations too, kept apart from the laboratory results.
    IReadOnlyList<Observation> Vitals);
