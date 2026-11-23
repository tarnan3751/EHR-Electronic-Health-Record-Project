"""sessions.py: chunks in order, resent or refused; owners; limits; stopping, sorting and the result; and everything
discarded on cancel or expiry. Uses a stand-in decoder (the bytes are samples) and stand-in models."""

from __future__ import annotations

import asyncio
import dataclasses

import pytest
from conftest import MODELS, FakeLlama, FakeRecognizer, answer, raw_decoder, speech_and_pauses

import threading
import time

from ehr_dictation.asr import Recognition
from ehr_dictation.audio import SAMPLE_RATE, AudioError, DecodeTimeout
from ehr_dictation.provenance import CODE_SHA256
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


# TC-UC9-01 (FR-07, FR-10, FR-32, NFR-14): chunks sent in order as the clinician speaks build the live transcript,
# which grows with each chunk. Also covered, through the web app, by DictationApiTests and DictationPageTests.
def test_chunks_in_order_build_a_live_transcript(settings):
    async def run():
        sessions, _ = make(settings)
        session = sessions.start(ALICE)
        answers = [await sessions.add_audio(session.id, ALICE, seq, chunk) for seq, chunk in enumerate(chunks())]
        return answers

    answers = asyncio.run(run())

    assert [a["receivedSeq"] for a in answers] == list(range(10))
    assert [a["audioSeconds"] for a in answers] == [float(n) for n in range(1, 11)]
    last = answers[-1]["transcript"]
    assert last["text"].startswith("speech") and not last["final"]
    assert [s["final"] for s in last["segments"]] == [True, True, False]


# Also TC-UC9-04 (FR-07, FR-10): what resuming relies on. A chunk sent again is ignored, and one that skips ahead
# is refused with the one to send next. The whole resumption is the next test.
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


# TC-UC9-04 (FR-07, FR-10): the connection drops and the transcript resumes. Mid-dictation, chunk 4 reaches the
# sidecar but its answer is lost, so the client sends it again; then chunk 5 is lost on the way, so the client's next
# chunk skips ahead and is told to send 5. Sending from there, the live transcript carries on from where it was, and the
# final transcript is the one a dictation with no drop gives.
def test_after_a_dropped_connection_the_transcript_resumes_where_it_left_off(settings):
    async def uninterrupted():
        sessions, _ = make(settings)
        return await sessions.stop(await dictate(sessions), ALICE)

    async def interrupted():
        sessions, _ = make(settings)
        session = sessions.start(ALICE)
        parts = chunks()
        before = [await sessions.add_audio(session.id, ALICE, seq, parts[seq]) for seq in range(5)]
        resent = await sessions.add_audio(session.id, ALICE, 4, parts[4])
        with pytest.raises(SessionError) as skipped:
            await sessions.add_audio(session.id, ALICE, 6, parts[6])
        resumed_from = skipped.value.extra["expectedSeq"]
        after = [await sessions.add_audio(session.id, ALICE, seq, parts[seq]) for seq in range(resumed_from, len(parts))]
        return before, resent, skipped.value, after, await sessions.stop(session.id, ALICE)

    expected = asyncio.run(uninterrupted())
    before, resent, skipped, after, result = asyncio.run(interrupted())

    assert resent == before[-1]
    assert (skipped.status, skipped.code, skipped.extra) == (409, "out_of_order", {"expectedSeq": 5})
    assert [a["receivedSeq"] for a in after] == list(range(5, 10))
    assert after[0]["transcript"]["text"].startswith(before[-1]["transcript"]["text"])
    assert after[-1]["audioSeconds"] == 10.0
    assert result["transcript"] == expected["transcript"] and result["transcript"]["final"]


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
    assert result["fields"]["subjective"]["status"] == "pending"
    assert result["fields"]["plan"] == {"status": "empty", "reasons": ["not_dictated"]}


