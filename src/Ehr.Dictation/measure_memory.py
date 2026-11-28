# /// script
# requires-python = ">=3.12"
# dependencies = []
# ///
"""Measures the dictation container's peak memory with both optional models loaded, under the load its cap has to hold,
so the cap can be set to that peak plus 25%, rounded up to the next whole GB (the owner's ruling of 2026-11-24). Run on
the machine and in the model window of run B, never in CI:

    uv run src/Ehr.Dictation/measure_memory.py [--out <results.json>] [--cap-while-measuring 12g] [--keep-raised]
        [--llama-url http://host.docker.internal:8080]

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
- Or, with --llama-url (the owner's ruling of 2026-11-24, for a Docker Desktop too small for the llama container and
  the dictation container with every model): llama-server running natively on the Mac, as in run B, serving K2 Horizon
  with the stack's settings (-c 8192 -np 2 --reasoning off --no-webui --no-slots, on 127.0.0.1:8080) and a throwaway
  key in its --api-key-file, with the same key in LLAMA_API_KEY in the environment this runs in. Only `dictation` runs
  in Docker, recreated with --no-deps, so the stack's llama service isn't needed. This measures the same thing: the cap
  is the dictation container's own cgroup limit, and its memory.peak counts that container's processes only; llama
  ran in a container of its own before, outside that cgroup, and runs outside Docker now. What changes is only where
  the dictation container sends its sorting and drafting. The override then also sets the container's
  DICTATION_LLAMA_URL and LLAMA_API_KEY (the key written only there, in a temporary file readable by its owner alone
  and removed on every way out), attaches a network of the measurement's own (MEASURE_NETWORK) beside `inference`,
  which is internal and has no way out to the host, and maps host.docker.internal to the host (host-gateway; Docker
  Desktop maps it already). Before the load it checks llama-server answers /health from inside the container.
- Docker's memory (docker info's MemTotal, Docker Desktop's VM): a --cap-while-measuring above it is refused, and a
  peak within 1.5 GiB of it is suspect, held down by the VM: no cap is given, and the owner raises Docker Desktop's
  memory. This script never changes Docker's settings.
- Nothing else. It never reads, prints or passes on a key: the load is sent from inside the dictation container, with
  the key the stack gave it. Its compose calls leave DICTATION_API_KEY and LLAMA_API_KEY out of their environment, so
  the recreated container takes infra/compose/.env's, never a value set in the shell that runs this (with --llama-url,
  LLAMA_API_KEY is the native server's, set in the override above).

What it does:

1. Recreates `dictation` with its cap raised for the measurement (--cap-while-measuring) and this file and the
   recordings mounted read-only (a compose override written to a temporary folder), so the peak isn't cut short by the
   cap it is measuring, and waits until it is healthy. A new container starts its cgroup's memory.peak at zero, so the
   peak covers loading MedASR, Whisper and the speaker model as well as the load. From here on, whatever happens (a
   failure, a timeout, Ctrl-C), it recreates `dictation` with compose.yaml's own cap at the end (not with
   --keep-raised) and waits until it is healthy, saying so if it isn't or was killed for memory while loading its
   models (memory.peak after loading them alone, the page cache included, can be above that cap); on a timeout or
   Ctrl-C it first stops the sender in the container, which a killed docker exec leaves running, --keep-raised or not.
2. Sends the load from inside the container (this file in --drive mode, standard library only, run with docker exec,
   mounted alone at /measure/measure_memory.py, so the sender reads nothing of the repository's layout):
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
   sessions came back done: a partial load never gives a cap, printed or written. A session lost to model_timeout
   still leaves the container's peak sound: its audio went through every model in the container (Whisper at each
   chunk and after stop, the speaker model for a visit) before it waited on the model server, which runs outside the
   container and isn't what the cap holds. The verdict stays "not whole", and the owner reads the peak printed (as on
   2026-11-24, when two of six dictations timed out on the Mac's model server and the cap was set from the peak).

The cap itself is set by hand from its result: infra/compose/compose.yaml's mem_limit and its comment, and the memory
figures in infra/compose/README.md (Docker Desktop memory) and docs/dictation.md (Dictation models).
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

MAX_SESSIONS = 8  # settings.py: sessions at once, visits among them
VISITS = 2  # settings.py: ambient_max_sessions
HEADROOM = 1.25  # the owner's ruling: the peak plus 25%
GIB = 1 << 30
CHUNK_BYTES = 1_900_000  # under settings.py's 2 MB a chunk
DICTATION_CHUNK_SECONDS = 5.0
SENDER_SECONDS = 2 * 3600  # the whole load, at most
KEYS = ("DICTATION_API_KEY", "LLAMA_API_KEY")  # never passed from the shell to compose
NOT_CACHE = ("anon", "shmem", "kernel")  # memory.stat's lines that aren't the page cache
MEASURE_NETWORK = "ehr-measure-memory"  # with --llama-url: a network with a way out, for the measurement only
NEAR_THE_VM = int(1.5 * GIB)  # a peak this close to Docker's memory is suspect


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


def repo() -> Path:
    """The repository this file is in. The host's alone, and never read at import: in --drive mode the file is mounted
    by itself in the container (/measure/measure_memory.py), with no repository around it."""
    return Path(__file__).resolve().parents[2]


def compose_dir() -> Path:
    return repo() / "infra" / "compose"


def spanish_audio() -> Path:
    return repo() / "eval" / "audio-es"


def visit_audio() -> Path:
    return repo() / "eval" / "audio" / "conversations"


def compose_env() -> dict[str, str]:
    """This process's environment without the stack's keys, so compose takes infra/compose/.env's (KEYS)."""
    return {name: value for name, value in os.environ.items() if name not in KEYS}


