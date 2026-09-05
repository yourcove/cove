import type { MediaDetailSectionProps } from "./types";

export function MediaDetailLayoutSidebar({ children, className }: MediaDetailSectionProps) {
  return (
    <aside
      className={[
        "flex w-full min-w-0 flex-col gap-2 rounded-2xl border border-border/80 bg-surface/55 p-2 shadow-sm backdrop-blur lg:sticky lg:top-20 lg:self-start",
        className,
      ]
        .filter(Boolean)
        .join(" ")}
    >
      {children}
    </aside>
  );
}
