using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ehr.Migrations
{
    /// <inheritdoc />
    public partial class ChildrenRanges : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropPrimaryKey(
                name: "pk_vital_reference_ranges",
                schema: "ehr",
                table: "vital_reference_ranges");

            migrationBuilder.DropCheckConstraint(
                name: "ck_vital_reference_ranges_band",
                schema: "ehr",
                table: "vital_reference_ranges");

            migrationBuilder.DropColumn(
                name: "band",
                schema: "ehr",
                table: "vital_reference_ranges");

            migrationBuilder.AddColumn<string>(
                name: "sex",
                schema: "ehr",
                table: "vital_reference_ranges",
                type: "character varying(10)",
                maxLength: 10,
                nullable: false,
                defaultValue: "any");

            migrationBuilder.AddColumn<string>(
                name: "kind",
                schema: "ehr",
                table: "vital_reference_ranges",
                type: "character varying(20)",
                maxLength: 20,
                nullable: false,
                defaultValue: "normal");

            migrationBuilder.AddColumn<string>(
                name: "reference_kind",
                schema: "ehr",
                table: "observations",
                type: "character varying(20)",
                maxLength: 20,
                nullable: false,
                defaultValue: "normal");

            migrationBuilder.AddPrimaryKey(
                name: "pk_vital_reference_ranges",
                schema: "ehr",
                table: "vital_reference_ranges",
                columns: new[] { "code", "sex", "from_months" });

            migrationBuilder.AddCheckConstraint(
                name: "ck_vital_reference_ranges_kind",
                schema: "ehr",
                table: "vital_reference_ranges",
                sql: "kind IN ('normal', 'screening')");

            migrationBuilder.AddCheckConstraint(
                name: "ck_vital_reference_ranges_screening",
                schema: "ehr",
                table: "vital_reference_ranges",
                sql: "kind <> 'screening' OR (low IS NULL AND high IS NOT NULL)");

            migrationBuilder.AddCheckConstraint(
                name: "ck_vital_reference_ranges_sex",
                schema: "ehr",
                table: "vital_reference_ranges",
                sql: "sex IN ('male', 'female', 'any')");

            migrationBuilder.AddCheckConstraint(
                name: "ck_observations_reference_kind",
                schema: "ehr",
                table: "observations",
                sql: "reference_kind IN ('normal', 'screening')");

            // Written by hand (the expansion plan's step 9d). Children's ranges come from the primary sources: heart and
            // respiratory rate from Fleming et al.'s 13 age groups (1st and 99th centiles), and blood pressure from the
            // AAP guideline's Table 6 by year of age and sex, a screening value at or above which a reading needs
            // follow-up. The rows are repeated here from db/reference/35-vital-ranges.sql, which runs after the
            // migrations: the table is given them now, where the codes they name are loaded (a database being set up
            // has none yet, and db/reference fills it), so that readings and ranges agree the moment this ends. Every
            // vital sign of these four is then given the range for the patient's age on the day it was taken and sex,
            // as ehr.file_result gives one (db/policies/75-results.sql), entries in error too. As in VitalCorrections:
            // forced row-level security is lifted on observations for the owner while it runs and the audit trigger
            // paused, since no value changes; the patients table is read through its owner's policy. Only the range
            // columns change, which the readings' check (observations_reading_changed) doesn't run for, so nothing is
            // left waiting at the commit. It reports, for each sign, how many readings were re-ranged and how their
            // flags changed, and the totals, with RAISE NOTICE, which the migration job prints (DatabaseNotices). Its
            // run time is in docs/expansion/PLAN.md's log, step 9d.
            migrationBuilder.Sql("""
                DO $$
                DECLARE
                    forced boolean := (SELECT relforcerowsecurity FROM pg_class WHERE oid = 'ehr.observations'::regclass);
                    audited boolean := EXISTS (SELECT 1 FROM pg_trigger WHERE tgrelid = 'ehr.observations'::regclass AND tgname = 'observations_audit');
                    sign record;
                    change record;
                    counted bigint;
                    detail text;
                BEGIN
                    CREATE TEMPORARY TABLE ranges_now (code text, sex text, from_months int, to_months int, low numeric, high numeric, kind text, source text) ON COMMIT DROP;
                    INSERT INTO ranges_now (code, sex, from_months, to_months, low, high, kind, source) VALUES
                    ('8867-4', 'any', 0, 3::int, 107::numeric, 181::numeric, 'normal',
                     'Fleming S et al., Lancet 2011;377:1011-18, Web Appendix, Web Table 5 (heart rate), 1st and 99th centiles, page 12'),
                    ('8867-4', 'any', 3, 6::int, 104::numeric, 175::numeric, 'normal',
                     'Fleming S et al., Lancet 2011;377:1011-18, Web Appendix, Web Table 5 (heart rate), 1st and 99th centiles, page 12'),
                    ('8867-4', 'any', 6, 9::int, 98::numeric, 168::numeric, 'normal',
                     'Fleming S et al., Lancet 2011;377:1011-18, Web Appendix, Web Table 5 (heart rate), 1st and 99th centiles, page 12'),
                    ('8867-4', 'any', 9, 12::int, 93::numeric, 161::numeric, 'normal',
                     'Fleming S et al., Lancet 2011;377:1011-18, Web Appendix, Web Table 5 (heart rate), 1st and 99th centiles, page 12'),
                    ('8867-4', 'any', 12, 18::int, 88::numeric, 156::numeric, 'normal',
                     'Fleming S et al., Lancet 2011;377:1011-18, Web Appendix, Web Table 5 (heart rate), 1st and 99th centiles, page 12'),
                    ('8867-4', 'any', 18, 24::int, 82::numeric, 149::numeric, 'normal',
                     'Fleming S et al., Lancet 2011;377:1011-18, Web Appendix, Web Table 5 (heart rate), 1st and 99th centiles, page 12'),
                    ('8867-4', 'any', 24, 36::int, 76::numeric, 142::numeric, 'normal',
                     'Fleming S et al., Lancet 2011;377:1011-18, Web Appendix, Web Table 5 (heart rate), 1st and 99th centiles, page 12'),
                    ('8867-4', 'any', 36, 48::int, 70::numeric, 136::numeric, 'normal',
                     'Fleming S et al., Lancet 2011;377:1011-18, Web Appendix, Web Table 5 (heart rate), 1st and 99th centiles, page 12'),
                    ('8867-4', 'any', 48, 72::int, 65::numeric, 131::numeric, 'normal',
                     'Fleming S et al., Lancet 2011;377:1011-18, Web Appendix, Web Table 5 (heart rate), 1st and 99th centiles, page 12'),
                    ('8867-4', 'any', 72, 96::int, 59::numeric, 123::numeric, 'normal',
                     'Fleming S et al., Lancet 2011;377:1011-18, Web Appendix, Web Table 5 (heart rate), 1st and 99th centiles, page 12'),
                    ('8867-4', 'any', 96, 144::int, 52::numeric, 115::numeric, 'normal',
                     'Fleming S et al., Lancet 2011;377:1011-18, Web Appendix, Web Table 5 (heart rate), 1st and 99th centiles, page 12'),
                    ('8867-4', 'any', 144, 180::int, 47::numeric, 108::numeric, 'normal',
                     'Fleming S et al., Lancet 2011;377:1011-18, Web Appendix, Web Table 5 (heart rate), 1st and 99th centiles, page 12'),
                    ('8867-4', 'any', 180, 216::int, 43::numeric, 104::numeric, 'normal',
                     'Fleming S et al., Lancet 2011;377:1011-18, Web Appendix, Web Table 5 (heart rate), 1st and 99th centiles, page 12'),
                    ('8867-4', 'any', 216, NULL::int, 60::numeric, 100::numeric, 'normal',
                     'MedlinePlus (NIH), Vital signs, reviewed 2025-01-01'),
                    ('9279-1', 'any', 0, 3::int, 25::numeric, 66::numeric, 'normal',
                     'Fleming S et al., Lancet 2011;377:1011-18, Web Appendix, Web Table 4 (respiratory rate), 1st and 99th centiles, page 12'),
                    ('9279-1', 'any', 3, 6::int, 24::numeric, 64::numeric, 'normal',
                     'Fleming S et al., Lancet 2011;377:1011-18, Web Appendix, Web Table 4 (respiratory rate), 1st and 99th centiles, page 12'),
                    ('9279-1', 'any', 6, 9::int, 23::numeric, 61::numeric, 'normal',
                     'Fleming S et al., Lancet 2011;377:1011-18, Web Appendix, Web Table 4 (respiratory rate), 1st and 99th centiles, page 12'),
                    ('9279-1', 'any', 9, 12::int, 22::numeric, 58::numeric, 'normal',
                     'Fleming S et al., Lancet 2011;377:1011-18, Web Appendix, Web Table 4 (respiratory rate), 1st and 99th centiles, page 12'),
                    ('9279-1', 'any', 12, 18::int, 21::numeric, 53::numeric, 'normal',
                     'Fleming S et al., Lancet 2011;377:1011-18, Web Appendix, Web Table 4 (respiratory rate), 1st and 99th centiles, page 12'),
                    ('9279-1', 'any', 18, 24::int, 19::numeric, 46::numeric, 'normal',
                     'Fleming S et al., Lancet 2011;377:1011-18, Web Appendix, Web Table 4 (respiratory rate), 1st and 99th centiles, page 12'),
                    ('9279-1', 'any', 24, 36::int, 18::numeric, 38::numeric, 'normal',
                     'Fleming S et al., Lancet 2011;377:1011-18, Web Appendix, Web Table 4 (respiratory rate), 1st and 99th centiles, page 12'),
                    ('9279-1', 'any', 36, 48::int, 17::numeric, 33::numeric, 'normal',
                     'Fleming S et al., Lancet 2011;377:1011-18, Web Appendix, Web Table 4 (respiratory rate), 1st and 99th centiles, page 12'),
                    ('9279-1', 'any', 48, 72::int, 17::numeric, 29::numeric, 'normal',
                     'Fleming S et al., Lancet 2011;377:1011-18, Web Appendix, Web Table 4 (respiratory rate), 1st and 99th centiles, page 12'),
                    ('9279-1', 'any', 72, 96::int, 16::numeric, 27::numeric, 'normal',
                     'Fleming S et al., Lancet 2011;377:1011-18, Web Appendix, Web Table 4 (respiratory rate), 1st and 99th centiles, page 12'),
                    ('9279-1', 'any', 96, 144::int, 14::numeric, 25::numeric, 'normal',
                     'Fleming S et al., Lancet 2011;377:1011-18, Web Appendix, Web Table 4 (respiratory rate), 1st and 99th centiles, page 12'),
                    ('9279-1', 'any', 144, 180::int, 12::numeric, 23::numeric, 'normal',
                     'Fleming S et al., Lancet 2011;377:1011-18, Web Appendix, Web Table 4 (respiratory rate), 1st and 99th centiles, page 12'),
                    ('9279-1', 'any', 180, 216::int, 11::numeric, 22::numeric, 'normal',
                     'Fleming S et al., Lancet 2011;377:1011-18, Web Appendix, Web Table 4 (respiratory rate), 1st and 99th centiles, page 12'),
                    ('9279-1', 'any', 216, NULL::int, 12::numeric, 20::numeric, 'normal',
                     'StatPearls, Vital Sign Assessment (NIH Bookshelf NBK553213)'),
                    ('8480-6', 'male', 12, 24::int, NULL::numeric, 98::numeric, 'screening',
                     'Flynn JT et al., Pediatrics 2017;140(3):e20171904, Table 6 (Screening BP Values Requiring Further Evaluation), page 15, boys'),
                    ('8480-6', 'male', 24, 36::int, NULL::numeric, 100::numeric, 'screening',
                     'Flynn JT et al., Pediatrics 2017;140(3):e20171904, Table 6 (Screening BP Values Requiring Further Evaluation), page 15, boys'),
                    ('8480-6', 'male', 36, 48::int, NULL::numeric, 101::numeric, 'screening',
                     'Flynn JT et al., Pediatrics 2017;140(3):e20171904, Table 6 (Screening BP Values Requiring Further Evaluation), page 15, boys'),
                    ('8480-6', 'male', 48, 60::int, NULL::numeric, 102::numeric, 'screening',
                     'Flynn JT et al., Pediatrics 2017;140(3):e20171904, Table 6 (Screening BP Values Requiring Further Evaluation), page 15, boys'),
                    ('8480-6', 'male', 60, 72::int, NULL::numeric, 103::numeric, 'screening',
                     'Flynn JT et al., Pediatrics 2017;140(3):e20171904, Table 6 (Screening BP Values Requiring Further Evaluation), page 15, boys'),
                    ('8480-6', 'male', 72, 84::int, NULL::numeric, 105::numeric, 'screening',
                     'Flynn JT et al., Pediatrics 2017;140(3):e20171904, Table 6 (Screening BP Values Requiring Further Evaluation), page 15, boys'),
                    ('8480-6', 'male', 84, 96::int, NULL::numeric, 106::numeric, 'screening',
                     'Flynn JT et al., Pediatrics 2017;140(3):e20171904, Table 6 (Screening BP Values Requiring Further Evaluation), page 15, boys'),
                    ('8480-6', 'male', 96, 108::int, NULL::numeric, 107::numeric, 'screening',
                     'Flynn JT et al., Pediatrics 2017;140(3):e20171904, Table 6 (Screening BP Values Requiring Further Evaluation), page 15, boys'),
                    ('8480-6', 'male', 108, 120::int, NULL::numeric, 107::numeric, 'screening',
                     'Flynn JT et al., Pediatrics 2017;140(3):e20171904, Table 6 (Screening BP Values Requiring Further Evaluation), page 15, boys'),
                    ('8480-6', 'male', 120, 132::int, NULL::numeric, 108::numeric, 'screening',
                     'Flynn JT et al., Pediatrics 2017;140(3):e20171904, Table 6 (Screening BP Values Requiring Further Evaluation), page 15, boys'),
                    ('8480-6', 'male', 132, 144::int, NULL::numeric, 110::numeric, 'screening',
                     'Flynn JT et al., Pediatrics 2017;140(3):e20171904, Table 6 (Screening BP Values Requiring Further Evaluation), page 15, boys'),
                    ('8480-6', 'male', 144, 156::int, NULL::numeric, 113::numeric, 'screening',
                     'Flynn JT et al., Pediatrics 2017;140(3):e20171904, Table 6 (Screening BP Values Requiring Further Evaluation), page 15, boys'),
                    ('8480-6', 'male', 156, 216::int, NULL::numeric, 120::numeric, 'screening',
                     'Flynn JT et al., Pediatrics 2017;140(3):e20171904, Table 6 (Screening BP Values Requiring Further Evaluation), page 15, boys'),
                    ('8480-6', 'female', 12, 24::int, NULL::numeric, 98::numeric, 'screening',
                     'Flynn JT et al., Pediatrics 2017;140(3):e20171904, Table 6 (Screening BP Values Requiring Further Evaluation), page 15, girls'),
                    ('8480-6', 'female', 24, 36::int, NULL::numeric, 101::numeric, 'screening',
                     'Flynn JT et al., Pediatrics 2017;140(3):e20171904, Table 6 (Screening BP Values Requiring Further Evaluation), page 15, girls'),
                    ('8480-6', 'female', 36, 48::int, NULL::numeric, 102::numeric, 'screening',
                     'Flynn JT et al., Pediatrics 2017;140(3):e20171904, Table 6 (Screening BP Values Requiring Further Evaluation), page 15, girls'),
                    ('8480-6', 'female', 48, 60::int, NULL::numeric, 103::numeric, 'screening',
                     'Flynn JT et al., Pediatrics 2017;140(3):e20171904, Table 6 (Screening BP Values Requiring Further Evaluation), page 15, girls'),
                    ('8480-6', 'female', 60, 72::int, NULL::numeric, 104::numeric, 'screening',
                     'Flynn JT et al., Pediatrics 2017;140(3):e20171904, Table 6 (Screening BP Values Requiring Further Evaluation), page 15, girls'),
                    ('8480-6', 'female', 72, 84::int, NULL::numeric, 105::numeric, 'screening',
                     'Flynn JT et al., Pediatrics 2017;140(3):e20171904, Table 6 (Screening BP Values Requiring Further Evaluation), page 15, girls'),
                    ('8480-6', 'female', 84, 96::int, NULL::numeric, 106::numeric, 'screening',
                     'Flynn JT et al., Pediatrics 2017;140(3):e20171904, Table 6 (Screening BP Values Requiring Further Evaluation), page 15, girls'),
                    ('8480-6', 'female', 96, 108::int, NULL::numeric, 107::numeric, 'screening',
                     'Flynn JT et al., Pediatrics 2017;140(3):e20171904, Table 6 (Screening BP Values Requiring Further Evaluation), page 15, girls'),
                    ('8480-6', 'female', 108, 120::int, NULL::numeric, 108::numeric, 'screening',
                     'Flynn JT et al., Pediatrics 2017;140(3):e20171904, Table 6 (Screening BP Values Requiring Further Evaluation), page 15, girls'),
                    ('8480-6', 'female', 120, 132::int, NULL::numeric, 109::numeric, 'screening',
                     'Flynn JT et al., Pediatrics 2017;140(3):e20171904, Table 6 (Screening BP Values Requiring Further Evaluation), page 15, girls'),
                    ('8480-6', 'female', 132, 144::int, NULL::numeric, 111::numeric, 'screening',
                     'Flynn JT et al., Pediatrics 2017;140(3):e20171904, Table 6 (Screening BP Values Requiring Further Evaluation), page 15, girls'),
                    ('8480-6', 'female', 144, 156::int, NULL::numeric, 114::numeric, 'screening',
                     'Flynn JT et al., Pediatrics 2017;140(3):e20171904, Table 6 (Screening BP Values Requiring Further Evaluation), page 15, girls'),
                    ('8480-6', 'female', 156, 216::int, NULL::numeric, 120::numeric, 'screening',
                     'Flynn JT et al., Pediatrics 2017;140(3):e20171904, Table 6 (Screening BP Values Requiring Further Evaluation), page 15, girls'),
                    ('8480-6', 'any', 216, NULL::int, 90::numeric, 129::numeric, 'normal',
                     'MedlinePlus (NIH), Vital signs (low); AHA/ACC adult categories, stage 1 from 130 or 80 (high)'),
                    ('8462-4', 'male', 12, 24::int, NULL::numeric, 52::numeric, 'screening',
                     'Flynn JT et al., Pediatrics 2017;140(3):e20171904, Table 6 (Screening BP Values Requiring Further Evaluation), page 15, boys'),
                    ('8462-4', 'male', 24, 36::int, NULL::numeric, 55::numeric, 'screening',
                     'Flynn JT et al., Pediatrics 2017;140(3):e20171904, Table 6 (Screening BP Values Requiring Further Evaluation), page 15, boys'),
                    ('8462-4', 'male', 36, 48::int, NULL::numeric, 58::numeric, 'screening',
                     'Flynn JT et al., Pediatrics 2017;140(3):e20171904, Table 6 (Screening BP Values Requiring Further Evaluation), page 15, boys'),
                    ('8462-4', 'male', 48, 60::int, NULL::numeric, 60::numeric, 'screening',
                     'Flynn JT et al., Pediatrics 2017;140(3):e20171904, Table 6 (Screening BP Values Requiring Further Evaluation), page 15, boys'),
                    ('8462-4', 'male', 60, 72::int, NULL::numeric, 63::numeric, 'screening',
                     'Flynn JT et al., Pediatrics 2017;140(3):e20171904, Table 6 (Screening BP Values Requiring Further Evaluation), page 15, boys'),
                    ('8462-4', 'male', 72, 84::int, NULL::numeric, 66::numeric, 'screening',
                     'Flynn JT et al., Pediatrics 2017;140(3):e20171904, Table 6 (Screening BP Values Requiring Further Evaluation), page 15, boys'),
                    ('8462-4', 'male', 84, 96::int, NULL::numeric, 68::numeric, 'screening',
                     'Flynn JT et al., Pediatrics 2017;140(3):e20171904, Table 6 (Screening BP Values Requiring Further Evaluation), page 15, boys'),
                    ('8462-4', 'male', 96, 108::int, NULL::numeric, 69::numeric, 'screening',
                     'Flynn JT et al., Pediatrics 2017;140(3):e20171904, Table 6 (Screening BP Values Requiring Further Evaluation), page 15, boys'),
                    ('8462-4', 'male', 108, 120::int, NULL::numeric, 70::numeric, 'screening',
                     'Flynn JT et al., Pediatrics 2017;140(3):e20171904, Table 6 (Screening BP Values Requiring Further Evaluation), page 15, boys'),
                    ('8462-4', 'male', 120, 132::int, NULL::numeric, 72::numeric, 'screening',
                     'Flynn JT et al., Pediatrics 2017;140(3):e20171904, Table 6 (Screening BP Values Requiring Further Evaluation), page 15, boys'),
                    ('8462-4', 'male', 132, 144::int, NULL::numeric, 74::numeric, 'screening',
                     'Flynn JT et al., Pediatrics 2017;140(3):e20171904, Table 6 (Screening BP Values Requiring Further Evaluation), page 15, boys'),
                    ('8462-4', 'male', 144, 156::int, NULL::numeric, 75::numeric, 'screening',
                     'Flynn JT et al., Pediatrics 2017;140(3):e20171904, Table 6 (Screening BP Values Requiring Further Evaluation), page 15, boys'),
                    ('8462-4', 'male', 156, 216::int, NULL::numeric, 80::numeric, 'screening',
                     'Flynn JT et al., Pediatrics 2017;140(3):e20171904, Table 6 (Screening BP Values Requiring Further Evaluation), page 15, boys'),
                    ('8462-4', 'female', 12, 24::int, NULL::numeric, 54::numeric, 'screening',
                     'Flynn JT et al., Pediatrics 2017;140(3):e20171904, Table 6 (Screening BP Values Requiring Further Evaluation), page 15, girls'),
                    ('8462-4', 'female', 24, 36::int, NULL::numeric, 58::numeric, 'screening',
                     'Flynn JT et al., Pediatrics 2017;140(3):e20171904, Table 6 (Screening BP Values Requiring Further Evaluation), page 15, girls'),
                    ('8462-4', 'female', 36, 48::int, NULL::numeric, 60::numeric, 'screening',
                     'Flynn JT et al., Pediatrics 2017;140(3):e20171904, Table 6 (Screening BP Values Requiring Further Evaluation), page 15, girls'),
                    ('8462-4', 'female', 48, 60::int, NULL::numeric, 62::numeric, 'screening',
                     'Flynn JT et al., Pediatrics 2017;140(3):e20171904, Table 6 (Screening BP Values Requiring Further Evaluation), page 15, girls'),
                    ('8462-4', 'female', 60, 72::int, NULL::numeric, 64::numeric, 'screening',
                     'Flynn JT et al., Pediatrics 2017;140(3):e20171904, Table 6 (Screening BP Values Requiring Further Evaluation), page 15, girls'),
                    ('8462-4', 'female', 72, 84::int, NULL::numeric, 67::numeric, 'screening',
                     'Flynn JT et al., Pediatrics 2017;140(3):e20171904, Table 6 (Screening BP Values Requiring Further Evaluation), page 15, girls'),
                    ('8462-4', 'female', 84, 96::int, NULL::numeric, 68::numeric, 'screening',
                     'Flynn JT et al., Pediatrics 2017;140(3):e20171904, Table 6 (Screening BP Values Requiring Further Evaluation), page 15, girls'),
                    ('8462-4', 'female', 96, 108::int, NULL::numeric, 69::numeric, 'screening',
                     'Flynn JT et al., Pediatrics 2017;140(3):e20171904, Table 6 (Screening BP Values Requiring Further Evaluation), page 15, girls'),
                    ('8462-4', 'female', 108, 120::int, NULL::numeric, 71::numeric, 'screening',
                     'Flynn JT et al., Pediatrics 2017;140(3):e20171904, Table 6 (Screening BP Values Requiring Further Evaluation), page 15, girls'),
                    ('8462-4', 'female', 120, 132::int, NULL::numeric, 72::numeric, 'screening',
                     'Flynn JT et al., Pediatrics 2017;140(3):e20171904, Table 6 (Screening BP Values Requiring Further Evaluation), page 15, girls'),
                    ('8462-4', 'female', 132, 144::int, NULL::numeric, 74::numeric, 'screening',
                     'Flynn JT et al., Pediatrics 2017;140(3):e20171904, Table 6 (Screening BP Values Requiring Further Evaluation), page 15, girls'),
                    ('8462-4', 'female', 144, 156::int, NULL::numeric, 75::numeric, 'screening',
                     'Flynn JT et al., Pediatrics 2017;140(3):e20171904, Table 6 (Screening BP Values Requiring Further Evaluation), page 15, girls'),
                    ('8462-4', 'female', 156, 216::int, NULL::numeric, 80::numeric, 'screening',
                     'Flynn JT et al., Pediatrics 2017;140(3):e20171904, Table 6 (Screening BP Values Requiring Further Evaluation), page 15, girls'),
                    ('8462-4', 'any', 216, NULL::int, 60::numeric, 79::numeric, 'normal',
                     'MedlinePlus (NIH), Vital signs (low); AHA/ACC adult categories, stage 1 from 130 or 80 (high)');

                    DELETE FROM ehr.vital_reference_ranges;
                    INSERT INTO ehr.vital_reference_ranges (code, sex, from_months, to_months, low, high, kind, source)
                    SELECT r.code, r.sex, r.from_months, r.to_months, r.low, r.high, r.kind, r.source FROM ranges_now r
                    WHERE EXISTS (SELECT 1 FROM ehr.observation_codes c WHERE c.code = r.code);

                    ALTER TABLE ehr.observations NO FORCE ROW LEVEL SECURITY;
                    IF audited THEN
                        ALTER TABLE ehr.observations DISABLE TRIGGER observations_audit;
                    END IF;

                    -- Each reading's range now, beside the one it had: for a sex other than male or female, the higher low
                    -- and the lower high of both sexes' rows; none for a blood pressure under 1 year.
                    CREATE TEMPORARY TABLE regraded ON COMMIT DROP AS
                    WITH aged AS (
                        SELECT o.id, o.code, o.value, o.reference_low, o.reference_high, o.reference_kind, p.gender,
                            greatest(0, (extract(year FROM age((o.effective_at AT TIME ZONE 'UTC')::date::timestamp, p.birth_date::timestamp)) * 12
                                + extract(month FROM age((o.effective_at AT TIME ZONE 'UTC')::date::timestamp, p.birth_date::timestamp)))::int) AS months
                        FROM ehr.observations o JOIN ehr.patients p ON p.id = o.patient_id
                        WHERE o.category = 'vital-signs' AND o.code IN (SELECT code FROM ranges_now))
                    SELECT a.id, a.code, a.value, a.reference_low AS old_low, a.reference_high AS old_high, a.reference_kind AS old_kind, n.low, n.high, n.kind
                    FROM aged a
                    CROSS JOIN LATERAL (
                        SELECT max(r.low) AS low, min(r.high) AS high, coalesce(max(r.kind), 'normal') AS kind
                        FROM ranges_now r
                        WHERE r.code = a.code AND a.months >= r.from_months AND (r.to_months IS NULL OR a.months < r.to_months)
                          AND (r.sex = 'any' OR r.sex = a.gender OR a.gender NOT IN ('male', 'female'))) n;

                    -- The flags before and after, as the pages work them out (Ehr.Domain's VitalFlag).
                    CREATE TEMPORARY TABLE flag_moves ON COMMIT DROP AS
                    SELECT code,
                        CASE WHEN old_kind = 'screening' THEN CASE WHEN value >= old_high THEN 'needs follow-up' END
                             WHEN value > old_high THEN 'High' WHEN value < old_low THEN 'Low' END AS before,
                        CASE WHEN kind = 'screening' THEN CASE WHEN value >= high THEN 'needs follow-up' END
                             WHEN value > high THEN 'High' WHEN value < low THEN 'Low' END AS after
                    FROM regraded
                    WHERE (old_low, old_high, old_kind) IS DISTINCT FROM (low, high, kind);

                    UPDATE ehr.observations o SET reference_low = g.low, reference_high = g.high, reference_kind = g.kind
                    FROM regraded g
                    WHERE o.id = g.id AND (g.old_low, g.old_high, g.old_kind) IS DISTINCT FROM (g.low, g.high, g.kind);

                    IF audited THEN
                        ALTER TABLE ehr.observations ENABLE TRIGGER observations_audit;
                    END IF;
                    IF forced THEN
                        ALTER TABLE ehr.observations FORCE ROW LEVEL SECURITY;
                    END IF;

                    FOR sign IN
                        SELECT * FROM (VALUES (1, '8867-4', 'heart rate'), (2, '9279-1', 'respiratory rate'), (3, '8480-6', 'systolic blood pressure'),
                            (4, '8462-4', 'diastolic blood pressure')) s(place, code, name) ORDER BY place
                    LOOP
                        SELECT count(*) INTO counted FROM flag_moves m WHERE m.code = sign.code;
                        RAISE NOTICE 'Migration 9d: % (%): % readings re-ranged', sign.name, sign.code, counted;
                        FOR change IN
                            SELECT * FROM (VALUES (1, 'newly flagged'), (2, 'no longer flagged'), (3, 'changed from one flag to another')) c(place, name) ORDER BY place
                        LOOP
                            SELECT coalesce(sum(t.n), 0), coalesce(' (' || string_agg(t.n || ' ' || t.what, ', ' ORDER BY t.what) || ')', '') INTO counted, detail
                            FROM (
                                SELECT count(*) AS n,
                                    CASE change.place WHEN 1 THEN m.after WHEN 2 THEN m.before ELSE m.before || ' to ' || m.after END AS what
                                FROM flag_moves m
                                WHERE m.code = sign.code
                                  AND CASE change.place
                                      WHEN 1 THEN m.before IS NULL AND m.after IS NOT NULL
                                      WHEN 2 THEN m.before IS NOT NULL AND m.after IS NULL
                                      ELSE m.before IS NOT NULL AND m.after IS NOT NULL AND m.before <> m.after END
                                GROUP BY 2) t;
                            RAISE NOTICE 'Migration 9d: % (%): % %', sign.name, sign.code, counted, change.name || detail;
                        END LOOP;
                    END LOOP;
                    RAISE NOTICE 'Migration 9d: in all, % readings re-ranged; % newly flagged, % no longer flagged, % changed from one flag to another',
                        (SELECT count(*) FROM flag_moves), (SELECT count(*) FROM flag_moves WHERE before IS NULL AND after IS NOT NULL),
                        (SELECT count(*) FROM flag_moves WHERE before IS NOT NULL AND after IS NULL),
                        (SELECT count(*) FROM flag_moves WHERE before IS NOT NULL AND after IS NOT NULL AND before <> after);
                END
                $$;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Written by hand. The schema this returns to has five age bands and no sex or kind: every vital sign of these
            // four is given back the range VitalCorrections gave it, from the same figures, and the table is emptied for
            // the earlier release's db/reference to fill. As in Up, forced row-level security is lifted and the audit
            // trigger paused while it runs. Until the earlier release's db/policies is applied after a rollback,
            // recording a vital sign fails: apply it straight after.
            migrationBuilder.Sql("""
                DO $$
                DECLARE
                    forced boolean := (SELECT relforcerowsecurity FROM pg_class WHERE oid = 'ehr.observations'::regclass);
                    audited boolean := EXISTS (SELECT 1 FROM pg_trigger WHERE tgrelid = 'ehr.observations'::regclass AND tgname = 'observations_audit');
                BEGIN
                    ALTER TABLE ehr.observations NO FORCE ROW LEVEL SECURITY;
                    IF audited THEN
                        ALTER TABLE ehr.observations DISABLE TRIGGER observations_audit;
                    END IF;

                    CREATE TEMPORARY TABLE vital_ranges_then (code text, from_months int, to_months int, low numeric, high numeric) ON COMMIT DROP;
                    INSERT INTO vital_ranges_then VALUES
                        ('8867-4', 0, 12::int, 91::numeric, 186::numeric),
                        ('8867-4', 12, 72::int, 66::numeric, 173::numeric),
                        ('8867-4', 72, 144::int, 56::numeric, 155::numeric),
                        ('8867-4', 144, 216::int, 48::numeric, 138::numeric),
                        ('8867-4', 216, NULL::int, 60::numeric, 100::numeric),
                        ('9279-1', 0, 12::int, 19::numeric, 76::numeric),
                        ('9279-1', 12, 72::int, 14::numeric, 60::numeric),
                        ('9279-1', 72, 144::int, 13::numeric, 46::numeric),
                        ('9279-1', 144, 216::int, 11::numeric, 35::numeric),
                        ('9279-1', 216, NULL::int, 12::numeric, 20::numeric),
                        ('8480-6', 0, 12::int, 70::numeric, NULL::numeric),
                        ('8480-6', 12, 72::int, 72::numeric, 103::numeric),
                        ('8480-6', 72, 144::int, 82::numeric, 110::numeric),
                        ('8480-6', 144, 216::int, 90::numeric, 119::numeric),
                        ('8480-6', 216, NULL::int, 90::numeric, 129::numeric),
                        ('8462-4', 12, 72::int, NULL::numeric, 63::numeric),
                        ('8462-4', 72, 144::int, NULL::numeric, 73::numeric),
                        ('8462-4', 144, 216::int, NULL::numeric, 79::numeric),
                        ('8462-4', 216, NULL::int, 60::numeric, 79::numeric);

                    WITH aged AS (
                        SELECT o.id, o.code,
                            greatest(0, (extract(year FROM age((o.effective_at AT TIME ZONE 'UTC')::date::timestamp, p.birth_date::timestamp)) * 12
                                + extract(month FROM age((o.effective_at AT TIME ZONE 'UTC')::date::timestamp, p.birth_date::timestamp)))::int) AS months
                        FROM ehr.observations o JOIN ehr.patients p ON p.id = o.patient_id
                        WHERE o.category = 'vital-signs' AND o.code IN (SELECT code FROM vital_ranges_then))
                    UPDATE ehr.observations o
                    SET reference_low = r.low, reference_high = r.high
                    FROM aged a
                    LEFT JOIN vital_ranges_then r ON r.code = a.code AND a.months >= r.from_months AND (r.to_months IS NULL OR a.months < r.to_months)
                    WHERE o.id = a.id AND (o.reference_low, o.reference_high) IS DISTINCT FROM (r.low, r.high);

                    DELETE FROM ehr.vital_reference_ranges;

                    IF audited THEN
                        ALTER TABLE ehr.observations ENABLE TRIGGER observations_audit;
                    END IF;
                    IF forced THEN
                        ALTER TABLE ehr.observations FORCE ROW LEVEL SECURITY;
                    END IF;
                END
                $$;
                """);

            migrationBuilder.DropPrimaryKey(
                name: "pk_vital_reference_ranges",
                schema: "ehr",
                table: "vital_reference_ranges");

            migrationBuilder.DropCheckConstraint(
                name: "ck_vital_reference_ranges_kind",
                schema: "ehr",
                table: "vital_reference_ranges");

            migrationBuilder.DropCheckConstraint(
                name: "ck_vital_reference_ranges_screening",
                schema: "ehr",
                table: "vital_reference_ranges");

            migrationBuilder.DropCheckConstraint(
                name: "ck_vital_reference_ranges_sex",
                schema: "ehr",
                table: "vital_reference_ranges");

            migrationBuilder.DropCheckConstraint(
                name: "ck_observations_reference_kind",
                schema: "ehr",
                table: "observations");

            migrationBuilder.DropColumn(
                name: "sex",
                schema: "ehr",
                table: "vital_reference_ranges");

            migrationBuilder.DropColumn(
                name: "kind",
                schema: "ehr",
                table: "vital_reference_ranges");

            migrationBuilder.DropColumn(
                name: "reference_kind",
                schema: "ehr",
                table: "observations");

            migrationBuilder.AddColumn<string>(
                name: "band",
                schema: "ehr",
                table: "vital_reference_ranges",
                type: "character varying(20)",
                maxLength: 20,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddPrimaryKey(
                name: "pk_vital_reference_ranges",
                schema: "ehr",
                table: "vital_reference_ranges",
                columns: new[] { "code", "band" });

            migrationBuilder.AddCheckConstraint(
                name: "ck_vital_reference_ranges_band",
                schema: "ehr",
                table: "vital_reference_ranges",
                sql: "band IN ('infant', 'young-child', 'older-child', 'adolescent', 'adult')");
        }
    }
}
