"""rescore.py: a run's stored answers checked again offline give the same results under the same rules, and a run made
with another prompt is refused."""

from __future__ import annotations

import asyncio
import dataclasses

import pytest
from conftest import ScriptedRecognizer, fake_structurer
from test_run import speech, webm

from ehr_dictation import prompt
from ehr_eval import rescore, run, scoring
from ehr_eval.scripts import gold_sha256, pipeline_sha256, recordings_sha256, scoring_sha256


def a_run(tmp_path, scripts) -> dict:
    (tmp_path / "d02.webm").write_bytes(webm(speech(4)))
    variant = {"id": "d02--clean", "script": "d02", "file": "d02.webm", "container": "webm", "accent": "gb",
               "noise": None, "snr_db": None}
    heard = "Takes aprocitentan daily {period} Go ahead and order a chest X-ray {period}"
    structurer = fake_structurer({
        "subjective": {"value": "Takes aprocitentan daily.", "sources": ["Takes aprocitentan daily"]},
        "objective": {"value": "", "sources": []},
        "assessment": {"value": "", "sources": []},
        "plan": {"value": "Chest X-ray.", "sources": ["order a chest X-ray"]},
    })
    item = asyncio.run(run.run_variant(variant, tmp_path, ScriptedRecognizer(heard), structurer, scripts))
    return {
        "run": {"parameters": prompt.PARAMETERS},
        "provenance": {"structuring": prompt.load().structuring_sha256, "pipeline": pipeline_sha256(), "scoring": "old"},
        "dataset": {"recordings_sha256": recordings_sha256(), "gold_sha256": "old"},
        "metrics": scoring.score([item], scripts),
        "items": [dataclasses.asdict(item)],
    }


def test_the_same_rules_give_the_same_results(tmp_path, scripts, monkeypatch):
    monkeypatch.setattr(rescore, "load_all", lambda: scripts)
    results = a_run(tmp_path, scripts)

    again = rescore.rescore(results)

    assert again["metrics"] == results["metrics"]
    assert [i["fields"] for i in again["items"]] == [i["fields"] for i in results["items"]]
    assert again["provenance"]["scoring"] == scoring_sha256() and again["rescored_from"]["scoring"] == "old"
    assert again["provenance"]["pipeline"] == pipeline_sha256()
    assert again["dataset"]["gold_sha256"] == gold_sha256() and again["rescored_from"]["gold_sha256"] == "old"


def test_the_instructions_reported_and_proposed_and_the_scripts_tags_are_the_current_ones(tmp_path, scripts, monkeypatch):
    monkeypatch.setattr(rescore, "load_all", lambda: scripts)
    results = a_run(tmp_path, scripts)
    stored = results["items"][0]
    expected = ([list(entry) for entry in stored["not_carried_over"]], stored["proposals"], stored["tags"])
    assert expected[0] and expected[1] and len(expected[2]) > 3
    stored["not_carried_over"] = []  # as rules that missed the instruction would have left it
    stored["proposals"] = []
    stored["tags"] = [*stored["tags"][:3], "a-tag-since-removed"]

    again = rescore.rescore(results)["items"][0]

    assert ([list(entry) for entry in again["not_carried_over"]], again["proposals"], again["tags"]) == expected


def test_answers_from_other_recordings_are_refused(tmp_path, scripts):
    results = a_run(tmp_path, scripts)
    results["dataset"]["recordings_sha256"] = "0" * 64

    with pytest.raises(ValueError, match="other recordings"):
        rescore.rescore(results)


def test_answers_from_other_code_making_them_are_refused(tmp_path, scripts):
    results = a_run(tmp_path, scripts)
    results["provenance"]["pipeline"] = "0" * 64

    with pytest.raises(ValueError, match="other code making the transcript"):
        rescore.rescore(results)


def test_answers_to_another_prompt_are_refused(tmp_path, scripts):
    results = a_run(tmp_path, scripts)
    results["provenance"]["structuring"] = "0" * 64

    with pytest.raises(ValueError, match="run the eval again"):
        rescore.rescore(results)
