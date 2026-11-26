"""Listening to a visit (the expansion plan's step 16; UC 23): sessions of that kind, held with dictations' (Sessions,
sessions.py, which this extends), and everything they do that a dictation doesn't.

A visit's session is started with how many people the clinician said would speak (2 or 3), and every call names its
kind (api.py), so a dictation's calls never reach it, nor its calls a dictation. It needs the speaker model (ecapa.py,
from setup_models.py): without one, or with one this port can't load, starting is refused (503 listening_unavailable)
and dictation works as before.

1. Audio comes in numbered chunks, as a dictation's does, up to ambient_max_session_seconds and ambient_max_session_bytes
   (settings.py). Nothing is transcribed while it records: each chunk is decoded with all before it, in memory, to check
   it and to measure how long nobody has spoken (quietSeconds), which the page stops on.
2. Stop takes no more audio and, in the background, decodes the recording once and drops its bytes; tells its voices
   apart (diarize.py); and transcribes it a turn at a time with MedASR, each turn cut at its own pauses, taking MedASR
   turn by turn so dictations' live transcripts go on between. Then it waits for the clinician: sorting
   "awaiting_speakers", with speakers, each voice that said something and for how long. A visit in which nobody said
   anything is done there, every field not dictated, and has nothing to name.
3. speakers names every voice: each voice told apart named once, exactly one clinician, exactly one patient, someone
   else only when the clinician said three would speak, and then once, and any other voice "unsure", when the clinician
   can't tell whose it is (its passages are then Unsure). Anything else is refused (422 speakers_incomplete): a voice
   left out or named twice, a third role on a visit of two, a group that isn't a plain number, a body that isn't one
   JSON object with "speakers" once. The web app checks the same against the consent. The same names again are answered as the first time, so a client that lost the
   answer can ask again; other names once they are given are refused (409 speakers_given).
4. Then, in the background, the note is drafted a window at a time (conversation.py, drafted()): windows of about
   conversation.WINDOW_TOKENS tokens, as llama-server counts them. Each window waits its turn with K2 Horizon
   (sessions.Turns: while no dictation is waiting or being sorted, one window at a time across visits) and then has
   structuring_seconds, with the conversation's prompt and its shorter answer (prompt.load_conversation()); if a
   dictation asks meanwhile, the window gives way at once and waits for its turn again. A window that timed out or ran
   out of tokens is drafted once more as two halves. A visit's windows wait ambient_wait_seconds in all at most; after
   that it is left undrafted with its transcript (model_busy: the service was busy). Its proposal is checked against the
   whole visit, its sources held to the window and to who may be quoted (validation.py), and the windows' fields are
   merged. A window K2 Horizon couldn't draft leaves the whole visit unsorted, saying why, so the review gets the
   transcript as plain text rather than a note missing a part nobody can see. A visit proposes no order or allergy
   (decision D18: its session's `proposes` is false, so proposals is always empty), and lists as not carried over only
   what the clinician said.

A visit's result is a dictation's with what a conversation adds: each segment's speakerGroup and speaker, the speakers
list while it awaits names, each source's speaker, the conversation's warnings, and provenance naming the speaker model
and the conversation prompt's hashes. Like a dictation, it keeps nothing but in memory: the recording until it is
transcribed, the embeddings until the voices are told apart, the transcript and fields until the session is discarded;
a visit whose voices are never named goes idle_seconds after it was ready to name. Logs carry the session, sizes,
times, counts and codes, never a word.
"""

from __future__ import annotations

import asyncio
import contextlib
import logging
import re
import time
from collections.abc import Awaitable, Callable
from dataclasses import dataclass, field

import numpy as np

from . import audio, conversation, diarize, prompt, quality, segmenter, transcriber, validation
from .audio import SAMPLE_RATE
from .diarize import Embedder
from .llama import AnswerCutOff, ModelTimeout, Proposal, StructuringFailed, Structurer
from .prompt import FIELDS
from .provenance import CODE_SHA256
from .transcript import UNCLEAR_MARK, Transcript
from .sessions import (
    AMBIENT, DICTATION, UNDECODABLE_BYTES, GaveWay, Session, SessionError, Sessions, TooBusy, _busy, _not_audio, _within,
)

log = logging.getLogger("ehr_dictation")

