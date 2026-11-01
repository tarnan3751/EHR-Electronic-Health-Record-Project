namespace Ehr.Web.Email;

// Ehr:Smtp: the mail server the on-prem app sends through. In the local stack it is the mail catcher
// (infra/compose/compose.yaml), which keeps every message for inspection and delivers none.
public sealed class SmtpOptions
{
    public const string Section = "Ehr:Smtp";

    public string? Host { get; set; }

    public int Port { get; set; } = 25;

    public string From { get; set; } = "no-reply@example.com";
}
