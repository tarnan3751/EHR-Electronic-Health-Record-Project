using System.Security.Cryptography;
using System.Text;
using Ehr.Data.Accounts;
using Ehr.Testing;
using Npgsql;
using Xunit;

namespace Ehr.Security.Tests;

// The expansion plan's step 6c: enrollment codes and proxy invitations, as the runtime roles, in SQL of its own
// (db/policies/10-grants.sql and 45-codes.sql). Both are used before anyone is signed in, so the question is what an
// app session with nobody signed in can get: neither table, at all; and from the functions, the one row a valid,
// unexpired, unused code opens, and nothing for any other. Issuing is held to who may: a clinician for an enrollment
// code, the giver of the access for an invitation.
public class CodeLookupSecurityTests(MigratedDatabase database) : IClassFixture<MigratedDatabase>
{
    string App => database.ConnectionStringFor("ehr_app", MigratedDatabase.AppPassword);

    string Read => database.ConnectionStringFor("ehr_read", MigratedDatabase.ReadPassword);

    static string Hash(string code) => Convert.ToHexStringLower(SHA256.HashData(Encoding.ASCII.GetBytes(code)));

    Task AsSuperuserAsync(string sql, params (string Name, object Value)[] parameters) =>
        Accounts.ExecuteAsync(database.SuperuserConnectionString, null, sql, parameters);

