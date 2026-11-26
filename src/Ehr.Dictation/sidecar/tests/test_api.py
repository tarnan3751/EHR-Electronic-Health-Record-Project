"""api.py, over HTTP as the on-prem app calls it: the key, the owner, a whole dictation recorded as Chromium records it,
the transcript kept when the model fails, discarded values never sent, the language a dictation starts in, and
refusing to start without what it needs."""

from __future__ import annotations

import asyncio
import dataclasses
import json

import pytest
from conftest import (MODELS, MODELS_WITH_WHISPER, FakeLlama, FakeRecognizer, FakeWhisper, answer, encode, spanish_answer,
                      speech_and_pauses)
from fastapi.testclient import TestClient
from starlette.requests import Request

from ehr_dictation import api
from ehr_dictation.api import create_app
from ehr_dictation.provenance import ProvenanceError
from ehr_dictation.sessions import SessionError, Spanish
from ehr_dictation.settings import Settings, SettingsError

OWNER = "11111111-1111-1111-1111-111111111111"
HEADERS = {"Authorization": "Bearer dictation-key", "X-Dictation-Owner": OWNER}


def client(settings, llama: FakeLlama | None = None, timeout: float = 5.0) -> TestClient:
    llama = llama or FakeLlama(answer(("Speech speech.", ["speech speech"])))
    app = create_app(settings, recognizer=FakeRecognizer(), structurer=llama.structurer(timeout), models=MODELS)
    return TestClient(app)


