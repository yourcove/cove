import { useMemo, useState } from "react";
import { useQuery } from "@tanstack/react-query";
import { ChevronDown, ChevronRight } from "lucide-react";
import { metadata, type LibraryFolder } from "../api/client";

export function LibraryFolderTree({
  roots,
  selected,
  onToggle,
  selectionMode = "multiple",
  probeChildren = true,
  emptyHint,
}: {
  roots: LibraryFolder[];
  selected: string[];
  onToggle: (path: string, checked: boolean) => void;
  selectionMode?: "single" | "multiple";
  probeChildren?: boolean;
  emptyHint: string;
}) {
  const selectedSet = useMemo(() => new Set(selected), [selected]);
  if (roots.length === 0) {
    return <p className="text-[11px] text-muted">{emptyHint}</p>;
  }

  return (
    <div className="max-h-72 space-y-0.5 overflow-auto rounded-lg border border-border/60 bg-surface/40 p-1.5">
      {roots.map((root) => (
        <LibraryFolderNode
          key={root.path}
          folder={root}
          depth={0}
          selectedSet={selectedSet}
          onToggle={onToggle}
          selectionMode={selectionMode}
          probeChildren={probeChildren}
        />
      ))}
    </div>
  );
}

function LibraryFolderNode({
  folder,
  depth,
  selectedSet,
  onToggle,
  selectionMode,
  probeChildren,
}: {
  folder: LibraryFolder;
  depth: number;
  selectedSet: Set<string>;
  onToggle: (path: string, checked: boolean) => void;
  selectionMode: "single" | "multiple";
  probeChildren: boolean;
}) {
  const [expanded, setExpanded] = useState(false);
  const {
    data: children,
    isLoading,
    isFetching,
    isError,
  } = useQuery({
    queryKey: ["library-folders", folder.path, probeChildren],
    queryFn: () => metadata.libraryFolders(folder.path, probeChildren),
    enabled: expanded && folder.hasChildren,
  });
  const indent = depth * 16 + 4;

  return (
    <div>
      <div
        className="flex items-center gap-1.5 rounded px-1 py-0.5 hover:bg-surface/70"
        style={{ paddingLeft: indent }}
      >
        {folder.hasChildren ? (
          <button
            type="button"
            onClick={() => setExpanded((current) => !current)}
            className="text-muted hover:text-foreground"
            aria-label={`${expanded ? "Collapse" : "Expand"} folder ${folder.name}`}
          >
            {expanded ? <ChevronDown className="h-3.5 w-3.5" /> : <ChevronRight className="h-3.5 w-3.5" />}
          </button>
        ) : (
          <span className="inline-block w-3.5" />
        )}
        <label className="flex min-w-0 cursor-pointer items-center gap-1.5">
          <input
            type={selectionMode === "single" ? "radio" : "checkbox"}
            name={selectionMode === "single" ? "library-folder" : undefined}
            checked={selectedSet.has(folder.path)}
            onChange={(event) => onToggle(folder.path, event.target.checked)}
            className="border-border"
          />
          <span className="truncate text-xs text-foreground" title={folder.path}>
            {folder.name}
          </span>
        </label>
      </div>
      {expanded && folder.hasChildren ? (
        <div>
          {isLoading || (isFetching && isError) ? (
            <p className="text-[11px] text-muted" style={{ paddingLeft: indent + 38 }}>
              Loading…
            </p>
          ) : isError ? (
            <p className="text-[11px] text-red-300" style={{ paddingLeft: indent + 38 }}>
              Unable to list subfolders
            </p>
          ) : (children ?? []).length === 0 ? (
            <p className="text-[11px] text-muted" style={{ paddingLeft: indent + 38 }}>
              No subfolders
            </p>
          ) : (
            (children ?? []).map((child) => (
              <LibraryFolderNode
                key={child.path}
                folder={child}
                depth={depth + 1}
                selectedSet={selectedSet}
                onToggle={onToggle}
                selectionMode={selectionMode}
                probeChildren={probeChildren}
              />
            ))
          )}
        </div>
      ) : null}
    </div>
  );
}
