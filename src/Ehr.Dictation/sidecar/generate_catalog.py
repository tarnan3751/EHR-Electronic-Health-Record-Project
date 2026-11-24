"""Writes ehr_dictation/catalog.py, the names a dictated order or allergy to add may name to be proposed (validation.py,
"plain"), from the repository's reference data, the only source: the sidecar has no database.

- Orderables: every item of the order catalog (db/reference/20-order-catalog.sql: LOINC's Universal Lab Orders and
  RxNorm's clinical drugs), the clinic's common catalog items (the Orders migration's, read from
  db/migrations/schema.sql), and every RxNorm medication and ingredient the chart's codes hold
  (db/reference/10-clinical-codes.sql, kinds medication and allergen, system rxnorm).
- Allergens: every allergen the chart's codes hold (db/reference/10-clinical-codes.sql, kind allergen: RxNorm
  ingredients and UNII foods and environmental substances).
- Common names (db/reference/dictation-names.toml, the expansion plan's step 15.1): a name a clinician dictates for an
  item the reference data holds under another name ("penicillin", "latex", "CRP"), each written whole as a name of its
  own, of its kind only, and refused unless every code it stands for is in the reference data as that kind.

An orderable that is a medication (an RxNorm drug, medication or ingredient, or a common catalog item of type
medication) is marked "+" before its name: only a medication can be refilled, prescribed, renewed, called in or sent to
the pharmacy (validation.py's closed sentence).

Each name is written as its words (textnorm.tokens), lower case, without its numbers, one name a line, with its
standalone runs marked, "^" before the first word and "$" after the last: the generic head (each ingredient's name
where it starts in a drug's; a LOINC order's component, before its property, system, method or a dash; a substance's
whole name), an abbreviation given in parentheses ("Complete blood count (CBC)"), a brand in brackets ("[Prinivil]"),
and a common catalog item's name, to its first comma or parenthesis. An item must begin with one of them whole and may
go on along its name (validation.is_item()). So "lisinopril", "lisinopril 10 mg oral tablet", "CBC", "chest x-ray" and
"lipid panel" can be items, and "morning", "pain", "normal" or "chest" alone can't. Names that come out the same are
written once. Nothing else is added by hand: a word neither the reference data nor a common name holds is never part of
an item. Run it after changing any of those files; a test fails while the copy is out of date.

The names are a derived copy of LOINC's, RxNorm's and the FDA's UNII names, and carry their notices (catalog.py's
header, and docs/expansion/reference-data.md, "Licenses and attribution").

    uv run --project src/Ehr.Dictation/sidecar python src/Ehr.Dictation/sidecar/generate_catalog.py
"""

from __future__ import annotations

import re
import sys
import tomllib
from pathlib import Path

SIDECAR = Path(__file__).resolve().parent
REPOSITORY = SIDECAR.parents[2]
OUTPUT = SIDECAR / "ehr_dictation" / "catalog.py"
CLINICAL_CODES = "db/reference/10-clinical-codes.sql"
ORDER_CATALOG = "db/reference/20-order-catalog.sql"
SCHEMA = "db/migrations/schema.sql"
NAMES = "db/reference/dictation-names.toml"
COMMON_ITEM = re.compile(r"VALUES \('(SYN-[A-Z]+-\d+)', '((?:[^']|'')*)', '(\w+)'\)")

sys.path.insert(0, str(SIDECAR))
from ehr_dictation.textnorm import tokens  # noqa: E402


def copy_rows(path: Path, table: str) -> list[list[str]]:
    """The rows of one COPY block, as pg_dump writes it: tab-separated, ending at a line holding only a backslash and a
    full stop."""
    rows, inside = [], False
    for line in path.read_text(encoding="utf-8").splitlines():
        if line.startswith(f"COPY {table} "):
            inside = True
        elif inside and line == "\\.":
            break
        elif inside:
            rows.append(line.split("\t"))
    if not rows:
        raise SystemExit(f"No COPY {table} rows in {path}.")
    return rows


PARENTHESES = re.compile(r"\(([^)]+)\)")
BRACKETS = re.compile(r"\[([^\]]+)\]")
# Where a LOINC name's component ends: its property in brackets, its system, its method, or a dash.
LOINC_COMPONENT_END = re.compile(r" \[| in | by | - ")


