"""run_a_en.py, run A's English counterpart (the expansion plan's step 17), with a stand-in for llama-server answering
each English script with its rule-following gold: the English text sorted, checked and set against run A's stop
thresholds line by line, scored again without the model, a run that isn't whole refused, and the two sets side by
side."""

from __future__ import annotations

import json

import httpx2
import pytest

from dictation_scripts_es import load_scripts as load_spanish
from ehr_dictation.llama import LlamaServer
from ehr_dictation.provenance import Models
from ehr_eval import run_a_en, scoring, scoring_es
from ehr_eval.scripts import load_all

ENGLISH = load_all()
SPANISH = {script.id: script for script in load_spanish()}


def gold_for(sent: str) -> dict:
    """The rule-following gold of the script whose display text was sent."""
    (script,) = [s for s in ENGLISH.values() if " ".join(s.display.split()) in " ".join(sent.split())]
    return {name: {"value": g.value, "sources": list(g.sources)} for name, g in script.gold_rules.items()}


@pytest.fixture
def english_run(tmp_path, monkeypatch):  # noqa: ANN001, ANN201
    async def handle(request: httpx2.Request) -> httpx2.Response:
        if request.url.path == "/props":
            return httpx2.Response(200, json={"model_path": "/models/K2.gguf", "build_info": "b11669-test",
                                              "default_generation_settings": {"n_ctx": 8192}})
        if request.url.path == "/tokenize":
            return httpx2.Response(200, json={"tokens": list(range(len(json.loads(request.content)["content"].split())))})
        sent = json.loads(request.content)["messages"][-1]["content"]
        return httpx2.Response(200, json={"choices": [{"finish_reason": "stop",
                                                       "message": {"content": json.dumps(gold_for(sent))}}]})

    monkeypatch.setattr(run_a_en, "LlamaServer",
                        lambda url, key: LlamaServer(url, key, transport=httpx2.MockTransport(handle)))
    monkeypatch.setattr(run_a_en.provenance, "load", lambda models_dir, verify: Models(
        {"repository": "google/medasr", "commit": "a" * 40, "sha256": "b" * 64},
        {"repository": "IFM/K2-Horizon-7B-GGUF", "commit": "c" * 40, "file": "K2.gguf", "sha256": "d" * 64}))
    monkeypatch.setenv("LLAMA_API_KEY", "key")

    def run(*only: str) -> dict:
        out = tmp_path / "run-a-en.json"
        assert run_a_en.main(["--llama-url", "http://llama.test", "--models-dir", str(tmp_path), "--machine",
                              "stand-ins", "--out", str(out), *(["--only", *only] if only else [])]) == 0
        return json.loads(out.read_text(encoding="utf-8"))

    return run


def test_the_english_text_is_sorted_checked_and_set_against_run_as_thresholds(english_run, capsys):
    results = english_run()

    assert results["set"] == "en" and sorted(item["script"] for item in results["items"]) == sorted(ENGLISH)
    metrics = results["metrics"]
    assert metrics["sorting"]["unavailable"] == {} and metrics["order_allergy"]["shown"] == 0
    assert metrics["fields"]["content"]["f1"] == 1.0 and metrics["misfiled"]["total"] == 0
    first, *rest = results["thresholds"]
    assert first["crossed"] is None and first["measured"].startswith("not applicable")
    assert len(results["thresholds"]) == len(scoring_es.thresholds(scoring_es.score([], SPANISH)))
    assert not any(line["crossed"] for line in rest)
    printed = capsys.readouterr().out
    assert "n/a" in printed and "INCOMPLETE" not in printed


def test_a_partial_english_run_says_so_and_isnt_scored_again(english_run, tmp_path, capsys):
    results = english_run("d01", "d02")
    assert "INCOMPLETE, not a whole run of the English set" in capsys.readouterr().out

    stored = tmp_path / "partial.json"
    stored.write_text(json.dumps(results), encoding="utf-8")
    assert run_a_en.main(["--rescore", str(stored), "--out", str(tmp_path / "again.json")]) == 2
    assert "isn't a whole run of the English set" in capsys.readouterr().err
    assert not (tmp_path / "again.json").exists()


def test_a_whole_english_run_is_scored_again_without_the_model(english_run, tmp_path):
    stored = tmp_path / "whole.json"
    stored.write_text(json.dumps(english_run()), encoding="utf-8")

    assert run_a_en.main(["--rescore", str(stored), "--out", str(tmp_path / "again.json")]) == 0
    again = json.loads((tmp_path / "again.json").read_text(encoding="utf-8"))
    assert again["rescored"] and again["metrics"]["fields"]["content"]["f1"] == 1.0 and again["thresholds"]


def test_a_script_that_timed_out_is_a_failure_in_every_english_measure():
    script = ENGLISH["d02-t2dm-bexagliflozin"]
    timed_out = scoring.Item(variant="d02--text", script=script.id, tags=[], seconds=0.0, hypothesis=script.display,
                             sorting="unavailable", reason="model_timeout")
    metrics = scoring.score([timed_out], {script.id: script})

    with_gold = sum(bool(g.value) for g in script.gold_rules.values())
    assert metrics["discarded"]["discarded"] == metrics["discarded"]["fields_with_gold"] == with_gold
    assert metrics["fields"]["content"]["f1"] == 0.0
    crossed = [crossed for _, crossed, _ in run_a_en.thresholds(metrics)]
    assert crossed == [None, False, False, True, True, True]


def test_the_two_sets_are_set_side_by_side_line_by_line(tmp_path, capsys):
    script = ENGLISH["d02-t2dm-bexagliflozin"]
    item = scoring.Item(variant="d02--text", script=script.id, tags=[], seconds=0.0, hypothesis=script.display,
                        sorting="done", fields={name: scoring.FieldOutcome(proposed=g.value, sources=list(g.sources),
                                                                           status="pending", value=g.value)
                                                for name, g in script.gold_rules.items()})
    (tmp_path / "en.json").write_text(json.dumps({"metrics": scoring.score([item], {script.id: script})}))
    timed_out = scoring_es.SpanishItem(variant="e14-nino-sano--text", script="e14-nino-sano", tags=[], hypothesis="",
                                       sorting="unavailable", reason="model_timeout")
    (tmp_path / "es.json").write_text(json.dumps({"metrics": scoring_es.score([timed_out], SPANISH)}))

    assert run_a_en.main(["--compare", str(tmp_path / "en.json"), str(tmp_path / "es.json")]) == 0
    table = capsys.readouterr().out
    first, misfiled, *_, sorting = table.split("\n\n")[1:]
    assert "English: n/a: not applicable" in first and "Spanish: ok: none" in first
    assert "English: ok: 0 value(s)" in misfiled and "Spanish: ok: Spanish 0 of 0 statements (None)" in misfiled
    assert "; English " in misfiled and "cap " in misfiled  # capped at the English text run's committed rate
    assert "English: ok: unavailable none" in sorting and "Spanish: CROSSED: unavailable {'model_timeout': 1}" in sorting


def test_measures_from_before_this_scorer_are_refused_for_the_table(tmp_path, capsys):
    (tmp_path / "en.json").write_text(json.dumps({"metrics": {"fields": {}}}))
    (tmp_path / "es.json").write_text(json.dumps({"metrics": {"mistranslations": {}}}))

    assert run_a_en.main(["--compare", str(tmp_path / "en.json"), str(tmp_path / "es.json")]) == 2
    assert "score it again with this scorer first" in capsys.readouterr().err
