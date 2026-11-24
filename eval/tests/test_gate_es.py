"""gate.py's Spanish checks (the expansion plan's step 17): what CI accepts as the committed Spanish eval results,
eval/results/latest-es.json, and what it says to do when it doesn't. The results here are stored answers the gate can
score again: each recording heard by a stand-in as its script's display text, and answered with its gold, through the
sidecar's own checks; a fault is put in the file, or in the test set's copy (a fact, a forbidden phrase), never in the
checks. The English checks are test_gate.py's, unchanged."""

from __future__ import annotations

import dataclasses
import json
import shutil
from pathlib import Path

import numpy as np
import pytest
from conftest import ScriptedRecognizer

from dictation_scripts import load_scripts as load_english
from dictation_scripts_es import load_scripts
from ehr_dictation import prompt, spanish
from ehr_dictation.audio import SAMPLE_RATE
from ehr_dictation.prompt import FIELDS
from ehr_dictation.transcriber import Progress, advance
from ehr_dictation.transcript import assemble
from ehr_eval import run_es, scoring_es
from ehr_eval.answers_es import variant_tags
from ehr_eval.gate import check_spanish
from ehr_eval.run_a import check, text_transcript
from ehr_eval.scripts import (EVAL_DIR, gold_es_sha256, pipeline_sha256, recordings_es_sha256, scoring_sha256,
                              variant_ids)

SPANISH = ["e01-hta-control", "e14-nino-sano", "e16-anadido-corto"]
VARIANTS_ES = """
[variants_es]
snr_db = [20, 10]
unusable_snr_db = 0
unusable = ["e01-hta-control"]
english_as_spanish = "d02"
spanish_as_english = "e14-nino-sano"
"""
# Two seconds of sound, then a second of silence the passage's sound is judged against: one passage, heard clearly.
TONE = np.concatenate([0.1 * np.sin(2 * np.pi * 220 * np.arange(2 * SAMPLE_RATE) / SAMPLE_RATE),
                       np.zeros(SAMPLE_RATE)]).astype(np.float32)


@pytest.fixture
def spanish_dir(eval_dir: Path) -> Path:
    (eval_dir / "scripts-es").mkdir()
    for name in SPANISH:
        shutil.copy(EVAL_DIR / "scripts-es" / f"{name}.toml", eval_dir / "scripts-es")
    with (eval_dir / "voices.toml").open("a", encoding="utf-8") as voices:
        voices.write(VARIANTS_ES)
    return eval_dir


def gold(script) -> dict[str, list[dict]]:  # noqa: ANN001
    return {name: [{"spanish": list(s.spanish), "english": s.english} for s in said]
            for name, said in script.statements.items()}


def recording(variant: str, script, run_as: str | None = None) -> scoring_es.SpanishItem:  # noqa: ANN001
    """The recording heard as its script's display text, answered with its gold; one in the wrong language answered
    with nothing."""
    transcript = assemble(advance(TONE, Progress(), ScriptedRecognizer(script.display), final=True).segments, final=True)
    spec = {"accent": "es", "container": "webm", "noise": None, "run_as": run_as}
    item = scoring_es.SpanishItem(
        variant=variant, script=script.id, tags=variant_tags(spec, list(script.tags)), hypothesis=transcript.text,
        sorting="done", run_as=run_as, segments=[dataclasses.asdict(p.segment) for p in transcript.placed],
        not_carried_over=[(kind, s, e) for s, e, kind in spanish.directives(transcript.text)])
    if run_as == "en":
        run_es.check_english(item, {name: {"value": "", "sources": []} for name in FIELDS}, [], transcript)
    else:
        check(item, {name: [] for name in FIELDS} if run_as else gold(script), [], transcript)
    return item


def text(script) -> scoring_es.SpanishItem:  # noqa: ANN001
    transcript, _ = text_transcript(script)
    item = scoring_es.SpanishItem(variant=f"{script.id}--text", script=script.id, tags=list(script.tags),
                                  hypothesis=transcript.text, sorting="done")
    check(item, gold(script), [], transcript)
    return item