def name(display: str, runs: list[tuple[int, int]]) -> str:
    """A name as its words, lower case, without numbers, each standalone run (character offsets in the display) marked:
    "^" before its first word, "$" after its last ("lisinopril 10 MG Oral Tablet [Prinivil]" is "^lisinopril$ mg oral
    tablet ^prinivil$")."""
    words = [token for token in tokens(display) if not token.text[0].isdigit()]
    starts, ends = set(), set()
    for start, end in runs:
        inside = [i for i, token in enumerate(words) if start <= token.start and token.end <= end]
        if inside:
            starts.add(inside[0])
            ends.add(inside[-1])
    return " ".join(("^" if i in starts else "") + token.text + ("$" if i in ends else "") for i, token in enumerate(words))


def in_brackets(display: str, pattern: re.Pattern) -> list[tuple[int, int]]:
    return [match.span(1) for match in pattern.finditer(display)]


def drug(display: str, ingredients: dict[str, list[tuple[str, ...]]]) -> str:
    """A drug's name: each ingredient's name where it starts, and its brand in brackets."""
    words = [token for token in tokens(display) if not token.text[0].isdigit()]
    runs = in_brackets(display, BRACKETS)
    for i, token in enumerate(words):
        for ingredient in ingredients.get(token.text, ()):
            if tuple(word.text for word in words[i : i + len(ingredient)]) == ingredient:
                runs.append((token.start, words[i + len(ingredient) - 1].end))
    return name(display, runs or [(0, len(display))])


def lab(display: str) -> str:
    """A LOINC lab order's name: its component (before its property, system, method or a dash) and what parentheses
    give."""
    end = LOINC_COMPONENT_END.search(display)
    return name(display, [(0, end.start() if end else len(display)), *in_brackets(display, PARENTHESES)])


def common(display: str) -> str:
    """A common catalog item's name, to its first comma or parenthesis ("Chest X-ray, 2 views" is "chest x ray"), and
    the abbreviation in its parentheses ("Complete blood count (CBC)")."""
    end = min([at for at in (display.find(","), display.find("(")) if at >= 0], default=len(display))
    return name(display, [(0, end), *in_brackets(display, PARENTHESES)])


def whole(display: str) -> str:
    """A substance's name (an ingredient, a food, an allergen): the whole of it."""
    return name(display, [(0, len(display))])


def common_names(repository: Path, orderables: dict[str, str], allergens: set[str]) -> tuple[set[str], set[str]]:
    """The common names (NAMES) of each kind, orderables (each marked "+" when every code it stands for is a
    medication) and allergens, each written whole. `orderables` gives each order catalog code its type; `allergens`
    holds each allergen as system:code. A code that isn't one of its kind stops the generator."""
    entries = tomllib.loads((repository / NAMES).read_text(encoding="utf-8")).get("name", [])
    kinds: dict[str, set[str]] = {"orderable": set(), "allergen": set()}
    for entry in entries:
        known = orderables if entry["kind"] == "orderable" else allergens if entry["kind"] == "allergen" else {}
        missing = [code for code in entry["codes"] if code not in known]
        if missing or not entry["codes"]:
            raise SystemExit(
                f"{NAMES}: {entry['name']!r} stands for codes the reference data doesn't hold as an {entry['kind']}: "
                f"{', '.join(missing) or 'none given'}."
            )
        if not entry.get("source", "").strip():
            raise SystemExit(f"{NAMES}: {entry['name']!r} says no source for its name.")
        medication = entry["kind"] == "orderable" and all(orderables[code] == "medication" for code in entry["codes"])
        kinds[entry["kind"]].add(("+" if medication else "") + whole(entry["name"]))
    return kinds["orderable"], kinds["allergen"]


