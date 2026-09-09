import type { ReactNode } from "react";
import type { MetadataPanelItem } from "./MediaDetailLayout/types";

interface MetadataPanelProps {
  title?: ReactNode;
  items?: MetadataPanelItem[];
  children?: ReactNode;
  className?: string;
}

export function MetadataPanel({ title, items = [], children, className }: MetadataPanelProps) {
  return (
    <section className={["rounded-2xl border border-border bg-card/70 p-4", className].filter(Boolean).join(" ")}>
      {title ? <h3 className="mb-3 text-xs font-semibold uppercase tracking-[0.16em] text-muted">{title}</h3> : null}

      {items.length > 0 ? (
        <dl className="grid gap-3 sm:grid-cols-2">
          {items.map((item) => (
            <div key={item.label} className="flex flex-col gap-1">
              <dt className="text-xs uppercase tracking-wide text-muted">{item.label}</dt>
              <dd className="text-sm text-foreground">{item.value}</dd>
            </div>
          ))}
        </dl>
      ) : null}

      {children ? <div className={items.length > 0 ? "mt-4" : undefined}>{children}</div> : null}
    </section>
  );
}
