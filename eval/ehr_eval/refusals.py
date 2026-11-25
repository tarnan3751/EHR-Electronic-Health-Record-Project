"""False refusals (the owner's ruling of 2026-11-24): the correct translations the Spanish checks (translation.py)
refuse, each listed by sentence, beside the share of the fields with gold the checks discarded. Reported, never a stop.

- Of the gold: each gold statement of the scripts a run holds, checked alone against its script's display text read as
  one passage (goldreport.transcript(), as tests/test_gold_es.py reads it), that the checks refuse. The gold is a
  correct translation by definition, so every one is a false refusal. The test set's gold has none (test_gold_es.py);
  the scripts run only when asked are held to their structure only, so theirs are found here.
- Of a run's answers: each statement of a field the checks discarded, checked alone against the transcript the run had,
  that the checks refuse. A model's statement differs from the gold, so it is decided by the scorer, the reading that
  decides a mistranslation (scoring_es.py, facts_es.py, never the checks' own): counted as a false refusal when every
  passage it cites is in the transcript, the scorer reads something in it (a value, a name, a negation, a doubt, the
  past or a relative of the script's fact list) and finds nothing changed from what the Spanish it cites says; a value
  no field's gold has is no change ("In 4 weeks." for "Control en 4 semanas." is a false refusal). One the scorer reads
  nothing in is listed as undecided, not counted: the facts don't say enough to tell ("Right-handed patient" for
  "Paciente diestro" is read the same as "Left-handed patient"). The rest are right refusals, listed too. The scorer
  only reads what the facts list, so a change they don't record can make a right refusal look false: h04's "for 3
  weeks" for "hace 3 semanas" (the facts say 3 weeks, not when), or a word they don't list that the model got wrong
  ("febrile" for "atérmico"). So each is listed with the gold that cites the same Spanish and the checks' reasons the
  scorer compares nothing of (UNSEEN), for a person to read. Also counted: the statements discarded alongside a refused
  one (a field fails whole), and the refusals of a hedge of leave added ("The dose may be repeated" for "Se puede
  repetir la dosis", translation.LEAVE): the owner's known false refusal.
- The scorer's reading of the gold, for the scripts run only when asked (whose gold no test holds to the scorer): each
  gold statement the scorer reads as changing something, so a changed item an answer shows in the same statement may be
  the scorer's, not the model's.

run_es.py stores the counts and lists in run B's results (metrics' false_refusals, and the texts' under text) and
prints them; for run A's results, the held-out scripts' among them (run_a.py --holdout), run this on its results file:

    uv run --project eval python -m ehr_eval.refusals <results.json>
"""

from __future__ import annotations

import json
import sys
from pathlib import Path

from dictation_scripts_es import HOLDOUT_DIR, Script, load_scripts
from ehr_dictation import spanish, translation
from ehr_dictation.prompt import FIELDS
from ehr_dictation.transcript import Transcript

from . import facts_es, goldreport, scoring_es


def refused_alone(name: str, statement: dict, transcript: Transcript) -> list[str]:
    """The reasons the checks refuse one statement for, checked alone in its field (as the review's harness and
    wrong_translations.py check a pair)."""
    statement = {"spanish": list(statement["spanish"]), "english": statement["english"]}
    return list(translation.check({name: [statement]}, [], transcript).fields[name].reasons)


def _spans(quotes: list[str], hypothesis: str) -> list[tuple[int, int]] | None:
    """Where each quote is in the transcript, word for word on the checks' keys (as translation._locate() finds it), or
    None if one isn't."""
    toks = spanish.tokens(hypothesis)
    keys = [t.key for t in toks]
    spans = []
    for quote in quotes:
        wanted = [t.key for t in spanish.tokens(quote)]
        at = next((i for i in range(len(keys) - len(wanted) + 1) if wanted and keys[i : i + len(wanted)] == wanted), None)
        if at is None:
            return None
        spans.append((toks[at].start, toks[at + len(wanted) - 1].end))
    return spans


