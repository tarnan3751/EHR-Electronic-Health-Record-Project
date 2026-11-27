"""listening.py and its routes in api.py: a visit's session kept apart from dictations', in Spanish too, audio taken
with nothing transcribed and the quiet measured, the voices told apart at stop and named by the clinician, every voice
and one clinician and one patient, the note drafted a window at a time after every dictation waiting, with who said each
source, the limits, and the speaker model and Whisper each optional. Stand-ins for the speaker model, MedASR, Whisper and
llama-server; tones, never a voice."""

from __future__ import annotations

import asyncio
import dataclasses
import json
import threading

import pytest
from conftest import (
    LISTENING_MODELS, MODELS, WHISPER, FakeLlama, FakeRecognizer, FakeWhisper, PitchEmbedder, PitchRecognizer, answer, encode,
    raw_decoder, spanish_answer, visit,
)
from fastapi.testclient import TestClient

from ehr_dictation import conversation, prompt, provenance
from ehr_dictation.api import create_app
from ehr_dictation.audio import SAMPLE_RATE
from ehr_dictation.listening import Listening
from ehr_dictation.sessions import AMBIENT, GaveWay, SessionError, Spanish, TooBusy, Turns

ALICE = "11111111-1111-1111-1111-111111111111"
BOB = "22222222-2222-2222-2222-222222222222"
DRAFTED = answer(("I have had a cough for three days.", ["Patient: I have had a cough for three days"]),
                 objective=("Your chest is clear.", ["Your chest is clear"]))
NAMES = {"1": "clinician", "2": "patient"}
HEADERS = {"Authorization": "Bearer dictation-key", "X-Dictation-Owner": ALICE}


def make(settings, llama: FakeLlama | None = None, embedder=None, models=LISTENING_MODELS, **limits) -> Listening:
    return Listening(dataclasses.replace(settings, **limits), PitchRecognizer(), (llama or FakeLlama(DRAFTED)).structurer(),
                     models, decoder=raw_decoder, embedder=embedder if embedder is not None else PitchEmbedder())


def chunks(samples, seconds: float = 5.0) -> list[bytes]:
    data = samples.astype("<f4").tobytes()
    size = int(seconds * SAMPLE_RATE) * 4
    return [data[i : i + size] for i in range(0, len(data), size)]


async def listen(sessions: Listening, *turns: str, owner: str = ALICE, speakers: int = 2) -> str:
    session = sessions.start(owner, AMBIENT, speakers)
    for seq, chunk in enumerate(chunks(visit(*turns))):
        await sessions.hear(session.id, owner, seq, chunk)
    return session.id


async def stopped(sessions: Listening, *turns: str, owner: str = ALICE, speakers: int = 2) -> tuple[str, dict]:
    session_id = await listen(sessions, *turns, owner=owner, speakers=speakers)
    await sessions.stop_listening(session_id, owner)
    return session_id, await sessions.visit_result(session_id, owner, wait=5)


async def drafted(sessions: Listening, *turns: str, names: dict = NAMES) -> tuple[str, dict]:
    session_id, _ = await stopped(sessions, *turns)
    await sessions.name_speakers(session_id, ALICE, names)
    return session_id, await sessions.visit_result(session_id, ALICE, wait=5)


