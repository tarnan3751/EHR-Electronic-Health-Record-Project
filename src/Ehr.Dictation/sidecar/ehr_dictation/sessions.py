"""Dictation sessions, in memory only: nothing about one is ever written anywhere, and each is discarded when it's
cancelled, when no audio has reached it for a while, or a while after its result is ready.

A session belongs to the clinician who started it (the owner the on-prem app names); to anyone else it doesn't exist.
Audio arrives in numbered chunks. A chunk sent again is ignored, so a client that lost an answer can resend; a chunk
that skips ahead is refused with the number expected, so a client that lost its connection resends from there and the
transcript carries on (FR-10); a chunk ffmpeg can't decode in time is refused as busy, to be sent again. After each chunk the transcript is brought up to date (transcriber.py), if MedASR is
free in time; if not, the chunk is kept and the answer has the transcript as it was, and the next chunk catches up.

Stop takes no more audio and, in the background, transcribes the rest, drops the audio, and has K2 Horizon sort the
transcript. Stop and result answer within a few seconds whatever state that has reached: the transcript (final once
transcribed), the fields once sorted, and every instruction in the transcript to order something or change the allergy
list, which the note never carries (notCarriedOver), so the review screen can say what still needs placing, and, once
the transcript is final, those of them the dictation proposes (proposals: an order, or an allergy to add, dictated
plainly; validation.proposals(), the one place they are made). A session whose dictation must propose nothing has
`proposes` set to False, and its results carry none. If K2
Horizon is unavailable or too slow, the result keeps the transcript and says why the fields are missing (UC 10); if
MedASR fails on the rest of the recording, or ffmpeg can't decode it in time twice over, it keeps the transcript as it
was live, not final, with the reason transcription_failed. The fields are checked (validation.py) and marked pending; a field that failed
carries only its reason codes.

Cancelling discards everything at once (UC 9): the session is marked cancelled, and its background work is cancelled,
including a model call in flight, so it stores nothing more; a call still waiting on the session checks the mark after
each wait and stores nothing either.

Every call answers within its budget (settings.py), well inside the on-prem app's 20 seconds: waits for the session,
for MedASR and for the fields are cut to what is left of it. A session counts against the limits on sessions at once
until its result is ready, sorting included, and the time K2 Horizon has starts when the session starts waiting for it.

Logs name the session (a random ID, not patient information), sizes, times and reason codes, never audio or text.
"""

from __future__ import annotations

import asyncio
import contextlib
import logging
import secrets
import time
from collections.abc import AsyncIterator, Callable
from dataclasses import dataclass, field

import numpy as np

from . import audio, validation
from .asr import Recognizer
from .llama import StructuringFailed, Structurer
from .prompt import FIELDS
from .provenance import CODE_SHA256, Models
from .settings import Settings
from .transcriber import Progress, advance
from .transcript import Transcript, assemble

log = logging.getLogger("ehr_dictation")

# An opening chunk can be too short to decode; this much audio that still decodes to nothing isn't audio.
UNDECODABLE_BYTES = 256 * 1024


class SessionError(Exception):
    def __init__(self, status: int, code: str, detail: str, **extra: object) -> None:
        super().__init__(detail)
        self.status = status
        self.code = code
        self.detail = detail
        self.extra = extra


def _not_found() -> SessionError:
    return SessionError(404, "not_found", "No such dictation: it was cancelled or expired, or it isn't yours.")


def _busy() -> SessionError:
    return SessionError(429, "busy", "Dictation is busy. Try again in a moment.")


def _not_audio() -> SessionError:
    return SessionError(422, "not_audio", "The audio couldn't be decoded.")