# The checks' reasons whose kind the scorer compares nothing of: a refusal for one of them that the scorer reads as
# false may be a right one it can't see.
UNSEEN = frozenset({"translation_adds_words", "finding_not_in_sources", "orderable_not_in_sources",
                    "dose_form_not_in_sources", "order_changed", "order_or_allergy_change", "too_long",
                    "source_repeated"})


def verdict(english: str, spans: list[tuple[int, int]] | None, hypothesis: str, name: str, script: Script) -> str:
    """"false" (a correct translation, as the scorer reads it), "right" or "undecided": the header says how."""
    return _judged(english, spans, hypothesis, name, script)[0]


def _judged(english: str, spans: list[tuple[int, int]] | None, hypothesis: str, name: str,
            script: Script) -> tuple[str, str | None]:
    """The verdict, and for a right one whose every change is a known scorer limit (scoring_es.KNOWN_LIMITS), the
    limits: such a refusal may be a false one the scorer can't see."""
    if not spans:
        return "right", None
    others = tuple(script.gold[other].value for other in script.gold if other != name)
    changed, *_ = scoring_es._statement_items(english, spans, hypothesis, script.gold[name].value, script, others)
    if changed:
        limits = [scoring_es.known_limit(script.id, kind, english) for kind, _, _ in changed]
        return "right", "; ".join(dict.fromkeys(limits)) if all(limits) else None
    entries = facts_es.entries(scoring_es.facts_of(script))
    lexicon = facts_es.Lexicon(tuple(item for entry in entries for item in entry.items), scoring_es.drug_names())
    return ("false" if facts_es.claims(english, lexicon) else "undecided"), None


def _gold_citing(script: Script, quotes: list[str]) -> list[str]:
    """The gold statements, of any field, citing any of the same Spanish."""
    return [s.english for name in FIELDS for s in script.statements[name]
            if any(a in b or b in a for a in s.spanish for b in quotes)]


def of_answers(items: list[scoring_es.SpanishItem], transcripts: dict[str, Transcript],
               scripts: dict[str, Script]) -> dict:
    refused: list[dict] = []
    alongside = 0
    for item in items:
        if item.sorting != "done":
            continue
        script = scripts[item.script]
        for name in FIELDS:
            outcome = item.fields.get(name)
            if not outcome or outcome.status == "pending":
                continue
            for statement in outcome.statements:
                reasons = refused_alone(name, statement, transcripts[item.variant])
                alongside += not reasons
                if reasons:
                    quotes = list(statement["spanish"])
                    refused.append({
                        "variant": item.variant, "field": name, "english": statement["english"], "spanish": quotes,
                        "reasons": reasons, "gold": _gold_citing(script, quotes),
                        "unseen": sorted(set(reasons) & UNSEEN)} | dict(zip(("verdict", "known_limit"), _judged(
                            statement["english"], _spans(quotes, item.hypothesis), item.hypothesis, name, script))))
    by = {kind: [entry for entry in refused if entry["verdict"] == kind] for kind in ("false", "undecided", "right")}
    leave = [entry for entry in refused
             if "certainty_added" in entry["reasons"] and translation.LEAVE.search(entry["english"])]
    return {"refused": len(refused), **by, "alongside": alongside, "leave": leave}


def of_gold(scripts: list[Script]) -> list[dict]:
    found = []
    for script in scripts:
        transcript = goldreport.transcript(script.display)
        for name in FIELDS:
            for statement in script.statements[name]:
                quotes = list(statement.spanish)
                if reasons := refused_alone(name, {"spanish": quotes, "english": statement.english}, transcript):
                    found.append({"script": script.id, "field": name, "english": statement.english, "spanish": quotes,
                                  "reasons": reasons})
    return found


def scorer_on_gold(scripts: list[Script]) -> list[dict]:
    found = []
    for script in scripts:
        for name in FIELDS:
            others = tuple(script.gold[other].value for other in script.gold if other != name)
            for statement in script.statements[name]:
                spans = [(script.display.index(q), script.display.index(q) + len(q)) for q in statement.spanish]
                changed, *_ = scoring_es._statement_items(statement.english, spans, script.display,
                                                          script.gold[name].value, script, others)
                if changed:
                    found.append({"script": script.id, "field": name, "english": statement.english,
                                  "changed": [[kind, what] for kind, what, _ in changed],
                                  "known_limit": [scoring_es.known_limit(script.id, kind, statement.english)
                                                  for kind, _, _ in changed]})
    return found


