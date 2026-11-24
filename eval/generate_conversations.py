# /// script
# requires-python = ">=3.12,<3.13"
# dependencies = [
#     "kokoro==0.9.4",
#     "misaki[en]==0.9.4",
#     "en-core-web-sm @ https://github.com/explosion/spacy-models/releases/download/en_core_web_sm-3.8.0/en_core_web_sm-3.8.0-py3-none-any.whl",
#     "huggingface_hub>=2.2,<3",
#     "numpy>=2.5,<3",
#     "torch>=2.14,<3",
# ]
# ///
"""Generates the audio of the eval's synthetic visits (eval/conversations, for listening to a visit: the expansion
plan's step 16) into eval/audio/conversations (gitignored), with a manifest.json for the scoring.

    uv run eval/generate_conversations.py                    # every visit's recordings
    uv run eval/generate_conversations.py --only c03 c11     # those visits'
    uv run eval/generate_conversations.py --clean-only       # no room or babble variants
    uv run eval/generate_conversations.py --skip-existing    # keep recordings made from the same text and versions
    uv run eval/generate_conversations.py --dry-run          # list the recordings; make nothing

It imports generate_audio.py for everything but the timeline, so the dictation set's recordings, and their hash, stay
as they are: the same Kokoro-82M at the revision voices.toml pins (already in the Hugging Face cache once the dictation
audio has been made, with every voice a visit names: nothing new is downloaded), the same pace, level, seeds, thread
count, noise recipes and containers. It needs what generate_audio.py needs, ffmpeg with libopus among it.

Each turn is read by its speaker's voice (conversation-voices.toml) at the dictation set's speech level, and laid on one
timeline: after the turn before it ends and its gap (conversation_scripts.DEFAULT_GAP when it gives none), or `overlap`
seconds before that turn ends, the two then heard at once. Every visit gets a clean recording, and the variants its
`noise` lists (conversation-voices.toml says how each is made): room, the patient further from the microphone in a
reverberant room with ventilation; babble, other voices through the door. Recordings alternate between WebM with Opus
(Chromium) and fragmented MP4 with AAC (WebKit). The manifest lists each recording with the role, start and end of
every turn, in seconds into the recording: what the scoring measures who-spoke-when, and each speaker's words, against.
No visit may be longer than the sidecar's limit; the generator stops if one would be.
"""

from __future__ import annotations

import argparse
import hashlib
import json
import math
import sys
import time
import tomllib
from dataclasses import dataclass
from pathlib import Path

import numpy as np

import conversation_scripts as cs
import generate_audio as ga

OUT_DIR = ga.OUT_DIR / "conversations"
MANIFEST = OUT_DIR / "manifest.json"
VOICES_FILE = ga.EVAL_DIR / "conversation-voices.toml"
BLOCK = 1 << 16  # samples a block, for the room's reverberation (overlap-add)


@dataclass(frozen=True)
class Recording:
    visit: cs.Conversation
    noise: str | None
    container: str

    @property
    def id(self) -> str:
        return f"{self.visit.id}--{self.noise or 'clean'}"

    @property
    def file(self) -> str:
        return f"{self.id}.{self.container}"


def plan(visits: list[cs.Conversation]) -> list[Recording]:
    """Every recording, visit by visit: clean, then room and babble where the visit lists them, the containers
    alternating down the list."""
    wanted = [(visit, noise) for visit in visits for noise in (None, *(n for n in ("room", "babble") if n in visit.noise))]
    return [Recording(visit, noise, ga.CONTAINER_ORDER[i % len(ga.CONTAINER_ORDER)]) for i, (visit, noise) in enumerate(wanted)]


def timeline(visit: cs.Conversation, clips: list[np.ndarray]) -> tuple[list[tuple[int, int]], int]:
    """Where each turn starts and ends, in samples, and the recording's length, with silence before and after."""
    rate = ga.RATE
    places: list[tuple[int, int]] = []
    last_end = int(ga.LEAD_IN * rate)
    for turn, clip in zip(visit.turns, clips, strict=True):
        if not places:
            start = last_end
        elif turn.overlap is not None:
            start = max(places[-1][0], places[-1][1] - int(turn.overlap * rate))
        else:
            start = last_end + int((cs.DEFAULT_GAP if turn.gap is None else turn.gap) * rate)
        places.append((start, start + len(clip)))
        last_end = max(last_end, start + len(clip))
    return places, last_end + int(ga.TAIL * rate)


def lay(clips: list[np.ndarray], places: list[tuple[int, int]], gains: list[float], length: int) -> np.ndarray:
    out = np.zeros(length)
    for clip, (start, end), gain in zip(clips, places, gains, strict=True):
        out[start:end] += clip * gain
    return out


