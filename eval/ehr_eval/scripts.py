"""The test set's dictation scripts (eval/scripts/*.toml), as the eval reads them: eval/dictation_scripts.py loads them
and renders their text, the one implementation the audio generator, its tests and the scoring all use. The display text
is the reference transcript and what gold sources quote; the spoken text is what the voices and the people recording
read. All of it is synthetic: no script is about a real person.

The recordings hash covers what makes the audio (each script's text, voice and noise, their renderer, the voices and
the audio generator), and the gold hash the rest of each script, both gold sets included, so results from a different
test set, or scored against different gold, are told apart (gate.py); only the gold may change without recording again
(rescore.py). The pipeline hash covers what makes a run's answers (the sidecar's code apart from
its checks, and both lockfiles), and the scoring hash what checks and scores them (the sidecar's checks and the eval's
own code), so results from other answers, or scored differently, are told apart too; only the scoring may be redone
without the models (rescore.py).
"""

from __future__ import annotations

import hashlib
import json
import tomllib
from pathlib import Path

from dictation_scripts import SCRIPTS_DIR, Gold, Script, load_scripts  # noqa: F401 (re-exported for the eval)
from ehr_dictation.provenance import CHECKS_CODE_SHA256, PIPELINE_CODE_SHA256
from dictation_scripts import display_text as render_display  # noqa: F401
from dictation_scripts import spoken_text as render_spoken  # noqa: F401

EVAL_DIR = Path(__file__).resolve().parents[1]
DATASET_FILES = ("dictation_scripts.py", "voices.toml", "generate_audio.py")
# What makes a run's answers, the transcript and K2 Horizon's proposals, beside the sidecar's own code: both lockfiles.
# A change to either means running the eval again.
PIPELINE_FILES = ("uv.lock", "../src/Ehr.Dictation/sidecar/uv.lock")
# What checks and scores those answers, beside the sidecar's checks: a change means scoring the stored answers again
# (rescore.py), which the gate also asks for when the list of accepted limitations changes.
SCORING_FILES = ("ehr_eval/scripts.py", "ehr_eval/normalize.py", "ehr_eval/dates.py", "ehr_eval/scoring.py",
                 "ehr_eval/run.py", "ehr_eval/rescore.py", "known-limitations.toml")


def load_all(directory: Path = SCRIPTS_DIR) -> dict[str, Script]:
    return {script.id: script for script in load_scripts(directory)}


# What makes a script's recordings: what it says, and who says it how.
SPOKEN = ("id", "voice", "noise", "text")


def recordings_sha256(eval_dir: Path = EVAL_DIR) -> str:
    """One hash over what makes the recordings: each script's id, voice, noise and text, in name order, and the files
    that turn them into audio. A change means generating the audio and running the eval again."""
    digest = hashlib.sha256()
    for path in sorted((eval_dir / "scripts").glob("*.toml")):
        data = tomllib.loads(path.read_text(encoding="utf-8"))
        digest.update(json.dumps({key: data.get(key) for key in SPOKEN}, sort_keys=True).encode() + b"\0")
    for name in DATASET_FILES:
        path = eval_dir / name
        content = path.read_bytes().replace(b"\r\n", b"\n") if path.is_file() else b""
        digest.update(name.encode() + b"\0" + content + b"\0")
    return digest.hexdigest()


def gold_sha256(eval_dir: Path = EVAL_DIR) -> str:
    """One hash over the rest of every script, what it is scored against: both gold sets, tags, dates, drugs, forbidden
    and retracted. A change means scoring the stored answers again (rescore.py)."""
    digest = hashlib.sha256()
    for path in sorted((eval_dir / "scripts").glob("*.toml")):
        data = tomllib.loads(path.read_text(encoding="utf-8"))
        rest = {key: value for key, value in data.items() if key not in SPOKEN}
        digest.update(path.name.encode() + b"\0" + json.dumps(rest, sort_keys=True).encode() + b"\0")
    return digest.hexdigest()


def pipeline_sha256(eval_dir: Path = EVAL_DIR) -> str:
    """One hash over what makes a run's answers: the sidecar's code apart from its checks, and both lockfiles."""
    return _files_sha256(eval_dir, PIPELINE_FILES, PIPELINE_CODE_SHA256)


def scoring_sha256(eval_dir: Path = EVAL_DIR) -> str:
    """One hash over what checks and scores the answers: the sidecar's checks and the eval's own scoring code."""
    return _files_sha256(eval_dir, SCORING_FILES, CHECKS_CODE_SHA256)


def _files_sha256(eval_dir: Path, names: tuple[str, ...], code: str) -> str:
    digest = hashlib.sha256()
    digest.update(code.encode() + b"\0")
    for name in names:
        path = eval_dir / name
        digest.update(name.encode() + b"\0")
        digest.update((path.read_bytes().replace(b"\r\n", b"\n") if path.is_file() else b"missing") + b"\0")
    return digest.hexdigest()
