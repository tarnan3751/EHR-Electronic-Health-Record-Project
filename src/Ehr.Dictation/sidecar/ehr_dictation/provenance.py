"""Which models produced a result: the repository, commit and SHA-256 of MedASR's weights and of the K2 Horizon file,
from the models-manifest.json setup_models.py wrote, for the provenance each dictated note will store (NFR-15).

At startup MedASR's weights are hashed and compared with the manifest, and the sidecar refuses to start if they differ,
so a result never claims a model it didn't use. The speaker model that tells a visit's voices apart (the expansion
plan's step 16) is optional: with no entry in the manifest, speaker is None and listening to a visit is refused; with
one, its weights are checked the same way (and again as ecapa.py loads them). The K2 Horizon file is llama-server's to load: llama.py checks that it
serves the file the manifest names. The manifest's hashes are the ones Hugging Face publishes.

CODE_SHA256 is a hash over this package's source: every result names the code that transcribed and checked it, and
the eval's results are tied to it, so any change to what is measured asks for a new eval run (eval/ehr_eval/gate.py).
"""

from __future__ import annotations

import hashlib
import json
from collections.abc import Collection
from dataclasses import dataclass
from pathlib import Path

MANIFEST = "models-manifest.json"
PACKAGE = Path(__file__).resolve().parent
MEDASR_DIR = "medasr"
MEDASR_WEIGHTS = "model.safetensors"
SPEAKER_DIR = "speaker"
SPEAKER_WEIGHTS = "embedding_model.ckpt"


class ProvenanceError(Exception):
    """The models on disk aren't the ones the manifest records, or there's no manifest."""


@dataclass(frozen=True)
class Models:
    speech: dict
    language: dict | None
    speaker: dict | None = None

    @property
    def language_file(self) -> str | None:
        return self.language["file"] if self.language else None


def load(models_dir: Path, *, verify: bool = True) -> Models:
    path = models_dir / MANIFEST
    try:
        manifest = json.loads(path.read_text(encoding="utf-8"))
    except FileNotFoundError:
        raise ProvenanceError(f"No {MANIFEST} in {models_dir}: run src/Ehr.Dictation/setup_models.py.") from None
    except json.JSONDecodeError:
        raise ProvenanceError(f"{path} isn't valid JSON: run src/Ehr.Dictation/setup_models.py again.") from None

    medasr = manifest.get("medasr")
    weights = next((f for f in (medasr or {}).get("files", []) if f.get("path") == MEDASR_WEIGHTS), None)
    if not medasr or not weights:
        raise ProvenanceError(f"{MANIFEST} records no MedASR weights: run src/Ehr.Dictation/setup_models.py.")
    if verify and _sha256(models_dir / MEDASR_DIR / MEDASR_WEIGHTS) != weights["sha256"]:
        raise ProvenanceError(
            f"MedASR's {MEDASR_WEIGHTS} doesn't match {MANIFEST}: run src/Ehr.Dictation/setup_models.py --verify."
        )
    speech = {"repository": medasr["repository"], "commit": medasr["commit"], "sha256": weights["sha256"]}

    llm = manifest.get("llm")
    language = (
        {
            "repository": llm["repository"],
            "commit": llm["commit"],
            "file": llm["file"],
            "sha256": next((f["sha256"] for f in llm["files"] if f["path"] == llm["file"]), None),
        }
        if llm
        else None
    )
    return Models(speech, language, _speaker(manifest.get("speaker"), models_dir, verify))


def _speaker(entry: dict | None, models_dir: Path, verify: bool) -> dict | None:
    if not entry:
        return None
    weights = next((f for f in entry.get("files", []) if f.get("path") == SPEAKER_WEIGHTS), None)
    if not weights:
        raise ProvenanceError(f"{MANIFEST} records no speaker-model weights: run src/Ehr.Dictation/setup_models.py.")
    if verify and _sha256(models_dir / SPEAKER_DIR / SPEAKER_WEIGHTS) != weights["sha256"]:
        raise ProvenanceError(
            f"The speaker model's {SPEAKER_WEIGHTS} doesn't match {MANIFEST}: run src/Ehr.Dictation/setup_models.py --verify."
        )
    return {"repository": entry["repository"], "commit": entry["commit"], "sha256": weights["sha256"]}


def _sha256(path: Path) -> str | None:
    if not path.is_file():
        return None
    digest = hashlib.sha256()
    with path.open("rb") as file:
        while chunk := file.read(1 << 20):
            digest.update(chunk)
    return digest.hexdigest()


def code_sha256(package: Path = PACKAGE, names: Collection[str] | None = None) -> str:
    """A hash over the package's Python files (or those named), by name and content (line endings normalized)."""
    digest = hashlib.sha256()
    for path in sorted(package.glob("*.py")):
        if names is None or path.name in names:
            digest.update(path.name.encode() + b"\0" + path.read_bytes().replace(b"\r\n", b"\n") + b"\0")
    return digest.hexdigest()


CODE_SHA256 = code_sha256()

# For the eval: the files that check a proposal, which re-scoring stored answers may change (eval/ehr_eval/rescore.py),
# and those that make the transcript and the model's answer, which it may not. Neither are the HTTP layer (api.py),
# which the eval doesn't go through, or this file, since results record the models' own hashes. catalog.py is the
# reference data's names the checks read (generate_catalog.py writes it).
CHECKS = ("validation.py", "textnorm.py", "catalog.py")
CHECKS_CODE_SHA256 = code_sha256(names=CHECKS)
PIPELINE_CODE_SHA256 = code_sha256(
    names=[path.name for path in PACKAGE.glob("*.py") if path.name not in {*CHECKS, "api.py", "provenance.py"}]
)