def results(eval_dir: Path, keep=lambda variant: True, change=lambda item: item) -> dict:  # noqa: ANN001
    """Results as run_es.py writes them, from every item the test set defines (those `keep` keeps, each as `change`
    leaves it), their measures scored as the run scores them."""
    scripts = {s.id: s for s in load_scripts(eval_dir / "scripts-es")}
    english = {s.id: s for s in load_english(eval_dir / "scripts")}
    items = []
    for variant in sorted(variant_ids(eval_dir, "es")):
        name, run_as = variant.split("--")[0], (variant.split("--as-")[1] if "--as-" in variant else None)
        items.append(recording(variant, scripts.get(name) or english[name], run_as))
    items += [text(script) for script in scripts.values()]
    items = [change(item) for item in items if keep(item.variant)]
    return {
        "baseline": False,
        "run": {"machine": {"label": "the server"}},
        "provenance": {"structuring": prompt.load(language="es").structuring_sha256, "pipeline": pipeline_sha256(eval_dir),
                       "scoring": scoring_sha256(eval_dir)},
        "dataset": {"recordings_sha256": recordings_es_sha256(eval_dir), "gold_sha256": gold_es_sha256(eval_dir),
                    "complete": True, "recordings": sum(1 for i in items if not i.variant.endswith("--text"))},
        "metrics": json.loads(json.dumps(scoring_es.score_audio(items, scripts, english))),
        "items": [dataclasses.asdict(item) for item in items],
    }


def save(eval_dir: Path, name: str, data: dict) -> None:
    (eval_dir / "results").mkdir(exist_ok=True)
    (eval_dir / "results" / name).write_text(json.dumps(data))


def problems(eval_dir: Path) -> list[str]:
    return check_spanish(eval_dir / "results", eval_dir)


def edit(eval_dir: Path, script: str, old: str, new: str) -> None:
    """Changes the test set's copy of a script, as a fault the stored answers are scored against."""
    path = eval_dir / "scripts-es" / f"{script}.toml"
    content = path.read_text(encoding="utf-8")
    assert content.count(old) == 1
    path.write_text(content.replace(old, new), encoding="utf-8")


def test_matching_spanish_results_pass(spanish_dir):
    save(spanish_dir, "latest-es.json", results(spanish_dir))

    assert problems(spanish_dir) == []


def test_the_gate_holds_the_spanish_set_to_the_recordings_the_audio_generator_makes():
    import sys
    import types

    sys.modules.setdefault("kokoro", types.SimpleNamespace(KModel=object, KPipeline=object))
    import generate_audio  # noqa: PLC0415  (its speech model is never loaded here)

    voices = generate_audio.tomllib.loads((EVAL_DIR / "voices.toml").read_text(encoding="utf-8"))["variants_es"]
    english = next(s for s in load_english() if s.id == voices["english_as_spanish"])
    made = {variant.id for variant in generate_audio.plan_spanish(load_scripts(), voices, english)}
    assert made | {f"{voices['spanish_as_english']}--clean--as-en"} == variant_ids(EVAL_DIR, "es")


def test_no_spanish_results_says_to_run_the_spanish_eval(spanish_dir):
    (problem,) = problems(spanish_dir)

    assert "no Spanish eval results" in problem and "--set es" in problem


@pytest.mark.parametrize(("part", "key", "says"), [
    ("provenance", "structuring", "Spanish prompt, note schema or generation parameters changed"),
    ("provenance", "pipeline", "What makes the transcripts and the model's answers changed"),
    ("provenance", "scoring", "--rescore"),
    ("dataset", "recordings_sha256", "Generate the audio again"),
    ("dataset", "gold_sha256", "--rescore"),
])
def test_results_from_another_prompt_pipeline_scoring_recordings_or_gold_say_what_to_do(spanish_dir, part, key, says):
    data = results(spanish_dir)
    data[part][key] = "something else"
    save(spanish_dir, "latest-es.json", data)

    (problem,) = problems(spanish_dir)

    assert says in problem


# Exactly the items the scripts and [variants_es] define: one missing, the review's "only clean recordings" (the noisy
# ones dropped and the counts made to agree), one held twice or one no script defines all fail.
@pytest.mark.parametrize("keep", [
    lambda v: v != "e16-anadido-corto--clean", lambda v: v != "e16-anadido-corto--text",
    lambda v: v != "d02--clean--as-es", lambda v: v != "e14-nino-sano--clean--as-en",
    lambda v: "db" not in v,
], ids=["a recording", "a text", "english as spanish", "spanish as english", "only clean recordings"])
def test_results_missing_a_recording_a_text_or_a_wrong_language_item_fail(spanish_dir, keep):
    save(spanish_dir, "latest-es.json", results(spanish_dir, keep))

    (problem,) = problems(spanish_dir)

    assert problem.startswith("The Spanish results hold") and "missing: " in problem and "missing: none" not in problem


