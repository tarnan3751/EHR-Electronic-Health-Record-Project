using Ehr.Domain.Accounts;

namespace Ehr.Web.Email;

// At most RateLimits:EmailsPerAddressPerHour account emails to one address (lower-cased) in any hour, so the account
// pages can't be used to flood someone's inbox. Over the cap it sends nothing and says nothing: the pages answer as
// usual. Kept in memory, so a restart starts the count again.
public sealed class CappedEmailSender(IEmailSender inner, int perAddressPerHour, TimeProvider time) : IEmailSender
{
    static readonly TimeSpan Window = TimeSpan.FromHours(1);

    readonly Dictionary<string, List<DateTimeOffset>> sent = [];

    public Task SendAsync(string to, string subject, string body, CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow();
        lock (sent)
        {
            foreach (var (address, times) in sent)
            {
                times.RemoveAll(at => now - at >= Window);
                if (times.Count == 0)
                {
                    sent.Remove(address);
                }
            }

            var key = to.ToLowerInvariant();
            if (!sent.TryGetValue(key, out var recent))
            {
                sent[key] = recent = [];
            }

            if (recent.Count >= perAddressPerHour)
            {
                return Task.CompletedTask;
            }

            recent.Add(now);
        }

        return inner.SendAsync(to, subject, body, cancellationToken);
    }
}
