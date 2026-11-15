"""The note schema (src/Ehr.Dictation/schema/note-fields.schema.json): valid JSON Schema, History, Assessment and Plan
only, nothing else allowed in, and the same shape llama.py accepts."""

from __future__ import annotations

import json

import jsonschema
import pytest
from conftest import answer

from ehr_dictation import prompt
from ehr_dictation.llama import ModelOutputInvalid, parse_answer

SCHEMA = json.loads((prompt.SCHEMA_DIRECTORY / prompt.SCHEMA_FILE).read_text(encoding="utf-8"))


def test_the_schema_is_valid_json_schema():
    jsonschema.Draft202012Validator.check_schema(SCHEMA)


def test_only_history_assessment_and_plan_each_a_value_and_its_sources():
    assert list(SCHEMA["properties"]) == ["history", "assessment", "plan"]
    assert SCHEMA["required"] == ["history", "assessment", "plan"]
    assert SCHEMA["additionalProperties"] is False
    for field in SCHEMA["properties"].values():
        assert field["required"] == ["value", "sources"] and field["additionalProperties"] is False


def test_a_sorted_transcript_matches():
    jsonschema.validate(json.loads(answer(("Cough.", ["cough for three days"]), plan=("Rest.", ["rest"]))), SCHEMA)


@pytest.mark.parametrize("extra", [{"orders": {"value": "CBC", "sources": ["order a CBC"]}},
                                   {"allergies": {"value": "penicillin", "sources": []}}])
def test_an_order_or_allergy_field_doesnt_match(extra):
    with pytest.raises(jsonschema.ValidationError):
        jsonschema.validate(json.loads(answer(**extra)), SCHEMA)


def test_llama_py_accepts_exactly_what_the_schema_does():
    valid = answer(("Cough.", ["cough"]))
    jsonschema.validate(json.loads(valid), SCHEMA)
    assert parse_answer(valid)[0]["history"] == {"value": "Cough.", "sources": ["cough"]}

    missing = json.dumps({"history": {"value": "", "sources": []}, "assessment": {"value": "", "sources": []}})
    with pytest.raises(jsonschema.ValidationError):
        jsonschema.validate(json.loads(missing), SCHEMA)
    with pytest.raises(ModelOutputInvalid):
        parse_answer(missing)


def test_each_fields_definition_in_the_prompt_is_the_schemas_description_word_for_word():
    system = prompt.load().system

    for name, field in SCHEMA["properties"].items():
        assert field["description"], f"{name} has no definition"
        assert f"- {name}: {field['description']}\n" in system + "\n", f"the prompt's definition of {name} differs"


def test_history_is_the_history_of_present_illness_with_findings_and_reviewed_results():
    history = SCHEMA["properties"]["history"]["description"]

    assert history.startswith("The history of present illness")
    assert "vital signs and examination findings" in history and "(labs, ECG, imaging)" in history