def test_the_result_waits_for_fields_that_take_longer_than_stop(settings):
    # Stop's wait must outlast the final transcription, an ffmpeg decode that takes longer to start on Windows than on
    # Linux, and still end well before the model answers.
    async def run():
        sessions, _ = make(settings, FakeLlama(GROUNDED, delay=2.0), stop_wait_seconds=0.5)
        session_id = await dictate(sessions)
        stopped = await sessions.stop(session_id, ALICE)
        waited = await sessions.result(session_id, ALICE, wait=5)
        return stopped, waited

    stopped, waited = asyncio.run(run())

    assert stopped["sorting"] == "pending" and stopped["fields"] is None and stopped["transcript"]["final"]
    assert waited["sorting"] == "done" and waited["fields"]["subjective"]["status"] == "pending"


# Also TC-UC10-04 (FR-11, FR-13): with the language model down, slow or answering nonsense, the transcript stays
# as plain text and the result says why the fields weren't sorted. The primary test is DictationApiTests.
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


# TC-UC9-02 (FR-13): cancel discards the dictation: its audio and transcript are gone, and it can't be stopped.
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


# TC-UC10-05 (NFR-15): every result names the models, llama-server build, prompt, schema and code that made it.
def test_every_result_says_which_models_prompt_and_schema_made_it(settings):
    async def run():
        sessions, _ = make(settings)
        return await sessions.stop(await dictate(sessions), ALICE)

    provenance = asyncio.run(run())["provenance"]

    assert provenance["speech"] == MODELS.speech and provenance["language"] == MODELS.language
    assert provenance["llamaServer"] == "b11669-620d2225"
    assert all(provenance[key].startswith("sha256:") for key in ("prompt", "schema", "structuring", "code"))
    assert provenance["code"] == f"sha256:{CODE_SHA256}"


def test_when_medasr_fails_on_the_rest_the_live_transcript_stays_and_says_why(settings, caplog):
    class Failing(FakeRecognizer):
        final = False

        def recognize(self, samples):
            if self.final:
                raise RuntimeError("speech speech")  # what an exception might say: it must not be logged
            return super().recognize(samples)

    async def run():
        llama = FakeLlama(GROUNDED)
        recognizer = Failing()
        sessions = Sessions(settings, recognizer, llama.structurer(), MODELS, decoder=raw_decoder)
        session = sessions.start(ALICE)
        for seq, chunk in enumerate(chunks()):
            await sessions.add_audio(session.id, ALICE, seq, chunk)
        live = session.transcript.text
        recognizer.final = True
        await sessions.stop(session.id, ALICE)
        return live, await sessions.result(session.id, ALICE, wait=5), llama

    with caplog.at_level("INFO", logger="ehr_dictation"):
        live, result, llama = asyncio.run(run())

    assert (result["sorting"], result["reason"], result["fields"]) == ("unavailable", "transcription_failed", None)
    assert result["transcript"]["text"] == live and live and not result["transcript"]["final"]
    assert llama.requests == []
    assert "RuntimeError" in caplog.text and "speech speech" not in caplog.text


# ---- Cancel discards everything, whatever is running (UC 9) ------------------------------------------------------


def test_cancel_during_the_final_transcription_sends_nothing_to_the_model_and_returns_nothing(settings):
    async def run():
        llama = FakeLlama(GROUNDED)
        sessions, _ = make(settings, llama, stop_wait_seconds=0.2)
        session_id = await dictate(sessions)
        recognizer = sessions._recognizer
        recognizer.hold = threading.Event()
        stopped = await sessions.stop(session_id, ALICE)
        session = sessions._sessions[session_id]
        sessions.cancel(session_id, ALICE)
        recognizer.hold.set()
        await asyncio.sleep(0.3)
        with pytest.raises(SessionError) as gone:
            await sessions.result(session_id, ALICE, 0)
        return stopped, session, llama, gone.value

    stopped, session, llama, gone = asyncio.run(run())

    assert stopped["sorting"] == "pending" and not stopped["transcript"]["final"]
    assert gone.status == 404
    assert llama.requests == [] and session.checks is None and session.transcript.text == ""


