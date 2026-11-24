# /// script
# requires-python = ">=3.12"
# dependencies = []
# ///
"""The Spanish dictation test set's scripts (eval/scripts-es/*.toml, the expansion plan's step 17): loading them, and
rendering their text as said and as written. Beside dictation_scripts.py, which reads the English set and is left as
it is, so the English recordings stay what they were.

A clinician dictating in Spanish seldom says punctuation (Whisper writes it), so a script's `text` is written with its
punctuation as marks, and marked up only where speech and writing differ:

- `[[display|spoken]]`: what is written, then what is said, such as `[[142/88|ciento cuarenta y dos sobre ochenta y
  ocho]]` or `[[0,25 mg|cero coma veinticinco miligramos]]`. Every number is written this way, so the spoken text
  has no digits.
- `{punto y aparte}`, `{nuevo párrafo}`, `{punto y seguido}`: punctuation the clinician says aloud, written as a
  paragraph break or a full stop.
- `<<en|...>>`: words said in English, in a Spanish dictation; the audio generator reads them with an English voice.

The gold is a list of statements per field: the passages of the display text a statement comes from, word for word,
and the English a careful clinician would accept, which in the test set must pass the sidecar's Spanish checks
(translation.py; eval/tests/test_gold_es.py); the scripts run only when asked are held to their structure only, and a
gold statement of theirs the checks refuse is a false refusal (ehr_eval/refusals.py). Beside it, `facts` lists what each passage of the text says, written by hand, for the
scoring to compare each statement shown with what the Spanish it cites says without the checks' own readers
(ehr_eval/facts_es.py says the format; this reader keeps them as written). All of it is synthetic and needs a
Spanish-speaking clinician's reading before any real use.

More scripts, in eval/scripts-es-holdout, in the same format, are run only when asked (run_a.py --holdout). The first
five, h01 to h05, are seen (SEEN), not held out: written once the checks of run A's first round were settled, read once,
unseen, in run A2, regression items since, with changes made after seeing them (eval/README.md lists them), and the
owner's definition of a value filed in the wrong field (2026-11-24) came after their results; their measures are
reported as seen (scoring_es.score()). Their files stay as sealed, so their fact lists are kept apart, in
eval/scripts-es-holdout-facts. Held out are those after them, h06 on, written by someone who has seen no results.

    uv run eval/dictation_scripts_es.py            # every script: id, voice, spoken words, tags
    uv run eval/dictation_scripts_es.py e07        # e07's spoken text, to read aloud
    uv run eval/dictation_scripts_es.py e07 --display
    uv run eval/dictation_scripts_es.py --holdout          # the held-out scripts instead
"""

from __future__ import annotations

import argparse
import re
import sys
import tomllib
from dataclasses import dataclass
from pathlib import Path

SCRIPTS_DIR = Path(__file__).resolve().parent / "scripts-es"
HOLDOUT_DIR = Path(__file__).resolve().parent / "scripts-es-holdout"
# The first five scripts in HOLDOUT_DIR, seen: read in run A2 and since, and reported as seen, not held out.
SEEN = ("h01", "h02", "h03", "h04", "h05")
FIELDS = ("subjective", "objective", "assessment", "plan")

PUNCTUATION = {"punto y aparte": ("\n\n", "."), "nuevo párrafo": ("\n\n", "."), "punto y seguido": (".", ",")}
MARKUP = re.compile(r"\{([^{}]*)\}|\[\[([^\[\]|]*)\|([^\[\]|]*)\]\]")
ENGLISH = re.compile(r"<<en\|([^<>]*)>>")
WORD = re.compile(r"[^\W\d_]+(?:['-][^\W\d_]+)*")
KEYS = {"id", "language", "voice", "tags", "noise", "dates", "drugs", "drugs_en", "forbidden", "forbidden_en",
        "retracted", "negated", "affirmed", "family", "facts", "text", "gold"}
REQUIRED = {"id", "language", "voice", "tags", "dates", "drugs", "drugs_en", "text", "gold"}


class ScriptError(ValueError):
    """A script that doesn't follow the format. The message names the file."""


@dataclass(frozen=True)
class Statement:
    spanish: tuple[str, ...]
    english: str


@dataclass(frozen=True)
class Gold:
    """A field's gold as the scoring reads it (the English set's shape): the statements' English joined, and all their
    Spanish passages."""

    value: str
    sources: tuple[str, ...]


