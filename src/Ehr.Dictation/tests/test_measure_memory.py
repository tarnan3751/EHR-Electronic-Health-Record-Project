"""Tests for measure_memory.py's parts that need no stack: the cap the owner's rule gives, which recordings make the
load, how a recording is cut into chunks, what it reads from the cgroup and the sender, that it puts the stack back
whatever happens once the cap is raised, never reads or passes on a key, fails unless every session came back done, and
needs nothing but the standard library (it runs in the dictation container as the sender, too). The stack is played by
a stand-in for docker here; the measurement itself runs in the model window of run B (its header), never here.

uv run --no-project --python 3.12 --with pytest --with "huggingface_hub>=2.2,<3" python -m pytest src/Ehr.Dictation/tests
"""

import ast
import importlib.util
import json
import subprocess
import sys
from pathlib import Path

import pytest

SCRIPT = Path(__file__).resolve().parents[1] / "measure_memory.py"
spec = importlib.util.spec_from_file_location("measure_memory", SCRIPT)
measure_memory = importlib.util.module_from_spec(spec)
spec.loader.exec_module(measure_memory)
GIB = 1 << 30


@pytest.mark.parametrize(("peak", "cap"), [(4 * GIB, 5), (4 * GIB + 1, 6), (int(4.6 * GIB), 6), (int(3.2 * GIB), 4)])
def test_the_cap_is_the_peak_plus_a_quarter_rounded_up_to_a_whole_gib(peak, cap):
    assert measure_memory.cap_gib(peak) == cap


def test_the_load_is_the_longest_recordings_of_each_kind():
    manifest = {"variants": [{"id": "a", "seconds": 60.0}, {"id": "b", "seconds": 118.5, "run_as": "en"},
                             {"id": "c", "seconds": 110.0}, {"id": "d", "seconds": 90.0}]}
    chosen = measure_memory.longest(manifest, 2, lambda entry: not entry.get("run_as"))
    assert [entry["id"] for entry in chosen] == ["c", "d"]


