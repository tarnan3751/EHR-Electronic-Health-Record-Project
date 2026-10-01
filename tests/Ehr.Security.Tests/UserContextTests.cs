using Ehr.Data;
using Ehr.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Ehr.Security.Tests;

// AGENTS.md: the RLS context is set with set_config(..., true) as the first statement of every transaction, and
// never outlasts it. These use the same registration as the apps (AddEhrData).
public class UserContextTests(MigratedDatabase database) : IClassFixture<MigratedDatabase>
{
    [Fact]
    public async Task Every_transaction_starts_with_the_user_context()
    {
        var userId = Guid.CreateVersion7();
        await using var services = BuildServices();
        await using var scope = services.CreateAsyncScope();
        var db = DbContextFor(scope, userId);

        await using var transaction = await db.Database.BeginTransactionAsync(TestContext.Current.CancellationToken);

        Assert.Equal(userId.ToString(), await CurrentUserIdAsync(db));
    }

    [Fact]
    public async Task No_user_is_sent_as_an_empty_string()
    {
        await using var services = BuildServices();
        await using var scope = services.CreateAsyncScope();
        var db = DbContextFor(scope, userId: null);

        await using var transaction = await db.Database.BeginTransactionAsync(TestContext.Current.CancellationToken);

        Assert.Equal("", await CurrentUserIdAsync(db));
    }

    [Fact]
    public async Task The_user_context_ends_with_the_transaction()
    {
        var userId = Guid.CreateVersion7();
        await using var services = BuildServices();
        await using var scope = services.CreateAsyncScope();
        var db = DbContextFor(scope, userId);

        // One connection throughout, as a pooled connection would be reused by the next request.
        await db.Database.OpenConnectionAsync(TestContext.Current.CancellationToken);
        await using (var transaction = await db.Database.BeginTransactionAsync(TestContext.Current.CancellationToken))
        {
            await transaction.CommitAsync(TestContext.Current.CancellationToken);
        }

        Assert.Equal("", await CurrentUserIdAsync(db));
    }

    ServiceProvider BuildServices() =>
        new ServiceCollection()
            .AddEhrData(database.ConnectionStringFor("ehr_app", MigratedDatabase.AppPassword))
            .BuildServiceProvider(validateScopes: true);

    // The request's database, with the request's user set, as sign-in will set it.
    static EhrDbContext DbContextFor(AsyncServiceScope scope, Guid? userId)
    {
        scope.ServiceProvider.GetRequiredService<UserContext>().UserId = userId;
        return scope.ServiceProvider.GetRequiredService<EhrDbContext>();
    }

    static Task<string> CurrentUserIdAsync(EhrDbContext db) =>
        db.Database.SqlQuery<string>($"SELECT current_setting('app.user_id', true) AS \"Value\"")
            .SingleAsync(TestContext.Current.CancellationToken);
}
