"""What makes a visit's answers (ambient.py): each recording through the sidecar's own code for listening to a visit, as
its listening.py runs it after stop and once the voices are named: ffmpeg decoding, the speaker model telling the
voices apart (diarize.py), MedASR a turn at a time, and K2 Horizon a window at a time with the conversation's prompt,
timed stage by stage and window by window. The longest clean recording is also fed in five-second chunks, as the page
sends a visit, to time each chunk's answer (every chunk is decoded with all before it).

Naming the voices is the one step a person takes. Here each voice is named for the role it spoke most of the time in
the recording's turns (its manifest's), one clinician, one patient and at most one other, as a careful clinician
confirms them, and "unsure" for a voice beyond those that spoke no other's turns; a recording whose voices can't be named
so (fewer than two told apart) can't be drafted, and says so.

proposes() asks the sidecar's own visit session whether it proposes for the chart, and contend() shares K2 Horizon
between a visit's window and two dictations at once through the sidecar's own turns (sessions.Turns, decision D16), for
ambient.py --contention.

Its hash is part of the conversations' pipeline hash (conversations.py), so a change to it means running the eval
again. What ambient.py does with the answers, the checks, the merging and the scoring, rescoring does again.
"""

from __future__ import annotations

import asyncio
import itertools
import time
from dataclasses import dataclass, field

from ehr_dictation import audio, conversation, diarize, segmenter, transcriber
from ehr_dictation.llama import Proposal, StructuringFailed, Structurer
from ehr_dictation import listening as service
from ehr_dictation.listening import Listening
from ehr_dictation.sessions import AMBIENT, GaveWay
from ehr_dictation.settings import Settings

CHUNK_SECONDS = 5.0  # as the Listen page sends a visit (ambient.js)


@dataclass
class Heard:
    """A recording transcribed, its voices told apart and named, with each window drafted (or why not)."""

    conversation: conversation.Conversation
    doubts: list[dict | None]
    names: dict[int, str]
    sizes: list[int] = field(default_factory=list)  # each passage's tokens, as the windows were cut by
    # Each window K2 Horizon was asked for, in order, a timed-out one's halves after it: the window, its proposal (None
    # if it failed), its seconds, and why it failed.
    tries: list[tuple[conversation.Window, Proposal | None, float, str | None]] = field(default_factory=list)
    reason: str | None = None
    timings: dict[str, float] = field(default_factory=dict)


def name_voices(heard: conversation.Conversation, turns: list[dict]) -> dict[int, str]:
    """Each voice's name: the naming the sidecar accepts (one clinician, one patient, at most one other, any others
    unsure) that agrees with the recording's turns for the most time, a voice named other only for time it shares with
    the other's turns. Empty when fewer than two voices said anything."""
    voices = [voice["group"] for voice in heard.speakers()]
    roles = sorted({turn["role"] for turn in turns})
    together: dict[tuple[int, str], float] = {}
    for placed, voice in zip(heard.placed, heard.voices, strict=True):
        if voice is None:
            continue
        start, end = placed.segment.start_sample / audio.SAMPLE_RATE, placed.segment.end_sample / audio.SAMPLE_RATE
        for turn in turns:
            shared = min(end, turn["end"]) - max(start, turn["start"])
            if shared > 0:
                together[(voice, turn["role"])] = together.get((voice, turn["role"]), 0.0) + shared
    best: dict[int, str] = {}
    best_time = -1.0
    for chosen in itertools.product([*roles, "unsure"], repeat=len(voices)):
        names = dict(zip(voices, chosen, strict=True))
        said = list(names.values())
        if said.count("clinician") != 1 or said.count("patient") != 1 or said.count("other") > 1:
            continue
        if any(name == "other" and not together.get((voice, name)) for voice, name in names.items()):
            continue
        agreed = sum(together.get(item, 0.0) for item in names.items())
        if agreed > best_time:
            best, best_time = names, agreed
    return best


def hear(data: bytes, speakers: int, turns: list[dict], recognizer, embedder) -> Heard:  # noqa: ANN001
    """Decoded, its voices told apart, transcribed a turn at a time and named (see the header)."""
    timings: dict[str, float] = {}
    started = time.perf_counter()
    samples = audio.decode(data, max_seconds=Settings.ambient_max_session_seconds + 1, timeout=60.0)
    timings["decode"] = time.perf_counter() - started

    started = time.perf_counter()
    found = diarize.diarize(samples, embedder, speakers)
    timings["diarize"] = time.perf_counter() - started

    started = time.perf_counter()
    levels = segmenter.levels(samples)
    floor = segmenter.noise_floor(levels)
    passages = []
    for turn in found.turns:
        for passage in transcriber.passages(samples, levels, floor, turn.start, turn.end, recognizer):
            passages.append((passage, turn.voice, found.unsure(passage.start_sample, passage.end_sample, turn.voice,
                                                               conversation.words(passage))))
    heard = conversation.assemble(passages)
    timings["transcribe"] = time.perf_counter() - started
    doubts = [doubted(found, p.segment, voice) for p, voice in zip(heard.placed, heard.voices, strict=True)]
    heard = heard.numbered()  # as the sidecar lists them, once each passage's doubts are read with diarize.py's numbers
    names = name_voices(heard, turns)
    return Heard(heard.named(names) if names else heard, doubts, names, timings=timings)


