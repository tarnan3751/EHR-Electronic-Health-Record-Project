"""run_a.py and scoring_es.py, run A of the expansion plan's step 17, with a stand-in for K2 Horizon: a script's text
goes through the Spanish prompt and checks as the sidecar's own code, comes back scored, and each stop threshold says
whether it was crossed."""

from __future__ import annotations

import asyncio
import json

import httpx2

from dictation_scripts_es import load_scripts
from ehr_dictation import prompt
from ehr_dictation.llama import LlamaServer, Structurer
from ehr_eval import run_a, scoring_es

SCRIPTS = {script.id: script for script in load_scripts()}


def structurer(content: dict | str, finish_reason: str = "stop") -> Structurer:
    async def handle(request: httpx2.Request) -> httpx2.Response:
        if request.url.path == "/props":
            return httpx2.Response(200, json={"model_path": "/models/K2.gguf", "build_info": "b11669-test",
                                              "default_generation_settings": {"n_ctx": 8192}})
        if request.url.path == "/tokenize":
            return httpx2.Response(200, json={"tokens": list(range(len(json.loads(request.content)["content"].split())))})
        text = content if isinstance(content, str) else json.dumps(content)
        return httpx2.Response(200, json={"choices": [{"finish_reason": finish_reason, "message": {"content": text}}],
                                          "timings": {"prompt_n": 900, "predicted_n": 700}})

    server = LlamaServer("http://llama.test", "key", transport=httpx2.MockTransport(handle))
    return Structurer(server, prompt.load(language="es"), "K2.gguf", timeout=5)


def gold_answer(script_id: str, **changes: list[dict]) -> dict:
    """The script's gold as the Spanish schema's answer: one list, each field's statements in turn."""
    script = SCRIPTS[script_id]
    fields = {name: [{"spanish": list(s.spanish), "english": s.english} for s in statements]
              for name, statements in script.statements.items()}
    return {"statements": [{"spanish": s["spanish"], "field": name, "english": s["english"]}
                           for name in fields for s in changes.get(name, fields[name])]}


def run(script_id: str, content: dict | str, finish_reason: str = "stop") -> scoring_es.SpanishItem:
    return asyncio.run(run_a.run_script(SCRIPTS[script_id], structurer(content, finish_reason)))


def test_the_text_is_the_transcript_with_the_english_passages_marked_and_sent_as_unclear():
    transcript, sent = run_a.text_transcript(SCRIPTS["e10-codeswitch-dermatitis"])

    (start, end), = transcript.unclear_ranges()
    assert transcript.text[start:end] == "Patient was counseled on diet and exercise."
    assert "[unclear]" in sent and "counseled" not in sent and sent.replace("[unclear]", "") in transcript.text.replace(
        transcript.text[start:end], "")


def test_the_gold_answer_passes_scores_full_marks_and_crosses_no_threshold():
    item = run("e01-hta-control", gold_answer("e01-hta-control"))
    metrics = scoring_es.score([item], SCRIPTS)

    assert item.sorting == "done" and all(f.status == "pending" for f in item.fields.values())
    assert metrics["fields"]["content"]["f1"] == 1.0 and metrics["discarded"]["share"] == 0.0
    assert metrics["mistranslations"]["unflagged_total"] == 0 and metrics["instructions"]["shown"] == 0
    assert metrics["instructions"]["reported_not_carried_over"] == metrics["instructions"]["said"] == 1
    assert metrics["timing"]["statements_spanish_first"] == {"written": 11, "in_order": 11}
    assert not any(crossed for _, crossed, _ in scoring_es.thresholds(metrics))


def test_a_changed_number_the_checks_discard_is_counted_as_discarded_not_shown():
    changed = [{"spanish": ["aumentar losartán a 100 mg una vez al día."], "english": "Increase losartan to 150 mg once a day."}]
    item = run("e01-hta-control", gold_answer("e01-hta-control", plan=changed))
    metrics = scoring_es.score([item], SCRIPTS)

    assert item.fields["plan"].reasons == ["number_not_in_sources"]
    assert metrics["discarded"]["discarded"] == 1 and metrics["mistranslations"]["unflagged_total"] == 0


def test_a_mistranslation_the_checks_let_through_is_shown_and_crosses_the_first_threshold():
    # "Niega dolor torácico" translated "denies headache": the negation is kept, the finding swapped. The checks don't
    # check the meaning of every word (translation.py), so it is offered; the eval sees a finding the script says is
    # present ("headache") shown negated, and the step stops.
    wrong = [{"spanish": ["Niega dolor torácico y niega disnea."], "english": "Denies headache and denies shortness of breath."}]
    item = run("e01-hta-control", gold_answer("e01-hta-control", subjective=wrong))
    metrics = scoring_es.score([item], SCRIPTS)

    assert item.fields["subjective"].status == "pending"
    assert metrics["mistranslations"]["unflagged"]["negation_added"] == 1
    crossed = dict((name, over) for name, over, _ in scoring_es.thresholds(metrics))
    assert crossed[next(iter(crossed))] is True
    assert "STOP" in run_a.summary(metrics)


def test_an_answer_cut_off_at_the_token_limit_is_not_sorted_and_crosses_the_last_threshold():
    item = run("e14-nino-sano", gold_answer("e14-nino-sano"), finish_reason="length")
    metrics = scoring_es.score([item], SCRIPTS)

    assert item.sorting == "unavailable" and "cut off" in item.detail
    assert metrics["sorting"]["cut_off_at_the_token_limit"] == 1
    assert scoring_es.thresholds(metrics)[-1][1] is True


def test_the_order_the_model_wrote_each_statements_keys_in_is_counted():
    content = '{"statements": [{"spanish": ["no tiene preocupaciones"], "english": "No concerns.", "field": "subjective"}]}'
    assert run_a.spanish_first(content) == (1, 0)
    count = sum(len(statements) for statements in SCRIPTS["e14-nino-sano"].statements.values())
    assert run_a.spanish_first(json.dumps(gold_answer("e14-nino-sano"))) == (count, count)
