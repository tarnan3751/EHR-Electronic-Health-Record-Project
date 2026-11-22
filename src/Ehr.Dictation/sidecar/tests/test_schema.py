"""The note schema (src/Ehr.Dictation/schema/note-fields.schema.json): valid JSON Schema, Subjective, Objective,
Assessment and Plan only (the expansion plan's step 14), nothing else allowed in, and the same shape llama.py accepts."""

from __future__ import annotations

import json

import jsonschema
import pytest
from conftest import answer

from ehr_dictation import prompt, validation
from ehr_dictation.llama import ModelOutputInvalid, parse_answer

SCHEMA = json.loads((prompt.SCHEMA_DIRECTORY / prompt.SCHEMA_FILE).read_text(encoding="utf-8"))


def test_the_schema_is_valid_json_schema():
    jsonschema.Draft202012Validator.check_schema(SCHEMA)


def test_only_subjective_objective_assessment_and_plan_each_a_value_and_its_sources():
    assert list(SCHEMA["properties"]) == ["subjective", "objective", "assessment", "plan"]
    assert SCHEMA["required"] == ["subjective", "objective", "assessment", "plan"]
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
    assert parse_answer(valid)[0]["subjective"] == {"value": "Cough.", "sources": ["cough"]}

    missing = json.dumps({"subjective": {"value": "", "sources": []}, "assessment": {"value": "", "sources": []},
                          "plan": {"value": "", "sources": []}})
    with pytest.raises(jsonschema.ValidationError):
        jsonschema.validate(json.loads(missing), SCHEMA)
    with pytest.raises(ModelOutputInvalid):
        parse_answer(missing)


def test_each_fields_definition_in_the_prompt_is_the_schemas_description_word_for_word():
    system = prompt.load().system

    for name, field in SCHEMA["properties"].items():
        assert field["description"], f"{name} has no definition"
        assert f"- {name}: {field['description']}\n" in system + "\n", f"the prompt's definition of {name} differs"


def test_subjective_is_what_the_patient_reports_and_objective_what_the_clinician_found():
    subjective = SCHEMA["properties"]["subjective"]["description"]
    objective = SCHEMA["properties"]["objective"]["description"]

    assert subjective.startswith("What the patient reports") and "readings they took at home" in subjective
    assert objective.startswith("What the clinician found")
    assert "vital signs and examination findings" in objective and "(labs, ECG, imaging)" in objective


def test_a_history_from_before_four_fields_doesnt_match():
    earlier = json.dumps({name: {"value": "", "sources": []} for name in ("history", "assessment", "plan")})
    with pytest.raises(jsonschema.ValidationError):
        jsonschema.validate(json.loads(earlier), SCHEMA)
    with pytest.raises(ModelOutputInvalid):
        parse_answer(earlier)


def test_the_schema_limits_are_the_checks_limits():
    for name, field in SCHEMA["properties"].items():
        value, sources = field["properties"]["value"], field["properties"]["sources"]
        assert value["maxLength"] == validation.LIMITS[name], name
        assert (sources["maxItems"], sources["items"]["maxLength"]) == (validation.MAX_SOURCES, validation.MAX_QUOTE)
