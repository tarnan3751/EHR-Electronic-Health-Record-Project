"""The contract between the sidecar and the on-prem app (Ehr.Web, Api/Dictation), pinned in files both sides test.

Each file in src/Ehr.Dictation/contract is one kind of answer the on-prem app can get: the route, the status and the
body, produced here by the real sidecar code (its app, sessions, transcript and validation, with stand-ins only for
MedASR and llama-server). This test fails if what the sidecar answers no longer matches the files; the on-prem app's
DictationApiTests reads every file through its real reading path and fails if any would be refused. So a change on
either side that the other can't read fails one suite or the other.

After a deliberate change to what the sidecar answers, rewrite the files and commit them with the change:

    UPDATE_CONTRACT=1 uv run --project src/Ehr.Dictation/sidecar pytest src/Ehr.Dictation/sidecar/tests/test_contract.py

Values that vary from run to run, or with the prompt, are replaced with fixed ones: the session ID and the prompt,
schema and structuring hashes.
"""

from __future__ import annotations

import dataclasses
import json
import os
from collections.abc import Callable
from pathlib import Path

import numpy as np
from conftest import MODELS, MODELS_WITH_WHISPER, FakeLlama, FakeRecognizer, FakeWhisper, answer, encode, noise, spanish_answer, tone
from fastapi.testclient import TestClient

from ehr_dictation.api import create_app
from ehr_dictation.asr import Recognition
from ehr_dictation.audio import SAMPLE_RATE
from ehr_dictation.sessions import Spanish
from ehr_dictation.settings import Settings

CONTRACT_DIR = Path(__file__).resolve().parents[2] / "contract"
SESSION_ID = "SESSION-ID-IN-THE-CONTRACT-FILES"
HASH = "sha256:" + "0" * 64
COMMENT = (
    "Written by src/Ehr.Dictation/sidecar/tests/test_contract.py from the sidecar's real answers, and read by "
    "tests/Ehr.Web.Tests/DictationApiTests.cs. Rewrite with UPDATE_CONTRACT=1; never edit by hand."
)
OWNER = "11111111-1111-1111-1111-111111111111"
HEADERS = {"Authorization": "Bearer dictation-key", "X-Dictation-Owner": OWNER}
GROUNDED = answer(("Speech speech.", ["speech speech"]), plan=("Order a chest X-ray.", ["speech speech"]))


def wav(samples: np.ndarray) -> bytes:
    # PCM at the sidecar's own rate: no codec or resampler, so every ffmpeg decodes it to the same samples.
    return encode(samples, "wav", rate=SAMPLE_RATE)


def dictation() -> bytes:
    """Speech, a pause, speech too quiet to trust, a pause, and speech still going: a live transcript with a passage
    marked unclear and a tentative tail."""
    parts = [tone(2), np.zeros(SAMPLE_RATE, np.float32), tone(2, level_db=-50), np.zeros(SAMPLE_RATE, np.float32), tone(4)]
    samples = np.concatenate(parts)
    return wav(samples + noise(samples.size / SAMPLE_RATE, -80))


def one_passage() -> bytes:
    """A second's pause, then speech: one passage, heard clearly."""
    samples = np.concatenate([np.zeros(SAMPLE_RATE, np.float32), tone(3)])
    return wav(samples + noise(samples.size / SAMPLE_RATE, -80))


class FailingAtTheEnd(FakeRecognizer):
    """Transcribes the chunks, and fails on the rest of the recording after stop."""

    final = False

    def recognize(self, samples: np.ndarray) -> Recognition:
        if self.final:
            raise RuntimeError("MedASR failed")
        return super().recognize(samples)


class Misheard(FakeRecognizer):
    """Hears a correction whose cue speech recognition got wrong ("no weight" for "no wait")."""

    def recognize(self, samples: np.ndarray) -> Recognition:
        return Recognition("Lisinopril 10 mg no weight 20 mg daily {period}", 0.9)


class Dictating(FakeRecognizer):
    """Hears a short dictation, all of it one field's."""

    def recognize(self, samples: np.ndarray) -> Recognition:
        return Recognition("Cough for three days with a sore throat {period}", 0.9)


class Instructing(FakeRecognizer):
    """Hears a plan with an order and an allergy change in it, in every passage."""

    def recognize(self, samples: np.ndarray) -> Recognition:
        return Recognition(
            "Start metformin {period} Go ahead and order a hemoglobin A1c {period} Add penicillin to her allergy list "
            "{period}", 0.9
        )


