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


def test_a_source_running_across_two_voices_is_cut_and_its_value_supported_by_what_is_left():
    cut = checked(VISIT, subjective=("I have had a cough.", ["today I have had a cough"]))["subjective"]
    took = checked(VISIT, subjective=("Today I have had a cough.", ["today I have had a cough"]))["subjective"]

    assert cut.pending and cut.speakers == ["patient"] and [VISIT.text[s:e] for s, e in cut.spans] == ["I have had a cough"]
    assert "unsupported_content" in took.reasons, "a value's word from the clinician's piece is no one's to give"


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
    windows = conversation.windows(text, 6)

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

    windows = conversation.windows(text, 4)

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
    text = heard(("patient", "can you refill my inhaler"), ("clinician", "i'll send that to your pharmacy {period}", True),
                 ("clinician", "i'll put in a referral to cardiology {period}"))

    listed = validation.conversation_not_carried_over(text, text.passages())

    assert [(text.text[i["start"] : i["end"]], i["speaker"]) for i in listed] == [
        ("i'll send that to your pharmacy", "unsure"), ("i'll put in a referral to cardiology", "clinician")]


# The second review: one voice's turn is one line however sure each passage is, so a cue or an instruction reaches
# across a passage diarize.py was unsure of, and a source across both is unsure, warned, not wrong.
def test_a_cue_an_instruction_and_a_source_reach_across_one_voices_unsure_passage():
    text = heard(("clinician", "i don't think you have", True), ("clinician", "a chest infection or pneumonia"),
                 ("clinician", "i'm going to send you", True), ("clinician", "for an ultrasound of your kidneys"),
                 ("patient", "okay"))

    fields = checked(text, assessment=("Chest infection or pneumonia.", ["a chest infection or pneumonia"]),
                     objective=("You have a chest infection.", ["i don't think you have a chest infection"]))
    listed = validation.conversation_not_carried_over(text, text.passages())

    assert "negation_dropped" in fields["assessment"].reasons
    assert any("ultrasound of your kidneys" in text.text[i["start"] : i["end"]] for i in listed)
    unsure = checked(text, assessment=("I don't think you have a chest infection.", ["i don't think you have a chest infection"]))
    assert unsure["assessment"].pending and unsure["assessment"].speakers == ["unsure"]


# The service's contract (series 1's fix round): the voices listed for naming are numbered 1 to k with none missing, so
# a voice diarize.py told apart that MedASR heard nothing from leaves no gap.
def test_the_voices_that_said_something_are_numbered_from_one_with_none_missing():
    passages = [(Segment(i * 16000, (i + 1) * 16000, said, 0.9, None, True), voice, False)
                for i, (said, voice) in enumerate((("Hello {period}", 1), ("Hi {period}", 3), ("Cough {period}", 3)))]

    heard = conversation.assemble(passages).numbered()

    assert heard.voices == (1, 2, 2) and [v["group"] for v in heard.speakers()] == [1, 2]


# ---- The second review: one shape for hedges, denials and reports; questions; bare answers --------------------------

# Said with a head, fillers, a subject and its verb before the finding; offered without them, each is refused.
LEAKS = [
    ("clinician", "it doesn't look like you have a chest infection", "assessment", "chest infection"),
    ("clinician", "there's no sign that you have a fracture", "assessment", "fracture"),
    ("clinician", "i can't rule out that you have a stone", "assessment", "stone"),
    ("clinician", "it's possible you have a stone", "assessment", "stone"),
    ("clinician", "we'll see whether you have an infection", "assessment", "infection"),
    ("clinician", "if you have chest pain come straight back", "objective", "chest pain"),
    ("clinician", "i think um given what you've told me you have a sinus infection", "assessment", "sinus infection"),
    ("clinician", "i think um you have a sinus infection", "assessment", "sinus infection"),
    ("clinician", "there's a chance you have a hernia", "assessment", "hernia"),
    ("clinician", "there's no evidence you have a clot", "assessment", "clot"),
    ("clinician", "nothing to suggest you have a clot", "assessment", "clot"),
    ("clinician", "it's unlikely you have a clot", "assessment", "clot"),
    ("clinician", "i wouldn't think you have a fracture", "assessment", "fracture"),
    ("clinician", "my guess is you have a sprain", "assessment", "sprain"),
    ("clinician", "i suspect um you have a sprain", "assessment", "sprain"),
    ("clinician", "you may well have a sprain", "assessment", "sprain"),
    ("clinician", "we're not sure yet whether it's a sprain", "assessment", "sprain"),
    ("clinician", "hard to say if it's a sprain", "assessment", "sprain"),
    ("clinician", "i'd like to exclude a clot", "assessment", "clot"),
    ("patient", "i don't think i have a fever", "subjective", "fever"),
    ("patient", "i might have had a temperature", "subjective", "temperature"),
    ("patient", "i'm not sure if i had a temperature", "subjective", "temperature"),
    ("patient", "i thought i had a fever but", "subjective", "fever"),
    ("patient", "i wouldn't say i've had a fever", "subjective", "fever"),
    ("patient", "i don't reckon i've had a fever", "subjective", "fever"),
    ("patient", "i dunno if i had a fever", "subjective", "fever"),
    ("patient", "i could have had a fever", "subjective", "fever"),
    ("patient", "my wife thinks i have sleep apnoea", "subjective", "sleep apnoea"),
    ("patient", "i was told i might have asthma", "subjective", "asthma"),
    ("patient", "the other doctor said i had asthma", "subjective", "asthma"),
]


