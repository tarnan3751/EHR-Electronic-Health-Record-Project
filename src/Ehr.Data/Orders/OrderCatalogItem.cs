namespace Ehr.Data.Orders;

// UC 1 Place an order (Tahseen Arnan): one thing that can be ordered. Not PHI: grants only, read by both apps, and
// nothing at runtime changes it. Two sets of items share the table. The clinic's common items, with the app's own
// SYN-... codes, are seeded by migration (Seeded, through HasData): the order form lists them, and where a standard
// code applies each names it (LOINC for a lab test, RxNorm for a medication). The rest are standard items under
// their own codes, LOINC's lab orders and RxNorm's clinical drugs, loaded with the reference data (db/reference): the
// order form finds those as the clinician types.
public class OrderCatalogItem
{
    public required string Code { get; set; }

    // One of OrderType.
    public required string Type { get; set; }

    // The item as clinicians read it, unique within its type: what the order form suggests and takes.
    public required string Display { get; set; }

    // The name in lower case, kept by the database, for the form's search and its check of the name picked.
    public string DisplayKey { get; set; } = "";

    // One of the clinic's common items, which the order form lists without being asked.
    public bool Common { get; set; }

    // The standard code the item stands for, and its system (ClinicalCodeSystem: "loinc" or "rxnorm"), when it has
    // one. For an item loaded from a standard list these repeat its own code.
    public string? StandardSystem { get; set; }

    public string? StandardCode { get; set; }

    // Changing this list takes a migration.
    public static readonly OrderCatalogItem[] Seeded =
    [
        Item("SYN-LAB-001", OrderType.Lab, "Complete blood count (CBC)", "loinc", "58410-2"),
        Item("SYN-LAB-002", OrderType.Lab, "Basic metabolic panel (BMP)"),
        Item("SYN-LAB-003", OrderType.Lab, "Comprehensive metabolic panel (CMP)", "loinc", "24323-8"),
        Item("SYN-LAB-004", OrderType.Lab, "Lipid panel", "loinc", "57698-3"),
        Item("SYN-LAB-005", OrderType.Lab, "Hemoglobin A1c"),
        Item("SYN-LAB-006", OrderType.Lab, "Thyroid-stimulating hormone (TSH)", "loinc", "3016-3"),
        Item("SYN-LAB-007", OrderType.Lab, "Urinalysis", "loinc", "24356-8"),
        Item("SYN-LAB-008", OrderType.Lab, "Vitamin D, 25-hydroxy", "loinc", "62292-8"),
        Item("SYN-IMG-001", OrderType.Imaging, "Chest X-ray, 2 views"),
        Item("SYN-IMG-002", OrderType.Imaging, "Lumbar spine X-ray"),
        Item("SYN-IMG-003", OrderType.Imaging, "Abdominal ultrasound"),
        Item("SYN-IMG-004", OrderType.Imaging, "CT head without contrast"),
        Item("SYN-IMG-005", OrderType.Imaging, "MRI lumbar spine without contrast"),
        Item("SYN-IMG-006", OrderType.Imaging, "Screening mammogram"),
        Item("SYN-MED-001", OrderType.Medication, "Lisinopril 10 mg tablet", "rxnorm", "314076"),
        Item("SYN-MED-002", OrderType.Medication, "Metformin 500 mg tablet", "rxnorm", "861007"),
        Item("SYN-MED-003", OrderType.Medication, "Atorvastatin 20 mg tablet", "rxnorm", "617310"),
        Item("SYN-MED-004", OrderType.Medication, "Amlodipine 5 mg tablet", "rxnorm", "197361"),
        Item("SYN-MED-005", OrderType.Medication, "Amoxicillin 500 mg capsule", "rxnorm", "308191"),
        Item("SYN-MED-006", OrderType.Medication, "Albuterol 90 mcg inhaler"),
        Item("SYN-MED-007", OrderType.Medication, "Sertraline 50 mg tablet", "rxnorm", "312941"),
        Item("SYN-MED-008", OrderType.Medication, "Ibuprofen 400 mg tablet", "rxnorm", "197805"),
        Item("SYN-REF-001", OrderType.Referral, "Cardiology referral"),
        Item("SYN-REF-002", OrderType.Referral, "Dermatology referral"),
        Item("SYN-REF-003", OrderType.Referral, "Endocrinology referral"),
        Item("SYN-REF-004", OrderType.Referral, "Gastroenterology referral"),
        Item("SYN-REF-005", OrderType.Referral, "Orthopedics referral"),
        Item("SYN-REF-006", OrderType.Referral, "Physical therapy referral"),
        Item("SYN-REF-007", OrderType.Referral, "Psychiatry referral"),
    ];

    // A common item, with the standard code it stands for when the standard lists have one. The basic metabolic
    // panel, hemoglobin A1c as an order, and the inhaler have none in the lists loaded.
    static OrderCatalogItem Item(string code, string type, string display, string? system = null, string? standard = null) =>
        new() { Code = code, Type = type, Display = display, Common = true, StandardSystem = system, StandardCode = standard };
}
