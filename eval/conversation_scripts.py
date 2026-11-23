# /// script
# requires-python = ">=3.12"
# dependencies = []
# ///
"""The eval's synthetic visits (eval/conversations/cNN-*.toml), for listening to a visit (the expansion plan's step 16):
loading them, and rendering each turn two ways with the dictation scripts' markup (dictation_scripts.py), so the
generator (generate_conversations.py), the tests and the scoring read them the same way. All of it is made up: no real
patient, clinician or relative, and no names.

A visit is a list of turns, each said by the clinician, the patient, or someone else in the room (a parent, a grown-up
child), as people talk, not as a clinician dictates: no spoken punctuation, but written punctuation, which the voices
read as pauses and questions. `[[display|spoken]]` is used wherever writing and speech differ, every number among
them: `[[142/88|one forty-two over eighty-eight]]`.

- Spoken text is what is said, the spoken side of each `[[display|spoken]]`, punctuation kept for the voices.
- Display text is what a transcriptionist writes, the display side. A visit's display text is its turns' display
  texts, a line each: the reference for word error rate, and what gold sources quote.
- Heard text is the display text without the punctuation that ends a clause, as MedASR writes conversation, which
  nobody punctuates aloud: what the gold is checked against (tests/test_conversations.py).

Run it to read a visit aloud, a line a turn:

    uv run eval/conversation_scripts.py              # every visit: id, speakers, spoken words, minutes, tags
    uv run eval/conversation_scripts.py c03          # c03's spoken text, each line led by who says it
    uv run eval/conversation_scripts.py c03 --display
"""

from __future__ import annotations

import argparse
import re
import sys
import tomllib
from dataclasses import dataclass
from pathlib import Path

import dictation_scripts as ds

CONVERSATIONS_DIR = Path(__file__).resolve().parent / "conversations"
FIELDS = ds.FIELDS
ROLES = ("clinician", "patient", "other")
DEFAULT_GAP = 0.35  # seconds of silence before a turn that sets neither gap nor overlap
WORDS_A_MINUTE = 150  # the voices' pace (generate_audio.py), for an estimate of each visit's length

KEYS = {"id", "speakers", "voices", "tags", "noise", "dates", "drugs", "forbidden", "retracted", "denied", "hedged",
        "asked", "requests", "turns", "gold", "gold_rules"}
REQUIRED = {"id", "speakers", "voices", "tags", "dates", "drugs", "turns", "gold"}
TURN_KEYS = {"role", "text", "gap", "overlap"}
# Punctuation that ends a clause or a sentence, never the point or slash inside a number.
CLAUSE_PUNCTUATION = re.compile(r"[,;:?!]|\.(?!\d)")


class ScriptError(ValueError):
    """A visit that doesn't follow the format. The message names the file."""


@dataclass(frozen=True)
class Turn:
    role: str
    text: str
    gap: float | None
    overlap: float | None

    @property
    def spoken(self) -> str:
        return ds.spoken_text(self.text)

    @property
    def display(self) -> str:
        return ds.display_text(self.text)

    @property
    def heard(self) -> str:
        return " ".join(CLAUSE_PUNCTUATION.sub(" ", self.display).split())


@dataclass(frozen=True)
class Conversation:
    id: str
    speakers: int
    voices: dict[str, str]
    tags: tuple[str, ...]
    noise: tuple[str, ...]
    dates: tuple[str, ...]
    drugs: tuple[str, ...]
    forbidden: tuple[str, ...]
    retracted: tuple[str, ...]
    denied: tuple[str, ...]
    requests: tuple[str, ...]
    turns: tuple[Turn, ...]
    gold: dict[str, ds.Gold]
    gold_rules: dict[str, ds.Gold]
    path: Path
    rules_fields: tuple[str, ...] = ()
    hedged: tuple[str, ...] = ()  # diagnoses or findings hedged, denied or reported: never offered as certain
    asked: tuple[str, ...] = ()  # questions, as said: never a finding's source (and every question in a turn is one)

    @property
    def display(self) -> str:
        return "\n".join(turn.display for turn in self.turns)

    def said_by(self, role: str) -> str:
        """The display text of every turn the role said, a line each."""
        return "\n".join(turn.display for turn in self.turns if turn.role == role)

    @property
    def words(self) -> int:
        return sum(len(ds.WORD.findall(turn.spoken)) for turn in self.turns)

    @property
    def minutes(self) -> float:
        return self.words / WORDS_A_MINUTE