@pytest.mark.parametrize(("who", "said", "field", "finding"), LEAKS)
def test_a_hedge_denial_or_report_reaches_its_finding_past_fillers_and_a_subject(who, said, field, finding):
    text = heard((who, said))

    reasons = checked(text, **{field: (f"Has {finding}.", [said])})[field].reasons

    assert {"negation_dropped", "certainty_dropped", "reported_dropped"} & set(reasons), reasons


def test_a_report_kept_with_its_frame_is_the_patients_to_give():
    text = heard(("patient", "my wife thinks i have sleep apnoea"))

    assert checked(text, subjective=("My wife thinks I have sleep apnoea.", ["my wife thinks i have sleep apnoea"]))[
        "subjective"].pending


QUESTIONS = [
    (("clinician", "any fever"), ("patient", "no"), "objective", "Fever."),
    (("clinician", "have you had any fever or chills"), ("patient", "no"), "objective", "Fever or chills."),
    (("clinician", "do you have chest pain"), ("patient", "nope"), "assessment", "Chest pain."),
    (("clinician", "your chest is clear any shortness of breath"), ("patient", "not really"), "objective",
     "Shortness of breath."),
    (("patient", "do i need antibiotics for it"), ("clinician", "no"), "subjective", "I need antibiotics for it."),
    (("clinician", "chest pain at all"), ("patient", "no"), "objective", "Chest pain."),
]


@pytest.mark.parametrize(("asked", "answered", "field", "value"), QUESTIONS)
def test_a_question_is_never_a_findings_source(asked, answered, field, value):
    text = heard(asked, answered)
    source = asked[1].split("clear ")[-1] if "clear " in asked[1] else asked[1]

    assert "source_is_a_question" in checked(text, **{field: (value, [source])})[field].reasons


def test_a_statement_answered_yes_is_still_a_statement_and_not_every_auxiliary_asks():
    text = heard(("clinician", "your tummy is a little tender just above the pubic bone"), ("patient", "yes"),
                 ("clinician", "the thing is it hurts more at night i'll have you back in two weeks"))

    fields = checked(text, objective=("Tender just above the pubic bone.", ["tender just above the pubic bone"]),
                     plan=("Back in two weeks.", ["back in two weeks"]))

    assert fields["objective"].pending and fields["plan"].pending
    assert validation.conversation_questions(text.text, text.passages()) == []


# A source across a question and its answer: what is left of it after the cut must say something on its own.
CUT_AT_THE_VOICE = [
    (("clinician", "any chest pain"), ("patient", "no"), "No chest pain.", "any chest pain no", False),
    (("clinician", "any chest pain"), ("patient", "no chest pain"), "No chest pain.", "any chest pain no chest pain", True),
    (("clinician", "how long has it been going on"), ("patient", "about two weeks"), "About two weeks.",
     "how long has it been going on about two weeks", False),
    (("clinician", "how long has it been going on"), ("patient", "about two weeks"), "Going on about two weeks.",
     "how long has it been going on about two weeks", False),
    (("clinician", "how long has the cough been going on"), ("patient", "the cough's been about two weeks"),
     "The cough's been about two weeks.", "how long has the cough been going on the cough's been about two weeks", True),
    (("clinician", "what brings you in today"), ("patient", "i've had a cough for two weeks"),
     "I've had a cough for two weeks.", "what brings you in today i've had a cough for two weeks", True),
    (("patient", "i get headaches most mornings"), ("clinician", "okay noted"), "I get headaches most mornings.",
     "i get headaches most mornings okay noted", True),
]


