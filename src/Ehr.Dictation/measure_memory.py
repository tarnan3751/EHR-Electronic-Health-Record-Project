# /// script
# requires-python = ">=3.12"
# dependencies = []
# ///
"""Measures the dictation container's peak memory with both optional models loaded, under the load its cap has to hold,
so the cap can be set to that peak plus 25%, rounded up to the next whole GB (the owner's ruling of 2026-11-24). Run on
the machine and in the model window of run B, never in CI:

    uv run src/Ehr.Dictation/measure_memory.py [--out <results.json>] [--cap-while-measuring 12g] [--keep-raised]

What it needs:

- The local stack's ai profile running from infra/compose (docker compose --profile ai up -d --build), with `llama`
  and `dictation` healthy: llama-server serving K2 Horizon, for the dictations' sorting and the visits' drafting.
- In infra/compose/models, every model the dictation container can load: MedASR and K2 Horizon, Whisper
  (setup_models.py --only whisper) and the speaker model (setup_models.py --only speaker), all in
  models-manifest.json; it checks that the manifest names whisper and speaker.
- Run B's Spanish recordings in eval/audio-es (generate_audio.py --set es) and the visits' recordings in
  eval/audio/conversations (generate_conversations.py).
- DICTATION_API_KEY in the environment it runs in, the same as infra/compose/.env's: read from the environment and
  passed by name to the container that sends the load, never printed or written anywhere.

What it does:

1. Recreates `dictation` with its cap raised for the measurement (--cap-while-measuring, a compose override written
   to a temporary folder), so the peak isn't cut short by the cap it is measuring, and waits until it is healthy. A
   new container starts its cgroup's memory.peak at zero, so the peak covers loading MedASR, Whisper and the speaker
   model as well as the load.
2. Sends the load from a container of its own on the inference network (the dictation image, run with this file in
   --drive mode, standard library only), so the sender's memory isn't the measured container's: two of the visits'
   longest recordings at once, each sent in chunks, stopped, its voices told apart, named and its note drafted; and, once
   both are stopped, the sessions the server takes besides them (max_sessions, 8, less the 2 visits: 6), each one of run
   B's longest Spanish recordings sent in chunks as a dictation in Spanish (a Whisper pass at each chunk, as live, and a
   final one after stop) and sorted. Each session has an owner of its own, so the per-owner limit doesn't refuse any.
3. Reads the container's peak from its cgroup (memory.peak), and memory.current every two seconds as a timeline; if the
   kernel has no memory.peak, the highest memory.current read, said to be sampled. Checks the container wasn't killed
   for memory or restarted, and that every session got its answer.
4. Recreates `dictation` with compose.yaml's own cap (not with --keep-raised), and prints, and writes to --out, the peak,
   the cap it gives (peak x 1.25, rounded up to the next whole GiB, the unit Docker's "g" is), and what each session
   came to.

The cap itself is set by hand from its result: infra/compose/compose.yaml's mem_limit and its comment, and the memory
figures in infra/compose/README.md (Docker Desktop memory) and the root README (Dictation models).
"""

from __future__ import annotations

import argparse
import json
import math
import os
import subprocess
import sys
import tempfile
import threading
import time
import uuid
from pathlib import Path

REPO = Path(__file__).resolve().parents[2]
COMPOSE_DIR = REPO / "infra" / "compose"
PROJECT = "ehr"  # compose.yaml's name
NETWORK = f"{PROJECT}_inference"
IMAGE = "ehr-dictation:dev"
SPANISH_AUDIO = REPO / "eval" / "audio-es"
VISIT_AUDIO = REPO / "eval" / "audio" / "conversations"
MAX_SESSIONS = 8  # settings.py: sessions at once, visits among them
VISITS = 2  # settings.py: ambient_max_sessions
HEADROOM = 1.25  # the owner's ruling: the peak plus 25%
GIB = 1 << 30
CHUNK_BYTES = 1_900_000  # under settings.py's 2 MB a chunk
DICTATION_CHUNK_SECONDS = 5.0


def cap_gib(peak_bytes: int) -> int:
    """The cap the owner's rule gives: the peak plus 25%, rounded up to the next whole GiB."""
    return math.ceil(peak_bytes * HEADROOM / GIB)


