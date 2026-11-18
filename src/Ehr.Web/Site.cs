namespace Ehr.Web;

// The site an app instance is, as a service (Program.cs).
public sealed record SiteKind(Site Site)
{
    // The on-prem app writes to the primary; the cloud app reads a copy.
    public bool TakesWrites => Site == Site.OnPrem;
}

// Which server this copy of the app runs on, from the Ehr:Site setting. The same binary runs on both.
public enum Site
{
    // Reads and writes the on-prem primary, as ehr_app.
    OnPrem,

    // Reads the cloud replica, as ehr_read, and forwards everything else to the on-prem app (Forwarding/).
    Cloud,
}