@pytest.mark.parametrize(("first", "second", "value", "source", "offered"), CUT_AT_THE_VOICE)
def test_a_question_and_its_answer_quoted_together_leave_only_an_answer_that_says_what_it_is_about(
        first, second, value, source, offered):
    field = checked(heard(first, second), subjective=(value, [source]))["subjective"]

    assert field.pending == offered, field.reasons
    assert offered or field.reasons


def test_a_source_cut_at_the_voice_gives_the_furthest_reason_when_nothing_is_left():
    text = heard(("clinician", "any chest pain"), ("patient", "not really"))

    reasons = checked(text, subjective=("Not really.", ["any chest pain not really"]))["subjective"].reasons

    assert "bare_answer" in reasons and "wrong_speaker" not in reasons and "source_is_a_question" not in reasons


def test_an_answer_alone_is_never_a_value():
    text = heard(("clinician", "any chest pain"), ("patient", "not really"))

    assert "bare_answer" in checked(text, subjective=("Not really.", ["not really"]))["subjective"].reasons


# ---- The third review: a question or a hedge MedASR heard wrong, read by its frame ---------------------------------

def test_a_question_run_on_after_a_conjunction_is_a_question_with_or_without_its_auxiliary():
    for said in ("lets skip this weeks lesson and keep her ear dry until the pain is gone and how you holding up you had "
                 "a long night too", "keep her ear dry until the pain is gone and how are you holding up"):
        text = heard(("clinician", said), ("other", "I am okay just tired"))

        assert "source_is_a_question" in checked(text, plan=("Keep her ear dry until the pain is gone.", [said]))[
            "plan"].reasons, said


def test_a_wh_word_after_a_conjunction_that_asks_nothing_is_no_question():
    text = heard(("clinician", "take the tamsulosin at bedtime and when you get up at night sit on the edge of the bed"),
                 ("clinician", "we will go through your blood pressure and how your mood is"))

    assert validation.conversation_questions(text.text, text.passages()) == []


def test_the_sentence_an_answer_alone_answers_in_its_line_is_a_question():
    said = ("I did I wrote them all in a little notebook like you asked me to {period} So I read you the last week {period} "
            "yes please {period} This morning it was 142/88 {period}")
    text = heard(("patient", said))

    fields = checked(text, subjective=("I read you the last week.", ["So I read you the last week"]))
    kept = checked(text, subjective=("This morning it was 142/88.", ["This morning it was 142/88"]))

    assert "source_is_a_question" in fields["subjective"].reasons and kept["subjective"].pending


def test_a_word_that_isnt_an_answer_or_an_answer_after_an_answer_asks_nothing():
    text = heard(("patient", "I take it every morning {period} Okay {period} No {period} Not at all {period}"))

    assert validation.conversation_questions(text.text, text.passages()) == []


# Questions as the visits' transcripts show them asked, each found by its shape: (who asked, what MedASR heard, who
# answered and how, the question's words).
ASKED = [
    ("clinician", "does the medicine taste yucky", ("patient", "it tastes like bubble gum"), "does the medicine taste yucky"),
    ("patient", "is anything serious", ("clinician", "I don't think you have pneumonia"), "is anything serious"),
    ("clinician", "great has anyone else at home been sick", ("other", "a few kids in her class"),
     "has anyone else at home been sick"),
    ("clinician", "that must have been frightening what did they tell you was going on", ("patient", "they said my heart"),
     "did they tell you was going on"),
    ("clinician", "no never have {period} and alcohol {period}", ("patient", "a couple of pints at the weekend"),
     "and alcohol"),
    ("clinician", "Name about the blood test {period}", ("patient", "the nurse took it last week"),
     "Name about the blood test"),
    ("patient", "okay W I need an operation", ("clinician", "I doubt it most of these settle with exercises"),
     "okay W I need an operation"),
    ("patient", "so nothing serious then", ("clinician", "nothing that worries me today"), "so nothing serious then"),
]


@pytest.mark.parametrize(("asker", "asked", "answer", "question"), ASKED)
def test_a_question_is_found_by_its_shape_however_it_was_heard(asker, asked, answer, question):
    text = heard((asker, asked), answer)

    spans = [text.text[s:e] for s, e in validation.conversation_questions(text.text, text.passages())]

    assert any(question in span for span in spans), spans


