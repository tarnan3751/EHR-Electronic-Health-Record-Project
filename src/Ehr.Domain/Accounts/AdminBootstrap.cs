using Ehr.Data;
using Ehr.Data.Accounts;
using Ehr.Data.Audit;
using Ehr.Domain.Audit;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Ehr.Domain.Accounts;

// The first admin. When the on-prem app starts and there are no staff accounts at all, it creates one from
// Ehr:Bootstrap (BootstrapOptions), with the Admin role and a password it must change at its first sign-in, where it
// also enrolls an authenticator. It never runs again once any staff account exists, and it never runs on the cloud
// site, which can't write. With the settings missing it only warns, so a stack without them still starts.
public sealed class AdminBootstrap(IServiceScopeFactory scopes, IOptions<BootstrapOptions> options, ILogger<AdminBootstrap> logger)
    : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<EhrDbContext>();
        if (await db.StaffUsers.AnyAsync(cancellationToken))
        {
            return;
        }

        var settings = options.Value;
        if (string.IsNullOrWhiteSpace(settings.AdminEmail) || string.IsNullOrWhiteSpace(settings.AdminPassword))
        {
            logger.LogWarning("There are no staff accounts and {Section} is not set, so nobody can sign in", BootstrapOptions.Section);
            return;
        }

        var users = scope.ServiceProvider.GetRequiredService<UserManager<StaffUser>>();
        var admin = new StaffUser
        {
            UserName = settings.AdminEmail,
            Email = settings.AdminEmail,
            EmailConfirmed = true,
            DisplayName = "Administrator",
            Roles = [StaffRoles.Admin],
            MustChangePassword = true,
        };
        var result = await users.CreateAsync(admin, settings.AdminPassword);
        if (!result.Succeeded)
        {
            throw new InvalidOperationException(
                $"Couldn't create the bootstrap admin from {BootstrapOptions.Section}: {string.Join(" ", result.Errors.Select(e => e.Description))}");
        }

        scope.ServiceProvider.GetRequiredService<AuditRecorder>().Record(AuditActions.BootstrapAdmin, tableName: "staff_users", rowId: admin.Id);
        await db.SaveChangesAsync(cancellationToken);
        logger.LogInformation("Created the bootstrap admin account {Email}. It must change its password and enroll an authenticator at first sign-in", settings.AdminEmail);
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
