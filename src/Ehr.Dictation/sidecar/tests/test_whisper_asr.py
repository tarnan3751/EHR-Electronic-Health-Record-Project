"""whisper_asr.py: how a passage dictated in Spanish is transcribed and judged (the expansion plan's step 17), with a
stand-in for Whisper's processor and model: the decoding it asks for, the language and no-speech check, the
hallucination guard, and spoken punctuation. No test loads Whisper."""

from __future__ import annotations

from types import SimpleNamespace

import numpy as np
import pytest
import torch

from ehr_dictation import whisper_asr
from ehr_dictation.audio import SAMPLE_RATE
from ehr_dictation.quality import LOW_CONFIDENCE, OTHER_LANGUAGE
from ehr_dictation.transcript import format

# A vocabulary of the shape Whisper's has: text tokens, then <|endoftext|>, then the special tokens.
END, START, ENGLISH, SPANISH, TRANSCRIBE, NO_SPEECH, NO_TIMESTAMPS = 40, 41, 42, 43, 44, 45, 46
SIZE = 47
WORDS = {5: "Tos", 6: " seca", 7: " desde", 8: " hace", 9: " 3", 10: " días", 11: ".", 12: " Gracias", 13: " por",
         14: " ver", 15: " el", 16: " video"}
PROMPT = [START, SPANISH, TRANSCRIBE, NO_TIMESTAMPS]


class Processor:
    def __init__(self) -> None:
        self.tokenizer = SimpleNamespace(decode=self.decode)

    def __call__(self, samples, sampling_rate, return_tensors):  # noqa: ANN001, ANN204
        assert sampling_rate == SAMPLE_RATE and return_tensors == "pt"
        return SimpleNamespace(input_features=torch.zeros(1, 128, 3000))

    @staticmethod
    def decode(ids: list[int], skip_special_tokens: bool) -> str:
        return "".join(WORDS[i] for i in ids)


class Model:
    """Answers the step after <|startoftranscript|> with `first`, writes `written`, and gives each token it wrote
    `confidence` logits in the pass that reads them again."""

    def __init__(self, first: dict[int, float], written: list[int], confidence: float = 20.0) -> None:
        self.generation_config = SimpleNamespace(
            lang_to_id={"<|en|>": ENGLISH, "<|es|>": SPANISH}, task_to_id={"transcribe": TRANSCRIBE},
            eos_token_id=END, no_timestamps_token_id=NO_TIMESTAMPS, decoder_start_token_id=START)
        self.first = torch.zeros(SIZE)
        for token, logit in first.items():
            self.first[token] = logit
        self.written = written
        self.confidence = confidence
        self.decoded: list[list[int]] = []
        self.asked: dict = {}

    def get_encoder(self):  # noqa: ANN201
        return lambda features: ("encoded", features.dtype)

    def __call__(self, encoder_outputs, decoder_input_ids):  # noqa: ANN001, ANN204
        ids = decoder_input_ids[0].tolist()
        self.decoded.append(ids)
        if ids == [START]:
            return SimpleNamespace(logits=self.first[None, None, :])
        logits = torch.zeros(1, len(ids), SIZE)
        for position, target in enumerate([*ids[1:], END]):
            logits[0, position, target] = self.confidence
        return SimpleNamespace(logits=logits)

    def generate(self, **kwargs):  # noqa: ANN201
        self.asked = kwargs
        return torch.tensor([self.written])


def whisper(model: Model) -> whisper_asr.Whisper:
    return whisper_asr.Whisper(Processor(), model, "cpu", torch.float32)


SPEECH = np.zeros(SAMPLE_RATE, np.float32)
TEXT = [5, 6, 7, 8, 9, 10, 11]
SURE_OF_SPANISH = {SPANISH: 4.0, ENGLISH: 0.0}


def test_a_passage_is_transcribed_greedily_in_spanish_with_nothing_carried_over():
    model = Model(SURE_OF_SPANISH, [*PROMPT, *TEXT, END])

    recognition = whisper(model).recognize(SPEECH)

    assert recognition == whisper_asr.Recognition("Tos seca desde hace 3 días.", None, None)
    assert model.asked == {"encoder_outputs": ("encoded", torch.float32), "language": "es", "task": "transcribe",
                           "do_sample": False, "num_beams": 1, "temperature": 0.0}
    # The step after <|startoftranscript|>, then the words written read again after the forced prompt.
    assert model.decoded == [[START], [*PROMPT, *TEXT]]


