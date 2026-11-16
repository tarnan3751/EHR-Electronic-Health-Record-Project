using System.Threading.Channels;
using Ehr.Domain.Accounts;

namespace Ehr.Web.Email;

// The portal's account emails go out in the background, so a page answers in the same time whether or not it sent
// one: how long a response takes doesn't tell anyone which addresses have accounts. The queue is in memory. A message
// still queued when the app stops is lost, as is one the mail server refuses, which is acceptable: the patient asks again
// (/portal/resend, /portal/forgot). Past 1,000 waiting messages, new ones are dropped rather than held in memory.
public sealed class QueuedEmailSender(IEmailSender inner, ILogger<QueuedEmailSender> logger) : BackgroundService, IEmailSender
{
    readonly Channel<(string To, string Subject, string Body)> queue =
        Channel.CreateBounded<(string, string, string)>(new BoundedChannelOptions(1_000) { FullMode = BoundedChannelFullMode.DropWrite });

    public Task SendAsync(string to, string subject, string body, CancellationToken cancellationToken)
    {
        queue.Writer.TryWrite((to, subject, body));
        return Task.CompletedTask;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var (to, subject, body) in queue.Reader.ReadAllAsync(stoppingToken))
        {
            try
            {
                await inner.SendAsync(to, subject, body, stoppingToken);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                // Only the kind of failure: a mail error's message can name the recipient, and an address in a log is
                // a disclosure.
                logger.LogError("An account email could not be sent: {Error}.", exception.GetType().Name);
            }
        }
    }
}
