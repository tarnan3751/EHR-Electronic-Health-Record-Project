// Writes the rest of the reference data from pinned source files (import.cs writes the clinical codes):
//
//   db/reference/20-order-catalog.sql          what can be ordered beyond the clinic's common items: LOINC's
//                                              Universal Lab Orders Value Set, and RxNorm's clinical drugs
//   db/reference/30-observations.sql           the results, vital signs and survey scores the clinic records (LOINC
//                                              codes, UCUM units, this clinic's reference ranges), and which
//                                              results a common lab order produces. The PHQ-9 and GAD-7 are
//                                              hand-written in db/reference/36-questionnaires.sql, worded as their
//                                              authors' forms are (the expansion plan's step 13e)
//   db/reference/40-drug-classes.sql           each drug product's ingredients (RxNorm), each ingredient's FDA
//                                              Established Pharmacologic Classes, and which classes cross-react
//   db/reference/ddinter/10-drug-interactions.sql   interactions between ingredients, from DDInter 2.0, which has
//                                              its own license (see that folder)
//
//   dotnet run tools/reference/import-catalogs.cs -- --sources <folder>
//
// Nothing is fetched when the apps run: the SQL this writes is committed, and the migration job, the tests and the
// benchmark apply it (docs/expansion/reference-data.md has each source's license and attribution). <folder> holds the
// downloads below, under these names, each checked against its SHA-256 before it is read. Keep the folder outside the
// repository. DailyMed and DDInter publish their files without a version in the address: the date is when this
// project downloaded them, and the checksum is what pins them.
//
//   rxnorm.zip  RxNorm Current Prescribable Content, 2026-11-02 (as import.cs)
//   unii.zip    FDA Unique Ingredient Identifiers, 2026-08-04 (as import.cs)
//   loinc.zip   LOINC 2.83 (Regenstrief Institute; a free loinc.org account is needed to download it)
//               https://loinc.org/downloads/ (Loinc_2.83.zip)
//   pharmacologic_class_indexing_spl_files.zip, pharmacologic_class_mappings.zip, rxnorm_mappings.zip
//               the FDA's pharmacologic class indexing files, and DailyMed's two mapping files, 2026-11-18
//               https://dailymed-data.nlm.nih.gov/public-release-files/<name>
//   ddinter/ddinter_downloads_code_{A,B,D,H,L,P,R,V}.csv   DDInter 2.0, 2026-11-18
//               https://ddinter2.scbdd.com/static/media/download/<name>

// Run, never published: without this, file-based apps default to native AOT and restore its compiler.
#:property PublishAot=false

using System.Globalization;
using System.IO.Compression;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

var sourcesAt = Array.IndexOf(args, "--sources");
if (sourcesAt < 0 || sourcesAt + 1 >= args.Length)
{
    Console.Error.WriteLine("Usage: dotnet run tools/reference/import-catalogs.cs -- --sources <folder>");
    return 2;
}

var sources = args[sourcesAt + 1];
var root = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(ScriptPath())!, "..", ".."));
const string RxNormSha = "c742a6c4b2995194aa3479400e08a7c1ad707178bfc80fe9c8cd7afa693ae987";
const string LoincSha = "077a0718e87d8309ffe3a673f75b836a8e783dc36a646413ef97c71c12eab27e";

// ---- RxNorm: names, and what each product is made of ----------------------------------------------------------

var rxName = new Dictionary<string, string>(StringComparer.Ordinal);
var rxType = new Dictionary<string, string>(StringComparer.Ordinal);
using (var conso = Open("rxnorm.zip", RxNormSha, "rrf/RXNCONSO.RRF"))
{
    // Pipe-delimited: RXCUI (1), source (12), term type (13), name (15), suppress flag (17).
    while (conso.ReadLine() is { } line)
    {
        var fields = line.Split('|');
        if (fields[11] == "RXNORM" && fields[16] == "N" && fields[12] is "IN" or "SCD" or "SBD" or "SCDC")
        {
            rxName[fields[0]] = fields[14];
            rxType[fields[0]] = fields[12];
        }
    }
}

// A row (RXCUI1, RELA, RXCUI2) reads "RXCUI2 RELA RXCUI1": a product "constitutes" each of its components (SCDC), and
// a component is "ingredient_of" its ingredient (IN).
var components = new Dictionary<string, List<string>>(StringComparer.Ordinal);
var componentIngredients = new Dictionary<string, List<string>>(StringComparer.Ordinal);
using (var relations = Open("rxnorm.zip", RxNormSha, "rrf/RXNREL.RRF"))
{
    while (relations.ReadLine() is { } line)
    {
        var fields = line.Split('|');
        var (from, to, relation) = (fields[0], fields[4], fields[7]);
        if (relation == "constitutes" && rxType.GetValueOrDefault(from) is "SCD" or "SBD" && rxType.GetValueOrDefault(to) == "SCDC")
        {
            Into(components, from).Add(to);
        }
        else if (relation == "ingredient_of" && rxType.GetValueOrDefault(from) == "SCDC" && rxType.GetValueOrDefault(to) == "IN")
        {
            Into(componentIngredients, from).Add(to);
        }
    }
}

