"""generate_conversations.py, without speech synthesis: the recordings planned (12 clean, 6 room, 4 babble, the containers
alternating), each visit's turns laid on one timeline with their gaps and overlaps, the room's reverberation, and what
makes a recording out of date. Kokoro stands in as an empty module; nothing is synthesized, encoded or downloaded."""

from __future__ import annotations

import sys
import types
from collections import Counter

import numpy as np
import pytest

import conversation_scripts as cs

sys.modules.setdefault("kokoro", types.SimpleNamespace(KModel=None, KPipeline=None))
generate = pytest.importorskip("generate_conversations")
ga = generate.ga


def test_every_visit_gets_a_clean_recording_and_the_variants_it_lists():
    recordings = generate.plan(cs.load_conversations())

    assert Counter(r.noise for r in recordings) == {None: 12, "room": 6, "babble": 4}
    assert [r.container for r in recordings[:4]] == ["webm", "mp4", "webm", "mp4"]
    assert len({r.file for r in recordings}) == len(recordings)


def test_turns_follow_their_gaps_and_overlaps_on_one_timeline():
    turns = (cs.Turn("clinician", "a", None, None), cs.Turn("patient", "b", 1.0, None),
             cs.Turn("clinician", "c", None, 0.25), cs.Turn("patient", "d", None, None))
    visit = types.SimpleNamespace(turns=turns)
    second = ga.RATE
    clips = [np.ones(2 * second), np.ones(second), np.ones(second), np.ones(second)]

    places, length = generate.timeline(visit, clips)

    lead = int(ga.LEAD_IN * ga.RATE)
    assert places[0] == (lead, lead + 2 * second)
    assert places[1][0] == places[0][1] + second  # the gap it gives
    assert places[2][0] == places[1][1] - second // 4  # the overlap it gives: both heard at once
    assert places[3][0] == places[2][1] + int(cs.DEFAULT_GAP * ga.RATE)
    assert length == places[3][1] + int(ga.TAIL * ga.RATE)


def test_the_room_adds_a_tail_below_the_dry_speech_and_keeps_its_length():
    rng = np.random.default_rng(1)
    dry = np.zeros(3 * ga.RATE)
    dry[ga.RATE : ga.RATE + 2400] = 0.1

    wet = generate.reverberate(dry, rng, rt60=0.45, wet_db=-12.0)

    assert wet.size == dry.size
    after = wet[ga.RATE + 2400 : ga.RATE + 2400 + ga.RATE // 4]
    assert np.abs(after).max() > 0, "a tail after the sound"
    assert np.abs(wet[: ga.RATE]).max() < 1e-9, "nothing before it"


def test_a_recording_is_made_again_when_what_is_said_changes():
    visit = cs.load_conversations()[0]
    changed = cs.Conversation(**{**visit.__dict__, "turns": (cs.Turn(visit.turns[0].role, "Hello.", None, None),
                                                              *visit.turns[1:])})

    assert generate.text_sha256(visit) != generate.text_sha256(changed)
    assert generate.text_sha256(visit) == generate.text_sha256(cs.load_conversations()[0])
