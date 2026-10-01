namespace Ehr.Web;

// Which server this copy of the app runs on, from the Ehr:Site setting. The same binary runs on both.
public enum Site
{
    // Reads and writes the on-prem primary, as ehr_app.
    OnPrem,

    // Reads the cloud replica, as ehr_read. It can't save: forwarding saves to the on-prem app isn't built yet.
    Cloud,
}

// The site, for pages to ask what this server can do.
public sealed record CurrentSite(Site Site)
{
    public bool CanSave => Site == Site.OnPrem;
}