SortedSet<string> IngredientsOf(string product) =>
    new(components.GetValueOrDefault(product, []).SelectMany(component => componentIngredients.GetValueOrDefault(component, [])), StringComparer.Ordinal);

const int MaximumName = 300;
var products = rxType.Where(pair => pair.Value is "SCD" or "SBD" && rxName[pair.Key].Length <= MaximumName).Select(pair => pair.Key)
    .Order(StringComparer.Ordinal).ToList();
var ingredientRows = products.SelectMany(product => IngredientsOf(product).Select(ingredient => new[] { product, ingredient })).ToList();

// ---- Drug classes: the FDA's Established Pharmacologic Classes ---------------------------------------------------

// Each indexing file names a substance, by UNII, and its classes. A set of files (setId) has versions: the latest.
var classFiles = new Dictionary<string, (int Version, string Unii, string[] Classes)>(StringComparer.Ordinal);
using (var outer = OpenZip("pharmacologic_class_indexing_spl_files.zip", "ec6cf4bfaf74990f3b92265dc5604804271bb45c49312a9021f1ccbcb4033429"))
{
    foreach (var entry in outer.Entries.Where(e => e.Name.EndsWith(".zip", StringComparison.Ordinal)))
    {
        using var buffer = new MemoryStream();
        using (var stream = entry.Open())
        {
            stream.CopyTo(buffer);
        }

        buffer.Position = 0;
        using var inner = new ZipArchive(buffer);
        using var reader = new StreamReader(inner.Entries.Single(e => e.Name.EndsWith(".xml", StringComparison.Ordinal)).Open(), Encoding.UTF8);
        var xml = reader.ReadToEnd();
        var setId = Regex.Match(xml, "<setId root=\"([^\"]+)\"").Groups[1].Value;
        var version = int.Parse(Regex.Match(xml, "<versionNumber value=\"(\\d+)\"").Groups[1].Value, CultureInfo.InvariantCulture);
        var unii = Regex.Match(xml, "<code code=\"([A-Z0-9]{10})\" codeSystem=\"2\\.16\\.840\\.1\\.113883\\.4\\.9\"").Groups[1].Value;
        // The classes by name. The files' own codes for them are MED-RT's, which this project doesn't use.
        var classes = Regex.Matches(xml, "displayName=\"([^\"]+) \\[EPC\\]\"").Select(match => System.Net.WebUtility.HtmlDecode(match.Groups[1].Value))
            .Distinct(StringComparer.Ordinal).ToArray();
        if (setId.Length > 0 && (!classFiles.TryGetValue(setId, out var have) || have.Version < version))
        {
            classFiles[setId] = (version, unii, classes);
        }
    }
}

// Route A: a drug label's class files (DailyMed's pharmacologic class mappings) and its RxNorm products (DailyMed's
// RxNorm mappings). A label whose products have exactly one ingredient between them says what that ingredient's
// classes are; a class is kept when at least four in five of the ingredient's labels carry it, which drops the odd
// label mapped to the wrong substance.
var labelProducts = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
using (var mappings = Open("rxnorm_mappings.zip", "b34115f68e751f88d090d08d71a524db77cc4e9e8cab4c47e24c70e5da0dee5e", "rxnorm_mappings.txt"))
{
    // SETID|SPL_VERSION|RXCUI|RXSTRING|RXTTY
    while (mappings.ReadLine() is { } line)
    {
        var fields = line.Split('|');
        if (fields.Length >= 3 && rxType.GetValueOrDefault(fields[2]) is "SCD" or "SBD")
        {
            (labelProducts.TryGetValue(fields[0], out var set) ? set : labelProducts[fields[0]] = new(StringComparer.Ordinal)).Add(fields[2]);
        }
    }
}

var labels = new Dictionary<string, int>(StringComparer.Ordinal);
var votes = new Dictionary<(string Ingredient, string Class), int>();
using (var mappings = Open("pharmacologic_class_mappings.zip", "1308497de59ecf902bbd8309c23a9e3f18ccc0e033a6bc43dcacdcf752b1ba72", "pharmacologic_class_mappings.txt"))
{
    // SPL_SETID|SPL_VERSION|PHARMA_SETID|PHARMA_VERSION, one row for each class file of a label.
    var labelClasses = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
    while (mappings.ReadLine() is { } line)
    {
        var fields = line.Split('|');
        if (fields.Length >= 3 && classFiles.TryGetValue(fields[2], out var file))
        {
            (labelClasses.TryGetValue(fields[0], out var set) ? set : labelClasses[fields[0]] = new(StringComparer.Ordinal)).UnionWith(file.Classes);
        }
    }

    foreach (var (label, classes) in labelClasses)
    {
        if (!labelProducts.TryGetValue(label, out var labelled))
        {
            continue;
        }

        var ingredients = labelled.SelectMany(IngredientsOf).Distinct(StringComparer.Ordinal).ToList();
        if (ingredients.Count != 1)
        {
            continue;
        }

        labels[ingredients[0]] = labels.GetValueOrDefault(ingredients[0]) + 1;
        foreach (var name in classes)
        {
            votes[(ingredients[0], name)] = votes.GetValueOrDefault((ingredients[0], name)) + 1;
        }
    }
}

