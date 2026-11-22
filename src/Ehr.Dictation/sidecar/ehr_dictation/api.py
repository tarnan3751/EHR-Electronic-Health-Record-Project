"""The sidecar's HTTP API, which only the on-prem app calls (Ehr.Web, Api/Dictation). Run it with
`uvicorn --factory ehr_dictation.api:create_app`; the ai profile's image does.

Every route but /health needs `Authorization: Bearer <DICTATION_API_KEY>`: the inference network has no route out, but
being on it proves nothing. Session routes also need `X-Dictation-Owner`, the signed-in clinician's ID, which the on-prem
app sets from the session and never from the request; a session is invisible to any other owner.

    GET    /health                           {"status": "ok"}, nothing more; no key needed
    POST   /v1/sessions                      body: none, or {"spokenLanguage": "en" or "es"}; 201 {sessionId,
                                             maxSeconds, maxChunkBytes}, maxSeconds 120 in Spanish and 180 in English
    POST   /v1/sessions/{id}/audio?seq=N     body: the next chunk, any container; 200 {receivedSeq, audioSeconds,
                                             transcript}, audioSeconds being how much of maxSeconds the audio uses
    POST   /v1/sessions/{id}/stop            the transcript, and the fields, as far as they get within 8 s
    POST   /v1/sessions/{id}/result?wait=S   the same, waiting up to S (at most 10) seconds for the fields
    DELETE /v1/sessions/{id}                 204; audio, transcript and fields are gone

A dictation is in English unless it starts with {"spokenLanguage": "es"} (the expansion plan's step 17); see
sessions.py for what a dictation in Spanish goes through. A transcript is {text, final, segments: [{start, end, startMs,
endMs, final, unclear}]}, with UTF-16 offsets into text and unclear one of too_quiet, too_noisy, low_confidence, or in
Spanish other_language (a passage Whisper didn't hear as Spanish), or null; a Spanish transcript has no tentative
tail. A result is {transcript, sorting, reason, fields,
notCarriedOver, proposals, provenance}: the transcript is final once the rest of the audio is transcribed, and sorting is pending,
done (fields set) or unavailable (reason model_unavailable, model_timeout, model_output_invalid or transcript_too_long,
and the transcript stays as plain text; or transcription_failed, and it stays as it was live, not final).
notCarriedOver lists each instruction in the transcript to order something or change the allergy list, which no field
carries: [{reason: "order" or "allergy_change", start, end}], the span of the clinician's own words in the transcript,
never anything the model wrote. proposals are those of them the dictation proposes once the transcript is final (the
expansion plan's step 15): [{kind: "order" or "allergy_addition", start, end, warnings: [{code, start, end}]}], each
at its listed instruction's place, with possible_correction or near_unclear_passage warnings as on a field; never
words, and never anything the model wrote. provenance is {speech, language, llamaServer, prompt, schema, structuring, code};
language and llamaServer are null while unknown. Each field is {status: "pending", value, sources: [{start, end}],
warnings: [{code, start, end}]} or {status: "empty", reasons: [...]}: a value that failed a check is never sent, only
why; a warning is possible_correction, pointing at a later number, date or drug in the transcript that may be a
correction the checks didn't hear, near_unclear_passage, pointing at a passage marked unclear beside one of the
value's sources, or one_field_holds_dictation, on the only value offered when it quotes nearly the whole transcript,
pointing at what it quotes (validation.py). A result in Spanish also has "spokenLanguage": "es"; its pending fields
are English, each with statements, [{start, end, sources: [{start, end}]}], each sentence's UTF-16 place in the value
and the Spanish passages it was translated from, and may also warn ambiguous_number or ambiguous_frequency; its
notCarriedOver lists the Spanish instructions, its proposals are always empty, and its provenance names Whisper and
the Spanish prompt, schema and structuring (translation.py).

The limits on all of it (sizes, sessions at once, how long things are kept and waited for) are in settings.py.

Errors are {error, detail} with 400 (no owner, or a bad seq or wait; bad_language, a start body that isn't one of
the two above), 401 (no key), 404 (no such session for this owner), 409 (out_of_order with expectedSeq, stopped,
not_stopped), 413 (too_large, too_long), 422 (not_audio), 429 (busy) or 503 (language_unavailable: Spanish without
Whisper installed).
Nothing a request carries is logged.
"""

from __future__ import annotations

import asyncio
import contextlib
import hashlib
import hmac
import json
import logging
import re
from collections.abc import AsyncIterator

from fastapi import FastAPI, Query, Request, Response
from fastapi.exceptions import RequestValidationError
from fastapi.responses import JSONResponse

from . import prompt, provenance
from .asr import MedAsr, Recognizer
from .llama import LlamaServer, Structurer
from .sessions import SessionError, Sessions, Spanish
from .settings import Settings
from .whisper_asr import Whisper

log = logging.getLogger("ehr_dictation")

OWNER = re.compile(r"[A-Za-z0-9-]{1,100}")
LANGUAGES = ("en", "es")


