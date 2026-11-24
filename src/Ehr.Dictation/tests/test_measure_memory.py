"""Tests for measure_memory.py's parts that need no stack: the cap the owner's rule gives, which recordings make the
load, how a recording is cut into chunks, what it refuses before touching the stack, and that it needs nothing but the
standard library (it runs in the dictation image as the sender, too). The measurement itself runs in the model window
of run B (its header), never here.

uv run --no-project --python 3.12 --with pytest --with "huggingface_hub>=2.2,<3" python -m pytest src/Ehr.Dictation/tests
"""

import ast
import importlib.util
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


def test_without_the_dictation_key_it_stops_before_touching_the_stack(monkeypatch):
    monkeypatch.delenv("DICTATION_API_KEY", raising=False)
    monkeypatch.setattr(measure_memory.subprocess, "run", lambda *a, **k: pytest.fail("the stack was touched"))
    with pytest.raises(SystemExit, match="Set DICTATION_API_KEY"):
        measure_memory.main([])


def test_it_needs_nothing_but_the_standard_library():
    tree = ast.parse(SCRIPT.read_text(encoding="utf-8"))
    imported = {alias.name.split(".")[0] for node in ast.walk(tree) if isinstance(node, ast.Import) for alias in node.names}
    imported |= {(node.module or "").split(".")[0] for node in ast.walk(tree) if isinstance(node, ast.ImportFrom)}
    assert imported - {"__future__"} <= set(sys.stdlib_module_names), imported
