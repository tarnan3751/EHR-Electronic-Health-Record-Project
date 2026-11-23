"""The test set's dictation scripts (eval/scripts/*.toml), as the eval reads them: eval/dictation_scripts.py loads them
and renders their text, the one implementation the audio generator, its tests and the scoring all use. The display text
is the reference transcript and what gold sources quote; the spoken text is what the voices and the people recording
read. All of it is synthetic: no script is about a real person.

The recordings hash covers what makes the audio (each script's text, voice and noise, their renderer, the voices and
the audio generator), and the gold hash the rest of each script, both gold sets included, so results from a different
test set, or scored against different gold, are told apart (gate.py); only the gold may change without recording again
(rescore.py). The pipeline hash covers what makes a run's answers (the sidecar's code apart from
its checks, the eval's answers.py, and both lockfiles), and the scoring hash what checks and scores them (the sidecar's checks and the eval's
own code), so results from other answers, or scored differently, are told apart too; only the scoring may be redone
without the models (rescore.py).

The Spanish test set (eval/scripts-es, the expansion plan's step 17) has recordings and gold hashes of its own,
recordings_es_sha256() and gold_es_sha256(), over its scripts and the English script the Spanish set dictates as
Spanish (voices.toml's english_as_spanish). The pipeline and scoring hashes are the two sets' together, the Spanish
eval's code among them: a change to either set's code asks for both evals again (the decision log's step 17,
decision 22).
"""

from __future__ import annotations

import hashlib
import json
import tomllib
from pathlib import Path

from dictation_scripts import (  # noqa: F401 (re-exported for the eval)
    SCRIPTS_DIR,
    Gold,
    NotProposed,
    Proposal,
    Script,
    load_scripts,
)
from ehr_dictation.provenance import CHECKS_CODE_SHA256, PIPELINE_CODE_SHA256
from dictation_scripts import display_text as render_display  # noqa: F401
from dictation_scripts import spoken_text as render_spoken  # noqa: F401

EVAL_DIR = Path(__file__).resolve().parents[1]
DATASET_FILES = ("dictation_scripts.py", "voices.toml", "generate_audio.py")
# What makes a run's answers, the transcript and K2 Horizon's proposals, beside the sidecar's own code: the eval's code
# that feeds the recordings through it, and both lockfiles. A change to any means running the eval again.
PIPELINE_FILES = ("ehr_eval/answers.py", "ehr_eval/answers_es.py", "ehr_eval/run_a.py", "uv.lock",
                  "../src/Ehr.Dictation/sidecar/uv.lock")
# What checks and scores those answers, beside the sidecar's checks: a change means scoring the stored answers again
# (rescore.py), which the gate also asks for when the list of accepted limitations changes.
SCORING_FILES = ("ehr_eval/scripts.py", "ehr_eval/normalize.py", "ehr_eval/dates.py", "ehr_eval/scoring.py",
                 "ehr_eval/run.py", "ehr_eval/rescore.py", "ehr_eval/scoring_es.py", "ehr_eval/facts_es.py",
                 "ehr_eval/run_es.py",
                 "known-limitations.toml")
# What makes the Spanish set's recordings, beside its scripts: their renderers, the voices and the audio generator.
DATASET_FILES_ES = ("dictation_scripts_es.py", "dictation_scripts.py", "voices.toml", "generate_audio.py")


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


def variant_ids(eval_dir: Path = EVAL_DIR) -> set[str]:
    """The recordings the test set defines, by id, as generate_audio.py names them: each script's clean one, each of
    its noises at each SNR in voices.toml, and one more for each script voices.toml calls unusable."""
    variants = tomllib.loads((eval_dir / "voices.toml").read_text(encoding="utf-8")).get("variants", {})
    ids = set()
    for script in load_all(eval_dir / "scripts").values():
        ids.add(f"{script.id}--clean")
        ids |= {f"{script.id}--{noise}-{snr}db" for snr in variants.get("snr_db", []) for noise in script.noise}
        if script.id in variants.get("unusable", []):
            ids.add(f"{script.id}--{script.noise[0]}-{variants['unusable_snr_db']}db")
    return ids


def _spanish_set(eval_dir: Path) -> list[Path]:
    """The Spanish set's script files, and the English script it dictates as Spanish."""
    voices = tomllib.loads((eval_dir / "voices.toml").read_text(encoding="utf-8"))
    english = voices.get("variants_es", {}).get("english_as_spanish", "")
    return sorted((eval_dir / "scripts-es").glob("*.toml")) + sorted((eval_dir / "scripts").glob(f"{english}.toml"))


def recordings_es_sha256(eval_dir: Path = EVAL_DIR) -> str:
    """As recordings_sha256(), for the Spanish set: what its recordings say and who says it how."""
    digest = hashlib.sha256()
    for path in _spanish_set(eval_dir):
        data = tomllib.loads(path.read_text(encoding="utf-8"))
        digest.update(json.dumps({key: data.get(key) for key in SPOKEN}, sort_keys=True).encode() + b"\0")
    for name in DATASET_FILES_ES:
        path = eval_dir / name
        content = path.read_bytes().replace(b"\r\n", b"\n") if path.is_file() else b""
        digest.update(name.encode() + b"\0" + content + b"\0")
    return digest.hexdigest()


def gold_es_sha256(eval_dir: Path = EVAL_DIR) -> str:
    """As gold_sha256(), for the Spanish set: the rest of each script, what it is scored against."""
    digest = hashlib.sha256()
    for path in _spanish_set(eval_dir):
        data = tomllib.loads(path.read_text(encoding="utf-8"))
        rest = {key: value for key, value in data.items() if key not in SPOKEN}
        digest.update(path.name.encode() + b"\0" + json.dumps(rest, sort_keys=True).encode() + b"\0")
    return digest.hexdigest()


def gold_sha256(eval_dir: Path = EVAL_DIR) -> str:
    """One hash over the rest of every script, what it is scored against: both gold sets, tags, dates, drugs, forbidden,
    retracted, and what should and shouldn't be proposed. A change means scoring the stored answers again
    (rescore.py)."""
    digest = hashlib.sha256()
    for path in sorted((eval_dir / "scripts").glob("*.toml")):
        data = tomllib.loads(path.read_text(encoding="utf-8"))
        rest = {key: value for key, value in data.items() if key not in SPOKEN}
        digest.update(path.name.encode() + b"\0" + json.dumps(rest, sort_keys=True).encode() + b"\0")
    return digest.hexdigest()


def pipeline_sha256(eval_dir: Path = EVAL_DIR) -> str:
    """One hash over what makes a run's answers: the sidecar's code apart from its checks, answers.py, and both
    lockfiles."""
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
