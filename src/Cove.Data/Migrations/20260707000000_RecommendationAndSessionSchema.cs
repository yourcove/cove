using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Infrastructure;

#nullable disable

namespace Cove.Data.Migrations
{
    /// <inheritdoc />
    [DbContext(typeof(CoveContext))]
    [Migration("20260707000000_RecommendationAndSessionSchema")]
    public partial class RecommendationAndSessionSchema : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Consolidates the post-0.7.1 recommendation + session work into one migration:
            //   * user_entity_affinities signal columns (IsBookmarked, MaxDwell{,Start}Sec) and the removal
            //     of the dead HideCount/ShareCount rollups.
            //   * a one-time dedup + unique-index repair on user_entity_affinities.
            //   * global UserSessions + PlaybackSessions.UserSessionId.
            //   * the recommender/list support indexes (files(ImageId,Basename), embeddings composite, and
            //     the per-family asset-level HNSW ANN indexes).
            // Everything is written idempotently (IF [NOT] EXISTS) so it is safe on a fresh 0.7.1 database and
            // a no-op on one that already had the earlier iterations of these changes applied.

            // --- unify table naming on snake_case ------------------------------------------------------
            // A handful of secondary tables historically kept their PascalCase CLR type / DbSet name while
            // the rest of the schema is snake_case. Rename them so there is one convention. ALTER TABLE
            // IF EXISTS makes each rename a no-op once applied (the old name no longer resolves on re-run),
            // and it runs before the session changes below which target the renamed playback_sessions.
            // Only table names change here; PK/index/FK names are internal and are regenerated at the
            // full-release migration squash. Data, indexes and FKs are preserved (rename, not copy).
            migrationBuilder.Sql("""
                ALTER TABLE IF EXISTS "FileFingerprints"  RENAME TO file_fingerprints;
                ALTER TABLE IF EXISTS "VideoCaptions"     RENAME TO video_captions;
                ALTER TABLE IF EXISTS "VideoUrl"          RENAME TO video_urls;
                ALTER TABLE IF EXISTS "GalleryUrl"        RENAME TO gallery_urls;
                ALTER TABLE IF EXISTS "GroupUrl"          RENAME TO group_urls;
                ALTER TABLE IF EXISTS "ImageUrl"          RENAME TO image_urls;
                ALTER TABLE IF EXISTS "PerformerUrl"      RENAME TO performer_urls;
                ALTER TABLE IF EXISTS "StudioUrl"         RENAME TO studio_urls;
                ALTER TABLE IF EXISTS "PerformerAlias"    RENAME TO performer_aliases;
                ALTER TABLE IF EXISTS "StudioAlias"       RENAME TO studio_aliases;
                ALTER TABLE IF EXISTS "TagAlias"          RENAME TO tag_aliases;
                ALTER TABLE IF EXISTS "VideoRemoteId"     RENAME TO video_remote_ids;
                ALTER TABLE IF EXISTS "PerformerRemoteId" RENAME TO performer_remote_ids;
                ALTER TABLE IF EXISTS "StudioRemoteId"    RENAME TO studio_remote_ids;
                ALTER TABLE IF EXISTS "TagRemoteId"       RENAME TO tag_remote_ids;
                ALTER TABLE IF EXISTS "VideoPlayHistory"  RENAME TO video_play_history;
                ALTER TABLE IF EXISTS "PlaybackIntervals" RENAME TO playback_intervals;
                ALTER TABLE IF EXISTS "PlaybackSessions"  RENAME TO playback_sessions;
                ALTER TABLE IF EXISTS "UserSessions"      RENAME TO user_sessions;
                """);

            // --- drop the legacy VideoLikeHistory table ------------------------------------------------
            // A global, Stash-import-only "like" log. Live favorites are tracked per-user in
            // user_entity_affinities (IsFavorite / FavoritedAt) and interactions, and the "last liked at"
            // sort now reads FavoritedAt, so this table is dead. Drop both the pre- and post-rename name.
            migrationBuilder.Sql("""
                DROP TABLE IF EXISTS video_like_history;
                DROP TABLE IF EXISTS "VideoLikeHistory";
                """);

            // --- remove the abandoned entity_identifiers apparatus -------------------------------------
            // entity_identifiers backed the "identifier" content-rule scope kind (the abandoned Schema C
            // universal-identifier project). That feature is removed wholesale: purge any identifier
            // content rules, drop the authz SQL function that read this table, then drop the table. URLs,
            // aliases and remote-ids continue to live in their per-entity tables (the display source of
            // truth); only this parallel copy + its half-built auth feature go away.
            migrationBuilder.Sql("""
                DELETE FROM role_content_rules WHERE lower("ScopeKind") = 'identifier';
                DROP FUNCTION IF EXISTS public.cove_authz_entity_matches_identifier(text, integer, jsonb) CASCADE;
                DROP TABLE IF EXISTS entity_identifiers;
                """);

