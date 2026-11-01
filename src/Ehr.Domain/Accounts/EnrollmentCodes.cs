using System.Security.Cryptography;
using System.Text;

namespace Ehr.Domain.Accounts;

// The one-time codes that let a patient register for the portal: made at the clinic, shown once, and kept only as a
// hash (PatientEnrollment). Twelve symbols from an alphabet of 32 without the ones people confuse, so 60 bits of
// randomness: a plain SHA-256 is enough, since the code can't be guessed and is never reused.
public static class EnrollmentCodes
{
    const string Alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";

    public const int Length = 12;

    public static string Generate() => RandomNumberGenerator.GetString(Alphabet, Length);

    // As shown and typed: groups of four.
    public static string Format(string code) => $"{code[..4]}-{code[4..8]}-{code[8..]}";

    // Case, spaces and dashes don't matter when the code is typed in.
    public static string Hash(string code) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.ASCII.GetBytes(Normalize(code))));

    static string Normalize(string code) => new string([.. code.Where(char.IsAsciiLetterOrDigit)]).ToUpperInvariant();
}
