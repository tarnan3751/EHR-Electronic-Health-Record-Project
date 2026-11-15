"""Dictation sessions, in memory only: nothing about one is ever written anywhere, and each is discarded when it's
cancelled, when no audio has reached it for a while, or a while after its result is ready.

A session belongs to the clinician who started it (the owner the on-prem app names); to anyone else it doesn't exist.
Audio arrives in numbered chunks. A chunk sent again is ignored, so a client that lost an answer can resend; a chunk
that skips ahead is refused with the number expected, so a client that lost its connection resends from there and the
transcript carries on (FR-10). After each chunk the transcript is brought up to date (transcriber.py).

Stop commits the rest of the transcript, drops the audio, and starts K2 Horizon on the transcript in the background;
the result is collected with result(), which waits a few seconds for it. If K2 Horizon is unavailable or too slow, the
result keeps the transcript and says why the fields are missing (UC 10). Its fields are checked (validation.py) and
marked pending; a field that failed carries only its reason codes.

Logs name the session (a random ID, not patient information), sizes, times and reason codes, never audio or text.
"""

from __future__ import annotations

import asyncio
import logging
import secrets
import time
from collections.abc import Callable
from dataclasses import dataclass, field

import numpy as np

from . import audio, validation
from .asr import Recognizer
from .llama import StructuringFailed, Structurer
from .provenance import Models
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


