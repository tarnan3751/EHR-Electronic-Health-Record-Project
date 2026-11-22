using System.Globalization;
using System.Text;
using Ehr.Domain.Notes;
using Ehr.Testing;
using Npgsql;
using Xunit;

namespace Ehr.Web.Tests;

// A section that shows nothing isn't signed (the expansion plan's step 14), and the app and the database agree on what
// shows nothing: NoteContent.IsBlank and the signing rule's ehr.note_section_is_blank (db/policies/50-clinical-notes.sql)
// count the same code points, every one of them. What each refuses is in TC-UC14-28 and ClinicalNoteSecurityTests.
public class NoteBlankTests(MigratedDatabase database) : IClassFixture<MigratedDatabase>
{
    [Fact]
    public async Task The_app_and_the_database_count_the_same_characters_as_showing_nothing()
    {
        var app = Enumerable.Range(1, 0x10FFFF).Where(Rune.IsValid).Where(c => NoteContent.IsBlank(char.ConvertFromUtf32(c)))
            .Select(c => c.ToString("x", CultureInfo.InvariantCulture)).ToList();
        // As the app role, which signs notes.
        await using var connection = new NpgsqlConnection(database.ConnectionStringFor("ehr_app", MigratedDatabase.AppPassword));
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = new NpgsqlCommand(
            "SELECT to_hex(c) FROM generate_series(1, 1114111) AS c WHERE (c < 55296 OR c > 57343) AND ehr.note_section_is_blank(chr(c)) ORDER BY c", connection);
        var inDatabase = new List<string>();
        await using var reader = await command.ExecuteReaderAsync(TestContext.Current.CancellationToken);
        while (await reader.ReadAsync(TestContext.Current.CancellationToken))
        {
            inDatabase.Add(reader.GetString(0));
        }

        Assert.Equal(app, inDatabase);
        Assert.Contains("200b", app);
        Assert.Contains("a0", app);
        Assert.DoesNotContain("41", app);
    }
}
