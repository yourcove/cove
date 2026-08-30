import ReactMarkdown, { defaultUrlTransform } from "react-markdown";
import type { ComponentPropsWithoutRef } from "react";
import { useAuth } from "../auth/AuthContext";

export interface NarrativeTextProps {
  children: string;
  className?: string;
}

export function useMarkdownRenderingEnabled() {
  const { user } = useAuth();
  return user?.uiPreferences?.renderMarkdown === true;
}

function safeLinkHref(href: string | undefined) {
  if (!href) return undefined;
  return defaultUrlTransform(href) || undefined;
}

const COMPONENTS = {
  h1: (props: ComponentPropsWithoutRef<"h1">) => <h1 className="mt-5 text-2xl font-semibold tracking-tight text-foreground first:mt-0" {...props} />,
  h2: (props: ComponentPropsWithoutRef<"h2">) => <h2 className="mt-5 text-xl font-semibold tracking-tight text-foreground first:mt-0" {...props} />,
  h3: (props: ComponentPropsWithoutRef<"h3">) => <h3 className="mt-4 text-lg font-semibold text-foreground first:mt-0" {...props} />,
  h4: (props: ComponentPropsWithoutRef<"h4">) => <h4 className="mt-4 text-base font-semibold text-foreground first:mt-0" {...props} />,
  h5: (props: ComponentPropsWithoutRef<"h5">) => <h5 className="mt-3 text-sm font-semibold text-foreground first:mt-0" {...props} />,
  h6: (props: ComponentPropsWithoutRef<"h6">) => <h6 className="mt-3 text-xs font-semibold uppercase tracking-wide text-muted first:mt-0" {...props} />,
  p: (props: ComponentPropsWithoutRef<"p">) => <p className="mt-3 first:mt-0" {...props} />,
  ul: (props: ComponentPropsWithoutRef<"ul">) => <ul className="mt-3 list-disc space-y-1 pl-5 first:mt-0" {...props} />,
  ol: (props: ComponentPropsWithoutRef<"ol">) => <ol className="mt-3 list-decimal space-y-1 pl-5 first:mt-0" {...props} />,
  blockquote: (props: ComponentPropsWithoutRef<"blockquote">) => <blockquote className="mt-3 border-l-4 border-accent/40 bg-card/60 px-4 py-2 italic first:mt-0" {...props} />,
  code: ({ className, ...props }: ComponentPropsWithoutRef<"code">) => <code className={["rounded bg-card px-1 py-0.5 font-mono text-[0.92em] text-foreground", className].filter(Boolean).join(" ")} {...props} />,
  pre: (props: ComponentPropsWithoutRef<"pre">) => <pre className="mt-3 overflow-x-auto rounded-lg border border-border bg-slate-950/95 p-3 text-slate-100 first:mt-0" {...props} />,
  a: ({ href, children, ...props }: ComponentPropsWithoutRef<"a">) => {
    const safeHref = safeLinkHref(href);
    return safeHref ? <a href={safeHref} target="_blank" rel="noopener noreferrer" className="text-accent underline decoration-accent/40 underline-offset-4 hover:text-accent/80" {...props}>{children}</a> : <span>{children}</span>;
  },
  img: ({ alt }: ComponentPropsWithoutRef<"img">) => alt ? <span>{alt}</span> : null,
  hr: (props: ComponentPropsWithoutRef<"hr">) => <hr className="my-5 border-border" {...props} />,
};

export function NarrativeText({ children, className = "" }: NarrativeTextProps) {
  const enabled = useMarkdownRenderingEnabled();
  const classes = ["break-words", className].filter(Boolean).join(" ");

  if (!enabled) {
    return <div className={["whitespace-pre-wrap", classes].join(" ")}>{children}</div>;
  }

  return (
    <div className={classes}>
      <ReactMarkdown components={COMPONENTS} skipHtml>{children}</ReactMarkdown>
    </div>
  );
}
