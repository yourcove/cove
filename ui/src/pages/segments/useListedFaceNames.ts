import { useQueries, useQueryClient } from "@tanstack/react-query";
import { faces } from "../../api/client";
import type { Face, PaginatedResponse } from "../../api/types";
import { faceShownName } from "../../utils/faceDisplay";

export interface ListedFaceName {
  name: string;
  /** The name is the face's top suggested performer, not one it has been given. */
  suggested: boolean;
}

// Faces are looked up in batches of this size, in the order the page lists them, so the batches already
// loaded keep their query keys as infinite scroll appends more.
const BATCH_SIZE = 48;
const QUERY_KEY = ["faces", "by-ids"] as const;
const NO_NAMES: ReadonlyMap<number, ListedFaceName> = new Map();

// Looks up the current name of each listed face that the segment data names only by its placeholder: the name
// it has been given since, else its top suggested performer, as the faces list and the face page show it.
export function useListedFaceNames(faceIds: number[], enabled: boolean): ReadonlyMap<number, ListedFaceName> {
  const queryClient = useQueryClient();
  const ids = Array.from(new Set(faceIds));
  const batches: number[][] = [];
  for (let index = 0; index < ids.length; index += BATCH_SIZE) batches.push(ids.slice(index, index + BATCH_SIZE));

  const queries = useQueries({
    queries: batches.map((batch) => ({
      queryKey: [...QUERY_KEY, batch],
      queryFn: () => faces.findByIds(batch),
      enabled,
      staleTime: 60_000,
    })),
  });
  // Without access to faces, names cached earlier are not shown either.
  if (!enabled) return NO_NAMES;

  // The last batch takes a new key whenever more faces are listed, so while it loads, its faces are read from
  // the batches loaded before rather than falling back to their placeholders.
  let cachedFaces: Map<number, Face> | undefined;
  const readCachedFace = (id: number) => {
    cachedFaces ??= new Map(
      queryClient
        .getQueriesData<PaginatedResponse<Face>>({ queryKey: QUERY_KEY })
        .flatMap(([, data]) => data?.items ?? [])
        .map((face) => [face.id, face] as const),
    );
    return cachedFaces.get(id);
  };

  const names = new Map<number, ListedFaceName>();
  batches.forEach((batch, index) => {
    const query = queries[index];
    // A batch whose lookup failed shows its placeholders rather than names nothing will refresh.
    const items = query?.data?.items ?? (query?.isError ? [] : batch.flatMap((id) => readCachedFace(id) ?? []));
    for (const face of items) {
      const name = faceShownName(face);
      if (name) names.set(face.id, name);
    }
  });
  return names;
}
