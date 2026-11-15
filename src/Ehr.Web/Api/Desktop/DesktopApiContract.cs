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
    IReadOnlyList<CachedCondition> Conditions, IReadOnlyList<CachedVisit> Visits);

public sealed record CachedAllergy(string Display, string? Reaction, string? Criticality);

public sealed record CachedMedication(string Display, string? Dosage);

public sealed record CachedCondition(string Display, DateOnly? OnsetDate);

public sealed record CachedVisit(DateTimeOffset PeriodStart, string? Reason, short? SystolicMmHg, short? DiastolicMmHg, short? HeartRatePerMinute);
