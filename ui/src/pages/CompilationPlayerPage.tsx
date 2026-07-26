import { useQuery } from "@tanstack/react-query";
import { useMemo } from "react";
import { groups } from "../api/client";
import { CompilationPlayer } from "../components/CompilationPlayer";
import { DetailSkeleton } from "../components/DetailSkeleton";
import { useBackNavigation } from "../hooks/useBackNavigation";
import { useDocumentTitle } from "../hooks/useDocumentTitle";

interface Props {
  id: number;
  itemOrder?: string[];
  onNavigate: (r: any) => void;
}

export function CompilationPlayerPage({ id, itemOrder, onNavigate }: Props) {
  const { backLabel, goBack } = useBackNavigation({ page: "group", id }, onNavigate);
  const { data: group, isLoading: groupLoading } = useQuery({
    queryKey: ["group", id],
    queryFn: () => groups.get(id),
  });
  const { data: manifest, isLoading: manifestLoading } = useQuery({
    queryKey: ["group", id, "playback-manifest"],
    queryFn: () => groups.items.playbackManifest(id),
  });
  const items = useMemo(
    () => orderManifestItems(manifest?.items ?? [], itemOrder),
    [itemOrder, manifest?.items],
  );

  useDocumentTitle(group?.name);

  if (groupLoading || manifestLoading) {
    return (
      <div className="px-1 py-2">
        <DetailSkeleton />
      </div>
    );
  }

  if (!group || !manifest || items.length === 0) {
    return <div className="py-16 text-center text-secondary">Compilation playback is unavailable for this group yet.</div>;
  }

  return (
    <CompilationPlayer
      groupId={id}
      groupName={group.name}
      items={items}
      onNavigate={onNavigate}
      backLabel={backLabel}
      onGoBack={goBack}
    />
  );
}

export function orderManifestItems<T extends { groupItemId: number; hostType: string; hostId: number }>(items: T[], itemOrder?: string[]) {
  if (itemOrder == null) return items;

  const orderByKey = new Map(itemOrder.map((itemKey, index) => [itemKey, index]));
  return items
    .map((item, index) => ({
      item,
      index,
      order: orderByKey.get(`item:${item.groupItemId}`)
        ?? orderByKey.get(`${item.hostType.toLowerCase()}:${item.hostId}`),
    }))
    .filter(({ order }) => order != null)
    .sort((left, right) => {
      return left.order! - right.order! || left.index - right.index;
    })
    .map(({ item }) => item);
}
