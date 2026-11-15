namespace Ehr.Web.Areas.Clinical.Pages.Patients;

// A patient search's criteria as the clinician typed them, in the query string as name, birthdate and mrn. They travel
// from the search to a chart, through the emergency-access reason, and back, so "Back to search" returns to the same
// results. The chart and the reason page only echo them into links; only the search reads patients with them (Index).
public sealed record SearchQuery(string? Name, string? Birthdate, string? Mrn)
{
    public static readonly SearchQuery None = new(null, null, null);

    public bool IsEmpty => string.IsNullOrWhiteSpace(Name) && string.IsNullOrWhiteSpace(Birthdate) && string.IsNullOrWhiteSpace(Mrn);

    // For a redirect to a patient's chart or to the reason it asks for: the patient's ID, and the criteria.
    public RouteValueDictionary ChartRouteValues(Guid id)
    {
        var values = new RouteValueDictionary { ["id"] = id };
        foreach (var (key, value) in RouteData())
        {
            values[key] = value;
        }

        return values;
    }

    // For a link: the criteria given, under their query string names.
    public Dictionary<string, string> RouteData()
    {
        var data = new Dictionary<string, string>();
        Add("name", Name);
        Add("birthdate", Birthdate);
        Add("mrn", Mrn);
        return data;

        void Add(string key, string? value)
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                data[key] = value;
            }
        }
    }
}
