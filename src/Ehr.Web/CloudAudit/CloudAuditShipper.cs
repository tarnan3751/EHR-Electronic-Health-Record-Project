using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Ehr.Web.Api.Audit;
using Ehr.Web.Forwarding;

namespace Ehr.Web.CloudAudit;

// Sends the cloud app's queued audit events to the on-prem app every couple of seconds, a batch at a time, and marks
// them shipped once it has confirmed them. With nothing queued it still sends an empty batch: that's how the cloud app
// knows whether the on-prem app is reachable, which OnPremStatus shows on every page.
public sealed class CloudAuditShipper(
    CloudAuditQueue queue, OnPremApp onPrem, OnPremStatus status, CloudAuditKey key, ILogger<CloudAuditShipper> log)
    : BackgroundService
{
    static readonly TimeSpan Interval = TimeSpan.FromSeconds(2);

    static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    readonly SemaphoreSlim oneAtATime = new(1, 1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Interval);
        do
        {
            await ShipAsync(stoppingToken);
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    // One batch. True if the on-prem app took it.
    public async Task<bool> ShipAsync(CancellationToken cancellationToken)
    {
        await oneAtATime.WaitAsync(cancellationToken);
        try
        {
            var (events, end) = queue.ReadPending(CloudAuditApi.MaxEvents);
            using var request = new HttpRequestMessage(HttpMethod.Post, onPrem.Url.TrimEnd('/') + CloudAuditApi.Path)
            {
                Content = JsonContent.Create(events, options: JsonSerializerOptions.Web),
            };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key.Value);

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(Timeout);
            HttpResponseMessage response;
            try
            {
                response = await onPrem.Client.SendAsync(request, timeout.Token);
            }
            catch (Exception e) when (e is HttpRequestException || (e is OperationCanceledException && !cancellationToken.IsCancellationRequested))
            {
                status.Report(reachable: false);
                log.LogWarning("The on-prem app can't be reached; audit events stay queued. {Error}", e.Message);
                return false;
            }

            using (response)
            {
                status.Report(reachable: true);
                if (!response.IsSuccessStatusCode)
                {
                    // Reachable but refused: a key that doesn't match, or a bug. The events stay queued for a fix.
                    log.LogError("The on-prem app refused {Count} audit events with {Status}.", events.Count, (int)response.StatusCode);
                    return false;
                }
            }

            queue.MarkShipped(end);
            return true;
        }
        finally
        {
            oneAtATime.Release();
        }
    }
}

// The secret both apps share for shipping audit events (Ehr:CloudAudit:Key). Being on the WAN proves nothing (AGENTS.md).
public sealed record CloudAuditKey(string Value)
{
    public const int MinimumLength = 32;

    public static CloudAuditKey FromConfiguration(IConfiguration configuration) =>
        configuration["Ehr:CloudAudit:Key"] is { Length: >= MinimumLength } key
            ? new(key)
            : throw new InvalidOperationException($"Set Ehr:CloudAudit:Key to the same secret of at least {MinimumLength} characters on both apps.");
}
