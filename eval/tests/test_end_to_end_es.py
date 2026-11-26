"""The Spanish eval from end to end over stand-ins (the expansion plan's step 17): run_es.py runs every recording and
text the test set defines through the sidecar's own pipeline, with stand-ins for Whisper, MedASR and llama-server,
writes the results as it goes, scores them, and the gate scores them again and judges them. The results hold every
shape the Spanish path can give: a passage heard as another language, a field refused for each of the Spanish checks'
reasons (translation.REASONS, so a reason added there is covered here), a statement the English backstop drops, and an
item not sorted. A fault in the scoring leaves the answers in the file, for --rescore to finish."""

from __future__ import annotations

import argparse
import asyncio
import dataclasses
import json
import subprocess
from pathlib import Path

import httpx2
import numpy as np
import pytest
from conftest import ScriptedRecognizer, english_results_to_cap_at

from dictation_scripts import load_scripts as load_english
from dictation_scripts_es import load_scripts, runs
from ehr_dictation import prompt, translation
from ehr_dictation.asr import Recognition
from ehr_dictation.audio import SAMPLE_RATE
from ehr_dictation.llama import LlamaServer, Structurer
from ehr_dictation.prompt import FIELDS
from ehr_dictation.quality import OTHER_LANGUAGE
from ehr_dictation.whisper_asr import Heard
from ehr_eval import answers_es, gate, run_es, scoring_es
from ehr_eval.scripts import EVAL_DIR, variant_ids
from test_run_es import MODELS

SPANISH = {s.id: s for s in load_scripts()}
ENGLISH = {s.id: s for s in load_english()}
NOT_SORTED = "e05-asma-nino"  # its recording's answer isn't the schema's shape
BACKSTOP = "e01-hta-control"  # its answers add a statement that is only an English instruction


def first_sentence(text: str) -> str:
    return text.split(".")[0]


def script_of(text: str) -> str | None:
    return next((id for id, s in (SPANISH | ENGLISH).items() if first_sentence(s.display) in text), None)


def passages(script) -> list[tuple[str, str]]:  # noqa: ANN001
    return runs(script.text, display=True) if script.id in SPANISH else [("en", script.display)]


def audio(bursts: int) -> bytes:
    """A second and a half of sound, then a second of silence, for each passage."""
    one = np.concatenate([0.1 * np.sin(2 * np.pi * 220 * np.arange(int(1.5 * SAMPLE_RATE)) / SAMPLE_RATE),
                          np.zeros(SAMPLE_RATE)])
    samples = np.tile(one, bursts).astype(np.float32)
    return subprocess.run(
        ["ffmpeg", "-hide_banner", "-loglevel", "error", "-f", "f32le", "-ar", str(SAMPLE_RATE), "-ac", "1", "-i", "pipe:0",
         "-c:a", "libopus", "-f", "webm", "pipe:1"], input=samples.tobytes(), capture_output=True, check=True).stdout


class Whisper:
    """Hears each passage of each recording in the order the run plays them: Spanish as Spanish, English as another
    language, as the script says it."""

    def __init__(self, heard: list[tuple[str, str]]) -> None:
        self.heard = list(heard)

    def hear(self, samples) -> Heard:  # noqa: ANN001
        language, text = self.heard.pop(0)
        if language == "es":
            return Heard(Recognition(text, None), 0.97, 0.01, -0.2)
        return Heard(Recognition(text, None, OTHER_LANGUAGE), 0.12, 0.01, -0.3)

    def recognize(self, samples) -> Recognition:  # noqa: ANN001
        return self.hear(samples).recognition


def llama(answer) -> LlamaServer:  # noqa: ANN001
    async def handle(request: httpx2.Request) -> httpx2.Response:
        if request.url.path == "/props":
            return httpx2.Response(200, json={"model_path": "/models/K2.gguf", "build_info": "b11669-test",
                                              "default_generation_settings": {"n_ctx": 8192}})
        if request.url.path == "/tokenize":
            return httpx2.Response(200, json={"tokens": list(range(len(json.loads(request.content)["content"].split())))})
        sent = json.loads(request.content)["messages"][-1]["content"]
        return httpx2.Response(200, json={"choices": [{"finish_reason": "stop", "message": {"content": answer(sent)}}]})

    return LlamaServer("http://llama.test", "key", transport=httpx2.MockTransport(handle))


