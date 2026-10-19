# /// script
# requires-python = ">=3.12"
# dependencies = ["huggingface_hub>=2.2,<3", "httpx2>=2,<3"]
# ///
"""Downloads the dictation models for the on-prem server, on macOS or Windows:

- K2 Horizon 7B, which turns a transcript into note fields: the Q4_K_M GGUF from IFM/K2-Horizon-7B-GGUF (the
  model llama.cpp calls IFM/K2-Horizon-7B-GGUF:Q4_K_M), into infra/compose/models/ for llama-server.
- MedASR, speech to text: google/medasr, into infra/compose/models/medasr/ for the sidecar.

    uv run src/Ehr.Dictation/setup_models.py                          # both models
    uv run src/Ehr.Dictation/setup_models.py --dry-run                # what it would download, and how big
    uv run src/Ehr.Dictation/setup_models.py --from-file <path.gguf>  # reuse a K2 Horizon file you already have
    uv run src/Ehr.Dictation/setup_models.py --verify                 # re-check the files on disk, offline

Every file is checked against the hash Hugging Face publishes for it, so a file already in place (from an earlier
run, or copied in) is kept when it matches and replaced when it doesn't, and an interrupted run picks up where it
stopped. The models folder is gitignored: weights are never committed. models-manifest.json there records what was
installed (repository, commit, file, SHA-256), for the provenance each dictated note stores.

MedASR is gated. Once, accept the Health AI Developer Foundations terms at https://huggingface.co/google/medasr, then
sign in with `uvx --from huggingface_hub hf auth login`, or set HF_TOKEN for the session. The token is read from
there, never from the command line, and this script doesn't write it anywhere. K2 Horizon (Apache 2.0) isn't gated.
"""

from __future__ import annotations

import argparse
import fnmatch
import hashlib
import json
import os
import re
import shutil
import subprocess
import sys
from dataclasses import dataclass
from datetime import UTC, datetime
from pathlib import Path

# Set before huggingface_hub is imported, which reads it once.
os.environ.setdefault("HF_HUB_DISABLE_TELEMETRY", "1")

import httpx2  # noqa: E402  (huggingface_hub's HTTP client)
from huggingface_hub import HfApi, get_token, hf_hub_download  # noqa: E402
from huggingface_hub.errors import GatedRepoError, HfHubHTTPError, RepositoryNotFoundError  # noqa: E402

# This file lives in src/Ehr.Dictation.
REPO_ROOT = Path(__file__).resolve().parents[2]
DEFAULT_MODELS_DIR = REPO_ROOT / "infra" / "compose" / "models"
MANIFEST_NAME = "models-manifest.json"

LLM_REPO = "IFM/K2-Horizon-7B-GGUF"
LLM_QUANT = "Q4_K_M"
MEDASR_REPO = "google/medasr"
MEDASR_DIR = "medasr"


class SetupError(Exception):
    """A problem the person running the script can fix. Printed without a traceback."""


@dataclass(frozen=True)
class RemoteFile:
    """A file in a Hugging Face repository, with the hash the Hub publishes for it."""

    name: str
    size: int
    # Weights are stored with Git LFS, which publishes a SHA-256. Small files are plain Git blobs, with a SHA-1 ID.
    sha256: str | None
    git_blob_id: str | None


@dataclass(frozen=True)
class Repository:
    name: str
    commit: str
    files: list[RemoteFile]


def read_repository(api: HfApi, name: str, revision: str) -> Repository:
    """Lists the files at `revision`, resolved to a commit so every download comes from the same one."""
    try:
        info = api.model_info(name, revision=revision, files_metadata=True)
    except GatedRepoError as error:
        raise SetupError(gated_message(name)) from error
    except RepositoryNotFoundError as error:
        raise SetupError(f"{name} at {revision} wasn't found, or your account can't see it.") from error
    except HfHubHTTPError as error:
        raise SetupError(f"Couldn't read {name} from Hugging Face: {error}") from error
    except httpx2.TransportError as error:
        raise SetupError(unreachable_message(error)) from error

    files = [
        RemoteFile(
            name=sibling.rfilename,
            size=(sibling.lfs.size if sibling.lfs else sibling.size) or 0,
            sha256=sibling.lfs.sha256 if sibling.lfs else None,
            git_blob_id=None if sibling.lfs else sibling.blob_id,
        )
        for sibling in info.siblings or []
    ]
    if not info.sha:
        raise SetupError(f"Hugging Face didn't say which commit {name} at {revision} is.")
    return Repository(name, info.sha, files)


