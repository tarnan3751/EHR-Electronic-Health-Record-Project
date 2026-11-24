"""run_a.py and scoring_es.py, run A of the expansion plan's step 17, with a stand-in for K2 Horizon: a script's text
goes through the Spanish prompt and checks as the sidecar's own code, comes back scored, each stop threshold says
whether it was crossed, each statement is compared with the Spanish it cites, stored answers can be scored again, and a
run writes its results as it goes."""

from __future__ import annotations

import asyncio
import dataclasses
import json

import httpx2
import pytest

from dictation_scripts_es import HOLDOUT_DIR, load_scripts
from ehr_dictation import prompt, translation
from ehr_dictation.llama import LlamaServer, Structurer
from ehr_dictation.provenance import Models
from ehr_eval import run_a, scoring_es

SCRIPTS = {script.id: script for script in load_scripts() + load_scripts(HOLDOUT_DIR)}  # the held-out ones too


def structurer(content: dict | str, finish_reason: str = "stop") -> Structurer:
    return Structurer(llama_server(content, finish_reason), prompt.load(language="es"), "K2.gguf", timeout=5)


def llama_server(content: dict | str, finish_reason: str = "stop",
                 server: type[LlamaServer] = LlamaServer) -> LlamaServer:
    async def handle(request: httpx2.Request) -> httpx2.Response:
        if request.url.path == "/props":
            return httpx2.Response(200, json={"model_path": "/models/K2.gguf", "build_info": "b11669-test",
                                              "default_generation_settings": {"n_ctx": 8192}})
        if request.url.path == "/tokenize":
            return httpx2.Response(200, json={"tokens": list(range(len(json.loads(request.content)["content"].split())))})
        text = content if isinstance(content, str) else json.dumps(content)
        return httpx2.Response(200, json={"choices": [{"finish_reason": finish_reason, "message": {"content": text}}],
                                          "timings": {"prompt_n": 900, "predicted_n": 700}})

    return server("http://llama.test", "key", transport=httpx2.MockTransport(handle))


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
    assert metrics["instructions"]["reported_not_carried_over"] == metrics["instructions"]["said"] == 2
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
    # check the meaning of every word (translation.py), so it is offered; the eval sees a finding the script's facts
    # say is present ("headache", said in another passage) shown negated, and the step stops.
    wrong = [{"spanish": ["Niega dolor torácico y niega disnea."], "english": "Denies headache and denies shortness of breath."}]
    item = run("e01-hta-control", gold_answer("e01-hta-control", subjective=wrong))
    metrics = scoring_es.score([item], SCRIPTS)

    assert item.fields["subjective"].status == "pending"
    assert metrics["mistranslations"]["unflagged"]["negation"] == 1
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
    # The held-out scripts' too: their gold is an answer the scoring must count as right.
    items = [run(script_id, gold_answer(script_id)) for script_id in SCRIPTS]
    mistranslations = scoring_es.score(items, SCRIPTS)["mistranslations"]
    assert mistranslations["listed"] == [] and mistranslations["misfiled"]["listed"] == []


def test_a_passage_filed_in_another_field_is_misfiled_not_changed_and_stops_the_step_apart():
    # The Plan said again as Subjective, its own field left out: every number and change in it is what the clinician
    # said, so the first threshold isn't crossed; Subjective's gold doesn't have them, which is counted apart, and
    # crosses the second, as the first scorer counted it as changed.
    plan = SCRIPTS["e09-depresion"].statements["plan"]
    moved = [{"spanish": list(plan[0].spanish), "english": plan[0].english}]
    item = run("e09-depresion", gold_answer("e09-depresion", subjective=moved, plan=[]))
    metrics = scoring_es.score([item], SCRIPTS)

    assert item.fields["subjective"].status == "pending"
    assert metrics["mistranslations"]["unflagged_total"] == 0
    assert {(e["kind"], e["shown"]) for e in metrics["mistranslations"]["misfiled"]["listed"]} == {
        ("number", "100 mg"), ("action", "increase")}
    changed, misfiled, *_ = scoring_es.thresholds(metrics)
    assert not changed[1] and misfiled[1] and misfiled[0].startswith("a value the Spanish it cites says, filed in")
    assert "STOP" in run_a.summary(metrics)