def reverberate(dry: np.ndarray, rng: np.random.Generator, rt60: float, wet_db: float) -> np.ndarray:
    """The dry mix with a synthetic reverberant tail: noise decaying 60 dB in rt60 seconds, convolved block by block,
    at wet_db below the dry mix."""
    t = np.arange(int(rt60 * ga.RATE)) / ga.RATE
    response = rng.standard_normal(t.size) * np.exp(-6.9 * t / rt60)
    size = 1 << math.ceil(math.log2(BLOCK + response.size))
    kernel = np.fft.rfft(response, size)
    wet = np.zeros(dry.size + response.size)
    for start in range(0, dry.size, BLOCK):
        block = dry[start : start + BLOCK]
        wet[start : start + size] += np.fft.irfft(np.fft.rfft(block, size) * kernel, size)[: wet.size - start]
    wet = wet[: dry.size]
    return dry + wet * (ga.rms(dry) / max(ga.rms(wet), 1e-12)) * 10 ** (wet_db / 20)


def talkers(speaker: ga.Voices, voices: dict, babble: dict, visit: cs.Conversation, made: dict) -> list[np.ndarray]:
    """The babble's voices: `talkers` of those the visit doesn't use, chosen with the visit's seed, each reading one of
    voices.toml's babble texts. Made once a run each."""
    unused = sorted(set(voices["voices"]) - set(visit.voices.values()))
    rng = np.random.default_rng(ga.seed_for(f"babble:{visit.id}"))
    chosen = [unused[i] for i in rng.permutation(len(unused))[: voices["babble"]["talkers"]]]
    out = []
    for number, voice in enumerate(chosen):
        text = babble["texts"][number % len(babble["texts"])]
        if (voice, text) not in made:
            english = voices["voices"][voice]["english"]
            made[(voice, text)] = ga.at_level(speaker.speak(text, voice, english, ga.seed_for(f"babble:{voice}")))
        out.append(made[(voice, text)])
    return out


def text_sha256(visit: cs.Conversation) -> str:
    said = [f"{turn.role}:{visit.voices[turn.role]}:{turn.gap}:{turn.overlap}:{turn.spoken}" for turn in visit.turns]
    return hashlib.sha256("\n".join(said).encode()).hexdigest()


def parse_arguments(argv: list[str] | None) -> argparse.Namespace:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--only", nargs="+", metavar="ID", help="visit ids or numbers, such as c03")
    parser.add_argument("--clean-only", action="store_true", help="make the clean recordings only")
    parser.add_argument("--skip-existing", action="store_true", help="keep recordings made from the same text and versions")
    parser.add_argument("--dry-run", action="store_true", help="list the recordings; make nothing")
    return parser.parse_args(argv)


