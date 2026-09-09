import { useQuery } from "@tanstack/react-query";
import { customFields } from "../api/client";
import type { CustomFieldEntityType } from "../api/types";

export function customFieldDefinitionsQueryKey(entityType?: CustomFieldEntityType) {
  return ["custom-fields", entityType ?? "all"] as const;
}

export function useCustomFieldDefinitions(entityType?: CustomFieldEntityType, enabled = true) {
  return useQuery({
    queryKey: customFieldDefinitionsQueryKey(entityType),
    queryFn: () => customFields.list(entityType),
    enabled,
    staleTime: 60_000,
  });
}