@dataclass
class Session:
    id: str
    owner: str
    touched: float
    audio: bytearray = field(default_factory=bytearray)
    next_seq: int = 0
    progress: Progress = field(default_factory=Progress)
    transcript: Transcript = field(default_factory=lambda: assemble([], final=False))
    stopped: bool = False
    sorting: str = "pending"
    reason: str | None = None
    checks: validation.Checks | None = None
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
        self._sorting = asyncio.Semaphore(2)

    @property
    def count(self) -> int:
        return len(self._sessions)

    # ---- Lifecycle -----------------------------------------------------------------------------------------------

    def start(self, owner: str) -> Session:
        self.sweep()
        recording = [s for s in self._sessions.values() if not s.stopped]
        if len(recording) >= self._settings.max_sessions:
            raise SessionError(429, "busy", "Too many dictations are in progress. Try again in a minute.")
        if sum(1 for s in recording if s.owner == owner) >= self._settings.max_sessions_per_owner:
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

    # ---- Audio ---------------------------------------------------------------------------------------------------

    async def add_audio(self, session_id: str, owner: str, seq: int, data: bytes) -> dict:
        session = self._get(session_id, owner)
        async with session.lock:
            if session.id not in self._sessions:
                raise _not_found()
            if session.stopped:
                raise SessionError(409, "stopped", "This dictation has stopped; start another.")
            if seq < session.next_seq:
                return {"receivedSeq": seq, "transcript": session.transcript.to_json()}
            if seq > session.next_seq:
                raise SessionError(409, "out_of_order", "Send the chunks in order.", expectedSeq=session.next_seq)
            if len(data) > self._settings.max_chunk_bytes or len(session.audio) + len(data) > self._settings.max_session_bytes:
                raise SessionError(413, "too_large", "This chunk, or the dictation, is too large.")

            started = time.perf_counter()
            candidate = bytes(session.audio) + data
            samples = await asyncio.to_thread(self._decode, candidate, partial=True)
            if samples.size == 0 and len(candidate) > UNDECODABLE_BYTES:
                raise SessionError(422, "not_audio", "The audio couldn't be decoded.")
            if samples.size > self._settings.max_session_seconds * audio.SAMPLE_RATE:
                raise SessionError(
                    413, "too_long", f"A dictation can be up to {self._settings.max_session_seconds // 60} minutes."
                )
            session.audio = bytearray(candidate)
            session.next_seq += 1
            session.touched = self._clock()
            async with self._transcribing:
                session.progress = await asyncio.to_thread(
                    advance, samples, session.progress, self._recognizer, final=False
                )
            session.transcript = assemble(session.progress.all_segments, final=False)
            log.info(
                "session %s chunk %d: %d bytes, %.1f s of audio, %.2f s",
                session.id, seq, len(data), samples.size / audio.SAMPLE_RATE, time.perf_counter() - started,
            )
            return {"receivedSeq": seq, "transcript": session.transcript.to_json()}

    # ---- Stop and result -----------------------------------------------------------------------------------------

    async def stop(self, session_id: str, owner: str) -> dict:
        session = self._get(session_id, owner)
        async with session.lock:
            if session.id not in self._sessions:
                raise _not_found()
            if not session.stopped:
                started = time.perf_counter()
                try:
                    samples = await asyncio.to_thread(self._decode, bytes(session.audio), partial=False)
                except audio.AudioError:
                    raise SessionError(422, "not_audio", "The audio couldn't be decoded.") from None
                async with self._transcribing:
                    session.progress = await asyncio.to_thread(
                        advance, samples, session.progress, self._recognizer, final=True
                    )
                session.transcript = assemble(session.progress.segments, final=True)
                session.stopped = True
                session.audio = bytearray()
                session.touched = self._clock()
                log.info(
                    "session %s stopped: %.1f s of audio, %d passages (%d unclear), %.2f s",
                    session.id,
                    samples.size / audio.SAMPLE_RATE,
                    len(session.transcript.placed),
                    len(session.transcript.unclear_ranges()),
                    time.perf_counter() - started,
                )
                session.task = asyncio.create_task(self._sort(session))
        return await self._result(session, self._settings.stop_wait_seconds)

    async def result(self, session_id: str, owner: str, wait: float) -> dict:
        session = self._get(session_id, owner)
        if not session.stopped:
            raise SessionError(409, "not_stopped", "Stop the dictation first.")
        return await self._result(session, min(max(wait, 0.0), self._settings.max_result_wait_seconds))

    async def _result(self, session: Session, wait: float) -> dict:
        if wait > 0 and not session.done.is_set():
            try:
                await asyncio.wait_for(asyncio.shield(session.done.wait()), timeout=wait)
            except TimeoutError:
                pass
        sorting = session.sorting if session.done.is_set() else "pending"
        return {
            "transcript": session.transcript.to_json(),
            "sorting": sorting,
            "reason": session.reason if sorting == "unavailable" else None,
            "fields": session.checks.to_json(session.transcript) if sorting == "done" and session.checks else None,
            "provenance": self.provenance(),
        }

    async def _sort(self, session: Session) -> None:
        started = time.perf_counter()
        transcript = session.transcript
        try:
            if not transcript.text.strip():
                fields = {name: {"value": "", "sources": []} for name in ("history", "assessment", "plan")}
                session.checks = validation.check(fields, [], transcript)
            else:
                async with self._sorting:
                    proposal = await self._structurer.propose(transcript.for_structuring())
                session.checks = validation.check(proposal.fields, proposal.unknown, transcript)
            session.sorting = "done"
            log.info(
                "session %s sorted in %.1f s: %s%s",
                session.id,
                time.perf_counter() - started,
                ", ".join(
                    f"{name} {'pending' if checked.pending else '/'.join(checked.reasons)}"
                    for name, checked in session.checks.fields.items()
                ),
                f"; unknown fields {len(session.checks.unknown)}" if session.checks.unknown else "",
            )
        except StructuringFailed as error:
            session.sorting, session.reason = "unavailable", error.reason
            log.warning("session %s not sorted: %s", session.id, error.reason)
        except asyncio.CancelledError:
            raise
        except Exception as error:  # noqa: BLE001 (the transcript still goes back, unsorted)
            session.sorting, session.reason = "unavailable", "model_output_invalid"
            log.error("session %s not sorted: %s", session.id, type(error).__name__)
        finally:
            session.finished_at = self._clock()
            session.done.set()

    def provenance(self) -> dict:
        structuring = self._structurer.structuring
        return {
            "speech": self._models.speech,
            "language": self._models.language,
            "llamaServer": self._structurer.build,
            "prompt": f"sha256:{structuring.prompt_sha256}",
            "schema": f"sha256:{structuring.schema_sha256}",
            "structuring": f"sha256:{structuring.structuring_sha256}",
            "validation": validation.VERSION,
        }
