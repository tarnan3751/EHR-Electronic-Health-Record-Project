"""audio.py: every container a browser records decodes to 16 kHz mono, a recording still growing decodes as far as it
goes, and nothing touches the disk."""

from __future__ import annotations

import numpy as np
import pytest
from conftest import encode, tone

from ehr_dictation.audio import SAMPLE_RATE, AudioError, decode


@pytest.mark.parametrize("container", ["webm", "ogg", "mp4", "wav"])
def test_decodes_each_container_browsers_record_to_16k_mono(container):
    samples = decode(encode(tone(2.0, level_db=-20), container))

    assert samples.dtype == np.float32
    assert abs(samples.size / SAMPLE_RATE - 2.0) < 0.1
    level = 20 * np.log10(np.sqrt(np.mean(samples[SAMPLE_RATE // 4 : -SAMPLE_RATE // 4] ** 2)))
    assert abs(level - -20) < 1.5


@pytest.mark.parametrize("container", ["webm", "ogg", "mp4"])
def test_decodes_the_complete_part_of_a_recording_still_being_made(container):
    data = encode(tone(4.0), container)

    samples = decode(data[: len(data) // 2], partial=True)

    assert 0.5 < samples.size / SAMPLE_RATE < 3.5


def test_a_growing_recording_decodes_to_the_same_samples_up_to_where_it_was_cut():
    data = encode(tone(6.0), "webm")
    whole = decode(data)

    part = decode(data[: len(data) * 2 // 3], partial=True)
    settled = part.size - SAMPLE_RATE

    assert settled > SAMPLE_RATE
    assert np.allclose(whole[:settled], part[:settled], atol=1e-4)


def test_an_opening_chunk_too_short_to_decode_gives_no_samples_yet():
    data = encode(tone(2.0), "webm")

    assert decode(data[:100], partial=True).size == 0


def test_refuses_bytes_that_are_not_audio_without_repeating_ffmpeg():
    with pytest.raises(AudioError) as refused:
        decode(b"this is not a recording of anything " * 50)

    assert "Invalid" not in str(refused.value)
    assert "exit code" in str(refused.value)


def test_nothing_is_written_to_disk(tmp_path, monkeypatch):
    monkeypatch.setenv("TMPDIR", str(tmp_path))
    monkeypatch.chdir(tmp_path)

    decode(encode(tone(1.0), "mp4"))
    decode(encode(tone(1.0), "webm")[:2000], partial=True)

    assert list(tmp_path.iterdir()) == []
