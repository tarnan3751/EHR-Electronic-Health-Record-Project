"""gate.py's Spanish checks (the expansion plan's step 17): what CI accepts as the committed Spanish eval results,
eval/results/latest-es.json, and what it says to do when it doesn't. The English checks are test_gate.py's, unchanged."""

from __future__ import annotations

import json
import shutil

import pytest

from ehr_dictation import prompt
from ehr_eval.gate import check_spanish
from ehr_eval.scripts import EVAL_DIR, gold_es_sha256, pipeline_sha256, recordings_es_sha256, scoring_sha256

SPANISH = ["e14-nino-sano", "e16-anadido-corto"]


@pytest.fixture
def spanish_dir(eval_dir):
    (eval_dir / "scripts-es").mkdir()
    for name in SPANISH:
        shutil.copy(EVAL_DIR / "scripts-es" / f"{name}.toml", eval_dir / "scripts-es")
    with (eval_dir / "voices.toml").open("a", encoding="utf-8") as voices:
        voices.write('\n[variants_es]\nenglish_as_spanish = "d02"\n')
    return eval_dir


def results(eval_dir, **metrics) -> dict:  # noqa: ANN001, ANN003
    items = ([{"variant": f"{s}--clean", "script": s} for s in SPANISH] + [{"variant": f"{s}--text", "script": s} for s in SPANISH]
             + [{"variant": "d02--clean--as-es", "script": "d02", "run_as": "es"},
                {"variant": f"{SPANISH[0]}--clean--as-en", "script": SPANISH[0], "run_as": "en"}])
    return {
        "baseline": False,
        "run": {"machine": {"label": "the server"}},
        "provenance": {"structuring": prompt.load(language="es").structuring_sha256, "pipeline": pipeline_sha256(eval_dir),
                       "scoring": scoring_sha256(eval_dir)},
        "dataset": {"recordings_sha256": recordings_es_sha256(eval_dir), "gold_sha256": gold_es_sha256(eval_dir),
                    "complete": True, "recordings": 4},
        "metrics": {"wer": {"overall": 0.12, "no_accents": 0.1}, "fields": {"content": {"f1": 0.7}},
                    "drugs": {"accuracy": 0.8}, "dates": {"accuracy": 1.0}, "sorting": {"unavailable": {}},
                    "sorted_every_item": True, "instructions_shown": 0, "mistranslations": {"listed": []},
                    "text": {"mistranslations": {"listed": []}, "fields": {"content": {"f1": 0.75}},
                             "sorting": {"unavailable": {}}}} | metrics,
        "items": items,
    }


def save(eval_dir, name: str, data: dict) -> None:  # noqa: ANN001
    (eval_dir / "results").mkdir(exist_ok=True)
    (eval_dir / "results" / name).write_text(json.dumps(data))


def problems(eval_dir) -> list[str]:  # noqa: ANN001
    return check_spanish(eval_dir / "results", eval_dir)


MISTRANSLATION = {"variant": "e14-nino-sano--clean", "field": "objective", "kind": "number", "shown": "12 kg",
                  "flagged": False, "moved_from_another_passage": False, "statement": "Weight 12 kg."}


def test_matching_spanish_results_pass(spanish_dir):
    save(spanish_dir, "latest-es.json", results(spanish_dir))

    assert problems(spanish_dir) == []


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


@pytest.mark.parametrize("missing", ["e16-anadido-corto--clean", "e16-anadido-corto--text", "d02--clean--as-es",
                                     "e14-nino-sano--clean--as-en"])
def test_results_missing_a_recording_a_text_or_a_wrong_language_item_fail(spanish_dir, missing):
    data = results(spanish_dir)
    data["items"] = [item for item in data["items"] if item["variant"] != missing]
    data["dataset"]["recordings"] = sum(1 for item in data["items"] if not item["variant"].endswith("--text"))
    save(spanish_dir, "latest-es.json", data)

    (problem,) = problems(spanish_dir)

    assert problem.startswith("The Spanish results cover")


@pytest.mark.parametrize(("metrics", "says"), [
    ({"sorted_every_item": False}, "didn't sort every Spanish recording and text"),
    ({"instructions_shown": 1}, "FR-14"),
])
def test_an_unsorted_item_or_an_instruction_shown_fails(spanish_dir, metrics, says):
    save(spanish_dir, "latest-es.json", results(spanish_dir, **metrics))

    (problem,) = problems(spanish_dir)

    assert says in problem


def test_a_mistranslation_shown_without_a_warning_fails_unless_the_owner_accepted_it(spanish_dir):
    save(spanish_dir, "latest-es.json", results(spanish_dir, mistranslations={"listed": [MISTRANSLATION]}))
    (problem,) = problems(spanish_dir)
    assert "shows '12 kg' in objective" in problem and "[[accepted_mistranslation]]" in problem

    (spanish_dir / "known-limitations.toml").write_text(
        '[[accepted_mistranslation]]\nvariant = "e14-nino-sano--clean"\nfield = "objective"\nkind = "number"\n'
        'shown = "12 kg"\nwhy = "synthetic"\n', encoding="utf-8")
    data = results(spanish_dir, mistranslations={"listed": [MISTRANSLATION]})
    save(spanish_dir, "latest-es.json", data)
    assert problems(spanish_dir) == []

    save(spanish_dir, "latest-es.json", results(spanish_dir))
    (stale,) = problems(spanish_dir)
    assert "no longer show" in stale


def test_a_mistranslation_with_a_warning_or_in_a_text_item_is_judged_the_same(spanish_dir):
    flagged = MISTRANSLATION | {"flagged": True}
    in_text = MISTRANSLATION | {"variant": "e14-nino-sano--text"}
    text = results(spanish_dir)["metrics"]["text"] | {"mistranslations": {"listed": [in_text]}}
    save(spanish_dir, "latest-es.json", results(spanish_dir, mistranslations={"listed": [flagged]}, text=text))

    (problem,) = problems(spanish_dir)

    assert "e14-nino-sano--text shows '12 kg'" in problem


@pytest.mark.parametrize(("label", "wer", "says"), [
    ("the server", 0.2, "Spanish word error rate is 0.2, worse"),
    ("a laptop", 0.12, "recorded on 'the server'"),
])
def test_a_spanish_baseline_holds_the_results_to_its_machine_and_scores(spanish_dir, label, wer, says):
    save(spanish_dir, "baseline-es.json", results(spanish_dir) | {"baseline": True})
    data = results(spanish_dir, wer={"overall": wer, "no_accents": 0.1})
    data["run"]["machine"]["label"] = label
    save(spanish_dir, "latest-es.json", data)

    (problem,) = problems(spanish_dir)

    assert says in problem
