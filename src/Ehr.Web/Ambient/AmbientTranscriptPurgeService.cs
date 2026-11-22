using Ehr.Data;
using Ehr.Data.Patients;

namespace Ehr.Web.Ambient;

// The on-site app's timer for listening to a visit (the expansion plan's step 16): every hour, the transcripts of
// recorded visits whose reviews nobody has finished are deleted once they are older than Ehr:Ambient:KeepTranscriptDays
// (7 unless set shorter; the database holds it to 7 whatever is asked, AmbientTranscriptPurge). With nobody signed in, so
// the write audit records each delete as the system's act, with no one's rights borrowed. A review left open is not
// ended: its proposals can then only be rejected, or edited into the clinician's own words. Logs how many, never which.
public sealed class AmbientTranscriptPurgeService(IServiceScopeFactory scopes, TimeSpan keep, ILogger<AmbientTranscriptPurgeService> logger)
    : BackgroundService
{
    public const string Setting = "Ehr:Ambient:KeepTranscriptDays";

    public static readonly TimeSpan Longest = TimeSpan.FromDays(7);

    static readonly TimeSpan Every = TimeSpan.FromHours(1);

    // 7 days, or the shorter time the setting gives; anything longer, or not more than nothing, refuses to start.
    public static TimeSpan KeepFrom(IConfiguration configuration)
    {
        var days = configuration.GetValue<double?>(Setting) ?? Longest.TotalDays;
        return days is > 0 && days <= 7
            ? TimeSpan.FromDays(days)
            : throw new InvalidOperationException($"Set {Setting} to a number of days more than 0 and at most 7, or leave it out for 7.");
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Every);
        do
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                var db = scope.ServiceProvider.GetRequiredService<EhrDbContext>();
                await using var transaction = await db.Database.BeginTransactionAsync(stoppingToken);
                var deleted = await AmbientTranscriptPurge.PurgeAsync(db, keep, stoppingToken);
                await transaction.CommitAsync(stoppingToken);
                if (deleted > 0)
                {
                    logger.LogInformation("Deleted {Count} transcripts of recorded visits whose reviews weren't finished in time.", deleted);
                }
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                logger.LogError("The transcripts of recorded visits couldn't be purged: {Error}.", exception.GetType().Name);
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
