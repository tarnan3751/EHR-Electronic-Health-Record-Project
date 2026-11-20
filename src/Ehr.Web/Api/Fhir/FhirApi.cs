using System.Text.Json;
using System.Text.Json.Serialization;
using Ehr.Data;
using Ehr.Data.Patients;
using Ehr.Domain.Accounts;
using Ehr.Domain.Patients;
using Ehr.Domain.Phi;
using Microsoft.Extensions.Options;

namespace Ehr.Web.Api.Fhir;

// A read-only FHIR R4 API for clinicians (the expansion plan's step 11, UC 19), at /fhir on both servers: a patient's
// demographics, allergies, medications, diagnoses, test results and vital signs, and appointments, read by ID or
// searched by patient, as FHIR JSON (FhirResources.cs, FhirObservations.cs), and the capability statement that says so
// at /fhir/metadata.
//
// Who: the Clinician policy on the whole group (Program.cs), with the clinical app's own sign-in. The staff cookie is
// the one /fhir reads (AccountSchemes), so UserContextMiddleware hands the clinician to the database as it does for a
// page, and a refusal is a 401 or 403 rather than a redirect to sign in (AccountServiceCollectionExtensions), answered
// with an OperationOutcome (UseFhirErrors). Then the chart's gate, for each patient, before anything about them is read:
// on the clinician's panel, or under emergency access already in force (BreakGlass; the API offers no way to start it).
// A patient who isn't gets 403; an entry the database doesn't give this reader, 404. A search by MRN is the exception:
// it finds nothing for a patient the clinician may not read, exactly as for an MRN that doesn't exist, so it can't be
// used to learn who is a patient here.
//
// Every read goes through the PHI reader, recorded as the page that shows the same thing records it: one read of the
// patient per request, or a read of their sensitive items when show-sensitive=true asked for those (sensitive
// diagnoses and medications are otherwise left out, and a read by ID of one is not found). Each request runs in one
// database transaction (InTransactionAsync), as a page does, so row-level security applies and the read's audit event
// commits before any of the answer is written. Off-site the cloud app reads its copy and its events go to its audit
// queue, written before the response is sent (CloudAuditFlushMiddleware), as a page's are.
//
// Every answer is application/fhir+json, never cached; a client whose Accept header takes no JSON gets 406. Errors are
// OperationOutcomes with fixed text: nothing from the request or the record. A search returns at most SearchLimit
// entries, with the total, each type newest first by its own date (SearchOrder), undated entries last; a parameter the
// capability statement doesn't list is refused, at /fhir/metadata too. Nothing writes: any other method is 405
// (off-site, the cloud app forwards it to the clinic's server, as every write, and its answer comes back).
public static class FhirApi
{
    public const string ContentType = "application/fhir+json; charset=utf-8";

    // The most entries a search returns: the most values the vital signs page and the portal's results read (PhiReader).
    public const int SearchLimit = 600;

    // The order a search returns each type's entries in (PhiReader), as the capability statement says it.
    public const string SearchOrder = "Each search returns the newest first, by its own date: allergies by when they were recorded, medications by "
        + "when they were started, diagnoses by onset, results and vital signs by when they were taken, and appointments by their start; "
        + "entries with no date come last.";

    // Includes sensitive diagnoses and medications, which are otherwise left out (Tahseen's name for it: in FHIR a
    // parameter named "sensitive" would read as a filter).
    public const string ShowSensitive = "show-sensitive";

    // What is served: each resource type, the table its entries are in, and the parameters its search and its read take
    // (beyond _format). Requests are checked against this, and the capability statement is written from it.
    internal static readonly IReadOnlyDictionary<string, (string Table, string[] Search, string[] Read)> Types =
        new Dictionary<string, (string Table, string[] Search, string[] Read)>(StringComparer.Ordinal)
        {
            ["Patient"] = (PhiReader.PatientsTable, ["identifier"], []),
            ["AllergyIntolerance"] = ("allergy_intolerances", ["patient"], []),
            ["MedicationStatement"] = ("medication_statements", ["patient", ShowSensitive], [ShowSensitive]),
            ["Condition"] = ("conditions", ["patient", ShowSensitive], [ShowSensitive]),
            ["Observation"] = ("observations", ["patient", "category"], []),
            ["Appointment"] = ("appointments", ["patient"], []),
        };

