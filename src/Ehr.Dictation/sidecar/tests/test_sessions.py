"""sessions.py: chunks in order, resent or refused; owners; limits; stopping, sorting and the result; and everything
discarded on cancel or expiry. Uses a stand-in decoder (the bytes are samples) and stand-in models."""

from __future__ import annotations

import asyncio
import dataclasses

import pytest
from conftest import MODELS, FakeLlama, FakeRecognizer, answer, raw_decoder, speech_and_pauses

from ehr_dictation.audio import SAMPLE_RATE
from ehr_dictation.sessions import SessionError, Sessions

ALICE = "11111111-1111-1111-1111-111111111111"
BOB = "22222222-2222-2222-2222-222222222222"
GROUNDED = answer(("Speech speech.", ["speech speech"]))


class Clock:
    def __init__(self) -> None:
        self.now = 1000.0

    def __call__(self) -> float:
        return self.now


def make(settings, llama: FakeLlama | None = None, **limits):
    clock = Clock()
    sessions = Sessions(dataclasses.replace(settings, **limits), FakeRecognizer(), (llama or FakeLlama(GROUNDED)).structurer(),
                        MODELS, clock=clock, decoder=raw_decoder)
    return sessions, clock


def chunks(seconds_each: float = 1.0) -> list[bytes]:
    data = speech_and_pauses(("speech", 2), ("pause", 1), ("speech", 2), ("pause", 1), ("speech", 4)).astype("<f4").tobytes()
    size = int(seconds_each * SAMPLE_RATE) * 4
    return [data[i : i + size] for i in range(0, len(data), size)]


async def dictate(sessions: Sessions, owner: str = ALICE) -> str:
    session = sessions.start(owner)
    for seq, chunk in enumerate(chunks()):
        await sessions.add_audio(session.id, owner, seq, chunk)
    return session.id


def test_chunks_in_order_build_a_live_transcript(settings):
    async def run():
        sessions, _ = make(settings)
        session = sessions.start(ALICE)
        answers = [await sessions.add_audio(session.id, ALICE, seq, chunk) for seq, chunk in enumerate(chunks())]
        return answers

    answers = asyncio.run(run())

    assert [a["receivedSeq"] for a in answers] == list(range(10))
    last = answers[-1]["transcript"]
    assert last["text"].startswith("speech") and not last["final"]
    assert [s["final"] for s in last["segments"]] == [True, True, False]


def test_a_chunk_sent_again_is_ignored_and_one_that_skips_ahead_is_refused(settings):
    async def run():
        sessions, _ = make(settings)
        session = sessions.start(ALICE)
        parts = chunks()
        first = await sessions.add_audio(session.id, ALICE, 0, parts[0])
        again = await sessions.add_audio(session.id, ALICE, 0, parts[0])
        with pytest.raises(SessionError) as skipped:
            await sessions.add_audio(session.id, ALICE, 2, parts[2])
        return first, again, skipped.value, session

    first, again, skipped, session = asyncio.run(run())

    assert again == first and session.next_seq == 1
    assert (skipped.status, skipped.code, skipped.extra) == (409, "out_of_order", {"expectedSeq": 1})


def test_a_dictation_is_invisible_to_anyone_else(settings):
    async def run():
        sessions, _ = make(settings)
        session_id = await dictate(sessions)
        for attempt in (sessions.add_audio(session_id, BOB, 10, b""), sessions.stop(session_id, BOB)):
            with pytest.raises(SessionError) as refused:
                await attempt
            assert refused.value.status == 404
        with pytest.raises(SessionError):
            sessions.cancel(session_id, BOB)
        return await sessions.stop(session_id, ALICE)

    assert asyncio.run(run())["transcript"]["text"]


def test_a_chunk_or_a_dictation_over_its_limit_is_refused(settings):
    async def run():
        sessions, _ = make(settings, max_chunk_bytes=1000)
        session = sessions.start(ALICE)
        with pytest.raises(SessionError) as too_large:
            await sessions.add_audio(session.id, ALICE, 0, chunks()[0])

        sessions, _ = make(settings, max_session_seconds=2)
        session = sessions.start(ALICE)
        parts = chunks()
        await sessions.add_audio(session.id, ALICE, 0, parts[0])
        await sessions.add_audio(session.id, ALICE, 1, parts[1])
        with pytest.raises(SessionError) as too_long:
            await sessions.add_audio(session.id, ALICE, 2, parts[2])
        return too_large.value, too_long.value, session

    too_large, too_long, session = asyncio.run(run())

    assert (too_large.status, too_large.code) == (413, "too_large")
    assert (too_long.status, too_long.code) == (413, "too_long")
    assert session.next_seq == 2


def test_too_many_dictations_at_once_are_refused_but_stopped_ones_dont_count(settings):
    async def run():
        sessions, _ = make(settings, max_sessions=3, max_sessions_per_owner=2)
        first = sessions.start(ALICE)
        sessions.start(ALICE)
        with pytest.raises(SessionError) as per_owner:
            sessions.start(ALICE)
        sessions.start(BOB)
        with pytest.raises(SessionError) as overall:
            sessions.start(BOB)
        await sessions.stop(first.id, ALICE)
        sessions.start(ALICE)
        return per_owner.value, overall.value

    per_owner, overall = asyncio.run(run())

    assert per_owner.status == overall.status == 429


