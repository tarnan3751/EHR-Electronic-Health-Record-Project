"""gate.py: what CI accepts as the committed eval results, and what it tells you to do when it doesn't."""

from __future__ import annotations

import json

from ehr_dictation import prompt
from ehr_dictation.provenance import CODE_SHA256
from ehr_eval.gate import check
from ehr_eval.scripts import dataset_sha256, measurement_sha256


def results(eval_dir, **overrides) -> dict:
    data = {
        "baseline": False,
        "run": {"machine": {"label": "the server"}},
        "provenance": {"structuring": prompt.load().structuring_sha256, "code": CODE_SHA256,
                       "measurement": measurement_sha256(eval_dir)},
        "dataset": {"sha256": dataset_sha256(eval_dir), "complete": True, "recordings": 2},
        "metrics": {"wer": {"overall": 0.1}, "fields": {"content": {"f1": 0.8}}, "dates": {"accuracy": 0.9},
                    "drugs": {"accuracy": 0.7}, "unsupported_content": {"shown": 0.02},
                    "order_allergy": {"shown": 0, "retracted_shown": 0}, "sorting": {"done": 2, "unavailable": {}}},
        "items": [{"script": "d01"}, {"script": "d02"}],
    }
    for key, value in overrides.items():
        data[key] = value
    return data


def save(eval_dir, name: str, data: dict) -> None:
    (eval_dir / "results").mkdir(exist_ok=True)
    (eval_dir / "results" / name).write_text(json.dumps(data))


def test_matching_results_with_no_baseline_pass(eval_dir):
    save(eval_dir, "latest.json", results(eval_dir))

    assert check(eval_dir / "results", eval_dir) == []


def test_no_results_says_to_run_the_eval(eval_dir):
    (problem,) = check(eval_dir / "results", eval_dir)

    assert "no eval results" in problem and "Run the eval" in problem


def test_results_from_another_prompt_code_measurement_or_test_set_fail(eval_dir):
    stale = results(eval_dir)
    stale["provenance"] = {"structuring": "0" * 64, "code": "1" * 64, "measurement": "2" * 64}
    save(eval_dir, "latest.json", stale)
    (eval_dir / "scripts" / "d01.toml").write_text(
        (eval_dir / "scripts" / "d01.toml").read_text() + "\n# edited\n")

    problems = check(eval_dir / "results", eval_dir)

    assert len(problems) == 4
    assert all("Run the eval" in p for p in problems)


def test_results_that_miss_a_script_or_a_sorting_fail(eval_dir):
    missing = results(eval_dir, items=[{"script": "d01"}])
    missing["dataset"]["recordings"] = 1
    missing["metrics"]["sorting"]["unavailable"] = {"model_timeout": 1}
    save(eval_dir, "latest.json", missing)

    problems = check(eval_dir / "results", eval_dir)

    assert any("cover 1 of the 2 scripts" in p for p in problems)
    assert any("didn't sort every recording" in p for p in problems)


def test_results_that_dont_say_what_was_shown_fail(eval_dir):
    silent = results(eval_dir)
    silent["metrics"]["order_allergy"] = {}
    save(eval_dir, "latest.json", silent)

    assert len(check(eval_dir / "results", eval_dir)) == 2


def test_partial_results_or_a_baseline_flag_fail(eval_dir):
    partial = results(eval_dir, baseline=True)
    partial["dataset"]["complete"] = False
    save(eval_dir, "latest.json", partial)

    assert len(check(eval_dir / "results", eval_dir)) == 2


def test_an_order_or_allergy_change_shown_fails(eval_dir):
    shown = results(eval_dir)
    shown["metrics"]["order_allergy"]["shown"] = 1
    save(eval_dir, "latest.json", shown)

    (problem,) = check(eval_dir / "results", eval_dir)
    assert "there must be none" in problem


def test_a_metric_the_baseline_has_and_the_results_lack_fails(eval_dir):
    save(eval_dir, "baseline.json", results(eval_dir))
    lacking = results(eval_dir)
    lacking["metrics"]["fields"]["content"]["f1"] = None
    save(eval_dir, "latest.json", lacking)

    (problem,) = check(eval_dir / "results", eval_dir)
    assert "note-field F1" in problem


def test_a_score_below_the_accepted_baseline_fails(eval_dir):
    save(eval_dir, "baseline.json", results(eval_dir))
    worse = results(eval_dir)
    worse["metrics"]["wer"]["overall"] = 0.12
    worse["metrics"]["fields"]["content"]["f1"] = 0.79
    save(eval_dir, "latest.json", worse)

    problems = check(eval_dir / "results", eval_dir)

    assert len(problems) == 2 and "word error rate" in problems[0] and "note-field F1" in problems[1]


def test_results_from_another_machine_than_the_baselines_fail(eval_dir):
    save(eval_dir, "baseline.json", results(eval_dir))
    save(eval_dir, "latest.json", results(eval_dir, run={"machine": {"label": "a laptop"}}))

    (problem,) = check(eval_dir / "results", eval_dir)
    assert "baseline's machine" in problem
