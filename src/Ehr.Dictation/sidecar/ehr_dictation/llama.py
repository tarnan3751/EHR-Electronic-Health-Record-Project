"""Asks K2 Horizon, in llama-server, for the note fields (FR-11), and reads its answer.

Every request carries llama-server's API key (being on the inference network proves nothing). Before each request
the sidecar checks that llama-server serves the model file models-manifest.json records, so the provenance on each
result is true. The answer is the message content, which the response format makes bare JSON; the
model's reasoning comes back separately and is dropped unread. Nothing in a request or an answer is logged. The model
is checked before every request, not just the first, so a llama-server restarted with another file is noticed.

A request that wouldn't fit the context llama-server gives each request (its n_ctx per slot, from /props), the prompt
counted by llama-server's own tokenizer plus the most the answer may take, is never sent: it would only be cut off. Nor
is any request while llama-server doesn't say what that context is.

When the model can't answer, the session keeps its transcript and says the fields couldn't be sorted (UC 10): the
reason is model_unavailable (no connection, an error, a refused key, the wrong model, no context size), model_timeout,
model_output_invalid (an answer that isn't the schema's JSON, or one cut off at the token limit), or
transcript_too_long (it wouldn't fit the context).
"""

from __future__ import annotations

import asyncio
import json
import posixpath
from dataclasses import dataclass, field

import httpx2

from .prompt import FIELDS, Structuring


class StructuringFailed(Exception):
    reason = "model_unavailable"


class ModelUnavailable(StructuringFailed):
    reason = "model_unavailable"


class ModelTimeout(StructuringFailed):
    reason = "model_timeout"


class ModelOutputInvalid(StructuringFailed):
    reason = "model_output_invalid"


class TranscriptTooLong(StructuringFailed):
    reason = "transcript_too_long"


# What the chat template wraps around the messages, beyond their own tokens.
TEMPLATE_TOKENS = 64


@dataclass
class Proposal:
    """What the model proposed: each field's value and quoted sources, and any keys outside the schema."""

    fields: dict[str, dict]
    unknown: list[str] = field(default_factory=list)
    timings: dict | None = None


class LlamaServer:
    def __init__(self, url: str, api_key: str, *, transport: httpx2.AsyncBaseTransport | None = None) -> None:
        self._client = httpx2.AsyncClient(
            base_url=url,
            headers={"Authorization": f"Bearer {api_key}"},
            timeout=httpx2.Timeout(None, connect=5.0),
            transport=transport,
        )

    async def props(self) -> dict:
        response = await self._client.get("/props")
        if response.status_code != 200:
            raise ModelUnavailable(f"llama-server answered /props with {response.status_code}.")
        return response.json()

    async def tokens(self, text: str) -> int:
        response = await self._client.post("/tokenize", json={"content": text})
        if response.status_code != 200:
            raise ModelUnavailable(f"llama-server answered /tokenize with {response.status_code}.")
        return len(response.json().get("tokens", []))

    async def chat(self, body: dict) -> dict:
        response = await self._client.post("/v1/chat/completions", json=body)
        if response.status_code != 200:
            raise ModelUnavailable(f"llama-server answered with {response.status_code}.")
        return response.json()

    async def aclose(self) -> None:
        await self._client.aclose()


class Structurer:
    def __init__(self, llama: LlamaServer, structuring: Structuring, model_file: str | None, *, timeout: float) -> None:
        self._llama = llama
        self._structuring = structuring
        self._model_file = model_file
        self._timeout = timeout
        self.build: str | None = None
        self.context: int | None = None

    @property
    def structuring(self) -> Structuring:
        return self._structuring

    def using(self, structuring: Structuring) -> Structurer:
        """The same llama-server, model file and time limit with another prompt: a conversation's (step 16)."""
        return Structurer(self._llama, structuring, self._model_file, timeout=self._timeout)

    async def propose(self, transcript: str) -> Proposal:
        try:
            async with asyncio.timeout(self._timeout):
                await self._check_model()
                request = self._structuring.request(transcript)
                await self._check_fits(request)
                answer = await self._llama.chat(request)
        except TimeoutError:
            raise ModelTimeout(f"No answer within {self._timeout:.0f} s.") from None
        except httpx2.TimeoutException:
            raise ModelTimeout("llama-server timed out.") from None
        except httpx2.TransportError as error:
            raise ModelUnavailable(f"llama-server can't be reached ({type(error).__name__}).") from None

        try:
            choice = answer["choices"][0]
            content = choice["message"]["content"]
        except (KeyError, IndexError, TypeError):
            raise ModelOutputInvalid("The answer has no message.") from None
        if choice.get("finish_reason") == "length":
            raise ModelOutputInvalid("The answer was cut off at the token limit.")
        fields, unknown = parse_answer(content)
        return Proposal(fields, unknown, answer.get("timings"))

    async def _check_model(self) -> None:
        if not self._model_file:
            raise ModelUnavailable("models-manifest.json records no K2 Horizon file.")
        props = await self._llama.props()
        served = posixpath.basename(str(props.get("model_path", "")).replace("\\", "/"))
        if served != self._model_file:
            self.build = None
            raise ModelUnavailable("llama-server serves a different model file than models-manifest.json records.")
        self.build = str(props.get("build_info") or "unknown")
        context = props.get("default_generation_settings", {}).get("n_ctx")
        self.context = int(context) if isinstance(context, int) and context > 0 else None

    async def _check_fits(self, request: dict) -> None:
        if self.context is None:
            raise ModelUnavailable("llama-server didn't say how much context a request has.")
        prompt = sum([await self._llama.tokens(message["content"]) for message in request["messages"]])
        if prompt + TEMPLATE_TOKENS + int(request.get("max_tokens", 0)) > self.context:
            raise TranscriptTooLong(f"{prompt} prompt tokens don't leave room for the answer in {self.context}.")


def parse_answer(content: object) -> tuple[dict[str, dict], list[str]]:
    """The four fields, checked against the schema's shape, and the names of any other keys."""
    try:
        data = json.loads(content) if isinstance(content, str) else None
    except json.JSONDecodeError:
        data = None
    if not isinstance(data, dict):
        raise ModelOutputInvalid("The answer isn't a JSON object.")

    fields: dict[str, dict] = {}
    for name in FIELDS:
        proposed = data.get(name)
        if (
            not isinstance(proposed, dict)
            or not isinstance(proposed.get("value"), str)
            or not isinstance(proposed.get("sources"), list)
            or not all(isinstance(source, str) for source in proposed["sources"])
        ):
            raise ModelOutputInvalid("The answer doesn't match the note schema.")
        fields[name] = {"value": proposed["value"], "sources": list(proposed["sources"])}
    return fields, [key for key in data if key not in FIELDS]
