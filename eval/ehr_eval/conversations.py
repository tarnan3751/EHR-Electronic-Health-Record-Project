"""The eval's synthetic visits (eval/conversations/*.toml), as the eval reads them (conversation_scripts.py loads and
renders them), and the hashes that tie a visits' results to what they were made and scored from, as scripts.py's do
for the dictations:

- the recordings hash covers what makes the visits' audio: each visit's id, voices, noise and turns, the renderers,
  conversation-voices.toml, and the generators with voices.toml, whose model pin and babble texts they use;
- the gold hash the rest of each visit, both gold sets and every list the scoring reads;
- the pipeline hash what makes a run's answers: the sidecar's code apart from its checks (the same part as dictation's),
  the eval's listening.py, and both lockfiles;
- the scoring hash what checks and scores them: the sidecar's checks and the eval's own code for visits.

heard_conversation() is a visit as the sidecar would hear it with every voice told apart and every word heard right:
its turns a passage each, without the punctuation nobody says aloud, each named for who said it. The gold is checked
against it (tests/test_conversations.py).
"""

from __future__ import annotations

import hashlib
import json
import tomllib
from pathlib import Path

from conversation_scripts import CONVERSATIONS_DIR, ROLES, Conversation, load_conversations  # noqa: F401 (re-exported)
from ehr_dictation import conversation
from ehr_dictation.provenance import CHECKS_CODE_SHA256, PIPELINE_CODE_SHA256
from ehr_dictation.transcript import Segment

from .scripts import EVAL_DIR, _files_sha256

CONVERSATIONS = load_conversations()
# What makes the audio, beside each visit's own spoken parts.
DATASET_FILES = ("conversation_scripts.py", "dictation_scripts.py", "conversation-voices.toml", "voices.toml",
                 "generate_conversations.py", "generate_audio.py")
PIPELINE_FILES = ("ehr_eval/listening.py", "uv.lock", "../src/Ehr.Dictation/sidecar/uv.lock")
SCORING_FILES = ("conversation_scripts.py", "ehr_eval/conversations.py", "ehr_eval/normalize.py", "ehr_eval/dates.py",
                 "ehr_eval/scoring.py", "ehr_eval/ambient_scoring.py", "ehr_eval/ambient.py", "known-limitations.toml")
SPOKEN = ("id", "speakers", "voices", "noise", "turns")
VOICE_NUMBERS = {role: number for number, role in enumerate(ROLES, 1)}


def recordings_sha256(eval_dir: Path = EVAL_DIR) -> str:
    digest = hashlib.sha256()
    for path in sorted((eval_dir / "conversations").glob("c*.toml")):
        data = tomllib.loads(path.read_text(encoding="utf-8"))
        digest.update(json.dumps({key: data.get(key) for key in SPOKEN}, sort_keys=True).encode() + b"\0")
    for name in DATASET_FILES:
        path = eval_dir / name
        digest.update(name.encode() + b"\0" + (path.read_bytes().replace(b"\r\n", b"\n") if path.is_file() else b"") + b"\0")
    return digest.hexdigest()


def gold_sha256(eval_dir: Path = EVAL_DIR) -> str:
    digest = hashlib.sha256()
    for path in sorted((eval_dir / "conversations").glob("c*.toml")):
        data = tomllib.loads(path.read_text(encoding="utf-8"))
        rest = {key: value for key, value in data.items() if key not in SPOKEN}
        digest.update(path.name.encode() + b"\0" + json.dumps(rest, sort_keys=True).encode() + b"\0")
    return digest.hexdigest()


def variant_ids(eval_dir: Path = EVAL_DIR) -> set[str]:
    """The recordings the visits define, by id, as generate_conversations.py names them: each visit's clean one, and its
    room and babble ones where it lists them."""
    return {
        f"{visit.id}--{noise or 'clean'}"
        for visit in load_conversations(eval_dir / "conversations")
        for noise in (None, *(n for n in ("room", "babble") if n in visit.noise))
    }


def pipeline_sha256(eval_dir: Path = EVAL_DIR) -> str:
    return _files_sha256(eval_dir, PIPELINE_FILES, PIPELINE_CODE_SHA256)


def scoring_sha256(eval_dir: Path = EVAL_DIR) -> str:
    return _files_sha256(eval_dir, SCORING_FILES, CHECKS_CODE_SHA256)


def heard_conversation(visit: Conversation) -> conversation.Conversation:
    """The visit as the sidecar would transcribe it with every voice told and every word heard (see the header)."""
    passages = [
        (Segment(i * 16000, (i + 1) * 16000, turn.heard, 1.0, None, True), VOICE_NUMBERS[turn.role], False)
        for i, turn in enumerate(visit.turns)
    ]
    return conversation.assemble(passages).named({VOICE_NUMBERS[role]: role for role in visit.voices})
