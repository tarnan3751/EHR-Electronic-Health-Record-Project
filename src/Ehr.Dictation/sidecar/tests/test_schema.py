"""The note schema (src/Ehr.Dictation/schema/note-fields.schema.json): valid JSON Schema, Subjective, Objective,
Assessment and Plan only (the expansion plan's step 14), nothing else allowed in, and the same shape llama.py accepts."""

from __future__ import annotations

import json

import jsonschema
import pytest
from conftest import answer

from ehr_dictation import prompt, translation, validation
from ehr_dictation.llama import ModelOutputInvalid, parse_answer, parse_statements

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


# ---- The Spanish note schema (the expansion plan's step 17) ---------------------------------------------------------

SPANISH = json.loads((prompt.SCHEMA_DIRECTORY / prompt.LANGUAGES["es"][1]).read_text(encoding="utf-8"))


def spanish_answer(**fields: list[dict]) -> str:
    return json.dumps({name: {"statements": fields.get(name, [])} for name in prompt.FIELDS})


def test_the_spanish_schema_is_valid_json_schema_with_the_same_four_fields():
    jsonschema.Draft202012Validator.check_schema(SPANISH)
    assert list(SPANISH["properties"]) == list(SCHEMA["properties"]) == list(SPANISH["required"])
    assert SPANISH["additionalProperties"] is False


def test_each_field_is_statements_the_spanish_first_then_the_english():
    for field in SPANISH["properties"].values():
        assert field["required"] == ["statements"] and field["additionalProperties"] is False
        statement = field["properties"]["statements"]["items"]
        # llama-server writes required properties in the schema's order: the model quotes before it translates.
        assert statement["required"] == ["spanish", "english"] and list(statement["properties"]) == ["spanish", "english"]
        assert statement["additionalProperties"] is False


def test_the_spanish_fields_mean_what_the_english_fields_mean_and_the_prompt_says_so_word_for_word():
    system = prompt.load(language="es").system
    for name, field in SPANISH["properties"].items():
        assert field["description"] == SCHEMA["properties"][name]["description"]
        assert f"- {name}: {field['description']}\n" in system + "\n"


def test_the_spanish_schema_limits_are_the_checks_limits():
    for field in SPANISH["properties"].values():
        statements = field["properties"]["statements"]
        spanish, english = statements["items"]["properties"]["spanish"], statements["items"]["properties"]["english"]
        assert statements["maxItems"] == translation.MAX_STATEMENTS
        assert (spanish["maxItems"], spanish["items"]["maxLength"]) == (translation.MAX_QUOTES, translation.MAX_QUOTE)
        assert english["maxLength"] == translation.MAX_ENGLISH


def test_llama_py_accepts_exactly_what_the_spanish_schema_does():
    valid = spanish_answer(plan=[{"spanish": ["Suspender metformina."], "english": "Stop metformin."}])
    jsonschema.validate(json.loads(valid), SPANISH)
    assert parse_statements(valid)[0]["plan"] == [{"spanish": ["Suspender metformina."], "english": "Stop metformin."}]

    for wrong in (
        answer(("Cough.", ["cough"])),  # the English shape
        spanish_answer(plan=[{"spanish": "Suspender metformina.", "english": "Stop metformin."}]),
        json.dumps({"plan": {"statements": []}}),
    ):
        with pytest.raises(jsonschema.ValidationError):
            jsonschema.validate(json.loads(wrong), SPANISH)
        with pytest.raises(ModelOutputInvalid):
            parse_statements(wrong)


def test_an_orders_field_doesnt_match_the_spanish_schema_and_is_reported_as_unknown():
    extra = json.loads(spanish_answer()) | {"orders": {"statements": []}}
    with pytest.raises(jsonschema.ValidationError):
        jsonschema.validate(extra, SPANISH)
    assert parse_statements(json.dumps(extra))[1] == ["orders"]
