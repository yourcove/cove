namespace Cove.Data.Auth;

public static class AuthorizationSqlDefinitions
{
    public const string CreateFunctionsSql = """
            CREATE OR REPLACE FUNCTION public.cove_authz_entity_has_tag(
                p_kind text,
                p_entity_id integer,
                p_tag_id integer
            ) RETURNS boolean
            LANGUAGE sql
            STABLE
            AS $$
                SELECT CASE lower(p_kind)
                    WHEN 'video' THEN EXISTS (SELECT 1 FROM video_tags st WHERE st."TagId" = p_tag_id AND st."VideoId" = p_entity_id)
                    WHEN 'performer' THEN EXISTS (SELECT 1 FROM performer_tags pt WHERE pt."TagId" = p_tag_id AND pt."PerformerId" = p_entity_id)
                    WHEN 'tag' THEN p_entity_id = p_tag_id
                    WHEN 'studio' THEN EXISTS (SELECT 1 FROM studio_tags st WHERE st."TagId" = p_tag_id AND st."StudioId" = p_entity_id)
                    WHEN 'gallery' THEN EXISTS (SELECT 1 FROM gallery_tags gt WHERE gt."TagId" = p_tag_id AND gt."GalleryId" = p_entity_id)
                    WHEN 'image' THEN EXISTS (SELECT 1 FROM image_tags it WHERE it."TagId" = p_tag_id AND it."ImageId" = p_entity_id)
                    WHEN 'group' THEN EXISTS (SELECT 1 FROM group_tags gt WHERE gt."TagId" = p_tag_id AND gt."GroupId" = p_entity_id)
                    WHEN 'file' THEN EXISTS (
                        SELECT 1
                        FROM files f
                        WHERE f."Id" = p_entity_id
                          AND (
                              (f."FileType" = 'Video' AND EXISTS (
                                  SELECT 1 FROM video_tags st WHERE st."TagId" = p_tag_id AND st."VideoId" = f."VideoId"
                              ))
                              OR (f."FileType" = 'Image' AND EXISTS (
                                  SELECT 1 FROM image_tags it WHERE it."TagId" = p_tag_id AND it."ImageId" = f."ImageId"
                              ))
                              OR (f."FileType" = 'Gallery' AND EXISTS (
                                  SELECT 1 FROM gallery_tags gt WHERE gt."TagId" = p_tag_id AND gt."GalleryId" = f."GalleryId"
                              ))
                          )
                    )
                    WHEN 'marker' THEN EXISTS (SELECT 1 FROM video_marker_tags smt WHERE smt."TagId" = p_tag_id AND smt."VideoMarkerId" = p_entity_id)
                        OR EXISTS (SELECT 1 FROM video_markers sm WHERE sm."Id" = p_entity_id AND sm."PrimaryTagId" = p_tag_id)
                    ELSE false
                END;
            $$;

            CREATE OR REPLACE FUNCTION public.cove_authz_entity_has_studio(
                p_kind text,
                p_entity_id integer,
                p_studio_id integer
            ) RETURNS boolean
            LANGUAGE sql
            STABLE
            AS $$
                SELECT CASE lower(p_kind)
                    WHEN 'video' THEN EXISTS (SELECT 1 FROM videos s WHERE s."Id" = p_entity_id AND s."StudioId" = p_studio_id)
                    WHEN 'studio' THEN p_entity_id = p_studio_id
                    WHEN 'gallery' THEN EXISTS (SELECT 1 FROM galleries g WHERE g."Id" = p_entity_id AND g."StudioId" = p_studio_id)
                    WHEN 'image' THEN EXISTS (SELECT 1 FROM images i WHERE i."Id" = p_entity_id AND i."StudioId" = p_studio_id)
                    WHEN 'group' THEN EXISTS (SELECT 1 FROM groups g WHERE g."Id" = p_entity_id AND g."StudioId" = p_studio_id)
                    WHEN 'file' THEN EXISTS (
                        SELECT 1
                        FROM files f
                        WHERE f."Id" = p_entity_id
                          AND (
                              (f."FileType" = 'Video' AND EXISTS (
                                  SELECT 1 FROM videos s WHERE s."Id" = f."VideoId" AND s."StudioId" = p_studio_id
                              ))
                              OR (f."FileType" = 'Image' AND EXISTS (
                                  SELECT 1 FROM images i WHERE i."Id" = f."ImageId" AND i."StudioId" = p_studio_id
                              ))
                              OR (f."FileType" = 'Gallery' AND EXISTS (
                                  SELECT 1 FROM galleries g WHERE g."Id" = f."GalleryId" AND g."StudioId" = p_studio_id
                              ))
                          )
                    )
                    WHEN 'marker' THEN EXISTS (
                        SELECT 1
                        FROM video_markers sm
                        JOIN videos s ON s."Id" = sm."VideoId"
                        WHERE sm."Id" = p_entity_id AND s."StudioId" = p_studio_id
                    )
                    ELSE false
                END;
            $$;

            CREATE OR REPLACE FUNCTION public.cove_authz_entity_json(
                p_kind text,
                p_entity_id integer
            ) RETURNS jsonb
            LANGUAGE sql
            STABLE
            AS $$
                SELECT coalesce(
                    CASE lower(p_kind)
                        WHEN 'video' THEN (
                            SELECT jsonb_object_agg(lower(entry.key), entry.value)
                            FROM videos entity
                            CROSS JOIN LATERAL jsonb_each(to_jsonb(entity)) AS entry
                            WHERE entity."Id" = p_entity_id
                        )
                        WHEN 'performer' THEN (
                            SELECT jsonb_object_agg(lower(entry.key), entry.value)
                            FROM performers entity
                            CROSS JOIN LATERAL jsonb_each(to_jsonb(entity)) AS entry
                            WHERE entity."Id" = p_entity_id
                        )
                        WHEN 'tag' THEN (
                            SELECT jsonb_object_agg(lower(entry.key), entry.value)
                            FROM tags entity
                            CROSS JOIN LATERAL jsonb_each(to_jsonb(entity)) AS entry
                            WHERE entity."Id" = p_entity_id
                        )
                        WHEN 'studio' THEN (
                            SELECT jsonb_object_agg(lower(entry.key), entry.value)
                            FROM studios entity
                            CROSS JOIN LATERAL jsonb_each(to_jsonb(entity)) AS entry
                            WHERE entity."Id" = p_entity_id
                        )
                        WHEN 'gallery' THEN (
                            SELECT jsonb_object_agg(lower(entry.key), entry.value)
                            FROM galleries entity
                            CROSS JOIN LATERAL jsonb_each(to_jsonb(entity)) AS entry
                            WHERE entity."Id" = p_entity_id
                        )
                        WHEN 'image' THEN (
                            SELECT jsonb_object_agg(lower(entry.key), entry.value)
                            FROM images entity
                            CROSS JOIN LATERAL jsonb_each(to_jsonb(entity)) AS entry
                            WHERE entity."Id" = p_entity_id
                        )
                        WHEN 'group' THEN (
                            SELECT jsonb_object_agg(lower(entry.key), entry.value)
                            FROM groups entity
                            CROSS JOIN LATERAL jsonb_each(to_jsonb(entity)) AS entry
                            WHERE entity."Id" = p_entity_id
                        )
                        WHEN 'file' THEN (
                            SELECT jsonb_object_agg(lower(entry.key), entry.value)
                            FROM files entity
                            CROSS JOIN LATERAL jsonb_each(to_jsonb(entity)) AS entry
                            WHERE entity."Id" = p_entity_id
                        )
                        WHEN 'marker' THEN (
                            SELECT jsonb_object_agg(lower(entry.key), entry.value)
                            FROM video_markers entity
                            CROSS JOIN LATERAL jsonb_each(to_jsonb(entity)) AS entry
                            WHERE entity."Id" = p_entity_id
                        )
                        ELSE null
                    END,
                    '{}'::jsonb
                );
            $$;

            CREATE OR REPLACE FUNCTION public.cove_authz_json_scalar_text(p_value jsonb)
            RETURNS text
            LANGUAGE sql
            IMMUTABLE
            AS $$
                SELECT CASE
                    WHEN p_value IS NULL OR p_value = 'null'::jsonb THEN NULL
                    WHEN jsonb_typeof(p_value) = 'string' THEN trim(both '"' FROM p_value::text)
                    ELSE p_value::text
                END;
            $$;

            CREATE OR REPLACE FUNCTION public.cove_authz_entity_matches_attribute(
                p_kind text,
                p_entity_id integer,
                p_scope_value jsonb
            ) RETURNS boolean
            LANGUAGE plpgsql
            STABLE
            AS $$
            DECLARE
                v_path text := lower(coalesce(p_scope_value ->> 'path', p_scope_value ->> 'field', ''));
                v_actual jsonb;
                v_actual_text text;
                v_expected_text text;
                v_expected_number numeric;
                v_actual_number numeric;
                v_exists boolean;
            BEGIN
                IF v_path = '' THEN
                    RETURN false;
                END IF;

                v_actual := public.cove_authz_entity_json(p_kind, p_entity_id) #> string_to_array(v_path, '.');
                v_exists := v_actual IS NOT NULL AND v_actual <> 'null'::jsonb;

                IF p_scope_value ? 'exists' THEN
                    IF coalesce((p_scope_value ->> 'exists')::boolean, false) THEN
                        RETURN v_exists;
                    END IF;

                    RETURN NOT v_exists;
                END IF;

                IF NOT v_exists THEN
                    RETURN false;
                END IF;

                v_actual_text := lower(coalesce(public.cove_authz_json_scalar_text(v_actual), ''));

                IF p_scope_value ? 'equals' THEN
                    RETURN v_actual = p_scope_value -> 'equals'
                        OR v_actual_text = lower(coalesce(public.cove_authz_json_scalar_text(p_scope_value -> 'equals'), ''));
                END IF;

                IF p_scope_value ? 'notEquals' THEN
                    RETURN NOT (
                        v_actual = p_scope_value -> 'notEquals'
                        OR v_actual_text = lower(coalesce(public.cove_authz_json_scalar_text(p_scope_value -> 'notEquals'), ''))
                    );
                END IF;

                IF p_scope_value ? 'contains' THEN
                    IF jsonb_typeof(v_actual) = 'array' THEN
                        RETURN EXISTS (
                            SELECT 1
                            FROM jsonb_array_elements(v_actual) AS element
                            WHERE lower(coalesce(public.cove_authz_json_scalar_text(element), '')) = lower(coalesce(p_scope_value ->> 'contains', ''))
                        );
                    END IF;

                    RETURN position(lower(coalesce(p_scope_value ->> 'contains', '')) IN v_actual_text) > 0;
                END IF;

                IF p_scope_value ? 'startsWith' THEN
                    RETURN v_actual_text LIKE lower(coalesce(p_scope_value ->> 'startsWith', '')) || '%';
                END IF;

                IF p_scope_value ? 'endsWith' THEN
                    RETURN v_actual_text LIKE '%' || lower(coalesce(p_scope_value ->> 'endsWith', ''));
                END IF;

                IF p_scope_value ? 'regex' THEN
                    RETURN coalesce(public.cove_authz_json_scalar_text(v_actual), '') ~* coalesce(p_scope_value ->> 'regex', '');
                END IF;

                IF p_scope_value ? 'in' THEN
                    RETURN EXISTS (
                        SELECT 1
                        FROM jsonb_array_elements(p_scope_value -> 'in') AS candidate
                        WHERE candidate = v_actual
                           OR lower(coalesce(public.cove_authz_json_scalar_text(candidate), '')) = v_actual_text
                    );
                END IF;

                IF p_scope_value ? 'gt' OR p_scope_value ? 'gte' OR p_scope_value ? 'lt' OR p_scope_value ? 'lte' THEN
                    v_actual_number := NULLIF(regexp_replace(coalesce(public.cove_authz_json_scalar_text(v_actual), ''), '[^0-9.\-]', '', 'g'), '')::numeric;

                    IF p_scope_value ? 'gt' THEN
                        v_expected_text := public.cove_authz_json_scalar_text(p_scope_value -> 'gt');
                        v_expected_number := NULLIF(regexp_replace(coalesce(v_expected_text, ''), '[^0-9.\-]', '', 'g'), '')::numeric;
                        RETURN v_actual_number IS NOT NULL AND v_expected_number IS NOT NULL AND v_actual_number > v_expected_number;
                    END IF;

                    IF p_scope_value ? 'gte' THEN
                        v_expected_text := public.cove_authz_json_scalar_text(p_scope_value -> 'gte');
                        v_expected_number := NULLIF(regexp_replace(coalesce(v_expected_text, ''), '[^0-9.\-]', '', 'g'), '')::numeric;
                        RETURN v_actual_number IS NOT NULL AND v_expected_number IS NOT NULL AND v_actual_number >= v_expected_number;
                    END IF;

                    IF p_scope_value ? 'lt' THEN
                        v_expected_text := public.cove_authz_json_scalar_text(p_scope_value -> 'lt');
                        v_expected_number := NULLIF(regexp_replace(coalesce(v_expected_text, ''), '[^0-9.\-]', '', 'g'), '')::numeric;
                        RETURN v_actual_number IS NOT NULL AND v_expected_number IS NOT NULL AND v_actual_number < v_expected_number;
                    END IF;

                    IF p_scope_value ? 'lte' THEN
                        v_expected_text := public.cove_authz_json_scalar_text(p_scope_value -> 'lte');
                        v_expected_number := NULLIF(regexp_replace(coalesce(v_expected_text, ''), '[^0-9.\-]', '', 'g'), '')::numeric;
                        RETURN v_actual_number IS NOT NULL AND v_expected_number IS NOT NULL AND v_actual_number <= v_expected_number;
                    END IF;
                END IF;

                RETURN false;
            END;
            $$;

            CREATE OR REPLACE FUNCTION public.cove_authz_expression_rule_matches(
                p_kind text,
                p_entity_id integer,
                p_rule jsonb
            ) RETURNS boolean
            LANGUAGE plpgsql
            STABLE
            AS $$
            DECLARE
                v_scope_kind text := lower(coalesce(p_rule ->> 'scopeKind', p_rule ->> 'scope_kind', ''));
                v_scope_value jsonb := coalesce(p_rule -> 'scopeValue', p_rule -> 'scope_value', '{}'::jsonb);
                v_operator text;
            BEGIN
                CASE v_scope_kind
                    WHEN 'all' THEN
                        RETURN true;
                    WHEN 'tag' THEN
                        RETURN public.cove_authz_entity_has_tag(p_kind, p_entity_id, NULLIF(v_scope_value ->> 'tagId', '')::integer);
                    WHEN 'studio' THEN
                        RETURN public.cove_authz_entity_has_studio(p_kind, p_entity_id, NULLIF(v_scope_value ->> 'studioId', '')::integer);
                    WHEN 'attribute' THEN
                        RETURN public.cove_authz_entity_matches_attribute(p_kind, p_entity_id, v_scope_value);
                    WHEN 'expression' THEN
                        v_operator := lower(coalesce(v_scope_value ->> 'op', ''));

                        CASE v_operator
                            WHEN 'and' THEN
                                RETURN NOT EXISTS (
                                    SELECT 1
                                    FROM jsonb_array_elements(coalesce(v_scope_value -> 'rules', '[]'::jsonb)) AS item
                                    WHERE NOT public.cove_authz_expression_rule_matches(p_kind, p_entity_id, item)
                                );
                            WHEN 'or' THEN
                                RETURN EXISTS (
                                    SELECT 1
                                    FROM jsonb_array_elements(coalesce(v_scope_value -> 'rules', '[]'::jsonb)) AS item
                                    WHERE public.cove_authz_expression_rule_matches(p_kind, p_entity_id, item)
                                );
                            WHEN 'not' THEN
                                RETURN NOT public.cove_authz_expression_rule_matches(
                                    p_kind,
                                    p_entity_id,
                                    coalesce(v_scope_value -> 'rule', '{}'::jsonb)
                                );
                            ELSE
                                RETURN false;
                        END CASE;
                    ELSE
                        RETURN false;
                END CASE;
            END;
            $$;

            CREATE OR REPLACE FUNCTION public.cove_authz_entity_matches_expression(
                p_kind text,
                p_entity_id integer,
                p_scope_value jsonb
            ) RETURNS boolean
            LANGUAGE sql
            STABLE
            AS $$
                SELECT public.cove_authz_expression_rule_matches(
                    p_kind,
                    p_entity_id,
                    jsonb_build_object('scopeKind', 'expression', 'scopeValue', coalesce(p_scope_value, '{}'::jsonb))
                );
            $$;

            CREATE OR REPLACE FUNCTION public.cove_authz_rule_matches(
                p_kind text,
                p_entity_id integer,
                p_scope_kind text,
                p_scope_value jsonb
            ) RETURNS boolean
            LANGUAGE sql
            STABLE
            AS $$
                SELECT CASE lower(p_scope_kind)
                    WHEN 'all' THEN true
                    WHEN 'tag' THEN public.cove_authz_entity_has_tag(p_kind, p_entity_id, NULLIF(p_scope_value ->> 'tagId', '')::integer)
                    WHEN 'studio' THEN public.cove_authz_entity_has_studio(p_kind, p_entity_id, NULLIF(p_scope_value ->> 'studioId', '')::integer)
                    WHEN 'attribute' THEN public.cove_authz_entity_matches_attribute(p_kind, p_entity_id, p_scope_value)
                    WHEN 'expression' THEN public.cove_authz_entity_matches_expression(p_kind, p_entity_id, p_scope_value)
                    ELSE false
                END;
            $$;

            CREATE OR REPLACE FUNCTION public.cove_authz_can_access(
                p_bypass boolean,
                p_has_permission boolean,
                p_role_names text[],
                p_kind text,
                p_entity_id integer,
                p_applies_to text
            ) RETURNS boolean
            LANGUAGE sql
            STABLE
            AS $$
                WITH role_ids AS (
                    SELECT r."Id"
                    FROM roles r
                    WHERE r."Name" = ANY(p_role_names)
                ),
                override_denies AS (
                    SELECT 1
                    FROM role_entity_overrides reo
                    WHERE reo."RoleId" IN (SELECT "Id" FROM role_ids)
                      AND lower(reo."EntityKind") = lower(p_kind)
                      AND reo."EntityId" = p_entity_id::text
                      AND lower(reo."Effect") = 'deny'
                      AND lower(reo."AppliesTo") IN ('all', lower(p_applies_to))
                ),
                override_allows AS (
                    SELECT 1
                    FROM role_entity_overrides reo
                    WHERE reo."RoleId" IN (SELECT "Id" FROM role_ids)
                      AND lower(reo."EntityKind") = lower(p_kind)
                      AND reo."EntityId" = p_entity_id::text
                      AND lower(reo."Effect") = 'allow'
                      AND lower(reo."AppliesTo") IN ('all', lower(p_applies_to))
                ),
                matching_rules AS (
                    SELECT lower(rcr."Effect") AS effect
                    FROM role_content_rules rcr
                    WHERE rcr."RoleId" IN (SELECT "Id" FROM role_ids)
                      AND lower(rcr."EntityKind") = lower(p_kind)
                      AND lower(rcr."AppliesTo") IN ('all', lower(p_applies_to))
                      AND public.cove_authz_rule_matches(lower(p_kind), p_entity_id, lower(rcr."ScopeKind"), rcr."ScopeValue")
                )
                SELECT CASE
                    WHEN p_bypass THEN true
                    WHEN NOT p_has_permission THEN false
                    WHEN EXISTS (SELECT 1 FROM override_denies) THEN false
                    WHEN EXISTS (SELECT 1 FROM override_allows) THEN true
                    WHEN EXISTS (SELECT 1 FROM matching_rules WHERE effect = 'deny')
                         AND NOT EXISTS (SELECT 1 FROM matching_rules WHERE effect = 'allow') THEN false
                    ELSE true
                END;
            $$;

            CREATE OR REPLACE FUNCTION public.cove_authz_can_read(
                p_bypass boolean,
                p_has_read_permission boolean,
                p_has_read_grant boolean,
                p_role_names text[],
                p_share_link_id uuid,
                p_kind text,
                p_entity_id integer
            ) RETURNS boolean
            LANGUAGE sql
            STABLE
            AS $$
                WITH matching_share_link AS (
                    SELECT 1
                    FROM share_links sl
                    WHERE sl."Id" = p_share_link_id
                      AND sl."RevokedAt" IS NULL
                      AND (sl."ExpiresAt" IS NULL OR sl."ExpiresAt" >= now())
                      AND lower(sl."EntityKind") = lower(p_kind)
                      AND sl."EntityIds" @> to_jsonb(ARRAY[p_entity_id::text])
                ),
                role_ids AS (
                    SELECT r."Id"
                    FROM roles r
                    WHERE r."Name" = ANY(p_role_names)
                ),
                override_denies AS (
                    SELECT 1
                    FROM role_entity_overrides reo
                    WHERE reo."RoleId" IN (SELECT "Id" FROM role_ids)
                      AND lower(reo."EntityKind") = lower(p_kind)
                      AND reo."EntityId" = p_entity_id::text
                      AND lower(reo."Effect") = 'deny'
                      AND lower(reo."AppliesTo") IN ('all', 'read')
                ),
                override_allows AS (
                    SELECT 1
                    FROM role_entity_overrides reo
                    WHERE reo."RoleId" IN (SELECT "Id" FROM role_ids)
                      AND lower(reo."EntityKind") = lower(p_kind)
                      AND reo."EntityId" = p_entity_id::text
                      AND lower(reo."Effect") = 'allow'
                      AND lower(reo."AppliesTo") IN ('all', 'read')
                ),
                matching_rules AS (
                    SELECT lower(rcr."Effect") AS effect
                    FROM role_content_rules rcr
                    WHERE rcr."RoleId" IN (SELECT "Id" FROM role_ids)
                      AND lower(rcr."EntityKind") = lower(p_kind)
                      AND lower(rcr."AppliesTo") IN ('all', 'read')
                      AND public.cove_authz_rule_matches(lower(p_kind), p_entity_id, lower(rcr."ScopeKind"), rcr."ScopeValue")
                )
                SELECT CASE
                    WHEN p_bypass THEN true
                    WHEN NOT p_has_read_permission AND NOT p_has_read_grant THEN false
                    WHEN p_share_link_id IS NOT NULL AND NOT EXISTS (SELECT 1 FROM matching_share_link) THEN false
                    WHEN EXISTS (SELECT 1 FROM override_denies) THEN false
                    WHEN EXISTS (SELECT 1 FROM override_allows) THEN true
                    WHEN p_has_read_permission THEN CASE
                        WHEN EXISTS (SELECT 1 FROM matching_rules WHERE effect = 'deny')
                             AND NOT EXISTS (SELECT 1 FROM matching_rules WHERE effect = 'allow') THEN false
                        ELSE true
                    END
                    WHEN EXISTS (SELECT 1 FROM matching_rules WHERE effect = 'allow') THEN true
                    WHEN EXISTS (SELECT 1 FROM matching_rules WHERE effect = 'deny')
                         AND NOT EXISTS (SELECT 1 FROM matching_rules WHERE effect = 'allow') THEN false
                    ELSE false
                END;
            $$;
            """;

