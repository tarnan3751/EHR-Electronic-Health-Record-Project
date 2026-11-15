# /// script
# requires-python = ">=3.12"
# dependencies = []
# ///
"""The dictation test set's scripts (eval/scripts/*.toml): loading them, and rendering their text two ways.

A script's `text` is what a clinician dictates, marked up where speech and writing differ:

- `{period}`, `{comma}`, `{colon}`, `{question mark}`, `{new line}`, `{new paragraph}`: punctuation the clinician
  says aloud, written the way MedASR writes it.
- `[[display|spoken]]`: what is written, then what is said, such as `[[142/88|one forty-two over eighty-eight]]`,
  `[[10 mg|ten milligrams]]` or `[[March 14, 2026|March fourteenth twenty twenty-six]]`.

Single brackets are not used: the scoring reads `[X]` as a dictated section heading.

- Spoken text is what the clinician says: each spoken punctuation mark as its words ("period"), and the spoken side
  of each `[[display|spoken]]`. With `pauses=True`, each spoken punctuation word is followed by a full stop or a
  comma ("period."), so the synthetic voices, and people recording the scripts, pause where a dictating clinician
  does. The pause marks are not words and change nothing else.
- Display text is what a transcriptionist writes: the punctuation as marks, `{new line}` and `{new paragraph}` as
  one and two line breaks, the display side of each `[[display|spoken]]`, and no space before a mark. It is the
  reference for word error rate, and gold sources quote it.

Standard library only, so the audio generator, the tests and the scoring code can import it without the speech
synthesis packages. Run it to read the scripts aloud:

    uv run eval/dictation_scripts.py              # every script: id, voice, spoken words, tags
    uv run eval/dictation_scripts.py d07          # d07's spoken text, to read aloud
    uv run eval/dictation_scripts.py d07 --display
"""

from __future__ import annotations

import argparse
import re
import sys
import tomllib
from dataclasses import dataclass
from pathlib import Path

SCRIPTS_DIR = Path(__file__).resolve().parent / "scripts"
FIELDS = ("history", "assessment", "plan")

# Spoken punctuation: the mark it is written as, and the pause after it when read aloud.
PUNCTUATION = {
    "period": (".", "."),
    "comma": (",", ","),
    "colon": (":", ","),
    "question mark": ("?", "."),
    "new line": ("\n", "."),
    "new paragraph": ("\n\n", "."),
}

MARKUP = re.compile(r"\{([^{}]*)\}|\[\[([^\[\]|]*)\|([^\[\]|]*)\]\]")
WORD = re.compile(r"[A-Za-z]+(?:['-][A-Za-z]+)*")
KEYS = {"id", "voice", "tags", "noise", "dates", "drugs", "forbidden", "retracted", "text", "gold", "gold_rules"}
REQUIRED = {"id", "voice", "tags", "dates", "drugs", "text", "gold"}


class ScriptError(ValueError):
    """A script that doesn't follow the format. The message names the file."""


@dataclass(frozen=True)
class Gold:
    value: str
    sources: tuple[str, ...]


@dataclass(frozen=True)
class Script:
    id: str
    voice: str
    tags: tuple[str, ...]
    noise: tuple[str, ...]
    dates: tuple[str, ...]
    drugs: tuple[str, ...]
    forbidden: tuple[str, ...]
    retracted: tuple[str, ...]
    text: str
    gold: dict[str, Gold]
    path: Path
    # The best answer the dictation service's rules allow, field by field: a script's own [gold_rules.<field>] where
    # the clinical gold (gold) doesn't follow them, and the clinical gold where it does. rules_fields names the former.
    gold_rules: dict[str, Gold]
    rules_fields: tuple[str, ...] = ()

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
    rendered = MARKUP.sub(replace, flat)
    for stray in ("{", "}", "[", "]", "|"):
        if stray in rendered:
            raise ValueError(f"stray {stray!r} in the text: write [[display|spoken]] or a spoken punctuation token")
    if display:
        rendered = re.sub(r" +([.,:?])", r"\1", rendered)
        rendered = re.sub(r" *\n *", "\n", rendered)
    return re.sub(r" {2,}", " ", rendered).strip()