def test_a_statement_the_next_voice_goes_on_from_is_still_a_statement():
    text = heard(("patient", "no fever at all"), ("clinician", "no fever on exam today"),
                 ("clinician", "the cough is the worst part {period} have the bloods done before you go {period}"),
                 ("patient", "is a little tender just above the pubic bone {period} and then some {period}"),
                 ("clinician", "okay {period}"), ("patient", "it hurts here"), ("clinician", "okay {period}"),
                 ("patient", "it started on tuesday"))

    assert validation.conversation_questions(text.text, text.passages()) == []


@pytest.mark.parametrize("verb", ["recogn", "recog", "reckon", "bet", "suppose", "expect", "gather", "imagine", "hope"])
def test_an_opinion_is_a_hedge_by_its_frame_whatever_its_verb_was_heard_as(verb):
    said = f"I {verb} you got allergic rhinitis on top of the asthma"
    text = heard(("clinician", said))

    reasons = checked(text, assessment=("Allergic rhinitis on top of the asthma.", [said]))["assessment"].reasons

    assert "certainty_dropped" in reasons, reasons


@pytest.mark.parametrize("verb", validation._KNOWING)
def test_a_verb_of_knowing_or_seeing_says_it_plainly(verb):
    said = f"I {verb} you have got allergic rhinitis"
    text = heard(("clinician", said))

    assert checked(text, assessment=("You have got allergic rhinitis.", [said]))["assessment"].pending


# The second review's acts: a closed-class noun and its verb in one clause, in either order.
ACTS = [
    "let me print you off a prescription for the cream", "it should be ready for you at the chemist by four",
    "it'll be with the pharmacy this afternoon", "i'll get the bloods requested before you leave",
    "i'll get your repeat sorted", "i'll sort the refill out for you", "i'll ask the lab to run a thyroid panel",
    "i'll pop a form in for some blood tests", "i'll write up the request for a dexa scan",
    "i'll mark the amoxicillin as an intolerance rather than an allergy", "i'm going to note that you're allergic to sulfa",
    "let me note you're allergic to latex", "i'll put you down as allergic to penicillin",
    "i'll update your record so it shows the latex allergy",
    # "Can" is no possibility.
    "i can book you in for an ultrasound this afternoon", "i can request the bloods now",
    "we can send you for an x-ray right now",
]
NOT_ACTS = [
    "i'll get you an appointment with the physio", "if it doesn't settle we can arrange an ultrasound",
    "we can book you in for a scan if you'd like", "let me get you another tissue", "can i get you a fresh glass of water",
    "we'll top up the vitamin d with some sunshine", "you're allergic to penicillin aren't you",
]


@pytest.mark.parametrize("said", ACTS)
def test_an_act_said_in_other_words_is_cut_and_listed(said):
    text = heard(("clinician", said))

    assert validation.conversation_not_carried_over(text, text.passages()), said


@pytest.mark.parametrize("said", NOT_ACTS)
def test_what_names_no_act_or_only_a_possibility_is_not_listed(said):
    text = heard(("clinician", said))

    assert validation.conversation_not_carried_over(text, text.passages()) == [], said


def test_a_list_of_tests_is_cut_whole_and_care_said_after_an_order_is_kept():
    text = heard(("clinician", "i'm sending you for bloods an ecg and a chest x-ray and a urine sample"),
                 ("clinician", "i'll send that to your pharmacy and you should rest your knee for a week"))

    fields = checked(text, plan=("X-ray and urine sample. Rest your knee for a week.",
                                 ["a chest x-ray and a urine sample", "you should rest your knee for a week"]))

    listed = " ".join(text.text[i["start"] : i["end"]] for i in validation.conversation_not_carried_over(text, text.passages()))

    assert "a chest x-ray and a urine sample" in listed and "send that to your pharmacy" in listed
    assert "rest your knee" not in listed
    assert not fields["plan"].pending, "the list's tail is the order's, not the Plan's"
    rest = checked(text, plan=("Rest your knee for a week.", ["you should rest your knee for a week"]))["plan"]
    assert rest.pending


def test_nothing_of_an_act_is_left_in_a_value_and_sort_that_out_needs_the_refill_last():
    referral = heard(("clinician", "i've put in a referral to cardiology"))
    parking = heard(("patient", "can you refill my inhaler also i got a parking ticket out there"),
                    ("clinician", "i'll sort that out"))

    plan = checked(referral, plan=("I've put in a referral to cardiology.", ["i've put in a referral to cardiology"]))["plan"]

    assert not plan.pending and plan.value == ""
    assert validation.conversation_not_carried_over(parking, parking.passages()) == []


