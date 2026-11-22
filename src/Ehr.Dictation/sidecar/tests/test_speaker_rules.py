"""TC-UC10-19 (proposed FR-62): a conversation's transcript and windows (conversation.py), and the checks validation.py
adds for a conversation: who may be quoted in which field, conversation hedges, the warnings that say who said a
value's sources, the windows' fields merged, and only the clinician's instructions listed. Dictation's checks are
unchanged: test_validation.py, untouched, proves it. All of it synthetic."""

from __future__ import annotations

from ehr_dictation import conversation, validation
from ehr_dictation.prompt import FIELDS
from ehr_dictation.transcript import Segment

VOICES = {"clinician": 1, "patient": 2, "other": 3}
NAMES = {1: "clinician", 2: "patient", 3: "other"}


def heard(*turns: tuple[str, str] | tuple[str, str, bool]) -> conversation.Conversation:
    """(who, what MedASR heard[, unsure]) a passage at a time, a second each, the voices named."""
    passages = [
        (Segment(i * 16000, (i + 1) * 16000, turn[1], 0.9, None, True), VOICES[turn[0]], turn[2] if len(turn) > 2 else False)
        for i, turn in enumerate(turns)
    ]
    return conversation.assemble(passages).named(NAMES)


def proposed(**values: tuple[str, list[str]]) -> dict[str, dict]:
    return {name: {"value": values.get(name, ("", []))[0], "sources": values.get(name, ("", []))[1]} for name in FIELDS}


def checked(text: conversation.Conversation, window: tuple[int, int] | None = None, **values) -> dict:
    scope = validation.Scope(window or (0, len(text.text)), text.passages())
    return validation.check(proposed(**values), [], text, scope=scope).fields


VISIT = heard(
    ("clinician", "What brings you in today {question mark}"),
    ("patient", "I have had a cough for two weeks {period}"),
    ("clinician", "Your chest is clear {period}"),
    ("clinician", "It could be a viral cough {period}"),
)


def test_subjective_quotes_the_patient_and_the_rest_the_clinician():
    fields = checked(
        VISIT,
        subjective=("I have had a cough for two weeks.", ["I have had a cough for two weeks"]),
        objective=("Your chest is clear.", ["Your chest is clear"]),
        assessment=("It could be a viral cough.", ["It could be a viral cough"]),
    )

    assert all(fields[name].pending for name in ("subjective", "objective", "assessment"))
    assert fields["subjective"].speakers == ["patient"] and fields["objective"].speakers == ["clinician"]
    assert fields["subjective"].to_json(VISIT)["sources"][0]["speaker"] == "patient"


def test_a_source_said_by_someone_the_field_may_not_quote_is_wrong_speaker():
    fields = checked(
        VISIT,
        subjective=("Your chest is clear.", ["Your chest is clear"]),
        objective=("Cough for two weeks.", ["I have had a cough for two weeks"]),
    )

    assert fields["subjective"].reasons[0] == "wrong_speaker" and fields["objective"].reasons[0] == "wrong_speaker"


def test_the_same_words_said_by_both_are_quoted_from_the_one_the_field_may_quote():
    text = heard(("clinician", "No fever {question mark}"), ("patient", "No fever at all {period}"),
                 ("clinician", "No fever on exam today {period}"))

    fields = checked(text, subjective=("No fever.", ["No fever"]), objective=("No fever on exam today.", ["No fever on exam today"]))

    assert fields["subjective"].pending and fields["subjective"].speakers == ["patient"]
    assert text.text[slice(*fields["subjective"].spans[0])] == "No fever"
    assert fields["subjective"].spans[0][0] > text.text.index("\n"), "the patient's, not the clinician's question"
    assert fields["objective"].pending


def test_a_source_running_across_two_speakers_turns_is_wrong_speaker():
    fields = checked(VISIT, subjective=("Today I have had a cough.", ["today I have had a cough"]))

    assert fields["subjective"].reasons[0] == "wrong_speaker"


def test_a_role_written_before_a_source_is_taken_off():
    fields = checked(VISIT, objective=("Your chest is clear.", ["Clinician: Your chest is clear"]))

    assert fields["objective"].pending


def test_a_source_outside_the_window_isnt_in_it():
    end = VISIT.text.index("\n", VISIT.text.index("\n") + 1)

    fields = checked(VISIT, window=(0, end), objective=("Your chest is clear.", ["Your chest is clear"]))

    assert fields["objective"].reasons[0] == "source_not_in_transcript"