def spanish_answer(asked: dict[str, int]):  # noqa: ANN201
    """K2 Horizon answering each script with its gold; the first recording of NOT_SORTED with something not the
    schema's shape; BACKSTOP with one statement more, an English instruction resting on a heading."""
    def answer(sent: str) -> str:
        script = script_of(sent)
        if script not in SPANISH:
            return json.dumps({"statements": []})
        asked[script] = asked.get(script, 0) + 1
        if script == NOT_SORTED and asked[script] == 1:
            return "not the schema's shape"
        statements = [{"spanish": list(s.spanish), "field": name, "english": s.english}
                      for name, said in SPANISH[script].statements.items() for s in said]
        if script == BACKSTOP:
            statements.append({"spanish": ["Control de hipertensión."], "field": "plan", "english": "Order an A1c today."})
        return json.dumps({"statements": statements})
    return answer


def refusing(real):  # noqa: ANN001, ANN201
    """The Spanish checks, with every reason they can give put on a field the gold fills, one reason to a field in
    script order (any left over on the last): the shapes a refused field takes, whatever the reason."""
    slots = [(id, name) for id, script in sorted(SPANISH.items()) for name in FIELDS if script.statements[name]]
    assigned: dict[tuple[str, str], list[str]] = {}
    for k, reason in enumerate(translation.REASONS):
        assigned.setdefault(slots[min(k, len(slots) - 1)], []).append(reason)

    def check(fields, unknown, transcript):  # noqa: ANN001, ANN202
        checks = real(fields, unknown, transcript)
        script = script_of(transcript.text)
        for (id, name), reasons in assigned.items():
            if id == script and name in checks.fields:
                checks.fields[name] = dataclasses.replace(checks.fields[name], reasons=reasons)
        return checks
    return check


@dataclasses.dataclass
class Run:
    path: Path
    pipeline: answers_es.Pipeline


@pytest.fixture
def run(tmp_path, monkeypatch) -> Run:  # noqa: ANN001
    """Every recording the test set defines, a manifest, and the run's stand-ins in place of the models."""
    manifest = []
    heard: list[tuple[str, str]] = []
    files: dict[int, str] = {}
    for variant in sorted(variant_ids(EVAL_DIR, "es")):
        name = variant.split("--")[0]
        script = SPANISH.get(name) or ENGLISH[name]
        run_as = variant.split("--as-")[1] if "--as-" in variant else None
        said = passages(script) if run_as != "en" else [("es", script.display)]
        if len(said) not in files:
            files[len(said)] = f"passages-{len(said)}.webm"
            (tmp_path / files[len(said)]).write_bytes(audio(len(said)))
        noise = variant.split("--")[1] if "db" in variant else None
        manifest.append({"id": variant, "script": name, "file": files[len(said)], "container": "webm", "accent": "es",
                         "noise": noise, "snr_db": None} | ({"run_as": run_as} if run_as else {}))
        if run_as != "en":
            heard += said
    (tmp_path / "manifest.json").write_text(json.dumps({"variants": manifest, "generator": {}}), encoding="utf-8")
    asked: dict[str, int] = {}
    spanish = Structurer(llama(spanish_answer(asked)), prompt.load(language="es"), "K2.gguf", timeout=5)
    english_values = {name: {"value": "", "sources": []} for name in FIELDS}
    english = Structurer(llama(lambda sent: json.dumps(english_values)), prompt.load(), "K2.gguf", timeout=5)
    as_english = SPANISH[next(v.split("--")[0] for v in variant_ids(EVAL_DIR, "es") if v.endswith("--as-en"))]
    pipeline = answers_es.Pipeline(MODELS, Whisper(heard), ScriptedRecognizer(as_english.display), spanish._llama,
                                   spanish, english)
    monkeypatch.setattr(run_es, "open_pipeline", lambda args, api_key: pipeline)
    monkeypatch.setattr(translation, "check", refusing(translation.check))
    return Run(tmp_path, pipeline)


