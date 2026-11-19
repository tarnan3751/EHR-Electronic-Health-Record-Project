-- Hand-written, not generated: the reference range of a vital sign for each age band (the expansion plan's step 9b,
-- Tahseen's decision of 19 November 2026). Heart rate (8867-4), respiratory rate (9279-1) and systolic and diastolic
-- blood pressure (8480-6, 8462-4) for an infant (under 1 year), a young child (1 to 5), an older child (6 to 11), an
-- adolescent (12 to 17) and an adult. Temperature and oxygen saturation keep the one range ehr.observation_codes
-- gives them. Ages are whole months: from from_months, up to but not including to_months.
--
-- Each row names its source. What each source defines, the figures it prints and how they were fitted to five bands
-- are in docs/expansion/reference-data.md. They are for flagging synthetic values on a flowsheet and are not
-- validated for clinical use. A value below low or above high is flagged; an end that no source gives is NULL, and a
-- diastolic pressure under 1 year has no row, so it is never flagged.
--
-- The same rows are Ehr.Data.Reference.VitalRanges, which the seed uses; a test fails if the two differ. Applied as
-- ehr_owner after db/policies and after 30-observations.sql, whose codes it refers to; safe to run again: it makes
-- the table hold exactly these rows.
BEGIN;
CREATE TEMPORARY TABLE incoming_vital_ranges (code text, band text, from_months int, to_months int, low numeric, high numeric, source text) ON COMMIT DROP;
INSERT INTO incoming_vital_ranges (code, band, from_months, to_months, low, high, source) VALUES
    ('8867-4', 'infant', 0, 12, 91, 186,
     'Bonafide CP et al., Pediatrics 2013;131(4):e1150 (hospitalized children), 1st and 99th percentiles, widest across the band''s ages'),
    ('8867-4', 'young-child', 12, 72, 66, 173,
     'Bonafide CP et al., Pediatrics 2013;131(4):e1150 (hospitalized children), 1st and 99th percentiles, widest across the band''s ages'),
    ('8867-4', 'older-child', 72, 144, 56, 155,
     'Bonafide CP et al., Pediatrics 2013;131(4):e1150 (hospitalized children), 1st and 99th percentiles, widest across the band''s ages'),
    ('8867-4', 'adolescent', 144, 216, 48, 138,
     'Bonafide CP et al., Pediatrics 2013;131(4):e1150 (hospitalized children), 1st and 99th percentiles, widest across the band''s ages'),
    ('8867-4', 'adult', 216, NULL, 60, 100,
     'MedlinePlus (NIH), Vital signs, reviewed 2025-01-01'),
    ('9279-1', 'infant', 0, 12, 19, 76,
     'Bonafide CP et al., Pediatrics 2013;131(4):e1150 (hospitalized children), 1st and 99th percentiles, widest across the band''s ages'),
    ('9279-1', 'young-child', 12, 72, 14, 60,
     'Bonafide CP et al., Pediatrics 2013;131(4):e1150 (hospitalized children), 1st and 99th percentiles, widest across the band''s ages'),
    ('9279-1', 'older-child', 72, 144, 13, 46,
     'Bonafide CP et al., Pediatrics 2013;131(4):e1150 (hospitalized children), 1st and 99th percentiles, widest across the band''s ages'),
    ('9279-1', 'adolescent', 144, 216, 11, 35,
     'Bonafide CP et al., Pediatrics 2013;131(4):e1150 (hospitalized children), 1st and 99th percentiles, widest across the band''s ages'),
    ('9279-1', 'adult', 216, NULL, 12, 20,
     'StatPearls, Vital Sign Assessment (NIH Bookshelf NBK553213)'),
    ('8480-6', 'infant', 0, 12, 70, NULL,
     'No source gives an upper value under 1 year; low: PALS hypotension threshold (70 mmHg under 1 year, 70 + 2 x age to 10 years, 90 above), at the band''s youngest age'),
    ('8480-6', 'young-child', 12, 72, 72, 103,
     'Flynn JT et al., AAP Clinical Practice Guideline, Pediatrics 2017;140(3):e20171904, Table 6 screening values, highest across the band''s ages and both sexes; low: PALS hypotension threshold (70 mmHg under 1 year, 70 + 2 x age to 10 years, 90 above), at the band''s youngest age'),
    ('8480-6', 'older-child', 72, 144, 82, 110,
     'Flynn JT et al., AAP Clinical Practice Guideline, Pediatrics 2017;140(3):e20171904, Table 6 screening values, highest across the band''s ages and both sexes; low: PALS hypotension threshold (70 mmHg under 1 year, 70 + 2 x age to 10 years, 90 above), at the band''s youngest age'),
    ('8480-6', 'adolescent', 144, 216, 90, 119,
     'Flynn JT et al., AAP Clinical Practice Guideline, Pediatrics 2017;140(3):e20171904, Table 6 screening values, highest across the band''s ages and both sexes; low: PALS hypotension threshold (70 mmHg under 1 year, 70 + 2 x age to 10 years, 90 above), at the band''s youngest age'),
    ('8480-6', 'adult', 216, NULL, 90, 129,
     'MedlinePlus (NIH), Vital signs (low); AHA/ACC adult categories, stage 1 from 130 or 80 (high)'),
    ('8462-4', 'young-child', 12, 72, NULL, 63,
     'Flynn JT et al., AAP Clinical Practice Guideline, Pediatrics 2017;140(3):e20171904, Table 6 screening values, highest across the band''s ages and both sexes; no source gives a lower value'),
    ('8462-4', 'older-child', 72, 144, NULL, 73,
     'Flynn JT et al., AAP Clinical Practice Guideline, Pediatrics 2017;140(3):e20171904, Table 6 screening values, highest across the band''s ages and both sexes; no source gives a lower value'),
    ('8462-4', 'adolescent', 144, 216, NULL, 79,
     'Flynn JT et al., AAP Clinical Practice Guideline, Pediatrics 2017;140(3):e20171904, Table 6 screening values, highest across the band''s ages and both sexes; no source gives a lower value'),
    ('8462-4', 'adult', 216, NULL, 60, 79,
     'MedlinePlus (NIH), Vital signs (low); AHA/ACC adult categories, stage 1 from 130 or 80 (high)');
DELETE FROM ehr.vital_reference_ranges r WHERE NOT EXISTS (SELECT 1 FROM incoming_vital_ranges i WHERE i.code = r.code AND i.band = r.band);
INSERT INTO ehr.vital_reference_ranges (code, band, from_months, to_months, low, high, source)
SELECT code, band, from_months, to_months, low, high, source FROM incoming_vital_ranges
ON CONFLICT (code, band) DO UPDATE SET from_months = excluded.from_months, to_months = excluded.to_months, low = excluded.low, high = excluded.high, source = excluded.source;
COMMIT;
