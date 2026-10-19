"""Tests for setup_models.py, against a fake Hugging Face Hub that publishes real hashes for its files. No network.

uv run --no-project --python 3.12 --with pytest --with "huggingface_hub>=2.2,<3" python -m pytest src/Ehr.Dictation/tests
"""

import hashlib
import importlib.util
import json
import subprocess
import sys
from pathlib import Path
from types import SimpleNamespace

import httpx2
import pytest
from huggingface_hub.errors import GatedRepoError
from huggingface_hub.hf_api import BlobLfsInfo, RepoSibling

# The script sits one folder up: src/Ehr.Dictation/setup_models.py, with this file in src/Ehr.Dictation/tests.
SCRIPT = Path(__file__).resolve().parents[1] / "setup_models.py"
spec = importlib.util.spec_from_file_location("setup_models", SCRIPT)
setup_models = importlib.util.module_from_spec(spec)
sys.modules["setup_models"] = setup_models
spec.loader.exec_module(setup_models)

Q4 = "K2-Horizon-7B-Q4_K_M.gguf"
Q4_BYTES = b"GGUF-q4km" * 5000


def git_blob(data: bytes) -> str:
    return hashlib.sha1(b"blob %d\0" % len(data) + data).hexdigest()


def gguf_repo(files=None):
    return {
        "sha": "a" * 40,
        "files": files
        or {
            ".gitattributes": (b"*.gguf filter=lfs", False),
            "README.md": (b"# K2 Horizon 7B GGUF", False),
            "LICENSE": (b"Apache License 2.0", False),
            Q4: (Q4_BYTES, True),
            "K2-Horizon-7B-Q8_0.gguf": (b"GGUF-q8" * 10, True),
            "K2-Horizon-7B-BF16.gguf": (b"GGUF-bf16" * 10, True),
            "K2-Horizon-7B-IQ4_XS.gguf": (b"GGUF-iq4" * 10, True),
        },
    }


MEDASR = {
    "sha": "b" * 40,
    "files": {
        ".gitattributes": (b"*.safetensors filter=lfs", False),
        "config.json": (b'{"model_type": "lasr_ctc"}', False),
        "model.safetensors": (b"weights" * 300, True),
        "tokenizer.json": (b'{"vocab": {}}', False),
        "README.md": (b"MedASR model card", False),
    },
}


class FakeHub:
    def __init__(self, repos, token="hf_test", gated=()):
        self.repos = repos
        self.token = token
        self.gated = set(gated)
        self.downloads = []
        self.corrupt = set()

    def model_info(self, name, revision="main", files_metadata=False):
        repo = self.repos[name]
        siblings = [
            RepoSibling(
                rfilename=filename,
                size=len(data),
                blob_id=git_blob(data),
                lfs=BlobLfsInfo(size=len(data), sha256=hashlib.sha256(data).hexdigest(), pointer_size=130)
                if lfs
                else None,
            )
            for filename, (data, lfs) in repo["files"].items()
        ]
        return SimpleNamespace(sha=repo["sha"], siblings=siblings)

    def download(self, repo_id, filename, revision=None, local_dir=None):
        assert revision == self.repos[repo_id]["sha"], "downloads use the resolved commit"
        if repo_id in self.gated:
            response = httpx2.Response(403, request=httpx2.Request("GET", "https://huggingface.co"))
            raise GatedRepoError("gated", response=response)
        self.downloads.append((repo_id, filename))
        data = self.repos[repo_id]["files"][filename][0]
        if filename in self.corrupt:
            data = b"X" + data[1:]
        target = Path(local_dir) / filename
        target.parent.mkdir(parents=True, exist_ok=True)
        target.write_bytes(data)
        return str(target)


@pytest.fixture
def hub(monkeypatch):
    fake = FakeHub({"IFM/K2-Horizon-7B-GGUF": gguf_repo(), "google/medasr": MEDASR})
    monkeypatch.setattr(setup_models, "HfApi", lambda: fake)
    monkeypatch.setattr(setup_models, "hf_hub_download", fake.download)
    monkeypatch.setattr(setup_models, "get_token", lambda: fake.token)
    return fake


@pytest.fixture
def models(tmp_path):
    return tmp_path / "models"


def run(models, *args):
    return setup_models.main(["--models-dir", str(models), *args])


def manifest(models):
    return json.loads((models / "models-manifest.json").read_text(encoding="utf-8"))


def test_a_dry_run_lists_the_files_and_changes_nothing(hub, models, capsys):
    assert run(models, "--dry-run") == 0
    out = capsys.readouterr().out
    assert Q4 in out and "model.safetensors" in out
    assert "Q8_0" not in out and "IQ4_XS" not in out and ".gitattributes" not in out
    assert not models.exists()
    assert hub.downloads == []


def test_sets_up_both_models_and_records_them(hub, models, capsys):
    assert run(models) == 0

    assert (models / Q4).read_bytes() == Q4_BYTES
    assert (models / "medasr" / "model.safetensors").is_file()
    assert not (models / "medasr" / ".gitattributes").exists()
    assert (models / "notices" / "IFM__K2-Horizon-7B-GGUF" / "LICENSE").is_file()
    gguf_downloads = [f for r, f in hub.downloads if f.endswith(".gguf")]
    assert gguf_downloads == [Q4], "only the Q4_K_M file"

    recorded = manifest(models)
    assert recorded["llm"]["file"] == Q4
    assert recorded["llm"]["commit"] == "a" * 40
    assert recorded["llm"]["files"][0]["sha256"] == hashlib.sha256(Q4_BYTES).hexdigest()
    assert {f["path"] for f in recorded["medasr"]["files"]} == {
        "config.json",
        "model.safetensors",
        "tokenizer.json",
        "README.md",
    }
    assert all(len(f["sha256"]) == 64 for f in recorded["medasr"]["files"])
    assert f"LLAMA_MODEL_FILE={Q4}" in capsys.readouterr().out


