"""The Spanish scorer's readers of a transcript raise nothing on what Whisper may write: a token of digits glued to
letters ("la 1C", A1C heard: run B's scoring died on it, 2026-11-24), an empty transcript, and every word-prefix of every
test-set script's display text (e01 to e16), through the word error rate's words and the dates; the word error rate,
drug names, instructions and passages mixing English on every tenth prefix, whose fuzzy drug matching is slow; and the
whole audio scoring (scoring_es.score_audio()) on the glued tokens and the empty transcript. The held-out scripts' texts are never
read here."""

from __future__ import annotations

import pytest

from dictation_scripts import load_scripts as load_english
from dictation_scripts_es import load_scripts
from ehr_dictation import spanish
from ehr_eval import scoring_es

SCRIPTS = {script.id: script for script in load_scripts()}
ENGLISH = {script.id: script for script in load_english()}
GLUED = ["la 1C es de 7,1", "A1C 7,1", "control de la 1c2", "3x al día", "5mg cada 8h", "1C", ""]


def heard(script, text: str) -> scoring_es.SpanishItem:  # noqa: ANN001
    return scoring_es.SpanishItem(variant=f"{script.id}--clean", script=script.id, tags=list(script.tags),
                                  hypothesis=text, sorting="unavailable", reason="model_timeout")


def test_a_token_of_digits_glued_to_letters_is_a_word_not_a_number():
    assert scoring_es.spanish_words("la 1C es de 7,1") == ["la", "1c", "es", "de", "7.1"]
    assert scoring_es.spanish_words("A1C 7,1", accents=False) == ["a1c", "7.1"]


@pytest.mark.parametrize("script", SCRIPTS.values(), ids=lambda s: s.id)
def test_the_scorers_transcript_readers_read_every_prefix_glued_tokens_and_nothing_without_raising(script):
    # The readers of words, numbers and dates on every prefix; the word error rate and the drug names, whose fuzzy
    # matching is slow on many texts, on every tenth prefix and the whole text.
    prefixes = [script.display[: token.end] for token in spanish.tokens(script.display)]
    for text in prefixes + GLUED:
        scoring_es.spanish_words(text)
        scoring_es.spanish_words(text, accents=False)
        for iso in script.dates:
            scoring_es.date_said(iso, text)
    items = [heard(script, text) for text in prefixes[::10] + prefixes[-1:] + GLUED]
    scoring_es._wer(items, SCRIPTS)
    scoring_es._drugs(items, SCRIPTS)
    scoring_es._instructions(items, SCRIPTS)
    scoring_es._code_switching(items, SCRIPTS)


def test_the_whole_audio_scoring_reads_glued_tokens_and_an_empty_transcript():
    items = [heard(script, text) for script in SCRIPTS.values() for text in GLUED]
    scoring_es.score_audio(items, SCRIPTS, ENGLISH)
