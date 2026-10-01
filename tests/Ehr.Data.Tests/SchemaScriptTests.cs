using Ehr.Migrations;
using Ehr.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Xunit;

namespace Ehr.Data.Tests;

// db/migrations/schema.sql is the SQL all the migrations produce, committed so that a pull request shows the real
// SQL next to the C# that generates it. This fails when the migrations change and the file wasn't regenerated.
public class SchemaScriptTests
{
    [Fact]
    public void Schema_sql_matches_the_migrations()
    {
        using var context = new EhrDbContextFactory().CreateDbContext([]);
        var generated = context.GetService<IMigrator>().GenerateScript();
        var committed = File.ReadAllText(RepositoryRoot.Combine("db/migrations/schema.sql"));

        if (Normalize(generated) != Normalize(committed))
        {
            Assert.Fail("db/migrations/schema.sql is out of date. Regenerate it from the repository folder with: "
                + "dotnet ef migrations script --project db/migrations --output db/migrations/schema.sql");
        }
    }

    [Fact]
    public void Every_model_change_has_a_migration()
    {
        using var context = new EhrDbContextFactory().CreateDbContext([]);

        if (context.Database.HasPendingModelChanges())
        {
            Assert.Fail("The model in Ehr.Data has changes no migration covers. Add one from the repository folder with: "
                + "dotnet ef migrations add <Name> --project db/migrations");
        }
    }

    // Ignores the byte order mark, line endings and trailing newlines, which vary by OS and editor.
    static string Normalize(string sql) => sql.TrimStart('﻿').ReplaceLineEndings("\n").TrimEnd();
}