def client(settings: Settings, llama: FakeLlama, recognizer: FakeRecognizer | None = None, model_timeout: float = 0.5,
           spanish: tuple[FakeWhisper, FakeLlama] | None = None, **limits) -> TestClient:
    settings = dataclasses.replace(settings, **limits)
    in_spanish = Spanish(spanish[0], spanish[1].structurer(model_timeout, language="es")) if spanish else None
    return TestClient(create_app(settings, recognizer=recognizer or FakeRecognizer(), structurer=llama.structurer(model_timeout),
                                 models=MODELS_WITH_WHISPER if spanish else MODELS, spanish=in_spanish))


def start(http: TestClient, language: str | None = None) -> str:
    body = {"spokenLanguage": language} if language else None
    return http.post("/v1/sessions", headers=HEADERS, json=body).json()["sessionId"]


def stopped(http: TestClient, audio: bytes | None = None, language: str | None = None) -> str:
    session = start(http, language)
    http.post(f"/v1/sessions/{session}/audio", params={"seq": 0}, content=audio or dictation(), headers=HEADERS)
    http.post(f"/v1/sessions/{session}/stop", headers=HEADERS)
    return session


def answers(settings: Settings) -> dict[str, dict]:
    produced: dict[str, dict] = {}

    def keep(name: str, route: str, response) -> None:  # noqa: ANN001
        produced[name] = {"route": route, "status": response.status_code, "body": response.json() if response.content else None}

    with client(settings, FakeLlama(GROUNDED)) as http:
        keep("start-201", "start", http.post("/v1/sessions", headers=HEADERS))
        session = start(http)
        keep("audio-200-partial", "audio",
             http.post(f"/v1/sessions/{session}/audio", params={"seq": 0}, content=dictation(), headers=HEADERS))
        keep("audio-409-out_of_order", "audio",
             http.post(f"/v1/sessions/{session}/audio", params={"seq": 5}, content=dictation(), headers=HEADERS))
        keep("result-409-not_stopped", "result", http.post(f"/v1/sessions/{session}/result", headers=HEADERS))
        http.post(f"/v1/sessions/{session}/stop", headers=HEADERS)
        keep("result-200-done", "result", http.post(f"/v1/sessions/{session}/result", params={"wait": 5}, headers=HEADERS))
        keep("audio-409-stopped", "audio",
             http.post(f"/v1/sessions/{session}/audio", params={"seq": 1}, content=dictation(), headers=HEADERS))
        keep("stop-404-not_found", "stop", http.post(f"/v1/sessions/{SESSION_ID}/stop", headers=HEADERS))
        session = start(http)
        keep("cancel-204", "cancel", http.delete(f"/v1/sessions/{session}", headers=HEADERS))
        keep("cancel-404-not_found", "cancel", http.delete(f"/v1/sessions/{session}", headers=HEADERS))
        keep("start-401-unauthorized", "start",
             http.post("/v1/sessions", headers={**HEADERS, "Authorization": "Bearer not-the-key"}))
        session = start(http)
        http.post(f"/v1/sessions/{session}/audio", params={"seq": 0}, content=b"not a recording " * 64, headers=HEADERS)
        keep("stop-422-not_audio", "stop", http.post(f"/v1/sessions/{session}/stop", headers=HEADERS))

    # K2 Horizon still working when stop gives up waiting; it hasn't even said which build it is yet. Stop waits a
    # second, which is long enough for a slow CI runner to decode and transcribe the recording, so the transcript is
    # always final, and K2 Horizon takes three and isn't given up on before that, so the sorting is always pending.
    with client(settings, FakeLlama(GROUNDED, delay=3, props_delay=3), model_timeout=10, stop_wait_seconds=1.0) as http:
        session = start(http)
        http.post(f"/v1/sessions/{session}/audio", params={"seq": 0}, content=dictation(), headers=HEADERS)
        keep("stop-200-pending", "stop", http.post(f"/v1/sessions/{session}/stop", headers=HEADERS))

    # The clinician dictated an order and an allergy change: the plan keeps "Start metformin", and both instructions
    # are listed as not carried over.
    planned = answer(
        plan=("Start metformin. Go ahead and order a hemoglobin A1c.", ["Start metformin", "Go ahead and order a hemoglobin A1c"])
    )
    with client(settings, FakeLlama(planned), Instructing()) as http:
        session = stopped(http)
        keep("result-200-done-not_carried_over", "result",
             http.post(f"/v1/sessions/{session}/result", params={"wait": 5}, headers=HEADERS))

    # A value whose transcript says another dose just after the one it uses: pending, with a possible_correction
    # warning that points at the later dose.
    flagged = answer(plan=("Lisinopril 10 mg.", ["Lisinopril 10 mg"]))
    with client(settings, FakeLlama(flagged), Misheard()) as http:
        session = stopped(http)
        keep("result-200-done-possible_correction", "result",
             http.post(f"/v1/sessions/{session}/result", params={"wait": 5}, headers=HEADERS))

    # The whole dictation sorted into one field, the others not dictated: pending, with a one_field_holds_dictation
    # warning that points at all its sources cover.
    whole = answer(("Cough for three days with a sore throat.", ["Cough for three days with a sore throat"]))
    with client(settings, FakeLlama(whole), Dictating()) as http:
        session = stopped(http, one_passage())
        keep("result-200-done-one_field_holds_dictation", "result",
             http.post(f"/v1/sessions/{session}/result", params={"wait": 5}, headers=HEADERS))

    unavailable: dict[str, Callable[[], FakeLlama]] = {
        "model_unavailable": lambda: FakeLlama(unreachable=True),
        "model_timeout": lambda: FakeLlama(GROUNDED, delay=3),
        "model_output_invalid": lambda: FakeLlama("Here are the fields you asked for."),
        "transcript_too_long": lambda: FakeLlama(GROUNDED, n_ctx=2048),
    }
    for reason, llama in unavailable.items():
        with client(settings, llama()) as http:
            session = stopped(http)
            keep(f"result-200-unavailable-{reason}", "result",
                 http.post(f"/v1/sessions/{session}/result", params={"wait": 5}, headers=HEADERS))

    # MedASR failed on the rest of the recording: the live transcript stays, not final, and nothing is sorted.
    failing = FailingAtTheEnd()
    with client(settings, FakeLlama(GROUNDED), failing) as http:
        session = start(http)
        http.post(f"/v1/sessions/{session}/audio", params={"seq": 0}, content=dictation(), headers=HEADERS)
        failing.final = True
        http.post(f"/v1/sessions/{session}/stop", headers=HEADERS)
        keep("result-200-unavailable-transcription_failed", "result",
             http.post(f"/v1/sessions/{session}/result", params={"wait": 5}, headers=HEADERS))

    with client(settings, FakeLlama(GROUNDED), max_chunk_bytes=1000) as http:
        session = start(http)
        keep("audio-413-too_large", "audio",
             http.post(f"/v1/sessions/{session}/audio", params={"seq": 0}, content=b"\0" * 2000, headers=HEADERS))
    with client(settings, FakeLlama(GROUNDED), max_session_seconds=1) as http:
        session = start(http)
        keep("audio-413-too_long", "audio",
             http.post(f"/v1/sessions/{session}/audio", params={"seq": 0}, content=dictation(), headers=HEADERS))
    with client(settings, FakeLlama(GROUNDED), max_sessions=0) as http:
        keep("start-429-busy", "start", http.post("/v1/sessions", headers=HEADERS))

    # A dictation in Spanish (the expansion plan's step 17): started in Spanish, two minutes at most; a passage Whisper
    # didn't hear as Spanish, marked; and a result whose English sentences each cite the Spanish they were translated
    # from, with the Spanish instruction listed and nothing proposed. Without Whisper, Spanish is refused, and any
    # other language is a bad one.
    translated = spanish_answer(("subjective", ["Tos seca desde hace 3 días"], "Dry cough for 3 days."),
                                ("subjective", ["Niega fiebre"], "Denies fever."),
                                ("plan", ["Pido una radiografía de tórax"], "Chest X-ray."))
    whisper = FakeWhisper("Tos seca desde hace 3 días. Niega fiebre. Pido una radiografía de tórax.")
    with client(settings, FakeLlama(GROUNDED), spanish=(whisper, FakeLlama(translated))) as http:
        keep("start-201-es", "start", http.post("/v1/sessions", headers=HEADERS, json={"spokenLanguage": "es"}))
        keep("start-400-bad_language", "start", http.post("/v1/sessions", headers=HEADERS, json={"spokenLanguage": "fr"}))
        session = stopped(http, one_passage(), "es")
        keep("result-200-done-es", "result", http.post(f"/v1/sessions/{session}/result", params={"wait": 5}, headers=HEADERS))
    heard_as_english = FakeWhisper("Patient says the pain started yesterday.", "other_language", lambda samples: True)
    with client(settings, FakeLlama(GROUNDED), spanish=(heard_as_english, FakeLlama(translated))) as http:
        session = start(http, "es")
        keep("audio-200-other_language", "audio",
             http.post(f"/v1/sessions/{session}/audio", params={"seq": 0}, content=dictation(), headers=HEADERS))
    with client(settings, FakeLlama(GROUNDED)) as http:
        keep("start-503-language_unavailable", "start",
             http.post("/v1/sessions", headers=HEADERS, json={"spokenLanguage": "es"}))

    return {name: {"$comment": COMMENT, **_fixed(value)} for name, value in produced.items()}