def test_a_source_whose_speaker_couldnt_be_told_or_someone_else_in_the_room_is_warned_of():
    text = heard(("clinician", "Any cough {question mark}"), ("patient", "Cough for two weeks {period}", True),
                 ("other", "She coughs at night {period}"), ("clinician", "Lungs clear {period}", True))

    fields = checked(
        text,
        subjective=("Cough for two weeks. She coughs at night.", ["Cough for two weeks", "She coughs at night"]),
        objective=("Lungs clear.", ["Lungs clear"]),
    )

    assert fields["subjective"].speakers == ["unsure", "other"]
    assert [code for code, _, _ in fields["subjective"].warnings] == ["speaker_unsure", "other_speaker"]
    assert [code for code, _, _ in fields["objective"].warnings] == ["speaker_unsure"]
    assert [s["speaker"] for s in text.to_json()["segments"]] == ["clinician", "unsure", "other", "unsure"]


def test_a_conversations_hedges_are_kept_and_dictation_is_as_it_was():
    dropped = checked(VISIT, assessment=("Viral cough.", ["It could be a viral cough"]))
    kept = checked(VISIT, assessment=("Could be a viral cough.", ["It could be a viral cough"]))
    patient = heard(("patient", "I think it started on Tuesday {period}"))
    started = checked(patient, subjective=("It started on Tuesday.", ["it started on Tuesday"]))
    # "It could be" is dictation's hedge too (step 15's subject cues); "could be" with no subject is a conversation's only.
    bare = heard(("clinician", "Could be a viral cough {period}"))
    dictated = validation.check(proposed(assessment=("Viral cough.", ["Could be a viral cough"])), [], bare).fields

    assert "certainty_dropped" in dropped["assessment"].reasons and kept["assessment"].pending
    assert "certainty_dropped" in started["subjective"].reasons
    assert dictated["assessment"].pending, "\"could be\" is a conversation's hedge only"
    assert "certainty_dropped" in checked(bare, assessment=("Viral cough.", ["Could be a viral cough"]))["assessment"].reasons


def test_a_cue_in_one_turn_doesnt_reach_the_next():
    text = heard(("clinician", "Any chest pain {question mark}"), ("patient", "No {period}"), ("clinician", "Heart sounds normal"))

    fields = checked(text, objective=("Heart sounds normal.", ["Heart sounds normal"]))

    assert fields["objective"].pending


def test_a_conversation_has_no_one_field_holds_dictation_warning():
    text = heard(("clinician", "Your chest is clear and your heart sounds normal {period}"))

    fields = checked(text, objective=("Your chest is clear and your heart sounds normal.",
                                      ["Your chest is clear and your heart sounds normal"]))

    assert fields["objective"].pending and fields["objective"].warnings == []


def test_instructions_are_cut_whoever_said_them_and_only_the_clinicians_are_listed():
    text = heard(("patient", "My inhaler ran out {period} Can you refill my inhaler {question mark}"),
                 ("clinician", "Continue the inhaler {period} I'll send that to your pharmacy {period}"))

    fields = checked(text, subjective=("My inhaler ran out. Can you refill my inhaler?",
                                       ["My inhaler ran out", "Can you refill my inhaler"]),
                     plan=("Continue the inhaler. I'll send that to your pharmacy.",
                           ["Continue the inhaler", "I'll send that to your pharmacy"]))
    listed = validation.conversation_not_carried_over(text, text.passages())

    assert fields["subjective"].pending and fields["subjective"].value == "My inhaler ran out."
    assert fields["plan"].pending and fields["plan"].value == "Continue the inhaler."
    assert [text.text[item["start"] : item["end"]] for item in listed] == ["I'll send that to your pharmacy"]
    assert len(validation.not_carried_over(text)) == 2, "a dictation's rule would list the patient's too"


def test_the_windows_fields_are_merged_in_order_with_what_didnt_pass_pointed_at():
    first = (0, VISIT.text.index("\n", VISIT.text.index("\n") + 1))
    second = (first[1] + 1, len(VISIT.text))
    parts = [
        (validation.Scope(first, VISIT.passages()), validation.check(proposed(
            subjective=("I have had a cough for two weeks.", ["I have had a cough for two weeks"]),
            objective=("Chest clear.", ["Your chest is clear"])), [], VISIT, scope=validation.Scope(first, VISIT.passages()))),
        (validation.Scope(second, VISIT.passages()), validation.check(proposed(
            objective=("Your chest is clear.", ["Your chest is clear"]),
            plan=("Rest at home.", ["rest at home"])), ["orders"], VISIT, scope=validation.Scope(second, VISIT.passages()))),
    ]

    merged = validation.merge(parts)

    assert merged.fields["subjective"].value == "I have had a cough for two weeks."
    assert [code for code, _, _ in merged.fields["subjective"].warnings] == ["patients_own_words"]
    objective = merged.fields["objective"]
    assert objective.value == "Your chest is clear." and objective.speakers == ["clinician"]
    assert [(code, start, end) for code, start, end in objective.warnings] == [("part_not_offered", *first)]
    assert merged.fields["plan"].reasons == ["source_not_in_transcript", "unsupported_content"]
    assert merged.fields["assessment"].reasons == ["not_dictated"]
    assert merged.unknown == ["orders"]