@dataclass(frozen=True)
class Script:
    id: str
    language: str
    voice: str
    tags: tuple[str, ...]
    noise: tuple[str, ...]
    dates: tuple[str, ...]
    drugs: tuple[str, ...]
    drugs_en: tuple[str, ...]
    forbidden: tuple[str, ...]
    forbidden_en: tuple[str, ...]
    retracted: tuple[str, ...]
    negated: tuple[str, ...]
    affirmed: tuple[str, ...]
    family: tuple[tuple[str, str], ...]  # a finding, and the relative who has it
    text: str
    statements: dict[str, tuple[Statement, ...]]
    path: Path
    facts: tuple[dict, ...] = ()  # what each passage says, as written (ehr_eval/facts_es.py reads them)

    @property
    def gold(self) -> dict[str, Gold]:
        return {name: Gold(" ".join(_sentence(s.english) for s in statements),
                           tuple(quote for s in statements for quote in s.spanish))
                for name, statements in self.statements.items()}

    @property
    def spoken(self) -> str:
        return spoken_text(self.text)

    @property
    def display(self) -> str:
        return display_text(self.text)

    @property
    def words(self) -> int:
        """Words the clinician says, spoken punctuation included."""
        return len(WORD.findall(self.spoken))


def _sentence(english: str) -> str:
    english = english.strip()
    return english if english[-1:] in ".!?" else english + "."


def _render(text: str, *, display: bool, pauses: bool = False) -> str:
    def replace(match: re.Match) -> str:
        token, written, said = match.groups()
        if token is None:
            if not written.strip() or not said.strip():
                raise ValueError(f"empty side in [[{written}|{said}]]")
            return written if display else said
        if token not in PUNCTUATION:
            raise ValueError(f"unknown spoken punctuation {{{token}}}")
        mark, pause = PUNCTUATION[token]
        if display:
            return mark
        return token + pause if pauses else token

    flat = " ".join(text.split())
    rendered = MARKUP.sub(replace, ENGLISH.sub(lambda m: m.group(1), flat))
    for stray in ("{", "}", "[", "]", "|", "<", ">"):
        if stray in rendered:
            raise ValueError(f"stray {stray!r} in the text: write [[display|spoken]], {{punto y aparte}} or <<en|...>>")
    if display:
        rendered = re.sub(r" +([.,:?])", r"\1", rendered)
        rendered = re.sub(r" *\n *", "\n", rendered)
        rendered = re.sub(r"\.+\n", ".\n", rendered)
    return re.sub(r" {2,}", " ", rendered).strip()


def spoken_text(text: str, *, pauses: bool = False) -> str:
    """What the clinician says, English passages included."""
    return _render(text, display=False, pauses=pauses)


def display_text(text: str) -> str:
    """What a transcriptionist writes: the word error rate reference, and what gold passages quote."""
    return _render(text, display=True)


def runs(text: str, *, display: bool = False, pauses: bool = False) -> list[tuple[str, str]]:
    """The text in runs of one language, ("es", ...) and ("en", ...), as said (with the pauses spoken punctuation
    leaves, for the audio generator) or as written, for the audio generator to read each with its own pronunciation,
    and for the text-only eval to mark the English ones."""
    flat = " ".join(text.split())
    found: list[tuple[str, str]] = []
    at = 0
    for match in ENGLISH.finditer(flat):
        if flat[at : match.start()].strip():
            found.append(("es", _render(flat[at : match.start()], display=display, pauses=pauses)))
        found.append(("en", _render(match.group(1), display=display, pauses=pauses)))
        at = match.end()
    if flat[at:].strip():
        found.append(("es", _render(flat[at:], display=display, pauses=pauses)))
    return found