# ---- The record model session's fix round -------------------------------------------------------------------------

# A prescription sent on, found by its frame whatever the place was heard as (c09's room recording heard "I'll send it
# over to the chemist for you" as "…to the chemistvi"); a person sent, a place that is a person, or a sending refused,
# isn't one.
SENT_ON = ["i will send it over to the chemistvi", "i'll send it over to the farmacy for you",
           "i'll send them off to the labb this afternoon", "we'll fax that through to the hospitle"]
NOT_SENT_ON = ["i won't send it over to the farmacy", "i'll send you to the hospital", "i'll send it to you by post",
               "i'll send her home to rest"]


@pytest.mark.parametrize("said", SENT_ON)
def test_a_prescription_sent_on_is_cut_and_listed_whatever_the_place_was_heard_as(said):
    text = heard(("clinician", "give it with food and finish the whole bottle {period}"), ("clinician", said))

    listed = validation.conversation_not_carried_over(text, text.passages())
    sent = checked(text, plan=("Give it with food and finish the whole bottle. " + said,
                               ["give it with food and finish the whole bottle", said]))["plan"]
    kept = checked(text, plan=("Give it with food and finish the whole bottle.",
                               ["give it with food and finish the whole bottle"]))["plan"]

    assert [text.text[i["start"] : i["end"]] for i in listed] == [said], listed
    assert not sent.pending or sent.value == "Give it with food and finish the whole bottle.", "cut from the value"
    assert kept.pending, "the care said beside it is still offered"


@pytest.mark.parametrize("said", NOT_SENT_ON)
def test_a_person_sent_or_a_sending_refused_is_not_listed(said):
    text = heard(("clinician", said))

    assert validation.conversation_not_carried_over(text, text.passages()) == [], said
    assert checked(text, plan=(said, [said]))["plan"].pending


# The lean: an Unsure passage in the clinician's voice is no Subjective source (c09's room recording quoted the
# clinician's "like a seashell? that's a good way to put it. any trouble with her hearing at school?", heard Unsure, as
# the patient's history), while Objective, Assessment and Plan may still quote it, warned; one in the patient's voice,
# or in a voice named "unsure", is still Subjective's, warned.
def test_subjective_never_quotes_the_clinicians_voice_even_where_its_passage_is_unsure():
    text = heard(("clinician", "Your chest is clear {period}"),
                 ("patient", "I have had a cough for two weeks {period}"),
                 ("clinician", "The cough is from the cold you had {period}", True),
                 ("patient", "It is worse at night {period}", True))

    leaned = checked(text, subjective=("The cough is from the cold you had.", ["The cough is from the cold you had"]))
    together = checked(text, subjective=("I have had a cough for two weeks. The cough is from the cold you had.",
                                         ["I have had a cough for two weeks", "The cough is from the cold you had"]))
    patients = checked(text, subjective=("I have had a cough for two weeks. It is worse at night.",
                                         ["I have had a cough for two weeks", "It is worse at night"]))
    assessed = checked(text, assessment=("The cough is from the cold you had.", ["The cough is from the cold you had"]))

    assert leaned["subjective"].reasons[0] == "wrong_speaker"
    assert not together["subjective"].pending, "refused, never trimmed"
    assert patients["subjective"].pending and patients["subjective"].speakers == ["patient", "unsure"]
    assert assessed["assessment"].pending
    assert [code for code, _, _ in assessed["assessment"].warnings] == ["speaker_unsure"]


def test_a_voice_named_unsure_has_no_lean():
    text = conversation.assemble([
        (Segment(0, 16000, "Your chest is clear {period}", 0.9, None, True), 1, False),
        (Segment(16000, 32000, "I have had a cough for two weeks {period}", 0.9, None, True), 2, False),
        (Segment(32000, 48000, "She coughs at night {period}", 0.9, None, True), 3, False),
    ]).named({1: "clinician", 2: "patient", 3: "unsure"})

    fields = checked(text, subjective=("She coughs at night.", ["She coughs at night"]))

    assert fields["subjective"].pending and fields["subjective"].speakers == ["unsure"]


