"""MedASR, the speech-to-text model: samples in, text and a confidence out.

It loads from the local models folder only (setup_models.py puts it there) and never from the internet: the image sets
HF_HUB_OFFLINE=1, and local_files_only says the same here. Decoding is greedy CTC, the model card's default; the
optional 6-gram language model in the MedASR repository isn't used. The text is MedASR's own, with spoken punctuation as
{period} and section headings as [HEADING]; transcript.py formats it. The confidence is the mean probability of the
token MedASR chose, over the frames where it chose one, and feeds quality.py.
"""

from __future__ import annotations

import threading
from dataclasses import dataclass
from pathlib import Path
from typing import Protocol

import numpy as np

from .audio import SAMPLE_RATE


@dataclass(frozen=True)
class Recognition:
    text: str
    confidence: float | None


class Recognizer(Protocol):
    def recognize(self, samples: np.ndarray) -> Recognition: ...


class MedAsr:
    """One copy of the model, shared by every session; recognize() takes one passage at a time."""

    def __init__(self, model_dir: Path, device: str = "cpu") -> None:
        import torch
        from transformers import AutoModelForCTC, AutoProcessor

        self._torch = torch
        self._device = device
        self._processor = AutoProcessor.from_pretrained(model_dir, local_files_only=True)
        self._model = AutoModelForCTC.from_pretrained(model_dir, local_files_only=True).to(device).eval()
        self._blank = int(self._model.config.pad_token_id or 0)
        self._lock = threading.Lock()

    def recognize(self, samples: np.ndarray) -> Recognition:
        if samples.size < SAMPLE_RATE // 10:
            return Recognition("", None)
        with self._lock, self._torch.inference_mode():
            inputs = self._processor(samples, sampling_rate=SAMPLE_RATE, return_tensors="pt").to(self._device)
            logits = self._model(**inputs).logits[0].float()
            best, ids = logits.softmax(-1).max(-1)
            text = self._processor.batch_decode(ids.unsqueeze(0).cpu())[0]
            chosen = ids != self._blank
            confidence = float(best[chosen].mean()) if bool(chosen.any()) else None
        return Recognition(text, confidence)
