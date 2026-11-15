"""transcriber.py: a live transcript commits passages once and keeps a tentative tail; stopping commits the rest."""

from __future__ import annotations

from conftest import FakeRecognizer, speech_and_pauses

from ehr_dictation.audio import SAMPLE_RATE
from ehr_dictation.transcriber import Progress, advance


def recording() -> list:
    return speech_and_pauses(("speech", 2), ("pause", 1), ("speech", 2), ("pause", 1), ("speech", 5))


def test_live_transcription_commits_passages_once_and_keeps_a_tentative_tail():
    samples = recording()
    recognizer = FakeRecognizer()

    first = advance(samples[: 7 * SAMPLE_RATE], Progress(), recognizer, final=False)
    calls = len(recognizer.calls)
    second = advance(samples, first, recognizer, final=False)

    assert [s.final for s in first.segments] == [True] and first.tail is not None and not first.tail.final
    assert second.segments[0] is first.segments[0]
    assert len(second.segments) == 2 and second.tail is not None
    # Only the new passage and the new tail were transcribed: nothing committed was read again.
    assert len(recognizer.calls) - calls == 2
    assert sum(recognizer.calls[calls:]) < samples.size - first.committed + SAMPLE_RATE


def test_stopping_commits_everything():
    progress = advance(recording(), Progress(), FakeRecognizer(), final=False)

    stopped = advance(recording(), progress, FakeRecognizer(), final=True)

    assert stopped.tail is None
    assert stopped.committed == recording().size
    assert len(stopped.segments) == 3 and all(s.final for s in stopped.segments)


def test_a_whole_recording_at_once_is_cut_at_its_pauses():
    progress = advance(recording(), Progress(), FakeRecognizer(), final=True)

    assert len(progress.segments) == 3
    ends = [s.end_sample / SAMPLE_RATE for s in progress.segments]
    assert 2 < ends[0] < 3 and 5 < ends[1] < 6 and ends[2] == recording().size / SAMPLE_RATE


def test_passages_the_model_is_unsure_of_are_marked():
    progress = advance(recording(), Progress(), FakeRecognizer(confidence=0.2), final=True)

    assert {s.unclear for s in progress.segments} == {"low_confidence"}


def test_silence_makes_no_passage_and_is_never_transcribed():
    recognizer = FakeRecognizer()
    silence = speech_and_pauses(("pause", 4))

    progress = advance(silence, Progress(), recognizer, final=True)

    assert progress.segments == [] and recognizer.calls == []


def test_a_passage_heard_as_only_a_spoken_command_is_not_unclear():
    progress = advance(recording(), Progress(), CommandRecognizer(), final=True)

    assert {s.unclear for s in progress.segments} == {None}


class CommandRecognizer(FakeRecognizer):
    def recognize(self, samples):
        from ehr_dictation.asr import Recognition

        return Recognition("{new paragraph}", 0.9)
