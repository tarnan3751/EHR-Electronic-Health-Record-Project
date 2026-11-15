"""scoring.py: each measure, on runs whose right answers are known."""

from __future__ import annotations

from ehr_eval.scoring import FieldOutcome, Item, score


def item(script: str, hypothesis: str, tags=("accent:us",), seconds=30.0, **fields) -> Item:
    return Item(variant=f"{script}-x", script=script, tags=list(tags), seconds=seconds, hypothesis=hypothesis,
                sorting="done", fields={n: fields.get(n, FieldOutcome()) for n in ("history", "assessment", "plan")},
                timings={"decode": 0.1, "transcribe": 1.0, "structure": 10.0})


def pending(value: str) -> FieldOutcome:
    return FieldOutcome(proposed=value, sources=[value], status="pending")


def failed(value: str, *reasons: str) -> FieldOutcome:
    return FieldOutcome(proposed=value, sources=[value], status="empty", reasons=list(reasons))


def test_word_error_rate_overall_and_per_tag(scripts):
    items = [
        item("d01", "Cough for three days. Seen on March 3. Plan: rest.", tags=["accent:us"]),
        item("d02", "Takes aprocitentan daily. Go ahead and order a chest X-ray.", tags=["accent:gb"]),
        item("d02", "Takes apricot and daily. Go ahead and order a chest X-ray.", tags=["accent:in"]),
    ]

    wer = score(items, scripts)["wer"]

    assert wer["by_tag"]["accent:us"] == 0 and wer["by_tag"]["accent:gb"] == 0
    assert wer["by_tag"]["accent:in"] == round(2 / 11, 4)
    assert 0 < wer["overall"] < wer["by_tag"]["accent:in"]


def test_dates_and_drug_names_right_wrong_and_nearly_right(scripts):
    items = [item("d01", "Seen on March 3."), item("d01", "Seen on March 4."),
             item("d02", "Takes aprocitentan."), item("d02", "Takes aprocitantan."), item("d02", "Takes nothing.")]

    metrics = score(items, scripts)

    assert metrics["dates"] == {"said": 2, "right": 1, "accuracy": 0.5}
    assert metrics["drugs"] == {"said": 3, "exact": 1, "accuracy": round(1 / 3, 4), "within_2_letters": 2}


def test_fields_detection_and_content(scripts):
    items = [
        item("d01", "x", history=pending("Cough for 3 days."), plan=pending("Rest.")),
        item("d01", "x", history=pending("Cough."), assessment=pending("Viral illness.")),
    ]

    fields = score(items, scripts)["fields"]

    # Gold: history and plan in both runs. Offered: both in the first; history and an unwanted assessment in the second.
    assert fields["detection"] | {} == {"tp": 3, "fp": 1, "fn": 1, "precision": 0.75, "recall": 0.75, "f1": 0.75}
    assert fields["content"]["precision"] == round(5 / 7, 4)
    assert fields["content"]["recall"] == round(5 / 8, 4)
    assert fields["by_field"]["plan"]["detection"]["recall"] == 0.5


def test_validation_pass_rate_and_reasons(scripts):
    items = [item("d01", "x", history=pending("Cough for 3 days."), plan=failed("Rest 5 days.", "number_not_in_sources"))]
    items[0].unknown = ["orders"]

    validation = score(items, scripts)["validation"]

    assert validation == {"proposed": 2, "passed": 1, "pass_rate": 0.5,
                          "reasons": {"number_not_in_sources": 1}, "unknown_fields": 1}


def test_unsupported_content_in_proposed_and_offered_values(scripts):
    items = [item("d01", "Cough for three days.", history=pending("Cough for 3 days."),
                  plan=failed("Antibiotics.", "unsupported_content"))]

    unsupported = score(items, scripts)["unsupported_content"]

    assert unsupported["shown"] == 0.0
    assert unsupported["proposed"] == round(1 / 4, 4)


def test_an_order_slipped_into_an_offered_value_is_counted(scripts):
    items = [item("d02", "x", plan=pending("Chest X-ray today."))]

    assert score(items, scripts)["order_allergy"]["shown"] == 1


def test_false_discards_are_counted_where_the_gold_has_legitimate_content(scripts):
    items = [
        item("d02", "x", history=failed("Order aprocitentan.", "order_or_allergy_change")),
        item("d01", "x", plan=failed("Order rest.", "order_or_allergy_change")),
        item("d02", "x", plan=failed("Order a chest X-ray.", "order_or_allergy_change")),
    ]

    order_allergy = score(items, scripts)["order_allergy"]

    assert order_allergy["shown"] == 0
    assert order_allergy["discarded_by_rule"] == 3
    # d02's plan has no gold, so throwing it away lost nothing; d02's history and d01's plan were legitimate.
    assert order_allergy["legitimate_fields_lost"] == 2
    assert order_allergy["lost_where_nothing_was_attempted"] == 1
    # Of the four fields with legitimate gold content across the three runs.
    assert order_allergy["false_discard_rate"] == 0.5


def test_timing_for_two_minute_dictations_and_live_chunks(scripts):
    long = item("d01", "x", seconds=118)
    long.chunk_seconds = [0.2, 0.3, 0.4, 1.5]

    timing = score([item("d01", "x"), long], scripts)["timing"]

    assert timing["all"]["items"] == 2 and timing["two_minute"]["items"] == 1
    assert timing["two_minute"]["structure"] == {"mean": 10.0, "max": 10.0}
    assert timing["live_chunk_seconds"] == {"chunks": 4, "p50": 0.35, "p95": 0.4, "max": 1.5}


def test_sorting_failures_by_reason(scripts):
    failed_run = item("d01", "x")
    failed_run.sorting, failed_run.reason = "unavailable", "model_timeout"

    assert score([item("d01", "x"), failed_run], scripts)["sorting"] == {"done": 1, "unavailable": {"model_timeout": 1}}


def test_no_offered_value_where_gold_exists_scores_f1_0(scripts):
    fields = score([item("d01", "x")], scripts)["fields"]

    assert fields["detection"]["f1"] == 0.0 and fields["content"]["f1"] == 0.0


def test_forbidden_text_counts_only_as_whole_words(scripts):
    items = [item("d02", "x", plan=pending("Chest X-rays were normal in 2019."))]

    assert score(items, scripts)["order_allergy"]["shown"] == 0


def test_dictated_orders_reported_as_not_carried_over(scripts):
    said = "Takes aprocitentan daily. Go ahead and order a chest X-ray."
    reported, missed = item("d02", said), item("d02", said)
    start = said.index("Go ahead")
    reported.not_carried_over = [("order", start, len(said) - 1)]

    order_allergy = score([reported, missed], scripts)["order_allergy"]

    assert (order_allergy["forbidden_said"], order_allergy["reported_not_carried_over"]) == (2, 1)
    assert order_allergy["reported_recall"] == 0.5
