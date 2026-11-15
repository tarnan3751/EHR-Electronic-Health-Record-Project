# Negation cues: strict against relaxed

The owner asked whether the checks should let a value use another negation cue than its source ("no chest pain" from
"denies chest pain"), and to apply that only if the measurements justify it. Measured offline, on the answers of the
reasoning-off run in `latest.json` (82 recordings, run 2026-11-15 on an Apple M5; not a baseline): its stored proposals
were checked again with `python -m ehr_eval.rescore`, once with the checks as committed (strict) and once with
negation cues (no, not, denies, denied, deny, without, negative) counted as one word in grounding and word order
(relaxed). Nothing else differed.

| Measure | Strict | Relaxed |
|---|---|---|
| Field content F1, rule-following gold | 0.6677 | 0.6677 |
| Field detection F1, rule-following gold | 0.8273 | 0.8273 |
| Field content F1, clinical gold | 0.6621 | 0.6621 |
| Field detection F1, clinical gold | 0.8273 | 0.8273 |
| Values passed, of 217 proposed | 170 | 170 |
| Order or allergy changes shown | 0 | 0 |
| Retracted values shown, with or without a warning | 7 | 7 |
| Drugs no gold value carries, shown | 1 | 1 |

## Values that pass only under the relaxation

None. K2 Horizon kept every negation cue as the clinician said it, as the prompt's rule 3 asks, so no value was
discarded for rewording one, and none failed as a dropped negation.

The relaxation is therefore not applied: nothing measured would gain from it.
