"""Nothing a clinician says reaches a log: a whole dictation, sorted and checked, logs no transcript text and no field
value at any level, from the sidecar or the libraries it uses."""

from __future__ import annotations

import logging

from conftest import MODELS, FakeLlama, FakeRecognizer, answer
from fastapi.testclient import TestClient
from test_api import HEADERS, recording_chunks

from ehr_dictation.api import create_app

SPOKEN = "zebrafinch"
PROPOSED = "Quokkas twice daily"


def test_a_dictation_logs_none_of_its_words(settings, caplog, capfd):
    caplog.set_level(logging.DEBUG)
    records: list[logging.LogRecord] = []
    collector = logging.Handler(logging.DEBUG)
    collector.emit = records.append
    logging.getLogger("ehr_dictation").addHandler(collector)
    llama = FakeLlama(answer((PROPOSED, [f"{SPOKEN} {SPOKEN}"]), plan=("Order zebrafinch panel.", [SPOKEN])))
    app = create_app(settings, recognizer=FakeRecognizer(SPOKEN), structurer=llama.structurer(), models=MODELS)
    try:
        with TestClient(app) as http:
            session_id = http.post("/v1/sessions", headers=HEADERS).json()["sessionId"]
            for seq, chunk in enumerate(recording_chunks()):
                http.post(f"/v1/sessions/{session_id}/audio", params={"seq": seq}, content=chunk, headers=HEADERS)
            result = http.post(f"/v1/sessions/{session_id}/stop", headers=HEADERS).json()
    finally:
        logging.getLogger("ehr_dictation").removeHandler(collector)

    assert SPOKEN in result["transcript"]["text"]
    logged = "\n".join(r.getMessage() for r in records + caplog.records) + "".join(capfd.readouterr())
    assert records, "the sidecar logged nothing at all"
    assert SPOKEN not in logged.lower() and "quokka" not in logged.lower()
