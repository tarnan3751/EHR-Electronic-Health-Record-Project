"""Tells the voices of a recorded visit apart (the expansion plan's step 16, decision D2): who spoke when, as turns of one
voice each, and how sure that is, from speaker embeddings made by an Embedder (ecapa.py, or a test's stand-in).

1. Windows of 1.5 s every 0.75 s, over the 10 ms frames segmenter.levels() measures. A window at least MIN_SPEECH of
   whose frames are speech (as loud as segmenter.pause_threshold() or louder) is embedded; the rest is silence.
2. The windows are clustered into as many groups as the clinician said people would speak (2 or 3), by average-linkage
   clustering on cosine distance, the tree cut where it first has that many groups of MIN_GROUP windows or more. A
   smaller group is a cough, a door or a chair rather than a voice: it joins the voice it is nearest instead of standing
   for one. So a visit can end with fewer voices than were expected, never more.
3. The labels are smoothed: each window takes the label most of itself and its neighbours have (a median filter over
   three), and a turn shorter than MIN_TURN joins the turn beside it whose voice its windows are nearer.
4. Each window's similarity is its cosine similarity to the centre of the voice it was first put with, and its margin
   the difference between its similarity to the nearest voice's centre and to the next nearest's. A passage is Unsure
   (unsure()) when it has fewer than MIN_WORDS words, too few to tell a voice by whatever its windows say; when no window
   over it was embedded; when any window over it was relabelled, by the smoothing, by a short turn joining the turn
   beside it, or as part of a small group joined to a voice; when any of them is less similar to its voice's centre than
   SIMILARITY_FLOOR; when any of them sits near two voices (a margin under UNSURE_MARGIN); or when most of them were
   first put with another voice. So a short turn ("no" between two of the clinician's questions) is never another
   voice's with confidence when the smoothing gave it there or it is a passage of its own; one too short for a window of
   its own, said with no pause either side, goes with the passage it is in. How much less similar a window is than its
   voice's typical window (the median), its drop, is measured but judges nothing (SIMILARITY_DROP is None): on the
   second visits run, any window over a passage more than 0.1 under it made most of every clean visit Unsure on
   ordinary spread, and without that rule every word the clean recordings then labelled wrong with confidence was in a
   passage under four words. These are the values the eval's visits measured (eval/README.md), which go on measuring
   them.

Speech in a window too quiet to embed goes with the turn nearest it. Overlapping speech goes to the voice the window
is nearer, usually the louder. Voices are numbered 1, 2, 3 in the order they first speak. Nothing here is kept: the
embeddings are dropped once the turns are found, and no voiceprint is stored anywhere.
"""

from __future__ import annotations

from collections import Counter
from dataclasses import dataclass
from typing import Protocol

import numpy as np

from . import quality, segmenter

WINDOW = 150  # frames: 1.5 s
HOP = 75  # frames: 0.75 s
MIN_SPEECH = 0.3  # of a window's frames, for it to be embedded
MIN_GROUP = 4  # windows: a group smaller than this (3 s of speech) is a noise, not a voice
MIN_TURN = 100  # frames: 1 s
UNSURE_MARGIN = 0.1
SIMILARITY_FLOOR = 0.5
SIMILARITY_DROP: float | None = None  # judges nothing (see the header); the eval's sweeps give it values
MIN_WORDS = 4  # a passage with fewer is Unsure whatever its windows
EDGE = 20  # frames: a turn reaches this far into the quiet either side of its speech, for the quiet starts of words


class Embedder(Protocol):
    def embed(self, windows: np.ndarray) -> np.ndarray:
        """Windows of 16 kHz samples, all the same length (windows, samples), to embeddings (windows, size)."""
        ...


@dataclass(frozen=True)
class Turn:
    start: int  # samples
    end: int
    voice: int


