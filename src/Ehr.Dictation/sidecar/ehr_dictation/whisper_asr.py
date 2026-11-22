"""Whisper large-v3-turbo, the speech-to-text model for dictation in Spanish (the expansion plan's step 17): samples in,
text and a verdict on whether to trust it out. Dictation in English uses MedASR (asr.py), unchanged.

It loads from the local models folder only (setup_models.py --only whisper puts it there) and never from the internet:
the image sets HF_HUB_OFFLINE=1, and local_files_only says the same here. It runs in fp16 on a GPU (mps on a Mac, cuda)
and in fp32 on a CPU. Decoding is greedy at temperature 0, with Spanish and transcription forced and no fallback to
other temperatures, no prompt, and nothing carried over from an earlier passage: each passage (at most 20 s, cut at
pauses by segmenter.py) is transcribed on its own, so its text holds only what Whisper heard in it.

Before transcribing a passage, one decoder step after <|startoftranscript|> gives two probabilities, as Whisper's own
decoding computes them (transformers' detect_language keeps only the likeliest language, and its no-speech detection
reads the same step): that the passage is Spanish, over the language tokens alone, and that it holds no speech, over the
whole vocabulary. The verdict (Recognition.unclear) is then, in this order:

- low_confidence when the no-speech probability is over NO_SPEECH;
- other_language when the passage is less than MIN_SPANISH likely to be Spanish: it was transcribed as Spanish anyway,
  and is shown marked, never sorted (transcript.py), for the clinician to dictate again in Spanish or type;
- low_confidence when the words written look made up, as Whisper can over noise: their mean log-probability (each
  token, the end of the text included, read again in one pass over what was written) is under MIN_LOG_PROBABILITY, the
  text compresses more than MAX_COMPRESSION times (a phrase repeated), or it is nothing but stock caption phrases from
  Whisper's training data (STOCK_PHRASES).

The thresholds are those Whisper's own decoding uses to try again; here they mark the passage instead. There is no
confidence for quality.py's MedASR threshold to read (None): the verdict above takes its place.

Spanish needs no spoken punctuation, since Whisper writes it. Only "punto y aparte" and "nuevo párrafo" (a full stop and
a new paragraph) and "punto y seguido" (a full stop) are read as punctuation; "punto", "coma" and "dos puntos" stay
words (a decimal point, a clinical coma, "two points"). The text goes out in the form transcript.py formats, as MedASR's
does: that punctuation as {period} and {new paragraph}, and Whisper's brackets and braces, which transcript.py would
read as a heading or a command, as parentheses.
"""

from __future__ import annotations

import re
import threading
import unicodedata
import zlib
from pathlib import Path

import numpy as np

from .asr import Recognition
from .audio import SAMPLE_RATE
from .quality import LOW_CONFIDENCE, OTHER_LANGUAGE

SPANISH = "<|es|>"
MIN_SPANISH = 0.5
NO_SPEECH = 0.6
MIN_LOG_PROBABILITY = -1.0
MAX_COMPRESSION = 2.4
STOCK_PHRASES = (
    "Subtítulos realizados por la comunidad de Amara.org",
    "Gracias por ver el video",
    "¡Suscríbete!",
)