def test_cancel_while_a_chunk_is_transcribed_stores_nothing(settings):
    async def run():
        sessions, _ = make(settings)
        session = sessions.start(ALICE)
        sessions._recognizer.hold = threading.Event()
        chunk = asyncio.create_task(sessions.add_audio(session.id, ALICE, 0, b"".join(chunks()[:4])))
        await asyncio.sleep(0.1)
        sessions.cancel(session.id, ALICE)
        sessions._recognizer.hold.set()
        with pytest.raises(SessionError) as gone:
            await chunk
        return session, gone.value

    session, gone = asyncio.run(run())

    assert gone.status == 404
    assert session.transcript.text == "" and session.progress.segments == [] and len(session.audio) == 0


def test_cancel_while_a_chunk_is_decoded_stores_nothing(settings):
    release = threading.Event()

    def held(data: bytes, **options: object):
        release.wait(10)
        return raw_decoder(data, **options)

    async def run():
        sessions = Sessions(settings, FakeRecognizer(), FakeLlama(GROUNDED).structurer(), MODELS, decoder=held)
        session = sessions.start(ALICE)
        chunk = asyncio.create_task(sessions.add_audio(session.id, ALICE, 0, b"".join(chunks()[:4])))
        await asyncio.sleep(0.1)
        sessions.cancel(session.id, ALICE)
        release.set()
        with pytest.raises(SessionError) as gone:
            await chunk
        return session, gone.value

    session, gone = asyncio.run(run())

    assert gone.status == 404
    assert len(session.audio) == 0 and session.next_seq == 0 and session.audio_seconds == 0


def test_cancel_stops_a_model_call_in_flight(settings):
    async def run():
        llama = FakeLlama(GROUNDED, delay=5)
        sessions, _ = make(settings, llama, stop_wait_seconds=0.1)
        session_id = await dictate(sessions)
        await sessions.stop(session_id, ALICE)
        while not any(r.url.path == "/v1/chat/completions" for r in llama.requests):
            await asyncio.sleep(0.02)
        task = sessions._sessions[session_id].task
        sessions.cancel(session_id, ALICE)
        await asyncio.sleep(0.1)
        return llama, task

    llama, task = asyncio.run(run())

    assert llama.cancelled and task.done()


# ---- Every call answers within its budget --------------------------------------------------------------------------


# Also TC-UC9-06 (NFR-14, FR-07), the live-transcript speed: a chunk is answered within its budget even while
# MedASR is busy. The accuracy part is the eval gate (eval/tests/test_gate.py, and ci.yml's eval step).
def test_a_chunk_is_answered_in_time_while_medasr_is_busy_and_the_next_one_catches_up(settings):
    async def run():
        sessions, _ = make(settings, call_seconds=0.3)
        session = sessions.start(ALICE)
        parts = chunks()
        await sessions._transcribing.acquire()
        started = time.perf_counter()
        busy = await sessions.add_audio(session.id, ALICE, 0, b"".join(parts[:4]))
        took = time.perf_counter() - started
        sessions._transcribing.release()
        caught_up = await sessions.add_audio(session.id, ALICE, 1, b"".join(parts[4:8]))
        return busy, took, caught_up

    busy, took, caught_up = asyncio.run(run())

    assert busy["receivedSeq"] == 0 and busy["transcript"]["text"] == "" and took < 1
    assert caught_up["transcript"]["text"].startswith("speech")


