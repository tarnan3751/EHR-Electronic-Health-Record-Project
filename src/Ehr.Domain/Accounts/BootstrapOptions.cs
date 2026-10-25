namespace Ehr.Domain.Accounts;

// Ehr:Bootstrap: the first admin account, created by AdminBootstrap when there are no staff accounts. Both values
// come from environment configuration (infra/compose/.env.example); with either missing, nothing is created.
public sealed class BootstrapOptions
{
    public const string Section = "Ehr:Bootstrap";

    public string? AdminEmail { get; set; }

    public string? AdminPassword { get; set; }
}