def compose(*args: str, override: Path | None = None) -> subprocess.CompletedProcess:
    files = ["-f", "compose.yaml"] + (["-f", str(override)] if override else [])
    return subprocess.run(["docker", "compose", *files, "--profile", "ai", *args], cwd=compose_dir(), check=True,
                          capture_output=True, text=True, env=compose_env())


def override_yaml(cap: str, llama_url: str | None = None, llama_key: str | None = None) -> str:
    """The compose override the measurement runs under: the raised cap, and this file and the recordings read-only.
    With a llama-server outside the stack (--llama-url), also its address and key, and what reaching it takes: the
    inference network is internal, with no way out, so a network of the measurement's own, and host.docker.internal
    mapped to the host where the engine doesn't do so itself (Docker Desktop does)."""
    mounts = [(Path(__file__).resolve(), "/measure/measure_memory.py"), (spanish_audio(), "/measure/audio-es"),
              (visit_audio(), "/measure/conversations")]
    volumes = "".join(f'      - "{source}:{target}:ro"\n' for source, target in mounts)
    yaml = f"services:\n  dictation:\n    mem_limit: {cap}\n    volumes:\n{volumes}"
    if llama_url is None:
        return yaml
    # JSON strings are YAML strings; "$" is doubled, or compose would read it as a variable.
    def quoted(value: str) -> str:
        return json.dumps(value).replace("$", "$$")
    return (f"{yaml}    environment:\n      DICTATION_LLAMA_URL: {quoted(llama_url)}\n"
            f"      LLAMA_API_KEY: {quoted(llama_key or '')}\n"
            f"    networks: [inference, measure]\n    extra_hosts: [\"host.docker.internal:host-gateway\"]\n"
            f"networks:\n  measure:\n    name: {MEASURE_NETWORK}\n")


def write_private(path: Path, text: str) -> None:
    """The file, created readable by its owner only (it may hold a key)."""
    with os.fdopen(os.open(path, os.O_WRONLY | os.O_CREAT | os.O_EXCL, 0o600), "w", encoding="utf-8") as file:
        file.write(text)


