namespace Cove.Data.Services;

/// <summary>
/// The PostgreSQL functions that the video shot set CHECK constraints call (see
/// <c>CoveContext.ConfigureVideoShotArrayChecks</c>). Migrations create them; a database created from the
/// model with <c>EnsureCreated</c> or a generated create script must run <see cref="CreateFunctionsSql"/>
/// first.
/// </summary>
public static class VideoShotSqlDefinitions
{
    /// <summary>
    /// Creates or replaces the functions. A cut must lie strictly inside the set and after the cut
    /// before it; comparisons with a finite bound also reject NaN (which PostgreSQL sorts above every
    /// number) and the infinities.
    /// </summary>
    public const string CreateFunctionsSql = """
        CREATE OR REPLACE FUNCTION public.cove_video_shot_cut_times_valid(cuts double precision[], duration double precision)
        RETURNS boolean
        LANGUAGE sql
        IMMUTABLE
        PARALLEL SAFE
        AS $function$
            SELECT NOT EXISTS (
                SELECT 1
                FROM (
                    SELECT item.cut, lag(item.cut) OVER (ORDER BY item.position) AS previous
                    FROM unnest(cuts) WITH ORDINALITY AS item(cut, position)
                ) AS ordered
                WHERE ordered.cut IS NULL
                   OR NOT (ordered.cut > 0 AND ordered.cut < duration)
                   OR ordered.cut <= ordered.previous)
        $function$;

        CREATE OR REPLACE FUNCTION public.cove_video_shot_cut_frames_valid(cuts integer[], frame_count integer)
        RETURNS boolean
        LANGUAGE sql
        IMMUTABLE
        PARALLEL SAFE
        AS $function$
            SELECT NOT EXISTS (
                SELECT 1
                FROM (
                    SELECT item.cut, lag(item.cut) OVER (ORDER BY item.position) AS previous
                    FROM unnest(cuts) WITH ORDINALITY AS item(cut, position)
                ) AS ordered
                WHERE ordered.cut IS NULL
                   OR NOT (ordered.cut > 0 AND ordered.cut < frame_count)
                   OR ordered.cut <= ordered.previous)
        $function$;
        """;

    public const string DropFunctionsSql = """
        DROP FUNCTION IF EXISTS public.cove_video_shot_cut_frames_valid(integer[], integer);
        DROP FUNCTION IF EXISTS public.cove_video_shot_cut_times_valid(double precision[], double precision);
        """;
}