var classRows = new SortedSet<(string Ingredient, string Class)>(votes.Where(vote => vote.Value * 5 >= labels[vote.Key.Ingredient] * 4).Select(vote => vote.Key));
var byLabels = classRows.Select(row => row.Ingredient).Distinct().Count();

// Route B, for an ingredient no label of its own names, such as one sold only in combinations: the class file's
// substance, through the FDA substance list's RxNorm ingredient.
using (var unii = Open("unii.zip", "e768525350d17e0eedfa179e62563226e9da0a3d5bda006d27b7b94600a76ee9", "UNII_Records_4Aug2026.txt"))
{
    // Tab-delimited: UNII (1), ..., RXCUI (6).
    var substanceIngredient = new Dictionary<string, string>(StringComparer.Ordinal);
    while (unii.ReadLine() is { } line)
    {
        var fields = line.Split('\t');
        if (fields.Length > 5 && rxType.GetValueOrDefault(fields[5].Trim()) == "IN")
        {
            substanceIngredient[fields[0]] = fields[5].Trim();
        }
    }

    var classed = classRows.Select(row => row.Ingredient).ToHashSet(StringComparer.Ordinal);
    foreach (var file in classFiles.Values)
    {
        if (substanceIngredient.TryGetValue(file.Unii, out var ingredient) && !classed.Contains(ingredient))
        {
            classRows.UnionWith(file.Classes.Select(name => (ingredient, name)));
        }
    }
}

// Which classes cross-react in an allergic patient. Curated: a row needs a published source. A class named here is
// also taken to cross-react within itself; any other class shared with an allergy is only noted
// (db/policies/61-order-safety.sql).
const string PracticeParameter = "Khan DA, Banerji A, Blumenthal KG, et al. Drug allergy: a 2022 practice parameter update. J Allergy Clin Immunol. 2022;150(6):1333-1393.";
(string A, string B, string Note)[] crossReactions =
[
    ("Cephalosporin Antibacterial", "Penicillin-class Antibacterial",
        "Under 2% of patients with a confirmed penicillin allergy react to a cephalosporin; the risk is higher when the two drugs share a side chain."),
    ("Penem Antibacterial", "Penicillin-class Antibacterial",
        "Under 1% of patients with a confirmed penicillin allergy react to a carbapenem."),
];
var classNames = classRows.Select(row => row.Class).ToHashSet(StringComparer.Ordinal);
foreach (var (a, b, _) in crossReactions)
{
    if (!classNames.Contains(a) || !classNames.Contains(b) || string.CompareOrdinal(a, b) >= 0)
    {
        Console.Error.WriteLine($"Cross-reaction {a} / {b}: both must be loaded class names, the first before the second.");
        return 1;
    }
}

Write("40-drug-classes.sql",
    "Each drug product's ingredients (RxNorm), each ingredient's FDA Established Pharmacologic Classes, and the classes that cross-react (curated, each row citing its source).",
    new Table("drug_ingredients", ["product_code", "ingredient_code"], 2, ingredientRows),
    new Table("drug_classes", ["ingredient_code", "class_name"], 2, [.. classRows.Select(row => new[] { row.Ingredient, row.Class })]),
    new Table("drug_class_cross_reactions", ["class_a", "class_b", "note", "source"], 2,
        [.. crossReactions.Select(row => new[] { row.A, row.B, row.Note, PracticeParameter })]));
Console.WriteLine($"drug_ingredients            {ingredientRows.Count,8:N0}  for {products.Count:N0} products");
Console.WriteLine($"drug_classes                {classRows.Count,8:N0}  for {classRows.Select(row => row.Ingredient).Distinct().Count():N0} ingredients ({byLabels:N0} through labels)");

// ---- LOINC -------------------------------------------------------------------------------------------------------

var loinc = new Dictionary<string, Dictionary<string, string>>(StringComparer.Ordinal);
using (var table = Open("loinc.zip", LoincSha, "LoincTable/Loinc.csv"))
{
    foreach (var row in Csv(table))
    {
        loinc[row["LOINC_NUM"]] = row;
    }
}

