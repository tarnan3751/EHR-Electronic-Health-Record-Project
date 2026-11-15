"""transcript.py: MedASR's spoken punctuation and headings formatted, passages placed, offsets in UTF-16."""

from __future__ import annotations

from ehr_dictation.transcript import Segment, assemble, format, utf16_offset

# MedASR's own output for its test clip (eval of the model on this Mac, November 2026).
MEDASR_SAMPLE = (
    "[EXAM TYPE] CT chest PE protocol {period} [INDICATION] 54-year-old female, shortness of breath, evaluate for PE "
    "{period} [FINDINGS] {colon} Pulmonary vasculature {colon} The main PA is patent {period} Small bilateral effusions "
    "{comma} right greater than left {period} {new paragraph} [IMPRESSION] {colon} Acute segmental PE, right lower lobe "
    "{period}</s>"
)


def test_spoken_punctuation_becomes_punctuation():
    assert format("Denies chest pain {period} No fever {comma} no chills {period}") == "Denies chest pain. No fever, no chills."


def test_medasr_output_reads_as_written_text():
    assert format(MEDASR_SAMPLE) == (
        "Exam type: CT chest PE protocol.\n"
        "Indication: 54-year-old female, shortness of breath, evaluate for PE.\n"
        "Findings: Pulmonary vasculature: The main PA is patent. Small bilateral effusions, right greater than left.\n\n"
        "Impression: Acute segmental PE, right lower lobe."
    )


def test_parentheses_hyphens_and_unknown_commands():
    assert format("x {hyphen} ray {open parenthesis} chest {close parenthesis} {smiley face}") == "x-ray (chest) smiley face"


def test_model_tokens_are_dropped():
    assert format("<s> no acute distress {period}</s>") == "no acute distress."


def segment(raw: str, unclear: str | None = None, final: bool = True) -> Segment:
    return Segment(0, 16000, raw, 0.9, unclear, final)


def test_passages_are_joined_and_placed():
    transcript = assemble([segment("Cough for three days {period}"), segment("{comma} worse at night {period}"),
                           segment("Plan {colon} rest {period}")], final=True)

    assert transcript.text == "Cough for three days., worse at night. Plan: rest."
    assert [transcript.text[p.start : p.end] for p in transcript.placed] == [
        "Cough for three days.", ", worse at night.", "Plan: rest."
    ]


def test_an_unclear_passage_is_shown_marked_and_left_out_of_structuring():
    transcript = assemble([segment("Fever since Monday {period}"), segment("mumble", unclear="too_noisy"),
                           segment("No cough {period}")], final=True)

    assert transcript.text == "Fever since Monday. mumble No cough."
    assert transcript.for_structuring() == "Fever since Monday. [unclear] No cough."
    assert transcript.unclear_ranges() == [(20, 26)]
    assert transcript.to_json()["segments"][1] | {} == {
        "start": 20, "end": 26, "startMs": 0, "endMs": 1000, "final": True, "unclear": "too_noisy"
    }


def test_an_unclear_passage_with_no_text_still_shows_where_it_was():
    transcript = assemble([segment("", unclear="too_quiet")], final=False)

    assert transcript.text == "[unclear]"


def test_offsets_count_utf16_code_units():
    assert utf16_offset("a\U0001F600b", 2) == 3
    assert utf16_offset("café", 4) == 4
