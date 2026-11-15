using Npgsql;
using NpgsqlTypes;

namespace Ehr.Seed;

// Loads a synthetic clinic into a database that has no patients yet: the local stack's primary, or a PostgreSQL the
// benchmark starts itself. Fast, with binary COPY, and all or nothing, in one transaction.
//
// It connects as ehr_owner, the role that owns the tables and runs migrations (AGENTS.md); nothing else can. The apps'
// roles may not write these tables, and the superuser can't log in over the network. Row-level security is forced, so
// it binds the owner too, and PostgreSQL refuses COPY into a table under it. So, inside the transaction, the seed lifts
// FORCE and turns the write-audit trigger off on each table it fills, loads them, and turns both back on before it
// commits. A transaction's DDL is all or nothing and holds each table locked until it ends, so no other session ever
// sees a table without them, and a failure part-way rolls everything back. The seeded rows therefore have no write-audit
// events, a decision taken for synthetic data in an empty clinic. After committing, the seed checks that FORCE and every
// trigger are back, and fails loudly if not.
public static class Seeder
{
    // The tables the seed fills, in the order it fills them, patients first.
    public static readonly string[] Tables = ["patients", "allergy_intolerances", "medication_statements", "conditions", "encounters"];

    // Loads the clinic, and returns how many rows went into each table.
    public static async Task<IReadOnlyDictionary<string, ulong>> LoadAsync(string connectionString, SyntheticClinic clinic, CancellationToken cancellationToken)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        var rows = new Dictionary<string, ulong>();

        await using (var transaction = await connection.BeginTransactionAsync(cancellationToken))
        {
            foreach (var table in Tables)
            {
                await ExecuteAsync(connection, $"ALTER TABLE ehr.{table} NO FORCE ROW LEVEL SECURITY; ALTER TABLE ehr.{table} DISABLE TRIGGER {table}_audit", cancellationToken);
            }

            // Only now does the owner see every row.
            await using (var any = new NpgsqlCommand("SELECT EXISTS (SELECT FROM ehr.patients)", connection))
            {
                if ((bool)(await any.ExecuteScalarAsync(cancellationToken))!)
                {
                    throw new InvalidOperationException("The clinic already has patients, and the seed only fills an empty one. Nothing was changed.");
                }
            }

            rows["patients"] = await CopyAsync(connection,
                "COPY ehr.patients (id, mrn, given_name, family_name, birth_date, gender, version, updated_at) FROM STDIN (FORMAT BINARY)",
                clinic.Charts().Select(chart => chart.Patient),
                async (row, p, ct) =>
                {
                    await row.WriteAsync(p.Id, NpgsqlDbType.Uuid, ct);
                    await row.WriteAsync(p.Mrn, NpgsqlDbType.Varchar, ct);
                    await row.WriteAsync(p.GivenName, NpgsqlDbType.Varchar, ct);
                    await row.WriteAsync(p.FamilyName, NpgsqlDbType.Varchar, ct);
                    await row.WriteAsync(p.BirthDate, NpgsqlDbType.Date, ct);
                    await row.WriteAsync(p.Gender, NpgsqlDbType.Varchar, ct);
                    await row.WriteAsync(p.Version, NpgsqlDbType.Integer, ct);
                    await row.WriteAsync(p.UpdatedAt, NpgsqlDbType.TimestampTz, ct);
                },
                cancellationToken);
            rows["allergy_intolerances"] = await CopyAsync(connection,
                "COPY ehr.allergy_intolerances (id, patient_id, code, display, clinical_status, category, criticality, reaction, recorded_at) FROM STDIN (FORMAT BINARY)",
                clinic.Charts().SelectMany(chart => chart.Allergies),
                async (row, a, ct) =>
                {
                    await row.WriteAsync(a.Id, NpgsqlDbType.Uuid, ct);
                    await row.WriteAsync(a.PatientId, NpgsqlDbType.Uuid, ct);
                    await WriteAsync(row, a.Code, ct);
                    await row.WriteAsync(a.Display, NpgsqlDbType.Varchar, ct);
                    await row.WriteAsync(a.ClinicalStatus, NpgsqlDbType.Varchar, ct);
                    await WriteAsync(row, a.Category, ct);
                    await WriteAsync(row, a.Criticality, ct);
                    await WriteAsync(row, a.Reaction, ct);
                    await row.WriteAsync(a.RecordedAt, NpgsqlDbType.TimestampTz, ct);
                },
                cancellationToken);
            rows["medication_statements"] = await CopyAsync(connection,
                "COPY ehr.medication_statements (id, patient_id, display, dosage, status, effective_start, effective_end) FROM STDIN (FORMAT BINARY)",
                clinic.Charts().SelectMany(chart => chart.Medications),
                async (row, m, ct) =>
                {
                    await row.WriteAsync(m.Id, NpgsqlDbType.Uuid, ct);
                    await row.WriteAsync(m.PatientId, NpgsqlDbType.Uuid, ct);
                    await row.WriteAsync(m.Display, NpgsqlDbType.Varchar, ct);
                    await WriteAsync(row, m.Dosage, ct);
                    await row.WriteAsync(m.Status, NpgsqlDbType.Varchar, ct);
                    await WriteAsync(row, m.EffectiveStart, NpgsqlDbType.Date, ct);
                    await WriteAsync(row, m.EffectiveEnd, NpgsqlDbType.Date, ct);
                },
                cancellationToken);
            rows["conditions"] = await CopyAsync(connection,
                "COPY ehr.conditions (id, patient_id, code, display, clinical_status, onset_date) FROM STDIN (FORMAT BINARY)",
                clinic.Charts().SelectMany(chart => chart.Conditions),
                async (row, c, ct) =>
                {
                    await row.WriteAsync(c.Id, NpgsqlDbType.Uuid, ct);
                    await row.WriteAsync(c.PatientId, NpgsqlDbType.Uuid, ct);
                    await WriteAsync(row, c.Code, ct);
                    await row.WriteAsync(c.Display, NpgsqlDbType.Varchar, ct);
                    await row.WriteAsync(c.ClinicalStatus, NpgsqlDbType.Varchar, ct);
                    await WriteAsync(row, c.OnsetDate, NpgsqlDbType.Date, ct);
                },
                cancellationToken);
            rows["encounters"] = await CopyAsync(connection,
                """
                COPY ehr.encounters (id, patient_id, status, class_code, reason, period_start, period_end, systolic_mm_hg, diastolic_mm_hg,
                    heart_rate_per_minute) FROM STDIN (FORMAT BINARY)
                """,
                clinic.Charts().SelectMany(chart => chart.Visits),
                async (row, e, ct) =>
                {
                    await row.WriteAsync(e.Id, NpgsqlDbType.Uuid, ct);
                    await row.WriteAsync(e.PatientId, NpgsqlDbType.Uuid, ct);
                    await row.WriteAsync(e.Status, NpgsqlDbType.Varchar, ct);
                    await row.WriteAsync(e.ClassCode, NpgsqlDbType.Varchar, ct);
                    await WriteAsync(row, e.Reason, ct);
                    await row.WriteAsync(e.PeriodStart, NpgsqlDbType.TimestampTz, ct);
                    await WriteAsync(row, e.PeriodEnd, NpgsqlDbType.TimestampTz, ct);
                    await WriteAsync(row, e.SystolicMmHg, NpgsqlDbType.Smallint, ct);
                    await WriteAsync(row, e.DiastolicMmHg, NpgsqlDbType.Smallint, ct);
                    await WriteAsync(row, e.HeartRatePerMinute, NpgsqlDbType.Smallint, ct);
                },
                cancellationToken);

            foreach (var table in Tables)
            {
                await ExecuteAsync(connection, $"ALTER TABLE ehr.{table} ENABLE TRIGGER {table}_audit; ALTER TABLE ehr.{table} FORCE ROW LEVEL SECURITY", cancellationToken);
            }

            await transaction.CommitAsync(cancellationToken);
        }