# TC-UC23-01's service half (proposed FR-62): audio in with nothing transcribed, the voices told apart at stop, named,
# and the note drafted with who said each source.
def test_a_visit_is_heard_its_voices_named_and_its_note_drafted_from_who_said_what(settings):
    async def run():
        sessions = make(settings)
        session = sessions.start(ALICE, AMBIENT, 2)
        heard = [await sessions.hear(session.id, ALICE, seq, chunk)
                 for seq, chunk in enumerate(chunks(visit("clinician", "patient", "clinician", "patient")))]
        live = list(sessions._recognizer.calls)
        await sessions.stop_listening(session.id, ALICE)
        waiting = await sessions.visit_result(session.id, ALICE, wait=5)
        named = await sessions.name_speakers(session.id, ALICE, NAMES)
        return heard, live, waiting, named, await sessions.visit_result(session.id, ALICE, wait=5), [session.proposes]

    heard, live, waiting, named, done, sessions_proposes = asyncio.run(run())

    assert live == [], "nothing is transcribed while it records"
    assert all(h["transcript"] == {"text": "", "final": False, "segments": []} for h in heard)
    assert heard[-1]["audioSeconds"] == 15.0 and "quietSeconds" in heard[-1]
    assert waiting["sorting"] == "awaiting_speakers" and waiting["fields"] is None
    assert [v["group"] for v in waiting["speakers"]] == [1, 2] and all(v["seconds"] > 0 for v in waiting["speakers"])
    text = waiting["transcript"]["text"]
    assert text.split("\n") == ["Your chest is clear.", "I have had a cough for three days."] * 2
    assert [(s["speakerGroup"], s["speaker"]) for s in waiting["transcript"]["segments"]] == [(1, None), (2, None)] * 2
    assert named["sorting"] == "pending" and "speakers" not in named
    assert done["sorting"] == "done" and done["proposals"] == [] and done["notCarriedOver"] == []
    assert sessions_proposes == [False], "a visit proposes nothing for the chart (decision D18)"
    assert [s["speaker"] for s in done["transcript"]["segments"]] == ["clinician", "patient"] * 2
    subjective, objective = done["fields"]["subjective"], done["fields"]["objective"]
    assert subjective["value"] == "I have had a cough for three days."
    assert [s["speaker"] for s in subjective["sources"]] == ["patient"]
    assert [w["code"] for w in subjective["warnings"]] == ["patients_own_words"]
    assert objective["value"] == "Your chest is clear." and [s["speaker"] for s in objective["sources"]] == ["clinician"]
    assert objective["warnings"] == []
    provenance = done["provenance"]
    assert provenance["speaker"] == LISTENING_MODELS.speaker
    assert provenance["structuring"] == f"sha256:{prompt.load_conversation().structuring_sha256}"
    assert provenance["structuring"] != f"sha256:{prompt.load().structuring_sha256}"


# Also TC-UC10-19: the patient's words are never the clinician's finding, nor the clinician's the patient's report.
def test_a_value_quoted_from_the_wrong_speaker_is_never_offered(settings):
    swapped = answer(("Your chest is clear.", ["Your chest is clear"]),
                     objective=("I have had a cough for three days.", ["I have had a cough for three days"]))

    async def run():
        return (await drafted(make(settings, FakeLlama(swapped)), "clinician", "patient"))[1]

    fields = asyncio.run(run())["fields"]

    # Its words are then in no source it may quote, so they are unsupported too.
    assert fields["subjective"] == {"status": "empty", "reasons": ["wrong_speaker", "unsupported_content"]}
    assert fields["objective"]["status"] == "empty" and fields["objective"]["reasons"][0] == "wrong_speaker"


# TC-UC23-03's service half (amendment A1): every voice named, one clinician and one patient, or nothing is drafted.
def test_names_that_leave_a_voice_out_or_arent_one_clinician_and_one_patient_are_refused(settings):
    async def run():
        sessions = make(settings)
        session_id = await listen(sessions, "clinician", "patient", "other", "clinician", speakers=3)
        with pytest.raises(SessionError) as early:
            await sessions.name_speakers(session_id, ALICE, NAMES)
        await sessions.stop_listening(session_id, ALICE)
        await sessions.visit_result(session_id, ALICE, wait=5)
        refused = []
        for names in ({}, None, ["clinician"], NAMES, {"1": "clinician", "2": "clinician", "3": "patient"},
                      {"1": "clinician", "2": "patient", "3": "doctor"}, {"1": "clinician", "2": "patient", "3": "other", "4": "other"},
                      {"1": "patient", "2": "other", "3": "other"}, {"1": "unsure", "2": "patient", "3": "other"},
                      {"1": "clinician", "2": "unsure", "3": "unsure"}, [["1", "clinician"], ["2", "patient"], ["3", "other"]]):
            with pytest.raises(SessionError) as error:
                await sessions.name_speakers(session_id, ALICE, names)
            refused.append((error.value.status, error.value.code))
        everyone = {"1": "clinician", "2": "patient", "3": "other"}
        first = await sessions.name_speakers(session_id, ALICE, everyone)
        again = await sessions.name_speakers(session_id, ALICE, everyone)
        with pytest.raises(SessionError) as other:
            await sessions.name_speakers(session_id, ALICE, {"1": "patient", "2": "clinician", "3": "other"})
        return early.value, refused, first, again, other.value

    early, refused, first, again, other = asyncio.run(run())

    assert (early.status, early.code) == (409, "not_stopped")
    assert refused == [(422, "speakers_incomplete")] * 11
    assert first["sorting"] == "pending" and again["sorting"] in ("pending", "done")
    assert (other.status, other.code) == (409, "speakers_given")


