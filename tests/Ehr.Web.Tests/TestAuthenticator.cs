using System.Buffers.Binary;
using System.Buffers.Text;
using System.Formats.Cbor;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Ehr.Web.Tests;

// A passkey authenticator in software, for the server-side tests of step 19: it answers the options the app gives with
// what a browser would send back, as WebAuthn's JSON (toJSON), made and signed as an authenticator makes and signs them:
// an ES256 key, "none" attestation in CTAP2's canonical CBOR (System.Formats.Cbor, part of ASP.NET Core 10), and
// signatures over the authenticator data and the client data's hash. Each answer can be made wrong in one way at a time:
// the person not verified, another origin, a cross-origin frame, another relying party, a count that doesn't go up.
// One authenticator holds one passkey. Chromium's own virtual authenticator serves the browser tests instead.
sealed class TestAuthenticator : IDisposable
{
    const byte UserPresent = 0x01;
    const byte UserVerified = 0x04;
    const byte AttestedCredentialData = 0x40;

    readonly ECDsa key = ECDsa.Create(ECCurve.NamedCurves.nistP256);

    public byte[] CredentialId { get; } = RandomNumberGenerator.GetBytes(16);

    // The count the next assertion carries, one more each time unless a test says otherwise.
    public uint SignCount { get; set; }

    // The account the passkey was made for, as the options named it (the user handle).
    byte[] userHandle = [];

    // The passkey made from the options navigator.credentials.create would have been given.
    public string Create(JsonElement options, string origin, bool verified = true, string? relyingParty = null)
    {
        userHandle = Base64Url.DecodeFromChars(options.GetProperty("user").GetProperty("id").GetString());
        var rpId = relyingParty ?? options.GetProperty("rp").GetProperty("id").GetString()!;
        var clientData = ClientData("webauthn.create", options.GetProperty("challenge").GetString()!, origin, crossOrigin: false);

        var parameters = key.ExportParameters(includePrivateParameters: false);
        var publicKey = new CborWriter(CborConformanceMode.Ctap2Canonical);
        publicKey.WriteStartMap(5);
        publicKey.WriteInt32(1);
        publicKey.WriteInt32(2);
        publicKey.WriteInt32(3);
        publicKey.WriteInt32(-7);
        publicKey.WriteInt32(-1);
        publicKey.WriteInt32(1);
        publicKey.WriteInt32(-2);
        publicKey.WriteByteString(parameters.Q.X!);
        publicKey.WriteInt32(-3);
        publicKey.WriteByteString(parameters.Q.Y!);
        publicKey.WriteEndMap();

        var credentialIdLength = new byte[2];
        BinaryPrimitives.WriteUInt16BigEndian(credentialIdLength, (ushort)CredentialId.Length);
        byte[] authenticatorData =
        [
            .. AuthenticatorData(rpId, (byte)(UserPresent | (verified ? UserVerified : 0) | AttestedCredentialData), SignCount),
            .. new byte[16], .. credentialIdLength, .. CredentialId, .. publicKey.Encode(),
        ];

        var attestation = new CborWriter(CborConformanceMode.Ctap2Canonical);
        attestation.WriteStartMap(3);
        attestation.WriteTextString("fmt");
        attestation.WriteTextString("none");
        attestation.WriteTextString("attStmt");
        attestation.WriteStartMap(0);
        attestation.WriteEndMap();
        attestation.WriteTextString("authData");
        attestation.WriteByteString(authenticatorData);
        attestation.WriteEndMap();

        return Credential(new JsonObject
        {
            ["clientDataJSON"] = Base64Url.EncodeToString(clientData),
            ["attestationObject"] = Base64Url.EncodeToString(attestation.Encode()),
            ["transports"] = new JsonArray("internal"),
        });
    }

    // The passkey used, from the options navigator.credentials.get would have been given.
    public string Get(
        JsonElement options, string origin, bool verified = true, string? relyingParty = null, bool crossOrigin = false, uint? signCount = null)
    {
        var count = signCount ?? ++SignCount;
        var rpId = relyingParty ?? options.GetProperty("rpId").GetString()!;
        var clientData = ClientData("webauthn.get", options.GetProperty("challenge").GetString()!, origin, crossOrigin);
        var authenticatorData = AuthenticatorData(rpId, (byte)(UserPresent | (verified ? UserVerified : 0)), count);
        var signature = key.SignData([.. authenticatorData, .. SHA256.HashData(clientData)], HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence);

        return Credential(new JsonObject
        {
            ["clientDataJSON"] = Base64Url.EncodeToString(clientData),
            ["authenticatorData"] = Base64Url.EncodeToString(authenticatorData),
            ["signature"] = Base64Url.EncodeToString(signature),
            ["userHandle"] = Base64Url.EncodeToString(userHandle),
        });
    }

    public void Dispose() => key.Dispose();

    string Credential(JsonObject response) => new JsonObject
    {
        ["id"] = Base64Url.EncodeToString(CredentialId),
        ["rawId"] = Base64Url.EncodeToString(CredentialId),
        ["type"] = "public-key",
        ["authenticatorAttachment"] = "platform",
        ["response"] = response,
        ["clientExtensionResults"] = new JsonObject(),
    }.ToJsonString();

    static byte[] ClientData(string type, string challenge, string origin, bool crossOrigin) =>
        Encoding.UTF8.GetBytes(new JsonObject
        {
            ["type"] = type,
            ["challenge"] = challenge,
            ["origin"] = origin,
            ["crossOrigin"] = crossOrigin,
        }.ToJsonString());

    static byte[] AuthenticatorData(string relyingParty, byte flags, uint count)
    {
        var counter = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(counter, count);
        return [.. SHA256.HashData(Encoding.UTF8.GetBytes(relyingParty)), flags, .. counter];
    }
}
