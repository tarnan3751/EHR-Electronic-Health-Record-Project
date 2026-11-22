"""diarize.py: turns of one voice each, from windows clustered into the voices expected, smoothed, with noises left out
and passages the voices can't be told apart in marked unsure. Tones stand in for voices and a stand-in embedder for
the model: each tone's pitch is its own direction, so two tones at once sit between two voices."""

from __future__ import annotations

import numpy as np
from conftest import noise, tone

from ehr_dictation import diarize
from ehr_dictation.audio import SAMPLE_RATE

PITCHES = (220.0, 440.0, 660.0, 1500.0)  # three voices and a noise


class PitchEmbedder:
    """How loud each pitch is in a window: the voices it holds, as a direction."""

    def __init__(self) -> None:
        self.calls: list[tuple[int, int]] = []

    def embed(self, windows: np.ndarray) -> np.ndarray:
        self.calls.append(windows.shape)
        spectrum = np.abs(np.fft.rfft(windows, axis=1))
        bins = np.fft.rfftfreq(windows.shape[1], 1 / SAMPLE_RATE)
        energy = [spectrum[:, (bins > p - 20) & (bins < p + 20)].sum(axis=1) for p in PITCHES]
        return np.stack(energy, axis=1) + 1e-3


def visit(*parts: tuple[str | int | None, float]) -> np.ndarray:
    """(voice, seconds) in turn: a voice by its number (0, 1, 2), "both" for 0 and 1 at once, "noise" for the noise
    pitch, None for a pause; over a quiet room."""
    pieces = []
    for who, seconds in parts:
        if who is None:
            pieces.append(np.zeros(int(seconds * SAMPLE_RATE), np.float32))
        elif who == "both":
            pieces.append(tone(seconds, frequency=PITCHES[0]) + tone(seconds, frequency=PITCHES[1]))
        elif who == "noise":
            pieces.append(tone(seconds, frequency=PITCHES[3]))
        else:
            pieces.append(tone(seconds, frequency=PITCHES[who]))
    samples = np.concatenate(pieces)
    return samples + noise(samples.size / SAMPLE_RATE, -70)


def seconds(turns) -> list[tuple[float, float, int]]:
    return [(round(t.start / SAMPLE_RATE, 1), round(t.end / SAMPLE_RATE, 1), t.voice) for t in turns]


def test_two_voices_taking_turns_are_two_voices_in_the_order_they_spoke():
    samples = visit((1, 4), (None, 0.5), (0, 3), (None, 0.5), (1, 5), (None, 0.5), (0, 4))

    found = diarize.diarize(samples, PitchEmbedder(), 2)

    assert [t.voice for t in found.turns] == [1, 2, 1, 2] and found.voices == 2
    edges = [(4.25, 4.5), (7.5, 8.0), (13.0, 13.5)]
    for (low, high), turn in zip(edges, found.turns[1:], strict=True):
        assert low - 0.5 <= turn.start / SAMPLE_RATE <= high + 0.5
    assert all(a.end <= b.start for a, b in zip(found.turns, found.turns[1:]))


def test_three_voices_when_three_were_expected():
    samples = visit((0, 4), (None, 0.5), (1, 4), (None, 0.5), (2, 4), (None, 0.5), (0, 3))

    found = diarize.diarize(samples, PitchEmbedder(), 3)

    assert [t.voice for t in found.turns] == [1, 2, 3, 1]


def test_never_more_voices_than_expected_and_a_noise_is_not_a_voice():
    samples = visit((0, 5), (None, 0.5), ("noise", 0.75), (None, 0.5), (1, 5), (None, 0.5), (2, 5))

    found = diarize.diarize(samples, PitchEmbedder(), 2)

    assert found.voices <= 2
    two = diarize.diarize(visit((0, 5), (None, 0.5), ("noise", 0.75), (None, 0.5), (1, 5)), PitchEmbedder(), 2)
    assert [t.voice for t in two.turns] == [1, 2]


def test_no_turn_is_shorter_than_a_second():
    # Half a second of the other voice inside a long turn, with no pause either side.
    samples = visit((None, 1), (0, 4), (1, 0.5), (0, 4), (None, 1), (1, 4))

    found = diarize.diarize(samples, PitchEmbedder(), 2)

    assert all(t.end - t.start >= diarize.MIN_TURN * 160 for t in found.turns[:-1])
    assert [t.voice for t in found.turns] == [1, 2]


