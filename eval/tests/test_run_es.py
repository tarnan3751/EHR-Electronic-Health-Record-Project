"""run_es.py, answers_es.py and scoring_es.py's audio measures (the expansion plan's step 17): a recording dictated in
Spanish, and one dictated as English, through the sidecar's own pipeline with stand-in models, scored, and scored
again from what was stored; the Spanish word error rate's normalization, drugs and dates; and a live Spanish
dictation timed chunk by chunk. No test runs a model."""

from __future__ import annotations

import asyncio
import dataclasses
import json

import pytest
from conftest import ScriptedRecognizer, fake_structurer
from test_run import speech, webm

from dictation_scripts import load_scripts as load_english
from dictation_scripts_es import load_scripts
from ehr_eval import answers_es, run_es, scoring_es
from ehr_dictation.asr import Recognition
from ehr_dictation.provenance import Models
from ehr_dictation.quality import OTHER_LANGUAGE
from ehr_dictation.whisper_asr import Heard
from ehr_eval.run_a import check, run_script

MODELS = Models(
    {"repository": "google/medasr", "commit": "a" * 40, "sha256": "b" * 64},
    {"repository": "IFM/K2-Horizon-7B-GGUF", "commit": "c" * 40, "file": "K2.gguf", "sha256": "d" * 64},
    {"repository": "openai/whisper-large-v3-turbo", "commit": "e" * 40, "sha256": "f" * 64},
)
SCRIPTS = {s.id: s for s in load_scripts()}
ENGLISH = {s.id: s for s in load_english()}
E14 = SCRIPTS["e14-nino-sano"]


def statements(script) -> dict:  # noqa: ANN001
    return {"statements": [{"spanish": list(s.spanish), "field": name, "english": s.english}
                           for name, said in script.statements.items() for s in said]}


def pipeline(heard: str, content: dict, english_content: dict | None = None) -> answers_es.Pipeline:
    spanish = fake_structurer(content, language="es")
    return answers_es.Pipeline(MODELS, ScriptedRecognizer(heard), ScriptedRecognizer(heard), spanish._llama, spanish,
                               fake_structurer(english_content or {}))


def variant(**extra) -> dict:  # noqa: ANN003
    return {"id": "e14-nino-sano--clean", "script": "e14-nino-sano", "file": "e14.webm", "container": "webm",
            "accent": "en-voice", "noise": None, "snr_db": None} | extra


async def through(tmp_path, chosen: dict, piped: answers_es.Pipeline) -> scoring_es.SpanishItem:  # noqa: ANN001
    item, transcript, proposal = await answers_es.answer(chosen, tmp_path, piped, list(E14.tags))
    if chosen.get("run_as") == "en":
        run_es.check_english(item, proposal.fields, proposal.unknown, transcript)
    else:
        check(item, proposal.fields, proposal.unknown, transcript)
    return item


def test_a_recording_dictated_in_spanish_goes_through_whisper_and_the_spanish_checks(tmp_path):
    (tmp_path / "e14.webm").write_bytes(webm(speech(4)))

    item = asyncio.run(through(tmp_path, variant(), pipeline(E14.display, statements(E14))))

    assert item.tags[:3] == ["accent:en-voice", "noise:none", "container:webm"]
    assert item.hypothesis.startswith("Revisión de niña sana de 18 meses.")
    assert {name: outcome.status for name, outcome in item.fields.items()} == {
        name: "pending" if said else "empty" for name, said in E14.statements.items()}
    assert item.segments and set(item.timings) == {"decode", "transcribe", "structure"}
    metrics = scoring_es.score_audio([item], SCRIPTS, ENGLISH)
    assert metrics["sorted_every_item"] and metrics["instructions_shown"] == 0
    assert metrics["mistranslations"]["unflagged_total"] == 0