def create_app(
    settings: Settings | None = None,
    *,
    recognizer: Recognizer | None = None,
    structurer: Structurer | None = None,
    models: provenance.Models | None = None,
    spanish: Spanish | None = None,
) -> FastAPI:
    """The app, from the environment unless told otherwise (the tests pass stand-ins for the models). Refuses to start
    without its keys, or with a MedASR or a Whisper that isn't the one models-manifest.json records; starts without
    Spanish when Whisper isn't installed."""
    settings = settings or Settings.from_environment()
    _configure_logging()
    expected = hashlib.sha256(f"Bearer {settings.api_key}".encode()).digest()

    @contextlib.asynccontextmanager
    async def lifespan(app: FastAPI) -> AsyncIterator[None]:
        loaded_models = models or provenance.load(settings.models_dir)
        llama = None
        sorter = structurer
        in_spanish = spanish
        if sorter is None:
            llama = LlamaServer(settings.llama_url, settings.llama_api_key)
            sorter = Structurer(llama, prompt.load(), loaded_models.language_file, timeout=settings.structuring_seconds)
            if in_spanish is None and loaded_models.spanish_speech is not None:
                in_spanish = Spanish(
                    Whisper.load(settings.models_dir / provenance.WHISPER_DIR, settings.device),
                    Structurer(llama, prompt.load(language="es"), loaded_models.language_file,
                               timeout=settings.structuring_seconds),
                )
        asr = recognizer or MedAsr(settings.models_dir / provenance.MEDASR_DIR, settings.device)
        app.state.sessions = Sessions(settings, asr, sorter, loaded_models, spanish=in_spanish)
        sweeper = asyncio.create_task(_sweep(app.state.sessions))
        log.info("ready: MedASR on %s, %s, llama-server at %s", settings.device,
                 f"Whisper on {settings.device}" if in_spanish else "no Whisper (Spanish refused)", settings.llama_url)
        try:
            yield
        finally:
            sweeper.cancel()
            if llama is not None:
                await llama.aclose()

    app = FastAPI(lifespan=lifespan, openapi_url=None, docs_url=None, redoc_url=None)

    @app.exception_handler(SessionError)
    async def session_error(_: Request, error: SessionError) -> JSONResponse:
        return JSONResponse({"error": error.code, "detail": error.detail, **error.extra}, status_code=error.status)

    # FastAPI's own answer would be a 422 echoing the input; 422 here means only audio that won't decode.
    @app.exception_handler(RequestValidationError)
    async def bad_request(_: Request, __: RequestValidationError) -> JSONResponse:
        return JSONResponse(
            {"error": "bad_request", "detail": "Send seq as a whole number from 0, and wait as seconds from 0."}, status_code=400
        )

    @app.middleware("http")
    async def require_key(request: Request, call_next):  # noqa: ANN202
        if request.url.path != "/health":
            presented = hashlib.sha256(request.headers.get("authorization", "").encode()).digest()
            if not hmac.compare_digest(presented, expected):
                return JSONResponse({"error": "unauthorized", "detail": "Send the dictation API key."}, status_code=401)
        return await call_next(request)

    def owner(request: Request) -> str:
        value = request.headers.get("x-dictation-owner", "")
        if not OWNER.fullmatch(value):
            raise SessionError(400, "no_owner", "Send X-Dictation-Owner.")
        return value

    def sessions(request: Request) -> Sessions:
        return request.app.state.sessions

    @app.get("/health")
    async def health() -> dict:
        return {"status": "ok"}

    @app.post("/v1/sessions", status_code=201)
    async def start(request: Request) -> dict:
        who = owner(request)
        session = sessions(request).start(who, await _spoken_language(request))
        return {"sessionId": session.id, "maxSeconds": sessions(request).max_seconds(session),
                "maxChunkBytes": settings.max_chunk_bytes}

    @app.post("/v1/sessions/{session_id}/audio")
    async def add_audio(request: Request, session_id: str, seq: int = Query(ge=0)) -> dict:
        who = owner(request)
        data = await _read(request, settings.max_chunk_bytes)
        return await sessions(request).add_audio(session_id, who, seq, data)

    @app.post("/v1/sessions/{session_id}/stop")
    async def stop(request: Request, session_id: str) -> dict:
        return await sessions(request).stop(session_id, owner(request))

    @app.post("/v1/sessions/{session_id}/result")
    async def result(request: Request, session_id: str, wait: float = Query(0.0, ge=0)) -> dict:
        return await sessions(request).result(session_id, owner(request), wait)

    @app.delete("/v1/sessions/{session_id}", status_code=204)
    async def cancel(request: Request, session_id: str) -> Response:
        sessions(request).cancel(session_id, owner(request))
        return Response(status_code=204)

    return app


async def _read(request: Request, limit: int) -> bytes:
    """The body, refused once it passes `limit` bytes rather than read whole."""
    declared = request.headers.get("content-length")
    if declared and declared.isdigit() and int(declared) > limit:
        raise SessionError(413, "too_large", "This chunk is too large.")
    body = bytearray()
    async for chunk in request.stream():
        body += chunk
        if len(body) > limit:
            raise SessionError(413, "too_large", "This chunk is too large.")
    return bytes(body)


async def _spoken_language(request: Request) -> str:
    """The language the dictation is in: English with no body or {}, otherwise exactly "en" or "es"."""
    try:
        body = await _read(request, 1024)
        data = json.loads(body) if body.strip() else {}
    except (SessionError, ValueError):
        data = None
    if not isinstance(data, dict) or set(data) - {"spokenLanguage"} or data.get("spokenLanguage", "en") not in LANGUAGES:
        raise SessionError(400, "bad_language", 'Send spokenLanguage as "en" or "es", or nothing for English.')
    return data.get("spokenLanguage", "en")


async def _sweep(sessions: Sessions) -> None:
    while True:
        await asyncio.sleep(30)
        sessions.sweep()


def _configure_logging() -> None:
    logger = logging.getLogger("ehr_dictation")
    logger.setLevel(logging.INFO)
    if not logger.handlers:
        handler = logging.StreamHandler()
        handler.setFormatter(logging.Formatter("%(asctime)s %(levelname)s %(name)s: %(message)s"))
        logger.addHandler(handler)
        logger.propagate = False
