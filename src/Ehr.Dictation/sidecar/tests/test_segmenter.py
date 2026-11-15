"""segmenter.py: where a growing recording is cut, so committed passages are never transcribed again."""

from __future__ import annotations

import numpy as np
from conftest import noise, speech_and_pauses, tone

from ehr_dictation import segmenter
from ehr_dictation.audio import SAMPLE_RATE

FPS = segmenter.FRAMES_PER_SECOND


def test_levels_are_dbfs_for_each_10_ms():
    levels = segmenter.levels(tone(1.0, level_db=-20))

    assert levels.size == 1 + (SAMPLE_RATE - segmenter.WINDOW) // segmenter.HOP
    assert np.allclose(levels, -20, atol=0.5)


def test_commits_at_a_pause_well_before_the_end():
    samples = speech_and_pauses(("speech", 2), ("pause", 1), ("speech", 2), ("pause", 1), ("speech", 5))
    levels = segmenter.levels(samples)

    cuts = segmenter.cut_points(levels, 0, levels.size, final=False)

    assert len(cuts) == 2
    assert 2 * FPS < cuts[0] < 3 * FPS
    assert 5 * FPS < cuts[1] < 6 * FPS


def test_the_last_3_seconds_stay_tentative_while_recording():
    samples = speech_and_pauses(("speech", 3), ("pause", 1), ("speech", 3), ("pause", 0.6), ("speech", 2))
    levels = segmenter.levels(samples)

    cuts = segmenter.cut_points(levels, 0, levels.size, final=False)

    # The second pause ends 2 s before the end of the audio: the speaker may only be catching their breath.
    assert len(cuts) == 1


def test_stopping_cuts_at_every_pause():
    samples = speech_and_pauses(("speech", 3), ("pause", 1), ("speech", 3), ("pause", 0.6), ("speech", 2))
    levels = segmenter.levels(samples)

    assert len(segmenter.cut_points(levels, 0, levels.size, final=True)) == 2


def test_long_speech_without_a_pause_is_cut_at_its_quietest_moment():
    samples = np.concatenate([tone(17), tone(0.15, level_db=-35), tone(13)]) + noise(30.15)
    levels = segmenter.levels(samples)

    cuts = segmenter.cut_points(levels, 0, levels.size, final=True)

    assert 17 * FPS <= cuts[0] <= 17.15 * FPS + 1
    assert all(b - a <= segmenter.MAX_SEGMENT for a, b in zip([0, *cuts], [*cuts, levels.size], strict=True))


def test_cuts_start_after_what_is_already_committed():
    samples = speech_and_pauses(("speech", 2), ("pause", 1), ("speech", 2), ("pause", 1), ("speech", 5))
    levels = segmenter.levels(samples)

    cuts = segmenter.cut_points(levels, int(2.5 * FPS), levels.size, final=False)

    assert len(cuts) == 1 and cuts[0] > 5 * FPS
