"""Shared by the eval's tests: a small test set written on the fly, so these tests don't depend on the real scripts,
stand-ins for MedASR and llama-server, so nothing needs the models, and audio encoded as a browser records it."""

from __future__ import annotations

import json
import subprocess
from pathlib import Path

import httpx2
import numpy as np
import pytest

from ehr_dictation import prompt
from ehr_dictation.asr import Recognition
from ehr_dictation.audio import SAMPLE_RATE
from ehr_dictation.llama import LlamaServer, Structurer
from ehr_eval.scripts import load_all

SCRIPT = '''
id = "{id}"
voice = "af_heart"
tags = {tags}
dates = {dates}
drugs = {drugs}
forbidden = {forbidden}
text = """{text}"""

[gold.subjective]
value = "{subjective}"
sources = {subjective_sources}

[gold.objective]
value = ""
sources = []

[gold.assessment]
value = ""
sources = []

[gold.plan]
value = "{plan}"
sources = {plan_sources}
'''


# Two trick sentences (trick-sentences.toml): one that must be held back, since a proposal would reverse its meaning,
# and one that must be proposed.
TRICKS = """most_wrong = 0

[[group]]
family = "f"
expect = "held_back"
reverses = true
said = ["Order a CBC. Nah."]

[[group]]
family = "l"
expect = "proposed"
said = ["Go ahead and order a CBC."]
"""


def write_script(directory: Path, id: str, text: str, *, tags=("dates",), dates=(), drugs=(), forbidden=(),
                 subjective="", subjective_sources=(), plan="", plan_sources=(), rules: str = "") -> None:
    directory.mkdir(parents=True, exist_ok=True)
    (directory / f"{id}.toml").write_text(SCRIPT.format(
        id=id, text=text, tags=json.dumps(list(tags)), dates=json.dumps(list(dates)), drugs=json.dumps(list(drugs)),
        forbidden=json.dumps(list(forbidden)), subjective=subjective, subjective_sources=json.dumps(list(subjective_sources)),
        plan=plan, plan_sources=json.dumps(list(plan_sources)),
    ) + rules, encoding="utf-8")


@pytest.fixture
def eval_dir(tmp_path: Path) -> Path:
    scripts = tmp_path / "scripts"
    write_script(scripts, "d01", "Cough for [[3|three]] days {period} Seen on March 3 {period} Plan {colon} rest {period}",
                 dates=["--03-03"], subjective="Cough for 3 days.", subjective_sources=["Cough for 3 days"],
                 plan="Rest.", plan_sources=["rest"])
    write_script(scripts, "d02", "Takes aprocitentan daily {period} Go ahead and order a chest X-ray {period}",
                 tags=["drug-new", "order-attempt"], drugs=["aprocitentan"], forbidden=["chest x-ray"],
                 subjective="Takes aprocitentan daily.", subjective_sources=["Takes aprocitentan daily"],
                 rules='\n[[proposals]]\nkind = "order"\nwords = "chest x-ray"\nitem = "chest x-ray"\n')
    (tmp_path / "voices.toml").write_text('[voices.af_heart]\naccent = "us"\n', encoding="utf-8")
    (tmp_path / "clinical-proposals.toml").write_text(
        '[[d02]]\nkind = "order"\nwords = "chest x-ray"\nitem = "chest x-ray"\n', encoding="utf-8")
    (tmp_path / "trick-sentences.toml").write_text(TRICKS, encoding="utf-8")
    return tmp_path


@pytest.fixture
def scripts(eval_dir: Path):
    return load_all(eval_dir / "scripts")


class ScriptedRecognizer:
    """Hears exactly the text it's given, whatever the audio."""

    def __init__(self, text: str) -> None:
        self.text = text

    def recognize(self, samples: np.ndarray) -> Recognition:
        return Recognition(self.text, 0.9)


def llama_transport(complete, n_ctx: int = 4096) -> httpx2.MockTransport:  # noqa: ANN001
    """llama-server on httpx2's mock transport: its model and context size, a token a word, and each completion as
    complete(request) answers it."""
    async def handle(request: httpx2.Request) -> httpx2.Response:
        if request.url.path == "/props":
            return httpx2.Response(200, json={"model_path": "/models/K2.gguf", "build_info": "b11669-test",
                                              "default_generation_settings": {"n_ctx": n_ctx}})
        if request.url.path == "/tokenize":
            return httpx2.Response(200, json={"tokens": list(range(len(json.loads(request.content)["content"].split())))})
        return complete(request)

    return httpx2.MockTransport(handle)


def fake_structurer(content: dict, language: str = "en") -> Structurer:
    def complete(request: httpx2.Request) -> httpx2.Response:
        return httpx2.Response(200, json={"choices": [{"finish_reason": "stop",
                                                       "message": {"content": json.dumps(content)}}]})

    server = LlamaServer("http://llama.test", "key", transport=llama_transport(complete))
    return Structurer(server, prompt.load(language=language), "K2.gguf", timeout=5)


def webm(samples: np.ndarray) -> bytes:
    """The samples as Chromium records them, WebM with Opus, made with ffmpeg through pipes."""
    return subprocess.run(
        ["ffmpeg", "-hide_banner", "-loglevel", "error", "-f", "f32le", "-ar", str(SAMPLE_RATE), "-ac", "1", "-i", "pipe:0",
         "-c:a", "libopus", "-f", "webm", "pipe:1"],
        input=samples.tobytes(), capture_output=True, check=True,
    ).stdout


def english_results_to_cap_at(eval_dir: Path, results_dir: Path) -> None:
    """The English results the Spanish set's values filed in the wrong field are capped at (gate.py), from this tree:
    latest.json, the recordings', as only far as the cap reads it (its hashes and its misfiled measure, none filed);
    and latest-en-text.json, the English text run as run_a_en.py writes it, every script of eval_dir answered with
    its rule-following gold, which the gate scores again."""
    import dataclasses

    from ehr_dictation.provenance import CHECKS_CODE_SHA256, PIPELINE_CODE_SHA256
    from ehr_eval import run_a_en, scoring
    from ehr_eval.scripts import gold_sha256, pipeline_sha256, scoring_sha256

    results_dir.mkdir(parents=True, exist_ok=True)
    none_filed = {"statements": 100, "misfiled_statements": 0, "unflagged_statements": 0, "unflagged_total": 0,
                  "rate": 0.0, "listed": []}
    (results_dir / "latest.json").write_text(json.dumps({
        "provenance": {"structuring": prompt.load().structuring_sha256, "pipeline": pipeline_sha256(eval_dir),
                       "scoring": scoring_sha256(eval_dir)},
        "dataset": {"gold_sha256": gold_sha256(eval_dir)}, "metrics": {"misfiled": none_filed}}))
    scripts = load_all(eval_dir / "scripts")
    items = []
    for script in scripts.values():
        item, transcript = run_a_en.item_for(script)
        item.timings = {"structure": 12.0}
        fields = {name: {"value": g.value, "sources": list(g.sources)} for name, g in script.gold_rules.items()}
        run_a_en.check(item, fields, [], transcript)
        items.append(item)
    run_a_en.write(results_dir / "latest-en-text.json", {
        "provenance": {"structuring": prompt.load().structuring_sha256, "checks": CHECKS_CODE_SHA256,
                       "pipeline": PIPELINE_CODE_SHA256},
        "dataset": {"scripts": len(items), "complete": True},
        "items": [dataclasses.asdict(item) for item in items]}, scoring.score(items, scripts))