def load_script(path: Path) -> Script:
    try:
        data = tomllib.loads(path.read_text(encoding="utf-8"))
    except tomllib.TOMLDecodeError as error:
        raise ScriptError(f"{path.name}: not valid TOML ({error})") from error
    unknown, missing = set(data) - KEYS, REQUIRED - set(data)
    if unknown or missing:
        raise ScriptError(f"{path.name}: unknown keys {sorted(unknown)}, missing keys {sorted(missing)}")

    def strings(key: str) -> tuple[str, ...]:
        value = data.get(key, [])
        if not isinstance(value, list) or not all(isinstance(item, str) and item for item in value):
            raise ScriptError(f"{path.name}: {key} must be a list of non-empty strings")
        return tuple(value)

    facts = data.get("facts", [])
    if not isinstance(facts, list) or not all(isinstance(fact, dict) for fact in facts):
        raise ScriptError(f"{path.name}: facts must be a list of tables")
    family = data.get("family", [])
    if not isinstance(family, list) or not all(
        isinstance(pair, list) and len(pair) == 2 and all(isinstance(x, str) and x for x in pair) for pair in family
    ):
        raise ScriptError(f"{path.name}: family must be a list of [finding, relative] pairs")
    gold = data["gold"]
    if not isinstance(gold, dict) or not set(gold) <= set(FIELDS):
        raise ScriptError(f"{path.name}: gold may have only {', '.join(FIELDS)}")
    statements: dict[str, tuple[Statement, ...]] = {}
    for name in FIELDS:
        entries = gold.get(name, [])
        if not isinstance(entries, list) or not all(
            isinstance(e, dict) and set(e) == {"spanish", "english"} and isinstance(e["english"], str)
            and e["english"].strip() and isinstance(e["spanish"], list) and e["spanish"]
            and all(isinstance(q, str) and q for q in e["spanish"])
            for e in entries
        ):
            raise ScriptError(f"{path.name}: each gold.{name} statement needs spanish (a list) and english (a string)")
        statements[name] = tuple(Statement(tuple(e["spanish"]), e["english"].strip()) for e in entries)
    for key in ("id", "language", "voice", "text"):
        if not isinstance(data[key], str) or not data[key].strip():
            raise ScriptError(f"{path.name}: {key} must be a non-empty string")
    if data["language"] != "es":
        raise ScriptError(f"{path.name}: language must be \"es\"")
    drugs, drugs_en = strings("drugs"), strings("drugs_en")
    if len(drugs) != len(drugs_en):
        raise ScriptError(f"{path.name}: drugs_en must give each of drugs its English name, in the same order")
    script = Script(
        id=data["id"], language=data["language"], voice=data["voice"], tags=strings("tags"), noise=strings("noise"),
        dates=strings("dates"), drugs=drugs, drugs_en=drugs_en, forbidden=strings("forbidden"),
        forbidden_en=strings("forbidden_en"), retracted=strings("retracted"), negated=strings("negated"),
        affirmed=strings("affirmed"), family=tuple((pair[0], pair[1]) for pair in family), text=data["text"],
        statements=statements, path=path, facts=tuple(facts),
    )
    try:
        spoken_text(script.text)
        display_text(script.text)
    except ValueError as error:
        raise ScriptError(f"{path.name}: {error}") from error
    return script


def load_scripts(directory: Path = SCRIPTS_DIR) -> list[Script]:
    """Every script, in id order."""
    return sorted((load_script(path) for path in directory.glob("*.toml")), key=lambda script: script.id)


def select(scripts: list[Script], wanted: list[str]) -> list[Script]:
    chosen = [s for s in scripts if any(s.id == w or s.id.startswith(f"{w}-") for w in wanted)]
    unmatched = [w for w in wanted if not any(s.id == w or s.id.startswith(f"{w}-") for s in scripts)]
    if unmatched:
        raise ScriptError(f"no script matches {', '.join(unmatched)}")
    return chosen


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("ids", nargs="*", help="script ids or prefixes, such as e07; none lists every script")
    parser.add_argument("--display", action="store_true", help="print the display text instead of the spoken text")
    parser.add_argument("--holdout", action="store_true", help="the held-out scripts (scripts-es-holdout) instead")
    args = parser.parse_args(argv)
    try:
        scripts = load_scripts(HOLDOUT_DIR if args.holdout else SCRIPTS_DIR)
        if not args.ids:
            for script in scripts:
                print(f"{script.id:32} {script.voice:11} {script.words:4} words  {' '.join(script.tags)}")
            return 0
        for script in select(scripts, args.ids):
            print(f"== {script.id} ({script.words} words)\n")
            print(script.display if args.display else spoken_text(script.text, pauses=True))
            print()
    except ScriptError as error:
        print(f"error: {error}", file=sys.stderr)
        return 1
    return 0


if __name__ == "__main__":
    sys.exit(main())