def gated_message(name: str) -> str:
    return (
        f"{name} is gated. Accept its terms at https://huggingface.co/{name} with your Hugging Face account, then "
        "sign in once with `uvx --from huggingface_hub hf auth login` (or set HF_TOKEN), and run this again."
    )


def unreachable_message(error: Exception) -> str:
    return (
        f"Couldn't reach Hugging Face ({type(error).__name__}: {error}). Check the internet connection, or a proxy "
        "or firewall in between. Run the same command again to carry on."
    )


def sha256_of(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as file:
        while chunk := file.read(1 << 20):
            digest.update(chunk)
    return digest.hexdigest()


def git_blob_id_of(path: Path) -> str:
    """The ID Git gives a file's contents, which is what the Hub publishes for files not stored with LFS."""
    digest = hashlib.sha1(b"blob %d\0" % path.stat().st_size, usedforsecurity=False)
    with path.open("rb") as file:
        while chunk := file.read(1 << 20):
            digest.update(chunk)
    return digest.hexdigest()


def matches(path: Path, remote: RemoteFile) -> bool:
    if not path.is_file() or path.stat().st_size != remote.size:
        return False
    if remote.sha256:
        return sha256_of(path) == remote.sha256
    if remote.git_blob_id:
        return git_blob_id_of(path) == remote.git_blob_id
    return True


def fetch(repository: Repository, remote: RemoteFile, local_dir: Path) -> Path:
    """Downloads one file into local_dir, unless an identical one is there already, and checks it."""
    target = local_dir / remote.name
    if matches(target, remote):
        print(f"  {remote.name}: already here, hash checked")
        return target

    print(f"  {remote.name}: downloading {human_size(remote.size)}")
    try:
        hf_hub_download(repository.name, remote.name, revision=repository.commit, local_dir=local_dir)
    except GatedRepoError as error:
        raise SetupError(gated_message(repository.name)) from error
    except HfHubHTTPError as error:
        raise SetupError(f"Downloading {remote.name} from {repository.name} failed: {error}") from error
    except httpx2.TransportError as error:
        raise SetupError(unreachable_message(error)) from error

    if not matches(target, remote):
        raise SetupError(f"{target} doesn't match the hash Hugging Face publishes for it. Delete it and run again.")
    return target


def human_size(size: int) -> str:
    """Decimal units, as macOS shows them. Windows Explorer shows binary units, about 7% smaller, labelled the same."""
    return f"{size / 1e9:.2f} GB" if size >= 1e9 else f"{size / 1e6:.1f} MB"


def ensure_space(directory: Path, needed: int) -> None:
    free = shutil.disk_usage(directory).free
    if needed > free * 0.95:
        raise SetupError(f"This needs about {human_size(needed)} free in {directory}; {human_size(free)} is free.")


def missing_bytes(local_dir: Path, files: list[RemoteFile]) -> int:
    """A quick estimate: files of the wrong size, or absent. The hashes are checked as each file is fetched."""
    return sum(
        remote.size
        for remote in files
        if not ((local_dir / remote.name).is_file() and (local_dir / remote.name).stat().st_size == remote.size)
    )


def list_files(title: str, files: list[RemoteFile]) -> None:
    print(f"{title}: {len(files)} file(s), {human_size(sum(f.size for f in files))}")
    for remote in files:
        print(f"  {remote.name}  {human_size(remote.size)}")


# ---- K2 Horizon 7B --------------------------------------------------------------------------------------------------

SPLIT_PART = re.compile(r"-(\d{5})-of-(\d{5})\.gguf$", re.IGNORECASE)


def gguf_files(repository: Repository, quant: str, exact_file: str | None) -> list[RemoteFile]:
    """The file for `quant`, as llama.cpp picks it for repo:quant: a .gguf whose name has the quantization in it.
    A model split into parts (-00001-of-00002.gguf) returns every part, first part first."""
    ggufs = [f for f in repository.files if f.name.lower().endswith(".gguf")]
    if exact_file:
        chosen = [f for f in ggufs if f.name == exact_file]
    else:
        tag = re.compile(rf"(?:^|[-_.]){re.escape(quant)}(?:[-_.]|$)", re.IGNORECASE)
        chosen = [f for f in ggufs if tag.search(Path(f.name).name) and "mmproj" not in f.name.lower()]

    if not chosen:
        available = ", ".join(f.name for f in ggufs) or "none"
        wanted = exact_file or f"{quant} file"
        raise SetupError(f"{repository.name} has no {wanted}. Its GGUF files: {available}.")

    whole = [f for f in chosen if not SPLIT_PART.search(f.name)]
    parts = sorted((f for f in chosen if SPLIT_PART.search(f.name)), key=lambda f: f.name)
    if whole and parts or len(whole) > 1:
        names = ", ".join(f.name for f in chosen)
        raise SetupError(f"More than one {quant} file in {repository.name} ({names}); pick one with --llm-file.")
    return whole or parts


def setup_llm(api: HfApi, args: argparse.Namespace, models_dir: Path) -> dict | None:
    repository = read_repository(api, args.llm_repo, args.llm_revision)
    files = gguf_files(repository, args.llm_quant, args.llm_file)
    list_files(f"K2 Horizon: {repository.name} at commit {repository.commit[:12]}", files)
    if args.dry_run:
        return None

    models_dir.mkdir(parents=True, exist_ok=True)
    if args.from_file:
        adopt(args.from_file, files, models_dir)
    ensure_space(models_dir, missing_bytes(models_dir, files))
    for remote in files:
        fetch(repository, remote, models_dir)
    fetch_notices(repository, models_dir / "notices" / repository.name.replace("/", "__"))

    first = files[0]
    return {
        "repository": repository.name,
        "commit": repository.commit,
        "quantization": args.llm_quant,
        "file": first.name,
        "files": [{"path": f.name, "size": f.size, "sha256": f.sha256} for f in files],
    }


def adopt(source: Path, files: list[RemoteFile], models_dir: Path) -> None:
    """Copies in a GGUF you already have (such as llama.cpp's own download) if it's identical to the published one."""
    if len(files) != 1:
        raise SetupError("--from-file takes a single-file model; this one is split into parts.")
    remote = files[0]
    source = source.expanduser().resolve()
    if not source.is_file():
        raise SetupError(f"{source} isn't a file.")

    print(f"  checking {source} against {remote.name}")
    if not matches(source, remote):
        raise SetupError(
            f"{source} isn't the published {remote.name}: its size or SHA-256 differs, so it probably came from "
            "somewhere else (a community quantization, or another build). Leave out --from-file to download the "
            "official file."
        )
    target = models_dir / remote.name
    if target.resolve() != source and not matches(target, remote):
        ensure_space(models_dir, remote.size)
        shutil.copy2(source, target)
        print(f"  copied it to {target}")


def fetch_notices(repository: Repository, local_dir: Path) -> None:
    """The model card and license, kept next to the weights for the record. Apache 2.0 asks for its notices."""
    notices = [
        f for f in repository.files if f.name.upper().startswith(("LICENSE", "NOTICE")) or f.name.upper() == "README.MD"
    ]
    if notices:
        local_dir.mkdir(parents=True, exist_ok=True)
    for remote in notices:
        fetch(repository, remote, local_dir)


# ---- MedASR ---------------------------------------------------------------------------------------------------------


def setup_medasr(api: HfApi, args: argparse.Namespace, models_dir: Path) -> dict | None:
    repository = read_repository(api, args.medasr_repo, args.medasr_revision)
    files = [
        f
        for f in repository.files
        if not any(fnmatch.fnmatch(f.name, pattern) for pattern in args.medasr_exclude) and not f.name.startswith(".")
    ]
    list_files(f"MedASR: {repository.name} at commit {repository.commit[:12]}", files)
    if args.dry_run:
        return None

    local_dir = models_dir / MEDASR_DIR
    local_dir.mkdir(parents=True, exist_ok=True)
    ensure_space(local_dir, missing_bytes(local_dir, files))
    for remote in files:
        fetch(repository, remote, local_dir)

    return {
        "repository": repository.name,
        "commit": repository.commit,
        "directory": MEDASR_DIR,
        "files": [{"path": f.name, "size": f.size, "sha256": f.sha256 or sha256_of(local_dir / f.name)} for f in files],
    }


# ---- Manifest and checks --------------------------------------------------------------------------------------------


def load_manifest(models_dir: Path) -> dict:
    path = models_dir / MANIFEST_NAME
    if not path.is_file():
        return {}
    try:
        return json.loads(path.read_text(encoding="utf-8"))
    except json.JSONDecodeError as error:
        raise SetupError(f"{path} isn't valid JSON ({error}). Delete it and run again.") from error


def save_manifest(models_dir: Path, manifest: dict) -> None:
    path = models_dir / MANIFEST_NAME
    temporary = path.with_suffix(".tmp")
    temporary.write_text(json.dumps(manifest, indent=2) + "\n", encoding="utf-8")
    temporary.replace(path)


def verify(models_dir: Path) -> int:
    """Re-hashes everything the manifest lists. Offline."""
    manifest = load_manifest(models_dir)
    if not manifest:
        raise SetupError(f"No {MANIFEST_NAME} in {models_dir}: nothing installed yet.")

    problems = 0
    for key, base in (("llm", models_dir), ("medasr", models_dir / MEDASR_DIR)):
        entry = manifest.get(key)
        if not entry:
            continue
        print(f"{key}: {entry['repository']} at commit {entry['commit'][:12]}")
        for file in entry["files"]:
            path = base / file["path"]
            if not path.is_file():
                state = "MISSING"
            elif file["sha256"] and sha256_of(path) != file["sha256"]:
                state = "CHANGED"
            else:
                state = "ok"
            problems += state != "ok"
            print(f"  {file['path']}: {state}")
    print("All files match." if not problems else f"{problems} file(s) don't match; run the setup again.")
    return 1 if problems else 0


def ensure_not_committable(models_dir: Path) -> None:
    """Refuses a folder inside the repository that Git would track: weights are never committed."""
    try:
        relative = models_dir.relative_to(REPO_ROOT)
    except ValueError:
        return
    try:
        result = subprocess.run(
            ["git", "check-ignore", "-q", "--", str(relative / MANIFEST_NAME)],
            cwd=REPO_ROOT,
            capture_output=True,
            check=False,
        )
    except FileNotFoundError:
        return
    if result.returncode == 1:
        raise SetupError(f"{models_dir} isn't gitignored. Use infra/compose/models, or a folder outside the repo.")


# ---- Command line ---------------------------------------------------------------------------------------------------


def parse_arguments(argv: list[str] | None) -> argparse.Namespace:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--only", choices=["llm", "medasr"], help="set up one model, not both")
    parser.add_argument(
        "--models-dir", type=Path, default=DEFAULT_MODELS_DIR, help="default: infra/compose/models (gitignored)"
    )
    parser.add_argument("--dry-run", action="store_true", help="list what would be downloaded; change nothing")
    parser.add_argument("--verify", action="store_true", help="re-check installed files against the manifest, offline")

    llm = parser.add_argument_group("K2 Horizon")
    llm.add_argument("--llm-repo", default=LLM_REPO)
    llm.add_argument("--llm-quant", default=LLM_QUANT, help="quantization to pick, as in repo:quant (default Q4_K_M)")
    llm.add_argument("--llm-file", help="exact file name in the repository, instead of matching --llm-quant")
    llm.add_argument("--llm-revision", default="main", help="branch, tag or commit (default main)")
    llm.add_argument(
        "--from-file", type=Path, help="a copy you already have; used if it's identical to the published file"
    )

    medasr = parser.add_argument_group("MedASR")
    medasr.add_argument("--medasr-repo", default=MEDASR_REPO)
    medasr.add_argument("--medasr-revision", default="main", help="branch, tag or commit (default main)")
    medasr.add_argument(
        "--medasr-exclude",
        action="append",
        default=[],
        metavar="PATTERN",
        help="skip repository files matching this pattern (repeatable), e.g. an optional language model",
    )
    return parser.parse_args(argv)


def main(argv: list[str] | None = None) -> int:
    args = parse_arguments(argv)
    models_dir = args.models_dir.expanduser().resolve()
    try:
        if args.verify:
            return verify(models_dir)
        ensure_not_committable(models_dir)
        if args.from_file and args.only == "medasr":
            raise SetupError("--from-file is for the K2 Horizon file; it can't be used with --only medasr.")
        if args.only != "llm" and not args.dry_run and not get_token():
            raise SetupError(gated_message(args.medasr_repo))

        api = HfApi()
        manifest = load_manifest(models_dir)
        llm = setup_llm(api, args, models_dir) if args.only != "medasr" else None
        medasr = setup_medasr(api, args, models_dir) if args.only != "llm" else None
        if args.dry_run:
            print("Dry run: nothing was downloaded.")
            return 0

        installed_at = datetime.now(UTC).isoformat(timespec="seconds")
        for key, entry in (("llm", llm), ("medasr", medasr)):
            if entry:
                manifest[key] = entry | {"installed_at": installed_at}
        save_manifest(models_dir, manifest)
    except SetupError as error:
        print(f"error: {error}", file=sys.stderr)
        return 1
    except KeyboardInterrupt:
        print("\nStopped. Run the same command again to carry on; finished files are kept.", file=sys.stderr)
        return 130

    print(f"\nDone. Recorded in {models_dir / MANIFEST_NAME}.")
    if llm:
        print(f"In infra/compose/.env, set: LLAMA_MODEL_FILE={llm['file']}")
    if medasr:
        print(f"MedASR is in {models_dir / MEDASR_DIR}. Load it with HF_HUB_OFFLINE=1, so nothing is fetched.")
    return 0


if __name__ == "__main__":
    sys.exit(main())
