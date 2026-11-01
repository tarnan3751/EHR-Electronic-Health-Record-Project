using System.Collections.Concurrent;
using Ehr.Domain.Accounts;

namespace Ehr.Web.Tests;

// Keeps every email the apps send, so a test can follow the link in it.
public sealed class RecordingEmailSender : IEmailSender
{
    public ConcurrentQueue<(string To, string Subject, string Body)> Sent { get; } = new();

    public Task SendAsync(string to, string subject, string body, CancellationToken cancellationToken)
    {
        Sent.Enqueue((to, subject, body));
        return Task.CompletedTask;
    }
}