def test_a_passage_where_both_speak_at_once_sits_between_the_voices_and_under_the_margin_is_unsure(monkeypatch):
    samples = visit((0, 5), (None, 0.5), (1, 5), (None, 0.5), ("both", 1.5), (None, 0.5), (0, 5))
    found = diarize.diarize(samples, PitchEmbedder(), 2)
    at = lambda low, high: (int(low * SAMPLE_RATE), int(high * SAMPLE_RATE))  # noqa: E731
    overlap = next(t for t in found.turns if t.start <= at(11.7, 11.7)[0] < t.end)

    between = found.doubts(*at(11.1, 12.4), 1)["margin"]
    assert between < 0.5 * found.doubts(*at(1, 4), 1)["margin"] and between < 0.5 * found.doubts(*at(6.5, 9.5), 2)["margin"]
    assert not found.unsure(*at(1, 4), 1) and not found.unsure(*at(6.5, 9.5), 2)
    monkeypatch.setattr(diarize, "UNSURE_MARGIN", between + 0.01)
    assert found.unsure(*at(11.1, 12.4), overlap.voice) and not found.unsure(*at(1, 4), 1)
    assert found.unsure(*at(1, 4), 2), "a passage given to the voice its windows aren't is unsure"


def test_silence_has_no_turns_and_the_embedder_is_never_asked():
    embedder = PitchEmbedder()

    found = diarize.diarize(visit((None, 10)), embedder, 2)

    assert found.turns == () and found.voices == 0 and embedder.calls == []


def test_the_embedder_gets_whole_windows_and_the_result_keeps_no_embedding():
    embedder = PitchEmbedder()

    found = diarize.diarize(visit((0, 6), (None, 0.5), (1, 6)), embedder, 2)

    assert all(shape[1] == (diarize.WINDOW - 1) * 160 + 400 for shape in embedder.calls)
    assert {field for field in vars(found)} == {"turns", "windows"}
    assert all(set(vars(window)) == {"start", "end", "group", "margin", "relabelled", "similarity", "drop"} for window in found.windows)


# Series 1's review, F2: a short turn joined to the turn around it, a small group joined to a voice, and a window far
# from its voice's centre all make a passage Unsure, never a confident wrong label.
def test_a_short_answer_between_questions_is_unsure_not_the_questioners():
    samples = visit((None, 1), (0, 4), (None, 0.3), (1, 0.6), (None, 0.3), (0, 4), (None, 0.5), (1, 5))
    found = diarize.diarize(samples, PitchEmbedder(), 2)
    answer = int(5.3 * SAMPLE_RATE), int(5.9 * SAMPLE_RATE)
    turn = next(t for t in found.turns if t.start <= (answer[0] + answer[1]) // 2 < t.end)

    # The patient's (voice 2), or unsure: never the clinician's with confidence.
    assert turn.voice == 2 or found.unsure(*answer, turn.voice)


def test_a_voice_too_short_to_be_a_group_is_unsure_wherever_it_went():
    samples = visit((0, 6), (None, 0.5), (1, 6), (None, 0.5), (2, 2), (None, 0.5), (0, 6), (None, 0.5), (1, 6))
    found = diarize.diarize(samples, PitchEmbedder(), 3)
    third = int(13.5 * SAMPLE_RATE), int(15.5 * SAMPLE_RATE)
    turn = next(t for t in found.turns if t.start <= (third[0] + third[1]) // 2 < t.end)

    assert found.voices <= 3 and found.unsure(*third, turn.voice)
    assert found.doubts(*third, turn.voice)["similarity"] < found.doubts(SAMPLE_RATE, 5 * SAMPLE_RATE, 1)["similarity"]


def test_a_passage_far_from_its_voices_centre_is_unsure(monkeypatch):
    samples = visit((0, 5), (None, 0.5), (1, 5), (None, 0.5), (0, 5))
    found = diarize.diarize(samples, PitchEmbedder(), 2)
    clear = int(1 * SAMPLE_RATE), int(4 * SAMPLE_RATE)

    assert not found.unsure(*clear, 1)
    monkeypatch.setattr(diarize, "SIMILARITY_FLOOR", found.doubts(*clear, 1)["similarity"] + 0.01)
    assert found.unsure(*clear, 1)


def test_a_short_turn_too_short_for_a_window_of_its_own_is_unsure():
    # The clinician's question, 0.8 s, inside the patient's talk with no pause: no window is mostly the clinician's.
    samples = visit((None, 1), (1, 6), (0, 0.8), (1, 6), (None, 0.5), (0, 6))
    found = diarize.diarize(samples, PitchEmbedder(), 2)
    question = int(7.0 * SAMPLE_RATE), int(7.8 * SAMPLE_RATE)
    turn = next(t for t in found.turns if t.start <= question[0] < t.end)

    assert found.unsure(*question, turn.voice)
    assert found.doubts(*question, turn.voice)["drop"] > found.doubts(int(2 * SAMPLE_RATE), int(5 * SAMPLE_RATE), 1)["drop"]
