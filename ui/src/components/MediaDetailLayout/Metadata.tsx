import type { MediaDetailSectionProps } from "./types";

export function MediaDetailLayoutMetadata({ children, className }: MediaDetailSectionProps) {
  return (
    <aside
      className={["flex w-full flex-col gap-4 rounded-3xl border border-border bg-card/70 p-4 shadow-sm", className]
        .filter(Boolean)
        .join(" ")}
    >
      {children}
    </aside>
  );
}