@pytest.mark.parametrize(("size", "pieces", "sent_in"), [(10, 3, 3), (5_000_000, 1, 3), (5_000_000, 24, 24)])
def test_a_recording_is_sent_whole_in_chunks_none_over_the_services_limit(size, pieces, sent_in):
    data = bytes(range(256)) * (size // 256) + bytes(size % 256)
    sent = measure_memory.chunks(data, pieces)
    assert b"".join(sent) == data and len(sent) == sent_in
    assert all(len(chunk) <= measure_memory.CHUNK_BYTES for chunk in sent)


DONE = [{"kind": "visit", "file": f"v{i}", "sorting": "done"} for i in range(2)] + [
    {"kind": "dictation in Spanish", "file": f"d{i}", "sorting": "done"} for i in range(6)]
KEY = "a-key-from-the-shell"


class Docker:
    """A stand-in for the docker CLI: the stack's answers, every call recorded with its environment."""

    def __init__(self, sender, memory: int = 16 * GIB, llama_answers: bool = True):  # noqa: ANN001
        self.calls: list[tuple[list[str], dict | None]] = []
        self.sender = sender
        self.memory, self.llama_answers = memory, llama_answers
        self.overrides: list[tuple[str, int]] = []  # each override's text and permissions, as compose read it

    def run(self, args, **kwargs):  # noqa: ANN001, ANN201
        self.calls.append((list(args), kwargs.get("env")))
        out = ""
        if args[:2] == ["docker", "compose"] and "up" in args and args[3:].count("-f") == 1:
            override = Path(args[args.index("-f", 3) + 1])
            self.overrides.append((override.read_text(encoding="utf-8"), override.stat().st_mode & 0o777))
        if args[:2] == ["docker", "info"]:
            out = f"{self.memory}\n"
        elif args[:2] == ["docker", "network"]:
            pass
        elif args[:2] == ["docker", "exec"] and args[4] == "-c" and "/health" in args[5]:
            return subprocess.CompletedProcess(args, 0 if self.llama_answers else 1, "", "")
        elif args[:2] == ["docker", "compose"] and "ps" in args:
            out = "c0ffee\n"
        elif args[:2] == ["docker", "inspect"]:
            out = '{"Health": {"Status": "healthy"}, "OOMKilled": false}|sha256:1234|ehr-dictation:dev|0\n'
        elif args[:2] == ["docker", "exec"] and args[3] == "cat":
            out = {"/sys/fs/cgroup/memory.peak": "5000000000\n", "/sys/fs/cgroup/memory.current": "4000000000\n",
                   "/sys/fs/cgroup/memory.stat": "anon 3000000000\nfile 900000000\nkernel 50000000\nshmem 1000\n"}[args[4]]
        elif args[:2] == ["docker", "exec"] and args[4] == "-c":
            pass  # the sender stopped in the container
        elif args[:2] == ["docker", "exec"]:
            return self.sender(args, kwargs)
        return subprocess.CompletedProcess(args, 0, out, "")

    def stopped_sender(self) -> bool:
        return any(args[:2] == ["docker", "exec"] and args[4] == "-c" and "--drive" in args[5] for args, _ in self.calls)

    def restored(self) -> bool:
        ups = [args for args, _ in self.calls if args[:2] == ["docker", "compose"] and "up" in args]
        return len(ups) == 2 and "-f" in ups[0][3:] and ups[1][:4] == ["docker", "compose", "-f", "compose.yaml"] \
            and ups[1].count("-f") == 1


def measured(monkeypatch, sender, argv=(), docker=None):  # noqa: ANN001, ANN201
    docker = docker or Docker(sender)
    monkeypatch.setenv("DICTATION_API_KEY", KEY)
    monkeypatch.setattr(measure_memory.subprocess, "run", docker.run)
    monkeypatch.setattr(measure_memory, "preflight", lambda: (
        [{"id": f"d{i}", "file": f"d{i}.webm"} for i in range(6)],
        [{"id": f"v{i}", "file": f"v{i}.webm", "voices": ["a", "b"]} for i in range(2)]))
    monkeypatch.setattr(measure_memory.time, "sleep", lambda seconds: None)
    return docker, measure_memory.main(list(argv))


def answering(sessions):  # noqa: ANN001, ANN201
    def sender(args, kwargs):  # noqa: ANN001, ANN202
        return subprocess.CompletedProcess(args, 0, json.dumps({"sessions": sessions, "maxrss_bytes": 80_000_000}), "")
    return sender


def test_a_whole_load_reports_both_peaks_the_image_and_the_cap_and_puts_the_stack_back(monkeypatch, tmp_path, capsys):
    docker, code = measured(monkeypatch, answering(DONE), ["--out", str(tmp_path / "m.json")])
    results = json.loads((tmp_path / "m.json").read_text(encoding="utf-8"))
    assert code == 0 and docker.restored()
    assert results["peak_bytes"] == 5_000_000_000 and results["cap_gib"] == 6
    assert results["not_cache_peak_bytes"] == 3_050_001_000 and results["sender_maxrss_bytes"] == 80_000_000
    assert results["image"] == {"name": "ehr-dictation:dev", "id": "sha256:1234"}
    assert "sha256:1234" in capsys.readouterr().out


def test_it_never_reads_or_passes_on_a_key(monkeypatch, capsys):
    docker, _ = measured(monkeypatch, answering(DONE))
    assert not any(KEY in " ".join(args) for args, _ in docker.calls)
    assert all(env is not None and "DICTATION_API_KEY" not in env and "LLAMA_API_KEY" not in env
               for args, env in docker.calls if args[:2] == ["docker", "compose"])
    assert not any("-e" in args for args, _ in docker.calls) and KEY not in capsys.readouterr().out
    host = SCRIPT.read_text(encoding="utf-8").split("def drive(")[0]
    assert "os.environ[" not in host and "getenv" not in host


@pytest.mark.parametrize("failure", ["timeout", "nothing printed", "not json", "interrupted"])
def test_once_the_cap_is_raised_the_stack_is_put_back_whatever_happens(monkeypatch, failure):
    def sender(args, kwargs):  # noqa: ANN001, ANN202
        if failure == "timeout":
            raise subprocess.TimeoutExpired(args, kwargs["timeout"])
        if failure == "interrupted":
            raise KeyboardInterrupt
        return subprocess.CompletedProcess(args, 0 if failure == "nothing printed" else 1,
                                           "" if failure == "nothing printed" else "Traceback ...", "boom")
    if failure == "interrupted":
        docker = Docker(sender)
        with pytest.raises(KeyboardInterrupt):
            measured(monkeypatch, sender, docker=docker)
        assert docker.restored() and docker.stopped_sender()
        return
    docker, code = measured(monkeypatch, sender)
    assert code == 1 and docker.restored()
    # A sender that timed out is stopped in the container too: killing docker exec leaves it running.
    assert docker.stopped_sender() is (failure == "timeout")


@pytest.mark.parametrize("failure", ["timeout", "interrupted"])
def test_the_sender_is_stopped_in_the_container_even_when_the_cap_is_kept_raised(monkeypatch, failure):
    def sender(args, kwargs):  # noqa: ANN001, ANN202
        if failure == "timeout":
            raise subprocess.TimeoutExpired(args, kwargs["timeout"])
        raise KeyboardInterrupt
    docker = Docker(sender)
    if failure == "interrupted":
        with pytest.raises(KeyboardInterrupt):
            measured(monkeypatch, sender, ["--keep-raised"], docker=docker)
    else:
        measured(monkeypatch, sender, ["--keep-raised"], docker=docker)
    assert docker.stopped_sender() and len([args for args, _ in docker.calls if "up" in args]) == 1


def test_kept_raised_only_when_asked(monkeypatch):
    docker, _ = measured(monkeypatch, answering(DONE), ["--keep-raised"])
    assert len([args for args, _ in docker.calls if "up" in args]) == 1


@pytest.mark.parametrize("sessions", [DONE[:7], DONE[:7] + [{"kind": "visit", "file": "v9", "error": "KeyError: 'x'"}],
                                      DONE[:7] + [{"kind": "visit", "file": "v9", "sorting": "unavailable"}]])
def test_it_fails_unless_all_eight_sessions_came_back_done_and_gives_no_cap_then(monkeypatch, tmp_path, capsys, sessions):
    docker, code = measured(monkeypatch, answering(sessions), ["--out", str(tmp_path / "m.json")])
    assert code == 1 and docker.restored()
    results = json.loads((tmp_path / "m.json").read_text(encoding="utf-8"))
    assert results["cap_gib"] is None and results["not_whole"]
    out = capsys.readouterr()
    assert "The cap" not in out.out and "No cap" in out.err
    assert measure_memory.whole({"sessions": DONE}) == []


def test_the_sender_reads_any_answer_and_the_host_any_output():
    assert measure_memory.decoded(b"<html>Bad Gateway</html>") == {"error": "<html>Bad Gateway</html>"}
    assert measure_memory.decoded(b'{"sorting": "done"}') == {"sorting": "done"} and measure_memory.decoded(b"") == {}
    assert measure_memory.sent("") == {} and measure_memory.sent("noise\n{not json") == {}
    assert measure_memory.not_cache("anon 10\nfile 99\nkernel 2\nshmem 1\n") == 13 and measure_memory.not_cache(None) is None


def test_the_measurement_mounts_this_file_and_the_recordings_read_only_with_the_cap_raised():
    override = measure_memory.override_yaml("12g")
    assert "mem_limit: 12g" in override and override.count(":ro\"") == 3 and "/measure/measure_memory.py" in override


def test_it_needs_nothing_but_the_standard_library():
    tree = ast.parse(SCRIPT.read_text(encoding="utf-8"))
    imported = {alias.name.split(".")[0] for node in ast.walk(tree) if isinstance(node, ast.Import) for alias in node.names}
    imported |= {(node.module or "").split(".")[0] for node in ast.walk(tree) if isinstance(node, ast.ImportFrom)}
    assert imported - {"__future__"} <= set(sys.stdlib_module_names), imported


LLAMA_KEY = "a-throwaway-llama-key-$x"


def native(monkeypatch, docker, argv=()):  # noqa: ANN001, ANN201
    monkeypatch.setenv("LLAMA_API_KEY", LLAMA_KEY)
    return measured(monkeypatch, None, ["--llama-url", "http://host.docker.internal:8080", *argv], docker=docker)


def test_with_a_native_llama_server_the_override_gives_its_address_and_key_privately_and_a_way_out(monkeypatch, capsys):
    docker = Docker(answering(DONE))
    _, code = native(monkeypatch, docker)
    assert code == 0 and docker.restored()
    (text, mode), = docker.overrides
    assert mode == 0o600 and 'DICTATION_LLAMA_URL: "http://host.docker.internal:8080"' in text
    assert 'LLAMA_API_KEY: "a-throwaway-llama-key-$$x"' in text  # "$" doubled for compose
    assert "networks: [inference, measure]" in text and "host.docker.internal:host-gateway" in text
    assert f"name: {measure_memory.MEASURE_NETWORK}" in text
    # The key is in the override only: never in a command, never printed; the measurement's network is removed.
    assert not any(LLAMA_KEY in " ".join(args) for args, _ in docker.calls) and LLAMA_KEY not in capsys.readouterr().out
    assert ["docker", "network", "rm", measure_memory.MEASURE_NETWORK] in [args for args, _ in docker.calls]
    # Still --no-deps: the stack's llama service isn't started.
    assert all("--no-deps" in args for args, _ in docker.calls if args[:2] == ["docker", "compose"] and "up" in args)


def test_the_override_is_gone_however_the_measurement_ends(monkeypatch):
    docker = Docker(answering(DONE), llama_answers=False)
    with pytest.raises(SystemExit, match="llama-server doesn't answer"):
        native(monkeypatch, docker)
    assert docker.restored()
    up = next(args for args, _ in docker.calls if args[:2] == ["docker", "compose"] and "up" in args)
    assert not Path(up[up.index("-f", 3) + 1]).exists()


def test_without_the_native_servers_key_it_stops_before_touching_the_stack(monkeypatch):
    docker = Docker(answering(DONE))
    monkeypatch.delenv("LLAMA_API_KEY", raising=False)
    monkeypatch.setattr(measure_memory.subprocess, "run", docker.run)
    with pytest.raises(SystemExit, match="LLAMA_API_KEY"):
        measure_memory.main(["--llama-url", "http://host.docker.internal:8080"])
    assert docker.calls == []


def test_without_the_option_the_override_holds_no_llama_address_nor_key(monkeypatch):
    docker = Docker(answering(DONE))
    monkeypatch.setenv("LLAMA_API_KEY", LLAMA_KEY)
    measured(monkeypatch, None, docker=docker)
    (text, _), = docker.overrides
    assert "LLAMA" not in text and "networks" not in text and "extra_hosts" not in text


@pytest.mark.parametrize(("size", "bytes_"), [("12g", 12 * GIB), ("512m", 512 << 20), ("2048", 2048),
                                              ("1.5G", int(1.5 * GIB))])
def test_a_docker_size_is_read_in_powers_of_1024(size, bytes_):
    assert measure_memory.size_bytes(size) == bytes_


def test_a_cap_above_dockers_memory_is_refused_before_touching_the_stack(monkeypatch):
    docker = Docker(answering(DONE), memory=int(11.7 * GIB))
    with pytest.raises(SystemExit, match="more than Docker's memory"):
        measured(monkeypatch, None, docker=docker)
    assert [args for args, _ in docker.calls] == [["docker", "info", "--format", "{{.MemTotal}}"]]


def test_a_peak_near_dockers_memory_is_suspect_and_gives_no_cap(monkeypatch, tmp_path, capsys):
    # The stand-in's peak is 5,000,000,000 bytes (4.66 GiB): within 1.5 GiB of 6 GiB.
    docker = Docker(answering(DONE), memory=6 * GIB)
    _, code = measured(monkeypatch, None, ["--cap-while-measuring", "6g", "--out", str(tmp_path / "m.json")],
                       docker=docker)
    results = json.loads((tmp_path / "m.json").read_text(encoding="utf-8"))
    assert code == 1 and results["cap_gib"] is None
    err = capsys.readouterr().err
    assert "suspect" in err and "raise Docker Desktop's memory" in err and "No cap" in err
