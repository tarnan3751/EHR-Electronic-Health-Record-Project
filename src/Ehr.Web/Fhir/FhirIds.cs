namespace Ehr.Web.Fhir;

// What the mapping writes for an entry's id and for its references to the patient and the clinician. The clinician API's
// (Record) are the record's own: each entry's ID, Patient/{ID} and Practitioner/{ID}. A file made for one download
// (ForDownload: the portal's, the expansion plan's step 13b) has ids of its own instead: each record's ID becomes a random
// UUID made for that file, the same one wherever the file writes it, and a reference is that entry's urn:uuid: address in
// the file, so no identifier of the clinic's leaves it. A clinician, who is no entry of the file, is named there and not
// referred to.
internal sealed class FhirIds
{
    public static readonly FhirIds Record = new(null);

    // The file's own UUID for each record ID it has written, made the first time.
    readonly Dictionary<Guid, Guid>? made;

    FhirIds(Dictionary<Guid, Guid>? made) => this.made = made;

    public static FhirIds ForDownload() => new([]);

    public string Id(Guid record) => (made is null ? record : Made(record)).ToString();

    // Where an entry is in a file made for a download: the urn:uuid: address its references use.
    public string Address(Guid record) => $"urn:uuid:{Made(record)}";

    public ResourceReference Patient(Guid patient, string? name = null) => new(made is null ? $"Patient/{patient}" : Address(patient), name);

    public ResourceReference Practitioner(Guid clinician, string name) => new(made is null ? $"Practitioner/{clinician}" : null, name);

    Guid Made(Guid record)
    {
        var ids = made ?? throw new InvalidOperationException("The record's own IDs have no address in a file.");
        if (!ids.TryGetValue(record, out var id))
        {
            id = Guid.NewGuid();
            ids.Add(record, id);
        }

        return id;
    }
}
