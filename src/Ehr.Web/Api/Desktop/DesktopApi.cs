using System.Data;
using System.Security.Claims;
using Ehr.Data;
using Ehr.Data.Patients;
using Ehr.Domain.Accounts;
using Ehr.Domain.Authorization;
using Ehr.Domain.Phi;
using Ehr.Web.Api.Dictation;
using Microsoft.EntityFrameworkCore;

namespace Ehr.Web.Api.Desktop;

// The desktop app's sign-in, sign-out and assigned chart downloads. Sign-in is the one anonymous endpoint, like the sign-in
// pages; everything else under /api/desktop, and the sync API, needs the session token it hands out. The JSON is in
// DesktopApiContract.cs. The cloud app forwards these posts to the on-prem app, which can write (Forwarding/).
//
// Sign-in, POST /api/desktop/sign-in: email, password, the authenticator code and the device's name. 200 with the
// token, until when it works and the account's display name; 401 for a wrong email, password or code, or a locked
// account; 403 for an account that hasn't finished its web setup or isn't a clinician. Each refusal says why.
//
// Sign-out, POST /api/desktop/sign-out with the token: 204, and the token never works again.
public static class DesktopApi
{
    public static IEndpointRouteBuilder MapDesktopApi(this IEndpointRouteBuilder endpoints)
    {
        var desktop = endpoints.MapGroup("/api/desktop").RequireAuthorization(EhrPolicies.Desktop);
        desktop.MapPost("/sign-in", SignInAsync).AllowAnonymous();
        desktop.MapPost("/sign-out", SignOutAsync);
        desktop.MapPost("/offline-panel", OfflinePanelAsync);
        desktop.MapDraftSync();
        desktop.MapOfflineDictation();
        return endpoints;
    }

    static async Task<IResult> SignInAsync(SignInRequest request, DesktopSessions sessions, CancellationToken cancellationToken)
    {
        var missing = new Dictionary<string, string[]>();
        Require(missing, "email", request.Email);
        Require(missing, "password", request.Password);
        Require(missing, "code", request.Code);
        if (missing.Count > 0)
        {
            return Results.ValidationProblem(missing);
        }

        var device = string.IsNullOrWhiteSpace(request.Device) ? "desktop" : request.Device.Trim();
        var signIn = await sessions.SignInAsync(request.Email!, request.Password!, request.Code!, device, cancellationToken);
        return signIn.Outcome switch
        {
            DesktopSignInOutcome.SignedIn => Results.Ok(new SignInResponse(
                signIn.Token!, signIn.ExpiresAt!.Value, signIn.DisplayName!,
                new OfflineAccess(signIn.ClinicianId!.Value, signIn.ExpiresAt!.Value.ToUnixTimeSeconds()))),
            DesktopSignInOutcome.LockedOut => Results.Json(
                new Refusal("This account is locked after too many failed attempts. Try again in 15 minutes."), statusCode: StatusCodes.Status401Unauthorized),
            DesktopSignInOutcome.NotSetUp => Results.Json(
                new Refusal("Finish setting up your account in the web app first."), statusCode: StatusCodes.Status403Forbidden),
            DesktopSignInOutcome.NotAClinician => Results.Json(
                new Refusal("This account can't use the desktop app."), statusCode: StatusCodes.Status403Forbidden),
            _ => Results.Json(new Refusal("Wrong email, password or code."), statusCode: StatusCodes.Status401Unauthorized),
        };
    }

    // POST so the cloud forwards this to the primary, where the chart reads are audited.
    static async Task<IResult> OfflinePanelAsync(
        EhrDbContext db, PhiReader phi, UserContext user, HttpContext context, Ehr.Domain.Time.ClinicTime clinic, CancellationToken cancellationToken)
    {
        // The Desktop policy authenticates the bearer scheme after UserContextMiddleware.
        if (!Guid.TryParse(context.User.FindFirstValue(ClaimTypes.NameIdentifier), out var clinicianId))
        {
            return Results.StatusCode(StatusCodes.Status403Forbidden);
        }

        user.UserId = clinicianId;
        context.Response.Headers.CacheControl = "no-store";
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead, cancellationToken);
        var charts = await phi.OfflinePanelAsync(limit: 25, visits: 5, cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        return Results.Ok(charts.Select(chart => new CachedChart(
            chart.Patient.Id, $"{chart.Patient.GivenName} {chart.Patient.FamilyName}", chart.Patient.Mrn,
            chart.Patient.BirthDate, chart.Patient.Gender,
            chart.Allergies.Where(a => a.Code != AllergyIntolerance.NoKnownAllergy).Select(a => new CachedAllergy(a.Display, a.Reaction, a.Criticality)).ToList(),
            chart.Medications.Select(m => new CachedMedication(m.Display, m.Dosage)).ToList(),
            chart.Conditions.Select(c => new CachedCondition(c.Display, c.OnsetDate)).ToList(),
            // The contract is unchanged: the vital signs are read from the observations taken at the visit (step 9).
            chart.Visits.Select(v => new CachedVisit(v.PeriodStart, v.Reason, Whole(chart.VisitVitals.GetValueOrDefault(v.Id)?.Systolic),
                Whole(chart.VisitVitals.GetValueOrDefault(v.Id)?.Diastolic), Whole(chart.VisitVitals.GetValueOrDefault(v.Id)?.HeartRate))).ToList(),
            // The offline copy never holds sensitive items: the chart read left them out, and says how many.
            chart.SensitiveHidden + chart.SensitiveNotesHidden,
            // "No known allergies" is its own state, not an allergy in the list.
            chart.Allergies.Any(a => a.Code != AllergyIntolerance.NoKnownAllergy) ? "listed" : chart.Allergies.Count > 0 ? "none-known" : "not-recorded",
            // Signed notes and their addenda (step 10); sensitive ones were left out by the read, and are counted above.
            chart.OfflineNotes.Select(n => new CachedNote(n.Note.SignedAt, n.SignerName, n.Note.History, n.Note.Assessment, n.Note.Plan,
                n.Addenda.Select(a => new CachedAddendum(a.SignedAt, a.AuthorName, a.Text, !a.ByNoteAuthor)).ToList(), n.SensitiveAddendaNotKept)).ToList(),
            // The zone the desktop shows these times in (step 10d).
            clinic.Id)).ToList());
    }

    // A vital sign as the desktop app's contract carries it: a whole number.
    static short? Whole(decimal? value) => value is { } v ? (short)Math.Round(v) : null;

    static async Task<IResult> SignOutAsync(ClaimsPrincipal user, DesktopSessions sessions, CancellationToken cancellationToken)
    {
        // The policy has just authenticated the token, so its session is in the principal.
        await sessions.SignOutAsync(Guid.Parse(user.FindFirstValue(DesktopTokenHandler.SessionClaim)!), cancellationToken);
        return Results.NoContent();
    }

    static void Require(Dictionary<string, string[]> missing, string field, string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            missing[field] = [$"Enter the {field}."];
        }
    }
}