// What can be ordered. Labs: LOINC's Universal Lab Orders Value Set. Medications: RxNorm's clinical drugs. Each
// under its own code, which is also its standard code. The clinic's common items (SYN-...) are the migration's.
const int MaximumItem = 200;
var catalog = new List<string?[]>();
using (var orders = Open("loinc.zip", LoincSha, "AccessoryFiles/LoincUniversalLabOrdersValueSet/LoincUniversalLabOrdersValueSet.csv"))
{
    catalog.AddRange(Csv(orders).Where(row => row["LONG_COMMON_NAME"].Length <= MaximumItem)
        .Select(row => new[] { row["LOINC_NUM"], "lab", row["LONG_COMMON_NAME"], "loinc", row["LOINC_NUM"] }));
}

var labOrders = catalog.Count;
catalog.AddRange(rxType.Where(pair => pair.Value == "SCD" && rxName[pair.Key].Length <= MaximumItem)
    .Select(pair => new[] { pair.Key, "medication", rxName[pair.Key], "rxnorm", pair.Key }));
catalog = [.. catalog.OrderBy(row => row[1], StringComparer.Ordinal).ThenBy(row => row[0], StringComparer.Ordinal)];
Write("20-order-catalog.sql",
    "What can be ordered beyond the clinic's common items: LOINC's Universal Lab Orders Value Set, and RxNorm's clinical drugs.\n"
    + "-- An item is added or renamed, never removed: an order may name it. The common items (SYN-...) are the Orders migration's.",
    new Table("order_catalog_items", ["code", "type", "display", "standard_system", "standard_code"], 1, catalog, Keep: true, Where: "NOT order_catalog_items.common"));
Console.WriteLine($"order_catalog_items         {catalog.Count,8:N0}  ({labOrders:N0} lab orders, {catalog.Count - labOrders:N0} medications)");

// The results, vital signs and scores the clinic records, with the UCUM unit each is recorded in and this clinic's
// reference range. The ranges are this clinic's own, set for synthetic data from the adult intervals textbooks and
// laboratories commonly publish: they are not LOINC's, and not validated for clinical use.
const string Range = "This clinic's range for adults, set for synthetic data from commonly published intervals; not validated for clinical use.";
(string Code, string Kind, string Unit, decimal? Low, decimal? High)[] observations =
[
    ("4548-4", "lab", "%", 4.0m, 5.6m), ("2345-7", "lab", "mg/dL", 70m, 99m), ("2093-3", "lab", "mg/dL", 125m, 199m),
    ("13457-7", "lab", "mg/dL", null, 99m), ("2085-9", "lab", "mg/dL", 40m, 90m), ("2571-8", "lab", "mg/dL", null, 149m),
    ("718-7", "lab", "g/dL", 12.0m, 17.5m), ("4544-3", "lab", "%", 36m, 50m), ("6690-2", "lab", "10*3/uL", 4.5m, 11.0m),
    ("777-3", "lab", "10*3/uL", 150m, 400m), ("2160-0", "lab", "mg/dL", 0.6m, 1.3m), ("3094-0", "lab", "mg/dL", 7m, 20m),
    ("2823-3", "lab", "mmol/L", 3.5m, 5.1m), ("2951-2", "lab", "mmol/L", 135m, 145m), ("17861-6", "lab", "mg/dL", 8.6m, 10.2m),
    ("1742-6", "lab", "U/L", 7m, 56m), ("1920-8", "lab", "U/L", 10m, 40m), ("1975-2", "lab", "mg/dL", 0.1m, 1.2m),
    ("1751-7", "lab", "g/dL", 3.5m, 5.0m), ("3016-3", "lab", "m[IU]/L", 0.4m, 4.0m), ("62292-8", "lab", "ng/mL", 30m, 100m),
    ("1989-3", "lab", "ng/mL", 30m, 100m),
    ("8480-6", "vital", "mm[Hg]", 90m, 129m), ("8462-4", "vital", "mm[Hg]", 60m, 79m), ("8867-4", "vital", "/min", 60m, 100m),
    ("9279-1", "vital", "/min", 12m, 20m), ("8310-5", "vital", "Cel", 36.1m, 37.5m), ("59408-5", "vital", "%", 95m, 100m),
    ("29463-7", "vital", "kg", null, null), ("8302-2", "vital", "cm", null, null), ("39156-5", "vital", "kg/m2", 18.5m, 24.9m),
    // A questionnaire's total has no range: it is scored, banded and flagged by the questionnaire's own definition
    // (db/reference/36-questionnaires.sql, the expansion plan's step 13e).
    ("44261-6", "survey", "{score}", null, null), ("70274-6", "survey", "{score}", null, null),
];
var observationRows = observations.Select(o => new[]
{
    o.Code, loinc[o.Code]["LONG_COMMON_NAME"], o.Kind, o.Unit, Number(o.Low), Number(o.High), o.Low is null && o.High is null ? null : Range,
    Blank(loinc[o.Code]["EXTERNAL_COPYRIGHT_NOTICE"]),
}).ToList();