def names(repository: Path = REPOSITORY) -> tuple[list[str], list[str], dict[str, int]]:
    """The orderables' and the allergens' names, each sorted and once, and how many rows each source gave."""
    codes = copy_rows(repository / CLINICAL_CODES, "incoming_codes")
    catalog = copy_rows(repository / ORDER_CATALOG, "incoming_order_catalog_items")
    common_items = COMMON_ITEM.findall((repository / SCHEMA).read_text(encoding="utf-8"))
    if not common_items:
        raise SystemExit(f"No common catalog items in {SCHEMA}.")
    ingredients: dict[str, list[tuple[str, ...]]] = {}
    for kind, system, _, display, _ in codes:
        if kind == "allergen" and system == "rxnorm":
            words = tuple(token.text for token in tokens(display) if not token.text[0].isdigit())
            if words:
                ingredients.setdefault(words[0], []).append(words)
    drugs = [display for kind, system, _, display, _ in codes if kind == "medication" and system == "rxnorm"]
    drugs += [display for _, kind, display, *_ in catalog if kind == "medication"]
    substances = [display for kind, _, _, display, _ in codes if kind == "allergen"]  # ingredients, foods and the rest
    labs = [display for _, kind, display, *_ in catalog if kind == "lab"]
    named_orderables, named_allergens = common_names(
        repository,
        {code: kind for code, kind, *_ in catalog} | {code: kind for code, _, kind in common_items},
        {f"{system}:{code}" for kind, system, code, *_ in codes if kind == "allergen"},
    )
    counts = {
        "order catalog": len(catalog), "common catalog items": len(common_items),
        "RxNorm medications": len([1 for kind, system, *_ in codes if kind == "medication" and system == "rxnorm"]),
        "allergens": len(substances), "common names": len(named_orderables) + len(named_allergens),
    }
    medications = (
        {drug(display, ingredients) for display in drugs}
        | {common(display.replace("''", "'")) for _, display, kind in common_items if kind == "medication"}
        | {whole(display) for kind, system, _, display, _ in codes if kind == "allergen" and system == "rxnorm"}
    )
    orderables = (
        medications | {lab(display) for display in labs}
        | {common(display.replace("''", "'")) for _, display, _ in common_items} | {whole(display) for display in substances}
    )
    allergens = {whole(display) for display in substances}
    marked = [("+" if name in medications else "") + name for name in sorted(orderables - {""})]
    marked = sorted(set(marked) | named_orderables, key=lambda name: name.lstrip("+"))
    return marked, sorted((allergens | named_allergens) - {""}), counts


def render(repository: Path = REPOSITORY) -> str:
    orderables, allergens, counts = names(repository)
    sources = "; ".join(f"{source} {count}" for source, count in counts.items())
    return (
        '"""Generated by src/Ehr.Dictation/sidecar/generate_catalog.py from the reference data and its common names\n'
        "(db/reference/dictation-names.toml): change that script or the files it reads, not this one. The names a dictated\n"
        "order (ORDERABLES) or allergy to add (ALLERGENS) may name to be proposed (validation.py, \"plain\"), one a line, as\n"
        "their words in lower case without numbers, each standalone run marked \"^\" before its first word and \"$\" after\n"
        "its last, and a medication's \"+\" before it. Read only when a proposal is weighed.\n"
        f"From: {sources} rows.\n\n"
        "A derived copy of names from three sources, which ask for these notices (docs/expansion/reference-data.md):\n"
        "- RxNorm: This product uses publicly available data courtesy of the U.S. National Library of Medicine (NLM),\n"
        "  National Institutes of Health, Department of Health and Human Services; NLM is not responsible for the product and\n"
        "  does not endorse or recommend this or any other product. The data here is the release named there and may not be\n"
        "  current.\n"
        "- UNII: published by the U.S. Food and Drug Administration; as a work of the U.S. government it is taken to be in\n"
        "  the public domain.\n"
        "- LOINC: This material contains content from LOINC (http://loinc.org). LOINC is copyright © Regenstrief Institute,\n"
        "  Inc. and the Logical Observation Identifiers Names and Codes (LOINC) Committee and is available at no cost under the\n"
        "  license at http://loinc.org/license. LOINC® is a registered United States trademark of Regenstrief Institute, Inc.\n"
        '  The version used is 2.83."""\n\n'
        f'ORDERABLES = """\n{chr(10).join(orderables)}\n"""\n\n'
        f'ALLERGENS = """\n{chr(10).join(allergens)}\n"""\n'
    )


def main() -> int:
    OUTPUT.write_text(render(), encoding="utf-8", newline="\n")
    print(f"Wrote {OUTPUT.relative_to(REPOSITORY)}.")
    return 0


if __name__ == "__main__":
    sys.exit(main())
