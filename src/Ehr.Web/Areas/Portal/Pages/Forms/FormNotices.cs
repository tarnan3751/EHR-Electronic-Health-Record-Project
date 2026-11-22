using Ehr.Data.Reference;

namespace Ehr.Web.Areas.Portal.Pages.Forms;

// What the portal's pre-visit forms tell a person about getting help now (the expansion plan's step 13e), in this one
// place so it changes in one edit. The wording, and the choice of 911 and the 988 Suicide & Crisis Lifeline for a
// synthetic clinic in the United States, are the coordinator's, recorded in docs/expansion/PLAN.md under "Decided without
// Tahseen — to review": a clinician should read them before any real use, and a clinic outside the United States must
// change the numbers. Each is worded for the patient, or, when someone they share their record with is filling in the
// form, about the person whose record it is, throughout. The forms aren't watched as they arrive, and they say so;
// nothing here is ever emailed.
public static class FormNotices
{
    // Above every form, and on the forms page, whatever is answered: the state word, then the sentence.
    public const string NotMonitoredState = "Not monitored:";

    public static string NotMonitored(string? proxyFor) =>
        (proxyFor is null
            ? "your care team reads this form before your visit, not when you send it. If you need help now, call 911. "
            : $"{proxyFor}'s care team reads this form before the visit, not when you send it. If {proxyFor} needs help now, call 911. ")
        + "You can call or text 988 at any time to talk with someone at the 988 Suicide & Crisis Lifeline.";

    // The notice an alert question names (its definition's help, QuestionnaireHelp), shown when it was answered so that
    // the answer needs review, and whenever those answers are opened again: what to do now, leading with it, and never
    // the answer itself, which may be read over a shoulder. Sent says whether the answers reached the clinic: only then
    // does it say the care team will read them. Null for a name it doesn't know, which QuestionnaireDefinitionTests
    // keeps out of the questionnaires; the notice above still says where to get help.
    public static string? Help(string? help, string? proxyFor, bool sent) => help switch
    {
        QuestionnaireHelp.SelfHarm =>
            (proxyFor is null
                ? "If you are thinking about hurting yourself, or feel you might act on these thoughts, call 911 or go to the nearest emergency room now. "
                : $"If {proxyFor} is thinking about hurting themselves, or you think they might act on these thoughts, call 911 or go to the nearest emergency room now. ")
            + "You can call or text 988 at any time, day or night, to talk with someone at the 988 Suicide & Crisis Lifeline."
            + (!sent ? ""
                : proxyFor is null ? " Your care team will read your answers before your visit, but not as soon as you send them."
                : $" {proxyFor}'s care team will read these answers before the visit, but not as soon as you send them."),
        _ => null,
    };
}