@dataclass(frozen=True)
class Window:
    """An embedded window: its core (the frames it labels), its first group, and its margin."""

    start: int  # frames
    end: int
    group: int  # its voice as first clustered, numbered as the voices are
    margin: float
    relabelled: bool  # by the smoothing, by joining a short turn, or as part of a small group joined to a voice
    similarity: float  # to the centre of the voice it was first put with
    drop: float  # how much less similar than that voice's median window


@dataclass(frozen=True)
class Diarization:
    turns: tuple[Turn, ...]
    windows: tuple[Window, ...]

    @property
    def voices(self) -> int:
        return len({turn.voice for turn in self.turns})

    def doubts(self, start: int, end: int, voice: int) -> dict | None:
        """What unsure() weighs for the passage from start to end, given to `voice`: the windows' smallest margin and
        similarity, their largest drop, whether any was relabelled, and whether most were first put with another voice;
        None if none was embedded. The eval keeps these to measure the thresholds."""
        over = self.over(start, end)
        if not over:
            return None
        return {
            "margin": min(w.margin for w in over),
            "similarity": min(w.similarity for w in over),
            "drop": max(w.drop for w in over),
            "relabelled": any(w.relabelled for w in over),
            "elsewhere": sum(w.group != voice for w in over) * 2 > len(over),
        }

    def unsure(self, start: int, end: int, voice: int, words: int) -> bool:
        """Whether the passage from start to end, `words` long and given to `voice`, couldn't be told with confidence
        (see the header)."""
        return unsure(self.doubts(start, end, voice), words)

    def over(self, start: int, end: int) -> list[Window]:
        """The windows over the passage from start to end: those whose core (the frames each labels) it touches."""
        first, last = start // segmenter.HOP, max(start // segmenter.HOP + 1, -(-end // segmenter.HOP))
        return [w for w in self.windows if w.start < last and first < w.end]


def unsure(doubts: dict | None, words: int, margin: float | None = None, floor: float | None = None,
           drop: float | None = None) -> bool:
    """Whether a passage `words` long with these doubts (Diarization.doubts()) is Unsure, at the thresholds given or this
    module's."""
    drop = SIMILARITY_DROP if drop is None else drop
    return (
        words < MIN_WORDS
        or doubts is None
        or doubts["relabelled"]
        or doubts["elsewhere"]
        or doubts["margin"] < (UNSURE_MARGIN if margin is None else margin)
        or doubts["similarity"] < (SIMILARITY_FLOOR if floor is None else floor)
        or (drop is not None and doubts["drop"] > drop)
    )


def diarize(samples: np.ndarray, embedder: Embedder, speakers: int) -> Diarization:
    levels = segmenter.levels(samples)
    if levels.size == 0:
        return Diarization((), ())
    speech = (levels >= segmenter.pause_threshold(levels)) & (levels > quality.SILENCE_DBFS)
    size = min(WINDOW, levels.size)
    starts = [s for s in range(0, levels.size - size + 1, HOP) if speech[s : s + size].mean() >= MIN_SPEECH]
    if not starts:
        return Diarization((), ())

    length = (size - 1) * segmenter.HOP + segmenter.WINDOW
    embeddings = np.asarray(embedder.embed(np.stack([samples[s * segmenter.HOP :][:length] for s in starts])), np.float64)
    embeddings /= np.maximum(np.linalg.norm(embeddings, axis=1, keepdims=True), 1e-12)
    groups, absorbed = _cluster(embeddings, speakers)
    centres = np.stack([_unit(embeddings[groups == g].mean(axis=0)) for g in range(groups.max() + 1)])
    similarity = embeddings @ centres.T
    ordered = np.sort(similarity, axis=1)
    margins = ordered[:, -1] - ordered[:, -2] if centres.shape[0] > 1 else np.ones(len(starts))

    labels = _smoothed(groups, starts)
    cores = _cores(starts, size, levels.size)
    frames = np.full(levels.size, -1)
    for (low, high), label in zip(cores, labels, strict=True):
        frames[low:high] = label
    frames = _nearest(np.where(speech, frames, -1), speech)
    joined = _without_short_turns(frames, speech, cores, similarity)
    moved = speech & (joined != frames)
    frames = joined

    # Voices numbered in the order they first speak.
    order = {group: number for number, group in enumerate(dict.fromkeys(frames[frames >= 0].tolist()), start=1)}
    own = similarity[np.arange(len(starts)), groups]
    typical = {group: float(np.median(own[groups == group])) for group in set(groups.tolist())}
    windows = tuple(
        Window(low, high, order.get(int(group), 0), float(margin),
               bool(absorbed[i] or labels[i] != group or moved[low:high].any()), float(own[i]),
               typical[int(group)] - float(own[i]))
        for i, ((low, high), group, margin) in enumerate(zip(cores, groups, margins, strict=True))
    )
    return Diarization(_turns(frames, order, samples.size), windows)