// Which results a common lab order produces, under the clinic's own code for the order and, where it has one, the
// order's LOINC code.
(string[] Orders, string[] Results)[] panels =
[
    (["SYN-LAB-001", "58410-2"], ["718-7", "4544-3", "6690-2", "777-3"]),
    (["SYN-LAB-002"], ["2345-7", "3094-0", "2160-0", "2951-2", "2823-3", "17861-6"]),
    (["SYN-LAB-003", "24323-8"], ["2345-7", "3094-0", "2160-0", "2951-2", "2823-3", "17861-6", "1742-6", "1920-8", "1975-2", "1751-7"]),
    (["SYN-LAB-004", "57698-3"], ["2093-3", "13457-7", "2085-9", "2571-8"]),
    (["SYN-LAB-005"], ["4548-4"]),
    (["SYN-LAB-006", "3016-3"], ["3016-3"]),
    (["SYN-LAB-008", "62292-8"], ["62292-8"]),
];
var resultRows = panels.SelectMany(panel => panel.Orders.SelectMany(order => panel.Results.Select(result => new[] { order, result }))).ToList();

Write("30-observations.sql",
    "The results, vital signs and survey scores the clinic records (LOINC codes, UCUM units, this clinic's reference ranges), and which\n"
    + "-- results a common lab order produces. The PHQ-9 and GAD-7 themselves are db/reference/36-questionnaires.sql.\n"
    + "-- This material contains content from LOINC (http://loinc.org). LOINC is copyright © Regenstrief Institute, Inc. and the Logical\n"
    + "-- Observation Identifiers Names and Codes (LOINC) Committee and is available at no cost under the license at\n"
    + "-- http://loinc.org/license. LOINC® is a registered United States trademark of Regenstrief Institute, Inc.",
    new Table("observation_codes", ["code", "display", "kind", "unit", "reference_low::numeric", "reference_high::numeric", "range_source", "copyright"], 1, observationRows),
    new Table("order_result_codes", ["catalog_code", "observation_code"], 2, resultRows));
Console.WriteLine($"observation_codes           {observationRows.Count,8:N0}");
Console.WriteLine($"order_result_codes          {resultRows.Count,8:N0}");

// ---- DDInter 2.0: interactions between two drugs, by name, with a level --------------------------------------------

// DDInter names drugs as DrugBank does, mostly by international nonproprietary name; RxNorm uses the United States
// name. An interaction is kept when both names are RxNorm ingredients, as they stand or through this list.
var usName = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
{
    ["acetylsalicylic acid"] = "aspirin", ["salbutamol"] = "albuterol", ["levosalbutamol"] = "levalbuterol", ["paracetamol"] = "acetaminophen",
    ["torasemide"] = "torsemide", ["etacrynic acid"] = "ethacrynic acid", ["isoprenaline"] = "isoproterenol", ["orciprenaline"] = "metaproterenol",
    ["metamfetamine"] = "methamphetamine", ["amfetamine"] = "amphetamine", ["dexamfetamine"] = "dextroamphetamine", ["norethisterone"] = "norethindrone",
    ["ethinylestradiol"] = "ethinyl estradiol", ["ciclosporin"] = "cyclosporine", ["rifampicin"] = "rifampin", ["pethidine"] = "meperidine",
    ["hydroxycarbamide"] = "hydroxyurea", ["glycopyrronium"] = "glycopyrrolate", ["clomifene"] = "clomiphene", ["colestyramine"] = "cholestyramine",
    ["aciclovir"] = "acyclovir", ["cefalexin"] = "cephalexin", ["beclometasone"] = "beclomethasone", ["lidocaine"] = "lidocaine",
    ["adrenaline"] = "epinephrine", ["noradrenaline"] = "norepinephrine", ["valproic acid"] = "valproate", ["glibenclamide"] = "glyburide",
    ["benzylpenicillin"] = "penicillin G", ["phenoxymethylpenicillin"] = "penicillin V", ["sulfamethoxazole"] = "sulfamethoxazole",
    ["lithium carbonate"] = "lithium", ["furosemide"] = "furosemide", ["bendroflumethiazide"] = "bendroflumethiazide", ["tetracosactide"] = "cosyntropin",
    ["dextropropoxyphene"] = "propoxyphene", ["alimemazine"] = "trimeprazine", ["cromoglicic acid"] = "cromolyn", ["ulobetasol"] = "halobetasol",
    ["somatotropin"] = "somatropin", ["methotrimeprazine"] = "levomepromazine", ["thiamazole"] = "methimazole", ["mesalazine"] = "mesalamine",
    ["amoxicillin"] = "amoxicillin", ["nicotinamide"] = "niacinamide", ["pizotifen"] = "pizotyline", ["ergometrine"] = "ergonovine",
};
var ingredientByName = rxType.Where(pair => pair.Value == "IN").GroupBy(pair => rxName[pair.Key], StringComparer.OrdinalIgnoreCase)
    .ToDictionary(group => group.Key, group => group.Select(pair => pair.Key).Min(StringComparer.Ordinal)!, StringComparer.OrdinalIgnoreCase);
