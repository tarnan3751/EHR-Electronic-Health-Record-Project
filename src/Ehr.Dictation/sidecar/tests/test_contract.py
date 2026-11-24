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

A recorded visit's answers (the expansion plan's step 16) are here too, each route named as the on-prem app names the
call (start-ambient, audio-ambient and so on: AmbientApi.cs), made with stand-ins for the speaker model as well: tones,
a pitch a voice.
"""

from __future__ import annotations

import dataclasses
import json
import os
import threading
from collections.abc import Callable
from pathlib import Path

import numpy as np
from conftest import (
    LISTENING_MODELS, MODELS, FakeLlama, FakeRecognizer, PitchEmbedder, PitchRecognizer, answer, encode, noise, tone, visit,
)
from fastapi.testclient import TestClient

from ehr_dictation import conversation
from ehr_dictation.api import create_app
from ehr_dictation.asr import Recognition
from ehr_dictation.audio import SAMPLE_RATE
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
            "Start metformin {period} Go ahead and order a hemoglobin A1c {period} Add amoxicillin to her allergy list "
            "{period}", 0.9
        )


class Retracting(FakeRecognizer):
    """Hears a plain order and, after it, a refusal the proposal rules don't read: one of the trick sentences the owner
    accepted by name (eval/trick-sentences.toml), in every passage."""

    def recognize(self, samples: np.ndarray) -> Recognition:
        return Recognition("Order a CBC {period} She turned down the blood test {period}", 0.9)


def client(settings: Settings, llama: FakeLlama, recognizer: FakeRecognizer | None = None, model_timeout: float = 0.5,
           **limits) -> TestClient:
    settings = dataclasses.replace(settings, **limits)
    return TestClient(create_app(settings, recognizer=recognizer or FakeRecognizer(),
                                 structurer=llama.structurer(model_timeout), models=MODELS))


def start(http: TestClient) -> str:
    return http.post("/v1/sessions", headers=HEADERS).json()["sessionId"]


def stopped(http: TestClient, audio: bytes | None = None) -> str:
    session = start(http)
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

    # A plain order proposed with a refusal after it that the rules don't read: the proposal carries where what was
    # heard after it is, which the review shows beside it (step 15.1, at the owner's word).
    refused = answer(plan=("She turned down the blood test.", ["She turned down the blood test"]))
    with client(settings, FakeLlama(refused), Retracting()) as http:
        session = stopped(http)
        keep("result-200-done-proposal_heard_after", "result",
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

    _visits(settings, keep)
    return {name: {"$comment": COMMENT, **_fixed(value)} for name, value in produced.items()}


LISTENING = HEADERS | {"X-Dictation-Mode": "ambient"}
NAMED = {"speakers": {"1": "clinician", "2": "patient"}}
EVERYONE = {"speakers": {"1": "clinician", "2": "patient", "3": "other"}}
DRAFTED = answer(
    ("I have had a cough for three days. She coughs at night.", ["I have had a cough for three days", "She coughs at night"]),
    objective=("Your chest is clear.", ["Your chest is clear"]),
)


class InstructedVisit(PitchRecognizer):
    """Hears the clinician send a prescription, the patient ask for a refill and the third voice arrange a scan."""

    SAID = {"Your chest is clear {period}": "I'll send that to your pharmacy {period}",
            "I have had a cough for three days {period}": "Can you refill my inhaler {question mark}",
            "She coughs at night {period}": "I'll arrange a scan {period}"}

    def recognize(self, samples: np.ndarray) -> Recognition:
        heard = super().recognize(samples)
        return Recognition(self.SAID[heard.text], heard.confidence)


def listening_client(settings: Settings, llama: FakeLlama, recognizer: FakeRecognizer | None = None, model_timeout: float = 0.5,
                     models=LISTENING_MODELS, **limits) -> TestClient:
    settings = dataclasses.replace(settings, **limits)
    return TestClient(create_app(settings, recognizer=recognizer or PitchRecognizer(), structurer=llama.structurer(model_timeout),
                                 models=models, embedder=PitchEmbedder() if models.speaker else None))


def heard(http: TestClient, *turns: str, speakers: int = 2) -> str:
    """A visit started, its recording sent and stopped, and waited on until its voices are told apart."""
    session = http.post("/v1/sessions", params={"mode": "ambient", "speakers": speakers}, headers=LISTENING).json()["sessionId"]
    http.post(f"/v1/sessions/{session}/audio", params={"seq": 0}, content=wav(visit(*turns)), headers=LISTENING)
    http.post(f"/v1/sessions/{session}/stop", headers=LISTENING)
    http.post(f"/v1/sessions/{session}/result", params={"wait": 5}, headers=LISTENING)
    return session


def _visits(settings: Settings, keep: Callable) -> None:
    def drafted(http: TestClient, session: str, names: dict) -> None:
        http.post(f"/v1/sessions/{session}/speakers", json=names, headers=LISTENING)

    with listening_client(settings, FakeLlama(DRAFTED)) as http:
        started = http.post("/v1/sessions", params={"mode": "ambient", "speakers": 2}, headers=LISTENING)
        keep("start-ambient-201", "start-ambient", started)
        http.delete(f"/v1/sessions/{started.json()['sessionId']}", headers=LISTENING)
        session = http.post("/v1/sessions", params={"mode": "ambient", "speakers": 2}, headers=LISTENING).json()["sessionId"]
        keep("audio-ambient-200", "audio-ambient",
             http.post(f"/v1/sessions/{session}/audio", params={"seq": 0}, content=wav(visit("clinician", "patient")),
                       headers=LISTENING))
        keep("speakers-ambient-409-not_stopped", "speakers-ambient",
             http.post(f"/v1/sessions/{session}/speakers", json=NAMED, headers=LISTENING))
        keep("cancel-ambient-204", "cancel-ambient", http.delete(f"/v1/sessions/{session}", headers=LISTENING))
        dictation = start(http)
        keep("audio-ambient-404-not_found", "audio-ambient",
             http.post(f"/v1/sessions/{dictation}/audio", params={"seq": 0}, content=wav(visit("clinician")), headers=LISTENING))
        http.delete(f"/v1/sessions/{dictation}", headers=HEADERS)

        # Three voices: the patient's words and someone else's in Subjective, the clinician's in Objective.
        session = heard(http, "clinician", "patient", "other", "clinician", speakers=3)
        keep("result-ambient-200-awaiting_speakers", "result-ambient",
             http.post(f"/v1/sessions/{session}/result", headers=LISTENING))
        keep("speakers-ambient-422-speakers_incomplete", "speakers-ambient",
             http.post(f"/v1/sessions/{session}/speakers", json=NAMED, headers=LISTENING))
        drafted(http, session, EVERYONE)
        keep("result-ambient-200-done", "result-ambient",
             http.post(f"/v1/sessions/{session}/result", params={"wait": 5}, headers=LISTENING))
        keep("speakers-ambient-409-speakers_given", "speakers-ambient",
             http.post(f"/v1/sessions/{session}/speakers", json={"speakers": {"1": "patient", "2": "clinician", "3": "other"}},
                       headers=LISTENING))

    # Still being transcribed when stop gives up waiting: MedASR held until it has answered.
    recognizer = PitchRecognizer()
    with listening_client(settings, FakeLlama(DRAFTED), recognizer, stop_wait_seconds=0.5) as http:
        session = http.post("/v1/sessions", params={"mode": "ambient", "speakers": 2}, headers=LISTENING).json()["sessionId"]
        http.post(f"/v1/sessions/{session}/audio", params={"seq": 0}, content=wav(visit("clinician", "patient")),
                  headers=LISTENING)
        recognizer.hold = threading.Event()
        keep("stop-ambient-200-pending", "stop-ambient", http.post(f"/v1/sessions/{session}/stop", headers=LISTENING))
        recognizer.hold.set()

    # Named, and K2 Horizon still drafting: then too slow, so the visit is left undrafted.
    with listening_client(settings, FakeLlama(DRAFTED, delay=3)) as http:
        session = heard(http, "clinician", "patient")
        keep("speakers-ambient-200-pending", "speakers-ambient",
             http.post(f"/v1/sessions/{session}/speakers", json=NAMED, headers=LISTENING))
        keep("result-ambient-200-unavailable-model_timeout", "result-ambient",
             http.post(f"/v1/sessions/{session}/result", params={"wait": 5}, headers=LISTENING))

    # Named, and never a turn with K2 Horizon, a dictation always waiting: the visit is left undrafted as busy.
    with listening_client(settings, FakeLlama(DRAFTED), ambient_wait_seconds=0.2) as http:
        session = heard(http, "clinician", "patient")
        turns = http.app.state.sessions._turns
        turns._dictations, _ = 1, turns._none.clear()
        drafted(http, session, NAMED)
        keep("result-ambient-200-unavailable-model_busy", "result-ambient",
             http.post(f"/v1/sessions/{session}/result", params={"wait": 5}, headers=LISTENING))

    # Drafted in two windows, the second without the patient: Subjective comes from the first, and says the second had
    # something for it that didn't pass.
    original = conversation.WINDOW_TOKENS
    conversation.WINDOW_TOKENS = 12
    try:
        with listening_client(settings, FakeLlama(answer(("I have had a cough for three days.", ["I have had a cough for three days"]),
                                                         objective=("Your chest is clear.", ["Your chest is clear"])))) as http:
            session = heard(http, "clinician", "patient", "clinician", "clinician")
            drafted(http, session, NAMED)
            keep("result-ambient-200-done-part_not_offered", "result-ambient",
                 http.post(f"/v1/sessions/{session}/result", params={"wait": 5}, headers=LISTENING))
    finally:
        conversation.WINDOW_TOKENS = original

    # The third voice named unsure: its passage is Unsure, and the instruction said in it is listed beside the
    # clinician's, each with who said it; the patient's request for a refill isn't.
    with listening_client(settings, FakeLlama(answer()), InstructedVisit()) as http:
        session = heard(http, "clinician", "patient", "other", speakers=3)
        drafted(http, session, {"speakers": {"1": "clinician", "2": "patient", "3": "unsure"}})
        keep("result-ambient-200-done-not_carried_over", "result-ambient",
             http.post(f"/v1/sessions/{session}/result", params={"wait": 5}, headers=LISTENING))

    with listening_client(settings, FakeLlama(DRAFTED), models=MODELS) as http:
        keep("start-ambient-503-listening_unavailable", "start-ambient",
             http.post("/v1/sessions", params={"mode": "ambient", "speakers": 2}, headers=LISTENING))
    with listening_client(settings, FakeLlama(DRAFTED), ambient_max_sessions=0) as http:
        keep("start-ambient-429-busy", "start-ambient",
             http.post("/v1/sessions", params={"mode": "ambient", "speakers": 2}, headers=LISTENING))


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

    assert {value["status"] for value in produced.values()} == {200, 201, 204, 401, 404, 409, 413, 422, 429, 503}
    assert {value["route"] for value in produced.values()} == {
        "start", "audio", "stop", "result", "cancel",
        "start-ambient", "audio-ambient", "stop-ambient", "speakers-ambient", "result-ambient", "cancel-ambient",
    }
    reasons = ("model_unavailable", "model_timeout", "model_output_invalid", "transcript_too_long", "transcription_failed")
    assert {bodies[f"result-200-unavailable-{r}"]["reason"] for r in reasons} == set(reasons)
    planned = bodies["result-200-done-not_carried_over"]
    assert planned["fields"]["plan"]["value"] == "Start metformin."
    assert {item["reason"] for item in planned["notCarriedOver"]} == {"order", "allergy_change"}
    refused = bodies["result-200-done-proposal_heard_after"]
    first = refused["proposals"][0]
    assert refused["transcript"]["text"][first["heardAfter"]["start"] : first["heardAfter"]["end"]] == (
        "She turned down the blood test.")
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


# The same for a recorded visit's (the expansion plan's step 16): what the on-prem app must read of it, and nothing of a
# visit in a dictation's answers.
def test_the_contract_covers_every_answer_of_a_visit(settings):
    bodies = {name: value["body"] for name, value in answers(settings).items()}

    assert bodies["start-ambient-201"]["mode"] == "ambient" and bodies["start-ambient-201"]["maxSeconds"] == 1200
    assert "mode" not in bodies["start-201"]
    heard = bodies["audio-ambient-200"]
    assert heard["transcript"] == {"text": "", "final": False, "segments": []} and "quietSeconds" in heard
    assert bodies["stop-ambient-200-pending"]["sorting"] == "pending"
    waiting = bodies["result-ambient-200-awaiting_speakers"]
    assert waiting["sorting"] == "awaiting_speakers" and [v["group"] for v in waiting["speakers"]] == [1, 2, 3]
    assert {(s["speakerGroup"], s["speaker"]) for s in waiting["transcript"]["segments"]} == {(1, None), (2, None), (3, None)}
    assert bodies["speakers-ambient-200-pending"]["sorting"] == "pending"
    done = bodies["result-ambient-200-done"]
    assert done["sorting"] == "done" and done["proposals"] == [] and "speakers" not in done
    assert all(s["speaker"] in ("clinician", "patient", "other") for s in done["transcript"]["segments"])
    subjective = done["fields"]["subjective"]
    assert [s["speaker"] for s in subjective["sources"]] == ["patient", "other"]
    assert [w["code"] for w in subjective["warnings"]] == ["patients_own_words", "other_speaker"]
    assert done["provenance"]["speaker"]["repository"] == "speechbrain/spkrec-ecapa-voxceleb"
    parted = bodies["result-ambient-200-done-part_not_offered"]["fields"]["subjective"]
    assert [w["code"] for w in parted["warnings"]] == ["patients_own_words", "part_not_offered"]
    assert bodies["result-ambient-200-unavailable-model_timeout"]["reason"] == "model_timeout"
    assert bodies["result-ambient-200-unavailable-model_busy"]["reason"] == "model_busy"
    instructed = bodies["result-ambient-200-done-not_carried_over"]
    assert [s["speaker"] for s in instructed["transcript"]["segments"]] == ["clinician", "patient", "unsure"]
    assert [(i["reason"], i["speaker"]) for i in instructed["notCarriedOver"]] == [("order", "clinician"), ("order", "unsure")]
    assert {bodies[name]["error"] for name in ("speakers-ambient-409-not_stopped", "speakers-ambient-409-speakers_given",
                                               "speakers-ambient-422-speakers_incomplete", "start-ambient-503-listening_unavailable",
                                               "audio-ambient-404-not_found", "start-ambient-429-busy")} == {
        "not_stopped", "speakers_given", "speakers_incomplete", "listening_unavailable", "not_found", "busy"}
    # Every pending Subjective value of a visit says it is the patient's own words, and every named segment its speaker.
    for name, body in bodies.items():
        if "ambient" in name and body and body.get("fields"):
            subjective = body["fields"]["subjective"]
            assert subjective["status"] != "pending" or "patients_own_words" in [w["code"] for w in subjective["warnings"]], name
        if "ambient" in name and body and body.get("sorting") in ("done", "unavailable"):
            assert all(segment["speaker"] for segment in body["transcript"]["segments"]), name
    dictated = [body for name, body in bodies.items() if "ambient" not in name and body]
    assert not any("speaker" in json.dumps(body) or "quietSeconds" in body for body in dictated)
