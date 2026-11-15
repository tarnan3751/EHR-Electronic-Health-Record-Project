"""The test set's dictation scripts (eval/scripts/*.toml), as the eval reads them: eval/dictation_scripts.py loads them
and renders their text, the one implementation the audio generator, its tests and the scoring all use. The display text
is the reference transcript and what gold sources quote; the spoken text is what the voices and the people recording
read. All of it is synthetic: no script is about a real person.

The dataset hash covers the scripts, their renderer, the voices and the audio generator, so results from a different
test set are told apart (gate.py). The measurement hash covers the eval's own code that produces and scores a run, and
both lockfiles, so results measured or scored differently are told apart too.
"""

from __future__ import annotations

import hashlib
from pathlib import Path

from dictation_scripts import SCRIPTS_DIR, Gold, Script, load_scripts  # noqa: F401 (re-exported for the eval)
from dictation_scripts import display_text as render_display  # noqa: F401
from dictation_scripts import spoken_text as render_spoken  # noqa: F401

EVAL_DIR = Path(__file__).resolve().parents[1]
DATASET_FILES = ("dictation_scripts.py", "voices.toml", "generate_audio.py")
MEASUREMENT_FILES = ("ehr_eval/scripts.py", "ehr_eval/normalize.py", "ehr_eval/dates.py", "ehr_eval/scoring.py",
                     "ehr_eval/run.py", "ehr_eval/rescore.py", "uv.lock", "../src/Ehr.Dictation/sidecar/uv.lock")


def load_all(directory: Path = SCRIPTS_DIR) -> dict[str, Script]:
    return {script.id: script for script in load_scripts(directory)}


def dataset_sha256(eval_dir: Path = EVAL_DIR) -> str:
    """One hash over every script and the files that turn them into audio, in name order."""
    digest = hashlib.sha256()
    paths = sorted((eval_dir / "scripts").glob("*.toml")) + [eval_dir / name for name in DATASET_FILES]
    for path in paths:
        if path.is_file():
            digest.update(path.relative_to(eval_dir).as_posix().encode() + b"\0")
            digest.update(path.read_bytes().replace(b"\r\n", b"\n") + b"\0")
    return digest.hexdigest()


def measurement_sha256(eval_dir: Path = EVAL_DIR) -> str:
    """One hash over the code that runs and scores the eval, and the packages both sides lock."""
    digest = hashlib.sha256()
    for name in MEASUREMENT_FILES:
        path = eval_dir / name
        digest.update(name.encode() + b"\0")
        digest.update((path.read_bytes().replace(b"\r\n", b"\n") if path.is_file() else b"missing") + b"\0")
    return digest.hexdigest()
