"""The visits' run end to end, as the model session runs it: ehr_eval.ambient's main() over stand-ins for MedASR, the
speaker model and llama-server, on visits that list an instruction and a request, have a passage whose speaker is
unsure, one that K2 Horizon won't draft, and one of three voices; then a scoring that fails, --rescore finishing it,
--resume, the contention probe, and the gate reading it all. Tones stand in for voices; no model, no network."""

from __future__ import annotations

import json
from pathlib import Path

import httpx2
import numpy as np
import pytest
from conftest import llama_transport, webm

from ehr_dictation import prompt, provenance
from ehr_dictation.asr import Recognition
from ehr_dictation.audio import SAMPLE_RATE
from ehr_dictation.llama import LlamaServer
from ehr_eval import ambient, ambient_gate, ambient_scoring, conversations

import conversation_scripts as cs

PITCHES = {"clinician": 220.0, "patient": 440.0, "other": 660.0, "quiet": 330.0}
SAYS = {
    "clinician": "Your chest is clear. I'll send that to your pharmacy.",
    "patient": "I have had a cough for 2 weeks. Can you refill my inhaler?",
    "other": "She coughs at night.",
    "quiet": "Your ears look normal.",  # the clinician of the visit K2 Horizon won't draft
}
GOLD = '''
[gold.subjective]
value = "I have had a cough for 2 weeks."
sources = ["I have had a cough for 2 weeks"]

[gold.objective]
value = "Your chest is clear."
sources = ["Your chest is clear"]

[gold.assessment]
value = ""
sources = []

[gold.plan]
value = ""
sources = []
'''


def visit(number: str, speakers: int, voices: str, noise: str, roles: tuple[str, ...]) -> str:
    turns = "".join(f'\n[[turns]]\nrole = "{role}"\ntext = "{SAYS["quiet" if number == "c02" and role == "clinician" else role]}"\n'
                    for role in roles)
    return (f'id = "{number}-run"\nspeakers = {speakers}\nvoices = {{ {voices} }}\ntags = ["refill-request", "order-spoken"'
            f'{", " + chr(34) + "three-speakers" + chr(34) if speakers == 3 else ""}]\nnoise = [{noise}]\ndates = []\ndrugs = []\n'
            f'forbidden = ["I\'ll send that to your pharmacy"]\nrequests = ["Can you refill my inhaler"]\n{turns}{GOLD}')


VISITS = {
    "c01": visit("c01", 2, 'clinician = "af_heart", patient = "am_michael"', '"room"', ("clinician", "patient", "clinician")),
    "c02": visit("c02", 2, 'clinician = "af_heart", patient = "am_michael"', "", ("clinician", "patient", "clinician")),
    "c03": visit("c03", 3, 'clinician = "af_heart", patient = "am_michael", other = "bf_emma"', "",
                 ("clinician", "patient", "other", "clinician")),
}


class Embedder:
    def embed(self, windows: np.ndarray) -> np.ndarray:
        spectrum = np.abs(np.fft.rfft(windows, axis=1))
        bins = np.fft.rfftfreq(windows.shape[1], 1 / SAMPLE_RATE)
        return np.stack([spectrum[:, (bins > p - 20) & (bins < p + 20)].sum(axis=1) for p in PITCHES.values()], axis=1) + 1e-3


class Recognizer:
    def recognize(self, samples: np.ndarray) -> Recognition:
        spectrum = np.abs(np.fft.rfft(samples))
        bins = np.fft.rfftfreq(samples.size, 1 / SAMPLE_RATE)
        loudest = max(PITCHES, key=lambda who: spectrum[(bins > PITCHES[who] - 20) & (bins < PITCHES[who] + 20)].sum())
        return Recognition(SAYS[loudest], 0.9)


