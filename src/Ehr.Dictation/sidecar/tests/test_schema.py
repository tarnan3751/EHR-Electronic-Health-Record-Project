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
STATEMENT = SPANISH["properties"]["statements"]["items"]


def spanish_answer(*statements: tuple[str, list[str], str]) -> str:
    return json.dumps({"statements": [{"spanish": quotes, "field": field, "english": english}
                                      for field, quotes, english in statements]})


def test_the_spanish_schema_is_valid_json_schema_and_one_list_of_statements():
    jsonschema.Draft202012Validator.check_schema(SPANISH)
    assert SPANISH["required"] == ["statements"] and list(SPANISH["properties"]) == ["statements"]
    assert SPANISH["additionalProperties"] is False and SPANISH["properties"]["statements"]["type"] == "array"


def test_each_statement_is_its_spanish_first_then_its_field_then_its_english():
    # llama-server writes required properties in the schema's order: the model quotes before it sorts and translates.
    assert STATEMENT["required"] == ["spanish", "field", "english"] == list(STATEMENT["properties"])
    assert STATEMENT["additionalProperties"] is False


def test_a_statements_field_is_one_of_the_four_and_means_what_the_english_field_means_word_for_word():
    choices = STATEMENT["properties"]["field"]["oneOf"]
    assert [choice["const"] for choice in choices] == list(SCHEMA["properties"]) == list(prompt.FIELDS)
    system = prompt.load(language="es").system
    for choice in choices:
        assert choice["description"] == SCHEMA["properties"][choice["const"]]["description"]
        assert f"- {choice['const']}: {choice['description']}\n" in system + "\n"


def test_quotes_and_translations_are_short_and_the_checks_accept_every_statement_the_schema_allows():
    spanish, english = STATEMENT["properties"]["spanish"], STATEMENT["properties"]["english"]
    assert SPANISH["properties"]["statements"]["maxItems"] == 40
    assert (spanish["minItems"], spanish["maxItems"], spanish["items"]["maxLength"], english["maxLength"]) == (1, 2, 200, 240)
    assert spanish["maxItems"] <= translation.MAX_QUOTES and spanish["items"]["maxLength"] <= translation.MAX_QUOTE
    assert english["maxLength"] <= translation.MAX_ENGLISH


def test_llama_py_accepts_exactly_what_the_spanish_schema_does_and_sorts_it_into_the_fields():
    valid = spanish_answer(("plan", ["Suspender metformina."], "Stop metformin."),
                           ("subjective", ["Niega fiebre."], "Denies fever."),
                           ("plan", ["Control en 2 semanas."], "Follow-up in 2 weeks."))
    jsonschema.validate(json.loads(valid), SPANISH)
    fields, unknown = parse_statements(valid)
    assert fields == {
        "subjective": [{"spanish": ["Niega fiebre."], "english": "Denies fever.", "order": 1}],
        "objective": [],
        "assessment": [],
        "plan": [{"spanish": ["Suspender metformina."], "english": "Stop metformin.", "order": 0},
                 {"spanish": ["Control en 2 semanas."], "english": "Follow-up in 2 weeks.", "order": 2}],
    }
    assert unknown == []

    for wrong in (
        answer(("Cough.", ["cough"])),  # the English shape
        json.dumps({name: {"statements": []} for name in prompt.FIELDS}),  # a list in each field, as before
        spanish_answer(("orders", ["Pido una radiografía."], "Chest X-ray.")),  # no orders field
        json.dumps({"statements": [{"spanish": "Suspender metformina.", "field": "plan", "english": "Stop metformin."}]}),
        json.dumps({"statements": [{"spanish": ["Suspender metformina."], "english": "Stop metformin."}]}),
    ):
        with pytest.raises(jsonschema.ValidationError):
            jsonschema.validate(json.loads(wrong), SPANISH)
        with pytest.raises(ModelOutputInvalid):
            parse_statements(wrong)


def test_a_key_beside_the_list_doesnt_match_the_spanish_schema_and_is_reported_as_unknown():
    extra = json.loads(spanish_answer()) | {"orders": []}
    with pytest.raises(jsonschema.ValidationError):
        jsonschema.validate(extra, SPANISH)
    assert parse_statements(json.dumps(extra)) == ({name: [] for name in prompt.FIELDS}, ["orders"])
