import { useMemo } from "react";
import { useQuery } from "@tanstack/react-query";
import { extensions } from "../api/client";

/**
 * The installed extensions' names by ID. The manifest identifies an extension's User Guide topics only by
 * extension ID, so the User Guide loads the names when `enabled` is set, which is when it is open on a Cove
 * with extension topics.
 */
export function useExtensionNames(enabled: boolean): ReadonlyMap<string, string> {
  const { data } = useQuery({
    queryKey: ["extensions-list"],
    queryFn: () => extensions.list(),
    enabled,
  });
  return useMemo(() => new Map((data ?? []).map((extension) => [extension.id, extension.name])), [data]);
}