def recording(pitches: list[float]) -> tuple[bytes, list[float]]:
    pieces, times, at = [], [], 0.0
    for pitch in pitches:
        t = np.arange(3 * SAMPLE_RATE) / SAMPLE_RATE
        pieces += [0.1 * np.sin(2 * np.pi * pitch * t), np.zeros(SAMPLE_RATE // 2)]
        times.append(at)
        at += 3.5
    samples = np.concatenate(pieces).astype(np.float32)
    samples += (np.random.default_rng(1).standard_normal(samples.size) * 3e-4).astype(np.float32)
    return webm(samples), times


def llama() -> LlamaServer:
    answer = {"subjective": {"value": "I have had a cough for 2 weeks.", "sources": ["I have had a cough for 2 weeks"]},
              "objective": {"value": "Your chest is clear.", "sources": ["Your chest is clear"]},
              "assessment": {"value": "", "sources": []}, "plan": {"value": "", "sources": []}}

    def complete(request: httpx2.Request) -> httpx2.Response:
        if "ears look normal" in request.content.decode():
            return httpx2.Response(503, json={"error": {"message": "nope"}})
        return httpx2.Response(200, json={"choices": [{"finish_reason": "stop", "message": {"content": json.dumps(answer)}}]})

    return LlamaServer("http://llama.test", "key", transport=llama_transport(complete))


MODELS = provenance.Models(
    speech={"repository": "google/medasr", "commit": "a" * 40, "sha256": "b" * 64},
    language={"repository": "IFM/K2-Horizon-7B-GGUF", "commit": "c" * 40, "file": "K2.gguf", "sha256": "d" * 64},
    speaker={"repository": "speechbrain/spkrec-ecapa-voxceleb", "commit": "e" * 40, "sha256": "f" * 64},
)


@pytest.fixture
def run_dir(tmp_path, monkeypatch):
    """An eval folder with the three visits, their recordings and manifest, and the stand-ins in place."""
    (tmp_path / "conversations").mkdir()
    for number, text in VISITS.items():
        (tmp_path / "conversations" / f"{number}-run.toml").write_text(text, encoding="utf-8")
    visits = {v.id: v for v in cs.load_conversations(tmp_path / "conversations")}
    audio = tmp_path / "audio"
    audio.mkdir()
    variants = []
    for variant_id in sorted(conversations.variant_ids(tmp_path)):
        v = visits[variant_id.split("--")[0]]
        pitches = [PITCHES["quiet" if v.id.startswith("c02") and t.role == "clinician" else t.role] for t in v.turns]
        data, starts = recording(pitches)
        (audio / f"{variant_id}.webm").write_bytes(data)
        noise = variant_id.split("--")[1]
        variants.append({"id": variant_id, "script": v.id, "file": f"{variant_id}.webm", "container": "webm",
                         "noise": None if noise == "clean" else noise, "seconds": 3.5 * len(pitches),
                         "turns": [{"role": t.role, "start": s, "end": s + 3.0} for t, s in zip(v.turns, starts, strict=True)]})
    (audio / "manifest.json").write_text(json.dumps({"variants": variants}), encoding="utf-8")
    monkeypatch.setenv("LLAMA_API_KEY", "key")
    monkeypatch.setattr(ambient, "load_conversations", lambda: list(visits.values()))
    monkeypatch.setattr(ambient.provenance, "load", lambda directory: MODELS)
    monkeypatch.setattr(ambient, "MedAsr", lambda directory, device: Recognizer())
    monkeypatch.setattr(ambient.ecapa, "Ecapa", lambda *args: Embedder())
    monkeypatch.setattr(ambient, "LlamaServer", lambda url, key: llama())
    return tmp_path


def run(run_dir: Path, *more: str) -> int:
    return ambient.main(["--llama-url", "http://llama.test", "--audio", str(run_dir / "audio"), "--out",
                         str(run_dir / "results" / "ambient-latest.json"), "--no-live", "--models-dir", str(run_dir), *more])


def stored(run_dir: Path) -> dict:
    return json.loads((run_dir / "results" / "ambient-latest.json").read_text(encoding="utf-8"))


def test_the_run_scores_every_kind_of_visit_and_the_gate_reads_it(run_dir, monkeypatch):
    assert run(run_dir) == 0
    data = stored(run_dir)
    items = {item["variant"]: item for item in data["items"]}

    assert sorted(items) == sorted(conversations.variant_ids(run_dir))
    assert items["c02-run--clean"]["sorting"] == "unavailable" and items["c02-run--clean"]["reason"] == "model_unavailable"
    assert items["c03-run--clean"]["names"] == {"1": "clinician", "2": "patient", "3": "other"}
    assert any(any(item["unsure"]) for item in data["items"]), "a passage whose speaker is unsure"
    stored_windows = [w for item in data["items"] for doubts in item["doubts"] if doubts for w in doubts["windows"]]
    assert stored_windows and all(len(w) == 7 and all(isinstance(n, (int, float)) for n in w) for w in stored_windows)
    listed = [entry for item in data["items"] for entry in item["not_carried_over"]]
    assert listed and all(entry[3] in ("clinician", "unsure") for entry in listed)
    assert data["metrics"]["safety"]["requests_as_instructions"] == []
    assert data["metrics"]["order_allergy"]["reported_not_carried_over"] >= 1
    assert data["metrics"]["sorting"]["unavailable"] == {"model_unavailable": 1}

    # The gate, on the eval folder the run was scored for, reads all of it; the visit K2 Horizon wouldn't draft and the
    # missing contention probe are what it says.
    data["provenance"] |= {"pipeline": conversations.pipeline_sha256(run_dir), "scoring": conversations.scoring_sha256(run_dir)}
    data["dataset"] |= {"recordings_sha256": conversations.recordings_sha256(run_dir),
                        "gold_sha256": conversations.gold_sha256(run_dir)}
    (run_dir / "results" / "ambient-latest.json").write_text(json.dumps(data), encoding="utf-8")
    problems = ambient_gate.check(run_dir / "results", run_dir)
    assert any("Not every visit was drafted" in p for p in problems)
    assert any("no contention probe" in p for p in problems)
    assert not any("aren't what their stored answers come to" in p or "-run--" in p for p in problems), problems


def test_a_scoring_that_fails_leaves_every_answer_for_rescore_and_resume_skips_what_is_stored(run_dir, monkeypatch):
    real = ambient_scoring.score
    monkeypatch.setattr(ambient_scoring, "score", lambda *args: (_ for _ in ()).throw(ValueError("a scoring bug")))
    with pytest.raises(ValueError, match="a scoring bug"):
        run(run_dir)
    raw = stored(run_dir)
    assert raw["metrics"] is None and len(raw["items"]) == len(conversations.variant_ids(run_dir))

    monkeypatch.setattr(ambient_scoring, "score", real)
    asked = []
    original = ambient.run_variant
    monkeypatch.setattr(ambient, "run_variant", lambda *args: asked.append(args[0]["id"]) or original(*args))
    assert run(run_dir, "--resume") == 0
    assert asked == [] and stored(run_dir)["metrics"]["sorting"]["done"] == 3

    path = run_dir / "results" / "ambient-latest.json"
    path.write_text(json.dumps(raw), encoding="utf-8")
    assert ambient.main(["--rescore", str(path)]) == 0
    assert stored(run_dir)["metrics"]["sorting"]["done"] == 3


def test_the_contention_probe_runs_its_four_cases_and_the_gate_accepts_them(run_dir, monkeypatch):
    assert run(run_dir) == 0
    dictation_audio = run_dir / "dictation-audio"
    dictation_audio.mkdir()
    (dictation_audio / "manifest.json").write_text(json.dumps({"variants": [
        {"id": "d01--clean", "script": "d01", "file": "d01.wav", "noise": None, "seconds": 120.0}]}), encoding="utf-8")

    class Transcript:
        def for_structuring(self) -> str:
            return "Patient has a cough."

    async def answer(*args):  # noqa: ANN002
        return None, Transcript(), None

    monkeypatch.setattr(ambient.answers, "answer", answer)
    monkeypatch.setattr(ambient, "load_all", lambda: {})
    assert run(run_dir, "--contention", "--dictation-audio", str(dictation_audio)) == 0

    probe = stored(run_dir)["contention"]
    assert {case: len(probe[case]["dictation_sorted"]) for case in ("one_alone", "two_alone", "one_beside_a_window",
                                                                  "two_beside_a_window")} == {
        "one_alone": 1, "two_alone": 2, "one_beside_a_window": 1, "two_beside_a_window": 2}
    data = stored(run_dir)
    data["provenance"] |= {"pipeline": conversations.pipeline_sha256(run_dir), "scoring": conversations.scoring_sha256(run_dir)}
    data["dataset"] |= {"recordings_sha256": conversations.recordings_sha256(run_dir),
                        "gold_sha256": conversations.gold_sha256(run_dir)}
    (run_dir / "results" / "ambient-latest.json").write_text(json.dumps(data), encoding="utf-8")
    assert not any("contention" in p for p in ambient_gate.check(run_dir / "results", run_dir))
    assert prompt.load_conversation().parameters["max_tokens"] == 1100


# D17's measure: the longest clean visit of the run is fed in five-second chunks whatever its length (the visits are
# under the 20 minutes the decision speaks of), the results say which, and a run that should have timed them and didn't
# fails the gate by name.
def test_the_longest_clean_visit_has_its_chunks_timed_whatever_its_length(run_dir, monkeypatch):
    fed = []

    async def chunk_seconds(data, seconds, *_):
        fed.append(seconds)
        return [0.5] * 8

    monkeypatch.setattr(ambient.listening, "chunk_seconds", chunk_seconds)
    assert ambient.main(["--llama-url", "http://llama.test", "--audio", str(run_dir / "audio"), "--out",
                         str(run_dir / "results" / "ambient-latest.json"), "--models-dir", str(run_dir)]) == 0
    data = stored(run_dir)
    longest = max((v for v in json.loads((run_dir / "audio" / "manifest.json").read_text())["variants"] if v["noise"] is None),
                  key=lambda v: v["seconds"])

    assert fed == [longest["seconds"]] and data["run"]["chunks_timed"] == longest["id"]
    live = data["metrics"]["timing"]["chunk_seconds"]
    assert live["recording"] == longest["id"] and live["near_the_end"] == {"mean": 0.5, "max": 0.5}
    assert not any("chunks" in p for p in ambient_gate.check(run_dir / "results", run_dir))

    untimed = data | {"items": [item | {"chunk_seconds": []} for item in data["items"]]}
    (run_dir / "results" / "ambient-latest.json").write_text(json.dumps(untimed), encoding="utf-8")
    assert any(p.startswith(f"{longest['id']}, the longest clean visit, should have had its five-second chunks timed")
               for p in ambient_gate.check(run_dir / "results", run_dir))