def size_bytes(size: str) -> int:
    """A Docker size ("12g", "512m", "1073741824"), in bytes: b, k, m and g are powers of 1024."""
    units = {"b": 1, "k": 1 << 10, "m": 1 << 20, "g": 1 << 30}
    text = size.strip().lower()
    if text[-1:] in units:
        return int(float(text[:-1]) * units[text[-1]])
    return int(text)


def docker_memory() -> int:
    """Docker's memory (docker info's MemTotal): on Docker Desktop, its VM's."""
    out = subprocess.run(["docker", "info", "--format", "{{.MemTotal}}"], check=True, capture_output=True,
                         text=True).stdout
    return int(out.strip())


def reaches_llama(container_id: str, llama_url: str) -> bool:
    """Whether llama-server answers /health (no key needed) from inside the container."""
    probe = "import sys, urllib.request; urllib.request.urlopen(sys.argv[1].rstrip('/') + '/health', timeout=10)"
    return subprocess.run(["docker", "exec", container_id, "python", "-c", probe, llama_url], capture_output=True,
                          text=True, timeout=60).returncode == 0


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


def put_back() -> None:
    """Dictation recreated as compose.yaml has it, and waited for: memory.peak after loading the models alone, the page
    cache included, can be above compose.yaml's cap, so a container killed for memory while loading is said so here."""
    compose("up", "-d", "--no-deps", "--force-recreate", "dictation")
    restored = container()
    try:
        wait_healthy(restored)
    except SystemExit as stopped:
        if inspect(restored)["State"].get("OOMKilled"):
            raise SystemExit("it was killed for memory while loading its models, under compose.yaml's own cap "
                             "(mem_limit): see docker compose logs dictation.") from None
        raise


def preflight() -> tuple[list[dict], list[dict]]:
    manifest = json.loads((compose_dir() / "models" / "models-manifest.json").read_text(encoding="utf-8"))
    missing = [name for name in ("medasr", "llm", "whisper", "speaker") if not manifest.get(name)]
    if missing:
        raise SystemExit(f"models-manifest.json has no {', '.join(missing)}: install every model first "
                         "(src/Ehr.Dictation/setup_models.py, --only whisper and --only speaker).")
    spanish = longest(json.loads((spanish_audio() / "manifest.json").read_text(encoding="utf-8")),
                      MAX_SESSIONS - VISITS, lambda entry: not entry.get("run_as"))
    visits = longest(json.loads((visit_audio() / "manifest.json").read_text(encoding="utf-8")), VISITS)
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
    llama_key = None
    if args.llama_url:
        llama_key = os.environ.get("LLAMA_API_KEY", "")
        if not llama_key.strip():
            raise SystemExit("With --llama-url, set LLAMA_API_KEY to the key in the native llama-server's "
                             "--api-key-file (it is written only to the measurement's temporary override, never "
                             "printed).")
    vm = docker_memory()
    if size_bytes(args.cap_while_measuring) > vm:
        raise SystemExit(f"--cap-while-measuring {args.cap_while_measuring} is more than Docker's memory "
                         f"({round(vm / GIB, 2)} GiB): measure under a cap Docker has, or have the owner raise Docker "
                         "Desktop's memory. This script never changes it.")
    spanish, visits = preflight()
    results: dict = {"measured_at": time.strftime("%Y-%m-%dT%H:%M:%S%z"),
                     "cap_while_measuring": args.cap_while_measuring, "docker_memory_bytes": vm,
                     "llama": args.llama_url or "the stack's llama service",
                     "load": {"dictations_in_spanish": [entry["id"] for entry in spanish],
                              "visits": [entry["id"] for entry in visits]}, "sessions": []}
    try:
        with tempfile.TemporaryDirectory() as folder:  # removed on every way out, the override with it
            override = Path(folder) / "measure.yaml"
            write_private(override, override_yaml(args.cap_while_measuring, args.llama_url, llama_key))
            print(f"Recreating dictation with its cap at {args.cap_while_measuring} while it is measured...",
                  flush=True)
            compose("up", "-d", "--no-deps", "--force-recreate", "dictation", override=override)
        container_id = container()
        image = inspect(container_id)
        results["image"] = {"name": image["image"], "id": image["image_id"]}
        print(f"Measuring {image['image']} ({image['image_id']}).", flush=True)
        wait_healthy(container_id)
        if args.llama_url and not reaches_llama(container_id, args.llama_url):
            raise SystemExit(f"llama-server doesn't answer at {args.llama_url} from inside the container: start it "
                             "on the Mac first (eval/README.md's run B; the commands in this file's header).")
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
            stop_sender(container_id)
            sender = {}
            results["sender_failed"] = f"the load took over {SENDER_SECONDS} s"
        except KeyboardInterrupt:
            stop_sender(container_id)  # killing docker exec leaves it running in the container
            raise
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
                put_back()
            except subprocess.CalledProcessError as error:
                print(f"Couldn't put dictation back as compose.yaml has it: {error.stderr[-500:]}. Run docker compose "
                      "--profile ai up -d --no-deps --force-recreate dictation from infra/compose.", file=sys.stderr)
            except SystemExit as error:
                print(f"Dictation is back as compose.yaml has it, but not healthy: {error.code}", file=sys.stderr)
            if args.llama_url:
                subprocess.run(["docker", "network", "rm", MEASURE_NETWORK], capture_output=True, text=True)