def run(args: argparse.Namespace) -> None:
    started = time.monotonic()
    dictation = tomllib.loads(ga.VOICES_FILE.read_text(encoding="utf-8"))
    voices = tomllib.loads(VOICES_FILE.read_text(encoding="utf-8"))
    visits = cs.load_conversations()
    for visit in visits:
        if not set(visit.voices.values()) <= set(voices["voices"]):
            raise ga.GenerationError(f"{visit.id} names a voice conversation-voices.toml doesn't list.")
    every = plan(visits)
    chosen = {v.id for v in visits if not args.only or any(v.id == w or v.id.startswith(f"{w}-") for w in args.only)}
    wanted = [r for r in every if r.visit.id in chosen and not (args.clean_only and r.noise)]
    if args.dry_run:
        for recording in wanted:
            print(f"{recording.file:52} {recording.visit.speakers} voices {recording.visit.words:5} words "
                  f"~{recording.visit.minutes:4.1f} min")
        print(f"{len(wanted)} of {len(every)} recordings.")
        return

    current = ga.provenance(dictation, ga.ffmpeg_version()) | {
        "script": "eval/generate_conversations.py",
        "script_sha256": ga.sha256_of(Path(__file__)),
        "generate_audio_sha256": ga.sha256_of(ga.EVAL_DIR / "generate_audio.py"),
        "conversation_voices_sha256": ga.sha256_of(VOICES_FILE),
        "room": voices["room"],
        "babble": voices["babble"],
    }
    OUT_DIR.mkdir(parents=True, exist_ok=True)
    manifest = json.loads(MANIFEST.read_text(encoding="utf-8")) if MANIFEST.is_file() else {}
    same = {k: v for k, v in manifest.get("generator", {}).items() if k != "items"} == current
    entries = {item["id"]: item for item in manifest.get("variants", [])} if same else {}
    details = manifest.get("generator", {}).get("items", {}) if same else {}
    if manifest and not same:
        print("The generators, a voices file or a package version changed: recordings from earlier runs are dropped.")

    print(f"Loading {dictation['model']['name']} ({dictation['model']['repository']} at {dictation['model']['revision'][:12]})")
    speaker = ga.Voices(dictation["model"])
    made_talkers: dict = {}
    order = {recording.id: index for index, recording in enumerate(every)}

    def save() -> list[dict]:
        kept = sorted((e for e in entries.values() if e["id"] in order), key=lambda e: order[e["id"]])
        partial = MANIFEST.with_suffix(".part")
        partial.write_text(json.dumps({"generator": current | {"items": {e["id"]: details[e["id"]] for e in kept}},
                                       "variants": kept}, indent=2) + "\n", encoding="utf-8")
        partial.replace(MANIFEST)
        return kept

    made = 0
    for visit in [v for v in visits if v.id in {r.visit.id for r in wanted}]:
        mine = [r for r in wanted if r.visit is visit]
        if args.skip_existing:
            mine = [r for r in mine if not (r.id in entries and details.get(r.id, {}).get("text_sha256") == text_sha256(visit)
                                            and (OUT_DIR / r.file).is_file()
                                            and ga.sha256_of(OUT_DIR / r.file) == entries[r.id]["sha256"])]
        if not mine:
            continue
        clips = []
        for number, turn in enumerate(visit.turns):
            voice = visit.voices[turn.role]
            clips.append(ga.at_level(speaker.speak(turn.spoken, voice, voices["voices"][voice]["english"],
                                                   ga.seed_for(f"speech:{visit.id}:{number}"))))
        places, length = timeline(visit, clips)
        if length > voices["limits"]["max_seconds"] * ga.RATE:
            raise ga.GenerationError(f"{visit.id} reads in {length / ga.RATE:.0f} s, over the {voices['limits']['max_seconds']} s limit.")
        turns = [{"role": turn.role, "start": round(start / ga.RATE, 3), "end": round(end / ga.RATE, 3)}
                 for turn, (start, end) in zip(visit.turns, places, strict=True)]
        for recording in mine:
            measured = None
            if recording.noise is None:
                samples = lay(clips, places, [1.0] * len(clips), length)
                samples = samples * ga.headroom(samples)
            elif recording.noise == "room":
                room = voices["room"]
                gains = [10 ** (room[f"{turn.role}_db"] / 20) if turn.role != "clinician" else 1.0 for turn in visit.turns]
                rng = np.random.default_rng(ga.seed_for(f"room:{visit.id}"))
                speech = reverberate(lay(clips, places, gains, length), rng, room["rt60_seconds"], room["wet_db"])
                hvac = dictation["noise"]["hvac"]
                noise = ga.pink(rng, length, hvac["lowpass_hz"], hvac["lowpass_order"])
                samples, measured = ga.mix(speech, noise, room["snr_db"])
            else:
                rng = np.random.default_rng(ga.seed_for(f"babble-noise:{visit.id}"))
                noise = ga.babble(rng, length, talkers(speaker, voices, dictation["noise"]["babble"], visit, made_talkers))
                samples, measured = ga.mix(lay(clips, places, [1.0] * len(clips), length), noise, voices["babble"]["snr_db"])
            path = OUT_DIR / recording.file
            ga.encode(samples, recording.container, path)
            seconds = round(length / ga.RATE, 2)
            entries[recording.id] = {
                "id": recording.id, "script": visit.id, "file": recording.file, "container": recording.container,
                "voices": visit.voices, "noise": recording.noise,
                "snr_db": None if recording.noise is None else voices[recording.noise]["snr_db"],
                "seconds": seconds, "sha256": ga.sha256_of(path), "turns": turns,
            }
            details[recording.id] = {"text_sha256": text_sha256(visit), "words": visit.words,
                                     "snr_db_measured": None if measured is None else round(measured, 2)}
            made += 1
            save()
            print(f"  {recording.file:52} {seconds:7.1f} s" + ("" if measured is None else f"  SNR {measured:5.2f} dB"))

    kept = save()
    total = sum(e["seconds"] for e in kept)
    size = sum((OUT_DIR / e["file"]).stat().st_size for e in kept)
    print(f"\nMade {made} recording(s) in {time.monotonic() - started:.0f} s. The manifest lists {len(kept)} of "
          f"{len(every)}: {total / 60:.1f} minutes of audio, {size / 1e6:.1f} MB, in {OUT_DIR}.")


def main(argv: list[str] | None = None) -> int:
    try:
        run(parse_arguments(argv))
    except (ga.GenerationError, cs.ScriptError) as error:
        print(f"error: {error}", file=sys.stderr)
        return 1
    except KeyboardInterrupt:
        print("\nStopped. Finished recordings are kept; run again with --skip-existing to carry on.", file=sys.stderr)
        return 130
    return 0


if __name__ == "__main__":
    sys.exit(main())