def test_a_second_run_downloads_nothing(hub, models):
    assert run(models) == 0
    hub.downloads.clear()
    assert run(models) == 0
    assert hub.downloads == []


def test_a_file_that_differs_from_the_published_one_is_replaced(hub, models):
    assert run(models, "--only", "llm") == 0
    (models / Q4).write_bytes(b"Y" * len(Q4_BYTES))
    hub.downloads.clear()

    assert run(models, "--only", "llm") == 0
    assert ("IFM/K2-Horizon-7B-GGUF", Q4) in hub.downloads
    assert (models / Q4).read_bytes() == Q4_BYTES


def test_a_download_that_fails_its_hash_is_refused(hub, models, capsys):
    hub.corrupt.add(Q4)
    assert run(models, "--only", "llm") == 1
    assert "doesn't match the hash" in capsys.readouterr().err
    assert not (models / "models-manifest.json").exists()


def test_an_identical_copy_is_reused_instead_of_downloaded(hub, models, tmp_path):
    existing = tmp_path / "llama.cpp-cache" / f"IFM_K2-Horizon-7B-GGUF_{Q4}"
    existing.parent.mkdir()
    existing.write_bytes(Q4_BYTES)

    assert run(models, "--only", "llm", "--from-file", str(existing)) == 0
    assert (models / Q4).read_bytes() == Q4_BYTES
    assert not [f for _, f in hub.downloads if f.endswith(".gguf")]


def test_a_different_copy_is_refused(hub, models, tmp_path, capsys):
    other = tmp_path / "community-Q4_K_M.gguf"
    other.write_bytes(b"Z" * len(Q4_BYTES))
    assert run(models, "--only", "llm", "--from-file", str(other)) == 1
    assert "isn't the published" in capsys.readouterr().err
    assert not (models / Q4).exists()


def test_verify_finds_changed_and_missing_files(hub, models, capsys):
    assert run(models) == 0
    assert run(models, "--verify") == 0

    (models / Q4).write_bytes(b"W" + Q4_BYTES[1:])
    (models / "medasr" / "config.json").unlink()
    assert run(models, "--verify") == 1
    out = capsys.readouterr().out
    assert f"{Q4}: CHANGED" in out and "config.json: MISSING" in out


def test_a_missing_quantization_names_the_files_there(hub, models, capsys):
    assert run(models, "--only", "llm", "--llm-quant", "Q2_K") == 1
    err = capsys.readouterr().err
    assert "IFM/K2-Horizon-7B-GGUF has no Q2_K file" in err
    assert "K2-Horizon-7B-Q8_0.gguf" in err


def test_a_model_split_into_parts_downloads_every_part(hub, models):
    parts = {
        "K2-Horizon-7B-Q4_K_M-00002-of-00002.gguf": (b"part2" * 10, True),
        "K2-Horizon-7B-Q4_K_M-00001-of-00002.gguf": (b"part1" * 10, True),
    }
    hub.repos["IFM/K2-Horizon-7B-GGUF"] = gguf_repo(parts)
    assert run(models, "--only", "llm") == 0
    assert manifest(models)["llm"]["file"] == "K2-Horizon-7B-Q4_K_M-00001-of-00002.gguf"
    assert len(manifest(models)["llm"]["files"]) == 2


def test_two_candidate_files_ask_for_one(hub, models, capsys):
    files = gguf_repo()["files"] | {"K2-Horizon-7B-Q4_K_M-v2.gguf": (b"v2", True)}
    hub.repos["IFM/K2-Horizon-7B-GGUF"] = gguf_repo(files)
    assert run(models, "--only", "llm") == 1
    assert "--llm-file" in capsys.readouterr().err
    assert run(models, "--only", "llm", "--llm-file", Q4) == 0


def test_medasr_needs_a_sign_in_first(hub, models, capsys):
    hub.token = None
    assert run(models) == 1
    assert "huggingface.co/google/medasr" in capsys.readouterr().err
    assert hub.downloads == []
    assert run(models, "--only", "llm") == 0


def test_terms_not_accepted_is_explained(hub, models, capsys):
    hub.gated.add("google/medasr")
    assert run(models, "--only", "medasr") == 1
    assert "Accept its terms" in capsys.readouterr().err


def test_setting_up_one_model_keeps_the_others_record(hub, models):
    assert run(models) == 0
    assert run(models, "--only", "llm") == 0
    assert "medasr" in manifest(models)


def test_a_folder_git_would_track_is_refused(hub, tmp_path, monkeypatch, capsys):
    repo = tmp_path / "repo"
    (repo / "infra" / "compose").mkdir(parents=True)
    subprocess.run(["git", "init", "-q", str(repo)], check=True)
    (repo / ".gitignore").write_text("infra/compose/models/\n*.gguf\n", encoding="utf-8")
    monkeypatch.setattr(setup_models, "REPO_ROOT", repo)

    assert run(repo / "infra" / "compose" / "models", "--only", "llm") == 0
    assert run(repo / "weights", "--only", "llm") == 1
    assert "isn't gitignored" in capsys.readouterr().err
