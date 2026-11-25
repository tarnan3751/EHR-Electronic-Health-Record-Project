// Writes the reference codes clinicians pick chart entries from, db/reference/10-clinical-codes.sql, from pinned
// source files. Nothing is fetched when the apps run: the SQL this writes is committed, and the migration job, the
// tests and the benchmark apply it (docs/expansion/reference-data.md has each source's license and attribution).
//
//   dotnet run tools/reference/import.cs -- --sources <folder>
//
// <folder> holds the three downloads below, under these names, each checked against its SHA-256 before it is read.
// Keep the folder outside the repository. To move to a newer release, change the address, the name inside the
// archive and the checksum here, download the file, and run this again.
//
//   rxnorm.zip  RxNorm Current Prescribable Content, 2026-11-02 (NLM; no license needed)
//               https://download.nlm.nih.gov/rxnorm/RxNorm_full_prescribe_11022026.zip
//   icd.zip     ICD-10-CM code descriptions, fiscal year 2027 (CDC/NCHS)
//               https://ftp.cdc.gov/pub/Health_Statistics/NCHS/Publications/ICD10CM/2027/icd10cm-code-descriptions-2027.zip
//   unii.zip    FDA Unique Ingredient Identifiers, 2026-08-04
//               https://precision.fda.gov/uniisearch/archive/2026-08-04/UNII_Data_20260804.zip
//
// It takes every ICD-10-CM code valid on a claim; every RxNorm clinical and branded drug that can be prescribed, for
// medications, and every ingredient, for drug allergens; and a curated list of FDA substances for the allergens that
// aren't drugs. A name longer than a chart entry can hold (300 characters: a few products of many ingredients) is
// left out, and the script says how many.

// Run, never published: without this, file-based apps default to native AOT and restore its compiler.
#:property PublishAot=false

using System.Globalization;
using System.IO.Compression;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;

var sourcesAt = Array.IndexOf(args, "--sources");
if (sourcesAt < 0 || sourcesAt + 1 >= args.Length)
{
    Console.Error.WriteLine("Usage: dotnet run tools/reference/import.cs -- --sources <folder with rxnorm.zip, icd.zip and unii.zip>");
    return 2;
}

var sources = args[sourcesAt + 1];
var root = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(ScriptPath())!, "..", ".."));
var rows = new List<Row>();

// Diagnoses: every code a claim can carry (the order file's flag 1).
const int MaximumName = 300;
var tooLong = 0;
using (var icd = Open("icd.zip", "93e3ad6004badf470c55bfe679b748ae88fd9b2b421851e409eec382c7713b9a", "icd10cm-order-2027.txt"))
{
    // Fixed width: order number (1-5), code (7-13), 1 when the code is valid on a claim (15), short description
    // (17-76), long description (78 on).
    while (icd.ReadLine() is { } line)
    {
        var code = line[6..13].Trim();
        if (line[14] == '1')
        {
            rows.Add(new("condition", "icd-10-cm", code.Length > 3 ? $"{code[..3]}.{code[3..]}" : code, line[77..].Trim(), null));
        }
    }
}

// Medications: RxNorm's clinical drugs (SCD) and branded drugs (SBD). Allergens: its ingredients (IN).
using (var rxnorm = Open("rxnorm.zip", "c742a6c4b2995194aa3479400e08a7c1ad707178bfc80fe9c8cd7afa693ae987", "rrf/RXNCONSO.RRF"))
{
    // Pipe-delimited: RXCUI (1), source (12), term type (13), name (15), suppress flag (17).
    while (rxnorm.ReadLine() is { } line)
    {
        var fields = line.Split('|');
        if (fields[11] != "RXNORM" || fields[16] != "N")
        {
            continue;
        }

        var name = fields[14];
        if (fields[12] is "SCD" or "SBD" or "IN" && name.Length > MaximumName)
        {
            tooLong++;
        }
        else if (fields[12] is "SCD" or "SBD")
        {
            rows.Add(new("medication", "rxnorm", fields[0], name, null));
        }
        else if (fields[12] == "IN")
        {
            rows.Add(new("allergen", "rxnorm", fields[0], Sentence(name), "medication"));
        }
    }
}

// Allergens that aren't drugs: FDA substances, by their preferred names, each with the FHIR category it is recorded under.
var substances = new Dictionary<string, string>(StringComparer.Ordinal)
{
    ["ALMOND"] = "food", ["AMERICAN LOBSTER"] = "food", ["ATLANTIC COD"] = "food", ["CASHEW"] = "food", ["COW MILK"] = "food",
    ["EGG"] = "food", ["ENGLISH WALNUT"] = "food", ["PEANUT"] = "food", ["PECAN"] = "food", ["PISTACHIO"] = "food",
    ["SESAME SEED"] = "food", ["SOYBEAN"] = "food", ["STRAWBERRY"] = "food", ["TOMATO"] = "food", ["WHEAT"] = "food",
    ["APIS MELLIFERA VENOM"] = "environment", ["DERMATOPHAGOIDES PTERONYSSINUS"] = "environment", ["FELIS CATUS DANDER"] = "environment",
    ["NATURAL LATEX RUBBER"] = "environment", ["NICKEL"] = "environment", ["PHLEUM PRATENSE POLLEN"] = "environment",
};
using (var unii = Open("unii.zip", "e768525350d17e0eedfa179e62563226e9da0a3d5bda006d27b7b94600a76ee9", "UNII_Records_4Aug2026.txt"))
{
    // Tab-delimited: UNII (1), display name (2).
    while (unii.ReadLine() is { } line)
    {
        var fields = line.Split('\t');
        if (fields.Length > 1 && substances.TryGetValue(fields[1], out var category))
        {
            rows.Add(new("allergen", "unii", fields[0], Sentence(fields[1]), category));
        }
    }
}

