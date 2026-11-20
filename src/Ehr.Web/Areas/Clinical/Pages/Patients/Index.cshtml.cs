using System.Globalization;
using Ehr.Data.Patients;
using Ehr.Domain.Patients;
using Ehr.Domain.Phi;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Ehr.Web.Areas.Clinical.Pages.Patients;

// The clinician's patients: their own panel first, and a search across the clinic by name, date of birth and MRN,
// combined, with the patients on their panel marked as theirs and the rest as restricted, since opening one of those
// asks for a reason first (Reason). Criteria that can't be searched as given say why instead. Results also come as the
// clinician types (OnGetResultsAsync, through htmx), the results alone and fewer of them; a plain submit still works
// without JavaScript. Every patient listed is a PHI read, recorded by the PHI reader; row-level security decides which
// patients exist for this clinician.
public class IndexModel(PhiReader phi, PatientAssignments assignments) : PageModel
{
    // A search can be narrowed, and each patient it lists is a recorded read, most of a broad search's time (NFR-01).
    public const int Limit = 50;

    // As the clinician types, each pause lists patients again, and each one listed is a recorded read: so fewer.
    public const int LiveLimit = 20;

    // A panel can't be narrowed, so it lists more: a lower cap would hide the clinician's own patients.
    public const int PanelLimit = 100;

    // The longest a name can be (PatientConfiguration); an MRN's is PhiReader's.
    const int MaximumNameLength = 100;

    [BindProperty(SupportsGet = true)]
    public SearchQuery Search { get; set; } = SearchQuery.None;

    // Why the search can't run as given, to correct; empty when it ran or there was none.
    public IReadOnlyList<string> Problems { get; private set; } = [];

    // Whether a search ran: then Results holds what it found, otherwise PanelPatients the panel.
    public bool Searched { get; private set; }

    // The panel, when there is no search, and whether more patients are on it than are listed.
    public IReadOnlyList<Patient> PanelPatients { get; private set; } = [];

    public bool PanelMore { get; private set; }

    // The search results, when there is one, and whether more patients matched than are listed.
    public IReadOnlyList<Patient> Results { get; private set; } = [];

    public bool More { get; private set; }

    // Whether these are results as the clinician types, which list up to LiveLimit.
    public bool Live { get; private set; }

    // Who is on the panel, to mark in the results.
    public IReadOnlySet<Guid> Panel { get; private set; } = new HashSet<Guid>();

    // The latest date of birth a search takes: today, with a day's slack for the clinic's time zone.
    public static DateOnly LatestBirthDate => DateOnly.FromDateTime(DateTime.UtcNow).AddDays(1);

    public Task OnGetAsync() => LoadAsync(Limit);

    // The results alone, for htmx to put under the form as the clinician types.
    public async Task<IActionResult> OnGetResultsAsync()
    {
        Live = true;
        await LoadAsync(LiveLimit);
        return Partial("_SearchResults", this);
    }

    async Task LoadAsync(int limit)
    {
        if (Search.IsEmpty)
        {
            // Fields emptied as the clinician types list nobody: the panel would be up to PanelLimit more reads at every
            // pause, and Clear brings it back. The form submitted with every field empty lists nobody either, and asks
            // for something to search by (UC 3): only the page with no criteria at all, where signing in, Clear and the
            // way back from a chart land, lists the panel.
            if (Live)
            {
                return;
            }

            if (Request.Query.ContainsKey("name") || Request.Query.ContainsKey("birthdate") || Request.Query.ContainsKey("mrn"))
            {
                Problems = ["Enter a name, date of birth, or MRN to search."];
                return;
            }

            (PanelPatients, PanelMore) = await phi.PanelAsync(PanelLimit, HttpContext.RequestAborted);
            return;
        }

        var name = Search.Name?.Trim() is { Length: > 0 } givenName ? givenName : null;
        var mrn = Search.Mrn?.Trim() is { Length: > 0 } givenMrn ? givenMrn : null;
        DateOnly? birthDate = null;
        var problems = new List<string>();
        if (name is { Length: < PhiReader.MinimumNameLength })
        {
            problems.Add($"Type at least {PhiReader.MinimumNameLength} letters of the name.");
        }
        else if (name is { Length: > MaximumNameLength })
        {
            problems.Add($"A name can be at most {MaximumNameLength} characters.");
        }
        else if (name?.Any(char.IsControl) == true)
        {
            // Such as a NUL, which PostgreSQL refuses in text.
            problems.Add("The name has a control character in it. Type it again.");
        }

        if (Search.Birthdate?.Trim() is { Length: > 0 } text)
        {
            if (!DateOnly.TryParseExact(text, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
            {
                problems.Add("Enter the date of birth as a full date, such as 1980-05-02.");
            }
            else if (date > LatestBirthDate)
            {
                problems.Add("That date of birth is in the future. Check the year.");
            }
            else
            {
                birthDate = date;
            }
        }

        if (mrn is not null && !PhiReader.CouldBeMrn(mrn))
        {
            problems.Add(mrn.Length > PhiReader.MaximumMrnLength
                ? $"An MRN has at most {PhiReader.MaximumMrnLength} characters."
                : "The MRN has a control character in it. Type it again.");
        }

        Problems = problems;
        if (problems.Count > 0)
        {
            return;
        }

        var found = await phi.SearchPatientsAsync(name, birthDate, mrn, limit, HttpContext.RequestAborted);
        (Searched, Results, More) = (true, found.Patients, found.More);
        Panel = await assignments.PanelIdsAsync(HttpContext.RequestAborted);
    }
}
