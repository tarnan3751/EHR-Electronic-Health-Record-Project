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
"""Generates the dictation test set's audio from its scripts, with a synthetic voice, into eval/audio (gitignored).

    uv run eval/generate_audio.py                      # every item
    uv run eval/generate_audio.py --only d07 d12       # those scripts' items
    uv run eval/generate_audio.py --clean-only         # no noisy variants
    uv run eval/generate_audio.py --skip-existing      # keep items already made from the same text and versions
    uv run eval/generate_audio.py --dry-run            # list the items; make nothing
    uv run eval/generate_audio.py --set es             # the Spanish test set (any option above applies to it too)

Each script's spoken text is read by the voice it names (eval/voices.toml) with Kokoro-82M, at the revision pinned
there. That makes one clean item per script. A script that lists `noise` also gets that noise mixed in at each SNR in
voices.toml, measured over the frames where the speech is active, and a few get one more, too noisy to transcribe.
Items alternate between the containers browsers record, WebM with Opus (Chromium) and fragmented MP4 with AAC
(WebKit), so the pipeline is tested on both. eval/audio/manifest.json lists every item for the scoring.

--set es makes the Spanish test set's (eval/scripts-es, the expansion plan's step 17) the same way, into eval/audio-es
with its own manifest: each script read in Spanish by its voice (voices.toml's [voices_es]), its passages said in
English read in English, its noise at the SNRs in [variants_es], no item over a Spanish dictation's 2 minutes; and two
items dictated in the wrong language, an English script read in English to be run as Spanish, and a Spanish script's
clean item to be run as English. The English set's items, and how they are made, are unchanged.

The first run downloads the model (about 330 MB) and the voices from Hugging Face into its cache, and checks the model
against the SHA-256 its model card publishes. After that it runs offline. It needs ffmpeg on the PATH, with libopus.

Seeds and the CPU thread count are fixed, so the same machine with the same package versions makes the same files,
byte for byte. Another machine (another CPU, or another build of torch or ffmpeg) makes audio that sounds the same but
differs in the last bits, so its SHA-256 values differ: compare items by their manifest, not by hash across machines.
"""

from __future__ import annotations

import argparse
import hashlib
import json
import math
import os
import platform
import re
import shutil
import subprocess
import sys
import time
import tomllib
import warnings
from dataclasses import dataclass
from importlib import metadata
from pathlib import Path

# Set before huggingface_hub is imported, which reads it once.
os.environ.setdefault("HF_HUB_DISABLE_TELEMETRY", "1")

import numpy as np  # noqa: E402
import torch  # noqa: E402
from huggingface_hub import hf_hub_download  # noqa: E402
from huggingface_hub.errors import LocalEntryNotFoundError  # noqa: E402
from kokoro import KModel, KPipeline  # noqa: E402

import dictation_scripts as ds  # noqa: E402  (beside this file)
import dictation_scripts_es as dsx  # noqa: E402  (beside this file)

EVAL_DIR = Path(__file__).resolve().parent
OUT_DIR = EVAL_DIR / "audio"
MANIFEST = OUT_DIR / "manifest.json"
OUT_DIR_ES = EVAL_DIR / "audio-es"
VOICES_FILE = EVAL_DIR / "voices.toml"

SEED = 1
THREADS = 4  # torch splits its sums by thread count; a fixed count keeps the output from depending on the cores
SPEED = 1.1  # Kokoro's speed: about 150 words a minute, a typical dictating pace
RATE = 24_000  # Kokoro's sample rate; ffmpeg resamples to 48 kHz, as browsers record
FRAME = RATE // 50  # 20 ms frames for levels and SNR
SPEECH_DBFS = -26.0  # level of the active speech in every item
ACTIVE_WITHIN_DB = 30.0  # a frame is speech-active within this of the loud (95th percentile) frames
LEAD_IN, TAIL, GAP = 0.5, 0.7, 0.25  # seconds of silence before, after and between synthesized chunks
MAX_SECONDS = 180  # the dictation session limit: no item may be longer
MAX_SECONDS_ES = 120  # a dictation in Spanish's limit (step 17, decision 20)
MAX_PHONEMES = 510  # Kokoro cuts a piece's phonemes here: a Spanish piece that long would lose words
CHUNK_CHARS = 250  # Kokoro reads best in pieces of about 100 to 250 phonemes
PINK_FROM_HZ = 20.0  # the bottom of hearing