def test_a_passage_filed_in_the_wrong_field_counts_against_content_f1_and_apart():
    # The same answer scored twice: e14's weight and height in Objective, where its gold has them, and in Subjective.
    # Content F1 is per field, so the misplaced passage lowers precision in Subjective and recall in Objective; and it
    # is counted apart as misfiled: each value, without a warning here, and its statement.
    script = SCRIPTS["e14-nino-sano"]
    as_said = {name: [{"spanish": list(s.spanish), "english": s.english} for s in said]
               for name, said in script.statements.items()}
    weight, *rest = as_said["objective"]
    right = scoring_es.score([run("e14-nino-sano", gold_answer("e14-nino-sano"))], SCRIPTS)
    wrong = scoring_es.score([run("e14-nino-sano", gold_answer(
        "e14-nino-sano", subjective=[*as_said["subjective"], weight], objective=rest))], SCRIPTS)

    by_field = wrong["fields"]["by_field"]
    assert right["fields"]["content"]["f1"] == 1.0 > wrong["fields"]["content"]["f1"]
    assert by_field["subjective"]["content"]["precision"] < 1.0 and by_field["objective"]["content"]["recall"] < 1.0
    misfiled = wrong["mistranslations"]["misfiled"]
    assert (right["mistranslations"]["misfiled"]["misfiled_statements"], misfiled["misfiled_statements"]) == (0, 1)
    assert {(e["kind"], e["shown"], e["flagged"]) for e in misfiled["listed"]} == {
        ("number", "11 kg", False), ("number", "81 cm", False)}
    assert misfiled["unflagged_total"] == misfiled["unflagged"]["number"] == 2
    assert wrong["mistranslations"]["unflagged_total"] == 0
    assert [crossed for _, crossed, _ in scoring_es.thresholds(wrong)][:2] == [False, True]
    assert not any(crossed for _, crossed, _ in scoring_es.thresholds(right))


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
    changed, misfiled = scoring_es._statement_items("Increase sertraline to 100 mg daily.", [span], text,
                                                    script.gold["objective"].value, script)
    assert {(kind, what, moved) for kind, what, moved in changed} == {
        ("number", "100 mg (sertraline)", True), ("drug", "sertraline", True), ("action", "increase (sertraline)", True),
        ("frequency", "daily (sertraline)", True)}
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


def test_the_thresholds_before_and_after_are_paired_by_name_not_place():
    # A stored run scored before the misfiled line was inserted second: each line still meets its own.
    stored = stored_run("e01-hta-control")
    misfiled = stored["thresholds"].pop(1)
    stored["thresholds"].append({"threshold": "a line since reworded", "crossed": True, "measured": "x"})
    results, _ = run_a.rescore(stored, SCRIPTS)
    table = run_a.rescore_table(stored, results, SCRIPTS).split("\n\n")[-1]

    sorting = next(line for line in stored["thresholds"] if line["threshold"].startswith("a script not sorted"))
    assert f"{sorting['threshold']}:\n    before ok ({sorting['measured']}); after ok" in table
    assert f"{misfiled['threshold']}:\n    before not scored under this name; after ok" in table
    assert "a line since reworded (no longer a threshold under this name):\n    before CROSSED (x)" in table


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


# ---- Diagnostic runs ------------------------------------------------------------------------------------------------