# Also TC-UC23-03: the names are checked against how many the clinician said would speak, and a voice named twice in the
# body is refused, not taken as the last name given (the web app checks the same against the consent).
def test_a_third_role_on_a_visit_of_two_or_a_voice_named_twice_is_refused(settings):
    llama = FakeLlama(DRAFTED)
    with TestClient(create_app(settings, recognizer=PitchRecognizer(), structurer=llama.structurer(),
                               models=LISTENING_MODELS, embedder=PitchEmbedder())) as http:
        listening = {**HEADERS, "X-Dictation-Mode": "ambient"}
        visit_id = http.post("/v1/sessions", params={"mode": "ambient", "speakers": 2}, headers=listening).json()["sessionId"]
        http.post(f"/v1/sessions/{visit_id}/audio", params={"seq": 0},
                  content=encode(visit("clinician", "patient", "clinician"), "wav", rate=SAMPLE_RATE), headers=listening)
        http.post(f"/v1/sessions/{visit_id}/stop", headers=listening)
        http.post(f"/v1/sessions/{visit_id}/result", params={"wait": 5}, headers=listening)
        refused = [
            http.post(f"/v1/sessions/{visit_id}/speakers", content=body, headers=listening | {"Content-Type": "application/json"})
            for body in (
                '{"speakers": {"1": "clinician", "2": "patient", "3": "other"}}',
                '{"speakers": {"1": "clinician", "2": "other"}}',
                '{"speakers": {"1": "clinician", "1": "patient", "2": "patient"}}',
                '{"speakers": {"01": "clinician", "2": "patient"}}',
                '{"speakers": ["clinician", "patient"]}',
                'not json',
                '{"speakers": {"\u00b2": "clinician", "1": "patient"}}',
                '{"speakers": {"\u0661": "clinician", "2": "patient"}}',
                '{"speakers": [["1", "clinician"], ["2", "patient"]]}',
                '[["speakers", {"1": "clinician", "2": "patient"}]]',
                '{"speakers": {"1": "clinician", "2": "patient"}, "speakers": {"1": "patient", "2": "clinician"}}',
            )
        ]
        named = http.post(f"/v1/sessions/{visit_id}/speakers", json={"speakers": {"1": "clinician", "2": "patient"}},
                          headers=listening)

    # A superscript or another script's digit, an array where an object belongs, or "speakers" given twice: refused,
    # never a server error or the first of two.
    assert [(r.status_code, r.json()["error"]) for r in refused] == [(422, "speakers_incomplete")] * 11
    assert named.status_code == 200


# Series 1's review, F2: a voice the clinician can't place is named "unsure", and its passages are Unsure.
def test_a_voice_the_clinician_cant_place_is_named_unsure_and_its_passages_are(settings):
    async def run():
        sessions = make(settings)
        session_id, _ = await stopped(sessions, "clinician", "patient", "other", "clinician", "patient", "other",
                                      speakers=3)
        await sessions.name_speakers(session_id, ALICE, {"1": "clinician", "2": "patient", "3": "unsure"})
        return await sessions.visit_result(session_id, ALICE, wait=5)

    done = asyncio.run(run())

    assert done["sorting"] == "done"
    assert [s["speaker"] for s in done["transcript"]["segments"]] == ["clinician", "patient", "unsure"] * 2


# Also TC-UC23-04: every segment of a visit carries who said it, whatever its sorting comes to, so a result is never
# staged with a passage of nobody's; one that couldn't be transcribed has no segments at all.
def test_every_segment_of_a_visit_says_who_said_it_whatever_the_sorting(settings):
    class Failing(PitchRecognizer):
        def recognize(self, samples):
            raise RuntimeError("MedASR failed")

    async def run():
        done = (await drafted(make(settings), "clinician", "patient"))[1]
        timed_out = (await drafted(make(settings, FakeLlama(DRAFTED, delay=3), structuring_seconds=0.5), "clinician",
                                   "patient"))[1]
        sessions = Listening(settings, Failing(), FakeLlama(DRAFTED).structurer(), LISTENING_MODELS, decoder=raw_decoder,
                             embedder=PitchEmbedder())
        session_id, failed = await stopped(sessions, "clinician", "patient")
        return done, timed_out, failed

    done, timed_out, failed = asyncio.run(run())

    assert done["sorting"] == "done" and (timed_out["sorting"], timed_out["reason"]) == ("unavailable", "model_timeout")
    for result in (done, timed_out):
        assert result["transcript"]["segments"] and all(s["speaker"] for s in result["transcript"]["segments"])
    assert (failed["sorting"], failed["reason"], failed["transcript"]["segments"]) == ("unavailable", "transcription_failed", [])


