import type { MediaDetailSectionProps } from "./types";

export function MediaDetailLayoutContent({ children, className }: MediaDetailSectionProps) {
  return <section className={["min-w-0", className].filter(Boolean).join(" ")}>{children}</section>;
}