def test_stop_returns_the_final_transcript_and_the_checked_fields(settings):
    async def run():
        sessions, _ = make(settings)
        session_id = await dictate(sessions)
        return await sessions.stop(session_id, ALICE)

    result = asyncio.run(run())

    assert result["transcript"]["final"] and all(s["final"] for s in result["transcript"]["segments"])
    assert result["sorting"] == "done" and result["reason"] is None
    assert result["fields"]["history"]["status"] == "pending"
    assert result["fields"]["plan"] == {"status": "empty", "reasons": ["not_dictated"]}


def test_the_result_waits_for_fields_that_take_longer_than_stop(settings):
    async def run():
        sessions, _ = make(settings, FakeLlama(GROUNDED, delay=0.3), stop_wait_seconds=0.01)
        session_id = await dictate(sessions)
        stopped = await sessions.stop(session_id, ALICE)
        waited = await sessions.result(session_id, ALICE, wait=5)
        return stopped, waited

    stopped, waited = asyncio.run(run())

    assert stopped["sorting"] == "pending" and stopped["fields"] is None and stopped["transcript"]["final"]
    assert waited["sorting"] == "done" and waited["fields"]["history"]["status"] == "pending"


@pytest.mark.parametrize(("llama", "reason"), [
    (FakeLlama(status=503), "model_unavailable"),
    (FakeLlama(GROUNDED, delay=3), "model_timeout"),
    (FakeLlama("not json"), "model_output_invalid"),
])
def test_when_the_model_fails_the_transcript_stays_and_says_why(settings, llama, reason):
    async def run():
        sessions, _ = make(settings, llama, structuring_seconds=0.2)
        sessions._structurer._timeout = 0.2
        session_id = await dictate(sessions)
        return await sessions.stop(session_id, ALICE)

    result = asyncio.run(run())

    assert (result["sorting"], result["reason"], result["fields"]) == ("unavailable", reason, None)
    assert result["transcript"]["text"].startswith("speech")


def test_an_empty_dictation_has_empty_fields_without_asking_the_model(settings):
    async def run():
        llama = FakeLlama(GROUNDED)
        sessions, _ = make(settings, llama)
        session = sessions.start(ALICE)
        await sessions.add_audio(session.id, ALICE, 0, speech_and_pauses(("pause", 2)).astype("<f4").tobytes())
        return await sessions.stop(session.id, ALICE), llama

    result, llama = asyncio.run(run())

    assert result["transcript"]["text"] == "" and llama.requests == []
    assert {f["status"] for f in result["fields"].values()} == {"empty"}


def test_audio_after_stop_and_a_result_before_it_are_refused(settings):
    async def run():
        sessions, _ = make(settings)
        session = sessions.start(ALICE)
        with pytest.raises(SessionError) as early:
            await sessions.result(session.id, ALICE, 0)
        await sessions.add_audio(session.id, ALICE, 0, chunks()[0])
        await sessions.stop(session.id, ALICE)
        with pytest.raises(SessionError) as late:
            await sessions.add_audio(session.id, ALICE, 1, chunks()[1])
        return early.value, late.value

    early, late = asyncio.run(run())

    assert (early.status, early.code, late.status, late.code) == (409, "not_stopped", 409, "stopped")


def test_cancel_discards_audio_and_transcript(settings):
    async def run():
        sessions, _ = make(settings)
        session_id = await dictate(sessions)
        session = sessions._sessions[session_id]
        sessions.cancel(session_id, ALICE)
        with pytest.raises(SessionError) as gone:
            await sessions.stop(session_id, ALICE)
        return session, gone.value, sessions.count

    session, gone, count = asyncio.run(run())

    assert gone.status == 404 and count == 0
    assert len(session.audio) == 0 and session.transcript.text == "" and session.progress.segments == []


def test_idle_dictations_and_old_results_are_discarded(settings):
    async def run():
        sessions, clock = make(settings, idle_seconds=60, result_seconds=600)
        idle = sessions.start(ALICE)
        finished = await dictate(sessions, BOB)
        await sessions.stop(finished, BOB)
        clock.now += 61
        with pytest.raises(SessionError):
            sessions.cancel(idle.id, ALICE)
        still_there = await sessions.result(finished, BOB, 0)
        clock.now += 600
        with pytest.raises(SessionError):
            await sessions.result(finished, BOB, 0)
        return still_there

    assert asyncio.run(run())["sorting"] == "done"


def test_every_result_says_which_models_prompt_and_schema_made_it(settings):
    async def run():
        sessions, _ = make(settings)
        return await sessions.stop(await dictate(sessions), ALICE)

    provenance = asyncio.run(run())["provenance"]

    assert provenance["speech"] == MODELS.speech and provenance["language"] == MODELS.language
    assert provenance["llamaServer"] == "b11669-620d2225"
    assert all(provenance[key].startswith("sha256:") for key in ("prompt", "schema", "structuring"))
    assert provenance["validation"] == 1
