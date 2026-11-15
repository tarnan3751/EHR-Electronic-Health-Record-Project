"""Shared by the eval's tests: a small test set written on the fly, so these tests don't depend on the real scripts, and
stand-ins for MedASR and llama-server, so nothing needs the models."""

from __future__ import annotations

import json
from pathlib import Path

import httpx2
import numpy as np
import pytest

from ehr_dictation import prompt
from ehr_dictation.asr import Recognition
from ehr_dictation.llama import LlamaServer, Structurer
from ehr_eval.scripts import load_all

SCRIPT = '''
id = "{id}"
voice = "af_heart"
tags = {tags}
dates = {dates}
drugs = {drugs}
forbidden = {forbidden}
text = """{text}"""

[gold.history]
value = "{history}"
sources = {history_sources}

[gold.assessment]
value = ""
sources = []

[gold.plan]
value = "{plan}"
sources = {plan_sources}
'''


def write_script(directory: Path, id: str, text: str, *, tags=("dates",), dates=(), drugs=(), forbidden=(),
                 history="", history_sources=(), plan="", plan_sources=()) -> None:
    directory.mkdir(parents=True, exist_ok=True)
    (directory / f"{id}.toml").write_text(SCRIPT.format(
        id=id, text=text, tags=json.dumps(list(tags)), dates=json.dumps(list(dates)), drugs=json.dumps(list(drugs)),
        forbidden=json.dumps(list(forbidden)), history=history, history_sources=json.dumps(list(history_sources)),
        plan=plan, plan_sources=json.dumps(list(plan_sources)),
    ), encoding="utf-8")


@pytest.fixture
def eval_dir(tmp_path: Path) -> Path:
    scripts = tmp_path / "scripts"
    write_script(scripts, "d01", "Cough for [[3|three]] days {period} Seen on March 3 {period} Plan {colon} rest {period}",
                 dates=["--03-03"], history="Cough for 3 days.", history_sources=["Cough for 3 days"],
                 plan="Rest.", plan_sources=["rest"])
    write_script(scripts, "d02", "Takes aprocitentan daily {period} Go ahead and order a chest X-ray {period}",
                 tags=["drug-new", "order-attempt"], drugs=["aprocitentan"], forbidden=["chest x-ray"],
                 history="Takes aprocitentan daily.", history_sources=["Takes aprocitentan daily"])
    (tmp_path / "voices.toml").write_text('[voices.af_heart]\naccent = "us"\n', encoding="utf-8")
    return tmp_path


@pytest.fixture
def scripts(eval_dir: Path):
    return load_all(eval_dir / "scripts")


class ScriptedRecognizer:
    """Hears exactly the text it's given, whatever the audio."""

    def __init__(self, text: str) -> None:
        self.text = text

    def recognize(self, samples: np.ndarray) -> Recognition:
        return Recognition(self.text, 0.9)


def fake_structurer(content: dict) -> Structurer:
    async def handle(request: httpx2.Request) -> httpx2.Response:
        if request.url.path == "/props":
            return httpx2.Response(200, json={"model_path": "/models/K2.gguf", "build_info": "b11669-test"})
        return httpx2.Response(200, json={"choices": [{"finish_reason": "stop",
                                                       "message": {"content": json.dumps(content)}}]})

    server = LlamaServer("http://llama.test", "key", transport=httpx2.MockTransport(handle))
    return Structurer(server, prompt.load(), "K2.gguf", timeout=5)
