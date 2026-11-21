namespace Ehr.Web.Areas.Portal.Pages.Forms;

// What the portal's pre-visit forms tell a person about getting help now (the expansion plan's step 13e), in this one
// place so it changes in one edit. The wording, and the choice of 911 and the 988 Suicide & Crisis Lifeline for a
// synthetic clinic in the United States, are the coordinator's, recorded in docs/expansion/PLAN.md under "Decided without
// Tahseen — to review": a clinician should read them before any real use, and a clinic outside the United States must
// change the numbers. The forms aren't watched as they arrive, and they say so; nothing here is ever emailed.
public static class FormNotices
{
    // Above every form, and on the forms page, whatever is answered: the state word, then the sentence.
    public const string NotMonitoredState = "Not monitored:";

    public const string NotMonitored = "your care team reads this form before your visit, not when you send it. If you need help now, call 911. "
        + "You can call or text 988 at any time to talk with someone at the 988 Suicide & Crisis Lifeline.";

    // After the PHQ-9's question 9 was answered other than "Not at all", and whenever those answers are opened again: what
    // to do now, leading with it, and never the answer itself, which may be read over a shoulder. For the patient, or,
    // when a proxy sent them, about the person whose record it is.
    public static string AfterAnswerToReview(string? proxyFor) =>
        (proxyFor is null
            ? "If you are thinking about hurting yourself, or feel you might act on these thoughts, call 911 or go to the nearest emergency room now. "
            : $"If {proxyFor} is thinking about hurting themselves, or you think they might act on these thoughts, call 911 or go to the nearest emergency room now. ")
        + "You can call or text 988 at any time, day or night, to talk with someone at the 988 Suicide & Crisis Lifeline. "
        + "Your care team will read your answers before your visit, but not as soon as you send them.";
}