def test_a_visit_and_a_dictation_never_reach_each_others_sessions(settings):
    llama = FakeLlama(DRAFTED)
    with TestClient(create_app(settings, recognizer=PitchRecognizer(), structurer=llama.structurer(),
                               models=LISTENING_MODELS, embedder=PitchEmbedder())) as http:
        listening = {**HEADERS, "X-Dictation-Mode": "ambient"}
        visit_id = http.post("/v1/sessions", params={"mode": "ambient", "speakers": 2}, headers=listening).json()["sessionId"]
        dictation_id = http.post("/v1/sessions", headers=HEADERS).json()["sessionId"]
        crossed = [
            http.post(f"/v1/sessions/{visit_id}/audio", params={"seq": 0}, content=b"\0" * 64, headers=HEADERS),
            http.post(f"/v1/sessions/{visit_id}/stop", headers=HEADERS),
            http.post(f"/v1/sessions/{visit_id}/result", headers=HEADERS),
            http.delete(f"/v1/sessions/{visit_id}", headers=HEADERS),
            http.post(f"/v1/sessions/{dictation_id}/audio", params={"seq": 0}, content=b"\0" * 64, headers=listening),
            http.post(f"/v1/sessions/{dictation_id}/stop", headers=listening),
            http.post(f"/v1/sessions/{dictation_id}/speakers", json={"speakers": NAMES}, headers=listening),
            http.delete(f"/v1/sessions/{dictation_id}", headers=listening),
            http.post(f"/v1/sessions/{visit_id}/speakers", json={"speakers": NAMES}, headers=HEADERS),
        ]
        mixed = http.post("/v1/sessions", params={"mode": "ambient", "speakers": 2}, headers=HEADERS)
        spanish = http.post("/v1/sessions", params={"mode": "ambient", "speakers": 2}, json={"spokenLanguage": "es"},
                            headers=listening)
        four = http.post("/v1/sessions", params={"mode": "ambient", "speakers": 4}, headers=listening)
        still = http.delete(f"/v1/sessions/{visit_id}", headers=listening)

    assert [r.status_code for r in crossed] == [404] * 9
    assert (mixed.status_code, mixed.json()["error"]) == (400, "bad_request")
    assert (spanish.status_code, spanish.json()["error"]) == (422, "unsupported_language")
    assert (four.status_code, four.json()["error"]) == (400, "bad_request")
    assert still.status_code == 204


def test_without_the_speaker_model_a_visit_is_refused_and_dictation_works(settings):
    with TestClient(create_app(settings, recognizer=FakeRecognizer(), structurer=FakeLlama(DRAFTED).structurer(),
                               models=MODELS)) as http:
        refused = http.post("/v1/sessions", params={"mode": "ambient", "speakers": 2},
                            headers={**HEADERS, "X-Dictation-Mode": "ambient"})
        dictation = http.post("/v1/sessions", headers=HEADERS)

    assert (refused.status_code, refused.json()["error"]) == (503, "listening_unavailable")
    assert dictation.status_code == 201


def test_the_quiet_is_measured_from_the_last_time_anyone_spoke(settings):
    async def run():
        sessions = make(settings)
        session = sessions.start(ALICE, AMBIENT, 2)
        samples = visit("clinician", seconds=2.0, pause=6.0)
        return await sessions.hear(session.id, ALICE, 0, samples.astype("<f4").tobytes())

    heard = asyncio.run(run())

    assert 5.5 <= heard["quietSeconds"] <= 6.5


def test_a_visit_over_its_limits_or_too_many_at_once_is_refused(settings):
    async def run():
        sessions = make(settings, ambient_max_session_seconds=5, ambient_max_sessions=2)
        session = sessions.start(ALICE, AMBIENT, 2)
        parts = chunks(visit("clinician", "patient"), seconds=3.0)
        await sessions.hear(session.id, ALICE, 0, parts[0])
        with pytest.raises(SessionError) as too_long:
            await sessions.hear(session.id, ALICE, 1, parts[1])
        sessions.start(BOB, AMBIENT, 3)
        with pytest.raises(SessionError) as busy:
            sessions.start(BOB, AMBIENT, 2)
        small = make(settings, ambient_max_session_bytes=1000)
        with pytest.raises(SessionError) as too_large:
            await small.hear(small.start(ALICE, AMBIENT, 2).id, ALICE, 0, parts[0])
        return too_long.value, busy.value, too_large.value

    too_long, busy, too_large = asyncio.run(run())

    assert (too_long.status, too_long.code) == (413, "too_long")
    assert (busy.status, busy.code) == (429, "busy")
    assert (too_large.status, too_large.code) == (413, "too_large")