string? Ingredient(string name)
{
    // "Betamethasone (topical)" is betamethasone: the interaction is recorded against the ingredient, whatever the route.
    var plain = Regex.Replace(name, "\\s*\\([^)]*\\)\\s*$", "").Trim();
    return ingredientByName.GetValueOrDefault(usName.GetValueOrDefault(plain, plain));
}

(string File, string Sha)[] ddinter =
[
    ("ddinter_downloads_code_A.csv", "a22ca451d2b755ca2331886f7e00540c86f555f9f55704a59a19c691251f52e0"),
    ("ddinter_downloads_code_B.csv", "76de5115a55587f0e822e1096b684fd3ddde058fbefcbb19896df58820ace130"),
    ("ddinter_downloads_code_D.csv", "c0627ec39965dbe27829e4934cd20d71eb079f4e373287678737bba9423a306a"),
    ("ddinter_downloads_code_H.csv", "f0f925f0ba1ee68c4668d3e7a6732719b34623b22a0f5d017f9090e59f63c88e"),
    ("ddinter_downloads_code_L.csv", "f54f4486cc00344f8c86508c31c2ca3fad6d1d37ec3af5bcf609b4bbda5507ef"),
    ("ddinter_downloads_code_P.csv", "83f8c0edb20d09ef29b8500b48f9623e208379525688ade9e70a7df2d8749d55"),
    ("ddinter_downloads_code_R.csv", "1c39c3d4a6e41659b7a988538d7f363867592abc6f41a815de8746f6e2150574"),
    ("ddinter_downloads_code_V.csv", "353973fb300453946aea95733e8fb52338e4b59426d2ce6ca318363d4f84f10a"),
];
// The worst level DDInter gives a pair, in any of its files. "Unknown" is not a level a warning can state, and is left out.
string[] levels = ["minor", "moderate", "major"];
var interactions = new SortedDictionary<(string A, string B), int>();
var (pairs, unmatchedPairs, unknown) = (0, 0, 0);
var unmatchedNames = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
var drugNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
foreach (var (file, sha) in ddinter)
{
    var path = Path.Combine(sources, "ddinter", file);
    Check(path, sha);
    using var reader = new StreamReader(path, Encoding.UTF8);
    // DDInterID_A, Drug_A, DDInterID_B, Drug_B, Level
    foreach (var row in Csv(reader))
    {
        pairs++;
        drugNames.Add(row["Drug_A"]);
        drugNames.Add(row["Drug_B"]);
        var level = Array.IndexOf(levels, row["Level"].ToLowerInvariant());
        if (level < 0)
        {
            unknown++;
            continue;
        }

        var (a, b) = (Ingredient(row["Drug_A"]), Ingredient(row["Drug_B"]));
        foreach (var (name, found) in new[] { (row["Drug_A"], a), (row["Drug_B"], b) })
        {
            if (found is null)
            {
                unmatchedNames[name] = unmatchedNames.GetValueOrDefault(name) + 1;
            }
        }

        if (a is null || b is null || a == b)
        {
            unmatchedPairs++;
            continue;
        }

        var key = string.CompareOrdinal(a, b) < 0 ? (a, b) : (b, a);
        interactions[key] = Math.Max(level, interactions.GetValueOrDefault(key, -1));
    }
}

Write(Path.Combine("ddinter", "10-drug-interactions.sql"),
    "Interactions between two drug ingredients (RxNorm codes, the lower first), each with DDInter's level.\n"
    + "-- From DDInter 2.0 (https://ddinter2.scbdd.com), licensed CC BY-NC-SA 4.0: see LICENSE.md in this folder. Not for commercial use.",
    new Table("drug_interactions", ["ingredient_a", "ingredient_b", "source", "level"], 3,
        [.. interactions.Select(pair => new[] { pair.Key.A, pair.Key.B, "ddinter", levels[pair.Value] })], Scope: "t.source = 'ddinter'"));
Console.WriteLine($"drug_interactions           {interactions.Count,8:N0}  from {pairs:N0} rows: {unknown:N0} of unknown level, {unmatchedPairs:N0} with a drug that isn't a RxNorm ingredient");
Console.WriteLine($"  DDInter drugs: {drugNames.Count:N0}; not matched: {unmatchedNames.Count:N0}, the most frequent: "
    + string.Join(", ", unmatchedNames.OrderByDescending(pair => pair.Value).Take(12).Select(pair => pair.Key)));