def test_another_timeout_or_kept_answers_make_a_diagnostic_run_that_says_so_first():
    assert run_a.diagnostic(90.0, False) == []
    assert run_a.diagnostic(300.0, False) == ["timeout 300 s, not the sidecar's 90 s"]
    assert run_a.diagnostic(90.0, True) == ["the model's answers kept"]
    metrics = scoring_es.score([run("e01-hta-control", gold_answer("e01-hta-control"))], SCRIPTS)
    assert run_a.summary(metrics, run_a.diagnostic(300.0, True)).splitlines()[0].startswith("DIAGNOSTIC, not run A")
    assert not run_a.summary(metrics).startswith("DIAGNOSTIC")


def keeping(content: dict | str, finish_reason: str = "stop") -> scoring_es.SpanishItem:
    kept = llama_server(content, finish_reason, run_a.KeptAnswers)
    keeping = Structurer(kept, prompt.load(language="es"), "K2.gguf", timeout=5)
    return asyncio.run(run_a.run_script(SCRIPTS["e16-anadido-corto"], keeping, kept))


def test_the_answer_as_written_is_kept_only_when_asked_even_one_the_sidecar_refuses():
    content = gold_answer("e16-anadido-corto")
    assert json.loads(keeping(content).answer) == content and run("e16-anadido-corto", content).answer is None
    cut_off = keeping(content, finish_reason="length")
    assert cut_off.sorting == "unavailable" and json.loads(cut_off.answer) == content
    assert keeping("not json").answer == "not json"


TEST_SET = sorted(script_id for script_id in SCRIPTS if script_id.startswith("e"))


def test_scoring_again_needs_somewhere_to_write(tmp_path):
    stored = tmp_path / "run.json"
    stored.write_text(json.dumps(stored_run(*TEST_SET)), encoding="utf-8")
    assert run_a.main(["--rescore", str(stored)]) == 2
    assert run_a.main(["--rescore", str(stored), "--out", str(tmp_path / "again.json")]) == 0
    assert json.loads((tmp_path / "again.json").read_text(encoding="utf-8"))["rescored"]


# The product owner's rule: a script that timed out is a failure in every measure, or the run is repeated; never left
# out. A results file missing a script, holding one twice, or with one neither sorted nor failed isn't scored again.
@pytest.mark.parametrize(("change", "says"), [
    (lambda items: items[1:], "no item for e01-hta-control"),
    (lambda items: [*items, items[0]], "held twice: e01-hta-control"),
    (lambda items: [{**items[0], "sorting": "skipped", "fields": {}}, *items[1:]], "neither sorted nor failed: e01"),
])
def test_a_results_file_that_isnt_a_whole_run_is_refused(tmp_path, capsys, change, says):
    stored = stored_run(*TEST_SET)
    stored["items"] = change(stored["items"])
    (tmp_path / "run.json").write_text(json.dumps(stored), encoding="utf-8")

    assert run_a.main(["--rescore", str(tmp_path / "run.json"), "--out", str(tmp_path / "again.json")]) == 2
    assert says in capsys.readouterr().err and not (tmp_path / "again.json").exists()


def test_a_script_that_timed_out_is_a_failure_in_every_measure_and_a_partial_run_says_it_is_incomplete():
    timed_out = scoring_es.SpanishItem(variant="e14-nino-sano--text", script="e14-nino-sano", tags=[], hypothesis="",
                                       sorting="unavailable", reason="model_timeout")
    metrics = scoring_es.score([timed_out, run("e16-anadido-corto", gold_answer("e16-anadido-corto"))], SCRIPTS)

    crossed = {name: over for name, over, _ in scoring_es.thresholds(metrics)}
    assert crossed[next(name for name in crossed if name.startswith("a script not sorted"))]
    assert metrics["fields"]["detection"]["fn"] == sum(bool(s) for s in SCRIPTS["e14-nino-sano"].statements.values())
    assert metrics["fields"]["content"]["recall"] < 1.0
    with_gold = sum(bool(g.value) for g in SCRIPTS["e14-nino-sano"].gold.values())
    assert metrics["discarded"]["discarded"] == with_gold  # e16's gold answer discards nothing
    gap = run_a.incomplete([dataclasses.asdict(timed_out)], TEST_SET)
    assert gap.startswith("no item for e01-hta-control") and "e14" not in gap  # timed out is there, as a failure
    assert run_a.summary(metrics, None, gap).startswith("INCOMPLETE, not run A")