def _unit(vector: np.ndarray) -> np.ndarray:
    return vector / max(float(np.linalg.norm(vector)), 1e-12)


def _cluster(embeddings: np.ndarray, k: int) -> tuple[np.ndarray, np.ndarray]:
    """Average-linkage clustering on cosine distance into k groups at most, numbered from 0 in the order they first
    speak, and which windows were in a smaller group joined to a voice. The tree is cut where, read from the top, it first has k groups of MIN_GROUP windows or more: those are the
    voices, and each smaller group joins the voice it is nearest on average, so a noise far from everything never keeps
    two voices together. A recording too short for k such groups is cut into k groups of whatever size."""
    n = len(embeddings)
    if n == 1:
        return np.zeros(1, int), np.zeros(1, bool)
    distance = 1.0 - embeddings @ embeddings.T
    np.fill_diagonal(distance, np.inf)
    sizes = np.ones(n)
    merges: list[tuple[int, int]] = []
    for _ in range(n - 1):
        i, j = sorted(np.unravel_index(int(np.argmin(distance)), distance.shape))
        merged = (sizes[i] * distance[i] + sizes[j] * distance[j]) / (sizes[i] + sizes[j])
        distance[i, :], distance[:, i] = merged, merged
        distance[i, i] = np.inf
        distance[j, :], distance[:, j] = np.inf, np.inf
        sizes[i] += sizes[j]
        merges.append((int(i), int(j)))

    # How many groups of MIN_GROUP windows or more there are after each merge.
    size = [1] * n
    large = [n if MIN_GROUP <= 1 else 0]
    for i, j in merges:
        before = (size[i] >= MIN_GROUP) + (size[j] >= MIN_GROUP)
        size[i] += size[j]
        large.append(large[-1] + (size[i] >= MIN_GROUP) - before)
    enough = [t for t, count in enumerate(large) if count >= k]
    cut = max(enough) if enough else n - k

    members: dict[int, list[int]] = {i: [i] for i in range(n)}
    for i, j in merges[:cut]:
        members[i] += members.pop(j)
    voices = sorted((key for key, m in members.items() if len(m) >= MIN_GROUP or not enough), key=lambda key: min(members[key]))
    groups = np.zeros(n, int)
    absorbed = np.zeros(n, bool)
    for number, key in enumerate(voices):
        groups[members[key]] = number
    for key, m in members.items():
        if key not in voices:
            nearest = min(voices, key=lambda voice: float(np.mean(1.0 - embeddings[m] @ embeddings[members[voice]].T)))
            groups[m] = voices.index(nearest)
            absorbed[m] = True
    return groups, absorbed


def _smoothed(groups: np.ndarray, starts: list[int]) -> list[int]:
    """Each window's label, the most common of itself and its neighbours, where they are next to it in time."""
    labels = []
    for i, group in enumerate(groups):
        around = [int(groups[j]) for j in (i - 1, i, i + 1) if 0 <= j < len(groups) and abs(starts[j] - starts[i]) <= HOP]
        common = Counter(around).most_common()
        labels.append(int(group) if common[0][1] == 1 else common[0][0])
    return labels