@pytest.mark.parametrize("change", [
    lambda item: dataclasses.replace(item, variant="e14-nino-sano--hvac-20db") if item.variant == "e16-anadido-corto--clean"
    else item,
    lambda item: dataclasses.replace(item, variant="e14-nino-sano--clean") if item.variant == "e16-anadido-corto--clean"
    else item,
], ids=["undefined", "twice"])
def test_results_holding_an_item_twice_or_one_not_defined_fail(spanish_dir, change):
    save(spanish_dir, "latest-es.json", results(spanish_dir, change=change))

    assert any("not defined, or held twice: e14-nino-sano" in p for p in problems(spanish_dir))


# The review's F14: the gate trusted the file's measures. Each now comes from the stored answers.
def test_results_missing_a_measure_fail(spanish_dir):
    data = results(spanish_dir)
    del data["metrics"]["wer"]["no_accents"]
    del data["metrics"]["code_switching"]
    save(spanish_dir, "latest-es.json", data)

    (problem,) = problems(spanish_dir)

    assert "don't hold every measure" in problem and "wer.no_accents" in problem and "code_switching" in problem


def test_results_listing_no_mistranslation_against_a_count_fail_and_the_gate_lists_what_the_answers_give(spanish_dir):
    # The test set's copy says e14's child weighs 12 kg: its gold, "Weight 11 kg", is now a change shown without a
    # warning, in its recording and its text. The file states none.
    edit(spanish_dir, "e14-nino-sano", 'number = "11 kg"', 'number = "12 kg"')
    data = results(spanish_dir)
    assert data["metrics"]["mistranslations"]["unflagged_total"] == data["metrics"]["text"]["mistranslations"][
        "unflagged_total"] == 1
    data["metrics"]["mistranslations"]["listed"] = []
    data["metrics"]["text"]["mistranslations"]["listed"] = []
    save(spanish_dir, "latest-es.json", data)

    differ, *unaccepted = problems(spanish_dir)

    assert "aren't what their stored answers give" in differ and "mistranslations.listed" in differ
    assert [p.split(" shows ")[0] for p in unaccepted] == ["e14-nino-sano--clean", "e14-nino-sano--text"]
    assert all("shows '11 kg (weight)' in objective, a number" in p for p in unaccepted)


def test_results_stating_no_instruction_shown_when_the_answers_show_one_fail(spanish_dir):
    # The copy forbids "phone": e16's gold, "Notify the patient by phone today", now shows it, in its recording and text.
    edit(spanish_dir, "e16-anadido-corto", 'forbidden_en = ["urine test"', 'forbidden_en = ["phone", "urine test"')
    data = results(spanish_dir)
    assert data["metrics"]["instructions_shown"] == 2
    data["metrics"]["instructions_shown"] = 0
    save(spanish_dir, "latest-es.json", data)

    differ, fr14 = problems(spanish_dir)

    assert "instructions_shown" in differ and fr14.startswith("2 offered value(s)") and "FR-14" in fr14


def test_an_item_not_sorted_fails(spanish_dir):
    def unavailable(item: scoring_es.SpanishItem) -> scoring_es.SpanishItem:
        if item.variant != "e16-anadido-corto--clean":
            return item
        return dataclasses.replace(item, sorting="unavailable", reason="model_timeout", fields={})

    save(spanish_dir, "latest-es.json", results(spanish_dir, change=unavailable))

    (problem,) = problems(spanish_dir)

    assert "didn't sort every Spanish recording and text" in problem


# The product owner's rule: a timed-out item is a failure in every measure, or the run is repeated; never skipped.
def test_an_item_marked_skipped_fails(spanish_dir):
    def skipped(item: scoring_es.SpanishItem) -> scoring_es.SpanishItem:
        if item.variant != "e16-anadido-corto--clean":
            return item
        return dataclasses.replace(item, sorting="skipped", fields={})

    save(spanish_dir, "latest-es.json", results(spanish_dir, change=skipped))

    found = problems(spanish_dir)
    assert any("neither sorted nor failed (e16-anadido-corto--clean)" in p for p in found)
    assert any("didn't sort every Spanish recording and text" in p for p in found)


