namespace Cove.Data.Services;

/// <summary>
/// The PostgreSQL triggers that mark a video's stored spans stale. They increment the video's row in
/// <c>segment_span_video_versions</c> whenever its video segments are inserted, updated or deleted, and
/// whenever a tag those segments use changes in a way span resolution reads (colors, lane, group). A
/// further trigger moves a display profile to a new version whenever its rules change.
/// Living in the database, they also see writers that bypass the application: extensions that write
/// segments with their own SQL, bulk copies and foreign-key actions. The migration creates them; a
/// database created from the model with <c>EnsureCreated</c> must run <see cref="CreateSql"/> itself.
/// </summary>
public static class SegmentSpanIndexSqlDefinitions
{
    public const string CreateSql = """
        CREATE OR REPLACE FUNCTION public.cove_segment_span_touch_videos(video_ids integer[])
        RETURNS void
        LANGUAGE sql
        AS $function$
            -- Ordered so that concurrent writers lock the version rows in the same order.
            INSERT INTO public.segment_span_video_versions ("VideoId", "Version")
            SELECT touched.id, 1
            FROM (SELECT DISTINCT id FROM unnest(video_ids) AS item(id) WHERE id IS NOT NULL) AS touched
            WHERE EXISTS (SELECT 1 FROM public.videos AS video WHERE video."Id" = touched.id)
            ORDER BY touched.id
            ON CONFLICT ("VideoId") DO UPDATE
                SET "Version" = public.segment_span_video_versions."Version" + 1, "ChangedAt" = now()
        $function$;

        -- The segment triggers run once per statement, and Entity Framework saves one statement per
        -- row, so this runs once for every segment an extension adds or removes. It therefore writes
        -- the version rows itself, with plans PL/pgSQL keeps, rather than building an array for
        -- cove_segment_span_touch_videos: that call cost about four times as much per statement.
        CREATE OR REPLACE FUNCTION public.cove_segment_span_segments_changed()
        RETURNS trigger
        LANGUAGE plpgsql
        AS $function$
        BEGIN
            IF TG_OP = 'INSERT' THEN
                INSERT INTO public.segment_span_video_versions ("VideoId", "Version")
                SELECT DISTINCT changed."HostId", 1
                FROM new_rows AS changed
                WHERE changed."HostType" = 1
                  AND EXISTS (SELECT 1 FROM public.videos AS video WHERE video."Id" = changed."HostId")
                ORDER BY 1
                ON CONFLICT ("VideoId") DO UPDATE
                    SET "Version" = public.segment_span_video_versions."Version" + 1, "ChangedAt" = now();
            ELSIF TG_OP = 'DELETE' THEN
                INSERT INTO public.segment_span_video_versions ("VideoId", "Version")
                SELECT DISTINCT changed."HostId", 1
                FROM old_rows AS changed
                WHERE changed."HostType" = 1
                  AND EXISTS (SELECT 1 FROM public.videos AS video WHERE video."Id" = changed."HostId")
                ORDER BY 1
                ON CONFLICT ("VideoId") DO UPDATE
                    SET "Version" = public.segment_span_video_versions."Version" + 1, "ChangedAt" = now();
            ELSE
                INSERT INTO public.segment_span_video_versions ("VideoId", "Version")
                SELECT DISTINCT changed."HostId", 1
                FROM (SELECT "HostType", "HostId" FROM new_rows
                      UNION ALL
                      SELECT "HostType", "HostId" FROM old_rows) AS changed
                WHERE changed."HostType" = 1
                  AND EXISTS (SELECT 1 FROM public.videos AS video WHERE video."Id" = changed."HostId")
                ORDER BY 1
                ON CONFLICT ("VideoId") DO UPDATE
                    SET "Version" = public.segment_span_video_versions."Version" + 1, "ChangedAt" = now();
            END IF;
            RETURN NULL;
        END
        $function$;

        CREATE OR REPLACE FUNCTION public.cove_segment_span_tags_changed()
        RETURNS trigger
        LANGUAGE plpgsql
        AS $function$
        BEGIN
            PERFORM public.cove_segment_span_touch_videos(ARRAY(
                SELECT segment."HostId"
                FROM new_rows AS changed
                JOIN old_rows AS previous ON previous."Id" = changed."Id"
                JOIN public.segments AS segment ON segment."TagId" = changed."Id" AND segment."HostType" = 1
                -- Not the name: a tagged span shows the tag's current name, read when it is listed.
                WHERE (changed."Color", changed."SegmentColorOverride", changed."SegmentLaneOverride", changed."TagGroupId")
                    IS DISTINCT FROM
                      (previous."Color", previous."SegmentColorOverride", previous."SegmentLaneOverride", previous."TagGroupId")));
            RETURN NULL;
        END
        $function$;

        CREATE OR REPLACE FUNCTION public.cove_segment_span_tag_groups_changed()
        RETURNS trigger
        LANGUAGE plpgsql
        AS $function$
        BEGIN
            PERFORM public.cove_segment_span_touch_videos(ARRAY(
                SELECT segment."HostId"
                FROM new_rows AS changed
                JOIN old_rows AS previous ON previous."Id" = changed."Id"
                JOIN public.tags AS tag ON tag."TagGroupId" = changed."Id"
                JOIN public.segments AS segment ON segment."TagId" = tag."Id" AND segment."HostType" = 1
                WHERE changed."Name" IS DISTINCT FROM previous."Name"));
            RETURN NULL;
        END
        $function$;

        CREATE OR REPLACE FUNCTION public.cove_segment_span_rules_changed()
        RETURNS trigger
        LANGUAGE plpgsql
        AS $function$
        BEGIN
            -- Every change to a profile's rules moves the profile to a new version, whoever makes it:
            -- the profile editor, a tag merge re-pointing a rule, or the foreign key clearing a deleted
            -- tag from one.
            IF TG_OP = 'INSERT' THEN
                UPDATE public.segment_display_profiles SET "Version" = "Version" + 1
                WHERE "Id" IN (SELECT "ProfileId" FROM new_rows);
            ELSIF TG_OP = 'DELETE' THEN
                UPDATE public.segment_display_profiles SET "Version" = "Version" + 1
                WHERE "Id" IN (SELECT "ProfileId" FROM old_rows);
            ELSE
                UPDATE public.segment_display_profiles SET "Version" = "Version" + 1
                WHERE "Id" IN (SELECT "ProfileId" FROM new_rows UNION SELECT "ProfileId" FROM old_rows);
            END IF;
            RETURN NULL;
        END
        $function$;

        DROP TRIGGER IF EXISTS cove_segment_span_rules_insert ON public.segment_display_rules;
        CREATE TRIGGER cove_segment_span_rules_insert
            AFTER INSERT ON public.segment_display_rules
            REFERENCING NEW TABLE AS new_rows
            FOR EACH STATEMENT EXECUTE FUNCTION public.cove_segment_span_rules_changed();

        DROP TRIGGER IF EXISTS cove_segment_span_rules_update ON public.segment_display_rules;
        CREATE TRIGGER cove_segment_span_rules_update
            AFTER UPDATE ON public.segment_display_rules
            REFERENCING OLD TABLE AS old_rows NEW TABLE AS new_rows
            FOR EACH STATEMENT EXECUTE FUNCTION public.cove_segment_span_rules_changed();

        DROP TRIGGER IF EXISTS cove_segment_span_rules_delete ON public.segment_display_rules;
        CREATE TRIGGER cove_segment_span_rules_delete
            AFTER DELETE ON public.segment_display_rules
            REFERENCING OLD TABLE AS old_rows
            FOR EACH STATEMENT EXECUTE FUNCTION public.cove_segment_span_rules_changed();

        DROP TRIGGER IF EXISTS cove_segment_span_segments_insert ON public.segments;
        CREATE TRIGGER cove_segment_span_segments_insert
            AFTER INSERT ON public.segments
            REFERENCING NEW TABLE AS new_rows
            FOR EACH STATEMENT EXECUTE FUNCTION public.cove_segment_span_segments_changed();

        DROP TRIGGER IF EXISTS cove_segment_span_segments_update ON public.segments;
        CREATE TRIGGER cove_segment_span_segments_update
            AFTER UPDATE ON public.segments
            REFERENCING OLD TABLE AS old_rows NEW TABLE AS new_rows
            FOR EACH STATEMENT EXECUTE FUNCTION public.cove_segment_span_segments_changed();

        DROP TRIGGER IF EXISTS cove_segment_span_segments_delete ON public.segments;
        CREATE TRIGGER cove_segment_span_segments_delete
            AFTER DELETE ON public.segments
            REFERENCING OLD TABLE AS old_rows
            FOR EACH STATEMENT EXECUTE FUNCTION public.cove_segment_span_segments_changed();

        DROP TRIGGER IF EXISTS cove_segment_span_tags_update ON public.tags;
        CREATE TRIGGER cove_segment_span_tags_update
            AFTER UPDATE ON public.tags
            REFERENCING OLD TABLE AS old_rows NEW TABLE AS new_rows
            FOR EACH STATEMENT EXECUTE FUNCTION public.cove_segment_span_tags_changed();

        DROP TRIGGER IF EXISTS cove_segment_span_tag_groups_update ON public.tag_groups;
        CREATE TRIGGER cove_segment_span_tag_groups_update
            AFTER UPDATE ON public.tag_groups
            REFERENCING OLD TABLE AS old_rows NEW TABLE AS new_rows
            FOR EACH STATEMENT EXECUTE FUNCTION public.cove_segment_span_tag_groups_changed();
        """;

