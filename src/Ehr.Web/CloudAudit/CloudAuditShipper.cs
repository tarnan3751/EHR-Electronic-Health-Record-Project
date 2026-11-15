using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Ehr.Data.Audit;
using Ehr.Web.Api.Audit;
using Ehr.Web.Forwarding;

namespace Ehr.Web.CloudAudit;

// Sends the cloud app's queued audit events to the on-prem app every couple of seconds, a batch at a time, and marks
// them shipped once it has confirmed them. With nothing queued it still sends an empty batch: that's how the cloud app
// knows whether the on-prem app can save, which OnPremStatus shows on every page.
public sealed class CloudAuditShipper(
    CloudAuditQueue queue, OnPremApp onPrem, OnPremStatus status, CloudAuditKey key, ILogger<CloudAuditShipper> log)
    : BackgroundService
{
    static readonly TimeSpan Interval = TimeSpan.FromSeconds(2);

    static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    // How much of a refusal goes in the log: the on-prem app's own validation message.
    public const int MaxAnswerLength = 300;

    readonly SemaphoreSlim oneAtATime = new(1, 1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Interval);
        do
        {
            try
            {
                await ShipAsync(stoppingToken);
            }
            catch (Exception e) when (!stoppingToken.IsCancellationRequested)
            {
                // A background service that throws stops the whole app, and the next try may work.
                log.LogError(e, "Shipping audit events failed; they stay queued.");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    // One batch. True if the on-prem app took it.
    public async Task<bool> ShipAsync(CancellationToken cancellationToken)
    {
        await oneAtATime.WaitAsync(cancellationToken);
        try
        {
            var (events, end) = ReadPending();
            using var request = new HttpRequestMessage(HttpMethod.Post, onPrem.Url.TrimEnd('/') + CloudAuditApi.Path)
            {
                Content = JsonContent.Create(events, options: JsonSerializerOptions.Web),
            };
            request.Headers.Add(CloudAuditApi.KeyHeader, key.Value);

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
                // A server error means the on-prem app is up but can't save, as with its database down: read-only all
                // the same.
                var canSave = (int)response.StatusCode < 500;
                status.Report(reachable: canSave);
                if (!canSave)
                {
                    log.LogWarning("The on-prem app can't take audit events now ({Status}); they stay queued.", (int)response.StatusCode);
                    return false;
                }

                if (response.StatusCode == HttpStatusCode.Unauthorized)
                {
                    // Nothing on the page shows this: the on-prem app answers, so the site isn't read-only.
                    log.LogError(
                        "The on-prem app refused the cloud app's audit key: Ehr:CloudAudit:Key must be the same on both apps. {Count} audit events stay queued.",
                        events.Count);
                    return false;
                }

                if (!response.IsSuccessStatusCode)
                {
                    // Reachable but refused: its answer tells a clock too far ahead from a bug. The events stay queued
                    // for a fix.
                    var answer = await response.Content.ReadAsStringAsync(timeout.Token);
                    log.LogError(
                        "The on-prem app refused {Count} audit events with {Status}: {Answer}",
                        events.Count, (int)response.StatusCode, answer.Length > MaxAnswerLength ? answer[..MaxAnswerLength] : answer);
                    return false;
                }
            }

            // A queue that couldn't be read sent only the check-in, and has nothing to confirm.
            if (end is not { } shipped)
            {
                return false;
            }

            queue.MarkShipped(shipped);
            return true;
        }
        finally
        {
            oneAtATime.Release();
        }
    }

    // The queued events and where they end, or none if the queue can't be read. That needs someone to repair the queue,
    // but the empty check-in still goes: this is the only thing that marks the on-prem app reachable again.
    (List<AuditEvent> Events, long? End) ReadPending()
    {
        try
        {
            var (events, end) = queue.ReadPending(CloudAuditApi.MaxEvents);
            return (events, end);
        }
        catch (Exception e)
        {
            log.LogError(e, "The cloud app's audit queue can't be read; nothing in it ships until someone repairs it.");
            return ([], null);
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
