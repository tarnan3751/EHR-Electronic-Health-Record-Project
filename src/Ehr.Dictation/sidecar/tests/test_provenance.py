"""provenance.py: which models produced a result, from the manifest setup_models.py writes, with MedASR's weights
checked against it before the sidecar starts."""

from __future__ import annotations

import hashlib
import json

import pytest

from ehr_dictation import provenance


def install(tmp_path, weights: bytes = b"medasr weights", recorded: bytes | None = None) -> None:
    (tmp_path / "medasr").mkdir(parents=True)
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


def install_speaker(tmp_path, weights: bytes = b"ecapa weights", recorded: bytes | None = None) -> None:
    install(tmp_path)
    (tmp_path / "speaker").mkdir()
    (tmp_path / "speaker" / "embedding_model.ckpt").write_bytes(weights)
    manifest = json.loads((tmp_path / "models-manifest.json").read_text())
    manifest["speaker"] = {
        "repository": "speechbrain/spkrec-ecapa-voxceleb", "commit": "e" * 40, "license": "apache-2.0", "directory": "speaker",
        "file": "embedding_model.ckpt",
        "files": [{"path": "embedding_model.ckpt", "size": 1,
                   "sha256": hashlib.sha256(recorded if recorded is not None else weights).hexdigest()}],
    }
    (tmp_path / "models-manifest.json").write_text(json.dumps(manifest))


# The speaker model (the expansion plan's step 16) is optional; when it is there, it is checked like MedASR.
def test_the_speaker_model_is_read_when_installed_and_none_when_not(tmp_path):
    install(tmp_path)
    assert provenance.load(tmp_path).speaker is None

    install_speaker(tmp_path / "with")
    assert provenance.load(tmp_path / "with").speaker == {
        "repository": "speechbrain/spkrec-ecapa-voxceleb", "commit": "e" * 40,
        "sha256": hashlib.sha256(b"ecapa weights").hexdigest(),
    }


def test_refuses_speaker_weights_the_manifest_doesnt_record(tmp_path):
    install_speaker(tmp_path, weights=b"something else", recorded=b"ecapa weights")

    with pytest.raises(provenance.ProvenanceError, match="speaker model"):
        provenance.load(tmp_path)
