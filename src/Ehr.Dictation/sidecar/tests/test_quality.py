"""quality.py: passages too quiet, too noisy or too uncertain are marked; clear speech and silence are not."""

from __future__ import annotations

import numpy as np
from conftest import noise, tone

from ehr_dictation import quality, segmenter


def judge(samples: np.ndarray, recording: np.ndarray, confidence: float | None = 0.9, has_text: bool = True):
    return quality.assess(
        segmenter.levels(samples), segmenter.noise_floor(segmenter.levels(recording)), confidence, has_text
    )


def test_clear_speech_is_not_marked():
    speech = tone(2) + noise(2)

    assert judge(speech, np.concatenate([noise(1), speech])) is None


def test_quiet_speech_is_marked_too_quiet():
    speech = tone(2, level_db=-50) + noise(2, level_db=-80)

    assert judge(speech, np.concatenate([noise(1, level_db=-80), speech])) == quality.TOO_QUIET


def test_speech_barely_above_the_background_is_marked_too_noisy():
    background = noise(3, level_db=-26)
    speech = tone(2, level_db=-22) + noise(2, level_db=-26, seed=2)

    assert judge(speech, np.concatenate([background, speech])) == quality.TOO_NOISY


def test_speech_the_model_is_unsure_of_is_marked_low_confidence():
    speech = tone(2) + noise(2)

    assert judge(speech, np.concatenate([noise(1), speech]), confidence=0.3) == quality.LOW_CONFIDENCE


def test_silence_is_no_passage():
    assert quality.is_silence(segmenter.levels(noise(2, level_db=-75)))
    assert not quality.is_silence(segmenter.levels(tone(2, level_db=-50)))
    assert judge(noise(2), noise(3), confidence=None, has_text=False) is None


def test_a_breath_or_click_with_no_text_is_no_passage():
    click = np.concatenate([noise(1.5), tone(0.1, level_db=-40), noise(0.3)])

    assert judge(click, np.concatenate([noise(3), tone(3)]), confidence=None, has_text=False) is None


def test_sound_the_model_heard_nothing_in_is_marked():
    muffled = np.concatenate([noise(0.5), tone(1.5, level_db=-30), noise(0.5)])

    assert judge(muffled, np.concatenate([noise(3), tone(3)]), confidence=None, has_text=False) is not None
