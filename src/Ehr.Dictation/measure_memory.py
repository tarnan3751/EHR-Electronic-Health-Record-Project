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
  and `dictation` healthy: llama-server serving K2 Horizon, for the dictations' sorting and the visits' drafting. It
  measures the image the stack last built, ehr-dictation:dev (it recreates the container without --build), and prints
  that image's id: build it first (docker compose --profile ai build dictation) if the sidecar's code changed since.
- In infra/compose/models, every model the dictation container can load: MedASR and K2 Horizon, Whisper
  (setup_models.py --only whisper) and the speaker model (setup_models.py --only speaker), all in
  models-manifest.json; it checks that the manifest names whisper and speaker.
- Run B's Spanish recordings in eval/audio-es (generate_audio.py --set es) and the visits' recordings in
  eval/audio/conversations (generate_conversations.py).
- Nothing else. It never reads, prints or passes on a key: the load is sent from inside the dictation container, with
  the key the stack gave it. Its compose calls leave DICTATION_API_KEY and LLAMA_API_KEY out of their environment, so
  the recreated container takes infra/compose/.env's, never a value set in the shell that runs this.

What it does:

1. Recreates `dictation` with its cap raised for the measurement (--cap-while-measuring) and this file and the
   recordings mounted read-only (a compose override written to a temporary folder), so the peak isn't cut short by the
   cap it is measuring, and waits until it is healthy. A new container starts its cgroup's memory.peak at zero, so the
   peak covers loading MedASR, Whisper and the speaker model as well as the load. From here on, whatever happens (a
   failure, a timeout, Ctrl-C), it recreates `dictation` with compose.yaml's own cap at the end (not with
   --keep-raised).
2. Sends the load from inside the container (this file in --drive mode, standard library only, run with docker exec):
   two of the visits' longest recordings at once, each sent in chunks, stopped, its voices told apart, named and its
   note drafted; and, once both are stopped, the sessions the server takes besides them (max_sessions, 8, less the 2
   visits: 6), each one of run B's longest Spanish recordings sent in chunks as a dictation in Spanish (a Whisper pass
   at each chunk, as live, and a final one after stop) and sorted. Each session has an owner of its own, so the
   per-owner limit doesn't refuse any. The sender's own peak (its maxrss) is reported beside, as it is in the
   container's cgroup too.
3. Reads the container's peak from its cgroup (memory.peak), which holds the page cache of the model files and
   recordings read, and samples memory.current and memory.stat's anon + shmem + kernel every two seconds; reports both
   peaks, so the owner sees what the cap is computed from; if the kernel has no memory.peak, the highest memory.current
   read, said to be sampled. Checks the container wasn't killed for memory or restarted.