def longest(manifest: dict, count: int, keep=lambda entry: True) -> list[dict]:  # noqa: ANN001
    """The manifest's `count` longest recordings that `keep` keeps, longest first."""
    entries = [entry for entry in manifest.get("variants", []) if keep(entry)]
    return sorted(entries, key=lambda entry: (-entry["seconds"], entry["id"]))[:count]


def chunks(data: bytes, pieces: int) -> list[bytes]:
    """The recording's bytes in `pieces` chunks, none over CHUNK_BYTES."""
    pieces = max(pieces, math.ceil(len(data) / CHUNK_BYTES), 1)
    size = math.ceil(len(data) / pieces)
    return [data[i : i + size] for i in range(0, len(data), size)]


# ---- The host: the stack, the cgroup, the result ---------------------------------------------------------------------


def compose(*args: str, override: Path | None = None) -> subprocess.CompletedProcess:
    files = ["-f", "compose.yaml"] + (["-f", str(override)] if override else [])
    return subprocess.run(["docker", "compose", *files, "--profile", "ai", *args], cwd=COMPOSE_DIR, check=True,
                          capture_output=True, text=True)


def container() -> str:
    found = compose("ps", "-q", "dictation").stdout.strip()
    if not found:
        raise SystemExit("The dictation container isn't running: start the ai profile (infra/compose/README.md).")
    return found


def inspect(container_id: str) -> dict:
    out = subprocess.run(["docker", "inspect", container_id], check=True, capture_output=True, text=True).stdout
    return json.loads(out)[0]


def cgroup(container_id: str, name: str) -> int | None:
    read = subprocess.run(["docker", "exec", container_id, "cat", f"/sys/fs/cgroup/{name}"], capture_output=True, text=True)
    return int(read.stdout.strip()) if read.returncode == 0 and read.stdout.strip().isdigit() else None


def wait_healthy(container_id: str, seconds: float = 600) -> None:
    deadline = time.monotonic() + seconds
    while time.monotonic() < deadline:
        state = inspect(container_id)["State"]
        if state.get("Health", {}).get("Status") == "healthy":
            return
        if state.get("Status") == "exited":
            raise SystemExit("The dictation container stopped while loading its models: see docker compose logs dictation.")
        time.sleep(5)
    raise SystemExit("The dictation container didn't become healthy in 10 minutes.")


def preflight() -> tuple[list[dict], list[dict]]:
    if not os.environ.get("DICTATION_API_KEY"):
        raise SystemExit("Set DICTATION_API_KEY to the stack's dictation key (infra/compose/.env's); it is never printed.")
    manifest = json.loads((COMPOSE_DIR / "models" / "models-manifest.json").read_text(encoding="utf-8"))
    missing = [name for name in ("medasr", "llm", "whisper", "speaker") if not manifest.get(name)]
    if missing:
        raise SystemExit(f"models-manifest.json has no {', '.join(missing)}: install every model first "
                         "(src/Ehr.Dictation/setup_models.py, --only whisper and --only speaker).")
    spanish = longest(json.loads((SPANISH_AUDIO / "manifest.json").read_text(encoding="utf-8")), MAX_SESSIONS - VISITS,
                      lambda entry: not entry.get("run_as"))
    visits = longest(json.loads((VISIT_AUDIO / "manifest.json").read_text(encoding="utf-8")), VISITS)
    if len(spanish) < MAX_SESSIONS - VISITS or len(visits) < VISITS:
        raise SystemExit("Make run B's Spanish audio and the visits' audio first (eval/README.md).")
    return spanish, visits


