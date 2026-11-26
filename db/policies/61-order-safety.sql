-- Ordering safety checks (the expansion plan's step 8): what a medication order is checked against, in the database,
-- so the order form and the signing trigger (60-orders.sql) ask the same question and get the same answer. Plain SQL,
-- safe to run again.
--
-- ehr.order_safety_warnings lists what is known against ordering a catalog item for a patient:
--   allergy        the patient has an active, coded allergy to an ingredient of the drug.
--   allergy-class  the patient is allergic to a drug of a class the curated cross-reactivity table links to this
--                  one's, or to another drug of the same class when the table names that class
--                  (ehr.drug_class_cross_reactions, each row citing its source: source 'cross-reactivity'); or, as a
--                  note only, to another drug of the same FDA pharmacologic class (source 'fda-class').
--   interaction    an ingredient of the drug has a known interaction with something the patient is taking: an active
--                  medication on the chart, or another active medication order. One row for each source that has
--                  the pair (ehr.drug_interactions: DDInter, the ONC list, an FDA label), so a warning can say where
--                  it comes from.
-- against_sensitive says the medication it names is marked sensitive on the chart: the clinician ordering is told its
-- name, and what is kept on the order, and so in its write audit, is not (60-orders.sql).
-- Only coded data can be checked: an allergy or a medication typed without a code is not seen here, and the order
-- form says so. It runs as the caller, under row-level security: a clinician, who reads the chart anyway. Anyone
-- else sees no allergies and no medications, and gets no rows.
-- Replaced in place on every run, so it keeps its identity and an order form calling it as the file runs is never
-- refused. Dropped first only when the function there has a result other than the one created below, which CREATE OR
-- REPLACE can't change: once, on the upgrade from the step's first build, whose result had no against_sensitive (its
-- one change of result so far), in the same transaction as the rest, so no page finds it missing, though a call in that
-- one moment can fail. Its arguments have never changed. A change to its result changes the text compared here too, or
-- CREATE OR REPLACE refuses it, loudly.
BEGIN;
DO $drop$
BEGIN
    IF to_regprocedure('ehr.order_safety_warnings(uuid, text, uuid)') IS NOT NULL
       AND pg_get_function_result(to_regprocedure('ehr.order_safety_warnings(uuid, text, uuid)'))
           <> 'TABLE(kind text, severity text, ordered text, against text, source text, detail text, citation text, against_sensitive boolean)' THEN
        DROP FUNCTION ehr.order_safety_warnings(uuid, text, uuid);
    END IF;
END
$drop$;
CREATE OR REPLACE FUNCTION ehr.order_safety_warnings(for_patient uuid, catalog_code text, replaces uuid DEFAULT NULL)
    RETURNS TABLE (kind text, severity text, ordered text, against text, source text, detail text, citation text, against_sensitive boolean)
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
        SELECT coalesce(d.ingredient_code, m.code) AS ingredient, bool_or(m.sensitive) AS sensitive
        FROM ehr.medication_statements m
        LEFT JOIN ehr.drug_ingredients d ON d.product_code = m.code
        WHERE m.patient_id = for_patient AND m.status = 'active' AND m.code IS NOT NULL
        GROUP BY 1
        UNION
        SELECT d.ingredient_code, false
        FROM ehr.orders r
        JOIN ehr.order_catalog_items i ON i.code = r.catalog_code
        JOIN ehr.drug_ingredients d ON d.product_code = i.standard_code
        WHERE r.patient_id = for_patient AND r.type = 'medication' AND r.status = 'active' AND r.id IS DISTINCT FROM replaces
    )
    SELECT 'allergy', 'major', n.name, a.display, 'chart', NULL::text, NULL::text, false
    FROM named n JOIN allergies a ON a.code = n.ingredient
    UNION ALL
    -- The same FDA class. For a class the cross-reactivity table names (the penicillins, the cephalosporins, the
    -- penems), where allergy is known to run in the class, it stops the order like a cross-reaction. For any other
    -- class it is a note: FDA classes group drugs by how they act, not by structure, so two drugs of one class often
    -- share nothing an allergy would react to (heparin and the drugs that replace it).
    SELECT DISTINCT 'allergy-class',
        CASE WHEN listed.is_listed THEN 'moderate' ELSE 'minor' END, n.name, a.display,
        CASE WHEN listed.is_listed THEN 'cross-reactivity' ELSE 'fda-class' END,
        CASE WHEN listed.is_listed THEN 'Both are in the class ' || oc.class_name || '.'
             ELSE 'Both are in the FDA class ' || oc.class_name || '. A class groups drugs by how they act, which is not always how allergies run.' END,
        NULL::text, false
    FROM named n
    JOIN ehr.drug_classes oc ON oc.ingredient_code = n.ingredient
    JOIN ehr.drug_classes ac ON ac.class_name = oc.class_name AND ac.ingredient_code <> n.ingredient
    JOIN allergies a ON a.code = ac.ingredient_code
    CROSS JOIN LATERAL (SELECT EXISTS (SELECT 1 FROM ehr.drug_class_cross_reactions x WHERE oc.class_name IN (x.class_a, x.class_b)) AS is_listed) listed
    UNION ALL
    SELECT DISTINCT 'allergy-class', 'moderate', n.name, a.display, 'cross-reactivity', x.note::text, x.source::text, false
    FROM named n
    JOIN ehr.drug_classes oc ON oc.ingredient_code = n.ingredient
    JOIN ehr.drug_class_cross_reactions x ON oc.class_name IN (x.class_a, x.class_b)
    JOIN ehr.drug_classes ac ON ac.class_name = CASE WHEN x.class_a = oc.class_name THEN x.class_b ELSE x.class_a END
    JOIN allergies a ON a.code = ac.ingredient_code
    UNION ALL
    SELECT 'interaction', x.level::text, n.name, coalesce(c.display, t.ingredient), x.source::text, x.detail::text, x.citation::text, t.sensitive
    FROM named n
    -- Not the ordered product's own ingredients: a refill of a combination product is not an interaction with itself.
    JOIN taking t ON t.ingredient NOT IN (SELECT o.ingredient FROM ordered o)
    JOIN ehr.drug_interactions x ON x.ingredient_a = least(n.ingredient, t.ingredient) AND x.ingredient_b = greatest(n.ingredient, t.ingredient)
    LEFT JOIN ehr.clinical_codes c ON c.kind = 'allergen' AND c.system = 'rxnorm' AND c.code = t.ingredient
$$;
REVOKE ALL ON FUNCTION ehr.order_safety_warnings(uuid, text, uuid) FROM PUBLIC;
GRANT EXECUTE ON FUNCTION ehr.order_safety_warnings(uuid, text, uuid) TO ehr_app, ehr_read;
COMMIT;