def spoken_text(text: str, *, pauses: bool = False) -> str:
    """What the clinician says. `pauses` adds a full stop or comma after each spoken punctuation word."""
    return _render(text, display=False, pauses=pauses)


def display_text(text: str) -> str:
    """What a transcriptionist writes: the word error rate reference, and what gold sources quote."""
    return _render(text, display=True)


def load_script(path: Path) -> Script:
    try:
        data = tomllib.loads(path.read_text(encoding="utf-8"))
    except tomllib.TOMLDecodeError as error:
        raise ScriptError(f"{path.name}: not valid TOML ({error})") from error

    unknown = set(data) - KEYS
    missing = REQUIRED - set(data)
    if unknown or missing:
        raise ScriptError(f"{path.name}: unknown keys {sorted(unknown)}, missing keys {sorted(missing)}")

    def strings(key: str) -> tuple[str, ...]:
        value = data.get(key, [])
        if not isinstance(value, list) or not all(isinstance(item, str) and item for item in value):
            raise ScriptError(f"{path.name}: {key} must be a list of non-empty strings")
        return tuple(value)

    gold = data["gold"]
    if not isinstance(gold, dict) or set(gold) != set(FIELDS):
        raise ScriptError(f"{path.name}: gold must have exactly {', '.join(FIELDS)}")
    rules = data.get("gold_rules", {})
    if not isinstance(rules, dict) or not set(rules) <= set(FIELDS):
        raise ScriptError(f"{path.name}: gold_rules may have only {', '.join(FIELDS)}")

    def entry(table: str, field: str, value: object) -> Gold:
        if not isinstance(value, dict) or set(value) != {"value", "sources"} or not isinstance(value["value"], str):
            raise ScriptError(f"{path.name}: {table}.{field} must have a value (string) and sources (list)")
        sources = value["sources"]
        if not isinstance(sources, list) or not all(isinstance(s, str) and s for s in sources):
            raise ScriptError(f"{path.name}: {table}.{field}.sources must be a list of non-empty strings")
        return Gold(value["value"].strip(), tuple(sources))

    fields = {field: entry("gold", field, gold[field]) for field in FIELDS}
    rules_fields = tuple(field for field in FIELDS if field in rules)
    gold_rules = {field: entry("gold_rules", field, rules[field]) if field in rules else fields[field] for field in FIELDS}

    for key in ("id", "voice", "text"):
        if not isinstance(data[key], str) or not data[key].strip():
            raise ScriptError(f"{path.name}: {key} must be a non-empty string")

    script = Script(
        id=data["id"],
        voice=data["voice"],
        tags=strings("tags"),
        noise=strings("noise"),
        dates=strings("dates"),
        drugs=strings("drugs"),
        forbidden=strings("forbidden"),
        retracted=strings("retracted"),
        text=data["text"],
        gold=fields,
        path=path,
        gold_rules=gold_rules,
        rules_fields=rules_fields,
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
    """The scripts whose id is one of `wanted`, or starts with one followed by a hyphen (`d07` for d07-...)."""
    chosen = [s for s in scripts if any(s.id == w or s.id.startswith(f"{w}-") for w in wanted)]
    unmatched = [w for w in wanted if not any(s.id == w or s.id.startswith(f"{w}-") for s in scripts)]
    if unmatched:
        raise ScriptError(f"no script matches {', '.join(unmatched)}")
    return chosen


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("ids", nargs="*", help="script ids or prefixes, such as d07; none lists every script")
    parser.add_argument("--display", action="store_true", help="print the display text instead of the spoken text")
    args = parser.parse_args(argv)
    try:
        scripts = load_scripts()
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