def test_a_passage_not_heard_as_spanish_is_other_language():
    recognition = whisper(Model({SPANISH: 0.0, ENGLISH: 1.0}, [*PROMPT, *TEXT, END])).recognize(SPEECH)

    assert recognition.unclear == OTHER_LANGUAGE and recognition.text == "Tos seca desde hace 3 días."


@pytest.mark.parametrize(("first", "unclear"), [(SURE_OF_SPANISH, None),
                                                ({SPANISH: 0.0, ENGLISH: 1.0}, OTHER_LANGUAGE)])
def test_hear_gives_what_recognize_does_with_the_probabilities_its_verdict_read(first, unclear):
    model = Model(first, [*PROMPT, *TEXT, END])

    heard = whisper(model).hear(SPEECH)

    assert heard.recognition == whisper(model).recognize(SPEECH) and heard.recognition.unclear == unclear
    assert heard.spanish == pytest.approx(np.exp(first[SPANISH]) / (np.exp(first[SPANISH]) + np.exp(first[ENGLISH])))
    assert heard.no_speech == pytest.approx(1 / (np.exp(first[SPANISH]) + np.exp(first[ENGLISH]) + SIZE - 2))
    assert heard.mean_log_probability == pytest.approx(0, abs=1e-6)  # every token written with logit 20
    too_short = np.zeros(SAMPLE_RATE // 20, np.float32)
    assert whisper(model).hear(too_short) == whisper_asr.Heard(whisper_asr.Recognition("", None))


@pytest.mark.parametrize("model", [
    Model({**SURE_OF_SPANISH, NO_SPEECH: 12.0}, [*PROMPT, *TEXT, END]),  # most likely no speech at all
    Model(SURE_OF_SPANISH, [*PROMPT, *TEXT, END], confidence=0.0),  # every token a guess among 47
])
def test_a_passage_whisper_may_have_made_up_is_low_confidence(model):
    assert whisper(model).recognize(SPEECH).unclear == LOW_CONFIDENCE


def test_a_stock_caption_phrase_whisper_adds_after_real_speech_is_taken_off():
    thanks = [12, 13, 14, 15, 16, 11]  # " Gracias por ver el video."
    recognition = whisper(Model(SURE_OF_SPANISH, [*PROMPT, *TEXT, *thanks, END])).recognize(SPEECH)

    assert recognition == whisper_asr.Recognition("Tos seca desde hace 3 días.", None, None)


@pytest.mark.parametrize(("text", "kept"), [
    # A stock phrase, or more than one, at the end of a passage that says something else is taken off.
    ("Control en dos semanas. Gracias por ver el video.", "Control en dos semanas."),
    ("Niega fiebre. Subtítulos realizados por la comunidad de Amara.org", "Niega fiebre."),
    ("Tos seca, gracias por ver el vídeo. ¡Suscríbete!", "Tos seca"),
    ("Sin disnea. GRACIAS POR VER EL VIDEO", "Sin disnea."),
    # An ending only like one, or a stock phrase anywhere else, is kept as written.
    ("Le explico los ejercicios. Gracias por venir.", "Le explico los ejercicios. Gracias por venir."),
    ("Que vea el video de los ejercicios.", "Que vea el video de los ejercicios."),
    ("Puede ver el video.", "Puede ver el video."),
    ("Ejercicios realizados por la comunidad.", "Ejercicios realizados por la comunidad."),
    ("Gracias por ver el video del paciente.", "Gracias por ver el video del paciente."),
    ("Dice: gracias por ver el videojuego.", "Dice: gracias por ver el videojuego."),
    # Nothing but stock phrases is left whole, for the verdict to mark low confidence.
    ("Gracias por ver el video. Gracias por ver el video.", "Gracias por ver el video. Gracias por ver el video."),
])
def test_only_a_stock_ending_after_something_else_is_taken_off(text, kept):
    assert whisper_asr.without_stock_ending(text) == kept


def test_a_tenth_of_a_second_or_less_isnt_transcribed():
    model = Model(SURE_OF_SPANISH, [*PROMPT, *TEXT, END])

    assert whisper(model).recognize(np.zeros(SAMPLE_RATE // 10 - 1, np.float32)) == whisper_asr.Recognition("", None)
    assert model.decoded == []


def test_the_language_check_is_over_the_language_tokens_and_no_speech_over_all():
    logits = torch.zeros(SIZE)
    logits[SPANISH], logits[NO_SPEECH] = 2.0, 1.0

    spanish, no_speech = whisper_asr.language_and_no_speech(logits, [ENGLISH, SPANISH], SPANISH, NO_SPEECH)

    assert spanish == pytest.approx(np.exp(2) / (np.exp(2) + 1))
    assert no_speech == pytest.approx(np.exp(1) / (np.exp(2) + np.exp(1) + SIZE - 2))


@pytest.mark.parametrize(("text", "spanish", "no_speech", "mean", "expected"), [
    ("Tos seca desde hace 3 días.", 0.9, 0.1, -0.2, None),
    ("", 0.1, 0.9, -5.0, None),  # no text: quality.py judges the sound
    ("Tos seca desde hace 3 días.", 0.49, 0.1, -0.2, OTHER_LANGUAGE),
    ("Tos seca desde hace 3 días.", 0.4, 0.7, -0.2, LOW_CONFIDENCE),  # no speech, before the language
    ("Tos seca desde hace 3 días.", 0.9, 0.61, -0.2, LOW_CONFIDENCE),
    ("Tos seca desde hace 3 días.", 0.9, 0.1, -1.01, LOW_CONFIDENCE),
    ("Tos seca. " * 12, 0.9, 0.1, -0.2, LOW_CONFIDENCE),  # a phrase repeated compresses over 2.4 times
    ("Subtítulos realizados por la comunidad de Amara.org", 0.9, 0.1, -0.2, LOW_CONFIDENCE),
    ("gracias por ver el vídeo. ¡Suscríbete!", 0.9, 0.1, -0.2, LOW_CONFIDENCE),
    ("Gracias por ver el video del paciente.", 0.9, 0.1, -0.2, None),  # more than a stock phrase
])
def test_the_verdict(text, spanish, no_speech, mean, expected):
    assert whisper_asr.verdict(text, spanish, no_speech, mean) == expected


@pytest.mark.parametrize(("written", "shown"), [
    # Whisper writes Spanish's punctuation itself: nothing to say aloud.
    ("Refiere dolor torácico. Niega disnea.", "Refiere dolor torácico. Niega disnea."),
    # A new paragraph, with or without the full stop Whisper wrote around it.
    ("Seguimiento de depresión. Punto y aparte. Mujer de 29 años.", "Seguimiento de depresión.\n\nMujer de 29 años."),
    ("Seguimiento de depresión, punto y aparte, mujer de 29 años.", "Seguimiento de depresión.\n\nMujer de 29 años."),
    ("Sin fiebre. Nuevo párrafo. Exploración normal.", "Sin fiebre.\n\nExploración normal."),
    ("¿Tiene fiebre? Punto y aparte. No.", "¿Tiene fiebre?\n\nNo."),
    ("Dolor leve punto y seguido niega fiebre.", "Dolor leve. Niega fiebre."),
    # A passage that starts with one: Whisper ended the passage before with its own full stop.
    ("Punto y aparte. Impresión: neumonía.", "\n\nImpresión: neumonía."),
    # These stay words.
    ("Temperatura 37 punto 8, coma de Glasgow 15, dos puntos de sutura.",
     "Temperatura 37 punto 8, coma de Glasgow 15, dos puntos de sutura."),
    # Brackets and braces aren't read as a heading or a command.
    ("[Tos] {coma}", "(Tos) (coma)"),
])
def test_spoken_punctuation_and_brackets_go_out_as_transcript_py_formats_them(written, shown):
    assert format(whisper_asr.punctuated(written)) == shown