    /// <summary>
    /// Gives every video that already has segments a version row, so spans are built for libraries
    /// that existed before the triggers did.
    /// </summary>
    public const string BackfillVersionsSql = """
        INSERT INTO public.segment_span_video_versions ("VideoId", "Version")
        SELECT video."Id", 1
        FROM public.videos AS video
        WHERE EXISTS (SELECT 1 FROM public.segments AS segment WHERE segment."HostType" = 1 AND segment."HostId" = video."Id")
        ON CONFLICT ("VideoId") DO NOTHING;
        """;

    public const string DropSql = """
        DROP TRIGGER IF EXISTS cove_segment_span_rules_delete ON public.segment_display_rules;
        DROP TRIGGER IF EXISTS cove_segment_span_rules_update ON public.segment_display_rules;
        DROP TRIGGER IF EXISTS cove_segment_span_rules_insert ON public.segment_display_rules;
        DROP TRIGGER IF EXISTS cove_segment_span_tag_groups_update ON public.tag_groups;
        DROP TRIGGER IF EXISTS cove_segment_span_tags_update ON public.tags;
        DROP TRIGGER IF EXISTS cove_segment_span_segments_delete ON public.segments;
        DROP TRIGGER IF EXISTS cove_segment_span_segments_update ON public.segments;
        DROP TRIGGER IF EXISTS cove_segment_span_segments_insert ON public.segments;
        DROP FUNCTION IF EXISTS public.cove_segment_span_rules_changed();
        DROP FUNCTION IF EXISTS public.cove_segment_span_tag_groups_changed();
        DROP FUNCTION IF EXISTS public.cove_segment_span_tags_changed();
        DROP FUNCTION IF EXISTS public.cove_segment_span_segments_changed();
        DROP FUNCTION IF EXISTS public.cove_segment_span_touch_videos(integer[]);
        """;
}