QUIET_RUN = 30  # frames: sound this long ends a quiet stretch; a click or a cough doesn't
GROUP = re.compile(r"[1-9][0-9]*")  # a voice's number, in ASCII digits only
MODEL_BUSY = "model_busy"  # a visit's windows waited ambient_wait_seconds for their turns, in all
DECODE_SECONDS = 60.0  # for the whole recording after stop, which nobody waits on: a 20-minute visit takes a few


@dataclass
class Visit:
    """What a visit's session holds besides a dictation's: no audio and no words."""

    speakers: int  # how many the clinician said would speak
    quiet_seconds: float = 0.0
    voices: tuple[dict, ...] = ()  # each voice that said something, {group, seconds}, once transcribed
    roles: dict[int, str] | None = None  # who the clinician said each voice is
    ready: asyncio.Event = field(default_factory=asyncio.Event)  # transcribed and told apart, or failed


def quiet_seconds(samples: np.ndarray) -> float:
    """How long it has been, at the end of the recording, since anyone spoke: since the last QUIET_RUN frames of sound."""
    levels = segmenter.levels(samples)
    if levels.size == 0:
        return round(samples.size / SAMPLE_RATE, 1)
    loud = (levels >= segmenter.pause_threshold(levels)) & (levels > quality.SILENCE_DBFS)
    edges = np.flatnonzero(np.diff(np.concatenate(([0], loud.astype(np.int8), [0]))))
    runs = [(start, stop) for start, stop in zip(edges[0::2], edges[1::2], strict=True) if stop - start >= QUIET_RUN]
    last = runs[-1][1] if runs else 0
    return round((levels.size - last) / segmenter.FRAMES_PER_SECOND, 1)


class Members(list):
    """A JSON object's members as (name, value) pairs, every one kept, in order (api.py's json.loads hook): a name
    given twice is two pairs, and an array is never mistaken for an object."""


def _incomplete() -> SessionError:
    return SessionError(422, "speakers_incomplete", "Name every voice: one clinician, one patient, and one other at most.")