// ---- The ONC high-priority interactions, from Wikidata ----------------------------------------------------------------

// Wikidata's "significant drug interaction" (P769) statements whose reference is the ONC high-priority list (Phansalkar
// et al., J Am Med Inform Assoc 2012, Q17505343), each side with its RxNorm code (P3345). Wikidata is CC0. The query's
// result is committed, dated, beside the query that made it (db/reference/wikidata), so this step reads the
// repository and never the network. A pair is kept when both codes are RxNorm ingredients; every one is "high-priority".
const string oncCsv = "onc-high-priority-interactions-2026-11-19.csv";
const string oncCitation = "ONC high-priority drug-drug interactions (Phansalkar S, et al. J Am Med Inform Assoc. 2012;19(5):735-743), from Wikidata's \"significant drug interaction\" statements, 19 November 2026.";
var ingredientCodes = rxType.Where(pair => pair.Value == "IN").Select(pair => pair.Key).ToHashSet(StringComparer.Ordinal);
var onc = new SortedSet<(string A, string B)>();
var (oncRows, oncUnknown) = (0, 0);
var oncUnmatched = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
using (var reader = new StreamReader(Path.Combine(root, "db", "reference", "wikidata", oncCsv), Encoding.UTF8))
{
    foreach (var row in Csv(reader))
    {
        oncRows++;
        // Wikidata's code is usually the RxNorm ingredient. Where it is another RxNorm concept for the same drug
        // (bosentan's is), the drug is found by its name instead.
        var a = ingredientCodes.Contains(row["a_rxnorm"]) ? row["a_rxnorm"] : Ingredient(row["a_label"]);
        var b = ingredientCodes.Contains(row["b_rxnorm"]) ? row["b_rxnorm"] : Ingredient(row["b_label"]);
        if (a is null || b is null || a == b)
        {
            oncUnknown++;
            oncUnmatched.Add(a is null ? row["a_label"] : row["b_label"]);
            continue;
        }

        onc.Add(string.CompareOrdinal(a, b) < 0 ? (a, b) : (b, a));
    }
}

Write("50-onc-interactions.sql",
    "The ONC high-priority drug-drug interactions (Phansalkar et al., 2012) between two ingredients (RxNorm codes, the lower\n"
    + $"-- first), from Wikidata's \"significant drug interaction\" statements (CC0): db/reference/wikidata/{oncCsv}.",
    new Table("drug_interactions", ["ingredient_a", "ingredient_b", "source", "level", "citation"], 3,
        [.. onc.Select(pair => new[] { pair.A, pair.B, "onc", "high-priority", oncCitation })], Scope: "t.source = 'onc'"));
Console.WriteLine($"drug_interactions (ONC)     {onc.Count,8:N0}  from {oncRows:N0} statements: {oncUnknown:N0} with a side that isn't a RxNorm ingredient");
Console.WriteLine($"  ONC drugs not matched: {string.Join(", ", oncUnmatched)}");
return 0;

// ---- Helpers -----------------------------------------------------------------------------------------------------

// One generated file: a COPY block into a temporary table for each table, then the changes that make the table hold
// exactly those rows (or, with Keep, at least those rows).
void Write(string name, string about, params Table[] tables)
{
    var sql = new StringBuilder();
    sql.Append("-- Generated by tools/reference/import-catalogs.cs from pinned source files: change that script, not this file.\n");
    sql.Append("-- ").Append(about).Append('\n');
    sql.Append("-- Applied as ehr_owner after db/policies; safe to run again. Sources, versions, licenses and attribution:\n");
    sql.Append("-- docs/expansion/reference-data.md. The rows are COPY blocks, as pg_dump writes them: psql loads the file as it\n");
    sql.Append("-- stands, and the tests' database through tests/Ehr.Testing/ReferenceData.cs. Tab-separated; \\N is a null.\n");
    sql.Append("BEGIN;\n");
    foreach (var table in tables)
    {
        var columns = table.Columns.Select(column => column.Split("::")[0]).ToArray();
        var keys = columns[..table.Keys];
        var rest = columns[table.Keys..];
        sql.Append($"CREATE TEMPORARY TABLE incoming_{table.Name} ({string.Join(", ", columns.Select(column => column + " text"))}) ON COMMIT DROP;\n");
        sql.Append($"COPY incoming_{table.Name} ({string.Join(", ", columns)}) FROM stdin;\n");
        foreach (var row in table.Rows)
        {
            sql.AppendJoin('\t', row.Select(value => value is null ? "\\N" : Field(value))).Append('\n');
        }

        sql.Append("\\.\n");
        sql.Append($"INSERT INTO ehr.{table.Name} ({string.Join(", ", columns)})\n");
        sql.Append($"SELECT {string.Join(", ", table.Columns)} FROM incoming_{table.Name}\n");
        sql.Append($"ON CONFLICT ({string.Join(", ", keys)}) DO ");
        if (rest.Length == 0)
        {
            sql.Append("NOTHING;\n");
        }
        else
        {
            sql.Append($"UPDATE\n    SET {string.Join(", ", rest.Select(column => $"{column} = EXCLUDED.{column}"))}\n");
            sql.Append($"    WHERE ({string.Join(", ", rest.Select(column => $"{table.Name}.{column}"))})\n");
            sql.Append($"        IS DISTINCT FROM ({string.Join(", ", rest.Select(column => $"EXCLUDED.{column}"))})");
            sql.Append(table.Where is null ? ";\n" : $"\n        AND {table.Where};\n");
        }

        if (!table.Keep)
        {
            sql.Append($"DELETE FROM ehr.{table.Name} t\n");
            sql.Append($"WHERE NOT EXISTS (SELECT 1 FROM incoming_{table.Name} i WHERE {string.Join(" AND ", keys.Select(key => $"i.{key} = t.{key}"))})");
            sql.Append(table.Scope is null ? ";\n" : $"\n    AND {table.Scope};\n");
        }
    }

    sql.Append("COMMIT;\n");
    var output = Path.Combine(root, "db", "reference", name);
    Directory.CreateDirectory(Path.GetDirectoryName(output)!);
    File.WriteAllText(output, sql.ToString(), new UTF8Encoding(false));
}