@dataclass
class Session:
    id: str
    owner: str
    touched: float
    audio: bytearray = field(default_factory=bytearray)
    next_seq: int = 0
    audio_seconds: float = 0.0
    progress: Progress = field(default_factory=Progress)
    transcript: Transcript = field(default_factory=lambda: assemble([], final=False))
    stopped: bool = False
    cancelled: bool = False
    failure: SessionError | None = None
    sorting: str = "pending"
    reason: str | None = None
    checks: validation.Checks | None = None
    # Whether its results propose the orders and allergies to add it dictates (the expansion plan's step 15). The one
    # switch for them: a kind of session that must propose nothing sets it False when it starts.
    proposes: bool = True
    finished_at: float | None = None
    task: asyncio.Task | None = None
    lock: asyncio.Lock = field(default_factory=asyncio.Lock)
    done: asyncio.Event = field(default_factory=asyncio.Event)


class Sessions:
    def __init__(
        self,
        settings: Settings,
        recognizer: Recognizer,
        structurer: Structurer,
        models: Models,
        *,
        clock: Callable[[], float] = time.monotonic,
        decoder: Callable[..., np.ndarray] = audio.decode,
    ) -> None:
        self._settings = settings
        self._recognizer = recognizer
        self._structurer = structurer
        self._models = models
        self._clock = clock
        self._decode = decoder
        self._sessions: dict[str, Session] = {}
        self._transcribing = asyncio.Semaphore(1)
        self._sorting = asyncio.Semaphore(settings.sorting_at_once)

    @property
    def count(self) -> int:
        return len(self._sessions)

    # ---- Lifecycle -----------------------------------------------------------------------------------------------

    def start(self, owner: str) -> Session:
        self.sweep()
        # Until its result is ready, a session is using MedASR, K2 Horizon or both.
        working = [s for s in self._sessions.values() if s.finished_at is None]
        if len(working) >= self._settings.max_sessions:
            raise SessionError(429, "busy", "Too many dictations are in progress. Try again in a minute.")
        if sum(1 for s in working if s.owner == owner) >= self._settings.max_sessions_per_owner:
            raise SessionError(429, "busy", "Finish or cancel your other dictation first.")
        session = Session(id=secrets.token_urlsafe(24), owner=owner, touched=self._clock())
        self._sessions[session.id] = session
        log.info("session %s started", session.id)
        return session

    def cancel(self, session_id: str, owner: str) -> None:
        session = self._get(session_id, owner)
        self._discard(session)
        log.info("session %s cancelled", session.id)

    def sweep(self) -> None:
        now = self._clock()
        for session in list(self._sessions.values()):
            if session.finished_at is not None:
                expired = now - session.finished_at > self._settings.result_seconds
            else:
                expired = not session.stopped and now - session.touched > self._settings.idle_seconds
            if expired:
                self._discard(session)
                log.info("session %s expired", session.id)

    def _discard(self, session: Session) -> None:
        session.cancelled = True
        self._sessions.pop(session.id, None)
        if session.task is not None:
            session.task.cancel()
        session.audio = bytearray()
        session.progress = Progress()
        session.transcript = assemble([], final=False)
        session.checks = None

    def _get(self, session_id: str, owner: str) -> Session:
        self.sweep()
        session = self._sessions.get(session_id)
        if session is None or not secrets.compare_digest(session.owner, owner):
            raise _not_found()
        return session

    @staticmethod
    def _still_there(session: Session) -> None:
        """After every wait: a session cancelled meanwhile is gone, and nothing more is stored for it or sent."""
        if session.cancelled:
            raise _not_found()

    # ---- Audio ---------------------------------------------------------------------------------------------------

    async def add_audio(self, session_id: str, owner: str, seq: int, data: bytes) -> dict:
        deadline = self._clock() + self._settings.call_seconds
        session = self._get(session_id, owner)
        async with _within(session.lock, self._left(deadline)) as locked:
            if not locked:
                raise _busy()
            self._still_there(session)
            if session.stopped:
                raise SessionError(409, "stopped", "This dictation has stopped; start another.")
            if seq < session.next_seq:
                return self._received(session, seq)
            if seq > session.next_seq:
                raise SessionError(409, "out_of_order", "Send the chunks in order.", expectedSeq=session.next_seq)
            if len(data) > self._settings.max_chunk_bytes or len(session.audio) + len(data) > self._settings.max_session_bytes:
                raise SessionError(413, "too_large", "This chunk, or the dictation, is too large.")

            started = time.perf_counter()
            candidate = bytes(session.audio) + data
            limit = self._settings.max_session_seconds
            try:
                samples = await asyncio.to_thread(
                    self._decode, candidate, partial=True, max_seconds=limit + 1,
                    timeout=min(audio.DECODE_SECONDS, self._left(deadline)),
                )
            except audio.DecodeTimeout:
                raise _busy() from None  # the chunk isn't kept: sent again, it's decoded again
            except audio.AudioError:
                raise _not_audio() from None
            self._still_there(session)
            if samples.size == 0 and len(candidate) > UNDECODABLE_BYTES:
                raise _not_audio()
            if samples.size > limit * audio.SAMPLE_RATE:
                raise SessionError(413, "too_long", f"A dictation can be up to {limit // 60} minutes.")
            session.audio = bytearray(candidate)
            session.audio_seconds = samples.size / audio.SAMPLE_RATE
            session.next_seq += 1
            session.touched = self._clock()

            # The transcript catches up if MedASR is free in time; otherwise the next chunk brings it up to date.
            async with _within(self._transcribing, self._left(deadline)) as transcribing:
                if transcribing:
                    progress = await asyncio.to_thread(advance, samples, session.progress, self._recognizer, final=False)
                    self._still_there(session)
                    session.progress = progress
                    session.transcript = assemble(progress.all_segments, final=False)
            log.info(
                "session %s chunk %d: %d bytes, %.1f s of audio, %.2f s%s",
                session.id, seq, len(data), samples.size / audio.SAMPLE_RATE, time.perf_counter() - started,
                "" if transcribing else ", transcript not updated (MedASR busy)",
            )
            return self._received(session, seq)

    @staticmethod
    def _received(session: Session, seq: int) -> dict:
        # How much audio the dictation holds, so a screen can show what's left of maxSeconds before the cut-off.
        return {"receivedSeq": seq, "audioSeconds": round(session.audio_seconds, 1), "transcript": session.transcript.to_json()}

    # ---- Stop and result -----------------------------------------------------------------------------------------

    async def stop(self, session_id: str, owner: str) -> dict:
        deadline = self._clock() + self._settings.stop_wait_seconds
        session = self._get(session_id, owner)
        async with _within(session.lock, self._left(deadline)) as locked:
            if not locked:
                raise _busy()
            self._still_there(session)
            if not session.stopped:
                session.stopped = True
                session.touched = self._clock()
                recording, session.audio = bytes(session.audio), bytearray()
                session.task = asyncio.create_task(self._finish(session, recording))
        return await self._result(session, self._left(deadline))

    async def result(self, session_id: str, owner: str, wait: float) -> dict:
        session = self._get(session_id, owner)
        if not session.stopped:
            raise SessionError(409, "not_stopped", "Stop the dictation first.")
        return await self._result(session, min(max(wait, 0.0), self._settings.max_result_wait_seconds))

    async def _result(self, session: Session, wait: float) -> dict:
        if wait > 0 and not session.done.is_set():
            with contextlib.suppress(TimeoutError):
                await asyncio.wait_for(asyncio.shield(session.done.wait()), timeout=wait)
        self._still_there(session)
        if session.failure is not None:
            raise session.failure
        sorting = session.sorting if session.done.is_set() else "pending"
        return {
            "transcript": session.transcript.to_json(),
            "sorting": sorting,
            "reason": session.reason if sorting == "unavailable" else None,
            "fields": session.checks.to_json(session.transcript) if sorting == "done" and session.checks else None,
            "notCarriedOver": validation.not_carried_over(session.transcript),
            "proposals": validation.proposals(session.transcript) if session.proposes else [],
            "provenance": self.provenance(),
        }

    async def _finish(self, session: Session, recording: bytes) -> None:
        """After stop: the rest of the transcript, then the fields. Cancelling the session cancels this task."""
        started = time.perf_counter()
        try:
            try:
                samples = await self._decoded(recording)
            except audio.DecodeTimeout:
                self._unfinished(session, "DecodeTimeout")
                return
            except audio.AudioError:
                session.failure = _not_audio()
                return
            try:
                async with self._transcribing:
                    progress = await asyncio.to_thread(advance, samples, session.progress, self._recognizer, final=True)
            except Exception as error:  # noqa: BLE001 (the live transcript still goes back)
                self._unfinished(session, type(error).__name__)
                return
            session.progress = progress
            session.transcript = assemble(progress.segments, final=True)
            log.info(
                "session %s stopped: %.1f s of audio, %d passages (%d unclear), %d proposals, %.2f s",
                session.id,
                samples.size / audio.SAMPLE_RATE,
                len(session.transcript.placed),
                len(session.transcript.unclear_ranges()),
                len(validation.proposals(session.transcript)) if session.proposes else 0,
                time.perf_counter() - started,
            )
            await self._sort(session)
        finally:
            session.finished_at = self._clock()
            session.done.set()

    async def _decoded(self, recording: bytes) -> np.ndarray:
        """The whole recording, decoded; tried once more if ffmpeg runs out of time, since nobody is waiting on it."""

        def decode() -> np.ndarray:
            return self._decode(recording, partial=False, max_seconds=self._settings.max_session_seconds + 1)

        try:
            return await asyncio.to_thread(decode)
        except audio.DecodeTimeout:
            return await asyncio.to_thread(decode)

    @staticmethod
    def _unfinished(session: Session, cause: str) -> None:
        """The rest of the transcript couldn't be made: the live one stays, not final, and nothing is sorted."""
        session.sorting, session.reason = "unavailable", "transcription_failed"
        log.warning("session %s not transcribed: %s", session.id, cause)

    async def _sort(self, session: Session) -> None:
        started = time.perf_counter()
        transcript = session.transcript
        try:
            if not transcript.text.strip():
                checks = validation.check({name: {"value": "", "sources": []} for name in FIELDS}, [], transcript)
            else:
                # K2 Horizon's time starts now, waiting for a turn included.
                async with asyncio.timeout(self._settings.structuring_seconds), self._sorting:
                    proposal = await self._structurer.propose(transcript.for_structuring())
                checks = validation.check(proposal.fields, proposal.unknown, transcript)
            session.checks, session.sorting = checks, "done"
            log.info(
                "session %s sorted in %.1f s: %s%s",
                session.id,
                time.perf_counter() - started,
                ", ".join(
                    f"{name} {'pending' if checked.pending else '/'.join(checked.reasons)}"
                    for name, checked in checks.fields.items()
                ),
                f"; unknown fields {len(checks.unknown)}" if checks.unknown else "",
            )
        except TimeoutError:
            session.sorting, session.reason = "unavailable", "model_timeout"
            log.warning("session %s not sorted: model_timeout", session.id)
        except StructuringFailed as error:
            session.sorting, session.reason = "unavailable", error.reason
            log.warning("session %s not sorted: %s", session.id, error.reason)
        except asyncio.CancelledError:
            raise
        except Exception as error:  # noqa: BLE001 (the transcript still goes back, unsorted)
            session.sorting, session.reason = "unavailable", "model_output_invalid"
            log.error("session %s not sorted: %s", session.id, type(error).__name__)

    def _left(self, deadline: float) -> float:
        return max(0.0, deadline - self._clock())

    def provenance(self) -> dict:
        structuring = self._structurer.structuring
        return {
            "speech": self._models.speech,
            "language": self._models.language,
            "llamaServer": self._structurer.build,
            "prompt": f"sha256:{structuring.prompt_sha256}",
            "schema": f"sha256:{structuring.schema_sha256}",
            "structuring": f"sha256:{structuring.structuring_sha256}",
            "code": f"sha256:{CODE_SHA256}",
        }


@contextlib.asynccontextmanager
async def _within(lock: asyncio.Lock | asyncio.Semaphore, seconds: float) -> AsyncIterator[bool]:
    """Holds the lock if it comes free within `seconds`, and says whether it did."""
    try:
        await asyncio.wait_for(lock.acquire(), timeout=max(seconds, 0.001))
    except TimeoutError:
        yield False
        return
    try:
        yield True
    finally:
        lock.release()
