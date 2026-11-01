using System.Net.Mail;
using Ehr.Domain.Accounts;
using Microsoft.Extensions.Options;

namespace Ehr.Web.Email;

// Plain SMTP, unauthenticated, to the host in Ehr:Smtp: enough for the stack's mail catcher and a clinic's relay on
// the same network. The messages carry an address and a link, never PHI.
public sealed class SmtpEmailSender(IOptions<SmtpOptions> options) : IEmailSender
{
    public async Task SendAsync(string to, string subject, string body, CancellationToken cancellationToken)
    {
        var settings = options.Value;
        using var client = new SmtpClient(settings.Host, settings.Port);
        using var message = new MailMessage(settings.From, to, subject, body);
        await client.SendMailAsync(message, cancellationToken);
    }
}