def measure(args: argparse.Namespace) -> dict:
    spanish, visits = preflight()
    with tempfile.TemporaryDirectory() as folder:
        override = Path(folder) / "measure.yaml"
        override.write_text(f"services:\n  dictation:\n    mem_limit: {args.cap_while_measuring}\n", encoding="utf-8")
        print(f"Recreating dictation with its cap at {args.cap_while_measuring} while it is measured...", flush=True)
        compose("up", "-d", "--no-deps", "--force-recreate", "dictation", override=override)
    container_id = container()
    wait_healthy(container_id)
    loaded = {"peak": cgroup(container_id, "memory.peak"), "current": cgroup(container_id, "memory.current")}
    print(f"Loaded: {loaded}", flush=True)

    timeline: list[tuple[float, int]] = []
    sending = threading.Event()
    started = time.monotonic()

    def sample() -> None:
        while not sending.is_set():
            if (current := cgroup(container_id, "memory.current")) is not None:
                timeline.append((round(time.monotonic() - started, 1), current))
            time.sleep(2)

    sampler = threading.Thread(target=sample, daemon=True)
    sampler.start()
    drive = subprocess.run(
        ["docker", "run", "--rm", "--network", NETWORK, "-e", "DICTATION_API_KEY",
         "-v", f"{Path(__file__).resolve()}:/measure/measure_memory.py:ro",
         "-v", f"{SPANISH_AUDIO}:/measure/audio-es:ro", "-v", f"{VISIT_AUDIO}:/measure/conversations:ro",
         IMAGE, "python", "/measure/measure_memory.py", "--drive",
         "--dictations", *[entry["file"] for entry in spanish],
         "--visits", *[f"{entry['file']}:{len(entry['voices'])}" for entry in visits]],
        capture_output=True, text=True)
    sending.set()
    sampler.join()
    sessions = json.loads(drive.stdout.strip().splitlines()[-1]) if drive.returncode == 0 else []
    state = inspect(container_id)
    peak = cgroup(container_id, "memory.peak")
    sampled = max((current for _, current in timeline), default=0)
    measured = peak if peak is not None else sampled
    results = {
        "measured_at": time.strftime("%Y-%m-%dT%H:%M:%S%z"),
        "cap_while_measuring": args.cap_while_measuring,
        "peak_bytes": measured, "peak_from": "memory.peak" if peak is not None else "memory.current sampled every 2 s",
        "peak_gib": round(measured / GIB, 3), "cap_gib": cap_gib(measured),
        "after_loading": loaded, "highest_sampled": sampled, "timeline": timeline,
        "oom_killed": state["State"].get("OOMKilled"), "restarts": state.get("RestartCount"),
        "sessions": sessions, "sender_failed": None if drive.returncode == 0 else drive.stderr[-2000:],
        "load": {"dictations_in_spanish": [entry["id"] for entry in spanish], "visits": [entry["id"] for entry in visits]},
    }
    if not args.keep_raised:
        print("Recreating dictation with compose.yaml's own cap...", flush=True)
        compose("up", "-d", "--no-deps", "--force-recreate", "dictation")
    return results


# ---- The sender, in a container on the inference network (standard library only) -----------------------------------


