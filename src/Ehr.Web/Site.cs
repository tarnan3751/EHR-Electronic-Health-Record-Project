namespace Ehr.Web;

// Which server this copy of the app runs on, from the Ehr:Site setting. The same binary runs on both.
public enum Site
{
    // Reads and writes the on-prem primary, as ehr_app.
    OnPrem,

    // Reads the cloud replica, as ehr_read, and forwards everything else to the on-prem app (Forwarding/).
    Cloud,
}