def arguments(path: Path, only: list[str] | None = None) -> argparse.Namespace:
    return argparse.Namespace(audio=path, out=path / "results" / "latest-es.json", only=only, no_live=True,
                              llama_url="http://llama.test", models_dir=path, device="cpu", machine="stand-ins")


def test_the_spanish_eval_runs_scores_and_is_judged_from_end_to_end(run, monkeypatch):
    run_dir = run.path
    # The scoring fails first: every answer is in the file all the same, and --rescore finishes it.
    def fails(*args, **kwargs):  # noqa: ANN002, ANN003, ANN202
        raise RuntimeError("a fault in the scoring")

    real_score = scoring_es.score_audio
    monkeypatch.setattr(scoring_es, "score_audio", fails)
    with pytest.raises(RuntimeError, match="a fault in the scoring"):
        asyncio.run(run_es.main_async(arguments(run_dir), "key"))
    stored = json.loads((run_dir / "results" / "latest-es.json").read_text(encoding="utf-8"))
    defined = variant_ids(EVAL_DIR, "es") | {f"{id}--text" for id in SPANISH}
    assert "metrics" not in stored and sorted(item["variant"] for item in stored["items"]) == sorted(defined)

    monkeypatch.setattr(scoring_es, "score_audio", real_score)
    assert run_es.main_rescore(run_dir / "results" / "latest-es.json", run_dir / "results" / "latest-es.json") == 0
    results = json.loads((run_dir / "results" / "latest-es.json").read_text(encoding="utf-8"))
    metrics = results["metrics"]

    # Every shape: another language heard, every reason a field is refused for, a statement the backstop drops, an
    # item not sorted.
    assert metrics["unclear"].get(OTHER_LANGUAGE)
    reasons = set(metrics["validation"]["reasons"]) | set(metrics["text"]["validation"]["reasons"])
    assert set(translation.REASONS) <= reasons
    backstopped = next(item for item in results["items"] if item["variant"] == f"{BACKSTOP}--text")["fields"]["plan"]
    assert any(s["english"] == "Order an A1c today." for s in backstopped["statements"])
    assert not any("A1c" in s["english"] for s in backstopped["shown_statements"])
    assert metrics["sorting"]["unavailable"] == {"model_output_invalid": 1}
    assert metrics["code_switching"]["passages"] > 0

    # The gate scores the stored answers again and finds what they give: only the item not sorted (with English results
    # of this tree to cap the values filed in the wrong field at). The owner's acceptances are run B's, which these
    # stand-in answers don't give, so they are judged without them.
    english_results_to_cap_at(EVAL_DIR, run_dir / "results")
    monkeypatch.setattr(gate, "KNOWN_LIMITATIONS", "none-accepted.toml")
    (problem,) = gate.check_spanish(run_dir / "results", EVAL_DIR)
    assert "didn't sort every Spanish recording and text" in problem


def test_a_fault_in_the_checks_leaves_the_answer_as_k2_horizon_gave_it(run, monkeypatch):
    run_dir = run.path
    def fails(*args, **kwargs):  # noqa: ANN002, ANN003, ANN202
        raise RuntimeError("a fault in the checks")

    monkeypatch.setattr(translation, "check", fails)
    run.pipeline.whisper = Whisper(passages(SPANISH["e01-hta-control"]))
    with pytest.raises(RuntimeError, match="a fault in the checks"):
        asyncio.run(run_es.main_async(arguments(run_dir, only=["e01"]), "key"))
    (item,) = json.loads((run_dir / "results" / "latest-es.json").read_text(encoding="utf-8"))["items"]
    assert item["variant"] == "e01-hta-control--clean"
    assert item["sorting"] == "done" and any(outcome["statements"] for outcome in item["fields"].values())
    assert all(outcome["status"] == "empty" and not outcome["shown_statements"] for outcome in item["fields"].values())
