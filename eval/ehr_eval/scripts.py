"""The test set's dictation scripts (eval/scripts/*.toml): what is said, how it is tagged, and the note fields a perfect
run would propose. All of it is synthetic: no script is about a real person.

A script's text is written as it is dictated. Spoken punctuation is in braces, as MedASR writes it ({period},
{comma}, {new paragraph}), and [[display|spoken]] gives a stretch two forms: how it should appear in the transcript
("142/88") and how the speaker says it ("one forty-two over eighty-eight"). The display text, formatted by the
sidecar's own formatter, is the reference transcript, and the gold sources quote it; the spoken text is what the
text-to-speech voices and the people recording read (generate_audio.py, eval/README.md).

The dataset hash covers the scripts, the voices and the audio generator, so results from a different test set are
told apart (gate.py). The measurement hash covers the eval's own code that produces and scores a run, and both
lockfiles, so results measured or scored differently are told apart too.
"""

from __future__ import annotations

import hashlib
import re
import tomllib
from dataclasses import dataclass, field
from pathlib import Path

from ehr_dictation.prompt import FIELDS
from ehr_dictation.transcript import format as format_transcript

EVAL_DIR = Path(__file__).resolve().parents[1]
SCRIPTS_DIR = EVAL_DIR / "scripts"
DATASET_FILES = ("voices.toml", "generate_audio.py")
MEASUREMENT_FILES = ("ehr_eval/scripts.py", "ehr_eval/normalize.py", "ehr_eval/dates.py", "ehr_eval/scoring.py",
                     "ehr_eval/run.py", "uv.lock", "../src/Ehr.Dictation/sidecar/uv.lock")

ALTERNATIVE = re.compile(r"\[\[([^\[\]|]*)\|([^\[\]]*)\]\]")
COMMAND = re.compile(r"\{([^{}]*)\}")


@dataclass(frozen=True)
class Gold:
    value: str
    sources: list[str]


@dataclass(frozen=True)
class Script:
    id: str
    voice: str
    tags: list[str]
    text: str
    gold: dict[str, Gold]
    noise: list[str] = field(default_factory=list)
    dates: list[str] = field(default_factory=list)
    drugs: list[str] = field(default_factory=list)
    forbidden: list[str] = field(default_factory=list)

    @property
    def display(self) -> str:
        return render_display(self.text)

    @property
    def spoken(self) -> str:
        return render_spoken(self.text)


def render_display(text: str) -> str:
    return format_transcript(ALTERNATIVE.sub(lambda m: m.group(1), text))


def render_spoken(text: str) -> str:
    spoken = ALTERNATIVE.sub(lambda m: m.group(2), text)
    spoken = COMMAND.sub(lambda m: f" {m.group(1).strip()} ", spoken)
    return " ".join(spoken.split())


def load(path: Path) -> Script:
    data = tomllib.loads(path.read_text(encoding="utf-8"))
    gold = data.get("gold", {})
    return Script(
        id=data["id"],
        voice=data["voice"],
        tags=list(data.get("tags", [])),
        text=data["text"],
        gold={name: Gold(gold.get(name, {}).get("value", ""), list(gold.get(name, {}).get("sources", []))) for name in FIELDS},
        noise=list(data.get("noise", [])),
        dates=list(data.get("dates", [])),
        drugs=list(data.get("drugs", [])),
        forbidden=list(data.get("forbidden", [])),
    )


def load_all(directory: Path = SCRIPTS_DIR) -> dict[str, Script]:
    scripts = [load(path) for path in sorted(directory.glob("*.toml"))]
    return {script.id: script for script in scripts}


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