class Listening(Sessions):
    """Dictations' sessions and visits'. embedder is the speaker model; conversation drafts a visit's note, by default
    the same K2 Horizon with the conversation's prompt."""

    def __init__(self, *args: object, embedder: Embedder | None = None, conversation: Structurer | None = None,
                 **options: object) -> None:
        super().__init__(*args, **options)
        self._embedder = embedder
        self._conversation = conversation or self._structurer.using(prompt.load_conversation())

    def start(self, owner: str, mode: str = DICTATION, speakers: int = 0, *, language: str = "en") -> Session:
        """A dictation in its language; a visit, which is in English (api.py refuses any other), with how many will
        speak."""
        if mode != AMBIENT:
            return super().start(owner, mode, language=language)
        if self._embedder is None:
            raise SessionError(503, "listening_unavailable",
                               "The speaker model isn't installed (setup_models.py) or couldn't be loaded.")
        if speakers not in (2, 3):
            raise SessionError(400, "bad_request", "Say how many people will speak: 2 or 3.")
        self.sweep()
        if sum(1 for s in self._sessions.values() if s.mode == AMBIENT and s.finished_at is None) >= (
            self._settings.ambient_max_sessions
        ):
            raise SessionError(429, "busy", "Too many visits are being listened to. Try again when one is drafted.")
        session = super().start(owner, mode)
        session.visit = Visit(speakers)
        session.proposes = False
        return session

    # ---- Audio ---------------------------------------------------------------------------------------------------

    async def hear(self, session_id: str, owner: str, seq: int, data: bytes) -> dict:
        deadline = self._clock() + self._settings.call_seconds
        session = self._get(session_id, owner, AMBIENT)
        async with _within(session.lock, self._left(deadline)) as locked:
            if not locked:
                raise _busy()
            self._still_there(session)
            if session.stopped:
                raise SessionError(409, "stopped", "This visit has stopped; it takes no more audio.")
            if seq < session.next_seq:
                return self._heard(session, seq)
            if seq > session.next_seq:
                raise SessionError(409, "out_of_order", "Send the chunks in order.", expectedSeq=session.next_seq)
            if len(data) > self._settings.max_chunk_bytes or (
                len(session.audio) + len(data) > self._settings.ambient_max_session_bytes
            ):
                raise SessionError(413, "too_large", "This chunk, or the visit, is too large.")

            started = time.perf_counter()
            candidate = bytes(session.audio) + data
            limit = self._settings.ambient_max_session_seconds
            try:
                samples = await asyncio.to_thread(
                    self._decode, candidate, partial=True, max_seconds=limit + 1,
                    timeout=min(audio.DECODE_SECONDS, self._left(deadline)),
                )
            except audio.DecodeTimeout:
                raise _busy() from None
            except audio.AudioError:
                raise _not_audio() from None
            self._still_there(session)
            if samples.size == 0 and len(candidate) > UNDECODABLE_BYTES:
                raise _not_audio()
            if samples.size > limit * SAMPLE_RATE:
                raise SessionError(413, "too_long", f"A visit can be listened to for up to {limit // 60} minutes.")
            session.audio = bytearray(candidate)
            session.audio_seconds = samples.size / SAMPLE_RATE
            session.next_seq += 1
            session.touched = self._clock()
            session.visit.quiet_seconds = quiet_seconds(samples)
            log.info(
                "session %s chunk %d: %d bytes, %.1f s of audio, %.0f s quiet, %.2f s",
                session.id, seq, len(data), session.audio_seconds, session.visit.quiet_seconds, time.perf_counter() - started,
            )
            return self._heard(session, seq)

    @staticmethod
    def _heard(session: Session, seq: int) -> dict:
        return {
            "receivedSeq": seq,
            "audioSeconds": round(session.audio_seconds, 1),
            "quietSeconds": session.visit.quiet_seconds,
            "transcript": {"text": "", "final": False, "segments": []},
        }

    # ---- Stop, voices, names -------------------------------------------------------------------------------------

    async def stop_listening(self, session_id: str, owner: str) -> dict:
        deadline = self._clock() + self._settings.stop_wait_seconds
        session = self._get(session_id, owner, AMBIENT)
        async with _within(session.lock, self._left(deadline)) as locked:
            if not locked:
                raise _busy()
            self._still_there(session)
            if not session.stopped:
                session.stopped = True
                session.touched = self._clock()
                recording, session.audio = bytes(session.audio), bytearray()
                session.task = asyncio.create_task(self._prepare(session, recording))
        return await self._answer(session, self._left(deadline))

    async def _prepare(self, session: Session, recording: bytes) -> None:
        """After stop: the recording decoded once, its voices told apart and each turn transcribed (see the header)."""
        started = time.perf_counter()
        visit = session.visit
        try:
            try:
                samples = await self._whole(recording)
            except audio.DecodeTimeout:
                self._unfinished(session, "DecodeTimeout")
                return
            except audio.AudioError:
                session.failure = _not_audio()
                return
            del recording
            decoded = time.perf_counter()
            try:
                found = await asyncio.to_thread(diarize.diarize, samples, self._embedder, visit.speakers)
                told = time.perf_counter()
                levels = segmenter.levels(samples)
                floor = segmenter.noise_floor(levels)
                passages = []
                for turn in found.turns:
                    async with self._transcribing:
                        said = await asyncio.to_thread(
                            transcriber.passages, samples, levels, floor, turn.start, turn.end, self._recognizer
                        )
                    passages += [(p, turn.voice, found.unsure(p.start_sample, p.end_sample, turn.voice, conversation.words(p)))
                                 for p in said]
            except Exception as error:  # noqa: BLE001 (nothing is drafted; the reason goes back)
                self._unfinished(session, type(error).__name__)
                return
            heard = conversation.assemble(passages).numbered()
            session.transcript = heard
            visit.voices = tuple(heard.speakers())
            log.info(
                "session %s stopped: %.1f s of audio, %d voice(s) of %d, %d passages (%d unsure, %d unclear); decoded in "
                "%.1f s, voices told in %.1f s, transcribed in %.1f s",
                session.id, samples.size / SAMPLE_RATE, len(visit.voices), visit.speakers, len(heard.placed),
                sum(heard.unsure), len(heard.unclear_ranges()), decoded - started, told - decoded,
                time.perf_counter() - told,
            )
            if not heard.placed:
                session.checks = validation.check({name: {"value": "", "sources": []} for name in FIELDS}, [], heard,
                                                  scope=validation.Scope((0, 0), ()))
                session.sorting = "done"
                return
            session.sorting = "awaiting_speakers"
            session.touched = self._clock()
        finally:
            if session.sorting != "awaiting_speakers":
                session.finished_at = self._clock()
                session.done.set()
            visit.ready.set()

    async def name_speakers(self, session_id: str, owner: str, names: object) -> dict:
        session = self._get(session_id, owner, AMBIENT)
        visit = session.visit
        if not session.stopped or not visit.ready.is_set():
            raise SessionError(409, "not_stopped", "Stop listening, and let the visit be transcribed, first.")
        roles = self._roles(names, visit)
        if visit.roles is not None:
            if roles != visit.roles:
                raise SessionError(409, "speakers_given", "Who each voice is was said already.")
            return await self._answer(session, 0)
        visit.roles = roles
        session.transcript = session.transcript.named(roles)
        session.sorting = "pending"
        session.touched = self._clock()
        session.task = asyncio.create_task(self._draft(session))
        log.info("session %s voices named", session.id)
        return await self._answer(session, 0)

    async def _whole(self, recording: bytes) -> np.ndarray:
        """The whole recording, decoded; tried once more if ffmpeg runs out of time."""

        def decode() -> np.ndarray:
            return self._decode(recording, partial=False, max_seconds=self._settings.ambient_max_session_seconds + 1,
                                timeout=DECODE_SECONDS)

        try:
            return await asyncio.to_thread(decode)
        except audio.DecodeTimeout:
            return await asyncio.to_thread(decode)

    @staticmethod
    def _roles(names: object, visit: Visit) -> dict[int, str]:
        """The names as given, a JSON object of a voice's number and its name (Members, from api.py, or a dict), if they
        are what the header says; else 422 speakers_incomplete."""
        if not isinstance(names, dict | Members):
            raise _incomplete()
        pairs = list(names.items()) if isinstance(names, dict) else list(names)
        if not all(isinstance(group, str) and GROUP.fullmatch(group) for group, _ in pairs):
            raise _incomplete()
        groups = [int(group) for group, _ in pairs]
        said = [role for _, role in pairs]
        if (
            len(set(groups)) != len(groups)
            or set(groups) != {voice["group"] for voice in visit.voices}
            or not all(isinstance(role, str) and role in conversation.NAMES for role in said)
            or said.count("clinician") != 1 or said.count("patient") != 1
            or said.count("other") > visit.speakers - 2
        ):
            raise _incomplete()
        return dict(zip(groups, said, strict=True))

    # ---- Drafting ------------------------------------------------------------------------------------------------

    async def _draft(self, session: Session) -> None:
        """After the voices are named: the note drafted a window at a time and checked (see the header)."""
        started = time.perf_counter()
        heard = session.transcript
        passages = heard.passages()
        waited = 0.0

        async def ask(window: conversation.Window) -> Proposal:
            nonlocal waited
            while True:
                async with self._turns.window(self._settings.ambient_wait_seconds - waited) as turn:
                    waited += turn.waited
                    began = time.perf_counter()
                    try:
                        async with asyncio.timeout(self._settings.structuring_seconds), self._sorting:
                            proposal = await turn.draft(self._conversation.propose(window.text))
                    except GaveWay:
                        log.info("session %s window gave way to a dictation", session.id)
                        continue
                    log.info("session %s window drafted in %.1f s", session.id, time.perf_counter() - began)
                    return proposal

        try:
            parts = [
                (scope, validation.check(proposal.fields, proposal.unknown, heard, scope=scope))
                for window, proposal in await drafted(heard, self._conversation, ask)
                for scope in [validation.Scope((window.start, window.end), passages)]
            ]
            checks = validation.merge(parts)
            session.checks, session.sorting = checks, "done"
            log.info(
                "session %s drafted in %.1f s: %s",
                session.id,
                time.perf_counter() - started,
                ", ".join(f"{name} {'pending' if c.pending else '/'.join(c.reasons)}" for name, c in checks.fields.items()),
            )
        except TooBusy:
            session.sorting, session.reason = "unavailable", MODEL_BUSY
            log.warning("session %s not drafted: %s", session.id, MODEL_BUSY)
        except TimeoutError:
            session.sorting, session.reason = "unavailable", "model_timeout"
            log.warning("session %s not drafted: model_timeout", session.id)
        except StructuringFailed as error:
            session.sorting, session.reason = "unavailable", error.reason
            log.warning("session %s not drafted: %s", session.id, error.reason)
        except asyncio.CancelledError:
            raise
        except Exception as error:  # noqa: BLE001 (the transcript still goes back, undrafted)
            session.sorting, session.reason = "unavailable", "model_output_invalid"
            log.error("session %s not drafted: %s", session.id, type(error).__name__)
        finally:
            session.finished_at = self._clock()
            session.done.set()

    # ---- Result --------------------------------------------------------------------------------------------------

    async def visit_result(self, session_id: str, owner: str, wait: float) -> dict:
        session = self._get(session_id, owner, AMBIENT)
        if not session.stopped:
            raise SessionError(409, "not_stopped", "Stop listening first.")
        return await self._answer(session, min(max(wait, 0.0), self._settings.max_result_wait_seconds))

    async def _answer(self, session: Session, wait: float) -> dict:
        """What the visit has come to, waiting up to `wait` seconds for its next step: its voices told apart, or, once
        they are named, its note drafted."""
        step = session.done if session.visit.roles is not None else session.visit.ready
        if wait > 0 and not step.is_set():
            with contextlib.suppress(TimeoutError):
                await asyncio.wait_for(asyncio.shield(step.wait()), timeout=wait)
        self._still_there(session)
        if session.failure is not None:
            raise session.failure
        return result_json(session.transcript, session.sorting, session.reason, session.checks, session.proposes,
                           session.visit.voices, self.visit_provenance())

    def visit_provenance(self) -> dict:
        structuring = self._conversation.structuring
        return {
            "speech": self._models.speech,
            "language": self._models.language,
            "llamaServer": self._conversation.build,
            "prompt": f"sha256:{structuring.prompt_sha256}",
            "schema": f"sha256:{structuring.schema_sha256}",
            "structuring": f"sha256:{structuring.structuring_sha256}",
            "code": f"sha256:{CODE_SHA256}",
            "speaker": self._models.speaker,
        }