def recording_chunks(container: str = "webm", pieces: int = 5) -> list[bytes]:
    data = encode(speech_and_pauses(("speech", 2), ("pause", 1), ("speech", 2), ("pause", 1), ("speech", 3)), container)
    size = -(-len(data) // pieces)
    return [data[i : i + size] for i in range(0, len(data), size)]


def dictate(http: TestClient, container: str = "webm") -> str:
    started = http.post("/v1/sessions", headers=HEADERS)
    assert started.status_code == 201
    session_id = started.json()["sessionId"]
    for seq, chunk in enumerate(recording_chunks(container)):
        sent = http.post(f"/v1/sessions/{session_id}/audio", params={"seq": seq}, content=chunk,
                         headers=HEADERS | {"Content-Type": "application/octet-stream"})
        assert sent.status_code == 200, sent.text
    return session_id


def test_every_route_but_health_needs_the_key(settings):
    with client(settings) as http:
        health = http.get("/health")
        assert (health.status_code, health.json()) == (200, {"status": "ok"})
        for key in (None, "Bearer wrong", "dictation-key"):
            headers = {"X-Dictation-Owner": OWNER} | ({"Authorization": key} if key else {})
            assert http.post("/v1/sessions", headers=headers).status_code == 401
            assert http.post("/v1/sessions/x/stop", headers=headers).status_code == 401


def test_session_routes_need_an_owner(settings):
    with client(settings) as http:
        refused = http.post("/v1/sessions", headers={"Authorization": "Bearer dictation-key"})

    assert (refused.status_code, refused.json()["error"]) == (400, "no_owner")


@pytest.mark.parametrize("container", ["webm", "mp4"])
def test_a_whole_dictation_in_the_containers_browsers_record(settings, container):
    with client(settings) as http:
        session_id = dictate(http, container)
        stopped = http.post(f"/v1/sessions/{session_id}/stop", headers=HEADERS)
        again = http.post(f"/v1/sessions/{session_id}/result", params={"wait": 1}, headers=HEADERS)

    result = stopped.json()
    assert stopped.status_code == 200 and again.json() == result
    assert result["transcript"]["final"] and result["transcript"]["text"].startswith("speech")
    assert result["sorting"] == "done"
    assert result["fields"]["subjective"]["status"] == "pending" and result["fields"]["subjective"]["sources"]
    assert result["provenance"]["structuring"].startswith("sha256:")


def test_a_model_timeout_keeps_the_transcript_and_says_the_fields_were_not_sorted(settings):
    with client(dataclasses.replace(settings, stop_wait_seconds=2), FakeLlama(delay=3), timeout=0.3) as http:
        session_id = dictate(http)
        result = http.post(f"/v1/sessions/{session_id}/stop", headers=HEADERS).json()

    assert (result["sorting"], result["reason"], result["fields"]) == ("unavailable", "model_timeout", None)
    assert result["transcript"]["text"]


def test_a_discarded_value_is_never_sent(settings):
    llama = FakeLlama(answer(
        ("Speech speech.", ["speech speech"]),
        plan=("Order a chest X-ray stat.", ["speech speech"]),
        orders={"value": "chest X-ray", "sources": []},
    ))
    with client(settings, llama) as http:
        session_id = dictate(http)
        response = http.post(f"/v1/sessions/{session_id}/stop", headers=HEADERS)

    assert "X-ray" not in response.text and "orders" not in response.text
    assert response.json()["fields"]["plan"]["status"] == "empty"
    assert "order_or_allergy_change" in response.json()["fields"]["plan"]["reasons"]


def test_after_cancel_the_dictation_is_gone(settings):
    with client(settings) as http:
        session_id = dictate(http)
        cancelled = http.delete(f"/v1/sessions/{session_id}", headers=HEADERS)
        after = http.post(f"/v1/sessions/{session_id}/stop", headers=HEADERS)
        unknown = http.post("/v1/sessions/not-a-session/stop", headers=HEADERS)

    assert cancelled.status_code == 204
    assert after.status_code == unknown.status_code == 404


# Also TC-UC9-03 (FR-10): bytes that aren't audio are refused at stop. The primary test is in test_sessions.py.
def test_bytes_that_are_not_audio_are_refused_at_stop(settings):
    with client(settings) as http:
        session_id = http.post("/v1/sessions", headers=HEADERS).json()["sessionId"]
        http.post(f"/v1/sessions/{session_id}/audio", params={"seq": 0}, content=b"not audio " * 100, headers=HEADERS)
        refused = http.post(f"/v1/sessions/{session_id}/stop", headers=HEADERS)

    assert (refused.status_code, refused.json()["error"]) == (422, "not_audio")


def test_a_chunk_over_the_limit_without_a_length_is_refused(settings):
    def pieces():
        for _ in range(4):
            yield b"\0" * (settings.max_chunk_bytes // 2)

    with client(settings) as http:
        session_id = http.post("/v1/sessions", headers=HEADERS).json()["sessionId"]
        refused = http.post(f"/v1/sessions/{session_id}/audio", params={"seq": 0}, content=pieces(), headers=HEADERS)

    assert (refused.status_code, refused.json()["error"]) == (413, "too_large")


def test_a_body_without_a_length_is_read_only_until_it_passes_the_limit():
    pulled = 0

    async def receive():
        nonlocal pulled
        pulled += 1
        return {"type": "http.request", "body": b"\0" * 1000, "more_body": pulled < 10}

    request = Request({"type": "http", "method": "POST", "headers": []}, receive)
    with pytest.raises(SessionError) as refused:
        asyncio.run(api._read(request, 2500))

    assert (refused.value.status, refused.value.code, pulled) == (413, "too_large", 3)


def test_a_chunk_over_the_limit_is_refused_before_it_is_read(settings):
    with client(settings) as http:
        session_id = http.post("/v1/sessions", headers=HEADERS).json()["sessionId"]
        refused = http.post(f"/v1/sessions/{session_id}/audio", params={"seq": 0},
                            content=b"\0" * (settings.max_chunk_bytes + 1), headers=HEADERS)
        no_seq = http.post(f"/v1/sessions/{session_id}/audio", content=b"\0", headers=HEADERS)

    assert (refused.status_code, refused.json()["error"]) == (413, "too_large")
    assert no_seq.status_code == 400 and "input" not in no_seq.text


def test_refuses_to_start_without_its_keys():
    with pytest.raises(SettingsError, match="DICTATION_API_KEY"):
        Settings.from_environment({"LLAMA_API_KEY": "x"})
    with pytest.raises(SettingsError, match="LLAMA_API_KEY"):
        Settings.from_environment({"DICTATION_API_KEY": "x"})


def test_refuses_to_start_when_medasr_isnt_the_model_the_manifest_records(settings):
    (settings.models_dir / "medasr").mkdir()
    (settings.models_dir / "medasr" / "model.safetensors").write_bytes(b"something else")
    (settings.models_dir / "models-manifest.json").write_text(json.dumps({"medasr": {
        "repository": "google/medasr", "commit": "a" * 40,
        "files": [{"path": "model.safetensors", "size": 1, "sha256": "0" * 64}]}}))
    app = create_app(settings, recognizer=FakeRecognizer(), structurer=FakeLlama().structurer())

    with pytest.raises(ProvenanceError), TestClient(app):
        pass


# ---- The language a dictation starts in (the expansion plan's step 17) ----------------------------------------------


def spanish_client(settings) -> TestClient:
    english = FakeLlama(answer(("Speech speech.", ["speech speech"])))
    spanish = Spanish(FakeWhisper(), FakeLlama(spanish_answer()).structurer(language="es"))
    app = create_app(settings, recognizer=FakeRecognizer(), structurer=english.structurer(), models=MODELS_WITH_WHISPER,
                     spanish=spanish)
    return TestClient(app)


@pytest.mark.parametrize(("body", "seconds"), [
    (None, 180), (b"", 180), (b"{}", 180), (b'{"spokenLanguage": "en"}', 180), (b'{"spokenLanguage": "es"}', 120),
])
def test_a_dictation_starts_in_english_unless_asked_for_spanish(settings, body, seconds):
    with spanish_client(settings) as http:
        started = http.post("/v1/sessions", headers=HEADERS, content=body)

    assert started.status_code == 201 and started.json()["maxSeconds"] == seconds


@pytest.mark.parametrize("body", [
    b'{"spokenLanguage": "fr"}', b'{"spokenLanguage": "ES"}', b'{"spokenLanguage": " es"}', b'{"spokenLanguage": null}',
    b'{"spokenLanguage": 1}', b'{"spokenLanguage": ["es"]}', b'{"spokenLanguage": "es", "other": 1}', b'"es"', b"[]",
    b"es", b"{", b'{"spokenLanguage": "es"}' + b" " * 1024,
])
def test_any_other_start_is_refused_as_a_bad_language(settings, body):
    with spanish_client(settings) as http:
        refused = http.post("/v1/sessions", headers=HEADERS, content=body)
        listed = http.app.state.sessions.count

    assert (refused.status_code, refused.json()["error"]) == (400, "bad_language") and listed == 0


def test_spanish_without_whisper_is_refused_as_unavailable(settings):
    with client(settings) as http:
        refused = http.post("/v1/sessions", headers=HEADERS, content=b'{"spokenLanguage": "es"}')
        english = http.post("/v1/sessions", headers=HEADERS)

    assert (refused.status_code, refused.json()) == (503, {
        "error": "language_unavailable", "detail": "Spanish dictation isn't installed on this server."})
    assert english.status_code == 201