def _fixed(value: object) -> object:
    if isinstance(value, dict):
        out = {}
        for key, item in value.items():
            if key == "sessionId":
                out[key] = SESSION_ID
            elif key in ("prompt", "schema", "structuring", "code") and isinstance(item, str) and item.startswith("sha256:"):
                out[key] = HASH
            else:
                out[key] = _fixed(item)
        return out
    if isinstance(value, list):
        return [_fixed(item) for item in value]
    return value


def test_the_contract_files_are_what_the_sidecar_answers(settings):
    produced = answers(settings)

    if os.environ.get("UPDATE_CONTRACT") == "1":
        CONTRACT_DIR.mkdir(exist_ok=True)
        for stale in CONTRACT_DIR.glob("*.json"):
            stale.unlink()
        for name, value in produced.items():
            (CONTRACT_DIR / f"{name}.json").write_text(json.dumps(value, indent=2) + "\n", encoding="utf-8")

    committed = {path.stem: json.loads(path.read_text(encoding="utf-8")) for path in CONTRACT_DIR.glob("*.json")}
    assert sorted(committed) == sorted(produced), "Rewrite the contract files: UPDATE_CONTRACT=1 (see this file's header)."
    for name in produced:
        assert committed[name] == produced[name], f"{name}.json no longer matches what the sidecar answers."