def test_a_visit_where_nobody_spoke_is_done_at_stop_without_the_model(settings):
    async def run():
        llama = FakeLlama(DRAFTED)
        sessions = make(settings, llama)
        session = sessions.start(ALICE, AMBIENT, 2)
        await sessions.hear(session.id, ALICE, 0, (visit("clinician") * 0).astype("<f4").tobytes())
        await sessions.stop_listening(session.id, ALICE)
        return await sessions.visit_result(session.id, ALICE, wait=5), llama

    result, llama = asyncio.run(run())

    assert result["sorting"] == "done" and result["transcript"]["text"] == "" and "speakers" not in result
    assert {f["status"] for f in result["fields"].values()} == {"empty"} and llama.requests == []


def test_a_long_visit_is_drafted_a_window_at_a_time(settings, monkeypatch):
    monkeypatch.setattr(conversation, "WINDOW_TOKENS", 12)  # a turn of each: 4 tokens and 8 (FakeLlama counts words)

    async def run():
        llama = FakeLlama(DRAFTED)
        _, result = await drafted(make(settings, llama), *["clinician", "patient"] * 4)
        return result, [json.loads(r.content) for r in llama.requests if r.url.path == "/v1/chat/completions"]

    result, requests = asyncio.run(run())

    assert len(requests) == 4
    for request in requests:
        lines = request["messages"][1]["content"].split("\n")[1:-1]
        assert lines == ["Clinician: Your chest is clear.", "Patient: I have had a cough for three days."]
    assert result["fields"]["subjective"]["value"] == " ".join(["I have had a cough for three days."] * 4)
    assert len(result["fields"]["subjective"]["sources"]) == 4


def test_a_window_the_model_couldnt_draft_leaves_the_whole_visit_undrafted(settings):
    async def run():
        return (await drafted(make(settings, FakeLlama(status=503)), "clinician", "patient"))[1]

    result = asyncio.run(run())

    assert (result["sorting"], result["reason"], result["fields"]) == ("unavailable", "model_unavailable", None)
    assert [s["speaker"] for s in result["transcript"]["segments"]] == ["clinician", "patient"]


def test_a_visits_window_waits_while_a_dictation_is_being_sorted(settings):
    async def run():
        llama = FakeLlama(DRAFTED)
        sessions = make(settings, llama)
        session_id, _ = await stopped(sessions, "clinician", "patient")
        async with sessions._turns.dictation():
            await sessions.name_speakers(session_id, ALICE, NAMES)
            await asyncio.sleep(0.3)
            waiting = [r for r in llama.requests if r.url.path == "/v1/chat/completions"]
        return waiting, await sessions.visit_result(session_id, ALICE, wait=5)

    waiting, result = asyncio.run(run())

    assert waiting == [] and result["sorting"] == "done"


class FirstWindowSlow(FakeLlama):
    """Waits ten seconds before answering the first completion it is asked for, long enough for a dictation to make it
    give way, and answers the rest at once."""

    async def handle(self, request):
        self.delay = 0 if any(r.url.path == "/v1/chat/completions" for r in self.requests) else 10
        return await super().handle(request)


# Decision D16 as approved: a window is drafted only while no dictation is being sorted, so one being drafted gives way
# the moment a dictation asks, and is drafted again after it.
def test_a_window_being_drafted_gives_way_at_the_first_dictation(settings):
    async def run():
        llama = FirstWindowSlow(DRAFTED)
        sessions = make(settings, llama)
        session_id, _ = await stopped(sessions, "clinician", "patient")
        await sessions.name_speakers(session_id, ALICE, NAMES)
        while not any(r.url.path == "/v1/chat/completions" for r in llama.requests):
            await asyncio.sleep(0.02)
        async with asyncio.timeout(1), sessions._turns.dictation(), sessions._sorting:
            await asyncio.sleep(0)
            gave_way = llama.cancelled
        return gave_way, llama, await sessions.visit_result(session_id, ALICE, wait=5)

    gave_way, llama, result = asyncio.run(run())

    assert gave_way, "one dictation is enough"
    assert len([r for r in llama.requests if r.url.path == "/v1/chat/completions"]) == 2
    assert result["sorting"] == "done"


def test_a_window_gives_way_to_a_dictation_and_waits_no_longer_than_it_is_given():
    async def run():
        turns = Turns()

        async def window():
            async with turns.window() as turn:
                return await turn.draft(asyncio.sleep(10))

        async def timed_out():
            async with asyncio.timeout(0.1), turns.window() as turn:
                return await turn.draft(asyncio.sleep(10))

        drafting = asyncio.create_task(window())
        await asyncio.sleep(0.05)
        async with turns.dictation():
            await asyncio.sleep(0)
        with pytest.raises(GaveWay):
            await drafting
        with pytest.raises(TimeoutError):
            await timed_out()
        async with turns.dictation():
            with pytest.raises(TooBusy):
                async with turns.window(0.05):
                    pass

    asyncio.run(run())


