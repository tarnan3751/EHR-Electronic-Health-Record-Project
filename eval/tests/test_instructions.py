"""FR-14's rule, the one that keeps orders and allergy-list changes out of the note (validation.directives()), over
every text the eval holds: each script as spoken and as displayed, and each stored transcript, as heard and with its
punctuation stripped, as speech recognition that writes none would give it."""

from __future__ import annotations

import json
import re

import pytest

from ehr_dictation.textnorm import STOPWORDS, tokens
from ehr_dictation.transcript import Segment, assemble
from ehr_dictation.validation import LEAD_IN, MARKED_REACH, _directives, _without_instructions, check, directives
from ehr_eval.scripts import EVAL_DIR, load_all

STORED = json.loads((EVAL_DIR / "results" / "latest.json").read_text(encoding="utf-8"))["items"]


def stripped(text: str) -> str:
    """The text without sentence punctuation or line breaks (a decimal point stays)."""
    return re.sub(r"[ \t]+", " ", re.sub(r"[.,;:!?](?!\d)", " ", text).replace("\n", " ")).strip()


def texts() -> list[str]:
    scripts = load_all().values()
    heard = [item["hypothesis"] for item in STORED]
    return sorted({*(s.display for s in scripts), *(s.spoken for s in scripts), *heard, *map(stripped, heard)})


def said(text: str) -> list[str]:
    """The words that say something: not stopwords or lead-ins."""
    return [token.text for token in tokens(text) if token.text not in STOPWORDS and token.text not in LEAD_IN]


# What is cut from a value is what is listed, never more: every word that says something outside the instructions is
# kept, in order, and none inside them.
@pytest.mark.parametrize("text", texts(), ids=lambda text: text[:40])
def test_what_is_cut_from_a_text_is_exactly_what_is_listed(text):
    kept, _ = _without_instructions(text)
    spans = directives(text)
    outside = "".join(
        text[start:end] + " " for start, end in zip([0, *(e for _, e, _ in spans)], [*(s for s, _, _ in spans), len(text)])
    )

    assert said(kept) == said(outside)


# Without punctuation, an instruction reaches no further than MARKED_REACH words from its verb (it took a paragraph
# before: d22's took 111 words), and no value that passes with the transcript's punctuation is refused as an order once
# it is stripped.
@pytest.mark.parametrize("item", STORED, ids=lambda item: item["variant"])
def test_without_punctuation_an_instruction_takes_no_paragraph_and_refuses_no_passing_value(item):
    text = stripped(item["hypothesis"])

    assert all(len(tokens(text[at:end])) <= MARKED_REACH for _, end, _, at in _directives(text))
    if item["sorting"] != "done":
        return
    heard = assemble([Segment(0, 1, item["hypothesis"], 0.9, None, True)], final=True)
    bare = assemble([Segment(0, 1, text, 0.9, None, True)], final=True)
    fields = {name: {"value": field["proposed"] or "", "sources": field["sources"]} for name, field in item["fields"].items()}
    bare_fields = {
        name: {"value": stripped(field["value"]), "sources": [stripped(source) for source in field["sources"]]}
        for name, field in fields.items()
    }
    before, after = check(fields, item["unknown"], heard), check(bare_fields, item["unknown"], bare)
    for name in fields:
        if before.fields[name].pending:
            assert "order_or_allergy_change" not in after.fields[name].reasons, name
