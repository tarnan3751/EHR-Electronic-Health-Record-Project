using System.Buffers.Binary;
using Ehr.Data.Patients;

namespace Ehr.Seed;

// A synthetic clinic, the same every time (NFR-13: synthetic data only). Patient number i is always the same patient,
// with the same allergies, medications, diagnoses and visits, whatever the clinic's size, so the benchmark knows whom
// to search for and whose chart to open without reading anyone. Obviously synthetic: every name carries a number, as
// the Synthea generator's do ("Larkin807"), and every MRN starts SYN. The codes are real ICD-10-CM and SNOMED CT codes,
// the people are not.
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
            foreach (var (display, category, criticality, reaction) in Distinct(Substances, random.Next(1, 4), random))
            {
                allergies.Add(new AllergyIntolerance
                {
                    Id = Uuid7(made, random),
                    PatientId = patientId,
                    Display = display,
                    ClinicalStatus = random.NextDouble() < 0.9 ? AllergyClinicalStatus.Active : AllergyClinicalStatus.Resolved,
                    Category = category,
                    Criticality = criticality,
                    Reaction = reaction,
                    RecordedAt = RecentDay(random),
                });
            }
        }

        var medications = Distinct(Medications, random.Next(0, 8), random)
            .Select(medication =>
            {
                var start = DateOnly.FromDateTime(RecentDay(random).UtcDateTime);
                var active = random.NextDouble() < 0.75;
                return new MedicationStatement
                {
                    Id = Uuid7(made, random),
                    PatientId = patientId,
                    Display = medication.Display,
                    Dosage = medication.Dosage,
                    Status = active ? MedicationStatementStatus.Active : MedicationStatementStatus.Stopped,
                    EffectiveStart = start,
                    EffectiveEnd = active ? null : start.AddDays(random.Next(14, 365)),
                };
            })
            .ToList();

        var conditions = Distinct(Diagnoses, random.Next(0, 6), random)
            .Select(diagnosis => new Condition
            {
                Id = Uuid7(made, random),
                PatientId = patientId,
                Code = diagnosis.Code,
                Display = diagnosis.Display,
                ClinicalStatus = random.NextDouble() < 0.8 ? ConditionClinicalStatus.Active : ConditionClinicalStatus.Resolved,
                OnsetDate = DateOnly.FromDateTime(RecentDay(random).UtcDateTime),
            })
            .ToList();

        var visits = Enumerable.Range(0, random.Next(1, 13))
            .Select(_ =>
            {
                var start = Today.AddMinutes(-random.Next(1, 5 * 365 * 24 * 60));
                var vitals = random.NextDouble() < 0.9;
                return new Encounter
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
                    Reason = Pick(VisitReasons, random),
                    PeriodStart = start,
                    PeriodEnd = start.AddMinutes(20),
                    SystolicMmHg = vitals ? (short)random.Next(100, 161) : null,
                    DiastolicMmHg = vitals ? (short)random.Next(60, 101) : null,
                    HeartRatePerMinute = vitals ? (short)random.Next(55, 101) : null,
                };
            })
            .ToList();

        return new SyntheticChart(patient, allergies, medications, conditions, visits);
    }

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

    static readonly (string Display, string Category, string Criticality, string Reaction)[] Substances =
    [
        ("Penicillin", AllergyCategory.Medication, AllergyCriticality.High, "Hives"),
        ("Amoxicillin", AllergyCategory.Medication, AllergyCriticality.High, "Rash"),
        ("Sulfonamides", AllergyCategory.Medication, AllergyCriticality.Low, "Rash"),
        ("Aspirin", AllergyCategory.Medication, AllergyCriticality.Low, "Wheezing"),
        ("Codeine", AllergyCategory.Medication, AllergyCriticality.Low, "Nausea"),
        ("Peanut", AllergyCategory.Food, AllergyCriticality.High, "Anaphylaxis"),
        ("Shellfish", AllergyCategory.Food, AllergyCriticality.High, "Swelling of the lips"),
        ("Egg", AllergyCategory.Food, AllergyCriticality.Low, "Hives"),
        ("Milk", AllergyCategory.Food, AllergyCriticality.Low, "Stomach cramps"),
        ("Latex", AllergyCategory.Environment, AllergyCriticality.Low, "Contact dermatitis"),
        ("Grass pollen", AllergyCategory.Environment, AllergyCriticality.Low, "Sneezing"),
        ("Bee venom", AllergyCategory.Environment, AllergyCriticality.High, "Anaphylaxis"),
    ];

    static readonly (string Display, string Dosage)[] Medications =
    [
        ("Lisinopril 10 mg tablet", "1 tablet by mouth daily"),
        ("Metformin 500 mg tablet", "1 tablet by mouth twice daily with meals"),
        ("Atorvastatin 20 mg tablet", "1 tablet by mouth at bedtime"),
        ("Levothyroxine 50 mcg tablet", "1 tablet by mouth every morning"),
        ("Amlodipine 5 mg tablet", "1 tablet by mouth daily"),
        ("Omeprazole 20 mg capsule", "1 capsule by mouth before breakfast"),
        ("Albuterol 90 mcg inhaler", "2 puffs every 4 hours as needed"),
        ("Sertraline 50 mg tablet", "1 tablet by mouth daily"),
        ("Losartan 50 mg tablet", "1 tablet by mouth daily"),
        ("Gabapentin 300 mg capsule", "1 capsule by mouth three times daily"),
        ("Hydrochlorothiazide 25 mg tablet", "1 tablet by mouth daily"),
        ("Fluticasone 50 mcg nasal spray", "1 spray in each nostril daily"),
        ("Ibuprofen 400 mg tablet", "1 tablet by mouth every 6 hours as needed"),
        ("Montelukast 10 mg tablet", "1 tablet by mouth at bedtime"),
        ("Vitamin D3 1000 unit tablet", "1 tablet by mouth daily"),
    ];

    static readonly (string Code, string Display)[] Diagnoses =
    [
        ("I10", "Essential (primary) hypertension"),
        ("E11.9", "Type 2 diabetes mellitus without complications"),
        ("E78.5", "Hyperlipidemia, unspecified"),
        ("J45.909", "Unspecified asthma, uncomplicated"),
        ("F41.1", "Generalized anxiety disorder"),
        ("M54.50", "Low back pain, unspecified"),
        ("K21.9", "Gastro-esophageal reflux disease without esophagitis"),
        ("E03.9", "Hypothyroidism, unspecified"),
        ("J30.9", "Allergic rhinitis, unspecified"),
        ("F32.A", "Depression, unspecified"),
        ("E66.9", "Obesity, unspecified"),
        ("J06.9", "Acute upper respiratory infection, unspecified"),
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
    IReadOnlyList<Encounter> Visits);