def measure(items: list[scoring_es.SpanishItem], transcripts: dict[str, Transcript],
            scripts: dict[str, Script]) -> dict:
    """The false refusals of a run's items (the header's three lists), counted: the answers' and the gold's."""
    run = [scripts[name] for name in sorted({item.script for item in items})]
    answers = of_answers(items, transcripts, scripts)
    gold = of_gold(run)
    held_out = [script for script in run if script.path.parent == HOLDOUT_DIR]
    return {"count": len(answers["false"]), "answers": answers, "gold_count": len(gold), "gold": gold} | (
        {"scorer_on_gold": scorer_on_gold(held_out)} if held_out else {})


def lines(discarded: dict, found: dict) -> list[str]:
    """For someone to read: the discarded share with the false refusals beside it, and each, by sentence."""
    answers = found["answers"]
    out = [f"Fields with gold discarded: {discarded['discarded']} of {discarded['fields_with_gold']} "
           f"({discarded['share']}); false refusals: {found['count']} of the answers' {answers['refused']} "
           f"statements refused ({len(answers['undecided'])} undecided, {len(answers['right'])} right), "
           f"{found['gold_count']} of the gold; {answers['alongside']} more statements discarded alongside a refused "
           f"one; {len(answers['leave'])} refused for a hedge of leave added (\"may be repeated\", a known false "
           f"refusal)."]
    for entry in answers["false"] + answers["undecided"] + answers["right"]:
        unseen = f"; the scorer compares nothing of {', '.join(entry['unseen'])}" if entry.get("unseen") else ""
        unseen += f"; known scorer limit: {entry['known_limit']}" if entry.get("known_limit") else ""
        out.append(f"  {entry['verdict']}: {entry['variant']} {entry['field']}: {entry['english']!r} for "
                   f"{' / '.join(entry['spanish'])!r} ({', '.join(entry['reasons'])}{unseen}); "
                   f"the gold: {entry['gold']}")
    for entry in found["gold"]:
        out.append(f"  gold: {entry['script']} {entry['field']}: {entry['english']!r} for "
                   f"{' / '.join(entry['spanish'])!r} ({', '.join(entry['reasons'])})")
    for entry in found.get("scorer_on_gold", []):
        out.append(f"  the scorer reads the gold as changed: {entry['script']} {entry['field']}: {entry['english']!r} "
                   f"({', '.join(f'{kind} {what}' for kind, what in entry['changed'])})"
                   + "".join(f"; known scorer limit: {note}" for note in dict.fromkeys(entry["known_limit"]) if note))
    if answers["false"] or answers["undecided"]:
        out.append("  A false refusal is the scorer's reading, of what the fact lists record: a change they don't "
                   "record reads as none (h04's \"for 3 weeks\" for \"hace 3 semanas\": the facts say 3 weeks, not "
                   "when), so each is for a person to read beside the Spanish.")
    return out


def main(argv: list[str] | None = None) -> int:
    from .run_a import stored_item, stored_scripts_dir, text_transcript  # noqa: PLC0415

    argv = sys.argv[1:] if argv is None else argv
    if len(argv) != 1:
        print(__doc__, file=sys.stderr)
        return 2
    stored = json.loads(Path(argv[0]).read_text(encoding="utf-8"))
    scripts = {script.id: script for script in load_scripts(stored_scripts_dir(stored))}
    items = [stored_item(entry) for entry in stored["items"]]
    items = [item for item in items if item.script in scripts and not item.run_as and item.variant.endswith("--text")]
    transcripts = {item.variant: text_transcript(scripts[item.script])[0] for item in items}
    metrics = scoring_es.score(items, scripts)
    changed = scoring_es.changed_lines(metrics["mistranslations"], "Changed from the Spanish cited and shown")
    print("\n".join([*lines(metrics["discarded"], measure(items, transcripts, scripts)), *changed]))
    return 0


if __name__ == "__main__":
    sys.exit(main())