            // --- user_entity_affinities signal columns -------------------------------------------------
            migrationBuilder.Sql("""
                ALTER TABLE IF EXISTS user_entity_affinities ADD COLUMN IF NOT EXISTS "IsBookmarked" boolean NOT NULL DEFAULT false;
                ALTER TABLE IF EXISTS user_entity_affinities ADD COLUMN IF NOT EXISTS "MaxDwellSec" double precision NOT NULL DEFAULT 0;
                ALTER TABLE IF EXISTS user_entity_affinities ADD COLUMN IF NOT EXISTS "MaxDwellStartSec" double precision NOT NULL DEFAULT 0;

                -- HideCount/ShareCount were only ever written by InteractionKind.Hide/Share, which have no
                -- producer, so they were always 0 and nothing reads them.
                ALTER TABLE IF EXISTS user_entity_affinities DROP COLUMN IF EXISTS "HideCount";
                ALTER TABLE IF EXISTS user_entity_affinities DROP COLUMN IF EXISTS "ShareCount";
                """);

            // --- user_entity_affinities dedup + unique-index repair ------------------------------------
            // The (UserId, HostType, HostId) unique index belongs to the baseline schema, but some databases
            // arrive without it (Stash imports / plain-SQL backup restores that recreate the public schema
            // without every index) and/or carry duplicate rows. Without it the favorite/bookmark upsert
            // (INSERT ... ON CONFLICT ("UserId","HostType","HostId")) throws 42P10, and duplicate rows surface
            // as "same key already added" on HostId-keyed reads. Repair idempotently — a no-op when healthy.
            // Runs after IsBookmarked exists because the coalesce step references it.
            migrationBuilder.Sql("""
                -- Coalesce favorite/bookmark flags across duplicates so dedup can't drop a row that held one.
                UPDATE user_entity_affinities k
                SET "IsFavorite" = d.fav, "IsBookmarked" = d.book
                FROM (
                    SELECT "UserId", "HostType", "HostId",
                           bool_or("IsFavorite") AS fav, bool_or("IsBookmarked") AS book
                    FROM user_entity_affinities
                    GROUP BY "UserId", "HostType", "HostId"
                    HAVING count(*) > 1
                ) d
                WHERE k."UserId" = d."UserId" AND k."HostType" = d."HostType" AND k."HostId" = d."HostId";

                -- Remove duplicates, keeping the oldest (lowest Id) per (UserId, HostType, HostId).
                DELETE FROM user_entity_affinities a
                USING user_entity_affinities b
                WHERE a."UserId" = b."UserId" AND a."HostType" = b."HostType" AND a."HostId" = b."HostId"
                  AND a."Id" > b."Id";

                CREATE UNIQUE INDEX IF NOT EXISTS "IX_user_entity_affinities_UserId_HostType_HostId"
                ON user_entity_affinities ("UserId", "HostType", "HostId");
                """);

            // --- global user_sessions + playback_sessions.UserSessionId --------------------------------
            // (playback_sessions was renamed from "PlaybackSessions" above; user_sessions is created fresh
            //  here, or was renamed above from an "UserSessions" left by an earlier iteration.)
            migrationBuilder.Sql("""
                CREATE TABLE IF NOT EXISTS user_sessions (
                    "Id" integer GENERATED BY DEFAULT AS IDENTITY,
                    "UserId" integer NOT NULL,
                    "StartedAt" timestamp with time zone NOT NULL,
                    "LastSeenAt" timestamp with time zone NOT NULL,
                    "LastHostType" integer,
                    "LastHostId" integer,
                    "DerivedLikeAwarded" boolean NOT NULL,
                    "CreatedAt" timestamp with time zone NOT NULL,
                    "UpdatedAt" timestamp with time zone NOT NULL,
                    CONSTRAINT "PK_user_sessions" PRIMARY KEY ("Id")
                );

                CREATE INDEX IF NOT EXISTS "IX_user_sessions_UserId_LastSeenAt"
                ON user_sessions ("UserId", "LastSeenAt");

                ALTER TABLE IF EXISTS playback_sessions ADD COLUMN IF NOT EXISTS "UserSessionId" integer;

                -- Replace the old per-(UserId,SessionId) uniqueness with per-(user, entity, global session).
                DROP INDEX IF EXISTS "IX_PlaybackSessions_UserId_SessionId";
                CREATE UNIQUE INDEX IF NOT EXISTS "IX_playback_sessions_UserId_HostType_HostId_UserSessionId"
                ON playback_sessions ("UserId", "HostType", "HostId", "UserSessionId");
                """);

