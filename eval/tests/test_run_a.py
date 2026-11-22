"""run_a.py and scoring_es.py, run A of the expansion plan's step 17, with a stand-in for K2 Horizon: a script's text
goes through the Spanish prompt and checks as the sidecar's own code, comes back scored, each stop threshold says
whether it was crossed, each statement is compared with the Spanish it cites, and stored answers can be scored again."""

from __future__ import annotations

import asyncio
import dataclasses
import json

import httpx2
import pytest

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


# ---- Changed from the Spanish cited, or misfiled --------------------------------------------------------------------


def test_every_scripts_gold_answer_changes_nothing_and_misfiles_nothing():
    items = [run(script_id, gold_answer(script_id)) for script_id in SCRIPTS]
    mistranslations = scoring_es.score(items, SCRIPTS)["mistranslations"]
    assert mistranslations["listed"] == [] and mistranslations["misfiled"]["listed"] == []


def test_a_passage_filed_in_another_field_is_misfiled_not_changed():
    # The Plan said again as Subjective, its own field left out: every number and change in it is what the clinician
    # said, so the first threshold isn't crossed; Subjective's gold doesn't have them, which is reported apart.
    plan = SCRIPTS["e09-depresion"].statements["plan"]
    moved = [{"spanish": list(plan[0].spanish), "english": plan[0].english}]
    item = run("e09-depresion", gold_answer("e09-depresion", subjective=moved, plan=[]))
    metrics = scoring_es.score([item], SCRIPTS)

    assert item.fields["subjective"].status == "pending"
    assert metrics["mistranslations"]["unflagged_total"] == 0
    assert {(e["kind"], e["shown"]) for e in metrics["mistranslations"]["misfiled"]["listed"]} == {
        ("number", "100 mg"), ("action", "increase")}
    assert not scoring_es.thresholds(metrics)[0][1]


def test_a_drug_named_as_said_is_neither_changed_nor_misfiled_when_the_gold_names_it_otherwise():
    # "Salbutamol" said and written, the gold's "albuterol": the same drug.
    plan = [{"spanish": ["continuar salbutamol inhalado 2 disparos cada 4 horas si es necesario."],
             "english": "Continue inhaled salbutamol 2 puffs every 4 hours as needed."}]
    script = SCRIPTS["e05-asma-nino"]
    rest = [{"spanish": list(s.spanish), "english": s.english} for s in script.statements["plan"][1:]]
    item = run("e05-asma-nino", gold_answer("e05-asma-nino", plan=plan + rest))
    metrics = scoring_es.score([item], SCRIPTS)

    assert item.fields["plan"].status == "pending"
    assert metrics["mistranslations"]["listed"] == [] and metrics["mistranslations"]["misfiled"]["listed"] == []


def test_what_a_statement_changes_from_the_spanish_it_cites_is_marked_moved_when_said_elsewhere():
    script = SCRIPTS["e09-depresion"]
    text = script.display
    start = text.index("PHQ-9 de 12")
    span = (start, start + len("PHQ-9 de 12, previamente 18"))
    changed, misfiled = scoring_es._statement_items("Increase sertraline to 100 mg daily.", [span],
                                                    scoring_es._reading(text), script.gold["objective"].value, script)
    assert {(kind, what, moved) for kind, what, moved in changed} == {
        ("number", "100 mg", True), ("drug", "sertraline", True), ("action", "increase", True)}
    assert misfiled == []


def test_a_finding_listed_as_present_is_found_negated_only_where_a_negation_reaches_it():
    statement = "Left knee with mild effusion and crepitus, right knee without effusion."
    assert scoring_es.negated(statement, "mild effusion") == [False]
    assert scoring_es.negated(statement, "effusion") == [False, True]


# ---- Scoring stored answers again -----------------------------------------------------------------------------------


def stored_run(*script_ids: str) -> dict:
    items = [run(script_id, gold_answer(script_id)) for script_id in script_ids]
    metrics = scoring_es.score(items, SCRIPTS)
    return {"provenance": {"checks": "earlier"}, "dataset": {"scripts_sha256": "earlier"}, "metrics": metrics,
            "thresholds": [{"threshold": name, "crossed": over, "measured": measured}
                           for name, over, measured in scoring_es.thresholds(metrics)],
            "items": [dataclasses.asdict(item) for item in items]}


def test_stored_answers_scored_again_score_the_same_and_say_what_they_were_scored_with():
    stored = stored_run("e01-hta-control", "e09-depresion")
    results, _ = run_a.rescore(stored, SCRIPTS)

    assert results["metrics"] == stored["metrics"]
    assert results["rescored"]["before"]["checks"] == "earlier" and results["provenance"]["checks"] != "earlier"
    assert "e09-depresion" in run_a.rescore_table(stored, results, SCRIPTS)


def test_answers_stored_field_by_field_are_scored_again():
    # Run A's answers were stored before the answer was one list: no statement has its place in the answer.
    stored = stored_run("e14-nino-sano")
    for outcome in stored["items"][0]["fields"].values():
        outcome.pop("shown_statements")
        for statement in outcome["statements"]:
            statement.pop("order")
    results, items = run_a.rescore(stored, SCRIPTS)
    assert all(outcome.status == "pending" for outcome in items[0].fields.values() if outcome.statements)
    assert results["metrics"]["fields"]["content"]["f1"] == 1.0


def test_a_script_whose_text_changed_since_the_run_is_refused():
    stored = stored_run("e01-hta-control")
    stored["items"][0]["hypothesis"] += " Otra frase."
    with pytest.raises(ValueError, match="text has changed"):
        run_a.rescore(stored, SCRIPTS)