CONTAINERS = {
    # Chromium's MediaRecorder writes WebM with Opus, as a live stream: no duration or index in the header.
    "webm": ["-c:a", "libopus", "-b:a", "64k", "-f", "webm", "-live", "1"],
    # WebKit's writes fragmented MP4 with AAC.
    "mp4": ["-c:a", "aac", "-b:a", "96k", "-f", "mp4", "-movflags", "+empty_moov+default_base_moof",
            "-frag_duration", "1000000"],
}  # fmt: skip
CONTAINER_ORDER = ["webm", "mp4"]


class GenerationError(Exception):
    """A problem the person running the script can fix. Printed without a traceback."""


def seed_for(name: str) -> int:
    """A seed for one use (a script's speech, a variant's noise) that doesn't depend on what else is generated."""
    return int.from_bytes(hashlib.sha256(f"{SEED}:{name}".encode()).digest()[:8], "big") >> 1


def sha256_of(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as file:
        while chunk := file.read(1 << 20):
            digest.update(chunk)
    return digest.hexdigest()


# ---- Plan ----------------------------------------------------------------------------------------------------------


@dataclass(frozen=True)
class Variant:
    script: ds.Script | dsx.Script
    noise: str | None
    snr_db: int | None
    container: str
    run_as: str | None = None  # a Spanish set's item dictated in the wrong language: "es" for an English script

    @property
    def id(self) -> str:
        base = f"{self.script.id}--clean" if self.noise is None else f"{self.script.id}--{self.noise}-{self.snr_db}db"
        return base + (f"--as-{self.run_as}" if self.run_as else "")

    @property
    def file(self) -> str:
        return f"{self.id}.{self.container}"


def plan(scripts: list[ds.Script], variants: dict) -> list[Variant]:
    """Every item, in script order. Containers alternate down each column (the clean items, then each SNR), starting
    one further along in each, so every column and every script's noisy series has both."""
    columns = [[(script, None, None) for script in scripts]]
    for snr in variants["snr_db"]:
        columns.append([(script, noise, snr) for script in scripts for noise in script.noise])
    unusable = set(variants["unusable"])
    columns.append([(s, s.noise[0], variants["unusable_snr_db"]) for s in scripts if s.id in unusable])

    items = []
    for level, column in enumerate(columns):
        for position, (script, noise, snr) in enumerate(column):
            container = CONTAINER_ORDER[(position + level) % len(CONTAINER_ORDER)]
            items.append((script.id, level, Variant(script, noise, snr, container)))
    return [variant for _, _, variant in sorted(items, key=lambda item: item[:2])]


# ---- Speech --------------------------------------------------------------------------------------------------------


def fetch(repository: str, filename: str, revision: str) -> str:
    """A file from the Hugging Face cache, downloaded the first time only."""
    try:
        return hf_hub_download(repository, filename, revision=revision, local_files_only=True)
    except LocalEntryNotFoundError:
        print(f"  downloading {filename} from {repository} (once; it's cached after this)")
    try:
        return hf_hub_download(repository, filename, revision=revision)
    except Exception as error:
        raise GenerationError(f"Couldn't download {filename} from {repository}: {error}") from error


class Voices:
    """Kokoro at the pinned revision, with one English pipeline per pronunciation (us, gb) and the voices loaded."""

    def __init__(self, model: dict) -> None:
        self.repository, self.revision = model["repository"], model["revision"]
        config = fetch(self.repository, model["config"], self.revision)
        weights = fetch(self.repository, model["file"], self.revision)
        if sha256_of(Path(weights)) != model["sha256"]:
            raise GenerationError(f"{weights} doesn't match the SHA-256 in voices.toml. Delete it and run again.")
        torch.set_num_threads(THREADS)
        with warnings.catch_warnings():
            warnings.simplefilter("ignore")  # torch deprecation notices about how Kokoro and spaCy build their layers
            self.model = KModel(repo_id=self.repository, config=config, model=weights).eval()
            self.pipelines = {
                english: KPipeline(lang_code=code, repo_id=self.repository, model=self.model)
                for english, code in (("us", "a"), ("gb", "b"))
            }
        if any(pipeline.g2p.fallback is None for pipeline in self.pipelines.values()):
            raise GenerationError("espeak-ng didn't load, so words outside misaki's dictionary would be skipped.")
        self.spanish: KPipeline | None = None

    def speak_runs(self, runs: list[tuple[str, str]], voice: str, seed: int) -> np.ndarray:
        """A Spanish script read aloud, at 24 kHz: its Spanish with Kokoro's Spanish pipeline (espeak-ng's Spanish
        rules) and its passages said in English with the American English one, in the same voice, in chunks joined by
        short pauses."""
        if self.spanish is None:
            with warnings.catch_warnings():
                warnings.simplefilter("ignore")  # Kokoro's notice that its Spanish pipeline doesn't chunk long texts
                self.spanish = KPipeline(lang_code="e", repo_id=self.repository, model=self.model)
        voice_file = fetch(self.repository, f"voices/{voice}.pt", self.revision)
        torch.manual_seed(seed)
        pieces = []
        for language, text in runs:
            pipeline = self.spanish if language == "es" else self.pipelines["us"]
            for chunk in chunks(text):
                for result in pipeline(chunk, voice=voice_file, speed=SPEED, split_pattern=None):
                    if language == "es" and len(result.phonemes) >= MAX_PHONEMES:
                        raise GenerationError(f"A piece of {voice}'s Spanish is too long for Kokoro: {chunk!r}.")
                    silent = [t.text for t in result.tokens or [] if re.search("[A-Za-z]", t.text) and not t.phonemes]
                    if silent:
                        raise GenerationError(f"The voice has no pronunciation for {silent}; is espeak-ng installed?")
                    pieces += [result.audio.numpy(), np.zeros(int(GAP * RATE), dtype=np.float32)]
        return np.concatenate(pieces[:-1])

    def speak(self, text: str, voice: str, english: str, seed: int) -> np.ndarray:
        """The text read aloud, at 24 kHz, in chunks joined by short pauses."""
        pipeline = self.pipelines[english]
        voice_file = fetch(self.repository, f"voices/{voice}.pt", self.revision)
        torch.manual_seed(seed)
        pieces = []
        for chunk in chunks(text):
            for result in pipeline(chunk, voice=voice_file, speed=SPEED, split_pattern=None):
                silent = [t.text for t in result.tokens if re.search("[A-Za-z]", t.text) and not t.phonemes]
                if silent:
                    raise GenerationError(f"The voice has no pronunciation for {silent}; is espeak-ng installed?")
                pieces += [result.audio.numpy(), np.zeros(int(GAP * RATE), dtype=np.float32)]
        return np.concatenate(pieces[:-1])


def chunks(text: str) -> list[str]:
    """The text in pieces of about CHUNK_CHARS, broken after a pause mark where there is one, else between words."""
    pieces = []
    for piece in re.split(r"(?<=[.,]) ", text):
        while len(piece) > CHUNK_CHARS:
            cut = piece.rindex(" ", 0, CHUNK_CHARS)
            pieces.append(piece[:cut])
            piece = piece[cut + 1 :]
        pieces.append(piece)
    target = len(text) / math.ceil(len(text) / CHUNK_CHARS)
    out = [""]
    for piece in pieces:
        if out[-1] and len(out[-1]) + 1 + len(piece) > target:
            out.append(piece)
        else:
            out[-1] = f"{out[-1]} {piece}".strip()
    return out


# ---- Levels and noise ----------------------------------------------------------------------------------------------


def frame_power(signal: np.ndarray) -> np.ndarray:
    frames = len(signal) // FRAME
    return np.mean(signal[: frames * FRAME].reshape(frames, FRAME).astype(np.float64) ** 2, axis=1)


def active_frames(speech: np.ndarray) -> np.ndarray:
    power = frame_power(speech)
    return power >= np.percentile(power, 95) * 10 ** (-ACTIVE_WITHIN_DB / 10)


def at_level(speech: np.ndarray) -> np.ndarray:
    """Speech scaled so its active frames are at SPEECH_DBFS."""
    speech = speech.astype(np.float64)
    return speech * math.sqrt(10 ** (SPEECH_DBFS / 10) / frame_power(speech)[active_frames(speech)].mean())


def place(speech: np.ndarray) -> np.ndarray:
    """Speech at SPEECH_DBFS, with silence before and after."""
    return np.concatenate([np.zeros(int(LEAD_IN * RATE)), at_level(speech), np.zeros(int(TAIL * RATE))])


def headroom(signal: np.ndarray) -> float:
    """The scale that keeps a signal's peaks below full scale."""
    return min(1.0, 0.98 / float(np.max(np.abs(signal))))


def rms(signal: np.ndarray) -> float:
    return float(np.sqrt(np.mean(signal**2)))


def pink(rng: np.random.Generator, length: int, lowpass_hz: float | None = None, order: int = 2) -> np.ndarray:
    """Pink noise (power falling as 1/f) from PINK_FROM_HZ up, optionally low-passed, at an RMS of 1. Below that
    it would be mostly inaudible rumble, which codecs drop, and the SNR would overstate the noise heard."""
    spectrum = np.fft.rfft(rng.standard_normal(length))
    frequency = np.fft.rfftfreq(length, 1 / RATE)
    audible = frequency >= PINK_FROM_HZ
    spectrum[audible] /= np.sqrt(frequency[audible])
    spectrum[~audible] = 0
    if lowpass_hz:
        spectrum /= np.sqrt(1 + (frequency / lowpass_hz) ** (2 * order))
    noise = np.fft.irfft(spectrum, length)
    return noise / rms(noise)


def monitor(rng: np.random.Generator, length: int, recipe: dict) -> np.ndarray:
    """Pulse beeps (a tone with its 2nd and 3rd harmonics) at a jittered rate, over a pink floor."""
    pitch = rng.uniform(*recipe["beep_hz"])
    interval = 60 / rng.uniform(*recipe["beats_per_minute"])
    t = np.arange(int(recipe["beep_seconds"] * RATE)) / RATE
    tone = sum(weight * np.sin(2 * np.pi * harmonic * pitch * t) for harmonic, weight in ((1, 1), (2, 0.3), (3, 0.15)))
    ramp = min(len(t) // 2, int(0.005 * RATE))
    tone[:ramp] *= np.linspace(0, 1, ramp)
    tone[-ramp:] *= np.linspace(1, 0, ramp)
    beeps = np.zeros(length)
    start = rng.uniform(0, interval)
    while (first := int(start * RATE)) < length:
        end = min(length, first + len(tone))
        beeps[first:end] += tone[: end - first]
        start += interval * (1 + rng.uniform(-0.03, 0.03))
    floor = pink(rng, length) * rms(tone) * 10 ** (recipe["floor_db"] / 20)
    noise = beeps + floor
    return noise / rms(noise)


def babble(rng: np.random.Generator, length: int, talkers: list[np.ndarray]) -> np.ndarray:
    """Every talker at once, each looped from its own random starting point."""
    noise = np.zeros(length)
    for talker in talkers:
        start = int(rng.integers(len(talker)))
        noise += np.tile(talker, math.ceil((start + length) / len(talker)))[start : start + length]
    return noise / rms(noise)


def mix(speech: np.ndarray, noise: np.ndarray, snr_db: float) -> tuple[np.ndarray, float]:
    """Speech plus noise at snr_db, measured over the speech-active frames. Scaled down together if it would clip."""
    active = active_frames(speech)
    gain = math.sqrt(frame_power(speech)[active].mean() / (frame_power(noise)[active].mean() * 10 ** (snr_db / 10)))
    noise = noise * gain
    scale = headroom(speech + noise)
    speech, noise = speech * scale, noise * scale
    measured = 10 * math.log10(frame_power(speech)[active].mean() / frame_power(noise)[active].mean())
    return speech + noise, measured


# ---- Encoding ------------------------------------------------------------------------------------------------------


def ffmpeg_version() -> str:
    if not shutil.which("ffmpeg"):
        raise GenerationError("ffmpeg isn't on the PATH: brew install ffmpeg, or winget install Gyan.FFmpeg.")
    encoders = subprocess.run(["ffmpeg", "-hide_banner", "-encoders"], capture_output=True, text=True).stdout
    if "libopus" not in encoders:
        raise GenerationError("This ffmpeg has no libopus encoder, which WebM audio needs. Install a full build.")
    first = subprocess.run(["ffmpeg", "-version"], capture_output=True, text=True).stdout.splitlines()[0]
    return first.split()[2]


def encode(samples: np.ndarray, container: str, path: Path) -> None:
    """Encodes 24 kHz mono samples at 48 kHz, as a browser would record them. Bit-exact flags keep ffmpeg from
    writing its version or random IDs, so the same input gives the same file."""
    partial = path.with_name(path.name + ".part")
    command = [
        "ffmpeg", "-hide_banner", "-loglevel", "error", "-y",
        "-f", "f32le", "-ar", str(RATE), "-ac", "1", "-i", "pipe:0",
        "-ar", "48000", "-map_metadata", "-1", "-fflags", "+bitexact", "-flags:a", "+bitexact",
        *CONTAINERS[container], str(partial),
    ]  # fmt: skip
    result = subprocess.run(command, input=samples.astype("<f4").tobytes(), capture_output=True)
    if result.returncode:
        raise GenerationError(f"ffmpeg couldn't write {path.name}: {result.stderr.decode(errors='replace')}")
    partial.replace(path)


# ---- Manifest ------------------------------------------------------------------------------------------------------


def provenance(voices: dict, ffmpeg: str) -> dict:
    """What made the audio: every item in a manifest was made with exactly this."""
    packages = [
        "kokoro", "misaki", "torch", "numpy", "transformers", "spacy", "en-core-web-sm", "phonemizer-fork",
        "espeakng-loader", "huggingface-hub",
    ]  # fmt: skip
    return {
        "script": "eval/generate_audio.py",
        "script_sha256": sha256_of(Path(__file__)),
        "voices_sha256": sha256_of(VOICES_FILE),
        "seed": SEED,
        "threads": THREADS,
        "speed": SPEED,
        "tts": {key: voices["model"][key] for key in ("name", "repository", "revision", "file", "sha256", "license")},
        "packages": {name: metadata.version(name) for name in packages},
        "ffmpeg": ffmpeg,
        "platform": f"{platform.system()} {platform.machine()}",
        "sample_rate": {"synthesized": RATE, "encoded": 48_000},
        "speech_level_dbfs": SPEECH_DBFS,
        "containers": {name: " ".join(args) for name, args in CONTAINERS.items()},
        "voices": voices["voices"],
        "noise": voices["noise"],
    }


def load_manifest(path: Path = MANIFEST) -> dict:
    if not path.is_file():
        return {}
    try:
        return json.loads(path.read_text(encoding="utf-8"))
    except json.JSONDecodeError:
        return {}


def save_manifest(manifest: dict, path: Path = MANIFEST) -> None:
    partial = path.with_suffix(".part")
    partial.write_text(json.dumps(manifest, indent=2) + "\n", encoding="utf-8")
    partial.replace(path)


def entry(variant: Variant, accent: str, seconds: float, sha256: str) -> dict:
    """One item, as the scoring reads it. Its tags come from the script and these fields."""
    return {
        "id": variant.id,
        "script": variant.script.id,
        "file": variant.file,
        "container": variant.container,
        "voice": variant.script.voice,
        "accent": accent,
        "noise": variant.noise,
        "snr_db": variant.snr_db,
        "seconds": seconds,
        "sha256": sha256,
    }


def text_sha256(script: ds.Script | dsx.Script) -> str:
    spoken = (dsx if isinstance(script, dsx.Script) else ds).spoken_text(script.text, pauses=True)
    return hashlib.sha256(f"{script.voice}\n{spoken}".encode()).hexdigest()


def up_to_date(variant: Variant, old: dict, details: dict, out_dir: Path = OUT_DIR) -> bool:
    """Whether an item from an earlier run, made with the same versions, was made from this text and is intact."""
    path = out_dir / variant.file
    return (
        old.get("file") == variant.file
        and old.get("noise") == variant.noise
        and old.get("snr_db") == variant.snr_db
        and details.get("text_sha256") == text_sha256(variant.script)
        and path.is_file()
        and sha256_of(path) == old.get("sha256")
    )


# ---- Command line --------------------------------------------------------------------------------------------------


def parse_arguments(argv: list[str] | None) -> argparse.Namespace:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--only", nargs="+", metavar="ID", help="script ids or numbers, such as d07")
    parser.add_argument("--clean-only", action="store_true", help="make the clean items only")
    parser.add_argument("--skip-existing", action="store_true", help="keep items made from the same text and versions")
    parser.add_argument("--dry-run", action="store_true", help="list the items; make nothing")
    parser.add_argument("--set", choices=["en", "es"], default="en", help="the English test set (default) or the Spanish")
    return parser.parse_args(argv)


def run(args: argparse.Namespace) -> None:
    if args.set == "es":
        run_spanish(args)
        return
    started = time.monotonic()
    voices = tomllib.loads(VOICES_FILE.read_text(encoding="utf-8"))
    scripts = ds.load_scripts()
    for script in scripts:
        if script.voice not in voices["voices"]:
            raise GenerationError(f"{script.id} names voice {script.voice}, which voices.toml doesn't list.")
    every = plan(scripts, voices["variants"])
    chosen = {s.id for s in ds.select(scripts, args.only)} if args.only else {s.id for s in scripts}
    wanted = [v for v in every if v.script.id in chosen and not (args.clean_only and v.noise)]

    if args.dry_run:
        for variant in wanted:
            print(f"{variant.file:52} {variant.script.voice:11} {variant.script.words:4} words")
        print(f"{len(wanted)} of {len(every)} items.")
        return

    current = provenance(voices, ffmpeg_version())
    OUT_DIR.mkdir(exist_ok=True)
    manifest = load_manifest()
    same_versions = {k: v for k, v in manifest.get("generator", {}).items() if k != "items"} == current
    old_entries = {item["id"]: item for item in manifest.get("variants", [])} if same_versions else {}
    old_details = manifest.get("generator", {}).get("items", {}) if same_versions else {}
    if manifest and not same_versions:
        print("The generator, voices.toml or a package version changed: items from earlier runs are dropped.")

    print(f"Loading {voices['model']['name']} ({voices['model']['repository']} at {voices['model']['revision'][:12]})")
    speaker = Voices(voices["model"])
    talkers: list[np.ndarray] = []

    entries, details, made = dict(old_entries), dict(old_details), 0
    order = {variant.id: index for index, variant in enumerate(every)}

    def save() -> list[dict]:
        kept = sorted((e for e in entries.values() if e["id"] in order), key=lambda e: order[e["id"]])
        save_manifest({"generator": current | {"items": {e["id"]: details[e["id"]] for e in kept}}, "variants": kept})
        return kept

    for script in [s for s in scripts if s.id in {v.script.id for v in wanted}]:
        mine = [v for v in wanted if v.script is script]
        if args.skip_existing:
            mine = [v for v in mine if not (v.id in entries and up_to_date(v, entries[v.id], details.get(v.id, {})))]
        if not mine:
            continue
        voice = voices["voices"][script.voice]
        text = ds.spoken_text(script.text, pauses=True)
        speech = place(speaker.speak(text, script.voice, voice["english"], seed_for(f"speech:{script.id}")))
        if len(speech) > MAX_SECONDS * RATE:
            raise GenerationError(f"{script.id} reads in {len(speech) / RATE:.0f} s, over the {MAX_SECONDS} s limit.")
        for variant in mine:
            if variant.noise is None:
                samples, measured = speech * headroom(speech), None
            else:
                rng = np.random.default_rng(seed_for(f"noise:{variant.id}"))
                recipe = voices["noise"][variant.noise]
                if variant.noise == "hvac":
                    noise = pink(rng, len(speech), recipe["lowpass_hz"], recipe["lowpass_order"])
                elif variant.noise == "monitor":
                    noise = monitor(rng, len(speech), recipe)
                else:
                    if not talkers:
                        talkers = babble_talkers(speaker, recipe)
                    noise = babble(rng, len(speech), talkers)
                samples, measured = mix(speech, noise, variant.snr_db)
            path = OUT_DIR / variant.file
            encode(samples, variant.container, path)
            seconds = round(len(samples) / RATE, 2)
            entries[variant.id] = entry(variant, voice["accent"], seconds, sha256_of(path))
            details[variant.id] = {
                "text_sha256": text_sha256(script),
                "words": script.words,
                "snr_db_measured": None if measured is None else round(measured, 2),
            }
            made += 1
            save()
            print(f"  {variant.file:52} {seconds:6.1f} s" + ("" if measured is None else f"  SNR {measured:5.2f} dB"))

    kept = save()
    total = sum(e["seconds"] for e in kept)
    size = sum((OUT_DIR / e["file"]).stat().st_size for e in kept)
    print(
        f"\nMade {made} item(s) in {time.monotonic() - started:.0f} s. The manifest lists {len(kept)} of {len(every)}: "
        f"{total / 60:.1f} minutes of audio, {size / 1e6:.1f} MB, in {OUT_DIR}."
    )


# ---- The Spanish set (the expansion plan's step 17) ----------------------------------------------------------------


def plan_spanish(scripts: list[dsx.Script], variants: dict, english: ds.Script) -> list[Variant]:
    """The Spanish set's items, made as plan() makes the English set's, then the English script read in English to be
    dictated as Spanish. The Spanish script dictated as English is its clean item, listed again in the manifest."""
    return plan(scripts, variants) + [Variant(english, None, None, CONTAINER_ORDER[0], run_as="es")]


def run_spanish(args: argparse.Namespace) -> None:
    started = time.monotonic()
    voices = tomllib.loads(VOICES_FILE.read_text(encoding="utf-8"))
    variants = voices["variants_es"]
    scripts = dsx.load_scripts()
    for script in scripts:
        if script.voice not in voices["voices_es"]:
            raise GenerationError(f"{script.id} names voice {script.voice}, which voices.toml's [voices_es] doesn't list.")
    english = next((s for s in ds.load_scripts() if s.id == variants["english_as_spanish"]), None)
    if english is None:
        raise GenerationError(f"voices.toml's english_as_spanish names {variants['english_as_spanish']}, which isn't a script.")
    every = plan_spanish(scripts, variants, english)
    chosen = {s.id for s in dsx.select(scripts, args.only)} if args.only else {s.id for s in scripts} | {english.id}
    wanted = [v for v in every if v.script.id in chosen and not (args.clean_only and v.noise)]
    as_english = f"{variants['spanish_as_english']}--clean"

    if args.dry_run:
        for variant in wanted:
            print(f"{variant.file:52} {variant.script.voice:11} {variant.script.words:4} words")
        print(f"{len(wanted)} of {len(every)} items, and {as_english} again, dictated as English.")
        return

    current = provenance(voices, ffmpeg_version())
    OUT_DIR_ES.mkdir(exist_ok=True)
    manifest_path = OUT_DIR_ES / "manifest.json"
    manifest = load_manifest(manifest_path)
    same_versions = {k: v for k, v in manifest.get("generator", {}).items() if k != "items"} == current
    old_entries = {item["id"]: item for item in manifest.get("variants", [])} if same_versions else {}
    old_details = manifest.get("generator", {}).get("items", {}) if same_versions else {}
    if manifest and not same_versions:
        print("The generator, voices.toml or a package version changed: items from earlier runs are dropped.")

    print(f"Loading {voices['model']['name']} ({voices['model']['repository']} at {voices['model']['revision'][:12]})")
    speaker = Voices(voices["model"])
    talkers: list[np.ndarray] = []

    entries, details, made = dict(old_entries), dict(old_details), 0
    order = {variant.id: index for index, variant in enumerate(every)} | {f"{as_english}--as-en": len(every)}

    def save() -> list[dict]:
        if as_english in entries:
            entries[f"{as_english}--as-en"] = entries[as_english] | {"id": f"{as_english}--as-en", "run_as": "en"}
            details[f"{as_english}--as-en"] = details[as_english]
        kept = sorted((e for e in entries.values() if e["id"] in order), key=lambda e: order[e["id"]])
        save_manifest({"generator": current | {"items": {e["id"]: details[e["id"]] for e in kept}}, "variants": kept},
                      manifest_path)
        return kept

    for script_id in dict.fromkeys(v.script.id for v in wanted):
        mine = [v for v in wanted if v.script.id == script_id]
        script = mine[0].script
        if args.skip_existing:
            mine = [v for v in mine
                    if not (v.id in entries and up_to_date(v, entries[v.id], details.get(v.id, {}), OUT_DIR_ES))]
        if not mine:
            continue
        seed = seed_for(f"speech:{script.id}")
        if isinstance(script, dsx.Script):
            accent = voices["voices_es"][script.voice]["accent"]
            speech = place(speaker.speak_runs(dsx.runs(script.text, pauses=True), script.voice, seed))
        else:
            voice = voices["voices"][script.voice]
            accent = voice["accent"]
            speech = place(speaker.speak(ds.spoken_text(script.text, pauses=True), script.voice, voice["english"], seed))
        if len(speech) > MAX_SECONDS_ES * RATE:
            raise GenerationError(f"{script.id} reads in {len(speech) / RATE:.0f} s, over the {MAX_SECONDS_ES} s limit.")
        for variant in mine:
            if variant.noise is None:
                samples, measured = speech * headroom(speech), None
            else:
                rng = np.random.default_rng(seed_for(f"noise:{variant.id}"))
                recipe = voices["noise"][variant.noise]
                if variant.noise == "hvac":
                    noise = pink(rng, len(speech), recipe["lowpass_hz"], recipe["lowpass_order"])
                elif variant.noise == "monitor":
                    noise = monitor(rng, len(speech), recipe)
                else:
                    if not talkers:
                        talkers = babble_talkers(speaker, recipe)
                    noise = babble(rng, len(speech), talkers)
                samples, measured = mix(speech, noise, variant.snr_db)
            path = OUT_DIR_ES / variant.file
            encode(samples, variant.container, path)
            seconds = round(len(samples) / RATE, 2)
            entries[variant.id] = entry(variant, accent, seconds, sha256_of(path)) | (
                {"run_as": variant.run_as} if variant.run_as else {})
            details[variant.id] = {
                "text_sha256": text_sha256(script),
                "words": script.words,
                "snr_db_measured": None if measured is None else round(measured, 2),
            }
            made += 1
            save()
            print(f"  {variant.file:52} {seconds:6.1f} s" + ("" if measured is None else f"  SNR {measured:5.2f} dB"))

    kept = save()
    total = sum(e["seconds"] for e in kept)
    size = sum((OUT_DIR_ES / e["file"]).stat().st_size for e in kept)
    print(
        f"\nMade {made} item(s) in {time.monotonic() - started:.0f} s. The manifest lists {len(kept)} of {len(order)}: "
        f"{total / 60:.1f} minutes of audio, {size / 1e6:.1f} MB, in {OUT_DIR_ES}."
    )


def babble_talkers(speaker: Voices, recipe: dict) -> list[np.ndarray]:
    """Each babble voice reading its text once, at equal level, with the pronunciation of its accent (b: British).
    Made once per run."""
    return [
        at_level(speaker.speak(text, voice, "gb" if voice.startswith("b") else "us", seed_for(f"babble:{voice}")))
        for voice, text in zip(recipe["voices"], recipe["texts"], strict=True)
    ]


def main(argv: list[str] | None = None) -> int:
    try:
        run(parse_arguments(argv))
    except (GenerationError, ds.ScriptError, dsx.ScriptError) as error:
        print(f"error: {error}", file=sys.stderr)
        return 1
    except KeyboardInterrupt:
        print("\nStopped. Finished items are kept; run again with --skip-existing to carry on.", file=sys.stderr)
        return 130
    return 0


if __name__ == "__main__":
    sys.exit(main())
