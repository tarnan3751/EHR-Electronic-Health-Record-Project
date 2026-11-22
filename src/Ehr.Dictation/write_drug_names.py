# /// script
# requires-python = ">=3.12"
# dependencies = []
# ///
"""Writes src/Ehr.Dictation/sidecar/ehr_dictation/drugnames.py: the English drug names the Spanish dictation checks
recognize in a translated proposal (translation.py), so that a drug name in the English must be one the Spanish it
cites says, even one no USAN stem gives away (aspirin, insulin, digoxin).

They come from the RxNorm ingredients the chart's allergy list is coded with (db/reference/10-clinical-codes.sql, its
`allergen rxnorm` rows; RxNorm is published by the NLM without a licence): every ingredient named in one word, of four
letters or more, but for the words in STOP, which are elements, foods, materials and laboratory tests as often as
they are ingredients ("sodium 138", "water", "glucose", "creatinine"), and would otherwise ask a Spanish source for a
drug where the clinician named a result. Ingredients named in several words are left out: their first words are
mostly salts, excipients and plants ("sodium", "polyethylene", "black").

    uv run src/Ehr.Dictation/write_drug_names.py            # write it
    uv run src/Ehr.Dictation/write_drug_names.py --check    # fail if it is out of date

A sidecar test fails when the file isn't what this writes from the reference data in the repository.
"""

from __future__ import annotations

import argparse
import sys
from pathlib import Path

REPO_ROOT = Path(__file__).resolve().parents[2]
SOURCE = REPO_ROOT / "db" / "reference" / "10-clinical-codes.sql"
TARGET = REPO_ROOT / "src" / "Ehr.Dictation" / "sidecar" / "ehr_dictation" / "drugnames.py"
ROW = "allergen\trxnorm\t"

STOP = frozenset(
    """acetone air amber ammonia amylase bentonite beeswax boron bran butane calcium cellulose chlorine chlorophyll
    cholesterol choline cochineal collagen copper creatine creatinine cyclohexane fibrinogen fructose galactose gamboge
    gelatin glucose guaiac helium hemp heptane histamine indigo iodine iron isobutane kaolin lactase lactate lactose
    lanolin lecithin lipase magnesium maltose manganese mannose mica molasses molybdenum nitrogen oxygen paraffin
    pectin perlite petrolatum phenol potassium propane protease prothrombin pumice radium rhamnose rosin samarium
    selenium shellac silicon silver sodium starch strontium styrene sucrose sulfur talc tartrazine thrombin titanium
    trehalose turpentine urea vanadium water zein zinc zirconium""".split()
)


def names(sql: str) -> list[str]:
    found = set()
    for line in sql.splitlines():
        if not line.startswith(ROW):
            continue
        display = line.split("\t")[3].strip().lower()
        if display.isalpha() and display.isascii() and len(display) >= 4 and display not in STOP:
            found.add(display)
    return sorted(found)


def module(sql: str) -> str:
    listed = names(sql)
    lines = [
        '"""English drug names the Spanish dictation checks recognize (translation.py): RxNorm ingredients named in one',
        "word, from db/reference/10-clinical-codes.sql. Written by src/Ehr.Dictation/write_drug_names.py, which says",
        'which are left out and why; don\'t edit it."""',
        "",
        "NAMES = frozenset(",
        '    """',
    ]
    line = "   "
    for name in listed:
        if len(line) + 1 + len(name) > 116:
            lines.append(line)
            line = "   "
        line += " " + name
    lines += [line, '    """.split()', ")", ""]
    return "\n".join(lines)


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--check", action="store_true", help="fail if drugnames.py is out of date")
    args = parser.parse_args(argv)
    expected = module(SOURCE.read_text(encoding="utf-8"))
    if args.check:
        if TARGET.read_text(encoding="utf-8") != expected:
            print(f"{TARGET} is out of date: uv run src/Ehr.Dictation/write_drug_names.py", file=sys.stderr)
            return 1
        return 0
    TARGET.write_text(expected, encoding="utf-8", newline="\n")
    print(f"Wrote {len(names(SOURCE.read_text(encoding='utf-8')))} names to {TARGET}.")
    return 0


if __name__ == "__main__":
    sys.exit(main())
