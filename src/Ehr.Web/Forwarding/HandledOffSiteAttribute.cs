namespace Ehr.Web.Forwarding;

// Marks a page whose POST the cloud app handles itself rather than forwarding it to the on-prem app, because it saves
// nothing to the database: signing out only deletes the browser's cookie, and its audit event goes to the cloud app's
// queue (CloudAudit). So it works off-site whether or not the on-prem app can be reached.
[AttributeUsage(AttributeTargets.Class)]
public sealed class HandledOffSiteAttribute : Attribute;