var missing = substances.Keys.Where(name => !rows.Any(row => row.System == "unii" && row.Display == Sentence(name))).ToList();
if (missing.Count > 0)
{
    Console.Error.WriteLine($"Not in the UNII file: {string.Join(", ", missing)}");
    return 1;
}

rows = [.. rows.DistinctBy(row => (row.Kind, row.System, row.Code)).OrderBy(row => row.Kind, StringComparer.Ordinal)
    .ThenBy(row => row.System, StringComparer.Ordinal).ThenBy(row => row.Code, StringComparer.Ordinal)];

var sql = new StringBuilder();
sql.Append("-- Generated by tools/reference/import.cs from pinned source files: change that script, not this file.\n");
sql.Append("-- The codes a chart entry can be picked from. Applied as ehr_owner after db/policies; safe to run again: it\n");
sql.Append("-- leaves ehr.clinical_codes holding exactly these rows. A diagnosis is sensitive by its code's range\n");
sql.Append("-- (ehr.icd10cm_is_sensitive, db/policies/65-chart-lists.sql). Sources, versions, licenses and attribution:\n");
sql.Append("-- docs/expansion/reference-data.md.\n");
sql.Append("-- The rows are a COPY block, as pg_dump writes one: psql loads it as it stands, and the tests' database through\n");
sql.Append("-- tests/Ehr.Testing/ReferenceData.cs. Tab-separated; \\N is a null.\n");
sql.Append("BEGIN;\n");
sql.Append("CREATE TEMPORARY TABLE incoming_codes (kind text, system text, code text, display text, category text) ON COMMIT DROP;\n");
sql.Append("COPY incoming_codes (kind, system, code, display, category) FROM stdin;\n");
foreach (var row in rows)
{
    sql.Append(row.Kind).Append('\t').Append(row.System).Append('\t').Append(row.Code).Append('\t').Append(Field(row.Display)).Append('\t')
        .Append(row.Category ?? "\\N").Append('\n');
}

sql.Append("\\.\n");
sql.Append("""
    INSERT INTO ehr.clinical_codes (kind, system, code, display, category, sensitive)
    SELECT kind, system, code, display, category, kind = 'condition' AND ehr.icd10cm_is_sensitive(code) FROM incoming_codes
    ON CONFLICT (kind, system, code) DO UPDATE
        SET display = EXCLUDED.display, category = EXCLUDED.category, sensitive = EXCLUDED.sensitive
        WHERE (clinical_codes.display, clinical_codes.category, clinical_codes.sensitive)
            IS DISTINCT FROM (EXCLUDED.display, EXCLUDED.category, EXCLUDED.sensitive);
    DELETE FROM ehr.clinical_codes c
    WHERE NOT EXISTS (SELECT 1 FROM incoming_codes i WHERE i.kind = c.kind AND i.system = c.system AND i.code = c.code);
    COMMIT;

    """.ReplaceLineEndings("\n"));

var output = Path.Combine(root, "db", "reference", "10-clinical-codes.sql");
Directory.CreateDirectory(Path.GetDirectoryName(output)!);
File.WriteAllText(output, sql.ToString(), new UTF8Encoding(false));
foreach (var group in rows.GroupBy(row => (row.Kind, row.System)))
{
    Console.WriteLine($"{group.Key.Kind,-11} {group.Key.System,-10} {group.Count(),6:N0}");
}

Console.WriteLine($"Left out, with a name over {MaximumName} characters: {tooLong:N0}");
Console.WriteLine($"Wrote {Path.GetRelativePath(root, output).Replace('\\', '/')}");
return 0;

// One file of a source archive, as text, once the archive's checksum has been checked.
StreamReader Open(string archive, string sha256, string entry)
{
    var path = Path.Combine(sources, archive);
    using (var file = File.OpenRead(path))
    {
        var actual = Convert.ToHexStringLower(SHA256.HashData(file));
        if (actual != sha256)
        {
            throw new InvalidOperationException($"{archive} is not the pinned release: its SHA-256 is {actual}, expected {sha256}.");
        }
    }

    var zip = ZipFile.OpenRead(path);
    var found = zip.Entries.Single(e => e.FullName == entry || e.FullName.EndsWith("/" + entry, StringComparison.Ordinal));
    return new StreamReader(found.Open(), Encoding.UTF8);
}

// "NATURAL LATEX RUBBER" as "Natural latex rubber"; a name already in mixed case keeps its capitals after the first.
static string Sentence(string name)
{
    var lower = name == name.ToUpperInvariant() ? name.ToLowerInvariant() : name;
    return char.ToUpper(lower[0], CultureInfo.InvariantCulture) + lower[1..];
}

// A value as COPY's text format takes it.
static string Field(string value) => value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\t", "\\t", StringComparison.Ordinal)
    .Replace("\n", "\\n", StringComparison.Ordinal).Replace("\r", "\\r", StringComparison.Ordinal);

static string ScriptPath([CallerFilePath] string path = "") => path;

record Row(string Kind, string System, string Code, string Display, string? Category);
