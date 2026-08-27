using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Cove.Data.Migrations;

[DbContext(typeof(CoveContext))]
[Migration("20260826150000_AddCustomFieldJsonPointerFunctions")]
public sealed class AddCustomFieldJsonPointerFunctions : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            CREATE FUNCTION public.cove_json_pointer_value(document jsonb, pointer text)
            RETURNS jsonb
            LANGUAGE plpgsql
            IMMUTABLE
            STRICT
            PARALLEL SAFE
            AS $function$
            DECLARE
                current_value jsonb := document;
                raw_tokens text[];
                raw_token text;
                token text;
                array_index numeric;
            BEGIN
                IF char_length(pointer) = 0
                   OR char_length(pointer) > 500
                   OR left(pointer, 1) <> '/' THEN
                    RETURN NULL;
                END IF;

                -- regexp_split_to_array preserves the single empty token for pointer '/', while
                -- string_to_array('', '/') returns an empty array and would incorrectly select the root.
                raw_tokens := regexp_split_to_array(substr(pointer, 2), '/');
                IF cardinality(raw_tokens) > 32 THEN
                    RETURN NULL;
                END IF;

                FOREACH raw_token IN ARRAY raw_tokens LOOP
                    IF raw_token ~ '~([^01]|$)' THEN
                        RETURN NULL;
                    END IF;

                    token := replace(replace(raw_token, '~1', '/'), '~0', '~');

                    CASE jsonb_typeof(current_value)
                        WHEN 'object' THEN
                            current_value := current_value -> token;
                        WHEN 'array' THEN
                            IF token !~ '^(0|[1-9][0-9]*)$' THEN
                                RETURN NULL;
                            END IF;

                            array_index := token::numeric;
                            IF array_index >= jsonb_array_length(current_value) THEN
                                RETURN NULL;
                            END IF;
                            current_value := current_value -> array_index::integer;
                        ELSE
                            RETURN NULL;
                    END CASE;

                    IF current_value IS NULL THEN
                        RETURN NULL;
                    END IF;
                END LOOP;

                RETURN current_value;
            END
            $function$;

            CREATE FUNCTION public.cove_json_pointer_text(document jsonb, pointer text)
            RETURNS text
            LANGUAGE plpgsql
            IMMUTABLE
            STRICT
            PARALLEL SAFE
            AS $function$
            DECLARE
                scalar jsonb;
            BEGIN
                scalar := public.cove_json_pointer_value(document, pointer);
                IF jsonb_typeof(scalar) <> 'string' THEN
                    RETURN NULL;
                END IF;
                RETURN scalar #>> '{}';
            END
            $function$;

            CREATE FUNCTION public.cove_json_pointer_number(document jsonb, pointer text)
            RETURNS numeric
            LANGUAGE plpgsql
            IMMUTABLE
            STRICT
            PARALLEL SAFE
            AS $function$
            DECLARE
                scalar jsonb;
                numeric_value numeric;
                value_scale integer;
            BEGIN
                scalar := public.cove_json_pointer_value(document, pointer);
                IF jsonb_typeof(scalar) <> 'number' THEN
                    RETURN NULL;
                END IF;

                -- JSONB supports numeric values much larger than the CLR decimal contract used by
                -- custom-field criteria. Restrict the shared query/index function to values that fit
                -- that contract so an otherwise valid JSON document cannot overflow a B-tree entry.
                numeric_value := (scalar #>> '{}')::numeric;
                value_scale := min_scale(numeric_value);
                IF value_scale > 28
                   OR abs(numeric_value) * power(10::numeric, value_scale)
                        > 79228162514264337593543950335::numeric THEN
                    RETURN NULL;
                END IF;

                RETURN numeric_value;
            END
            $function$;

            CREATE FUNCTION public.cove_json_pointer_boolean(document jsonb, pointer text)
            RETURNS boolean
            LANGUAGE plpgsql
            IMMUTABLE
            STRICT
            PARALLEL SAFE
            AS $function$
            DECLARE
                scalar jsonb;
            BEGIN
                scalar := public.cove_json_pointer_value(document, pointer);
                IF jsonb_typeof(scalar) <> 'boolean' THEN
                    RETURN NULL;
                END IF;
                RETURN (scalar #>> '{}')::boolean;
            END
            $function$;

            CREATE FUNCTION public.cove_json_pointer_text_index_key(document jsonb, pointer text)
            RETURNS bytea
            LANGUAGE sql
            IMMUTABLE
            STRICT
            PARALLEL SAFE
            -- The explicit target encoding makes this deterministic within every database even
            -- though PostgreSQL conservatively catalogs convert_to itself as STABLE.
            RETURN substring(convert_to(public.cove_json_pointer_text(document, pointer), 'UTF8') FROM 1 FOR 1024);
            """);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            DROP FUNCTION IF EXISTS public.cove_json_pointer_text_index_key(jsonb, text) CASCADE;
            DROP FUNCTION IF EXISTS public.cove_json_pointer_boolean(jsonb, text) CASCADE;
            DROP FUNCTION IF EXISTS public.cove_json_pointer_number(jsonb, text) CASCADE;
            DROP FUNCTION IF EXISTS public.cove_json_pointer_text(jsonb, text) CASCADE;
            DROP FUNCTION IF EXISTS public.cove_json_pointer_value(jsonb, text);
            """);
    }
}