            // --- list / recommender support indexes ----------------------------------------------------
            migrationBuilder.Sql("""
                -- Image "title" sort falls back to MIN/MAX(Basename) WHERE ImageId = ?; without this the
                -- image list scans the files table per row and times out on large libraries.
                CREATE INDEX IF NOT EXISTS "IX_files_ImageId_Basename" ON files ("ImageId", "Basename");

                -- Asset-level embedding fetch by id (HostType, HostId IN (...), Modality, SectionIndex = 0).
                CREATE INDEX IF NOT EXISTS "IX_embeddings_HostType_HostId_Modality_SectionIndex"
                ON embeddings ("HostType", "HostId", "Modality", "SectionIndex");
                """);

            // --- per-family asset-level HNSW ANN indexes -----------------------------------------------
            // One asset-level (SectionIndex = 0) HNSW index per Cove-owned embedding kind: semantic.v1
            // (MetaCLIP2) and feature.v1 (DINOv3) visual, plus audio.v1. These are ~one row per asset so they
            // build in seconds. Dim differs per kind, so it is detected dynamically; the DO blocks are
            // non-fatal so a fresh DB with no embeddings simply skips creation.
            migrationBuilder.Sql("""
                DO $$
                DECLARE d int;
                BEGIN
                    SELECT "Dim" INTO d FROM embeddings WHERE "Modality" = 1 AND "KindFamily" = 'semantic.v1' AND "SectionIndex" = 0 LIMIT 1;
                    IF d IS NOT NULL THEN
                        EXECUTE format('CREATE INDEX IF NOT EXISTS ix_embeddings_visual_semantic_asset_hnsw ON embeddings USING hnsw (((%I)::vector(%s)) vector_cosine_ops) WHERE "Modality" = 1 AND "KindFamily" = ''semantic.v1'' AND "SectionIndex" = 0', 'Vector', d);
                    END IF;
                EXCEPTION WHEN OTHERS THEN NULL;
                END $$;

                DO $$
                DECLARE d int;
                BEGIN
                    SELECT "Dim" INTO d FROM embeddings WHERE "Modality" = 1 AND "KindFamily" = 'feature.v1' AND "SectionIndex" = 0 LIMIT 1;
                    IF d IS NOT NULL THEN
                        EXECUTE format('CREATE INDEX IF NOT EXISTS ix_embeddings_visual_feature_asset_hnsw ON embeddings USING hnsw (((%I)::vector(%s)) vector_cosine_ops) WHERE "Modality" = 1 AND "KindFamily" = ''feature.v1'' AND "SectionIndex" = 0', 'Vector', d);
                    END IF;
                EXCEPTION WHEN OTHERS THEN NULL;
                END $$;

                DO $$
                DECLARE d int;
                BEGIN
                    SELECT "Dim" INTO d FROM embeddings WHERE "Modality" = 2 AND "KindFamily" = 'audio.v1' AND "SectionIndex" = 0 LIMIT 1;
                    IF d IS NOT NULL THEN
                        EXECUTE format('CREATE INDEX IF NOT EXISTS ix_embeddings_audio_asset_hnsw ON embeddings USING hnsw (((%I)::vector(%s)) vector_cosine_ops) WHERE "Modality" = 2 AND "KindFamily" = ''audio.v1'' AND "SectionIndex" = 0', 'Vector', d);
                    END IF;
                EXCEPTION WHEN OTHERS THEN NULL;
                END $$;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DROP INDEX IF EXISTS ix_embeddings_audio_asset_hnsw;
                DROP INDEX IF EXISTS ix_embeddings_visual_feature_asset_hnsw;
                DROP INDEX IF EXISTS ix_embeddings_visual_semantic_asset_hnsw;
                DROP INDEX IF EXISTS "IX_embeddings_HostType_HostId_Modality_SectionIndex";
                DROP INDEX IF EXISTS "IX_files_ImageId_Basename";
                """);

            migrationBuilder.Sql("""
                DROP INDEX IF EXISTS "IX_playback_sessions_UserId_HostType_HostId_UserSessionId";
                CREATE UNIQUE INDEX IF NOT EXISTS "IX_PlaybackSessions_UserId_SessionId"
                ON playback_sessions ("UserId", "SessionId");
                ALTER TABLE IF EXISTS playback_sessions DROP COLUMN IF EXISTS "UserSessionId";
                DROP TABLE IF EXISTS user_sessions;
                """);

