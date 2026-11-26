using Npgsql;

namespace Ehr.Testing;

// Notes and drafts as they were written before the expansion plan's step 14, in History, Assessment and Plan: what the
// SoapNotes migration leaves as it found them. The database's rules no longer write one, so these go in as the
// superuser with every trigger held (session_replication_role), which also records no audit event and no change for the
// desktop's sync. For tests only; synthetic text only.
public static class EarlierNotes
{
    // A draft saved by the editor before step 14: structured, at version 1, with its History.
    public static async Task<Guid> DraftAsync(MigratedDatabase database, Guid clinician, Guid patient, string history,
        string assessment = "Synthetic assessment: earlier.", string plan = "Synthetic plan: earlier.")
    {
        var draft = Guid.CreateVersion7();
        await InsertAsync(database, """
            INSERT INTO ehr.clinical_drafts (id, patient_id, clinician_id, body, version, history, assessment, plan, structured_version, has_unreviewed_proposals)
            VALUES (@draft, @patient, @clinician, 'History' || E'\n' || @history || E'\n\n' || 'Assessment' || E'\n' || @assessment || E'\n\n' || 'Plan' || E'\n' || @plan,
                    1, @history, @assessment, @plan, 1, false)
            """, ("draft", draft), ("patient", patient), ("clinician", clinician), ("history", history), ("assessment", assessment), ("plan", plan));
        return draft;
    }

    // A note signed before step 14, as signing left it: the note holds the draft's version 1, and the draft, frozen, is
    // at version 2. Sensitive if asked, marked by its signer.
    public static async Task<(Guid Draft, Guid Note)> SignedAsync(MigratedDatabase database, Guid clinician, Guid patient, string history,
        string assessment = "Synthetic assessment: earlier.", string plan = "Synthetic plan: earlier.", bool sensitive = false)
    {
        var draft = await DraftAsync(database, clinician, patient, history, assessment, plan);
        var note = Guid.CreateVersion7();
        await InsertAsync(database, """
            UPDATE ehr.clinical_drafts SET version = 2, structured_version = 2 WHERE id = @draft;
            INSERT INTO ehr.clinical_notes (id, draft_id, patient_id, draft_version, history, assessment, plan, signer_id, signed_at, sensitive, sensitive_marked_by)
            VALUES (@note, @draft, @patient, 1, @history, @assessment, @plan, @clinician, clock_timestamp(), @sensitive, CASE WHEN @sensitive THEN @clinician END)
            """, ("draft", draft), ("note", note), ("patient", patient), ("clinician", clinician), ("history", history), ("assessment", assessment),
            ("plan", plan), ("sensitive", sensitive));
        return (draft, note);
    }

    static async Task InsertAsync(MigratedDatabase database, string sql, params (string Name, object Value)[] parameters)
    {
        await using var connection = new NpgsqlConnection(database.SuperuserConnectionString);
        await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await using (var hold = new NpgsqlCommand("SET LOCAL session_replication_role = replica", connection, transaction))
        {
            await hold.ExecuteNonQueryAsync();
        }

        await using var command = new NpgsqlCommand(sql, connection, transaction);
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        await command.ExecuteNonQueryAsync();
        await transaction.CommitAsync();
    }
}
