"""The trick sentences (trick-sentences.toml; the expansion plan's step 15.1), as the gate weighs them (tricks.py): the
set is labelled as its header says and is the one the gate records, a case for each sentence held back that reverses
the meaning and each control, and the sentences the rules still propose are exactly those the set records so, each
accepted by the owner by name (the gate fails on any other)."""

from __future__ import annotations

import pytest

from ehr_dictation.transcript import Transcript
from ehr_dictation.validation import not_carried_over, weigh
from ehr_eval import gate, tricks

TRICKS, MOST_WRONG, TOLERATED = tricks.load()
ACCEPTED = tricks.accepted()


def test_the_set_is_labelled_as_its_header_says_and_is_the_one_the_gate_records():
    said = [trick.said for trick in TRICKS]

    assert len(said) == len(set(said)) >= 341 + 183 + 54
    assert all(trick.expect in ("proposed", "held_back", "cost") for trick in TRICKS)
    assert not any(trick.reverses or trick.tolerated or trick.still_proposed for trick in TRICKS if trick.expect != "held_back")
    assert all(trick.tolerated in TOLERATED and not trick.reverses and not trick.still_proposed
               for trick in TRICKS if trick.tolerated)
    assert all(why.strip() for why in TOLERATED.values())
    # The owner accepts by name exactly the tolerated sentences and those still proposed, each expected held back, and
    # the cap is their number: one fixed leaves its group and the list, and the cap comes down with it.
    assert ACCEPTED == {trick.said for trick in TRICKS if trick.tolerated or trick.still_proposed}
    assert ACCEPTED <= {trick.said for trick in TRICKS if trick.expect == "held_back"}
    assert MOST_WRONG == len(ACCEPTED)
    assert gate.trick_sentences_sha256() == gate.TRICK_SENTENCES_SHA256


# Each sentence that reverses the meaning is held back, unless the owner has accepted it by name, each still listed as
# not carried over if it is an instruction at all; and every control is proposed. The sentences accepted by name, those
# the rules still propose, are not here, but below.
@pytest.mark.parametrize("trick", [trick for trick in TRICKS if (trick.reverses and not trick.still_proposed)
                                   or trick.expect == "proposed"], ids=lambda trick: trick.said[:60])
def test_a_trick_sentence_that_reverses_the_meaning_is_held_back_and_each_control_is_proposed(trick):
    transcript = Transcript(trick.said, (), True)

    assert bool(tricks.proposed(trick.said)) == (trick.expect == "proposed")
    assert len(weigh(transcript)) == len(not_carried_over(transcript))


# What is proposed of what should be held back is exactly the tolerated sentences and those recorded as still proposed,
# each accepted by name: a rule that holds one of them back, or a change that lets another through, fails here until
# the set says so.
def test_the_wrong_proposals_are_exactly_the_tolerated_and_those_recorded_as_still_proposed():
    scores = tricks.score()

    assert set(scores.wrong) == {trick.said for trick in TRICKS if trick.tolerated or trick.still_proposed}
    assert scores.unaccepted == [] and scores.missed == []