# Also TC-UC9-06 (NFR-14, FR-07), the live-transcript speed: stop answers within its budget.
def test_stop_answers_within_its_budget_and_the_result_follows(settings):
    async def run():
        sessions, _ = make(settings, stop_wait_seconds=0.2)
        session_id = await dictate(sessions)
        sessions._recognizer.hold = threading.Event()
        started = time.perf_counter()
        stopped = await sessions.stop(session_id, ALICE)
        took = time.perf_counter() - started
        sessions._recognizer.hold.set()
        later = await sessions.result(session_id, ALICE, wait=5)
        return stopped, took, later

    stopped, took, later = asyncio.run(run())

    assert took < 1 and stopped["sorting"] == "pending" and not stopped["transcript"]["final"]
    assert later["sorting"] == "done" and later["transcript"]["final"]


class SlowDecoder:
    """Runs out of time the first `slow` times it is called, then decodes as raw_decoder does."""

    def __init__(self, slow: int) -> None:
        self.slow = slow
        self.calls = 0

    def __call__(self, data: bytes, **options: object):
        self.calls += 1
        if self.calls <= self.slow:
            raise DecodeTimeout("ffmpeg took too long to decode the audio.")
        return raw_decoder(data, **options)


def test_a_chunk_ffmpeg_cant_decode_in_time_is_refused_as_busy_and_can_be_sent_again(settings):
    async def run():
        sessions = Sessions(settings, FakeRecognizer(), FakeLlama(GROUNDED).structurer(), MODELS, decoder=SlowDecoder(1))
        session = sessions.start(ALICE)
        chunk = b"".join(chunks()[:2])
        with pytest.raises(SessionError) as refused:
            await sessions.add_audio(session.id, ALICE, 0, chunk)
        return refused.value, await sessions.add_audio(session.id, ALICE, 0, chunk)

    refused, again = asyncio.run(run())

    assert (refused.status, refused.code) == (429, "busy")
    assert again["receivedSeq"] == 0 and again["audioSeconds"] == 2.0


# TC-UC9-03 (FR-10): audio that isn't audio, or can't be decoded, is refused (422 not_audio). Also covered by
# test_api.py (at stop) and, through the web app, DictationApiTests (415 and 422).
def test_bytes_that_are_not_audio_are_refused_as_not_audio(settings):
    def broken(data: bytes, **_: object):
        raise AudioError("ffmpeg couldn't decode the audio (exit code 1).")

    async def run():
        sessions = Sessions(settings, FakeRecognizer(), FakeLlama(GROUNDED).structurer(), MODELS, decoder=broken)
        session = sessions.start(ALICE)
        with pytest.raises(SessionError) as refused:
            await sessions.add_audio(session.id, ALICE, 0, b"\0" * 64)
        return refused.value

    refused = asyncio.run(run())

    assert (refused.status, refused.code) == (422, "not_audio")


@pytest.mark.parametrize(("slow", "sorting", "reason"), [(1, "done", None), (2, "unavailable", "transcription_failed")])
def test_after_stop_a_decode_out_of_time_is_tried_once_more(settings, slow, sorting, reason):
    async def run():
        decoder = SlowDecoder(0)
        sessions = Sessions(settings, FakeRecognizer(), FakeLlama(GROUNDED).structurer(), MODELS, decoder=decoder)
        session_id = await dictate(sessions)
        live = sessions._sessions[session_id].transcript.text
        decoder.slow, decoder.calls = slow, 0
        await sessions.stop(session_id, ALICE)
        return live, await sessions.result(session_id, ALICE, wait=5), decoder.calls

    live, result, calls = asyncio.run(run())

    assert (result["sorting"], result["reason"], calls) == (sorting, reason, 2)
    assert result["transcript"]["final"] == (sorting == "done")
    if reason:
        assert result["transcript"]["text"] == live


# ---- Limits --------------------------------------------------------------------------------------------------------


def test_a_dictation_still_being_sorted_counts_against_the_limits(settings):
    async def run():
        sessions, _ = make(settings, FakeLlama(GROUNDED, delay=0.5), max_sessions_per_owner=1, stop_wait_seconds=0.05)
        first = await dictate(sessions)
        await sessions.stop(first, ALICE)
        with pytest.raises(SessionError) as busy:
            sessions.start(ALICE)
        await sessions.result(first, ALICE, wait=5)
        sessions.start(ALICE)
        return busy.value

    assert asyncio.run(run()).status == 429


