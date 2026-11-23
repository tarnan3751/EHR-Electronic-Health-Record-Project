"""run.py and answers.py: one recording through the sidecar's own pipeline, with stand-in models, comes back as a
scored item, a two-minute recording is also timed chunk by chunk as a live dictation is, and a run writes its results
as it goes."""

from __future__ import annotations

import argparse
import asyncio
import json
import subprocess

import numpy as np
import pytest
from conftest import ScriptedRecognizer, fake_structurer

from ehr_dictation.audio import SAMPLE_RATE
from ehr_dictation.provenance import Models
from ehr_eval import answers, rescore, run
from ehr_eval.scoring import score
from ehr_eval.scripts import load_all


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
        "subjective": {"value": "Takes aprocitentan daily.", "sources": ["Takes aprocitentan daily"]},
        "objective": {"value": "", "sources": []},
        "assessment": {"value": "", "sources": []},
        "plan": {"value": "Chest X-ray.", "sources": ["order a chest X-ray"]},
    })

    item = asyncio.run(run.run_variant(variant, tmp_path, ScriptedRecognizer(heard), structurer, scripts))

    assert item.tags == ["accent:gb", "noise:none", "container:webm", "drug-new", "order-attempt"]
    assert item.hypothesis.startswith("Takes aprocitentan daily.")
    assert item.fields["subjective"].status == "pending"
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
        "subjective": {"value": "", "sources": []},
        "objective": {"value": "", "sources": []},
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

    times = asyncio.run(answers.live_chunks(data, 6.0, ScriptedRecognizer("speech"), fake_structurer({}), models))

    assert len(times) >= 6 and all(t >= 0 for t in times)


# The run writes its results as it goes, each recording's answer as K2 Horizon gave it before any check: a fault in the
# checks or the scoring (step 16's run lost everything to one) leaves the answers so far, which rescore.py finishes.
def english_run(tmp_path, monkeypatch, fault: str):  # noqa: ANN001, ANN201
    script = next(iter(load_all().values()))
    (tmp_path / "one.webm").write_bytes(webm(speech(4)))
    variant = {"id": f"{script.id}--clean", "script": script.id, "file": "one.webm", "container": "webm", "accent": "us",
               "noise": None, "snr_db": None}
    (tmp_path / "manifest.json").write_text(json.dumps({"variants": [variant], "generator": {}}), encoding="utf-8")
    said = script.gold["subjective"].sources[0]  # the clinician's own words, which the checks let through
    structurer = fake_structurer({name: {"value": said if name == "subjective" else "",
                                         "sources": [said] if name == "subjective" else []}
                                  for name in ("subjective", "objective", "assessment", "plan")})
    models = Models({"repository": "r", "commit": "c", "sha256": "s"}, None)
    monkeypatch.setattr(run, "open_pipeline", lambda args, api_key: (
        models, ScriptedRecognizer(script.display), structurer._llama, structurer.structuring, structurer))
    monkeypatch.setenv("LLAMA_API_KEY", "key")

    def fails(*args, **kwargs):  # noqa: ANN002, ANN003, ANN202
        raise RuntimeError(f"a fault in the {fault}")

    monkeypatch.setattr(*{"scoring": (run.scoring, "score"), "checks": (run.validation, "check")}[fault], fails)
    out = tmp_path / "latest.json"
    args = argparse.Namespace(set="en", audio=tmp_path, out=out, only=None, reasoning="off", device="cpu",
                              machine="stand-ins", no_live=True, models_dir=tmp_path, llama_url="http://llama.test")
    with pytest.raises(RuntimeError, match=f"a fault in the {fault}"):
        asyncio.run(run.main_async(args))
    return json.loads(out.read_text(encoding="utf-8"))


def test_a_fault_in_the_scoring_leaves_every_answer_for_rescore_to_finish(tmp_path, monkeypatch):
    stored = english_run(tmp_path, monkeypatch, "scoring")
    (item,) = stored["items"]
    assert "metrics" not in stored and item["fields"]["subjective"]["status"] == "pending"

    monkeypatch.undo()
    finished = rescore.rescore(stored)
    assert finished["metrics"]["validation"]["passed"] == 1


def test_a_fault_in_the_checks_leaves_the_answer_as_k2_horizon_gave_it(tmp_path, monkeypatch):
    stored = english_run(tmp_path, monkeypatch, "checks")
    (item,) = stored["items"]
    assert item["fields"]["subjective"]["proposed"] and item["fields"]["subjective"]["status"] == "empty"

    monkeypatch.undo()
    assert rescore.rescore(stored)["items"][0]["fields"]["subjective"]["status"] == "pending"