# The second review, D16: a visit whose windows never get a turn (a new dictation every few seconds) ends undrafted
# after ambient_wait_seconds of waiting, with its transcript, as model_busy, and goes when results go.
def test_a_visit_that_never_gets_a_turn_ends_undrafted_as_busy(settings):
    async def run():
        sessions = make(settings, ambient_wait_seconds=0.3)
        session_id, _ = await stopped(sessions, "clinician", "patient")
        async with sessions._turns.dictation():
            await sessions.name_speakers(session_id, ALICE, NAMES)
            return await sessions.visit_result(session_id, ALICE, wait=2)

    result = asyncio.run(run())

    assert (result["sorting"], result["reason"]) == ("unavailable", "model_busy")
    assert result["transcript"]["segments"] and all(s["speaker"] for s in result["transcript"]["segments"])


# The second measurement's windows: one that times out, or whose answer runs out of tokens, is drafted again as two
# halves, once, before the visit is given up.
def test_a_window_that_times_out_is_drafted_again_as_two_halves(settings, monkeypatch):
    monkeypatch.setattr(conversation, "WINDOW_TOKENS", 1000)

    class FirstTooLong(FakeLlama):
        async def handle(self, request):
            self.finish_reason = "length" if not any(r.url.path == "/v1/chat/completions" for r in self.requests) else "stop"
            return await super().handle(request)

    async def run():
        llama = FirstTooLong(DRAFTED)
        _, result = await drafted(make(settings, llama), "clinician", "patient", "clinician", "patient")
        return result, [json.loads(r.content) for r in llama.requests if r.url.path == "/v1/chat/completions"]

    result, requests = asyncio.run(run())

    assert len(requests) == 3, "the whole, then each half"
    assert result["sorting"] == "done"
    assert all(len(r["messages"][1]["content"].split("\n")) == 4 for r in requests[1:]), "two turns each"
    assert requests[0]["max_tokens"] == 1100


# ALSO from series 1's review: weights that are the manifest's but won't load turn listening off and nothing else;
# weights that aren't the manifest's still stop the service.
def test_speaker_weights_that_wont_load_turn_listening_off_and_dictation_goes_on(settings, caplog):
    import hashlib

    folder = settings.models_dir / provenance.SPEAKER_DIR
    folder.mkdir()
    (folder / provenance.SPEAKER_WEIGHTS).write_bytes(b"not weights")
    recorded = dataclasses.replace(LISTENING_MODELS, speaker={**LISTENING_MODELS.speaker,
                                                              "sha256": hashlib.sha256(b"not weights").hexdigest()})
    with TestClient(create_app(settings, recognizer=FakeRecognizer(), structurer=FakeLlama(DRAFTED).structurer(),
                               models=recorded)) as http:
        refused = http.post("/v1/sessions", params={"mode": "ambient", "speakers": 2},
                            headers={**HEADERS, "X-Dictation-Mode": "ambient"})
        dictation = http.post("/v1/sessions", headers=HEADERS)
    with pytest.raises(Exception, match="--verify"), TestClient(create_app(
        settings, recognizer=FakeRecognizer(), structurer=FakeLlama(DRAFTED).structurer(), models=LISTENING_MODELS
    )):
        pass

    assert (refused.status_code, refused.json()["error"]) == (503, "listening_unavailable")
    assert dictation.status_code == 201
    assert any("listening to a visit is off" in r.getMessage() for r in caplog.records)


def test_cancel_while_the_visit_is_transcribed_or_drafted_keeps_nothing(settings):
    async def run():
        llama = FakeLlama(DRAFTED, delay=5)
        sessions = make(settings, llama, stop_wait_seconds=0.1)
        session_id = await listen(sessions, "clinician", "patient")
        sessions._recognizer.hold = threading.Event()
        await sessions.stop_listening(session_id, ALICE)
        session = sessions._sessions[session_id]
        sessions.cancel(session_id, ALICE, AMBIENT)
        sessions._recognizer.hold.set()
        await asyncio.sleep(0.2)
        with pytest.raises(SessionError) as gone:
            await sessions.visit_result(session_id, ALICE, 0)

        drafting, _ = await stopped(sessions, "clinician", "patient")
        await sessions.name_speakers(drafting, ALICE, NAMES)
        while not any(r.url.path == "/v1/chat/completions" for r in llama.requests):
            await asyncio.sleep(0.02)
        task = sessions._sessions[drafting].task
        sessions.cancel(drafting, ALICE, AMBIENT)
        await asyncio.sleep(0.1)
        return session, gone.value, llama, task

    session, gone, llama, task = asyncio.run(run())

    assert gone.status == 404 and len(session.audio) == 0 and session.transcript.text == "" and session.checks is None
    assert llama.cancelled and task.done()


