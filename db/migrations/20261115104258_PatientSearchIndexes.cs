using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ehr.Migrations
{
    /// <inheritdoc />
    public partial class PatientSearchIndexes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "family_name_key",
                schema: "ehr",
                table: "patients",
                type: "text",
                nullable: false,
                computedColumnSql: "lower(family_name)",
                stored: true,
                collation: "C");

            migrationBuilder.AddColumn<string>(
                name: "given_name_key",
                schema: "ehr",
                table: "patients",
                type: "text",
                nullable: false,
                computedColumnSql: "lower(given_name)",
                stored: true,
                collation: "C");

            migrationBuilder.CreateIndex(
                name: "ix_patients_birth_date",
                schema: "ehr",
                table: "patients",
                column: "birth_date");

            migrationBuilder.CreateIndex(
                name: "ix_patients_family_name_key",
                schema: "ehr",
                table: "patients",
                column: "family_name_key");

            migrationBuilder.CreateIndex(
                name: "ix_patients_given_name_key",
                schema: "ehr",
                table: "patients",
                column: "given_name_key");

            // Written by hand (NameKeys): the first string after every string that starts with the prefix, in code point
            // order, the "C" collation's. The last character becomes the next code point, past the surrogates; a last
            // U+10FFFF is dropped and the one before it moves on instead. Null when nothing comes after: a prefix of
            // nothing but U+10FFFF. Code point order is UTF-8's byte order, and no character's UTF-8 begins another's,
            // so every string with the prefix sorts before the result and every string without it outside the range.
            migrationBuilder.Sql(
                """
                CREATE FUNCTION ehr.name_key_after(prefix text) RETURNS text
                    LANGUAGE plpgsql IMMUTABLE STRICT PARALLEL SAFE
                AS $$
                DECLARE
                    head text := prefix;
                    last integer;
                BEGIN
                    WHILE head <> '' LOOP
                        last := ascii(right(head, 1));
                        head := left(head, -1);
                        IF last < 1114111 THEN
                            RETURN head || chr(CASE WHEN last = 55295 THEN 57344 ELSE last + 1 END);
                        END IF;
                    END LOOP;
                    RETURN NULL;
                END
                $$;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP FUNCTION ehr.name_key_after(text);");

            migrationBuilder.DropIndex(
                name: "ix_patients_birth_date",
                schema: "ehr",
                table: "patients");

            migrationBuilder.DropIndex(
                name: "ix_patients_family_name_key",
                schema: "ehr",
                table: "patients");

            migrationBuilder.DropIndex(
                name: "ix_patients_given_name_key",
                schema: "ehr",
                table: "patients");

            migrationBuilder.DropColumn(
                name: "family_name_key",
                schema: "ehr",
                table: "patients");

            migrationBuilder.DropColumn(
                name: "given_name_key",
                schema: "ehr",
                table: "patients");
        }
    }
}