def test_a_spanish_recording_dictated_as_english_goes_the_english_way_and_is_scored_for_fr14_only(tmp_path):
    (tmp_path / "e14.webm").write_bytes(webm(speech(4)))
    english = {name: {"value": "", "sources": []} for name in ("subjective", "objective", "assessment", "plan")}

    item = asyncio.run(through(tmp_path, variant(id="e14-nino-sano--clean--as-en", run_as="en"),
                               pipeline(E14.display, {}, english)))

    assert item.run_as == "en" and "run-as:en" in item.tags
    assert {outcome.status for outcome in item.fields.values()} == {"empty"}
    metrics = scoring_es.score_audio([item], SCRIPTS, ENGLISH)
    (listed,) = metrics["wrong_language"]["items"]
    assert listed["run_as"] == "en" and listed["instructions_shown"] == [] and metrics["fields"]["content"]["f1"] is None


def test_an_instruction_offered_from_a_recording_in_the_wrong_language_counts_against_fr14():
    script = ENGLISH["d43-cough-lipids-orders"]
    offered = scoring_es.SpanishOutcome(status="pending", value=f"Plan to {script.forbidden[0]}.")
    item = scoring_es.SpanishItem(variant=f"{script.id}--clean--as-es", script=script.id, tags=[], hypothesis="x",
                                  sorting="done", fields={"plan": offered}, run_as="es")

    metrics = scoring_es.score_audio([item], SCRIPTS, ENGLISH)

    assert metrics["wrong_language"]["instructions_shown"] == 1 and metrics["instructions_shown"] == 1


def test_stored_answers_are_scored_again_without_a_model(tmp_path):
    (tmp_path / "e14.webm").write_bytes(webm(speech(4)))
    piped = pipeline(E14.display, statements(E14))
    item = asyncio.run(through(tmp_path, variant(), piped))
    text = asyncio.run(run_script(E14, piped.spanish))
    stored = {"provenance": {"scoring": "old"}, "dataset": {"gold_sha256": "old"},
              "items": [dataclasses.asdict(item), dataclasses.asdict(text)]}

    again = run_es.rescore(json.loads(json.dumps(stored)))

    assert again["provenance"]["scoring"] != "old" and again["rescored"]["scoring"] == "old"
    assert [i["fields"]["plan"]["status"] for i in again["items"]] == ["pending", "pending"]
    assert [i["fields"] for i in again["items"]] == [stored["items"][0]["fields"], stored["items"][1]["fields"]]
    assert again["metrics"]["fields"]["content"]["f1"] == again["metrics"]["text"]["fields"]["content"]["f1"]

    stored["items"][0]["hypothesis"] = "Not what was heard."
    with pytest.raises(SystemExit, match="can't be rebuilt"):
        run_es.rescore(json.loads(json.dumps(stored)))


class Hearing:
    """A stand-in for Whisper that hears each passage in turn as it is told, with how likely it was Spanish."""

    def __init__(self, *passages: tuple[str, float, str | None]) -> None:
        self.passages = list(passages)

    def hear(self, samples) -> Heard:  # noqa: ANN001
        text, spanish, unclear = self.passages.pop(0)
        return Heard(Recognition(text, None, unclear), spanish, 0.01, -0.2)

    def recognize(self, samples) -> Recognition:  # noqa: ANN001
        raise AssertionError("the eval asks Whisper what it heard, with its probabilities")


# Decision 6 as built: Spanish is forced on every passage, so English heard as Spanish can come out in Spanish nobody
# said. Run B lists every passage of the code-switching scripts, word for word, with its probability of being Spanish.
def test_every_passage_of_a_code_switching_recording_is_reported_with_how_likely_it_was_spanish(tmp_path):
    (tmp_path / "e10.webm").write_bytes(webm(speech(4)))  # two passages, a second's pause between them
    whisper = Hearing(("Tiene un rash en el brazo derecho.", 0.93, None),
                      ("Se aconsejó al paciente sobre dieta y ejercicio.", 0.62, None))
    piped = dataclasses.replace(pipeline("", {"statements": []}), whisper=whisper)
    e10 = variant(id="e10-codeswitch-dermatitis--clean", script="e10-codeswitch-dermatitis", file="e10.webm")

    item, _, _ = asyncio.run(answers_es.answer(e10, tmp_path, piped, list(SCRIPTS["e10-codeswitch-dermatitis"].tags)))
    metrics = scoring_es.score_audio([item, dataclasses.replace(item, variant="e14-nino-sano--clean",
                                                                script="e14-nino-sano")], SCRIPTS, ENGLISH)

    assert item.heard_as_spanish == [0.93, 0.62] and len(item.segments) == 2
    assert metrics["code_switching"] == {"passages": 2, "items": [{
        "variant": "e10-codeswitch-dermatitis--clean", "passages": [
            {"text": "Tiene un rash en el brazo derecho.", "spanish": 0.93, "unclear": None},
            {"text": "Se aconsejó al paciente sobre dieta y ejercicio.", "spanish": 0.62, "unclear": None}]}]}
    printed = run_es.code_switching(metrics)
    assert "0.620" in printed and "Se aconsejó al paciente sobre dieta y ejercicio." in printed


