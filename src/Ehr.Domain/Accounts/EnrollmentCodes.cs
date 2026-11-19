using System.Security.Cryptography;
using System.Text;

namespace Ehr.Domain.Accounts;

// The one-time codes that let a patient register for the portal: made at the clinic, shown once, and kept only as a
// hash (PatientEnrollment). Twelve symbols from an alphabet of 32 without the ones people confuse, so 60 bits of
// randomness: short enough to type, which makes guessing the risk. Online, tries are limited per client address
// (AccountRateLimits). Offline, a copy of the table could be searched for 60-bit codes if they were hashed plainly,
// so the stored hash is keyed (CodeHasher): without the key, which only the app has, the table gives nothing away.
public static class EnrollmentCodes
{
    const string Alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";

    public const int Length = 12;

    public static string Generate() => RandomNumberGenerator.GetString(Alphabet, Length);

    // As shown and typed: groups of four.
    public static string Format(string code) => $"{code[..4]}-{code[4..8]}-{code[8..]}";

    // The plain SHA-256 of a code. For enrollment codes this is the form stored before they were keyed (step 6c):
    // rows that old are still accepted until they expire. Invitation codes are 160 random bits and are stored this
    // way. Case, spaces and dashes don't matter when a code is typed in.
    public static string Hash(string code) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.ASCII.GetBytes(Normalize(code))));

    internal static string Normalize(string code) => new string([.. code.Where(char.IsAsciiLetterOrDigit)]).ToUpperInvariant();
}

// Ehr:Enrollment: the key enrollment codes are hashed with (HMAC-SHA-256). From configuration, never the repository;
// at least 32 characters. Required outside Development, where the app refuses to start without it.
public sealed class CodeHasher
{
    public const string Setting = "Ehr:Enrollment:Key";

    public const int MinimumKeyLength = 32;

    readonly byte[] key;

    public CodeHasher(string key)
    {
        if (key.Length < MinimumKeyLength)
        {
            throw new ArgumentException($"{Setting} must be at least {MinimumKeyLength} characters.", nameof(key));
        }

        this.key = Encoding.UTF8.GetBytes(key);
    }

    // The keyed hash an enrollment code is stored and looked up by.
    public string Enrollment(string code) =>
        Convert.ToHexStringLower(HMACSHA256.HashData(key, Encoding.ASCII.GetBytes(EnrollmentCodes.Normalize(code))));
}
