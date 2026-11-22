"""llama.py: the key on every request, the right model, the answer read and the reasoning dropped, and each way K2
Horizon can fail named so the transcript can go back unsorted (UC 10)."""

from __future__ import annotations

import asyncio
import json

import httpx2
import pytest
from conftest import FakeLlama, answer

from ehr_dictation import prompt
from ehr_dictation.llama import (
    LlamaServer,
    ModelOutputInvalid,
    ModelTimeout,
    ModelUnavailable,
    Structurer,
    TranscriptTooLong,
    parse_answer,
)


def propose(llama: FakeLlama, timeout: float = 5.0):
    return asyncio.run(llama.structurer(timeout).propose("Cough for three days."))


def test_every_request_carries_the_api_key_and_the_model_is_checked_first():
    llama = FakeLlama(answer(("Cough for three days.", ["Cough for three days"])))

    proposal = propose(llama)

    assert [r.url.path for r in llama.requests] == ["/props", "/tokenize", "/tokenize", "/v1/chat/completions"]
    assert all(r.headers["authorization"] == "Bearer llama-key" for r in llama.requests)
    assert proposal.fields["subjective"] == {"value": "Cough for three days.", "sources": ["Cough for three days"]}
    body = json.loads(llama.requests[3].content)
    assert body["messages"][1]["content"].endswith("Cough for three days.\n</transcript>")


def test_the_reasoning_is_dropped_and_extra_keys_are_reported():
    proposal = propose(FakeLlama(answer(orders={"value": "CBC", "sources": []})))

    assert proposal.unknown == ["orders"]
    assert "thinking" not in repr(proposal)


def test_a_different_model_file_is_refused():
    with pytest.raises(ModelUnavailable):
        propose(FakeLlama(model_path="/models/some-other-model.gguf"))


def test_an_error_or_a_refused_key_means_the_model_is_unavailable():
    with pytest.raises(ModelUnavailable):
        propose(FakeLlama(status=401))
    with pytest.raises(ModelUnavailable):
        propose(FakeLlama(status=503))


def test_no_connection_means_the_model_is_unavailable():
    def refuse(request):
        raise httpx2.ConnectError("refused", request=request)

    server = LlamaServer("http://llama.test", "key", transport=httpx2.MockTransport(refuse))
    with pytest.raises(ModelUnavailable):
        asyncio.run(Structurer(server, prompt.load(), "K2-Horizon-7B-Q4_K_M.gguf", timeout=5).propose("text"))


def test_a_slow_answer_is_a_timeout():
    with pytest.raises(ModelTimeout):
        propose(FakeLlama(delay=2.0), timeout=0.2)


def test_an_answer_cut_off_or_not_json_is_invalid():
    with pytest.raises(ModelOutputInvalid):
        propose(FakeLlama(finish_reason="length"))
    with pytest.raises(ModelOutputInvalid):
        propose(FakeLlama("Here are the fields you asked for"))


@pytest.mark.parametrize("content", ["[]", '{"subjective": "a string"}', json.dumps({
    "subjective": {"value": "x", "sources": [1]}, "objective": {"value": "", "sources": []}, "assessment": {"value": "", "sources": []},
    "plan": {"value": "", "sources": []}})])
def test_shapes_other_than_the_schemas_are_invalid(content):
    with pytest.raises(ModelOutputInvalid):
        parse_answer(content)


def test_a_transcript_that_would_not_fit_the_context_is_never_sent():
    llama = FakeLlama(n_ctx=2048 + 64 + 100)

    with pytest.raises(TranscriptTooLong):
        asyncio.run(llama.structurer().propose("word " * 200))

    assert "/v1/chat/completions" not in [r.url.path for r in llama.requests]


def test_nothing_is_sent_while_llama_server_does_not_say_its_context():
    llama = FakeLlama(answer(("Cough.", ["cough"])), n_ctx=None)

    with pytest.raises(ModelUnavailable):
        asyncio.run(llama.structurer().propose("Cough for three days."))

    assert "/v1/chat/completions" not in [r.url.path for r in llama.requests]


def test_the_model_file_is_checked_before_every_request():
    llama = FakeLlama(answer(("Cough.", ["cough"])))
    structurer = llama.structurer()
    asyncio.run(structurer.propose("Cough for three days."))

    llama.model_path = "/models/another.gguf"

    with pytest.raises(ModelUnavailable):
        asyncio.run(structurer.propose("Cough for three days."))
    assert structurer.build is None


# ---- Spanish dictation (the expansion plan's step 17) ---------------------------------------------------------------


def spanish_structurer(llama: FakeLlama) -> Structurer:
    server = LlamaServer("http://llama.test", "llama-key", transport=httpx2.MockTransport(llama.handle))
    return Structurer(server, prompt.load(language="es"), "K2-Horizon-7B-Q4_K_M.gguf", timeout=5)


def test_a_spanish_dictation_is_sent_the_spanish_prompt_and_its_statements_come_back_with_the_answer_as_written():
    content = json.dumps({"statements": [{"spanish": ["Niega fiebre."], "field": "subjective", "english": "Denies fever."}],
                          "orders": []})
    llama = FakeLlama(content)

    proposal = asyncio.run(spanish_structurer(llama).propose("Niega fiebre."))

    sent = json.loads(llama.requests[-1].content)
    assert sent["messages"][0]["content"].startswith("You sort a clinician's dictated transcript, spoken in Spanish")
    assert proposal.fields["subjective"] == [{"spanish": ["Niega fiebre."], "english": "Denies fever."}]
    assert proposal.fields["plan"] == [] and proposal.unknown == ["orders"] and proposal.content == content


def test_a_spanish_answer_in_the_english_shape_is_invalid():
    with pytest.raises(ModelOutputInvalid):
        asyncio.run(spanish_structurer(FakeLlama(answer(("Cough.", ["cough"])))).propose("Tos."))


def test_an_english_dictations_proposal_keeps_no_answer_text():
    proposal = propose(FakeLlama(answer(("Cough for three days.", ["Cough for three days"]))))
    assert proposal.content is None
