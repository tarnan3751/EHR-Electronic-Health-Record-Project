namespace Ehr.Domain.Accounts;

// Sends the portal's emails: confirmation links, and password resets later. The host decides how (Program.cs): logged
// in development, SMTP to the stack's mail catcher otherwise. A message never carries PHI: an address and a link.
public interface IEmailSender
{
    Task SendAsync(string to, string subject, string body, CancellationToken cancellationToken);
}