    async Task RefusedAsync(string connection, Guid? user, string sql, params (string Name, object Value)[] parameters) =>
        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege,
            (await Assert.ThrowsAsync<PostgresException>(() => Accounts.ExecuteAsync(connection, user, sql, parameters))).SqlState);

    [Fact]
    public async Task The_app_roles_cannot_read_or_write_either_table_whoever_is_signed_in()
    {
        var clinician = await Accounts.StaffAsync(database, StaffRoles.Clinician);
        var patient = await Accounts.PatientAccountAsync(database, await Accounts.PatientAsync(database, clinician));
        foreach (var table in new[] { "patient_enrollments", "proxy_invitations" })
        {
            foreach (var connection in new[] { App, Read })
            {
                foreach (var user in new Guid?[] { null, clinician, patient })
                {
                    foreach (var sql in new[]
                    {
                        $"SELECT count(*) FROM ehr.{table}",
                        $"SELECT code_hash FROM ehr.{table} LIMIT 1",
                        $"UPDATE ehr.{table} SET redeemed_at = NULL RETURNING 1",
                        $"DELETE FROM ehr.{table} RETURNING 1",
                    })
                    {
                        await RefusedAsync(connection, user, sql);
                    }
                }
            }
        }
    }

    [Fact]
    public async Task An_invitation_is_found_only_by_its_own_unused_unexpired_code_and_redeemed_once()
    {
        var clinician = await Accounts.StaffAsync(database, StaffRoles.Clinician);
        var patient = await Accounts.PatientAsync(database, clinician);
        await Accounts.AssignAsync(database, clinician, patient);
        var grant = Guid.CreateVersion7();
        await Accounts.ExecuteAsync(App, clinician,
            "INSERT INTO ehr.proxy_grants (id, patient_id, proxy_email, scope, include_sensitive, expires_at) VALUES (@id, @patient, @email, 'record', false, now() + interval '30 days')",
            ("id", grant), ("patient", patient), ("email", $"invited-{grant:N}@example.com"));
        var (valid, expired, used) = ($"valid-{grant:N}", $"expired-{grant:N}", $"used-{grant:N}");
        const string insert = "INSERT INTO ehr.proxy_invitations (id, grant_id, email, code_hash, expires_at, redeemed_at) VALUES (gen_random_uuid(), @grant, @email, @hash, now() + @days * interval '1 day', @redeemed)";
        await AsSuperuserAsync(insert, ("grant", grant), ("email", "expired@example.com"), ("hash", Hash(expired)), ("days", -1), ("redeemed", DBNull.Value));
        await AsSuperuserAsync(insert, ("grant", grant), ("email", "used@example.com"), ("hash", Hash(used)), ("days", 7), ("redeemed", DateTime.UtcNow));

        // Issued by the access's giver: the address is the grant's, whatever else there is.
        await Accounts.ExecuteAsync(App, clinician, "SELECT ehr.issue_proxy_invitation(gen_random_uuid(), @grant, @hash)", ("grant", grant), ("hash", Hash(valid)));
        // Not by anyone else, not for a grant that isn't theirs, and not through the read-only role.
        var other = await Accounts.StaffAsync(database, StaffRoles.Clinician);
        foreach (var user in new Guid?[] { other, null })
        {
            await RefusedAsync(App, user, "SELECT ehr.issue_proxy_invitation(gen_random_uuid(), @grant, @hash)", ("grant", grant), ("hash", Hash("someone-elses")));
        }

        await RefusedAsync(Read, clinician, "SELECT ehr.issue_proxy_invitation(gen_random_uuid(), @grant, @hash)", ("grant", grant), ("hash", Hash("read-only")));

        // With nobody signed in, through either role: the one row for the valid code, and nothing for any other.
        const string find = "SELECT email FROM ehr.find_proxy_invitation(@hash)";
        foreach (var connection in new[] { App, Read })
        {
            Assert.Equal([$"invited-{grant:N}@example.com"], await Accounts.QueryAsync(connection, null, find, ("hash", Hash(valid))));
            foreach (var code in new[] { expired, used, "unknown", "" })
            {
                Assert.Empty(await Accounts.QueryAsync(connection, null, find, ("hash", Hash(code))));
            }

            // Not the stored hash's prefix, not a pattern, not NULL: a row answers to its whole hash and nothing else.
            Assert.Empty(await Accounts.QueryAsync(connection, null, find, ("hash", Hash(valid)[..32])));
            Assert.Empty(await Accounts.QueryAsync(connection, null, find, ("hash", "%")));
            Assert.Empty(await Accounts.QueryAsync(connection, null, "SELECT email FROM ehr.find_proxy_invitation(NULL)"));
        }

        // Redeemed once, by the app role only.
        const string redeem = "SELECT email FROM ehr.redeem_proxy_invitation(@hash)";
        await RefusedAsync(Read, null, redeem, ("hash", Hash(valid)));
        foreach (var code in new[] { expired, used, "unknown" })
        {
            Assert.Empty(await Accounts.QueryAsync(App, null, redeem, ("hash", Hash(code))));
        }

        // Accounts.QueryAsync doesn't commit, so the redemption is made in a statement of its own.
        await Accounts.ExecuteAsync(App, null, "SELECT ehr.redeem_proxy_invitation(@hash)", ("hash", Hash(valid)));
        Assert.Empty(await Accounts.QueryAsync(App, null, redeem, ("hash", Hash(valid))));
        Assert.Empty(await Accounts.QueryAsync(App, null, find, ("hash", Hash(valid))));
    }

    [Fact]
    public async Task An_enrollment_code_is_issued_by_a_clinician_found_only_by_its_own_hash_and_redeemed_once()
    {
        var clinician = await Accounts.StaffAsync(database, StaffRoles.Clinician);
        var admin = await Accounts.StaffAsync(database, StaffRoles.Admin);
        var patient = await Accounts.PatientAsync(database, clinician);
        var account = await Accounts.PatientAccountAsync(database, await Accounts.PatientAsync(database, clinician));
        var (keyed, legacy) = (Hash($"keyed-{patient:N}"), Hash($"legacy-{patient:N}"));
        const string issue = "SELECT ehr.issue_patient_enrollment(gen_random_uuid(), @patient, @hash)";

        foreach (var user in new Guid?[] { admin, account, null })
        {
            await RefusedAsync(App, user, issue, ("patient", patient), ("hash", keyed));
        }

        await RefusedAsync(Read, clinician, issue, ("patient", patient), ("hash", keyed));
        await Accounts.ExecuteAsync(App, clinician, issue, ("patient", patient), ("hash", keyed));
        // Who, when and for how long are the database's.
        Assert.Equal([$"{clinician} true true"], await Accounts.QueryAsync(database.SuperuserConnectionString, null,
            "SELECT issued_by || ' ' || (issued_at > now() - interval '1 minute') || ' ' || (expires_at BETWEEN now() + interval '13 days' AND now() + interval '14 days') FROM ehr.patient_enrollments WHERE code_hash = @hash",
            ("hash", keyed)));
        // A code from before codes were keyed, stored as its plain hash: still found, by the second argument.
        var older = await Accounts.PatientAsync(database, clinician);
        await AsSuperuserAsync("INSERT INTO ehr.patient_enrollments (id, patient_id, code_hash, issued_at, expires_at) VALUES (gen_random_uuid(), @patient, @hash, now(), now() + interval '3 days')",
            ("patient", older), ("hash", legacy));
        await AsSuperuserAsync("INSERT INTO ehr.patient_enrollments (id, patient_id, code_hash, issued_at, expires_at) VALUES (gen_random_uuid(), @patient, @hash, now() - interval '20 days', now() - interval '6 days')",
            ("patient", older), ("hash", Hash("expired")));

        // Nothing finds a code without using it up: the function that did is gone, for both runtime roles.
        foreach (var connection in new[] { App, Read })
        {
            Assert.Equal(PostgresErrorCodes.UndefinedFunction, (await Assert.ThrowsAsync<PostgresException>(() => Accounts.QueryAsync(connection, null,
                "SELECT patient_id::text FROM ehr.find_patient_enrollment(@hash, @legacy)", ("hash", keyed), ("legacy", keyed)))).SqlState);
        }

        const string redeem = "SELECT patient_id::text FROM ehr.redeem_patient_enrollment(@hash, @legacy)";
        await RefusedAsync(Read, null, redeem, ("hash", keyed), ("legacy", keyed));
        Assert.Empty(await Accounts.QueryAsync(App, null, redeem, ("hash", Hash("expired")), ("legacy", Hash("expired"))));
        Assert.Empty(await Accounts.QueryAsync(App, null, redeem, ("hash", Hash("unknown")), ("legacy", Hash("unknown"))));
        Assert.Empty(await Accounts.QueryAsync(App, null, "SELECT patient_id::text FROM ehr.redeem_patient_enrollment(NULL, NULL)"));
        await Accounts.ExecuteAsync(App, null, "SELECT ehr.redeem_patient_enrollment(@hash, @legacy)", ("hash", keyed), ("legacy", keyed));
        Assert.Empty(await Accounts.QueryAsync(App, null, redeem, ("hash", keyed), ("legacy", keyed)));
        Assert.Equal([older.ToString()], await Accounts.QueryAsync(App, null, redeem, ("hash", Hash("nothing")), ("legacy", legacy)));
    }
}