def load_conversation(path: Path) -> Conversation:
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

    voices = data["voices"]
    if data["speakers"] not in (2, 3) or not isinstance(voices, dict) or set(voices) != set(ROLES[: data["speakers"]]):
        raise ScriptError(f"{path.name}: speakers is 2 or 3, and voices names the clinician, the patient and, for 3, other")
    turns = []
    for number, turn in enumerate(data["turns"], 1):
        if not isinstance(turn, dict) or set(turn) - TURN_KEYS or turn.get("role") not in voices or not turn.get("text"):
            raise ScriptError(f"{path.name}: turn {number} needs a role the visit has and a text, and nothing but gap or overlap")
        if "gap" in turn and "overlap" in turn:
            raise ScriptError(f"{path.name}: turn {number} has both a gap and an overlap")
        turns.append(Turn(turn["role"], " ".join(turn["text"].split()), turn.get("gap"), turn.get("overlap")))
        try:
            turns[-1].display
            turns[-1].spoken
        except ValueError as error:
            raise ScriptError(f"{path.name}: turn {number}: {error}") from error

    def entry(table: str, field: str, value: object) -> ds.Gold:
        if not isinstance(value, dict) or set(value) != {"value", "sources"} or not isinstance(value["value"], str):
            raise ScriptError(f"{path.name}: {table}.{field} must have a value (string) and sources (list)")
        if not isinstance(value["sources"], list) or not all(isinstance(s, str) and s for s in value["sources"]):
            raise ScriptError(f"{path.name}: {table}.{field}.sources must be a list of non-empty strings")
        return ds.Gold(value["value"].strip(), tuple(value["sources"]))

    gold, rules = data["gold"], data.get("gold_rules", {})
    if not isinstance(gold, dict) or set(gold) != set(FIELDS) or not isinstance(rules, dict) or not set(rules) <= set(FIELDS):
        raise ScriptError(f"{path.name}: gold has exactly {', '.join(FIELDS)}, and gold_rules only those")
    fields = {field: entry("gold", field, gold[field]) for field in FIELDS}
    return Conversation(
        id=data["id"],
        speakers=data["speakers"],
        voices=dict(voices),
        tags=strings("tags"),
        noise=strings("noise"),
        dates=strings("dates"),
        drugs=strings("drugs"),
        forbidden=strings("forbidden"),
        retracted=strings("retracted"),
        denied=strings("denied"),
        hedged=strings("hedged"),
        asked=strings("asked"),
        requests=strings("requests"),
        turns=tuple(turns),
        gold=fields,
        gold_rules={field: entry("gold_rules", field, rules[field]) if field in rules else fields[field] for field in FIELDS},
        path=path,
        rules_fields=tuple(field for field in FIELDS if field in rules),
    )


def load_conversations(directory: Path = CONVERSATIONS_DIR) -> list[Conversation]:
    """Every visit, in id order."""
    return sorted((load_conversation(path) for path in directory.glob("c*.toml")), key=lambda visit: visit.id)


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("ids", nargs="*", help="visit ids or prefixes, such as c03; none lists every visit")
    parser.add_argument("--display", action="store_true", help="print the display text instead of the spoken text")
    args = parser.parse_args(argv)
    try:
        visits = load_conversations()
        if not args.ids:
            for visit in visits:
                print(f"{visit.id:32} {visit.speakers} {visit.words:5} words {visit.minutes:5.1f} min  {' '.join(visit.tags)}")
            return 0
        for visit in visits:
            if any(visit.id == wanted or visit.id.startswith(f"{wanted}-") for wanted in args.ids):
                print(f"== {visit.id} ({visit.words} words)\n")
                for turn in visit.turns:
                    print(f"{turn.role.capitalize()}: {turn.display if args.display else turn.spoken}")
                print()
    except ScriptError as error:
        print(f"error: {error}", file=sys.stderr)
        return 1
    return 0


if __name__ == "__main__":
    sys.exit(main())