def doubted(found: diarize.Diarization, segment, voice: int | None) -> dict | None:  # noqa: ANN001
    """What diarize.unsure() weighed for a passage, and each window over it, numbers only, so other ways of judging a
    passage (the median window, or the windows away from a change of voice) can be measured from the stored run: its
    first and last frame, the voice it was first put with, its margin, similarity and drop, and whether it was
    relabelled."""
    doubts = found.doubts(segment.start_sample, segment.end_sample, voice) if voice is not None else None
    if doubts is None:
        return None
    return doubts | {"windows": [[w.start, w.end, w.group, round(w.margin, 4), round(w.similarity, 4), round(w.drop, 4),
                                  int(w.relabelled)] for w in found.over(segment.start_sample, segment.end_sample)]}


async def draft(heard: Heard, structurer: Structurer) -> Heard:
    """The visit drafted as the sidecar drafts it (ehr_dictation.listening.drafted()): windows by tokens, a window that
    timed out or ran out of tokens asked for again as two halves, once; each try timed and kept, the one that ended the
    drafting too, as it leaves the visit undrafted in the sidecar."""
    started = time.perf_counter()
    heard.sizes = await service.passage_tokens(heard.conversation, structurer)

    async def ask(window: conversation.Window) -> Proposal:
        began = time.perf_counter()
        try:
            proposal = await structurer.propose(window.text)
        except StructuringFailed as error:
            heard.tries.append((window, None, time.perf_counter() - began, error.reason))
            raise
        heard.tries.append((window, proposal, time.perf_counter() - began, None))
        return proposal

    try:
        await service.drafted(heard.conversation, structurer, ask, heard.sizes)
    except StructuringFailed as error:
        heard.reason = error.reason
    heard.timings["draft"] = time.perf_counter() - started
    return heard


async def chunk_seconds(data: bytes, seconds: float, recognizer, embedder, structurer: Structurer, models) -> list[float]:  # noqa: ANN001
    """The time to answer each five-second chunk of a visit, through the sidecar's sessions, every chunk decoded with
    all before it."""
    settings = Settings(api_key="eval", llama_api_key="eval", ambient_max_session_seconds=7200,
                        ambient_max_session_bytes=1 << 30, call_seconds=60.0)
    sessions = Listening(settings, recognizer, structurer, models, embedder=embedder)
    session = sessions.start("eval", AMBIENT, 2)
    size = max(1, int(len(data) * CHUNK_SECONDS / max(seconds, CHUNK_SECONDS)))
    times = []
    for seq, offset in enumerate(range(0, len(data), size)):
        started = time.perf_counter()
        await sessions.hear(session.id, "eval", seq, data[offset : offset + size])
        times.append(time.perf_counter() - started)
    sessions.cancel(session.id, "eval", AMBIENT)
    return times


def proposes(recognizer, embedder, structurer: Structurer, models) -> bool:  # noqa: ANN001
    """Whether a visit's session in the sidecar proposes for the chart, as Listening.start() makes it."""
    sessions = Listening(Settings(api_key="eval", llama_api_key="eval"), recognizer, structurer, models, embedder=embedder)
    return sessions.start("eval", AMBIENT, 2).proposes


async def contend(window: str | None, dictation: str, dictations: int, drafting: Structurer, dictating: Structurer,  # noqa: ANN001
                  recognizer, models, lead: float = 2.0) -> dict:
    """`dictations` dictations sorted at once, each with the sidecar's own turns and slots (Listening's), as the sidecar
    would; with a visit's window being drafted from `lead` seconds before they ask, or with none (window None, the
    baseline): each dictation's seconds from asking to answer and how it was sorted, and the window's seconds and how
    often it gave way."""
    sessions = Listening(Settings(api_key="eval", llama_api_key="eval"), recognizer, dictating, models, embedder=object())
    turns, slots = sessions._turns, sessions._sorting
    gave_way = 0

    async def window_drafted() -> float:
        nonlocal gave_way
        began = time.perf_counter()
        while True:
            async with turns.window() as turn:
                try:
                    async with slots:
                        await turn.draft(drafting.propose(window))
                except GaveWay:
                    gave_way += 1
                    continue
            return time.perf_counter() - began

    async def dictation_sorted() -> tuple[float, str]:
        began = time.perf_counter()
        try:
            async with asyncio.timeout(Settings.structuring_seconds), turns.dictation(), slots:
                await dictating.propose(dictation)
        except (TimeoutError, StructuringFailed) as error:
            return time.perf_counter() - began, getattr(error, "reason", "model_timeout")
        return time.perf_counter() - began, "done"

    drafted = asyncio.create_task(window_drafted()) if window is not None else None
    if drafted is not None:
        await asyncio.sleep(lead)
    sorted_ = await asyncio.gather(*(dictation_sorted() for _ in range(dictations)))
    out = {"dictation_seconds": [round(seconds, 1) for seconds, _ in sorted_], "dictation_sorted": [s for _, s in sorted_]}
    if drafted is not None:
        out |= {"window_seconds_alongside": round(await drafted, 1), "window_gave_way": gave_way}
    return out
