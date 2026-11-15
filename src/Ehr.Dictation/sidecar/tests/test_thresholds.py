"""Every threshold the sidecar still has, pinned by behaviour just either side of it, so changing one is a deliberate
change that fails here first (and changes the code hash the eval's results are tied to)."""

from __future__ import annotations

import numpy as np
from conftest import noise, speech_and_pauses, tone
from test_validation import field, own, transcript

from ehr_dictation import quality, segmenter, settings, validation
from ehr_dictation.validation import not_carried_over

FPS = segmenter.FRAMES_PER_SECOND


def test_the_values():
    assert (quality.QUIET_DBFS, quality.NOISY_SNR_DB, quality.CONFIDENCE) == (-42.0, 10.0, 0.55)
    assert (quality.SILENCE_DBFS, quality.MIN_SOUND_FRAMES) == (-60.0, 50)
    assert (segmenter.MIN_PAUSE, segmenter.KEEP, segmenter.MAX_SEGMENT) == (40, 300, 2000)
    assert (validation.CORRECTION_WINDOW, validation.MAX_GAP, validation.MAX_SOURCES, validation.MAX_QUOTE) == (3, 1, 30, 600)
    assert (validation.DIRECTIVE_REACH, validation.LEAD_IN_REACH) == (14, 5)
    assert validation.LIMITS == {"history": 3000, "assessment": 2000, "plan": 2000}
    limits = settings.Settings(api_key="k", llama_api_key="k")
    assert (limits.max_session_seconds, limits.max_session_bytes, limits.max_chunk_bytes) == (180, 10 << 20, 2 << 20)
    assert (limits.call_seconds, limits.stop_wait_seconds, limits.structuring_seconds) == (15.0, 8.0, 90.0)


def judge(level_db: float, confidence: float = 0.9, background_db: float = -80.0):
    speech = tone(2, level_db=level_db) + noise(2, background_db)
    recording = np.concatenate([noise(1, background_db), speech])
    return quality.assess(segmenter.levels(speech), segmenter.noise_floor(segmenter.levels(recording)), confidence, True)


def test_too_quiet_starts_below_minus_42_dbfs():
    assert judge(-41) is None
    assert judge(-43) == quality.TOO_QUIET


def test_too_noisy_starts_below_10_db_above_the_background():
    assert judge(-20, background_db=-31.5) is None
    assert judge(-20, background_db=-28.5) == quality.TOO_NOISY


def test_low_confidence_starts_below_0_55():
    assert judge(-20, confidence=0.56) is None
    assert judge(-20, confidence=0.54) == quality.LOW_CONFIDENCE


def cuts(*parts):
    # A second of quiet first, so the background is known whatever the pause being tested.
    levels = segmenter.levels(speech_and_pauses(("pause", 1), *parts))
    return segmenter.cut_points(levels, 0, levels.size, final=False)


def test_a_pause_is_at_least_0_4_seconds():
    assert cuts(("speech", 2), ("pause", 0.35), ("speech", 5)) == []
    assert len(cuts(("speech", 2), ("pause", 0.45), ("speech", 5))) == 1


def test_the_last_3_seconds_stay_tentative():
    assert cuts(("speech", 2), ("pause", 1), ("speech", 2.8)) == []
    assert len(cuts(("speech", 2), ("pause", 1), ("speech", 3.3))) == 1


def test_a_recording_that_is_nearly_all_speech_still_finds_its_pauses():
    levels = segmenter.levels(speech_and_pauses(("speech", 2), ("pause", 0.45), ("speech", 5)))

    assert len(segmenter.cut_points(levels, 0, levels.size, final=False)) == 1


def test_speech_without_a_pause_is_cut_by_20_seconds():
    levels = segmenter.levels(tone(25) + noise(25))
    (cut,) = segmenter.cut_points(levels, 0, levels.size, final=True)
    assert 15 * FPS <= cut <= 20 * FPS


def test_a_correction_counts_when_its_value_starts_within_three_words_of_the_cue():
    assert "source_superseded" in own("Fever 3 days, sorry, it was 2 days.", "Fever 3 days.", "Fever 3 days").reasons
    assert own("Fever 3 days, sorry, it was only 2 days.", "Fever 3 days.", "Fever 3 days").pending


def test_an_instruction_reaches_at_most_14_words_when_nothing_ends_its_clause():
    words = " ".join(f"item{chr(97 + i)}" for i in range(20))
    text = transcript(f"order {words}")

    (instruction,) = not_carried_over(text)
    assert text.text[instruction["start"] : instruction["end"]].split() == ["order"] + words.split()[:14]


def test_a_possible_correction_is_flagged_within_8_words():
    assert validation.WARNING_GAP_WORDS == frozenset(
        "no wait sorry scratch mean correction actually rather make change".split())
    eight = "Heart rate 124 then no b c d e f 118."
    nine = "Heart rate 124 then no b c d e f g 118."

    assert field("Heart rate 124.", "Heart rate 124", text=transcript(eight)).warnings != []
    assert field("Heart rate 124.", "Heart rate 124", text=transcript(nine)).warnings == []


def test_an_unclear_passage_within_5_words_flags_the_value():
    five = transcript("Cough for three days a b c d e.", ("Fever since Tuesday.", "too_noisy"))
    six = transcript("Cough for three days a b c d e f.", ("Fever since Tuesday.", "too_noisy"))

    assert field("Cough for three days.", "Cough for three days", text=five).warnings != []
    assert field("Cough for three days.", "Cough for three days", text=six).warnings == []


def test_field_and_source_limits():
    words = "cough " * 599
    text = transcript(words)
    for name, limit in (("history", 3000), ("assessment", 2000), ("plan", 2000)):
        assert "too_long" not in field(words.strip()[:limit], words.strip()[:600], name=name, text=text).reasons
        assert "too_long" in field(words.strip()[: limit + 1], words.strip()[:600], name=name, text=text).reasons
    assert "too_long" in own(words, "cough cough", words.strip()[:601]).reasons
    assert "too_long" not in own(words, "cough cough", *["cough cough"] * 30).reasons
    assert "too_long" in own(words, "cough cough", *["cough cough"] * 31).reasons