// One file of a source archive, as text, once the archive's checksum has been checked.
StreamReader Open(string archive, string sha256, string entry)
{
    var zip = OpenZip(archive, sha256);
    var found = zip.Entries.Single(e => e.FullName == entry || e.FullName.EndsWith("/" + entry, StringComparison.Ordinal));
    return new StreamReader(found.Open(), Encoding.UTF8);
}

ZipArchive OpenZip(string archive, string sha256)
{
    var path = Path.Combine(sources, archive);
    Check(path, sha256);
    return ZipFile.OpenRead(path);
}

// Each source file is checked once.
void Check(string path, string sha256)
{
    if (!Checked.Files.Add(path))
    {
        return;
    }

    using var file = File.OpenRead(path);
    var actual = Convert.ToHexStringLower(SHA256.HashData(file));
    if (actual != sha256)
    {
        throw new InvalidOperationException($"{Path.GetFileName(path)} is not the pinned release: its SHA-256 is {actual}, expected {sha256}.");
    }
}

// The rows of a CSV file with a header line, by column name: quoted fields, doubled quotes, and line breaks inside quotes.
static IEnumerable<Dictionary<string, string>> Csv(TextReader reader)
{
    string[]? header = null;
    var fields = new List<string>();
    var field = new StringBuilder();
    var quoted = false;
    int next;
    while ((next = reader.Read()) >= 0)
    {
        var c = (char)next;
        if (quoted)
        {
            if (c == '"' && reader.Peek() == '"')
            {
                field.Append('"');
                reader.Read();
            }
            else if (c == '"')
            {
                quoted = false;
            }
            else
            {
                field.Append(c);
            }
        }
        else if (c == '"')
        {
            quoted = true;
        }
        else if (c == ',')
        {
            fields.Add(field.ToString());
            field.Clear();
        }
        else if (c == '\n')
        {
            fields.Add(field.ToString().TrimEnd('\r'));
            field.Clear();
            if (header is null)
            {
                header = [.. fields.Select(name => name.Trim('\uFEFF'))];
            }
            else if (fields.Count == header.Length)
            {
                yield return header.Zip(fields).ToDictionary(pair => pair.First, pair => pair.Second, StringComparer.Ordinal);
            }

            fields.Clear();
        }
        else
        {
            field.Append(c);
        }
    }
}

static List<T> Into<T>(Dictionary<string, List<T>> map, string key) => map.TryGetValue(key, out var list) ? list : map[key] = [];

static string? Number(decimal? value) => value?.ToString(CultureInfo.InvariantCulture);

static string? Blank(string value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

// A value as COPY's text format takes it.
static string Field(string value) => value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\t", "\\t", StringComparison.Ordinal)
    .Replace("\n", "\\n", StringComparison.Ordinal).Replace("\r", "\\r", StringComparison.Ordinal);

static string ScriptPath([CallerFilePath] string path = "") => path;

// A table to fill: its columns (a cast after "::" where the column isn't text), how many of the first are its key,
// and its rows. Keep leaves rows the file doesn't have; Where limits which existing rows it may change.
// Scope limits which existing rows it may delete, for a table that other files fill too (alias t).
record Table(string Name, string[] Columns, int Keys, IReadOnlyList<string?[]> Rows, bool Keep = false, string? Where = null, string? Scope = null);

static class Checked
{
    public static readonly HashSet<string> Files = new(StringComparer.Ordinal);
}