class Whisper:
    """One copy of the model, shared by every Spanish session; recognize() takes one passage at a time. load() reads
    it from the models folder; the tests give stand-ins for the processor and the model."""

    @classmethod
    def load(cls, model_dir: Path, device: str = "cpu") -> Whisper:
        import torch
        from transformers import WhisperForConditionalGeneration, WhisperProcessor

        dtype = torch.float32 if device == "cpu" else torch.float16
        processor = WhisperProcessor.from_pretrained(model_dir, local_files_only=True)
        model = WhisperForConditionalGeneration.from_pretrained(model_dir, local_files_only=True, dtype=dtype)
        return cls(processor, model.to(device).eval(), device, dtype)

    def __init__(self, processor, model, device: str, dtype) -> None:  # noqa: ANN001
        import torch

        self._torch = torch
        self._device = device
        self._dtype = dtype
        self._processor = processor
        self._model = model
        config = model.generation_config
        self._languages = list(config.lang_to_id.values())
        self._spanish = config.lang_to_id[SPANISH]
        # Whisper's vocabulary: text tokens, then <|endoftext|>, then every special token; <|nospeech|> comes just
        # before <|notimestamps|>, as transformers' no-speech detection takes it.
        self._end = config.eos_token_id
        self._no_speech = config.no_timestamps_token_id - 1
        self._prompt = [config.decoder_start_token_id, self._spanish, config.task_to_id["transcribe"],
                        config.no_timestamps_token_id]
        self._lock = threading.Lock()

    def recognize(self, samples: np.ndarray) -> Recognition:
        if samples.size < SAMPLE_RATE // 10:
            return Recognition("", None)
        torch = self._torch
        with self._lock, torch.inference_mode():
            features = self._processor(samples, sampling_rate=SAMPLE_RATE, return_tensors="pt").input_features
            encoded = self._model.get_encoder()(features.to(self._device, self._dtype))
            start = torch.tensor([self._prompt[:1]], device=self._device)
            first = self._model(encoder_outputs=encoded, decoder_input_ids=start).logits[0, -1].float()
            spanish, no_speech = language_and_no_speech(first, self._languages, self._spanish, self._no_speech)
            written = self._model.generate(
                encoder_outputs=encoded, language="es", task="transcribe", do_sample=False, num_beams=1, temperature=0.0
            )
            words = [token for token in written[0].tolist() if token < self._end]
            read = torch.tensor([self._prompt + words], device=self._device)
            logits = self._model(encoder_outputs=encoded, decoder_input_ids=read).logits[0, len(self._prompt) - 1 :].float()
            mean = mean_log_probability(logits, torch.tensor(words + [self._end], device=self._device))
            text = self._processor.tokenizer.decode(words, skip_special_tokens=True).strip()
        return Recognition(punctuated(text), None, verdict(text, spanish, no_speech, mean))


def language_and_no_speech(logits, languages: list[int], spanish: int, no_speech: int) -> tuple[float, float]:  # noqa: ANN001
    """From the logits of the step after <|startoftranscript|>: how likely the passage is Spanish, over the language
    tokens alone, and how likely it holds no speech, over the whole vocabulary."""
    over_languages = logits[languages].softmax(-1)
    return float(over_languages[languages.index(spanish)]), float(logits.softmax(-1)[no_speech])


def mean_log_probability(logits, targets) -> float:  # noqa: ANN001
    """The mean log-probability of each target token, the logits at each position predicting the next."""
    return float(logits.log_softmax(-1).gather(-1, targets.unsqueeze(-1)).mean())


def compression_ratio(text: str) -> float:
    data = text.encode("utf-8")
    return len(data) / len(zlib.compress(data)) if data else 0.0


def _words(text: str) -> list[str]:
    folded = "".join(c for c in unicodedata.normalize("NFD", text.casefold()) if unicodedata.category(c) != "Mn")
    return re.findall(r"\w+", folded)


STOCK = [_words(phrase) for phrase in STOCK_PHRASES]


def is_stock(text: str) -> bool:
    """Nothing but stock caption phrases, in any case, accents or punctuation."""
    words = _words(text)
    while words:
        phrase = next((phrase for phrase in STOCK if words[: len(phrase)] == phrase), None)
        if phrase is None:
            return False
        words = words[len(phrase) :]
    return bool(_words(text))


def verdict(text: str, spanish: float, no_speech: float, mean: float) -> str | None:
    """Why a passage Whisper wrote `text` for can't be trusted, or None (the order is the module header's). A passage
    with no text is quality.py's to judge."""
    if not text:
        return None
    if no_speech > NO_SPEECH:
        return LOW_CONFIDENCE
    if spanish < MIN_SPANISH:
        return OTHER_LANGUAGE
    if mean < MIN_LOG_PROBABILITY or compression_ratio(text) > MAX_COMPRESSION or is_stock(text):
        return LOW_CONFIDENCE
    return None


BRACKETS = str.maketrans("[]{}", "()()")
SPOKEN = re.compile(r"[\s.,;:]*\b(punto\s+y\s+aparte|nuevo\s+p[aá]rrafo|punto\s+y\s+seguido)\b[\s.,;:]*(\w?)", re.IGNORECASE)


def punctuated(text: str) -> str:
    """Whisper's text with its spoken punctuation as transcript.py's commands. At the start of a passage Whisper ended
    the one before with its own full stop, so only the new paragraph is added; so too after a question or an
    exclamation. The word after it starts with a capital."""

    def command(match: re.Match[str]) -> str:
        paragraph = not match.group(1).casefold().endswith("seguido")
        before = match.string[: match.start()].rstrip()
        stop = "{period} " if before and before[-1] not in "?!" else ""
        mark = f"{stop}{{new paragraph}}" if paragraph else stop.strip()
        return f" {mark} {match.group(2).upper()}"

    return " ".join(SPOKEN.sub(command, text.translate(BRACKETS)).split())
