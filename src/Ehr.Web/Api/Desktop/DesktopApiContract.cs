namespace Ehr.Web.Api.Desktop;

// The JSON that DesktopApi receives and sends (camelCase on the wire).

// Code is the 6-digit code from the authenticator app, or a recovery code. Device is a short name for the computer.
public sealed record SignInRequest(string? Email, string? Password, string? Code, string? Device);

// Token is what the app keeps, and sends as "Authorization: Bearer <token>"; it is never shown again.
public sealed record SignInResponse(string Token, DateTimeOffset ExpiresAt, string DisplayName, OfflineAccess OfflineAccess);

// Why a sign-in was refused, in words for the person.
public sealed record Refusal(string Problem);

public sealed record OfflineAccess(Guid ClinicianId, long ExpiresUnixSeconds);

public sealed record CachedChart(
    Guid Id, string Name, string Mrn, DateOnly BirthDate, string Gender,
    IReadOnlyList<CachedAllergy> Allergies, IReadOnlyList<CachedMedication> Medications,
    IReadOnlyList<CachedCondition> Conditions, IReadOnlyList<CachedVisit> Visits,
    // How many sensitive diagnoses, medications and signed notes the chart has that aren't in this copy: they are
    // never kept offline.
    int SensitiveNotKept = 0,
    // What the allergy list is: "listed" (Allergies holds them), "none-known" (asked, and none) or "not-recorded".
    string AllergyStatus = "listed",
    // The latest signed notes that aren't sensitive, newest first, each with its addenda in order (step 10).
    IReadOnlyList<CachedNote>? Notes = null,
    // The clinic's time zone, an IANA name (step 10d): the desktop shows this copy's times in it, labeled.
    string? ClinicTimeZone = null);

// History: the note's History as signed or, for a note signed in Subjective, Objective, Assessment and Plan (step 14),
// its Subjective and Objective, each under its heading, so a build that knows only History shows all of it.
// SensitiveAddendaNotKept: how many sensitive addenda the note has that aren't in this copy.
// Subjective and Objective: a note signed in four sections has them, which a build that knows them shows in place of
// History (step 14); null for a note signed before then.
public sealed record CachedNote(
    DateTimeOffset SignedAt, string Signer, string History, string Assessment, string Plan, IReadOnlyList<CachedAddendum> Addenda,
    int SensitiveAddendaNotKept = 0, string? Subjective = null, string? Objective = null);

// NotNoteAuthor: written by someone other than the note's signer (step 10b); the desktop labels it so.
public sealed record CachedAddendum(DateTimeOffset SignedAt, string Author, string Text, bool NotNoteAuthor = false);

public sealed record CachedAllergy(string Display, string? Reaction, string? Criticality);

public sealed record CachedMedication(string Display, string? Dosage);

public sealed record CachedCondition(string Display, DateOnly? OnsetDate);

public sealed record CachedVisit(DateTimeOffset PeriodStart, string? Reason, short? SystolicMmHg, short? DiastolicMmHg, short? HeartRatePerMinute);