def test_the_english_script_dictated_as_spanish_is_reported_too_and_a_passage_heard_as_english_is_marked(tmp_path):
    (tmp_path / "d43.webm").write_bytes(webm(speech(4)))
    whisper = Hearing(("Tos desde hace dos semanas.", 0.31, OTHER_LANGUAGE),
                      ("Pido un perfil de lípidos.", 0.12, OTHER_LANGUAGE))
    piped = dataclasses.replace(pipeline("", {"statements": []}), whisper=whisper)
    d43 = variant(id="d43-cough-lipids-orders--clean--as-es", script="d43-cough-lipids-orders", file="d43.webm",
                  run_as="es")

    item, transcript, _ = asyncio.run(answers_es.answer(d43, tmp_path, piped, []))
    (listed,) = scoring_es.score_audio([item], SCRIPTS, ENGLISH)["code_switching"]["items"]

    assert len(transcript.unclear_ranges()) == 2
    assert [(p["spanish"], p["unclear"]) for p in listed["passages"]] == [(0.31, OTHER_LANGUAGE), (0.12, OTHER_LANGUAGE)]


def test_a_live_dictation_in_spanish_is_timed_chunk_by_chunk(tmp_path):
    data = webm(speech(6))

    times = asyncio.run(answers_es.live_chunks(data, 6.0, pipeline("Tos seca.", {})))

    assert len(times) >= 6 and all(t < 15 for t in times)


@pytest.mark.parametrize(("said", "written"), [
    ("Temperatura treinta y siete coma ocho grados.", "Temperatura 37,8 grados."),
    ("tensión ciento cuarenta y dos sobre ochenta y ocho", "Tensión 142/88."),
    ("saturación noventa y tres por ciento", "saturación 93 %"),
    ("metformina mil miligramos", "Metformina 1.000 mg"),
])
def test_the_word_error_rate_reads_numbers_as_digits_however_said(said, written):
    said_words, written_words = scoring_es.spanish_words(said), scoring_es.spanish_words(written)
    if "mg" in written_words:  # a unit's abbreviation is a word of its own: only the numbers are compared here
        said_words = [w for w in said_words if w != "miligramos"]
        written_words = [w for w in written_words if w != "mg"]
    assert said_words == written_words


def test_accents_count_in_the_word_error_rate_and_not_in_the_one_beside_it():
    item = scoring_es.SpanishItem(variant="e14--clean", script="e14-nino-sano", tags=[], sorting="done",
                                  hypothesis=E14.display.replace("Revisión", "Revision"))

    wer = scoring_es._wer([item], SCRIPTS)

    assert wer["overall"] > 0 and wer["no_accents"] == 0


@pytest.mark.parametrize(("text", "said"), [
    ("Inició el 3 de marzo de 2026.", True),
    ("Inició el tres de marzo.", True),
    ("Inició el 13 de marzo.", False),
    ("Inició en marzo.", False),
])
def test_a_date_is_said_with_its_day_and_month(text, said):
    assert scoring_es.date_said("2026-03-03", text) is said


def test_a_drug_is_heard_word_for_word_accents_aside():
    item = scoring_es.SpanishItem(variant="e15--clean", script="e15-multiproblema", tags=[], sorting="done",
                                  hypothesis="Toma acido acetilsalicilico y metformina; tiotropo una vez.")

    drugs = scoring_es._drugs([item], SCRIPTS)

    assert drugs["said"] == len(SCRIPTS["e15-multiproblema"].drugs)
    assert drugs["exact"] == 2 and drugs["within_2_letters"] == 3