4. Prints, and writes to --out, the image measured, both peaks, the cap memory.peak gives (peak x 1.25, rounded up to
   the next whole GiB, the unit Docker's "g" is), and what each session came to. It fails (exit 1) unless all 8
   sessions came back done: a partial load never gives a cap.

The cap itself is set by hand from its result: infra/compose/compose.yaml's mem_limit and its comment, and the memory
figures in infra/compose/README.md (Docker Desktop memory) and the root README (Dictation models).
"""

from __future__ import annotations

import argparse
import json
import math
import os
import resource
import subprocess
import sys
import tempfile
import threading
import time
import uuid
from pathlib import Path

REPO = Path(__file__).resolve().parents[2]
COMPOSE_DIR = REPO / "infra" / "compose"
SPANISH_AUDIO = REPO / "eval" / "audio-es"
VISIT_AUDIO = REPO / "eval" / "audio" / "conversations"
MAX_SESSIONS = 8  # settings.py: sessions at once, visits among them
VISITS = 2  # settings.py: ambient_max_sessions
HEADROOM = 1.25  # the owner's ruling: the peak plus 25%
GIB = 1 << 30
CHUNK_BYTES = 1_900_000  # under settings.py's 2 MB a chunk
DICTATION_CHUNK_SECONDS = 5.0
SENDER_SECONDS = 2 * 3600  # the whole load, at most
KEYS = ("DICTATION_API_KEY", "LLAMA_API_KEY")  # never passed from the shell to compose
NOT_CACHE = ("anon", "shmem", "kernel")  # memory.stat's lines that aren't the page cache


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


def compose_env() -> dict[str, str]:
    """This process's environment without the stack's keys, so compose takes infra/compose/.env's (KEYS)."""
    return {name: value for name, value in os.environ.items() if name not in KEYS}


def compose(*args: str, override: Path | None = None) -> subprocess.CompletedProcess:
    files = ["-f", "compose.yaml"] + (["-f", str(override)] if override else [])
    return subprocess.run(["docker", "compose", *files, "--profile", "ai", *args], cwd=COMPOSE_DIR, check=True,
                          capture_output=True, text=True, env=compose_env())


def override_yaml(cap: str) -> str:
    """The compose override the measurement runs under: the raised cap, and this file and the recordings read-only."""
    mounts = [(Path(__file__).resolve(), "/measure/measure_memory.py"), (SPANISH_AUDIO, "/measure/audio-es"),
              (VISIT_AUDIO, "/measure/conversations")]
    volumes = "".join(f'      - "{source}:{target}:ro"\n' for source, target in mounts)
    return f"services:\n  dictation:\n    mem_limit: {cap}\n    volumes:\n{volumes}"


def container() -> str:
    found = compose("ps", "-q", "dictation").stdout.strip()
    if not found:
        raise SystemExit("The dictation container isn't running: start the ai profile (infra/compose/README.md).")
    return found


def inspect(container_id: str) -> dict:
    out = subprocess.run(["docker", "inspect", "--format", "{{json .State}}|{{.Image}}|{{.Config.Image}}|"
                          "{{.RestartCount}}", container_id], check=True, capture_output=True, text=True).stdout
    state, image_id, image, restarts = out.strip().split("|")
    return {"State": json.loads(state), "image_id": image_id, "image": image, "restarts": int(restarts)}


def cgroup(container_id: str, name: str) -> str | None:
    read = subprocess.run(["docker", "exec", container_id, "cat", f"/sys/fs/cgroup/{name}"], capture_output=True,
                          text=True)
    return read.stdout if read.returncode == 0 else None


def number(text: str | None) -> int | None:
    return int(text.strip()) if text and text.strip().isdigit() else None


def not_cache(stat: str | None) -> int | None:
    """memory.stat's anon + shmem + kernel: the memory that isn't the page cache."""
    if not stat:
        return None
    lines = dict(line.split() for line in stat.splitlines() if len(line.split()) == 2)
    found = [int(lines[name]) for name in NOT_CACHE if name in lines]
    return sum(found) if found else None


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


def sent(stdout: str) -> dict:
    """What the sender printed last, as JSON, or nothing if it printed none."""
    lines = stdout.strip().splitlines()
    try:
        return json.loads(lines[-1]) if lines else {}
    except json.JSONDecodeError:
        return {}


def measure(args: argparse.Namespace) -> dict:
    spanish, visits = preflight()
    results: dict = {"measured_at": time.strftime("%Y-%m-%dT%H:%M:%S%z"),
                     "cap_while_measuring": args.cap_while_measuring, "load": {"dictations_in_spanish": [entry["id"] for entry in spanish],
                              "visits": [entry["id"] for entry in visits]}, "sessions": []}
    try:
        with tempfile.TemporaryDirectory() as folder:
            override = Path(folder) / "measure.yaml"
            override.write_text(override_yaml(args.cap_while_measuring), encoding="utf-8")
            print(f"Recreating dictation with its cap at {args.cap_while_measuring} while it is measured...",
                  flush=True)
            compose("up", "-d", "--no-deps", "--force-recreate", "dictation", override=override)
        container_id = container()
        image = inspect(container_id)
        results["image"] = {"name": image["image"], "id": image["image_id"]}
        print(f"Measuring {image['image']} ({image['image_id']}).", flush=True)
        wait_healthy(container_id)
        results["after_loading"] = {"peak": number(cgroup(container_id, "memory.peak")),
                                    "not_cache": not_cache(cgroup(container_id, "memory.stat"))}
        print(f"Loaded: {results['after_loading']}", flush=True)

        timeline: list[tuple[float, int | None, int | None]] = []
        done = threading.Event()
        started = time.monotonic()

        def reading() -> tuple[float, int | None, int | None]:
            return (round(time.monotonic() - started, 1), number(cgroup(container_id, "memory.current")),
                    not_cache(cgroup(container_id, "memory.stat")))

        def sample() -> None:
            while not done.wait(2):
                timeline.append(reading())

        sampler = threading.Thread(target=sample, daemon=True)
        sampler.start()
        try:
            drive = subprocess.run(
                ["docker", "exec", container_id, "python", "/measure/measure_memory.py", "--drive",
                 "--dictations", *[entry["file"] for entry in spanish],
                 "--visits", *[f"{entry['file']}:{len(entry['voices'])}" for entry in visits]],
                capture_output=True, text=True, timeout=SENDER_SECONDS)
            sender = sent(drive.stdout)
            failed = drive.returncode != 0 or not sender
            results["sender_failed"] = (drive.stderr or "no output")[-2000:] if failed else None
        except subprocess.TimeoutExpired:
            sender = {}
            results["sender_failed"] = f"the load took over {SENDER_SECONDS} s"
        finally:
            done.set()
            sampler.join()
            timeline.append(reading())  # as the load ends, however short it was
        results["sessions"] = sender.get("sessions", [])
        results["sender_maxrss_bytes"] = sender.get("maxrss_bytes")
        state = inspect(container_id)
        peak = number(cgroup(container_id, "memory.peak"))
        sampled = max((current for _, current, _ in timeline if current is not None), default=0)
        measured = peak if peak is not None else sampled
        results |= {
            "peak_bytes": measured,
            "peak_from": "memory.peak" if peak is not None else "memory.current sampled every 2 s",
            "peak_gib": round(measured / GIB, 3), "cap_gib": cap_gib(measured),
            "not_cache_peak_bytes": max((value for *_, value in timeline if value is not None), default=None),
            "highest_sampled": sampled, "timeline": timeline,
            "oom_killed": state["State"].get("OOMKilled"), "restarts": state["restarts"],
        }
        return results
    finally:
        if not args.keep_raised:
            print("Recreating dictation with compose.yaml's own cap...", flush=True)
            try:
                compose("up", "-d", "--no-deps", "--force-recreate", "dictation")
            except subprocess.CalledProcessError as error:
                print(f"Couldn't put dictation back as compose.yaml has it: {error.stderr[-500:]}. Run docker compose "
                      "--profile ai up -d --no-deps --force-recreate dictation from infra/compose.", file=sys.stderr)


def whole(results: dict) -> list[str]:
    """Why the measurement isn't whole, if it isn't: every one of the 8 sessions must have come back done."""
    problems = []
    if results.get("sender_failed"):
        problems.append(f"the sender failed: {results['sender_failed']}")
    sessions = results.get("sessions", [])
    if len(sessions) != MAX_SESSIONS:
        problems.append(f"{len(sessions)} of {MAX_SESSIONS} sessions came back")
    problems += [f"{s.get('kind')} {s.get('file')}: {s.get('error') or s.get('sorting')}" for s in sessions
                 if s.get("error") or s.get("sorting") != "done"]
    if results.get("oom_killed") or results.get("restarts"):
        problems.append("the container was killed for memory or restarted")
    return problems


# ---- The sender, inside the container (standard library only) ------------------------------------------------------


def decoded(text: bytes) -> dict:
    """A response's body as JSON, or its first words as the error when it isn't JSON."""
    try:
        body = json.loads(text) if text else {}
    except (json.JSONDecodeError, UnicodeDecodeError):
        return {"error": text[:200].decode("utf-8", "replace")}
    return body if isinstance(body, dict) else {"error": str(body)[:200]}


def drive(dictations: list[str], visits: list[str]) -> list[dict]:
    import urllib.error
    import urllib.request

    key = os.environ.get("DICTATION_API_KEY", "")  # the stack's, in the container it runs in

    def call(method: str, path: str, owner: str, body: bytes | None = None, ambient: bool = False) -> tuple[int, dict]:
        headers = {"Authorization": f"Bearer {key}", "X-Dictation-Owner": owner}
        if ambient:
            headers["X-Dictation-Mode"] = "ambient"
        request = urllib.request.Request(f"http://127.0.0.1:8000{path}", data=body, method=method, headers=headers)
        try:
            with urllib.request.urlopen(request, timeout=60) as response:
                return response.status, decoded(response.read())
        except urllib.error.HTTPError as error:
            return error.code, decoded(error.read())

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
            if body.get("sorting") in until:
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
        except Exception as error:  # noqa: BLE001 -- every session records an outcome, whatever went wrong
            outcome["error"] = f"{type(error).__name__}: {error}"
        finally:
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
            outcome |= {"sorting": done.get("sorting"), "reason": done.get("reason")}
        except Exception as error:  # noqa: BLE001 -- every session records an outcome, whatever went wrong
            outcome["error"] = f"{type(error).__name__}: {error}"
        finally:
            outcomes.append(outcome)

    stops = [threading.Event() for _ in visits]
    threads = [threading.Thread(target=visit, args=(entry, stop)) for entry, stop in zip(visits, stops, strict=True)]
    for thread in threads:
        thread.start()
    for thread, stop in zip(threads, stops, strict=True):
        while not stop.wait(1) and thread.is_alive():
            pass  # a visit that ends without stopping never holds the dictations back
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
    parser.add_argument("--drive", action="store_true", help=argparse.SUPPRESS)  # the sender, inside the container
    parser.add_argument("--dictations", nargs="*", default=[], help=argparse.SUPPRESS)
    parser.add_argument("--visits", nargs="*", default=[], help=argparse.SUPPRESS)
    args = parser.parse_args(argv)
    if args.drive:
        sessions = drive(args.dictations, args.visits)
        maxrss = resource.getrusage(resource.RUSAGE_SELF).ru_maxrss * 1024  # Linux gives kilobytes
        print(json.dumps({"sessions": sessions, "maxrss_bytes": maxrss}))
        return 0
    results = measure(args)
    if args.out:
        args.out.write_text(json.dumps(results, indent=1) + "\n", encoding="utf-8")
    not_cache_peak = results.get("not_cache_peak_bytes")
    print(f"Image {results['image']['name']} ({results['image']['id']}). Peak {results['peak_gib']} GiB "
          f"({results['peak_from']}, page cache included); anon + shmem + kernel at most "
          f"{round(not_cache_peak / GIB, 3) if not_cache_peak else 'unread'} GiB; the sender's own peak "
          f"{results.get('sender_maxrss_bytes')} bytes. The cap memory.peak gives: {results['cap_gib']} GiB "
          f"(peak x {HEADROOM}, rounded up).")
    problems = whole(results)
    for problem in problems:
        print(f"Not whole: {problem}", file=sys.stderr)
    return 1 if problems else 0


if __name__ == "__main__":
    sys.exit(main())
