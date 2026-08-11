using Cove.Data.Services;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Cove.Data.Migrations;

[DbContext(typeof(CoveContext))]
[Migration(NameRuleEnforcementService.MigrationId)]
public sealed class EnforceUniqueNames : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        // This is the authoritative read-only guard. The staged values were computed with the
        // shared .NET rules; compare every original row while holding write-blocking table locks so
        // no concurrent cleanup or writer can invalidate the preflight before enforcement lands.
        migrationBuilder.Sql("""
            LOCK TABLE tags, tag_aliases, performers, studios IN SHARE ROW EXCLUSIVE MODE;

            DO $cove_guard$
            DECLARE
                staged_tags boolean := to_regclass('pg_temp.cove_name_rule_tags') IS NOT NULL;
                staged_aliases boolean := to_regclass('pg_temp.cove_name_rule_aliases') IS NOT NULL;
                staged_performers boolean := to_regclass('pg_temp.cove_name_rule_performers') IS NOT NULL;
                staged_studios boolean := to_regclass('pg_temp.cove_name_rule_studios') IS NOT NULL;
            BEGIN
                IF (EXISTS (SELECT 1 FROM tags)
                    OR EXISTS (SELECT 1 FROM tag_aliases)
                    OR EXISTS (SELECT 1 FROM performers)
                    OR EXISTS (SELECT 1 FROM studios))
                   AND (NOT staged_tags OR NOT staged_aliases OR NOT staged_performers OR NOT staged_studios) THEN
                    RAISE EXCEPTION 'COVE_NAME_RULE_GUARD: Cove 1.3.0 requires its migration screen to run the tag, performer, and studio name preflight. Run the latest Cove 1.2.x Name Conflicts cleanup first, then retry the upgrade.';
                END IF;

                IF NOT staged_tags THEN
                    CREATE TEMP TABLE cove_name_rule_tags (
                        "Id" integer PRIMARY KEY,
                        "OriginalName" text NOT NULL,
                        "NormalizedName" text NOT NULL,
                        "NamespaceKey" text COLLATE "C" NOT NULL
                    ) ON COMMIT DROP;
                END IF;
                IF NOT staged_aliases THEN
                    CREATE TEMP TABLE cove_name_rule_aliases (
                        "Id" integer PRIMARY KEY,
                        "TagId" integer NOT NULL,
                        "OriginalAlias" text NOT NULL,
                        "NormalizedAlias" text NOT NULL,
                        "NamespaceKey" text COLLATE "C" NOT NULL
                    ) ON COMMIT DROP;
                END IF;
                IF NOT staged_performers THEN
                    CREATE TEMP TABLE cove_name_rule_performers (
                        "Id" integer PRIMARY KEY,
                        "OriginalName" text NOT NULL,
                        "OriginalDisambiguation" text NULL,
                        "NormalizedName" text NOT NULL,
                        "NormalizedDisambiguation" text NULL,
                        "IdentityKey" text COLLATE "C" NOT NULL
                    ) ON COMMIT DROP;
                END IF;
                IF NOT staged_studios THEN
                    CREATE TEMP TABLE cove_name_rule_studios (
                        "Id" integer PRIMARY KEY,
                        "OriginalName" text NOT NULL,
                        "NormalizedName" text NOT NULL,
                        "NameKey" text COLLATE "C" NOT NULL
                    ) ON COMMIT DROP;
                END IF;

                IF (SELECT count(*) FROM tags) <> (SELECT count(*) FROM pg_temp.cove_name_rule_tags)
                   OR EXISTS (
                       SELECT 1
                       FROM tags tag
                       FULL JOIN pg_temp.cove_name_rule_tags staged ON staged."Id" = tag."Id"
                       WHERE tag."Id" IS NULL OR staged."Id" IS NULL
                          OR tag."Name" IS DISTINCT FROM staged."OriginalName")
                   OR (SELECT count(*) FROM tag_aliases) <> (SELECT count(*) FROM pg_temp.cove_name_rule_aliases)
                   OR EXISTS (
                       SELECT 1
                       FROM tag_aliases alias
                       FULL JOIN pg_temp.cove_name_rule_aliases staged ON staged."Id" = alias."Id"
                       WHERE alias."Id" IS NULL OR staged."Id" IS NULL
                          OR alias."TagId" IS DISTINCT FROM staged."TagId"
                          OR alias."Alias" IS DISTINCT FROM staged."OriginalAlias")
                   OR (SELECT count(*) FROM performers) <> (SELECT count(*) FROM pg_temp.cove_name_rule_performers)
                   OR EXISTS (
                       SELECT 1
                       FROM performers performer
                       FULL JOIN pg_temp.cove_name_rule_performers staged ON staged."Id" = performer."Id"
                       WHERE performer."Id" IS NULL OR staged."Id" IS NULL
                          OR performer."Name" IS DISTINCT FROM staged."OriginalName"
                          OR performer."Disambiguation" IS DISTINCT FROM staged."OriginalDisambiguation")
                   OR (SELECT count(*) FROM studios) <> (SELECT count(*) FROM pg_temp.cove_name_rule_studios)
                   OR EXISTS (
                       SELECT 1
                       FROM studios studio
                       FULL JOIN pg_temp.cove_name_rule_studios staged ON staged."Id" = studio."Id"
                       WHERE studio."Id" IS NULL OR staged."Id" IS NULL
                          OR studio."Name" IS DISTINCT FROM staged."OriginalName") THEN
                    RAISE EXCEPTION 'COVE_NAME_RULE_GUARD: Tag, performer, or studio identities changed during the upgrade preflight. No migration changes were applied; verify readiness in the latest Cove 1.2.x and retry.';
                END IF;

                IF EXISTS (
                    SELECT "NamespaceKey"
                    FROM (
                        SELECT "NamespaceKey" FROM pg_temp.cove_name_rule_tags
                        UNION ALL
                        SELECT "NamespaceKey" FROM pg_temp.cove_name_rule_aliases
                    ) claims
                    GROUP BY "NamespaceKey"
                    HAVING count(*) > 1)
                   OR EXISTS (SELECT 1 FROM pg_temp.cove_name_rule_aliases WHERE "NamespaceKey" = '')
                   OR EXISTS (
                       SELECT "IdentityKey"
                       FROM pg_temp.cove_name_rule_performers
                       GROUP BY "IdentityKey"
                       HAVING "IdentityKey" = '' OR count(*) > 1)
                   OR EXISTS (
                       SELECT "NameKey"
                       FROM pg_temp.cove_name_rule_studios
                       GROUP BY "NameKey"
                       HAVING "NameKey" = '' OR count(*) > 1) THEN
                    RAISE EXCEPTION 'COVE_NAME_RULE_GUARD: Unresolved tag, performer, or studio name conflicts remain. Run the latest Cove 1.2.x Name Conflicts cleanup and retry the upgrade.';
                END IF;
            END
            $cove_guard$;
            """);

        migrationBuilder.AddColumn<string>(
            name: "NamespaceKey",
            table: "tags",
            type: "text",
            nullable: true,
            collation: "C");

        migrationBuilder.AddColumn<string>(
            name: "NamespaceKey",
            table: "tag_aliases",
            type: "text",
            nullable: true,
            collation: "C");

        migrationBuilder.AddColumn<string>(
            name: "IdentityKey",
            table: "performers",
            type: "text",
            nullable: true,
            collation: "C");

        migrationBuilder.AddColumn<string>(
            name: "NameKey",
            table: "studios",
            type: "text",
            nullable: true,
            collation: "C");

        // Only deterministic cleanup is allowed after the guard: apply Cove-computed trimming,
        // normalize blank disambiguation to null, discard blank/self aliases, and retain the lowest
        // alias row for a same-tag duplicate. Cross-entity conflicts are never merged here.
        migrationBuilder.Sql("""
            UPDATE tags tag
            SET "Name" = staged."NormalizedName",
                "NamespaceKey" = staged."NamespaceKey"
            FROM pg_temp.cove_name_rule_tags staged
            WHERE staged."Id" = tag."Id";

            UPDATE tag_aliases alias
            SET "Alias" = staged."NormalizedAlias",
                "NamespaceKey" = staged."NamespaceKey"
            FROM pg_temp.cove_name_rule_aliases staged
            WHERE staged."Id" = alias."Id";

            UPDATE performers performer
            SET "Name" = staged."NormalizedName",
                "Disambiguation" = staged."NormalizedDisambiguation",
                "IdentityKey" = staged."IdentityKey"
            FROM pg_temp.cove_name_rule_performers staged
            WHERE staged."Id" = performer."Id";

            UPDATE studios studio
            SET "Name" = staged."NormalizedName",
                "NameKey" = staged."NameKey"
            FROM pg_temp.cove_name_rule_studios staged
            WHERE staged."Id" = studio."Id";

            DELETE FROM tag_aliases
            WHERE "NamespaceKey" IS NULL OR "NamespaceKey" = '';

            DELETE FROM tag_aliases alias
            USING tags tag
            WHERE alias."TagId" = tag."Id"
              AND alias."NamespaceKey" = tag."NamespaceKey";

            DELETE FROM tag_aliases duplicate
            USING tag_aliases keeper
            WHERE duplicate."TagId" = keeper."TagId"
              AND duplicate."NamespaceKey" = keeper."NamespaceKey"
              AND duplicate."Id" > keeper."Id";

            DO $cove_validation$
            BEGIN
                IF EXISTS (SELECT 1 FROM tags WHERE "NamespaceKey" IS NULL OR "NamespaceKey" = '')
                   OR EXISTS (SELECT 1 FROM tag_aliases WHERE "NamespaceKey" IS NULL OR "NamespaceKey" = '')
                   OR EXISTS (
                       SELECT "NamespaceKey"
                       FROM (
                           SELECT "NamespaceKey" FROM tags
                           UNION ALL
                           SELECT "NamespaceKey" FROM tag_aliases
                       ) claims
                       GROUP BY "NamespaceKey"
                       HAVING count(*) > 1)
                   OR EXISTS (SELECT 1 FROM performers WHERE "IdentityKey" IS NULL OR "IdentityKey" = '')
                   OR EXISTS (
                       SELECT "IdentityKey"
                       FROM performers
                       GROUP BY "IdentityKey"
                       HAVING count(*) > 1)
                   OR EXISTS (SELECT 1 FROM studios WHERE "NameKey" IS NULL OR "NameKey" = '')
                   OR EXISTS (
                       SELECT "NameKey"
                       FROM studios
                       GROUP BY "NameKey"
                       HAVING count(*) > 1) THEN
                    RAISE EXCEPTION 'COVE_NAME_RULE_GUARD: Final name-rule validation failed. No migration changes were applied; verify readiness in the latest Cove 1.2.x and retry.';
                END IF;
            END
            $cove_validation$;
            """);

        migrationBuilder.AlterColumn<string>(
            name: "NamespaceKey",
            table: "tags",
            type: "text",
            nullable: false,
            collation: "C",
            oldClrType: typeof(string),
            oldType: "text",
            oldNullable: true,
            oldCollation: "C");

        migrationBuilder.AlterColumn<string>(
            name: "NamespaceKey",
            table: "tag_aliases",
            type: "text",
            nullable: false,
            collation: "C",
            oldClrType: typeof(string),
            oldType: "text",
            oldNullable: true,
            oldCollation: "C");

        migrationBuilder.AlterColumn<string>(
            name: "IdentityKey",
            table: "performers",
            type: "text",
            nullable: false,
            collation: "C",
            oldClrType: typeof(string),
            oldType: "text",
            oldNullable: true,
            oldCollation: "C");

        migrationBuilder.AlterColumn<string>(
            name: "NameKey",
            table: "studios",
            type: "text",
            nullable: false,
            collation: "C",
            oldClrType: typeof(string),
            oldType: "text",
            oldNullable: true,
            oldCollation: "C");

        migrationBuilder.AddCheckConstraint(
            name: "CK_performers_IdentityKey",
            table: "performers",
            sql: "\"IdentityKey\" <> ''");

        migrationBuilder.AddCheckConstraint(
            name: "CK_studios_NameKey",
            table: "studios",
            sql: "\"NameKey\" <> ''");

        // SP-GiST equality constraints enforce exact, arbitrarily long text identities without the
        // roughly one-page value limit of PostgreSQL B-tree indexes. Performer disambiguations and
        // tag aliases are intentionally unbounded text, so a B-tree could reject an otherwise ready
        // database during upgrade. Deferral preserves transaction-local merge ordering.
        migrationBuilder.Sql("""
            ALTER TABLE performers
                ADD CONSTRAINT "UQ_performers_identity"
                EXCLUDE USING spgist ("IdentityKey" WITH =)
                DEFERRABLE INITIALLY DEFERRED;
            ALTER TABLE studios
                ADD CONSTRAINT "UQ_studios_name"
                EXCLUDE USING spgist ("NameKey" WITH =)
                DEFERRABLE INITIALLY DEFERRED;

            -- The shared claims table applies the same exact equality constraint across canonical
            -- tag names and aliases. Triggers consume only Cove-computed keys.
            CREATE TABLE tag_name_claims (
                "ClaimType" smallint NOT NULL,
                "ClaimId" integer NOT NULL,
                "TagId" integer NOT NULL,
                "NamespaceKey" text COLLATE "C" NOT NULL,
                CONSTRAINT "PK_tag_name_claims" PRIMARY KEY ("ClaimType", "ClaimId"),
                CONSTRAINT "CK_tag_name_claims_type" CHECK ("ClaimType" IN (0, 1)),
                CONSTRAINT "CK_tag_name_claims_key" CHECK ("NamespaceKey" <> '')
            );
            ALTER TABLE tag_name_claims
                ADD CONSTRAINT "UQ_tag_name_claims_namespace"
                EXCLUDE USING spgist ("NamespaceKey" WITH =)
                DEFERRABLE INITIALLY DEFERRED;
            CREATE INDEX "IX_tag_name_claims_TagId" ON tag_name_claims ("TagId");

            INSERT INTO tag_name_claims ("ClaimType", "ClaimId", "TagId", "NamespaceKey")
            SELECT 0, "Id", "Id", "NamespaceKey" FROM tags
            UNION ALL
            SELECT 1, "Id", "TagId", "NamespaceKey" FROM tag_aliases;

            CREATE FUNCTION cove_sync_tag_name_claim() RETURNS trigger
            LANGUAGE plpgsql AS $cove_claim$
            BEGIN
                IF TG_OP = 'TRUNCATE' THEN
                    DELETE FROM tag_name_claims WHERE "ClaimType" = 0;
                    RETURN NULL;
                END IF;

                IF TG_OP = 'DELETE' THEN
                    DELETE FROM tag_name_claims
                    WHERE "ClaimType" = 0 AND "ClaimId" = OLD."Id";
                    RETURN OLD;
                END IF;

                IF NEW."NamespaceKey" IS NULL OR NEW."NamespaceKey" = '' THEN
                    RAISE EXCEPTION USING
                        ERRCODE = '23514',
                        MESSAGE = 'A tag namespace key must be supplied by Cove.';
                END IF;

                IF TG_OP = 'INSERT' THEN
                    INSERT INTO tag_name_claims ("ClaimType", "ClaimId", "TagId", "NamespaceKey")
                    VALUES (0, NEW."Id", NEW."Id", NEW."NamespaceKey");
                ELSE
                    UPDATE tag_name_claims
                    SET "ClaimId" = NEW."Id", "TagId" = NEW."Id", "NamespaceKey" = NEW."NamespaceKey"
                    WHERE "ClaimType" = 0 AND "ClaimId" = OLD."Id";
                END IF;
                RETURN NEW;
            END
            $cove_claim$;

            CREATE FUNCTION cove_sync_tag_alias_claim() RETURNS trigger
            LANGUAGE plpgsql AS $cove_alias_claim$
            BEGIN
                IF TG_OP = 'TRUNCATE' THEN
                    DELETE FROM tag_name_claims WHERE "ClaimType" = 1;
                    RETURN NULL;
                END IF;

                IF TG_OP = 'DELETE' THEN
                    DELETE FROM tag_name_claims
                    WHERE "ClaimType" = 1 AND "ClaimId" = OLD."Id";
                    RETURN OLD;
                END IF;

                IF NEW."NamespaceKey" IS NULL OR NEW."NamespaceKey" = '' THEN
                    RAISE EXCEPTION USING
                        ERRCODE = '23514',
                        MESSAGE = 'A tag alias namespace key must be supplied by Cove.';
                END IF;

                IF TG_OP = 'INSERT' THEN
                    INSERT INTO tag_name_claims ("ClaimType", "ClaimId", "TagId", "NamespaceKey")
                    VALUES (1, NEW."Id", NEW."TagId", NEW."NamespaceKey");
                ELSE
                    UPDATE tag_name_claims
                    SET "ClaimId" = NEW."Id", "TagId" = NEW."TagId", "NamespaceKey" = NEW."NamespaceKey"
                    WHERE "ClaimType" = 1 AND "ClaimId" = OLD."Id";
                END IF;
                RETURN NEW;
            END
            $cove_alias_claim$;

            CREATE TRIGGER cove_tag_name_claim_insert_delete
            AFTER INSERT OR DELETE ON tags
            FOR EACH ROW EXECUTE FUNCTION cove_sync_tag_name_claim();
            CREATE TRIGGER cove_tag_name_claim_update
            AFTER UPDATE OF "Id", "NamespaceKey" ON tags
            FOR EACH ROW EXECUTE FUNCTION cove_sync_tag_name_claim();
            CREATE TRIGGER cove_tag_name_claim_truncate
            AFTER TRUNCATE ON tags
            FOR EACH STATEMENT EXECUTE FUNCTION cove_sync_tag_name_claim();
            CREATE TRIGGER cove_tag_alias_claim_insert_delete
            AFTER INSERT OR DELETE ON tag_aliases
            FOR EACH ROW EXECUTE FUNCTION cove_sync_tag_alias_claim();
            CREATE TRIGGER cove_tag_alias_claim_update
            AFTER UPDATE OF "Id", "TagId", "NamespaceKey" ON tag_aliases
            FOR EACH ROW EXECUTE FUNCTION cove_sync_tag_alias_claim();
            CREATE TRIGGER cove_tag_alias_claim_truncate
            AFTER TRUNCATE ON tag_aliases
            FOR EACH STATEMENT EXECUTE FUNCTION cove_sync_tag_alias_claim();

            DO $cove_final_validation$
            BEGIN
                IF (SELECT count(*) FROM tag_name_claims)
                       <> (SELECT count(*) FROM tags) + (SELECT count(*) FROM tag_aliases)
                   OR EXISTS (
                       SELECT "NamespaceKey"
                       FROM tag_name_claims
                       GROUP BY "NamespaceKey"
                       HAVING count(*) > 1)
                   OR EXISTS (
                       SELECT 1
                       FROM performers performer
                       JOIN pg_temp.cove_name_rule_performers staged ON staged."Id" = performer."Id"
                       WHERE performer."Name" IS DISTINCT FROM staged."NormalizedName"
                          OR performer."Disambiguation" IS DISTINCT FROM staged."NormalizedDisambiguation"
                          OR performer."IdentityKey" IS DISTINCT FROM staged."IdentityKey")
                   OR EXISTS (
                       SELECT 1
                       FROM studios studio
                       JOIN pg_temp.cove_name_rule_studios staged ON staged."Id" = studio."Id"
                       WHERE studio."Name" IS DISTINCT FROM staged."NormalizedName"
                          OR studio."NameKey" IS DISTINCT FROM staged."NameKey") THEN
                    RAISE EXCEPTION 'COVE_NAME_RULE_GUARD: Final enforced name-rule validation failed. No migration changes were applied.';
                END IF;
            END
            $cove_final_validation$;
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            DROP TRIGGER IF EXISTS cove_tag_alias_claim_truncate ON tag_aliases;
            DROP TRIGGER IF EXISTS cove_tag_alias_claim_update ON tag_aliases;
            DROP TRIGGER IF EXISTS cove_tag_alias_claim_insert_delete ON tag_aliases;
            DROP TRIGGER IF EXISTS cove_tag_name_claim_truncate ON tags;
            DROP TRIGGER IF EXISTS cove_tag_name_claim_update ON tags;
            DROP TRIGGER IF EXISTS cove_tag_name_claim_insert_delete ON tags;
            DROP FUNCTION IF EXISTS cove_sync_tag_alias_claim();
            DROP FUNCTION IF EXISTS cove_sync_tag_name_claim();
            DROP TABLE IF EXISTS tag_name_claims;
            """);

        migrationBuilder.Sql("""
            ALTER TABLE studios DROP CONSTRAINT IF EXISTS "UQ_studios_name";
            ALTER TABLE performers DROP CONSTRAINT IF EXISTS "UQ_performers_identity";
            """);
        migrationBuilder.DropCheckConstraint(name: "CK_studios_NameKey", table: "studios");
        migrationBuilder.DropCheckConstraint(name: "CK_performers_IdentityKey", table: "performers");
        migrationBuilder.DropColumn(name: "NameKey", table: "studios");
        migrationBuilder.DropColumn(name: "IdentityKey", table: "performers");
        migrationBuilder.DropColumn(name: "NamespaceKey", table: "tag_aliases");
        migrationBuilder.DropColumn(name: "NamespaceKey", table: "tags");
        // Normalized display values are intentionally not expanded back to historical whitespace.
    }
}