def test_the_models_time_includes_waiting_for_a_turn(settings):
    async def run():
        sessions, _ = make(settings, FakeLlama(GROUNDED, delay=0.4), sorting_at_once=1, structuring_seconds=0.6,
                           stop_wait_seconds=0.05)
        first, second = await dictate(sessions, ALICE), await dictate(sessions, BOB)
        await sessions.stop(first, ALICE)
        await sessions.stop(second, BOB)
        return await sessions.result(first, ALICE, wait=5), await sessions.result(second, BOB, wait=5)

    first, second = asyncio.run(run())

    assert first["sorting"] == "done"
    assert (second["sorting"], second["reason"]) == ("unavailable", "model_timeout")


def test_every_result_lists_the_instructions_not_carried_over(settings):
    async def run():
        sessions, _ = make(settings)
        return await sessions.stop(await dictate(sessions), ALICE)

    assert asyncio.run(run())["notCarriedOver"] == []


class Ordering(FakeRecognizer):
    """Hears an order and an allergy to add in every passage; fails from when it is told to."""

    failing = False

    def recognize(self, samples):
        if self.failing:
            raise RuntimeError("Go ahead and order a hemoglobin A1c")  # what an exception might say: it must not be logged
        return Recognition("Go ahead and order a hemoglobin A1c {period} Add amoxicillin to her allergy list {period}",
                           0.9)


# TC-UC10-09 and TC-UC10-10 (the expansion plan's step 15): a result proposes the orders and allergies to add only from
# the final transcript, as kinds and places, and the log has their count and never their words.
def test_a_result_proposes_only_from_a_final_transcript_and_logs_no_words(settings, caplog):
    async def run(fail_final: bool):
        recognizer = Ordering()
        sessions = Sessions(settings, recognizer, FakeLlama(GROUNDED).structurer(), MODELS, decoder=raw_decoder)
        session = sessions.start(ALICE)
        for seq, chunk in enumerate(chunks()):
            await sessions.add_audio(session.id, ALICE, seq, chunk)
        recognizer.failing = fail_final
        await sessions.stop(session.id, ALICE)
        return await sessions.result(session.id, ALICE, wait=5)

    with caplog.at_level("INFO", logger="ehr_dictation"):
        final, live = asyncio.run(run(False)), asyncio.run(run(True))

    text = final["transcript"]["text"]
    assert final["transcript"]["final"] and {(p["kind"], text[p["start"] : p["end"]]) for p in final["proposals"]} == {
        ("order", "Go ahead and order a hemoglobin A1c"), ("allergy_addition", "Add amoxicillin to her allergy list")}
    assert len(final["proposals"]) == len(final["notCarriedOver"])
    assert not live["transcript"]["final"] and live["notCarriedOver"] and live["proposals"] == []
    assert "proposals" in caplog.text and "order a hemoglobin A1c" not in caplog.text and "amoxicillin" not in caplog.text


# A kind of session that must propose nothing (Session.proposes) still lists every instruction, and proposes none.
def test_a_session_switched_off_proposes_nothing_and_still_lists_every_instruction(settings):
    async def run():
        sessions = Sessions(settings, Ordering(), FakeLlama(GROUNDED).structurer(), MODELS, decoder=raw_decoder)
        session = sessions.start(ALICE)
        session.proposes = False
        for seq, chunk in enumerate(chunks()):
            await sessions.add_audio(session.id, ALICE, seq, chunk)
        await sessions.stop(session.id, ALICE)
        return await sessions.result(session.id, ALICE, wait=5)

    result = asyncio.run(run())

    assert result["transcript"]["final"] and result["notCarriedOver"] and result["proposals"] == []