        // The planner's statistics, for the searches that follow.
        await ExecuteAsync(connection, $"ANALYZE {string.Join(", ", Tables.Select(table => $"ehr.{table}"))}", cancellationToken);
        await CheckProtectedAsync(connection, cancellationToken);
        return rows;
    }

    // Row-level security enabled and forced, and the write-audit trigger enabled, on every table the seed filled.
    static async Task CheckProtectedAsync(NpgsqlConnection connection, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            """
            SELECT c.relname FROM pg_class c
            JOIN pg_namespace n ON n.oid = c.relnamespace
            LEFT JOIN pg_trigger t ON t.tgrelid = c.oid AND t.tgname = c.relname || '_audit'
            WHERE n.nspname = 'ehr' AND c.relname = ANY(@tables)
              AND c.relrowsecurity AND c.relforcerowsecurity AND t.tgenabled = 'O'
            """,
            connection);
        command.Parameters.AddWithValue("tables", Tables);
        var protectedTables = new List<string>();
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                protectedTables.Add(reader.GetString(0));
            }
        }

        var exposed = Tables.Except(protectedTables).ToList();
        if (exposed.Count > 0)
        {
            throw new InvalidOperationException(
                $"After the seed, these tables lack forced row-level security or an enabled write-audit trigger: {string.Join(", ", exposed)}. "
                + "Run the migration job (infra/migrate) to apply db/policies again.");
        }
    }

    static async Task<ulong> CopyAsync<T>(
        NpgsqlConnection connection, string copy, IEnumerable<T> items, Func<NpgsqlBinaryImporter, T, CancellationToken, Task> write,
        CancellationToken cancellationToken)
    {
        await using var importer = await connection.BeginBinaryImportAsync(copy, cancellationToken);
        foreach (var item in items)
        {
            await importer.StartRowAsync(cancellationToken);
            await write(importer, item, cancellationToken);
        }

        return await importer.CompleteAsync(cancellationToken);
    }

    static Task WriteAsync(NpgsqlBinaryImporter row, string? value, CancellationToken cancellationToken) =>
        value is null ? row.WriteNullAsync(cancellationToken) : row.WriteAsync(value, NpgsqlDbType.Varchar, cancellationToken);

    static Task WriteAsync<T>(NpgsqlBinaryImporter row, T? value, NpgsqlDbType type, CancellationToken cancellationToken)
        where T : struct =>
        value is { } present ? row.WriteAsync(present, type, cancellationToken) : row.WriteNullAsync(cancellationToken);

    static async Task ExecuteAsync(NpgsqlConnection connection, string sql, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