def test_the_transcript_is_a_line_a_turn_and_its_windows_label_each_line():
    text = heard(("clinician", "What brings you in {question mark}"), ("patient", "A cough {period}"),
                 ("patient", "For two weeks {period}"), ("other", "She coughs at night {period}"),
                 ("clinician", "Your chest is clear {period}"))
    windows = conversation.windows(text, words=6)

    assert text.text == "What brings you in?\nA cough. For two weeks.\nShe coughs at night.\nYour chest is clear."
    assert [w.text for w in windows] == [
        "Clinician: What brings you in?",
        "Patient: A cough. For two weeks.",
        "Other: She coughs at night.",
        "Clinician: Your chest is clear.",
    ]
    assert [text.text[w.start : w.end] for w in windows][1] == "A cough. For two weeks."
    assert [v["group"] for v in text.speakers()] == [1, 2, 3] and text.speakers()[1]["seconds"] == 2.0


def test_a_turn_longer_than_a_window_is_cut_at_its_passages_and_unclear_ones_are_marked():
    text = conversation.assemble([
        (Segment(0, 16000, "Cough for two weeks {period}", 0.9, None, True), 2, False),
        (Segment(16000, 32000, "mumbled", 0.3, "low_confidence", True), 2, False),
        (Segment(32000, 48000, "Worse at night {period}", 0.9, None, True), 2, False),
    ]).named({2: "patient", 1: "clinician"})

    windows = conversation.windows(text, words=4)

    assert [w.text for w in windows] == ["Patient: Cough for two weeks.", "Patient: [unclear] Worse at night."]


# ---- Said to the patient (series 1's review, F1, F4 and F6) ---------------------------------------------------------

import pytest  # noqa: E402

# Hedged or denied to the patient's face: offered as certain, the value is refused; offered with its hedge, it passes.
# subject_cues("you") reads most of them, as dictation's cues read "she" and "he"; the rest are a conversation's own.
HEDGED = [
    ("i don't think you have pneumonia", "you have pneumonia"), ("i think you have gout", "you have gout"),
    ("maybe you have gout", "you have gout"), ("it could be that you have gout", "you have gout"),
    ("you might have gout", "you have gout"), ("i wonder if you have gout", "you have gout"),
    ("possibly you have gout", "you have gout"), ("i'm not sure you have gout", "you have gout"),
    ("i doubt you have a fracture", "you have a fracture"), ("we think you've got gout", "you've got gout"),
    ("i don't believe you have a fracture", "you have a fracture"),
    ("it's unlikely that you have a fracture", "you have a fracture"),
    # A conversation's own.
    ("i'd be surprised if you had a fracture", "you had a fracture"), ("i suspect you have gout", "you have gout"),
    ("i reckon you have gout", "you have gout"), ("probably you have gout", "you have gout"),
    ("you probably have gout", "have gout"), ("it might be that you have gout", "you have gout"),
    ("i didn't think you had a fracture", "you had a fracture"),
]


@pytest.mark.parametrize(("said", "certain"), HEDGED)
def test_a_hedge_or_a_denial_said_to_the_patient_stays_with_what_it_hedges(said, certain):
    text = heard(("clinician", said))

    reasons = checked(text, assessment=(certain, [said]))["assessment"].reasons
    assert {"negation_dropped", "certainty_dropped"} & set(reasons), reasons
    assert checked(text, assessment=(said, [said]))["assessment"].pending