def test_a_run_of_the_held_out_scripts_is_scored_again_against_them():
    assert run_a.stored_scripts_dir({"dataset": {"holdout": True}}) == HOLDOUT_DIR
    assert run_a.stored_scripts_dir({"dataset": {"scripts": 16}}) != HOLDOUT_DIR


# ---- The results file, written as the run goes ----------------------------------------------------------------------


def run_a_with(tmp_path, monkeypatch, fault: str, only: tuple[str, ...] = ("--only", "e14")) -> dict:  # noqa: ANN001
    """Run A (on e14 alone, unless `only` says otherwise) with a stand-in for llama-server answering e14's gold, and a
    fault in the checks or the scoring: what it left."""
    content = gold_answer("e14-nino-sano")
    monkeypatch.setattr(run_a, "LlamaServer", lambda url, key: llama_server(content))
    monkeypatch.setattr(run_a.provenance, "load", lambda models_dir, verify: Models(
        {"repository": "google/medasr", "commit": "a" * 40, "sha256": "b" * 64},
        {"repository": "IFM/K2-Horizon-7B-GGUF", "commit": "c" * 40, "file": "K2.gguf", "sha256": "d" * 64}))
    monkeypatch.setenv("LLAMA_API_KEY", "key")

    def fails(*args, **kwargs):  # noqa: ANN002, ANN003, ANN202
        raise RuntimeError(f"a fault in the {fault}")

    monkeypatch.setattr(*{"scoring": (scoring_es, "score"), "checks": (translation, "check")}[fault], fails)
    out = tmp_path / "run-a.json"
    with pytest.raises(RuntimeError, match=f"a fault in the {fault}"):
        run_a.main(["--llama-url", "http://llama.test", "--models-dir", str(tmp_path), *only,
                    "--machine", "stand-ins", "--out", str(out)])
    monkeypatch.undo()
    return json.loads(out.read_text(encoding="utf-8"))


def test_a_fault_in_the_scoring_leaves_every_answer_for_rescore_to_finish(tmp_path, monkeypatch, capsys):
    stored = run_a_with(tmp_path, monkeypatch, "scoring", only=())  # the whole set: a partial run isn't scored again
    e14 = next(item for item in stored["items"] if item["script"] == "e14-nino-sano")
    assert "metrics" not in stored and "thresholds" not in stored and e14["fields"]["objective"]["status"] == "pending"
    assert sorted(item["script"] for item in stored["items"]) == TEST_SET

    (tmp_path / "stopped.json").write_text(json.dumps(stored), encoding="utf-8")
    assert run_a.main(["--rescore", str(tmp_path / "stopped.json"), "--out", str(tmp_path / "finished.json")]) == 0
    finished = json.loads((tmp_path / "finished.json").read_text(encoding="utf-8"))
    assert finished["metrics"]["fields"]["content"]["f1"] is not None and len(finished["thresholds"]) == 6
    assert "discarded before: not scored" in capsys.readouterr().out


def test_a_fault_in_the_checks_leaves_the_answer_as_k2_horizon_gave_it(tmp_path, monkeypatch):
    stored = run_a_with(tmp_path, monkeypatch, "checks")
    (item,) = stored["items"]
    assert item["sorting"] == "done" and item["fields"]["objective"]["statements"]
    assert item["fields"]["objective"]["status"] == "empty" and not item["fields"]["objective"]["shown_statements"]

    results, items = run_a.rescore(stored, SCRIPTS)
    assert items[0].fields["objective"].status == "pending" and results["metrics"]["fields"]["content"]["f1"] == 1.0
