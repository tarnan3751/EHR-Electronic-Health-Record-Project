-- Ordering safety checks (the expansion plan's step 8): what a medication order is checked against, in the database,
-- so the order form and the signing trigger (60-orders.sql) ask the same question and get the same answer. Plain SQL,
-- safe to run again.
--
-- ehr.order_safety_warnings lists what is known against ordering a catalog item for a patient:
--   allergy        the patient has an active, coded allergy to an ingredient of the drug.
--   allergy-class  the patient is allergic to another drug of the same FDA pharmacologic class, or of a class the
--                  curated cross-reactivity table links to it (ehr.drug_class_cross_reactions, each row citing its
--                  source).
--   interaction    an ingredient of the drug has a known interaction with something the patient is taking: an active
--                  medication on the chart, or another active medication order. One row for each source that has
--                  the pair (ehr.drug_interactions: DDInter, the ONC list, an FDA label), so a warning can say where
--                  it comes from.
-- Only coded data can be checked: an allergy or a medication typed without a code is not seen here, and the order
-- form says so. It runs as the caller, under row-level security: a clinician, who reads the chart anyway. Anyone
-- else sees no allergies and no medications, and gets no rows.
CREATE OR REPLACE FUNCTION ehr.order_safety_warnings(for_patient uuid, catalog_code text, replaces uuid DEFAULT NULL)
    RETURNS TABLE (kind text, severity text, ordered text, against text, source text, detail text, citation text)
    LANGUAGE sql
    STABLE
    SET search_path = pg_catalog, pg_temp
AS $$
    WITH ordered AS (
        SELECT DISTINCT d.ingredient_code AS ingredient
        FROM ehr.order_catalog_items i
        JOIN ehr.drug_ingredients d ON d.product_code = i.standard_code
        WHERE i.code = catalog_code AND i.type = 'medication' AND i.standard_system = 'rxnorm'
    ),
    named AS (
        SELECT o.ingredient, coalesce(c.display, o.ingredient) AS name
        FROM ordered o
        LEFT JOIN ehr.clinical_codes c ON c.kind = 'allergen' AND c.system = 'rxnorm' AND c.code = o.ingredient
    ),
    allergies AS (
        SELECT a.code, a.display
        FROM ehr.allergy_intolerances a
        WHERE a.patient_id = for_patient AND a.clinical_status = 'active' AND a.code_system = 'rxnorm' AND a.code IS NOT NULL
    ),
    taking AS (
        -- A medication on the chart is coded as a product, sometimes as an ingredient; either way, its ingredients.
        SELECT coalesce(d.ingredient_code, m.code) AS ingredient
        FROM ehr.medication_statements m
        LEFT JOIN ehr.drug_ingredients d ON d.product_code = m.code
        WHERE m.patient_id = for_patient AND m.status = 'active' AND m.code IS NOT NULL
        UNION
        SELECT d.ingredient_code
        FROM ehr.orders r
        JOIN ehr.order_catalog_items i ON i.code = r.catalog_code
        JOIN ehr.drug_ingredients d ON d.product_code = i.standard_code
        WHERE r.patient_id = for_patient AND r.type = 'medication' AND r.status = 'active' AND r.id IS DISTINCT FROM replaces
    )
    SELECT 'allergy', 'major', n.name, a.display, 'chart', NULL::text, NULL::text
    FROM named n JOIN allergies a ON a.code = n.ingredient
    UNION ALL
    SELECT DISTINCT 'allergy-class', 'moderate', n.name, a.display, 'fda-class',
        'Both are in the class ' || oc.class_name || '.', NULL::text
    FROM named n
    JOIN ehr.drug_classes oc ON oc.ingredient_code = n.ingredient
    JOIN ehr.drug_classes ac ON ac.class_name = oc.class_name AND ac.ingredient_code <> n.ingredient
    JOIN allergies a ON a.code = ac.ingredient_code
    UNION ALL
    SELECT DISTINCT 'allergy-class', 'moderate', n.name, a.display, 'cross-reactivity', x.note::text, x.source::text
    FROM named n
    JOIN ehr.drug_classes oc ON oc.ingredient_code = n.ingredient
    JOIN ehr.drug_class_cross_reactions x ON oc.class_name IN (x.class_a, x.class_b)
    JOIN ehr.drug_classes ac ON ac.class_name = CASE WHEN x.class_a = oc.class_name THEN x.class_b ELSE x.class_a END
    JOIN allergies a ON a.code = ac.ingredient_code
    UNION ALL
    SELECT 'interaction', x.level::text, n.name, coalesce(c.display, t.ingredient), x.source::text, x.detail::text, x.citation::text
    FROM named n
    JOIN taking t ON t.ingredient <> n.ingredient
    JOIN ehr.drug_interactions x ON x.ingredient_a = least(n.ingredient, t.ingredient) AND x.ingredient_b = greatest(n.ingredient, t.ingredient)
    LEFT JOIN ehr.clinical_codes c ON c.kind = 'allergen' AND c.system = 'rxnorm' AND c.code = t.ingredient
$$;
REVOKE ALL ON FUNCTION ehr.order_safety_warnings(uuid, text, uuid) FROM PUBLIC;
GRANT EXECUTE ON FUNCTION ehr.order_safety_warnings(uuid, text, uuid) TO ehr_app, ehr_read;
