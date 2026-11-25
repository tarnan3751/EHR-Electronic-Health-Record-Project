# /// script
# requires-python = ">=3.12"
# dependencies = []
# ///
"""Writes src/Ehr.Dictation/sidecar/ehr_dictation/englishwords.py: the words the English checks count as English when
they decide whether a dictation in English is mostly not English words (validation.py, english_share()), so a Spanish
dictation recorded with English chosen offers nothing (the owner's ruling of 2026-11-25). And LICENSE-SCOWL.md beside
it, the notice the lists ask to be shipped with them.

They come from SCOWL (Spell Checker Oriented Word Lists), Kevin Atkinson's, release 2020.12.07
(http://wordlist.aspell.net/), downloaded from SOURCE and checked against SHA256: its English, American, British and
Canadian lists of words, of words with capitals and of contractions, up to size 50 (SIZE: the words of an ordinary
dictionary, without the rarer ones a word of another language would match more often), in lower case. Not its
abbreviations, which are general ones ("DA", "ACLU") a short word of another language matches as often (the checks
count the clinical abbreviations they know themselves); not its proper names; and not possessives ("aardvark's"), which
validation.py reads as the word. SCOWL's licence lets the lists be copied, changed and passed on, with its notices: the
module keeps Kevin Atkinson's, and LICENSE-SCOWL.md is SCOWL's copyright file whole, verbatim, as the lists it draws
on ask. Nothing from a system dictionary.

    uv run src/Ehr.Dictation/write_english_words.py            # write both
    uv run src/Ehr.Dictation/write_english_words.py --check    # fail if either is out of date
    uv run src/Ehr.Dictation/write_english_words.py --source scowl-2020.12.07.tar.gz   # from a copy already here

The download needs the internet; a sidecar test checks what this makes from a small archive of its own, offline.
"""

from __future__ import annotations

import argparse
import hashlib
import io
import sys
import tarfile
import urllib.request
from pathlib import Path

REPO_ROOT = Path(__file__).resolve().parents[2]
PACKAGE = REPO_ROOT / "src" / "Ehr.Dictation" / "sidecar" / "ehr_dictation"
TARGET = PACKAGE / "englishwords.py"
NOTICE = PACKAGE / "LICENSE-SCOWL.md"
RELEASE = "2020.12.07"
SOURCE = f"https://downloads.sourceforge.net/project/wordlist/SCOWL/{RELEASE}/scowl-{RELEASE}.tar.gz"
SHA256 = "5587667caa20c4891390c2d42dbb4d5c4c3f41bee77af1457ece3ba23fb859cc"
SIZE = 50
REGIONS = ("english", "american", "british", "canadian")
KINDS = ("words", "upper", "contractions")
ATKINSON = """Copyright 2000-2018 by Kevin Atkinson

Permission to use, copy, modify, distribute and sell these word lists, the associated scripts, the output created from
the scripts, and its documentation for any purpose is hereby granted without fee, provided that the above copyright
notice appears in all copies and that both that copyright notice and this permission notice appear in supporting
documentation. Kevin Atkinson makes no representations about the suitability of this array for any purpose. It is
provided "as is" without express or implied warranty."""


def archive(source: str | None) -> bytes:
    """SCOWL's release archive, from a file here or downloaded, refused unless it is the one SHA256 records."""
    data = Path(source).read_bytes() if source else urllib.request.urlopen(SOURCE, timeout=120).read()  # noqa: S310
    if hashlib.sha256(data).hexdigest() != SHA256:
        raise SystemExit(f"{source or SOURCE} isn't SCOWL {RELEASE} as recorded (SHA256 in this script).")
    return data


def words(data: bytes) -> list[str]:
    """The lists' words (the module's header), in lower case, sorted. SCOWL writes its lists in Latin-1."""
    found: set[str] = set()
    with tarfile.open(fileobj=io.BytesIO(data), mode="r:gz") as tar:
        for member in tar.getmembers():
            folder, _, name = member.name.rpartition("/")
            family, _, level = name.rpartition(".")
            region, _, kind = family.partition("-")
            if (not member.isfile() or not folder.endswith("/final") or not level.isdigit() or int(level) > SIZE
                    or region not in REGIONS or kind not in KINDS):
                continue
            text = tar.extractfile(member).read().decode("latin-1")
            found |= {word.lower() for word in text.split() if not word.lower().endswith("'s")}
    return sorted(found)


def notice(data: bytes) -> str:
    """LICENSE-SCOWL.md: what it is, then SCOWL's copyright file, verbatim."""
    with tarfile.open(fileobj=io.BytesIO(data), mode="r:gz") as tar:
        member = next(m for m in tar.getmembers() if m.isfile() and m.name.endswith("/Copyright"))
        text = tar.extractfile(member).read().decode("latin-1")
    return "\n".join([
        "# SCOWL's word lists: license",
        "",
        "`englishwords.py` in this folder holds words from SCOWL (Spell Checker Oriented Word Lists) and",
        f"Friends, release {RELEASE}, by Kevin Atkinson and others (http://wordlist.aspell.net/), chosen and",
        "written by `src/Ehr.Dictation/write_english_words.py`. SCOWL's copyright file follows, whole and",
        "verbatim, as its notices ask of anyone who passes the lists on. It is not covered by this repository's",
        "own license.",
        "",
        "```text",
        text.rstrip("\n"),
        "```",
        "",
    ])


def module(listed: list[str]) -> str:
    lines = [
        '"""The words the English checks count as English when they decide whether a dictation in English is',
        "mostly not English words (validation.py, english_share()): SCOWL's English, American, British and",
        f"Canadian words, words with capitals and contractions up to size {SIZE}, release {RELEASE}, in lower",
        "case and without possessives. Written by src/Ehr.Dictation/write_english_words.py, which says what is",
        "left out and why; don't edit it. SCOWL's whole notice is LICENSE-SCOWL.md, beside this file.",
        "",
        *ATKINSON.splitlines(),
        '"""',
        "",
        "WORDS = frozenset(",
        '    """',
    ]
    line = "   "
    for word in listed:
        if len(line) + 1 + len(word) > 116:
            lines.append(line)
            line = "   "
        line += " " + word
    lines += [line, '    """.split()', ")", ""]
    return "\n".join(lines)


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--check", action="store_true",
                        help="fail if englishwords.py or LICENSE-SCOWL.md is out of date")
    parser.add_argument("--source", help="SCOWL's release archive, already downloaded")
    args = parser.parse_args(argv)
    data = archive(args.source)
    expected = {TARGET: module(words(data)), NOTICE: notice(data)}
    if args.check:
        stale = [path.name for path, text in expected.items() if not path.is_file()
                 or path.read_text(encoding="utf-8") != text]
        if stale:
            print(f"{', '.join(stale)} out of date: uv run src/Ehr.Dictation/write_english_words.py", file=sys.stderr)
            return 1
        return 0
    for path, text in expected.items():
        path.write_text(text, encoding="utf-8", newline="\n")
    print(f"Wrote {len(words(data))} words to {TARGET}, and {NOTICE}.")
    return 0


if __name__ == "__main__":
    sys.exit(main())
