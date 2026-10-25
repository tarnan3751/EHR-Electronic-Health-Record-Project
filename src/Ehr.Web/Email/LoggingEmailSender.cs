using Ehr.Domain.Accounts;

namespace Ehr.Web.Email;

// Development without a mail server: the message goes to the log, link included, so the flow can be followed from
// the console. Never for a deployment, where an address in a log is a disclosure (Program.cs picks the SMTP sender
// whenever Ehr:Smtp:Host is set).
public sealed class LoggingEmailSender(ILogger<LoggingEmailSender> logger) : IEmailSender
{
    public Task SendAsync(string to, string subject, string body, CancellationToken cancellationToken)
    {
        logger.LogInformation("Email to {To}: {Subject}\n{Body}", to, subject, body);
        return Task.CompletedTask;
    }
}