def test_stored_answers_that_cant_be_scored_again_fail(spanish_dir):
    def misheard(item: scoring_es.SpanishItem) -> scoring_es.SpanishItem:
        return dataclasses.replace(item, hypothesis="Not what was heard.") if item.variant == "e01-hta-control--clean" else item

    save(spanish_dir, "latest-es.json", results(spanish_dir, change=misheard))

    assert any("can't be scored again" in p for p in problems(spanish_dir))


def test_a_mistranslation_shown_without_a_warning_fails_unless_the_owner_accepted_it(spanish_dir):
    edit(spanish_dir, "e14-nino-sano", 'number = "11 kg"', 'number = "12 kg"')
    save(spanish_dir, "latest-es.json", results(spanish_dir))
    found = problems(spanish_dir)
    assert len(found) == 2 and all("[[accepted_mistranslation]]" in p for p in found)

    accepting = "".join(
        f'[[accepted_mistranslation]]\nvariant = "e14-nino-sano--{kind}"\nfield = "objective"\nkind = "number"\n'
        f'shown = "11 kg (weight)"\nwhy = "synthetic"\n' for kind in ("clean", "text"))
    (spanish_dir / "known-limitations.toml").write_text(accepting, encoding="utf-8")
    save(spanish_dir, "latest-es.json", results(spanish_dir))
    assert problems(spanish_dir) == []

    edit(spanish_dir, "e14-nino-sano", 'number = "12 kg"', 'number = "11 kg"')
    save(spanish_dir, "latest-es.json", results(spanish_dir))
    stale = problems(spanish_dir)
    assert len(stale) == 2 and all("no longer show" in p for p in stale)


def test_a_value_filed_in_the_wrong_field_without_a_warning_fails_unless_the_owner_accepted_it(spanish_dir):
    # e14's text answered with its gold, but its weight and height, Objective's first statement, filed in Subjective:
    # what the Spanish it cites says, in a field whose gold doesn't have it. Counted apart from a mistranslation, under
    # the same rule.
    script = {s.id: s for s in load_scripts(spanish_dir / "scripts-es")}["e14-nino-sano"]
    answer = gold(script)
    weight, *rest = answer["objective"]
    answer |= {"subjective": [*answer["subjective"], weight], "objective": rest}

    def refiled(item: scoring_es.SpanishItem) -> scoring_es.SpanishItem:
        if item.variant != "e14-nino-sano--text":
            return item
        transcript, _ = text_transcript(script)
        item = dataclasses.replace(item, fields={}, unknown=[])
        check(item, answer, [], transcript)
        return item

    save(spanish_dir, "latest-es.json", results(spanish_dir, change=refiled))
    found = problems(spanish_dir)
    assert [p.split(", a number ")[0] for p in found] == [
        "e14-nino-sano--text shows '11 kg' in subjective", "e14-nino-sano--text shows '81 cm' in subjective"]
    assert all("filed in the wrong field" in p and "[[accepted_misfiling]]" in p for p in found)

    accepting = "".join(
        f'[[accepted_misfiling]]\nvariant = "e14-nino-sano--text"\nfield = "subjective"\nkind = "number"\n'
        f'shown = "{shown}"\nwhy = "synthetic"\n' for shown in ("11 kg", "81 cm"))
    (spanish_dir / "known-limitations.toml").write_text(accepting, encoding="utf-8")
    save(spanish_dir, "latest-es.json", results(spanish_dir, change=refiled))
    assert problems(spanish_dir) == []

    save(spanish_dir, "latest-es.json", results(spanish_dir))
    stale = problems(spanish_dir)
    assert len(stale) == 2 and all("no longer show" in p for p in stale)


@pytest.mark.parametrize(("label", "worse", "says"), [
    ("the server", True, "Spanish word error rate is 0.0, worse"),
    ("a laptop", False, "recorded on 'the server'"),
])
def test_a_spanish_baseline_holds_the_results_to_its_machine_and_scores(spanish_dir, label, worse, says):
    data = results(spanish_dir)
    baseline = json.loads(json.dumps(data)) | {"baseline": True}
    if worse:
        baseline["metrics"]["wer"]["overall"] = -1.0  # a baseline no run can match
    save(spanish_dir, "baseline-es.json", baseline)
    data["run"]["machine"]["label"] = label
    save(spanish_dir, "latest-es.json", data)

    (problem,) = problems(spanish_dir)

    assert says in problem