def test_the_contract_covers_every_answer_the_on_prem_app_must_read(settings):
    produced = answers(settings)
    bodies = {name: value["body"] for name, value in produced.items()}

    assert {value["status"] for value in produced.values()} == {200, 201, 204, 400, 401, 404, 409, 413, 422, 429, 503}
    assert {value["route"] for value in produced.values()} == {"start", "audio", "stop", "result", "cancel"}
    reasons = ("model_unavailable", "model_timeout", "model_output_invalid", "transcript_too_long", "transcription_failed")
    assert {bodies[f"result-200-unavailable-{r}"]["reason"] for r in reasons} == set(reasons)
    planned = bodies["result-200-done-not_carried_over"]
    assert planned["fields"]["plan"]["value"] == "Start metformin."
    assert {item["reason"] for item in planned["notCarriedOver"]} == {"order", "allergy_change"}
    assert bodies["audio-200-partial"]["audioSeconds"] > 0
    segments = bodies["audio-200-partial"]["transcript"]["segments"]
    assert any(s["unclear"] for s in segments) and not segments[-1]["final"]
    fields = bodies["result-200-done"]["fields"]
    assert {f["status"] for f in fields.values()} == {"pending", "empty"}
    assert bodies["stop-200-pending"]["sorting"] == "pending" and bodies["stop-200-pending"]["provenance"]["llamaServer"] is None
    assert bodies["audio-409-out_of_order"]["expectedSeq"] == 1
    warned = bodies["result-200-done-possible_correction"]["fields"]["plan"]
    assert warned["status"] == "pending" and "possible_correction" in [w["code"] for w in warned["warnings"]]
    whole = bodies["result-200-done-one_field_holds_dictation"]["fields"]
    assert [w["code"] for w in whole["subjective"]["warnings"]] == ["one_field_holds_dictation"]
    assert all(whole[name]["reasons"] == ["not_dictated"] for name in ("objective", "assessment", "plan"))
    near = bodies["result-200-done"]["fields"]["subjective"]
    assert [w["code"] for w in near["warnings"]] == ["near_unclear_passage"]
    failed = bodies["result-200-unavailable-transcription_failed"]
    assert failed["transcript"]["text"] and not failed["transcript"]["final"]
    # Spanish (the expansion plan's step 17).
    assert bodies["start-201-es"]["maxSeconds"] == 120 and bodies["start-201"]["maxSeconds"] == 180
    assert bodies["start-400-bad_language"]["error"] == "bad_language"
    assert bodies["start-503-language_unavailable"]["error"] == "language_unavailable"
    assert any(s["unclear"] == "other_language" for s in bodies["audio-200-other_language"]["transcript"]["segments"])
    spanish = bodies["result-200-done-es"]
    assert spanish["spokenLanguage"] == "es" and spanish["proposals"] == [] and spanish["notCarriedOver"]
    said = spanish["fields"]["subjective"]
    assert [said["value"][s["start"] : s["end"]] for s in said["statements"]] == ["Dry cough for 3 days.", "Denies fever."]
    assert [s for statement in said["statements"] for s in statement["sources"]] == said["sources"]
    assert spanish["fields"]["plan"]["reasons"] == ["order_or_allergy_change"]
    assert spanish["provenance"]["speech"]["repository"] == "openai/whisper-large-v3-turbo"
    assert all("spokenLanguage" not in body for name, body in bodies.items() if body and not name.endswith("-es"))
