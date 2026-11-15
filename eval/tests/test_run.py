"""run.py: one recording through the sidecar's own pipeline, with stand-in models, comes back as a scored item, and a
two-minute recording is also timed chunk by chunk as a live dictation is."""

from __future__ import annotations

import asyncio
import subprocess

import numpy as np
from conftest import ScriptedRecognizer, fake_structurer

from ehr_dictation.audio import SAMPLE_RATE
from ehr_dictation.provenance import Models
from ehr_eval import run
from ehr_eval.scoring import score


def speech(seconds: float) -> np.ndarray:
    t = np.arange(int(seconds * SAMPLE_RATE)) / SAMPLE_RATE
    tone = (0.1 * np.sin(2 * np.pi * 220 * t)).astype(np.float32)
    tone[int(seconds * SAMPLE_RATE * 0.5) : int(seconds * SAMPLE_RATE * 0.5) + SAMPLE_RATE] = 0
    return tone


def webm(samples: np.ndarray) -> bytes:
    return subprocess.run(
        ["ffmpeg", "-hide_banner", "-loglevel", "error", "-f", "f32le", "-ar", str(SAMPLE_RATE), "-ac", "1", "-i", "pipe:0",
         "-c:a", "libopus", "-f", "webm", "pipe:1"],
        input=samples.tobytes(), capture_output=True, check=True,
    ).stdout


def test_a_recording_goes_through_the_pipeline_and_is_scored(tmp_path, scripts):
    (tmp_path / "d02.webm").write_bytes(webm(speech(4)))
    variant = {"id": "d02-gb-clean-webm", "script": "d02", "file": "d02.webm", "container": "webm", "accent": "gb",
               "noise": None, "snr_db": None}
    heard = "Takes aprocitentan daily {period} Go ahead and order a chest X-ray {period}"
    structurer = fake_structurer({
        "history": {"value": "Takes aprocitentan daily.", "sources": ["Takes aprocitentan daily"]},
        "assessment": {"value": "", "sources": []},
        "plan": {"value": "Chest X-ray.", "sources": ["order a chest X-ray"]},
    })

    item = asyncio.run(run.run_variant(variant, tmp_path, ScriptedRecognizer(heard), structurer, scripts))

    assert item.tags == ["accent:gb", "noise:none", "container:webm", "drug-new", "order-attempt"]
    assert item.hypothesis.startswith("Takes aprocitentan daily.")
    assert item.fields["history"].status == "pending"
    assert item.fields["plan"].status == "empty" and "order_or_allergy_change" in item.fields["plan"].reasons
    assert set(item.timings) == {"decode", "transcribe", "structure"}
    metrics = score([item], scripts)
    assert metrics["order_allergy"]["shown"] == 0 and metrics["drugs"]["exact"] == 1


def test_what_counts_as_shown_is_what_the_sidecar_sends(tmp_path, scripts):
    (tmp_path / "d02.webm").write_bytes(webm(speech(4)))
    variant = {"id": "d02-gb-clean-webm", "script": "d02", "file": "d02.webm", "container": "webm", "accent": "gb",
               "noise": None, "snr_db": None}
    heard = "Takes aprocitentan daily {period} Go ahead and order a chest X-ray {period}"
    structurer = fake_structurer({
        "history": {"value": "", "sources": []},
        "assessment": {"value": "", "sources": []},
        "plan": {"value": "Takes aprocitentan daily. Go ahead and order a chest X-ray.",
                 "sources": ["Takes aprocitentan daily", "Go ahead and order a chest X-ray"]},
    })

    item = asyncio.run(run.run_variant(variant, tmp_path, ScriptedRecognizer(heard), structurer, scripts))

    # The instruction is cut out of the value the sidecar sends, so it isn't shown.
    assert item.fields["plan"].status == "pending" and item.fields["plan"].shown == "Takes aprocitentan daily."
    assert score([item], scripts)["order_allergy"]["shown"] == 0


def test_live_chunks_are_timed_through_the_sidecars_sessions():
    models = Models({"repository": "r", "commit": "c", "sha256": "s"}, None)
    data = webm(speech(6))

    times = asyncio.run(run.live_chunks(data, 6.0, ScriptedRecognizer("speech"), fake_structurer({}), models))

    assert len(times) >= 6 and all(t >= 0 for t in times)