    public const string DropFunctionsSql = """
            DROP FUNCTION IF EXISTS public.cove_authz_can_read(boolean, boolean, boolean, text[], uuid, text, integer) CASCADE;
            DROP FUNCTION IF EXISTS public.cove_authz_can_access(boolean, boolean, text[], text, integer, text) CASCADE;
            DROP FUNCTION IF EXISTS public.cove_authz_rule_matches(text, integer, text, jsonb) CASCADE;
            DROP FUNCTION IF EXISTS public.cove_authz_entity_matches_expression(text, integer, jsonb) CASCADE;
            DROP FUNCTION IF EXISTS public.cove_authz_expression_rule_matches(text, integer, jsonb) CASCADE;
            DROP FUNCTION IF EXISTS public.cove_authz_entity_matches_attribute(text, integer, jsonb) CASCADE;
            DROP FUNCTION IF EXISTS public.cove_authz_json_scalar_text(jsonb) CASCADE;
            DROP FUNCTION IF EXISTS public.cove_authz_entity_json(text, integer) CASCADE;
            DROP FUNCTION IF EXISTS public.cove_authz_entity_matches_identifier(text, integer, jsonb) CASCADE;
            DROP FUNCTION IF EXISTS public.cove_authz_entity_has_studio(text, integer, integer) CASCADE;
            DROP FUNCTION IF EXISTS public.cove_authz_entity_has_tag(text, integer, integer) CASCADE;
            """;
}
