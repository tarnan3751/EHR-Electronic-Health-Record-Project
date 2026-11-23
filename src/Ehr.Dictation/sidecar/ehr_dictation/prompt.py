"""What K2 Horizon is asked: the system prompt, the transcript as the user message, and the note schema as the response
format llama-server enforces (FR-11). Human-reviewed, with the files it reads (AGENTS.md).

The prompt and the schema live in src/Ehr.Dictation/schema, beside this package in the repository and in the image.
The prompt file's opening <!-- comment --> and the schema's $schema and $comment are for people and are not sent. A
recorded visit's conversation (the expansion plan's step 16) is drafted with its own prompt, conversation-prompt.md,
the same schema, and the same parameters but a shorter answer (CONVERSATION_PARAMETERS: a window's answer, at about 20
tokens a second, ends within about 55 seconds), loaded with load_conversation(), so it has a structuring hash of its
own and dictation's doesn't change.

The transcript is untrusted input: it goes between <transcript> markers, any marker inside it is neutralized, and the
prompt says to treat all of it as dictation. Passages marked unclear arrive as [unclear] (transcript.py).

Generation is fixed: temperature 0 and a fixed seed, a cap on the answer, and K2 Horizon's reasoning off
(reasoning_effort "none", which llama-server turns into the chat template's enable_thinking false: an empty think
block). Its template's other settings, high, medium and low, all reason at length on this task: at "low", over 1,000
tokens for a two-minute dictation, measured, where off answers in about 30 seconds on a GPU. No tools are ever offered.
The structuring hash covers everything sent except the transcript, so the eval can tell when a result came from a
different prompt, schema or set of parameters (eval/README.md), such as its comparison run with reasoning on.
"""

from __future__ import annotations

import hashlib
import json
import re
from dataclasses import dataclass
from pathlib import Path

FIELDS = ("subjective", "objective", "assessment", "plan")

SCHEMA_DIRECTORY = Path(__file__).resolve().parents[2] / "schema"
PROMPT_FILE = "structuring-prompt.md"
CONVERSATION_PROMPT_FILE = "conversation-prompt.md"
SCHEMA_FILE = "note-fields.schema.json"

PARAMETERS = {
    "temperature": 0,
    "seed": 0,
    "max_tokens": 2048,
    "reasoning_effort": "none",
}
CONVERSATION_PARAMETERS = PARAMETERS | {"max_tokens": 1100}
USER_TEMPLATE = "<transcript>\n{transcript}\n</transcript>"

HEADER = re.compile(r"\A\s*<!--.*?-->\s*", re.DOTALL)
MARKER = re.compile(r"<\s*/?\s*transcript\s*>", re.IGNORECASE)


@dataclass(frozen=True)
class Structuring:
    system: str
    schema: dict
    parameters: dict
    prompt_sha256: str
    schema_sha256: str
    structuring_sha256: str

    def user_message(self, transcript: str) -> str:
        return USER_TEMPLATE.format(transcript=MARKER.sub("[removed]", transcript))

    def request(self, transcript: str) -> dict:
        return {
            "messages": [
                {"role": "system", "content": self.system},
                {"role": "user", "content": self.user_message(transcript)},
            ],
            **self.parameters,
            "response_format": {"type": "json_schema", "json_schema": {"name": "note_fields", "schema": self.schema}},
            "stream": False,
            "cache_prompt": True,
        }


def load(directory: Path = SCHEMA_DIRECTORY, parameters: dict | None = None, prompt_file: str = PROMPT_FILE) -> Structuring:
    """The prompt and schema as sent, with PARAMETERS unless the eval asks for others to compare."""
    parameters = dict(PARAMETERS if parameters is None else parameters)
    system = HEADER.sub("", (directory / prompt_file).read_text(encoding="utf-8")).strip()
    schema = json.loads((directory / SCHEMA_FILE).read_text(encoding="utf-8"))
    for key in ("$schema", "$comment"):
        schema.pop(key, None)
    return Structuring(
        system=system,
        schema=schema,
        parameters=parameters,
        prompt_sha256=_sha256(system),
        schema_sha256=_sha256(_canonical(schema)),
        structuring_sha256=_sha256(
            _canonical({"system": system, "schema": schema, "parameters": parameters, "user": USER_TEMPLATE})
        ),
    )


def load_conversation(directory: Path = SCHEMA_DIRECTORY, parameters: dict | None = None) -> Structuring:
    """A recorded visit's: conversation-prompt.md, with CONVERSATION_PARAMETERS unless the eval asks for others."""
    return load(directory, CONVERSATION_PARAMETERS if parameters is None else parameters, CONVERSATION_PROMPT_FILE)


def _canonical(value: object) -> str:
    return json.dumps(value, sort_keys=True, separators=(",", ":"), ensure_ascii=False)


def _sha256(text: str) -> str:
    return hashlib.sha256(text.encode("utf-8")).hexdigest()