# The coordinator's rule (F4): talk of an ordering act or of the prescription itself is cut and listed; care described
# without one stays in the Plan. The reviewer's sentences, as speech recognition writes conversation.
CUT = [
    # The prescription, the pharmacy, a refill.
    "i'll send that to your pharmacy", "i'll send it over to walgreens for you", "i'll send a prescription to cvs",
    "i'll give you a prescription for amoxicillin", "i'll write you a script for some naproxen",
    "i'm going to prescribe you some amoxicillin", "i'll call it in for you",
    "you'll have a new one at your usual pharmacy by tomorrow", "i'll renew your inhaler",
    "i'll refill your lisinopril for three months", "i'll sort out the refill before you go", "i'll get you a new inhaler",
    "i'll e-prescribe the antibiotic now", "i'll write you a prescription", "i'll print you a prescription",
    "i'll do you a repeat prescription", "i'll put a prescription through for you", "i've sent it electronically",
    "i'll send that off to the chemist", "i'll get that sent to cvs", "it'll be ready at the pharmacy this afternoon",
    "you can pick it up from the pharmacy later today", "i'll call the pharmacy for you",
    "i'll get you some more of the metformin", "i'll top up your inhaler", "i'll renew it for another six months",
    "i'll get that refilled for you", "i'll send the prescription over to the pharmacy on the high street near where you live",
    "i'll refill your lisinopril", "i'll put your order in",
    # An ordering act for a test or a referral.
    "i'm going to send you for an ultrasound", "i'll book you in for an mri", "i'll put in a referral to cardiology",
    "i'm going to order some blood work", "i'll put the order in for an echo", "i'll arrange a scan",
    "i'll request a lipid panel", "i'm sending you to the lab for a blood count", "i'll get you booked in for a colonoscopy",
    # The allergy list.
    "i'll add that to your allergies", "i'll add penicillin to your allergy list", "i'll put penicillin on your allergy list",
    "i'll take sulfa off your allergy list", "i'll update your allergies", "i'll note that you're allergic to penicillin",
    "i'll make a note that you're allergic to codeine", "i'll record that you react to sulfa",
    "i'll mark you as allergic to codeine", "i'll add sulfa to the allergy list",
    "i'll remove codeine from your chart's allergies", "i'll put that in your chart as an allergy",
    "i'll put that on your record as an allergy", "i'll write that down as an allergy",
    "i'll flag that you're allergic to latex", "i'll change your codeine allergy to an intolerance",
    "i'll get rid of the penicillin allergy", "i'll make sure that's on your allergy list",
    "i'll mark it as a side effect rather than an allergy",
    "codeine isn't really an allergy it just made you sleepy so we'll take that off your list",
]
PLAN = [
    "we'll get some bloods today", "let's get an x-ray of that knee", "i want you to have a chest x-ray today",
    "we'll do a urine test before you leave", "i'll get the nurse to take some blood", "i'll refer you to the physio",
    "we'll check your kidney function today", "let's do an ecg today", "the nurse will give you the flu jab before you go",
    "i'm going to give you a course of antibiotics", "i'm going to put you on amoxicillin", "let's start you on metformin",
    "we'll take that off your list", "if it's no better in 6 weeks we can arrange an ultrasound",
    "we could book you in for a scan if it carries on", "i'll sort that out for you",
]


@pytest.mark.parametrize("said", CUT)
def test_an_ordering_act_said_to_the_patient_is_cut_and_listed(said):
    text = heard(("clinician", said))

    assert validation.conversation_not_carried_over(text, text.passages()), "listed"
    plan = checked(text, plan=(said, [said]))["plan"]
    assert not plan.pending or plan.value != said, "cut from the value"


@pytest.mark.parametrize("said", PLAN)
def test_care_described_without_an_ordering_act_stays_in_the_plan(said):
    text = heard(("clinician", said))

    assert validation.conversation_not_carried_over(text, text.passages()) == []
    assert checked(text, plan=(said, [said]))["plan"].pending


def test_a_conversations_instructions_are_its_own_and_leave_dictations_as_they_were():
    # test_validation.py, untouched, holds dictation's directives(); these are a conversation's alone.
    assert validation.directives("i'll book you in for an mri") == []
    assert validation.conversation_directives("i'll book you in for an mri") != []


# "I'll sort that out" answering a request for a refill is the refill (the eval's c05 says it so).
def test_sorting_out_a_refill_just_asked_for_is_cut_and_listed():
    text = heard(("patient", "can you refill my sertraline i'm down to my last few tablets"),
                 ("clinician", "yes of course i'll sort that out before you go"))

    assert [text.text[i["start"] : i["end"]] for i in validation.conversation_not_carried_over(text, text.passages())] == [
        "i'll sort that out before you go"]


# F6: an order in a passage whose speaker couldn't be told is listed too, marked; a patient's request still isn't.
def test_an_order_whose_speaker_couldnt_be_told_is_listed_and_says_so():
    text = heard(("patient", "can you refill my inhaler"), ("clinician", "i'll send that to your pharmacy", True),
                 ("clinician", "i'll put in a referral to cardiology"))

    listed = validation.conversation_not_carried_over(text, text.passages())

    assert [(text.text[i["start"] : i["end"]], i["speaker"]) for i in listed] == [
        ("i'll send that to your pharmacy", "unsure"), ("i'll put in a referral to cardiology", "clinician")]


# The service's contract (series 1's fix round): the voices listed for naming are numbered 1 to k with none missing, so
# a voice diarize.py told apart that MedASR heard nothing from leaves no gap.
def test_the_voices_that_said_something_are_numbered_from_one_with_none_missing():
    passages = [(Segment(i * 16000, (i + 1) * 16000, said, 0.9, None, True), voice, False)
                for i, (said, voice) in enumerate((("Hello {period}", 1), ("Hi {period}", 3), ("Cough {period}", 3)))]

    heard = conversation.assemble(passages).numbered()

    assert heard.voices == (1, 2, 2) and [v["group"] for v in heard.speakers()] == [1, 2]