def _cores(starts: list[int], size: int, frames: int) -> list[tuple[int, int]]:
    """The frames each window labels: the middle HOP of it, reaching to the window's edges where no window is beside it,
    and the last to the end of the recording."""
    cores = []
    for i, start in enumerate(starts):
        before = i > 0 and start - starts[i - 1] <= HOP
        after = i + 1 < len(starts) and starts[i + 1] - start <= HOP
        low = start + (size - HOP) // 2 if before else start
        high = start + (size - HOP) // 2 + HOP if after else (frames if i + 1 == len(starts) else start + size)
        cores.append((low, min(high, frames)))
    return cores


def _nearest(frames: np.ndarray, speech: np.ndarray) -> np.ndarray:
    """Speech frames no window labelled take the label of the nearest labelled frame."""
    labelled = np.flatnonzero(frames >= 0)
    if labelled.size == 0:
        return frames
    out = frames.copy()
    for i in np.flatnonzero(speech & (frames < 0)):
        at = np.searchsorted(labelled, i)
        candidates = [labelled[j] for j in (at - 1, at) if 0 <= j < labelled.size]
        out[i] = frames[min(candidates, key=lambda c: abs(c - i))]
    return out


def _runs(frames: np.ndarray) -> list[tuple[int, int, int]]:
    """The turns in the labelled speech frames: (first frame, last frame + 1, label), quiet between frames of one label
    belonging to the turn."""
    at = np.flatnonzero(frames >= 0)
    runs: list[tuple[int, int, int]] = []
    for i in at:
        label = int(frames[i])
        if runs and runs[-1][2] == label:
            runs[-1] = (runs[-1][0], int(i) + 1, label)
        else:
            runs.append((int(i), int(i) + 1, label))
    return runs


def _without_short_turns(frames: np.ndarray, speech: np.ndarray, cores: list[tuple[int, int]],
                         similarity: np.ndarray) -> np.ndarray:
    """A turn shorter than MIN_TURN joins the turn beside it whose voice its windows are nearer, the shortest first,
    until none is left or only one turn is."""
    frames = frames.copy()
    while True:
        runs = _runs(frames)
        short = [(end - start, k) for k, (start, end, _) in enumerate(runs) if end - start < MIN_TURN]
        if len(runs) < 2 or not short:
            return frames
        _, k = min(short)
        start, end, _ = runs[k]
        beside = [runs[j][2] for j in (k - 1, k + 1) if 0 <= j < len(runs)]
        over = [i for i, (low, high) in enumerate(cores) if low < end and start < high]
        nearness = similarity[over].mean(axis=0) if over else np.zeros(similarity.shape[1])
        label = max(beside, key=lambda b: (nearness[b], -beside.index(b)))
        span = frames[start:end]
        span[(span >= 0) & speech[start:end]] = label


def _turns(frames: np.ndarray, order: dict[int, int], samples: int) -> tuple[Turn, ...]:
    """The turns in samples: from EDGE frames before each one's first speech frame to EDGE frames after its last, never
    into the turn beside it."""
    runs = _runs(frames)
    turns = []
    for k, (start, end, label) in enumerate(runs):
        low = max(start - EDGE, runs[k - 1][1] if k else 0)
        high = min(end + EDGE, runs[k + 1][0] if k + 1 < len(runs) else frames.size + EDGE)
        turns.append(Turn(low * segmenter.HOP, min(high * segmenter.HOP + segmenter.WINDOW - segmenter.HOP, samples),
                          order[label]))
    # Turns meet but don't overlap.
    for k in range(1, len(turns)):
        if turns[k].start < turns[k - 1].end:
            turns[k - 1] = Turn(turns[k - 1].start, turns[k].start, turns[k - 1].voice)
    return tuple(turns)