            // Leave IX_user_entity_affinities_UserId_HostType_HostId in place — it belongs to the baseline
            // schema and dropping it would re-break the favorite/bookmark upsert.
            migrationBuilder.Sql("""
                ALTER TABLE IF EXISTS user_entity_affinities DROP COLUMN IF EXISTS "MaxDwellStartSec";
                ALTER TABLE IF EXISTS user_entity_affinities DROP COLUMN IF EXISTS "MaxDwellSec";
                ALTER TABLE IF EXISTS user_entity_affinities DROP COLUMN IF EXISTS "IsBookmarked";
                ALTER TABLE IF EXISTS user_entity_affinities ADD COLUMN IF NOT EXISTS "ShareCount" integer NOT NULL DEFAULT 0;
                ALTER TABLE IF EXISTS user_entity_affinities ADD COLUMN IF NOT EXISTS "HideCount" integer NOT NULL DEFAULT 0;
                """);

            // Recreate the dropped entity_identifiers table shell (empty; the identifier content-rule
            // feature and any purged rules are not restored on rollback).
            migrationBuilder.Sql("""
                CREATE TABLE IF NOT EXISTS entity_identifiers (
                    "Id" integer GENERATED BY DEFAULT AS IDENTITY,
                    "EntityKind" character varying(32) NOT NULL,
                    "EntityId" integer NOT NULL,
                    "Scheme" character varying(32) NOT NULL,
                    "Value" character varying(2000) NOT NULL,
                    "NormalizedValue" character varying(2000) NOT NULL,
                    "Source" character varying(200),
                    "CreatedAt" timestamp with time zone NOT NULL,
                    CONSTRAINT "PK_entity_identifiers" PRIMARY KEY ("Id")
                );
                CREATE UNIQUE INDEX IF NOT EXISTS "IX_entity_identifiers_EntityKind_EntityId_Scheme_NormalizedValue"
                    ON entity_identifiers ("EntityKind", "EntityId", "Scheme", "NormalizedValue");
                CREATE INDEX IF NOT EXISTS "IX_entity_identifiers_Scheme_NormalizedValue"
                    ON entity_identifiers ("Scheme", "NormalizedValue");
                """);

            // Recreate the dropped legacy VideoLikeHistory table (empty — its imported data cannot be
            // reconstructed on rollback).
            migrationBuilder.Sql("""
                CREATE TABLE IF NOT EXISTS "VideoLikeHistory" (
                    "Id" integer GENERATED BY DEFAULT AS IDENTITY,
                    "VideoId" integer NOT NULL,
                    "OccurredAt" timestamp with time zone NOT NULL,
                    CONSTRAINT "PK_VideoLikeHistory" PRIMARY KEY ("Id"),
                    CONSTRAINT "FK_VideoLikeHistory_videos_VideoId" FOREIGN KEY ("VideoId") REFERENCES videos ("Id") ON DELETE CASCADE
                );
                CREATE INDEX IF NOT EXISTS "IX_VideoLikeHistory_VideoId" ON "VideoLikeHistory" ("VideoId");
                """);

            // Restore the original PascalCase table names (user_sessions was dropped above, so it is omitted).
            migrationBuilder.Sql("""
                ALTER TABLE IF EXISTS file_fingerprints    RENAME TO "FileFingerprints";
                ALTER TABLE IF EXISTS video_captions       RENAME TO "VideoCaptions";
                ALTER TABLE IF EXISTS video_urls           RENAME TO "VideoUrl";
                ALTER TABLE IF EXISTS gallery_urls         RENAME TO "GalleryUrl";
                ALTER TABLE IF EXISTS group_urls           RENAME TO "GroupUrl";
                ALTER TABLE IF EXISTS image_urls           RENAME TO "ImageUrl";
                ALTER TABLE IF EXISTS performer_urls       RENAME TO "PerformerUrl";
                ALTER TABLE IF EXISTS studio_urls          RENAME TO "StudioUrl";
                ALTER TABLE IF EXISTS performer_aliases    RENAME TO "PerformerAlias";
                ALTER TABLE IF EXISTS studio_aliases       RENAME TO "StudioAlias";
                ALTER TABLE IF EXISTS tag_aliases          RENAME TO "TagAlias";
                ALTER TABLE IF EXISTS video_remote_ids     RENAME TO "VideoRemoteId";
                ALTER TABLE IF EXISTS performer_remote_ids RENAME TO "PerformerRemoteId";
                ALTER TABLE IF EXISTS studio_remote_ids    RENAME TO "StudioRemoteId";
                ALTER TABLE IF EXISTS tag_remote_ids       RENAME TO "TagRemoteId";
                ALTER TABLE IF EXISTS video_play_history   RENAME TO "VideoPlayHistory";
                ALTER TABLE IF EXISTS playback_intervals   RENAME TO "PlaybackIntervals";
                ALTER TABLE IF EXISTS playback_sessions    RENAME TO "PlaybackSessions";
                """);
        }
    }
}
