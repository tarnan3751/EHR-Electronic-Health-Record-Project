using Ehr.Data.Orders;
using Ehr.Data.Reference;

namespace Ehr.Domain.Orders;

// How an ordering safety warning (step 8) is put to the clinician. The sources give a pair and a level, and the
// hand-written rows say what the FDA label says; DDInter's files carry no advice. So each level has one short line of
// this project's own, deliberately general: it is decision support for a demonstration system, not a clinical
// reference, and every warning says where it comes from so the clinician can look it up.
public static class OrderSafetyText
{
    public const string Disclaimer =
        "Decision support for this demonstration system, not a complete clinical reference. Only coded allergies and medications are checked, "
        + "and no warning doesn't mean a combination is safe.";

    // Where DDInter can be consulted when there is internet. Its download files don't give addresses for single pairs.
    public const string DdInterChecker = "https://ddinter2.scbdd.com/inter-checker/";

    // Whether a warning stops the order until a reason is given: an allergy, a class the patient is allergic to, a
    // major or high-priority interaction. The database's signing trigger holds the same rule (60-orders.sql). A
    // moderate or minor interaction is shown and kept on the order without one.
    public static bool NeedsReason(SafetyWarning warning) =>
        warning.Kind != "interaction" || warning.Severity is DrugInteractionLevel.Major or DrugInteractionLevel.HighPriority;

    // Allergies first, then interactions from the most severe.
    public static IEnumerable<SafetyWarning> Ordered(IEnumerable<SafetyWarning> warnings) =>
        warnings.OrderBy(w => w.Kind switch { "allergy" => 0, "allergy-class" => 1, _ => 2 })
            .ThenBy(w => w.Severity switch { DrugInteractionLevel.HighPriority => 0, DrugInteractionLevel.Major => 1, DrugInteractionLevel.Moderate => 2, _ => 3 })
            .ThenBy(w => w.Against, StringComparer.OrdinalIgnoreCase).ThenBy(w => w.Source, StringComparer.Ordinal);

    public static string Heading(SafetyWarning warning) => warning.Kind switch
    {
        "allergy" => $"Allergy: {warning.Ordered}",
        "allergy-class" => $"Possible cross-sensitivity: {warning.Ordered}, and an allergy to {warning.Against}",
        _ => $"Interaction: {warning.Ordered} with {warning.Against}",
    };

    public static string SeverityName(string severity) => severity switch
    {
        DrugInteractionLevel.HighPriority => "High priority",
        DrugInteractionLevel.Major => "Major",
        DrugInteractionLevel.Moderate => "Moderate",
        _ => "Minor",
    };

    // The one general line for a level, or for an allergy.
    public static string Advice(SafetyWarning warning) => warning.Kind switch
    {
        "allergy" => $"The chart records an allergy to {warning.Against}. Don't order it unless the allergy has been ruled out.",
        "allergy-class" => "A drug of a related class can cause the same reaction. Check what the reaction was before ordering.",
        _ => warning.Severity switch
        {
            DrugInteractionLevel.HighPriority => "On the list of interactions an electronic record should always stop for. Avoid the combination.",
            DrugInteractionLevel.Major => "Avoid the combination unless the benefit clearly outweighs the risk.",
            DrugInteractionLevel.Moderate => "Use together with caution: monitor the patient, and consider adjusting a dose.",
            _ => "A small effect is expected. No change is usually needed.",
        },
    };

    public static string SourceName(string source) => source switch
    {
        DrugInteractionSource.DdInter => "DDInter 2.0",
        DrugInteractionSource.Onc => "ONC high-priority list, by way of Wikidata",
        DrugInteractionSource.FdaLabel => "FDA label",
        "cross-reactivity" => "Cross-reactivity between classes",
        "fda-class" => "FDA pharmacologic class",
        _ => "The patient's chart",
    };
}