# The sender's processes in the container, found by their command line and stopped: run with docker exec, it outlives
# a docker exec that is killed.
STOP_SENDER = """import os, signal
for pid in filter(str.isdigit, os.listdir("/proc")):
    try:
        command = open(f"/proc/{pid}/cmdline", "rb").read().split(b"\\0")
    except OSError:
        continue
    if int(pid) != os.getpid() and b"/measure/measure_memory.py" in command and b"--drive" in command:
        os.kill(int(pid), signal.SIGTERM)
"""


def stop_sender(container_id: str) -> None:
    subprocess.run(["docker", "exec", container_id, "python", "-c", STOP_SENDER], capture_output=True, text=True,
                   timeout=30)


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
    vm, peak = results.get("docker_memory_bytes"), results.get("peak_bytes")
    if vm and peak is not None and peak >= vm - NEAR_THE_VM:
        problems.append(f"the peak ({round(peak / GIB, 2)} GiB) came within 1.5 GiB of Docker's memory "
                        f"({round(vm / GIB, 2)} GiB), so it may have been held down: suspect. The owner must raise "
                        "Docker Desktop's memory (Settings, Resources) and measure again; this script never changes it")
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
    parser.add_argument("--llama-url", help="a llama-server outside the stack, such as "
                        "http://host.docker.internal:8080, with its key in LLAMA_API_KEY; the stack's llama service "
                        "isn't needed then")
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
    problems = whole(results)
    if problems:
        results["cap_gib"] = None  # a partial load gives no cap
        results["not_whole"] = problems
    if args.out:
        args.out.write_text(json.dumps(results, indent=1) + "\n", encoding="utf-8")
    not_cache_peak = results.get("not_cache_peak_bytes")
    print(f"Image {results['image']['name']} ({results['image']['id']}). Peak {results['peak_gib']} GiB "
          f"({results['peak_from']}, page cache included); anon + shmem + kernel at most "
          f"{round(not_cache_peak / GIB, 3) if not_cache_peak else 'unread'} GiB; the sender's own peak "
          f"{results.get('sender_maxrss_bytes')} bytes.")
    if problems:
        for problem in problems:
            print(f"Not whole: {problem}", file=sys.stderr)
        print("No cap: the load wasn't whole, so the peak isn't the one the cap has to hold.", file=sys.stderr)
        return 1
    print(f"The cap memory.peak gives: {results['cap_gib']} GiB (peak x {HEADROOM}, rounded up).")
    return 0


if __name__ == "__main__":
    sys.exit(main())
