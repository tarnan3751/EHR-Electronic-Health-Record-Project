namespace Ehr.Data.Audit;

// The values AuditEvent.Part may hold (the expansion plan's step 13c2): which part of the record a read was for, as a
// short key the PHI reader in Ehr.Domain sets at each read, from the page or API that asked for it. Never free text,
// never an address or an ID: the database refuses anything but these, and any of them on an event that isn't a read
// (ck_audit_events_part). A list closed in the schema, so a new part is a migration, and a decision: what the portal's
// "Who viewed my record" calls it and whether a proxy without sensitive items is told it (db/policies/42-record-views.sql).
// Events recorded before this step, and by an app older than its migration, have none.
public static class AuditParts
{
    // The clinic's chart, with the signed notes and the drafts it lists.
    public const string Chart = "chart";

    // A chart list's entry form and the lists' history of changes: allergies, medications and diagnoses.
    public const string Lists = "lists";

    // The order form, stopping an order, and the orders stopped.
    public const string Orders = "orders";

    // An order's results in the clinic; the portal's test results.
    public const string Results = "results";

    public const string Vitals = "vitals";

    // The patient listed by a patient search, on a clinician's panel, on a day's schedule, or in a staff inbox.
    public const string Search = "search";

    public const string Panel = "panel";

    public const string Schedule = "schedule";

    public const string Inbox = "inbox";

    // An appointment's page and booking one in the clinic; the portal's appointments page.
    public const string Appointments = "appointments";

    // The portal's home page, which shows the appointments.
    public const string Home = "home";

    // Writing a message to the patient, a conversation in the clinic or the portal, and the portal's list of them.
    public const string Conversations = "conversations";

    // Who the record is shared with: the clinic's page of proxies and the portal's Access page.
    public const string Access = "access";

    // A signed note's pages: the note, an addendum, its sensitivity, its visit summaries.
    public const string VisitNote = "visit-note";

    // A note being written: dictation, a draft and its AI review, the desktop app's draft sync.
    public const string NoteDraft = "note-draft";

    // The desktop app's offline copy of a chart on the clinician's panel.
    public const string Offline = "offline";

    // The FHIR API (step 11).
    public const string Fhir = "fhir";

    // The portal's visit summaries (step 13a), its printable record and its download (step 13b), and Who viewed my
    // record (step 13c).
    public const string Summaries = "summaries";

    public const string Printable = "printable";

    public const string Download = "download";

    public const string Views = "views";

    // Refill requests (step 13d) and pre-visit forms (step 13e), listed here from the start so those steps need no
    // migration of their own for them.
    public const string Refills = "refills";

    public const string Forms = "forms";

    // Every part, for the database's check, the admin audit page's filter and the tests that hold the lists together.
    public static readonly string[] All =
    [
        Chart, Lists, Orders, Results, Vitals, Search, Panel, Schedule, Inbox, Appointments, Home, Conversations, Access,
        VisitNote, NoteDraft, Offline, Fhir, Summaries, Printable, Download, Views, Refills, Forms,
    ];
}