def drive(dictations: list[str], visits: list[str]) -> list[dict]:
    import urllib.error
    import urllib.request

    key = os.environ["DICTATION_API_KEY"]

    def call(method: str, path: str, owner: str, body: bytes | None = None, ambient: bool = False) -> tuple[int, dict]:
        headers = {"Authorization": f"Bearer {key}", "X-Dictation-Owner": owner}
        if ambient:
            headers["X-Dictation-Mode"] = "ambient"
        request = urllib.request.Request(f"http://dictation:8000{path}", data=body, method=method, headers=headers)
        try:
            with urllib.request.urlopen(request, timeout=60) as response:
                text = response.read()
                return response.status, json.loads(text) if text else {}
        except urllib.error.HTTPError as error:
            return error.code, json.loads(error.read() or b"{}")

    def send(session: str, owner: str, pieces: list[bytes], ambient: bool) -> None:
        seq = 0
        while seq < len(pieces):
            status, body = call("POST", f"/v1/sessions/{session}/audio?seq={seq}", owner, pieces[seq], ambient)
            if status == 200:
                seq += 1
            elif status == 409 and "expectedSeq" in body:
                seq = body["expectedSeq"]
            elif status == 429:
                time.sleep(1)
            else:
                raise RuntimeError(f"audio {seq}: {status} {body.get('error')}")

    def settle(session: str, owner: str, ambient: bool, until: set[str]) -> dict:
        for _ in range(360):
            status, body = call("POST", f"/v1/sessions/{session}/result?wait=10", owner, ambient=ambient)
            if status != 200:
                raise RuntimeError(f"result: {status} {body.get('error')}")
            if body["sorting"] in until:
                return body
        raise RuntimeError("no answer in an hour")

    outcomes: list[dict] = []

    def visit(entry: str, stopped: threading.Event) -> None:
        name, speakers = entry.rsplit(":", 1)
        owner, outcome = str(uuid.uuid4()), {"kind": "visit", "file": name}
        try:
            status, body = call("POST", f"/v1/sessions?mode=ambient&speakers={speakers}", owner,
                                json.dumps({"spokenLanguage": "en"}).encode(), ambient=True)
            if status != 201:
                raise RuntimeError(f"start: {status} {body.get('error')}")
            session = body["sessionId"]
            send(session, owner, chunks((Path("/measure/conversations") / name).read_bytes(), 1), True)
            call("POST", f"/v1/sessions/{session}/stop", owner, ambient=True)
            stopped.set()
            waiting = settle(session, owner, True, {"awaiting_speakers", "done", "unavailable"})
            if waiting["sorting"] == "awaiting_speakers":
                groups = [str(voice["group"]) for voice in waiting["speakers"]]
                if len(groups) < 2:
                    raise RuntimeError(f"{len(groups)} voice(s) told apart, not a clinician and a patient")
                roles = ["clinician", "patient", *["unsure"] * (len(groups) - 2)]
                status, body = call("POST", f"/v1/sessions/{session}/speakers", owner,
                                    json.dumps({"speakers": dict(zip(groups, roles, strict=True))}).encode(), True)
                if status != 200:
                    raise RuntimeError(f"speakers: {status} {body.get('error')}")
                waiting = settle(session, owner, True, {"done", "unavailable"})
            outcome |= {"sorting": waiting.get("sorting"), "reason": waiting.get("reason")}
        except (RuntimeError, OSError) as error:
            outcome["error"] = str(error)
            stopped.set()
        outcomes.append(outcome)

    def dictation(name: str) -> None:
        owner, outcome = str(uuid.uuid4()), {"kind": "dictation in Spanish", "file": name}
        try:
            status, body = call("POST", "/v1/sessions", owner, json.dumps({"spokenLanguage": "es"}).encode())
            if status != 201:
                raise RuntimeError(f"start: {status} {body.get('error')}")
            session, data = body["sessionId"], (Path("/measure/audio-es") / name).read_bytes()
            seconds = body["maxSeconds"]
            send(session, owner, chunks(data, math.ceil(seconds / DICTATION_CHUNK_SECONDS)), False)
            call("POST", f"/v1/sessions/{session}/stop", owner)
            done = settle(session, owner, False, {"done", "unavailable"})
            outcome |= {"sorting": done["sorting"], "reason": done.get("reason")}
        except (RuntimeError, OSError) as error:
            outcome["error"] = str(error)
        outcomes.append(outcome)

    stops = [threading.Event() for _ in visits]
    threads = [threading.Thread(target=visit, args=(entry, stop)) for entry, stop in zip(visits, stops, strict=True)]
    for thread in threads:
        thread.start()
    for stop in stops:
        stop.wait(3600)
    talking = [threading.Thread(target=dictation, args=(name,)) for name in dictations]
    for thread in talking:
        thread.start()
    for thread in threads + talking:
        thread.join()
    return outcomes


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--out", type=Path, help="where to write the measurement as JSON")
    parser.add_argument("--cap-while-measuring", default="12g", help="the dictation container's cap while it is measured")
    parser.add_argument("--keep-raised", action="store_true", help="leave dictation with the raised cap afterwards")
    parser.add_argument("--drive", action="store_true", help=argparse.SUPPRESS)  # the sender, in its own container
    parser.add_argument("--dictations", nargs="*", default=[], help=argparse.SUPPRESS)
    parser.add_argument("--visits", nargs="*", default=[], help=argparse.SUPPRESS)
    args = parser.parse_args(argv)
    if args.drive:
        print(json.dumps(drive(args.dictations, args.visits)))
        return 0
    results = measure(args)
    if args.out:
        args.out.write_text(json.dumps(results, indent=1) + "\n", encoding="utf-8")
    unanswered = [s for s in results["sessions"] if s.get("error")]
    print(f"Peak {results['peak_gib']} GiB ({results['peak_from']}); the cap it gives: {results['cap_gib']} GiB "
          f"(peak x {HEADROOM}, rounded up). Killed for memory: {results['oom_killed']}; restarts: {results['restarts']}; "
          f"sessions {len(results['sessions'])}, {len(unanswered)} without an answer.")
    if results["sender_failed"] or unanswered or results["oom_killed"] or not results["sessions"]:
        print("The measurement isn't whole: see the sessions and sender_failed in the results.", file=sys.stderr)
        return 1
    return 0


if __name__ == "__main__":
    sys.exit(main())
