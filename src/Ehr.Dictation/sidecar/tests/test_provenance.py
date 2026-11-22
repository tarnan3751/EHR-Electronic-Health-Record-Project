"""provenance.py: which models produced a result, from the manifest setup_models.py writes, with MedASR's weights
checked against it before the sidecar starts."""

from __future__ import annotations

import hashlib
import json

import pytest

from ehr_dictation import provenance


def install(tmp_path, weights: bytes = b"medasr weights", recorded: bytes | None = None) -> None:
    (tmp_path / "medasr").mkdir()
    (tmp_path / "medasr" / "model.safetensors").write_bytes(weights)
    manifest = {
        "llm": {"repository": "IFM/K2-Horizon-7B-GGUF", "commit": "c" * 40, "file": "K2.gguf",
                "files": [{"path": "K2.gguf", "size": 1, "sha256": "d" * 64}]},
        "medasr": {"repository": "google/medasr", "commit": "a" * 40, "directory": "medasr",
                   "files": [{"path": "model.safetensors", "size": 1,
                              "sha256": hashlib.sha256(recorded if recorded is not None else weights).hexdigest()}]},
    }
    (tmp_path / "models-manifest.json").write_text(json.dumps(manifest))


def test_reads_each_models_repository_commit_and_hash(tmp_path):
    install(tmp_path)

    models = provenance.load(tmp_path)

    assert models.speech == {"repository": "google/medasr", "commit": "a" * 40,
                             "sha256": hashlib.sha256(b"medasr weights").hexdigest()}
    assert models.language == {"repository": "IFM/K2-Horizon-7B-GGUF", "commit": "c" * 40, "file": "K2.gguf",
                               "sha256": "d" * 64}


def test_refuses_medasr_weights_the_manifest_doesnt_record(tmp_path):
    install(tmp_path, weights=b"something else", recorded=b"medasr weights")

    with pytest.raises(provenance.ProvenanceError, match="--verify"):
        provenance.load(tmp_path)


def test_refuses_a_missing_manifest(tmp_path):
    with pytest.raises(provenance.ProvenanceError, match="setup_models.py"):
        provenance.load(tmp_path)


# ---- Whisper, for dictation in Spanish (the expansion plan's step 17) ------------------------------------------------


def install_whisper(tmp_path, weights: bytes = b"whisper weights", recorded: bytes | None = b"whisper weights") -> None:
    (tmp_path / "whisper").mkdir()
    (tmp_path / "whisper" / "model.safetensors").write_bytes(weights)
    manifest = json.loads((tmp_path / "models-manifest.json").read_text())
    files = [] if recorded is None else [{"path": "model.safetensors", "size": 1, "sha256": hashlib.sha256(recorded).hexdigest()}]
    manifest["whisper"] = {"repository": "openai/whisper-large-v3-turbo", "commit": "f" * 40, "directory": "whisper",
                           "files": [{"path": "config.json", "size": 1, "sha256": "0" * 64}, *files]}
    (tmp_path / "models-manifest.json").write_text(json.dumps(manifest))


def test_without_whisper_the_sidecar_starts_without_spanish(tmp_path):
    install(tmp_path)

    assert provenance.load(tmp_path).spanish_speech is None


def test_reads_whispers_repository_commit_and_hash(tmp_path):
    install(tmp_path)
    install_whisper(tmp_path)

    assert provenance.load(tmp_path).spanish_speech == {
        "repository": "openai/whisper-large-v3-turbo", "commit": "f" * 40,
        "sha256": hashlib.sha256(b"whisper weights").hexdigest()}


@pytest.mark.parametrize(("weights", "recorded", "says"), [
    (b"something else", b"whisper weights", "--verify"),
    (b"whisper weights", None, "--only whisper"),
])
def test_refuses_whisper_weights_the_manifest_doesnt_record(tmp_path, weights, recorded, says):
    install(tmp_path)
    install_whisper(tmp_path, weights, recorded)

    with pytest.raises(provenance.ProvenanceError, match=says):
        provenance.load(tmp_path)