def result_json(heard: Transcript, sorting: str, reason: str | None, checks: validation.Checks | None, proposes: bool,
                voices: tuple[dict, ...], provenance: dict) -> dict:
    """A visit's result as the service sends it (see the header); the eval builds its items' from the same."""
    named = isinstance(heard, conversation.Conversation) and bool(heard.roles)
    result = {
        "transcript": heard.to_json(),
        "sorting": sorting,
        "reason": reason if sorting == "unavailable" else None,
        "fields": checks.to_json(heard) if sorting == "done" and checks else None,
        "notCarriedOver": validation.conversation_not_carried_over(heard, heard.passages()) if named else [],
        "proposals": validation.proposals(heard) if proposes else [],
        "provenance": provenance,
    }
    if sorting == "awaiting_speakers":
        result["speakers"] = list(voices)
    return result


async def passage_tokens(heard: conversation.Conversation, structurer: Structurer) -> list[int]:
    """Each passage's length in tokens, by llama-server's tokenizer, as its window shows it."""
    return [await structurer.tokens(UNCLEAR_MARK if placed.segment.unclear else placed.segment.text) for placed in heard.placed]


async def drafted(heard: conversation.Conversation, structurer: Structurer,
                  ask: Callable[[conversation.Window], Awaitable[Proposal]],
                  sizes: list[int] | None = None) -> list[tuple[conversation.Window, Proposal]]:
    """Each window of the named visit and K2 Horizon's proposal for it, asked for with `ask` (see the header): windows
    by llama-server's count of each passage's tokens (passage_tokens(), unless given), and a window that timed out or
    ran out of tokens asked for again as two halves, once. Raises what the first window that still couldn't be drafted
    raised."""
    sizes = sizes if sizes is not None else await passage_tokens(heard, structurer)
    out: list[tuple[conversation.Window, Proposal]] = []
    for window in conversation.windows(heard, conversation.WINDOW_TOKENS, sizes):
        try:
            out.append((window, await ask(window)))
        except (TimeoutError, ModelTimeout, AnswerCutOff):
            halves = conversation.halves(heard, window)
            if not halves:
                raise
            log.info("a window of %d passages is drafted again as two", window.last - window.first)
            out += [(half, await ask(half)) for half in halves]
    return out