# Turn-taking where the voices weren't told apart: the clinician's question pulled into the patient's voice, Unsure,
# answered in the same line by the patient (c11's clean recording heard "Do you nap during the day?" as "ate nap during
# the day"), is a question, and the patient's answer beside it still passes; the clinician's finding after an Unsure
# reading, in the clinician's own line, isn't one (c06: "is 118/74. no tenderness over your kidneys").
def test_an_unsure_passage_the_patient_answers_in_their_line_is_a_question():
    text = heard(("clinician", "anything in particular"),
                 ("patient", "nothing much whether I've done the right thing {period}"),
                 ("patient", "ate nap during the day {period}", True),
                 ("patient", "sometimes after lunch for half an hour or so {period}"))
    reading = heard(("clinician", "your blood pressure {period}"), ("clinician", "is 118/74 {period}", True),
                    ("clinician", "no tenderness over your kidneys {period}"), ("patient", "okay"))

    asked = checked(text, subjective=("Ate nap during the day.", ["ate nap during the day"]))["subjective"]
    answered = checked(text, subjective=("Sometimes after lunch for half an hour or so.",
                                         ["sometimes after lunch for half an hour or so"]))["subjective"]
    examined = checked(reading, objective=("Blood pressure is 118/74. No tenderness over your kidneys.",
                                           ["your blood pressure is 118/74", "no tenderness over your kidneys"]))[
        "objective"]

    assert "source_is_a_question" in asked.reasons
    assert answered.pending and answered.speakers == ["patient"]
    assert examined.pending, examined.reasons


# Answers as MedASR hears them: "ys please" answers the last clause of the patient's turn (c11's room recording heard
# "…like you asked me to. Shall I read you the last week?" as "…like you me to, so I redow the last week"), and what
# the patient said before it, and after, still passes; the clinician's statement answered "ys" is still a statement.
def test_an_answer_heard_with_a_letter_lost_answers_the_last_clause_of_a_turn():
    text = heard(("patient", "I did I wrote them all in a little notebook like you asked me to {comma} so I redow the "
                             "last week {period}"),
                 ("clinician", "ys please {period}"), ("patient", "This morning it was 142/88 {period}"))
    told = heard(("clinician", "your tummy is a little tender just above the pubic bone"), ("patient", "ys"),
                 ("clinician", "I'll have you back in two weeks"))

    offered = checked(text, subjective=("So I redow the last week.", ["so I redow the last week"]))["subjective"]
    wrote = checked(text, subjective=("I wrote them all in a little notebook.",
                                      ["I wrote them all in a little notebook"]))["subjective"]
    reading = checked(text, subjective=("This morning it was 142/88.", ["This morning it was 142/88"]))["subjective"]
    tender = checked(told, objective=("Tender just above the pubic bone.", ["tender just above the pubic bone"]))

    assert "source_is_a_question" in offered.reasons
    assert wrote.pending and reading.pending
    assert tender["objective"].pending
    assert validation.HEARD_ANSWERS["ys"] == "yes" and "up" not in validation.HEARD_ANSWERS


# A modal opening a sentence asks, its subject unheard (c11's room recording heard the patient's "Will it need an
# injection?" as "will need an injection"), and the patient's statement beside it still passes; before be, have, been
# or not it is a hedge or a prediction, and a modal after its subject is a statement.
def test_a_modal_opening_a_sentence_asks_whatever_follows_but_be_have_been_or_not():
    text = heard(("clinician", "you can use ibuprofen gel on it at bedtime {period}"),
                 ("patient", "will need an injection {period} I would rather try the exercises first {period}"),
                 ("clinician", "possibly if the exercises have not helped after a couple of months {period}"))
    stated = heard(("clinician", "could be a viral cough {period} should be drier by the weekend {period} would have "
                                 "been worse without the inhaler {period} can not see any redness {period}"))

    asked = checked(text, subjective=("Will need an injection.", ["will need an injection"]))["subjective"]
    rather = checked(text, subjective=("I would rather try the exercises first.",
                                       ["I would rather try the exercises first"]))["subjective"]

    assert "source_is_a_question" in asked.reasons
    assert rather.pending
    assert validation.conversation_questions(stated.text, stated.passages()) == []


# The conversation's own names live beside the common ones in one module: none may stand in for a common one, which
# would change what a dictation's checks do.
def test_no_name_in_the_checks_is_defined_twice():
    import ast
    from pathlib import Path

    tree = ast.parse(Path(validation.__file__).read_text(encoding="utf-8"))
    names = [target.id for node in tree.body if isinstance(node, ast.Assign) for target in node.targets
             if isinstance(target, ast.Name)]
    names += [node.name for node in tree.body if isinstance(node, (ast.FunctionDef, ast.ClassDef))]

    assert sorted({name for name in names if names.count(name) > 1}) == []
