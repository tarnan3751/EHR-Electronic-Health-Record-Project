"""The sidecar's HTTP API, which only the on-prem app calls (Ehr.Web, Api/Dictation). Run it with
`uvicorn --factory ehr_dictation.api:create_app`; the ai profile's image does.

Every route but /health needs `Authorization: Bearer <DICTATION_API_KEY>`: the inference network has no route out, but
being on it proves nothing. Session routes also need `X-Dictation-Owner`, the signed-in clinician's ID, which the on-prem
app sets from the session and never from the request; a session is invisible to any other owner.

    GET    /health                           {"status": "ok"}, nothing more; no key needed
    POST   /v1/sessions                      201 {sessionId, maxSeconds, maxChunkBytes}
    POST   /v1/sessions/{id}/audio?seq=N     body: the next chunk, any container; 200 {receivedSeq, transcript}
    POST   /v1/sessions/{id}/stop            the final transcript, and the fields if they are ready within 8 s
    POST   /v1/sessions/{id}/result?wait=S   the same, waiting up to S (at most 10) seconds for the fields
    DELETE /v1/sessions/{id}                 204; audio, transcript and fields are gone

A transcript is {text, final, segments: [{start, end, startMs, endMs, final, unclear}]}, with UTF-16 offsets into text
and unclear one of too_quiet, too_noisy, low_confidence or null. A result is {transcript, sorting, reason, fields,
provenance}: sorting is pending, done (fields set) or unavailable (reason model_unavailable, model_timeout or
model_output_invalid; the transcript stays as plain text). Each field is {status: "pending", value, sources: [{start,
end}]} or {status: "empty", reasons: [...]}: a value that failed a check is never sent, only why (validation.py).

Errors are {error, detail} with 400 (no owner, or no seq), 401 (no key), 404 (no such session for this owner), 409
(out_of_order with expectedSeq, stopped, not_stopped), 413 (too_large, too_long), 422 (not_audio) or 429 (busy).
Nothing a request carries is logged.
"""

from __future__ import annotations

import asyncio
import contextlib
import hashlib
import hmac
import logging
import re
from collections.abc import AsyncIterator

from fastapi import FastAPI, Query, Request, Response
from fastapi.exceptions import RequestValidationError
from fastapi.responses import JSONResponse

from . import prompt, provenance
from .asr import MedAsr, Recognizer
from .llama import LlamaServer, Structurer
from .sessions import SessionError, Sessions
from .settings import Settings

log = logging.getLogger("ehr_dictation")

OWNER = re.compile(r"[A-Za-z0-9-]{1,100}")


def create_app(
    settings: Settings | None = None,
    *,
    recognizer: Recognizer | None = None,
    structurer: Structurer | None = None,
    models: provenance.Models | None = None,
) -> FastAPI:
    """The app, from the environment unless told otherwise (the tests pass stand-ins for the models). Refuses to start
    without its keys, or with a MedASR that isn't the one models-manifest.json records."""
    settings = settings or Settings.from_environment()
    _configure_logging()
    expected = hashlib.sha256(f"Bearer {settings.api_key}".encode()).digest()

    @contextlib.asynccontextmanager
    async def lifespan(app: FastAPI) -> AsyncIterator[None]:
        loaded_models = models or provenance.load(settings.models_dir)
        llama = None
        sorter = structurer
        if sorter is None:
            llama = LlamaServer(settings.llama_url, settings.llama_api_key)
            sorter = Structurer(llama, prompt.load(), loaded_models.language_file, timeout=settings.structuring_seconds)
        asr = recognizer or MedAsr(settings.models_dir / provenance.MEDASR_DIR, settings.device)
        app.state.sessions = Sessions(settings, asr, sorter, loaded_models)
        sweeper = asyncio.create_task(_sweep(app.state.sessions))
        log.info("ready: MedASR on %s, llama-server at %s", settings.device, settings.llama_url)
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
        return JSONResponse({"error": "bad_request", "detail": "Send seq as a whole number from 0."}, status_code=400)

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
        session = sessions(request).start(owner(request))
        return {"sessionId": session.id, "maxSeconds": settings.max_session_seconds, "maxChunkBytes": settings.max_chunk_bytes}

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