    static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };

    public static RouteGroupBuilder MapFhirApi(this IEndpointRouteBuilder endpoints)
    {
        var fhir = endpoints.MapGroup(AccountSchemes.FhirPath);
        fhir.AddEndpointFilter(JsonAcceptedAsync);
        fhir.MapGet("/metadata", (HttpContext context, IOptions<SiteAddresses> addresses) =>
            Unsupported(context.Request.Query, []) ?? Answer(StatusCodes.Status200OK, FhirResources.Capabilities(BaseUrl(addresses))));
        fhir.MapGet("/{type}", SearchAsync).AddEndpointFilter(InTransactionAsync);
        fhir.MapGet("/{type}/{id}", ReadAsync).AddEndpointFilter(InTransactionAsync);
        // Everything else under /fhir: any method but GET is 405, so every write is refused, and any other GET is 404.
        fhir.Map("/{**rest}", (HttpContext context) => HttpMethods.IsGet(context.Request.Method) ? Refusal(StatusCodes.Status404NotFound) : Refusal(StatusCodes.Status405MethodNotAllowed));
        return fhir;
    }

    // Every refusal and failure under /fhir is an OperationOutcome, never a page or an empty answer. A refusal with no
    // body (not signed in, not a clinician) gets one here; a failure gets a 500 that says nothing about it (the exception
    // is logged, as any is), the cloud app's failure to write the request's audit events included, after which none of
    // the answer is sent. Program.cs puts this after the pages' error handling, which leaves /fhir alone (ErrorPages).
    public static IApplicationBuilder UseFhirErrors(this IApplicationBuilder app) =>
        app.UseWhen(context => context.Request.Path.StartsWithSegments(AccountSchemes.FhirPath), fhir =>
        {
            fhir.UseExceptionHandler(new ExceptionHandlerOptions
            {
                ExceptionHandler = context => WriteAsync(context, StatusCodes.Status500InternalServerError, Outcome(StatusCodes.Status500InternalServerError)),
            });
            fhir.UseStatusCodePages(context =>
            {
                var status = context.HttpContext.Response.StatusCode;
                return WriteAsync(context.HttpContext, status, Outcome(status));
            });
        });

    // GET /fhir/{type}?...: a searchset Bundle.
    static async Task<IResult> SearchAsync(
        string type, HttpContext context, PhiReader phi, PatientAssignments assignments, BreakGlass breakGlass, IOptions<SiteAddresses> addresses)
    {
        var cancel = context.RequestAborted;
        var query = context.Request.Query;
        if (!Types.TryGetValue(type, out var served))
        {
            return Refusal(StatusCodes.Status404NotFound, "not-supported", UnknownType);
        }

        if (Unsupported(query, served.Search) is { } unsupported)
        {
            return unsupported;
        }

        var search = new Searchset(BaseUrl(addresses), type);
        if (type == "Patient")
        {
            if (!query.ContainsKey("identifier"))
            {
                return Refusal(StatusCodes.Status400BadRequest, "required", "A search for patients needs identifier: the MRN.");
            }

            // The MRN, alone or with the clinic's system. With another system it is no identifier of this clinic's. A value
            // that can't be an MRN (the patient search page's rule) is refused before the database is asked.
            var identifier = query["identifier"].ToString();
            var mrn = identifier.Split('|') switch
            {
                [var value] => value,
                [FhirResources.MrnSystem, var value] => value,
                [_, { Length: > 0 }] => null,
                _ => "",
            };
            if (mrn is "" || (mrn is not null && !PhiReader.CouldBeMrn(mrn)))
            {
                return Refusal(StatusCodes.Status400BadRequest, "invalid", "identifier is the MRN, alone or as system|MRN.");
            }

            // A patient the clinician may not read is not found, as an MRN that is nobody's isn't: the two answers are the
            // same, so the search can't tell anyone whether a patient off their panel exists. Neither read anything about
            // a patient, so neither is recorded.
            search.Parameter("identifier", identifier);
            if (mrn is null || await phi.PatientIdByMrnAsync(mrn, cancel) is not { } found || !await MayReadAsync(found, assignments, breakGlass, cancel))
            {
                return search.Answer([], 0);
            }

            return await phi.PatientAsync(found, cancel) is { } patient
                ? search.Answer([(found, FhirResources.Patient(patient))], 1)
                : search.Answer([], 0);
        }

        if (!query.ContainsKey("patient"))
        {
            return Refusal(StatusCodes.Status400BadRequest, "required", "A search needs patient: the patient's ID, or Patient/ID.");
        }

        if (!TryPatient(query["patient"].ToString(), out var patientId))
        {
            return Refusal(StatusCodes.Status400BadRequest, "invalid", "patient is the patient's ID, or Patient/ID.");
        }

        search.Parameter("patient", patientId.ToString());
        string? category = null;
        if (query.ContainsKey("category"))
        {
            category = query["category"].ToString() switch
            {
                ObservationCategory.Laboratory or ObservationCategoryCode + "|" + ObservationCategory.Laboratory => ObservationCategory.Laboratory,
                ObservationCategory.VitalSigns or ObservationCategoryCode + "|" + ObservationCategory.VitalSigns => ObservationCategory.VitalSigns,
                _ => null,
            };
            if (category is null)
            {
                return Refusal(StatusCodes.Status400BadRequest, "invalid", "category is laboratory or vital-signs.");
            }

            search.Parameter("category", category);
        }

        if (!TrySensitive(query, out var sensitive))
        {
            return Refusal(StatusCodes.Status400BadRequest, "invalid", $"{ShowSensitive} is true or false.");
        }

        if (sensitive)
        {
            search.Parameter(ShowSensitive, "true");
        }

        if (!await MayReadAsync(patientId, assignments, breakGlass, cancel))
        {
            return Refusal(StatusCodes.Status403Forbidden, "forbidden", OffPanel);
        }

        switch (type)
        {
            case "AllergyIntolerance":
                var allergies = await phi.AllergiesAsync(patientId, only: null, SearchLimit, cancel);
                return search.Answer(allergies.Entries.Select(a => (a.Id, (object)FhirResources.Allergy(a))), allergies.Total);
            case "MedicationStatement":
                var medications = await phi.MedicationsAsync(patientId, only: null, sensitive, SearchLimit, cancel);
                return search.LeftOut(medications.SensitiveLeftOut)
                    .Answer(medications.Entries.Select(m => (m.Id, (object)FhirResources.Medication(m))), medications.Total);
            case "Condition":
                var conditions = await phi.ConditionsAsync(patientId, only: null, sensitive, SearchLimit, cancel);
                return search.LeftOut(conditions.SensitiveLeftOut)
                    .Answer(conditions.Entries.Select(c => (c.Id, (object)FhirResources.Condition(c))), conditions.Total);
            case "Observation":
                // A blood pressure's ID is its reading's, not one of its values' (FhirObservations).
                var observations = await phi.ObservationsAsync(patientId, only: null, category, SearchLimit, cancel);
                return search.Answer(FhirObservations.Map(observations.Entries).Select(mapped => (mapped.Id, (object)mapped.Resource)), observations.Total);
            default:
                var appointments = await phi.AllAppointmentsAsync(patientId, only: null, SearchLimit, cancel);
                return search.Answer(appointments.Entries.Select(a => (a.Id, (object)FhirResources.Appointment(a))), appointments.Total);
        }
    }

    // GET /fhir/{type}/{id}: the resource. The gate needs the patient: for anything but a patient, the reader says whose
    // entry it is, and nothing else, first.
    static async Task<IResult> ReadAsync(
        string type, string id, HttpContext context, PhiReader phi, PatientAssignments assignments, BreakGlass breakGlass)
    {
        var cancel = context.RequestAborted;
        if (!Types.TryGetValue(type, out var served))
        {
            return Refusal(StatusCodes.Status404NotFound, "not-supported", UnknownType);
        }

        if (Unsupported(context.Request.Query, served.Read) is { } unsupported)
        {
            return unsupported;
        }

        if (!TrySensitive(context.Request.Query, out var sensitive))
        {
            return Refusal(StatusCodes.Status400BadRequest, "invalid", $"{ShowSensitive} is true or false.");
        }

        if (!Guid.TryParseExact(id, "D", out var key))
        {
            return Refusal(StatusCodes.Status404NotFound);
        }

        // A patient is gated before anything is read, as the chart is: whether or not there is one with this ID.
        var patientId = type == "Patient" ? key : await phi.EntryPatientAsync(served.Table, key, sensitive, cancel);
        if (patientId is not { } patient)
        {
            return Refusal(StatusCodes.Status404NotFound);
        }

        if (!await MayReadAsync(patient, assignments, breakGlass, cancel))
        {
            return Refusal(StatusCodes.Status403Forbidden, "forbidden", OffPanel);
        }

        object? resource = type switch
        {
            "Patient" => await phi.PatientAsync(key, cancel) is { } found ? FhirResources.Patient(found) : null,
            "AllergyIntolerance" => (await phi.AllergiesAsync(patient, key, 1, cancel)).Entries.Select(FhirResources.Allergy).SingleOrDefault(),
            "MedicationStatement" => (await phi.MedicationsAsync(patient, key, sensitive, 1, cancel)).Entries.Select(FhirResources.Medication).SingleOrDefault(),
            "Condition" => (await phi.ConditionsAsync(patient, key, sensitive, 1, cancel)).Entries.Select(FhirResources.Condition).SingleOrDefault(),
            "Observation" => FhirObservations.Map((await phi.ObservationsAsync(patient, key, null, 1, cancel)).Entries).Select(mapped => mapped.Resource).SingleOrDefault(),
            _ => (await phi.AllAppointmentsAsync(patient, key, 1, cancel)).Entries.Select(FhirResources.Appointment).SingleOrDefault(),
        };
        return resource is null ? Refusal(StatusCodes.Status404NotFound) : Answer(StatusCodes.Status200OK, resource);
    }

    // The chart's gate (Patients/Chart.cshtml.cs): the patient on the clinician's panel, or emergency access in force.
    static async Task<bool> MayReadAsync(Guid patient, PatientAssignments assignments, BreakGlass breakGlass, CancellationToken cancellationToken) =>
        await assignments.IsOnPanelAsync(patient, cancellationToken) || await breakGlass.ActiveUntilAsync(patient, cancellationToken) is not null;

    // FHIR JSON or nothing: a client whose Accept header takes none of these gets 406, before anything is read. _format,
    // when given, decides instead, as FHIR has it, and Unsupported takes only JSON there.
    static async ValueTask<object?> JsonAcceptedAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var request = context.HttpContext.Request;
        var accepted = request.GetTypedHeaders().Accept;
        return request.Query.ContainsKey("_format") || accepted.Count == 0
            || accepted.Any(type => type.Quality is not 0 && JsonTypes.Contains(type.MediaType.Value, StringComparer.OrdinalIgnoreCase))
            ? await next(context)
            : Refusal(StatusCodes.Status406NotAcceptable, "not-supported", "This API answers in JSON only, as application/fhir+json.");
    }

    static readonly string[] JsonTypes = ["*/*", "application/*", "application/json", "application/fhir+json"];

    // One database transaction for the request, as DatabaseTransactionFilter gives a page (which doesn't run here):
    // UserContextInterceptor makes the clinician its first statement, for row-level security, and the read's audit
    // event commits with it, before the answer is written.
    static async ValueTask<object?> InTransactionAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var db = context.HttpContext.RequestServices.GetRequiredService<EhrDbContext>();
        await using var transaction = await db.Database.BeginTransactionAsync(context.HttpContext.RequestAborted);
        var result = await next(context);
        await transaction.CommitAsync(CancellationToken.None);
        return result;
    }

    // The first parameter this request has that the type doesn't take, or given more than once, as a refusal; null
    // when there is none. _format is taken everywhere, as long as it asks for JSON.
    static IResult? Unsupported(IQueryCollection query, string[] taken)
    {
        foreach (var (name, values) in query)
        {
            if (values.Count != 1)
            {
                return Refusal(StatusCodes.Status400BadRequest, "invalid", "Each parameter is given once.");
            }

            if (name == "_format")
            {
                if (!JsonFormats.Contains(values.ToString()))
                {
                    return Refusal(StatusCodes.Status400BadRequest, "not-supported", "Only JSON is served.");
                }
            }
            else if (!taken.Contains(name, StringComparer.Ordinal))
            {
                return Refusal(StatusCodes.Status400BadRequest, "not-supported",
                    "This request has a parameter that isn't supported here. The capability statement, at /fhir/metadata, lists those that are.");
            }
        }

        return null;
    }

    // The ways a client asks for JSON in _format; a + in a query string arrives as a space.
    static readonly string[] JsonFormats = ["json", "application/json", "application/fhir+json", "application/fhir json"];

    static bool TryPatient(string value, out Guid patient) =>
        Guid.TryParseExact(value.StartsWith("Patient/", StringComparison.Ordinal) ? value["Patient/".Length..] : value, "D", out patient);

    // show-sensitive, when given, is true or false.
    static bool TrySensitive(IQueryCollection query, out bool sensitive)
    {
        var value = query.TryGetValue(ShowSensitive, out var given) ? given.ToString() : "false";
        sensitive = value == "true";
        return value is "true" or "false";
    }

    const string ObservationCategoryCode = "http://terminology.hl7.org/CodeSystem/observation-category";

    const string UnknownType = "This server doesn't serve that resource type. The capability statement, at /fhir/metadata, lists those it does.";

    const string OffPanel = "This patient isn't on your panel. Open their chart in the clinical app, where emergency access can be started.";

    static string BaseUrl(IOptions<SiteAddresses> addresses) => Ehr.Web.Accounts.Links.Build(addresses.Value.ClinicalBaseUrl, AccountSchemes.FhirPath);

    // The outcome a status gets when nothing more particular is said: fixed text, about nothing in the request.
    static FhirOperationOutcome Outcome(int status) => status switch
    {
        StatusCodes.Status401Unauthorized => Outcome("error", "login", "Sign in to the clinical app first: this API uses its sign-in."),
        StatusCodes.Status403Forbidden => Outcome("error", "forbidden", "This API is for clinicians."),
        StatusCodes.Status404NotFound => Outcome("error", "not-found", "Nothing was found here."),
        StatusCodes.Status405MethodNotAllowed => Outcome("error", "not-supported", "This API is read-only: it answers GET and nothing else."),
        < 500 => Outcome("error", "invalid", "The request couldn't be answered."),
        _ => Outcome("error", "exception", "The request couldn't be completed. Try again in a moment."),
    };

    static FhirOperationOutcome Outcome(string severity, string code, string text) => new([new Issue(severity, code, text)]);

    static IResult Refusal(int status) => Answer(status, Outcome(status));

    static IResult Refusal(int status, string code, string text) => Answer(status, Outcome("error", code, text));

    static IResult Answer(int status, object resource) => new FhirAnswer(status, resource);

    // Writes a resource as the API's every answer is written.
    static async Task WriteAsync(HttpContext context, int status, object resource)
    {
        context.Response.StatusCode = status;
        context.Response.ContentType = ContentType;
        context.Response.Headers.CacheControl = "no-store";
        if (status == StatusCodes.Status405MethodNotAllowed)
        {
            context.Response.Headers.Allow = "GET";
        }

        await JsonSerializer.SerializeAsync(context.Response.Body, resource, resource.GetType(), Json, CancellationToken.None);
    }

    sealed class FhirAnswer(int status, object resource) : IResult
    {
        public Task ExecuteAsync(HttpContext httpContext) => WriteAsync(httpContext, status, resource);
    }

    // A searchset Bundle as it is built: the parameters used, for its self link, and what the search says beside its
    // entries (OperationOutcome entries, as FHIR has a search say it). Each entry's address is built from the clinical
    // app's configured address, never from the request (SiteAddresses).
    sealed class Searchset(string baseUrl, string type)
    {
        readonly List<string> parameters = [];

        readonly List<(string Severity, string Text)> notes = [];

        public void Parameter(string name, string value) => parameters.Add($"{name}={Uri.EscapeDataString(value)}");

        public void Note(string text, string severity = "information") => notes.Add((severity, text));

        // What sensitive items were left out, as the chart says it.
        public Searchset LeftOut(int sensitive)
        {
            if (sensitive > 0)
            {
                Note($"{sensitive} sensitive {(sensitive == 1 ? "entry was" : "entries were")} left out. Add {ShowSensitive}=true to include them; asking is recorded.");
            }

            return this;
        }

        public IResult Answer(IEnumerable<(Guid Id, object Resource)> matches, int total)
        {
            var entries = matches.Select(match => new BundleEntry($"{baseUrl}/{type}/{match.Id}", match.Resource, new BundleSearch("match"))).ToList();
            if (total > entries.Count)
            {
                Note($"Only the first {entries.Count} of the {total} matches, the newest first, are included: this server has no paging.", "warning");
            }

            entries.AddRange(notes.Select(note => new BundleEntry(
                $"urn:uuid:{Guid.NewGuid()}", Outcome(note.Severity, "informational", note.Text), new BundleSearch("outcome"))));
            var self = $"{baseUrl}/{type}" + (parameters.Count > 0 ? "?" + string.Join("&", parameters) : "");
            return FhirApi.Answer(StatusCodes.Status200OK, new FhirBundle("searchset", total, [new BundleLink("self", self)], entries.Count > 0 ? entries : null));
        }
    }
}
