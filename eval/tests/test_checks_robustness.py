"""The Spanish checks read a whole transcript without raising, however it ends or whatever is left out of it: every
word-prefix of every test-set script's display text (e01 to e16), and each text with each comma-delimited clause dropped
in turn, through translation._context(), which runs every reader of a whole transcript (the terms, the corrections, the
instructions, what is measured, the qualifiers and their cues). Run B died on a cue followed by a comma near the end of a
recording's transcript (spanish._to_the_finding(), 2026-11-24): a transcript stops wherever the speaker or the
recognizer does. The held-out scripts' texts are never read here."""

from __future__ import annotations

import pytest

from dictation_scripts_es import load_scripts
from ehr_dictation import spanish, translation
from ehr_eval import goldreport

SCRIPTS = load_scripts()


def readings(text: str) -> list[str]:
    """Every word-prefix of the text, and the text with each comma-delimited clause dropped in turn."""
    prefixes = [text[: token.end] for token in spanish.tokens(text)]
    clauses = text.split(",")
    dropped = [",".join(clauses[:k] + clauses[k + 1 :]) for k in range(len(clauses))] if len(clauses) > 1 else []
    return prefixes + dropped


@pytest.mark.parametrize("script", SCRIPTS, ids=lambda s: s.id)
def test_the_checks_read_every_prefix_and_every_clause_dropped_without_raising(script):
    for text in readings(script.display):
        translation._context(goldreport.transcript(text))


def test_a_cue_with_a_comma_after_it_and_none_closing_it_at_the_end_of_a_transcript_is_read():
    # Run B's crash: the clause the comma opens runs to the end of the transcript, with no comma closing it.
    context = translation._context(goldreport.transcript("No creo que, tenga fiebre"))
    assert context.qualified["negation"] and context.qualified["certainty"]