def test_a_visit_whose_voices_are_never_named_goes_after_the_idle_time(settings):
    class Clock:
        now = 1000.0

        def __call__(self) -> float:
            return self.now

    async def run():
        clock = Clock()
        sessions = Listening(dataclasses.replace(settings, idle_seconds=60), PitchRecognizer(),
                             FakeLlama(DRAFTED).structurer(), LISTENING_MODELS, decoder=raw_decoder, clock=clock,
                             embedder=PitchEmbedder())
        session_id, waiting = await stopped(sessions, "clinician", "patient")
        clock.now += 61
        with pytest.raises(SessionError) as gone:
            await sessions.visit_result(session_id, ALICE, 0)
        return waiting, gone.value

    waiting, gone = asyncio.run(run())

    assert waiting["sorting"] == "awaiting_speakers" and gone.status == 404


# TC-UC23-05's service half (NFR-07): a whole visit, through the API as the on-prem app calls it, with a recording ffmpeg
# decodes, writes nothing to disk.
def test_a_whole_visit_writes_nothing_to_disk(settings, tmp_path, monkeypatch):
    monkeypatch.setenv("TMPDIR", str(tmp_path))
    monkeypatch.chdir(tmp_path)
    data = encode(visit("clinician", "patient"), "webm")
    listening = {**HEADERS, "X-Dictation-Mode": "ambient"}
    app = create_app(settings, recognizer=PitchRecognizer(),
                     structurer=FakeLlama(DRAFTED).structurer(), models=LISTENING_MODELS, embedder=PitchEmbedder())
    with TestClient(app) as http:
        visit_id = http.post("/v1/sessions", params={"mode": "ambient", "speakers": 2}, headers=listening).json()["sessionId"]
        size = -(-len(data) // 3)
        for seq, offset in enumerate(range(0, len(data), size)):
            assert http.post(f"/v1/sessions/{visit_id}/audio", params={"seq": seq}, content=data[offset : offset + size],
                             headers=listening).status_code == 200
        http.post(f"/v1/sessions/{visit_id}/stop", headers=listening)
        waiting = http.post(f"/v1/sessions/{visit_id}/result", params={"wait": 5}, headers=listening).json()
        http.post(f"/v1/sessions/{visit_id}/speakers", json={"speakers": NAMES}, headers=listening)
        done = http.post(f"/v1/sessions/{visit_id}/result", params={"wait": 5}, headers=listening).json()

    assert waiting["sorting"] == "awaiting_speakers" and done["sorting"] == "done"
    assert list(tmp_path.iterdir()) == []


# ---- Beside dictation in Spanish (the expansion plan's step 17) ------------------------------------------------------
# A session's kind and its language are fixed at its start: a visit is in English, a dictation in Spanish is a
# dictation, and neither reaches the other's sessions or goes through the other's checks. The speaker model and Whisper
# are each optional, and the session that needs one is refused in words without it.

DICTADO = "Tos seca desde hace 3 días."
TRADUCIDO = spanish_answer(("subjective", ["Tos seca desde hace 3 días"], "Dry cough for 3 days."))
BOTH_MODELS = provenance.Models(MODELS.speech, MODELS.language, WHISPER, LISTENING_MODELS.speaker)
OWNERS = ("33333333-3333-3333-3333-333333333333", "44444444-4444-4444-4444-444444444444")


def in_spanish(llama: FakeLlama | None = None) -> Spanish:
    return Spanish(FakeWhisper(DICTADO), (llama or FakeLlama(TRADUCIDO)).structurer(language="es"))


def test_a_visit_and_a_dictation_in_spanish_never_reach_each_others_sessions(settings):
    with TestClient(create_app(settings, recognizer=PitchRecognizer(), structurer=FakeLlama(DRAFTED).structurer(),
                               models=BOTH_MODELS, embedder=PitchEmbedder(), spanish=in_spanish())) as http:
        listening = {**HEADERS, "X-Dictation-Mode": "ambient"}
        visit_id = http.post("/v1/sessions", params={"mode": "ambient", "speakers": 2}, headers=listening).json()["sessionId"]
        spanish_id = http.post("/v1/sessions", json={"spokenLanguage": "es"}, headers=HEADERS).json()["sessionId"]
        crossed = [
            http.post(f"/v1/sessions/{spanish_id}/audio", params={"seq": 0}, content=b"\0" * 64, headers=listening),
            http.post(f"/v1/sessions/{spanish_id}/stop", headers=listening),
            http.post(f"/v1/sessions/{spanish_id}/result", headers=listening),
            http.post(f"/v1/sessions/{spanish_id}/speakers", json={"speakers": NAMES}, headers=listening),
            http.delete(f"/v1/sessions/{spanish_id}", headers=listening),
            http.post(f"/v1/sessions/{visit_id}/audio", params={"seq": 0}, content=b"\0" * 64, headers=HEADERS),
            http.post(f"/v1/sessions/{visit_id}/stop", headers=HEADERS),
            http.delete(f"/v1/sessions/{visit_id}", headers=HEADERS),
        ]
        in_spanish_too = http.post("/v1/sessions", params={"mode": "ambient", "speakers": 2},
                                   json={"spokenLanguage": "es"}, headers={**listening, "X-Dictation-Owner": OWNERS[0]})
        kept = {(s.mode, s.language, s.proposes) for s in http.app.state.sessions._sessions.values()}

    assert [r.status_code for r in crossed] == [404] * 8
    assert (in_spanish_too.status_code, in_spanish_too.json()["error"]) == (422, "unsupported_language")
    assert kept == {(AMBIENT, "en", False), ("dictation", "es", False)}


# A dictation in Spanish asks K2 Horizon as an English one does: a visit's window being drafted gives way to it. Each
# result is its own kind's: the visit's has speakers and no translation, the dictation's translated statements and no
# speaker.
def test_a_dictation_in_spanish_has_its_turn_before_a_visit_and_neither_gets_the_others_checks(settings):
    async def run():
        llama = FirstWindowSlow(DRAFTED)
        sessions = Listening(settings, PitchRecognizer(), llama.structurer(), BOTH_MODELS, decoder=raw_decoder,
                             embedder=PitchEmbedder(), spanish=in_spanish())
        visit_id, _ = await stopped(sessions, "clinician", "patient")
        await sessions.name_speakers(visit_id, ALICE, NAMES)
        while not any(r.url.path == "/v1/chat/completions" for r in llama.requests):
            await asyncio.sleep(0.02)
        dictation = sessions.start(BOB, language="es")
        for seq, chunk in enumerate(chunks(visit("clinician"))):
            await sessions.add_audio(dictation.id, BOB, seq, chunk)
        await sessions.stop(dictation.id, BOB)
        spanish = await sessions.result(dictation.id, BOB, wait=5)
        return llama.cancelled, spanish, await sessions.visit_result(visit_id, ALICE, wait=5)

    gave_way, spanish, heard = asyncio.run(run())

    assert gave_way, "a dictation in Spanish is a dictation"
    assert (spanish["sorting"], heard["sorting"]) == ("done", "done")
    assert spanish["spokenLanguage"] == "es" and spanish["proposals"] == []
    assert spanish["fields"]["subjective"]["value"] == "Dry cough for 3 days." and spanish["fields"]["subjective"]["statements"]
    assert "speaker" not in json.dumps(spanish) and spanish["provenance"]["speech"] == WHISPER
    assert "spokenLanguage" not in heard and heard["proposals"] == []
    assert not any("statements" in field for field in heard["fields"].values())
    assert all(s["speaker"] for s in heard["transcript"]["segments"]) and heard["provenance"]["speech"] == MODELS.speech


@pytest.mark.parametrize(("speaker", "whisper"), [(False, False), (True, False), (False, True), (True, True)])
def test_the_speaker_model_and_whisper_are_each_optional_and_what_needs_one_is_refused_in_words(settings, speaker, whisper):
    models = provenance.Models(MODELS.speech, MODELS.language, WHISPER if whisper else None,
                               LISTENING_MODELS.speaker if speaker else None)
    with TestClient(create_app(settings, recognizer=PitchRecognizer(), structurer=FakeLlama(DRAFTED).structurer(),
                               models=models, embedder=PitchEmbedder() if speaker else None,
                               spanish=in_spanish() if whisper else None)) as http:
        english = http.post("/v1/sessions", headers=HEADERS)
        spanish = http.post("/v1/sessions", json={"spokenLanguage": "es"}, headers={**HEADERS, "X-Dictation-Owner": OWNERS[0]})
        listening = http.post("/v1/sessions", params={"mode": "ambient", "speakers": 2},
                              headers={**HEADERS, "X-Dictation-Owner": OWNERS[1], "X-Dictation-Mode": "ambient"})

    assert english.status_code == 201
    assert (spanish.status_code, spanish.json().get("detail")) == (
        (201, None) if whisper else (503, "Spanish dictation isn't installed on this server."))
    assert (listening.status_code, listening.json().get("detail")) == (
        (201, None) if speaker else (503, "The speaker model isn't installed (setup_models.py) or couldn't be loaded."))
