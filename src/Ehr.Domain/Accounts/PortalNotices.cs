using Ehr.Data;
using Ehr.Domain.Time;
using Microsoft.EntityFrameworkCore;

namespace Ehr.Domain.Accounts;

// The portal's one email about the record: that there is something new in the patient portal, and a link, nothing else.
// No word of what it is, no names and no health details, whatever it is about and whether it is sensitive or not. Sent
// to the patient's own portal account, never a proxy's, as for messages; a patient with no confirmed portal account gets
// nothing. Each feature that sends it decides when, and how often: the few emails an address may be sent in an hour
// (CappedEmailSender) are also what a password reset draws on. Visit summaries send it (step 13a), and the answers to
// refill requests (step 13d), each at most once a day.
public sealed class PortalNotices(EhrDbContext db, IEmailSender email, ClinicTime clinic)
{
    // A day, for "at most once a day", is the clinic's calendar day, from midnight in its time zone (ClinicTime): a
    // feature sends the notice for the first of its events since then. Not the last 24 hours, which held back the
    // first event of each day that came less than 24 hours after the one before.
    public DateTimeOffset StartOfToday => clinic.DayBounds(clinic.Today).From;

    public async Task SomethingNewAsync(Guid patient, string portalLink, CancellationToken cancellationToken)
    {
        var address = await db.PatientUsers.AsNoTracking().Where(u => u.PatientId == patient && u.EmailConfirmed)
            .Select(u => u.Email).FirstOrDefaultAsync(cancellationToken);
        if (address is not null)
        {
            await email.SendAsync(address, "Something new in your patient portal",
                "Your clinic added something to your patient portal. Sign in to see it.",
                new EmailAction("See what's new", portalLink), cancellationToken);
        }
    }
}
