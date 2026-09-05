import DOMPurify from "dompurify";
import ReactMarkdown from "react-markdown";
import { BookOpenText } from "lucide-react";

const MARKDOWN_COMPONENTS = {
  h1: (props: React.ComponentPropsWithoutRef<"h1">) => (
    <h1 className="mt-8 text-3xl font-semibold tracking-tight text-foreground first:mt-0" {...props} />
  ),
  h2: (props: React.ComponentPropsWithoutRef<"h2">) => (
    <h2 className="mt-7 text-2xl font-semibold tracking-tight text-foreground first:mt-0" {...props} />
  ),
  h3: (props: React.ComponentPropsWithoutRef<"h3">) => (
    <h3 className="mt-6 text-xl font-semibold tracking-tight text-foreground first:mt-0" {...props} />
  ),
  p: (props: React.ComponentPropsWithoutRef<"p">) => (
    <p className="mt-4 text-[15px] leading-7 text-foreground/92 first:mt-0" {...props} />
  ),
  ul: (props: React.ComponentPropsWithoutRef<"ul">) => (
    <ul className="mt-4 list-disc space-y-2 pl-6 text-[15px] leading-7 text-foreground/92" {...props} />
  ),
  ol: (props: React.ComponentPropsWithoutRef<"ol">) => (
    <ol className="mt-4 list-decimal space-y-2 pl-6 text-[15px] leading-7 text-foreground/92" {...props} />
  ),
  blockquote: (props: React.ComponentPropsWithoutRef<"blockquote">) => (
    <blockquote
      className="mt-5 border-l-4 border-accent/40 bg-card/70 px-4 py-3 text-[15px] italic text-foreground/85"
      {...props}
    />
  ),
  code: ({ className, children, ...props }: React.ComponentPropsWithoutRef<"code">) => {
    const isBlock = className?.includes("language-");
    if (isBlock) {
      return (
        <code
          className={[
            "block overflow-x-auto rounded-2xl border border-border bg-slate-950/95 p-4 text-sm text-slate-100",
            className,
          ]
            .filter(Boolean)
            .join(" ")}
          {...props}
        >
          {children}
        </code>
      );
    }

    return (
      <code
        className="rounded-md border border-border/80 bg-card px-1.5 py-0.5 text-[0.92em] text-foreground"
        {...props}
      >
        {children}
      </code>
    );
  },
  pre: (props: React.ComponentPropsWithoutRef<"pre">) => <pre className="mt-5 overflow-x-auto" {...props} />,
  a: (props: React.ComponentPropsWithoutRef<"a">) => (
    <a
      className="text-accent underline decoration-accent/40 underline-offset-4 transition hover:text-accent/80"
      target="_blank"
      rel="noreferrer"
      {...props}
    />
  ),
  hr: (props: React.ComponentPropsWithoutRef<"hr">) => <hr className="my-8 border-border" {...props} />,
};

interface PlainTextBlock {
  text: string;
  preserveLineBreaks: boolean;
}

function shouldPreservePlainTextLineBreaks(lines: string[]) {
  const nonEmptyLines = lines.map((line) => line.trim()).filter(Boolean);
  if (nonEmptyLines.length <= 1) {
    return false;
  }

  if (nonEmptyLines.some((line) => /^([-*+]\s|\d+[.)]\s|>\s|#{1,6}\s|\|)/.test(line))) {
    return true;
  }

  const shortLineCount = nonEmptyLines.filter((line) => line.length <= 32).length;
  return nonEmptyLines.length >= 3 && shortLineCount >= Math.ceil(nonEmptyLines.length / 2);
}

function toPlainTextBlocks(content: string): PlainTextBlock[] {
  return content
    .replace(/\r\n?/g, "\n")
    .trim()
    .split(/\n{2,}/)
    .map((block) => {
      const lines = block.split("\n").map((line) => line.replace(/[ \t]+$/g, ""));
      const preserveLineBreaks = shouldPreservePlainTextLineBreaks(lines);
      const text = preserveLineBreaks
        ? lines.join("\n").trim()
        : lines
            .map((line) => line.trim())
            .join(" ")
            .replace(/[ \t]{2,}/g, " ")
            .trim();

      return {
        text,
        preserveLineBreaks,
      };
    })
    .filter((block) => Boolean(block.text));
}

export function TextViewer({
  content,
  renderMode,
  className = "",
}: {
  content?: string;
  renderMode?: "text" | "markdown" | "html";
  className?: string;
}) {
  const plainBlocks =
    renderMode === "markdown" || renderMode === "html" || !content?.trim() ? [] : toPlainTextBlocks(content);
  const sanitizedHtml =
    renderMode === "html" && content?.trim() ? DOMPurify.sanitize(content, { USE_PROFILES: { html: true } }) : "";

  return (
    <div
      data-testid="text-viewer"
      className={[
        "flex h-full min-h-[24rem] flex-col overflow-hidden rounded-[2rem] border border-border bg-surface text-foreground shadow-[0_24px_64px_rgba(15,23,42,0.08)]",
        className,
      ]
        .filter(Boolean)
        .join(" ")}
    >
      <div className="border-b border-border bg-card/70 px-4 py-3 backdrop-blur-sm sm:px-6">
        <div className="inline-flex items-center gap-2 rounded-full border border-border bg-card px-3 py-1 text-xs font-medium uppercase tracking-[0.22em] text-secondary">
          <BookOpenText className="h-3.5 w-3.5" />
          Reader
        </div>
      </div>
      <div className="min-h-0 flex-1 overflow-y-auto p-4 sm:p-5 lg:p-6">
        {!content?.trim() ? (
          <div className="flex items-center gap-3 rounded-2xl border border-dashed border-border bg-card/80 px-5 py-6 text-sm text-muted">
            <BookOpenText className="h-5 w-5 shrink-0" />
            No readable text content is available for this document yet.
          </div>
        ) : renderMode === "html" ? (
          <article className="min-h-full w-full max-w-none rounded-2xl border border-border bg-card/70 px-5 py-6 shadow-[0_18px_42px_rgba(15,23,42,0.05)] sm:px-7 sm:py-7 lg:px-8">
            <div
              className="[&_a]:text-accent [&_a]:underline [&_a]:decoration-accent/40 [&_a]:underline-offset-4 [&_blockquote]:my-5 [&_blockquote]:border-l-4 [&_blockquote]:border-accent/40 [&_blockquote]:bg-card [&_blockquote]:px-4 [&_blockquote]:py-3 [&_blockquote]:italic [&_h1]:mt-8 [&_h1]:text-3xl [&_h1]:font-semibold [&_h1]:tracking-tight [&_h1:first-child]:mt-0 [&_h2]:mt-7 [&_h2]:text-2xl [&_h2]:font-semibold [&_h2]:tracking-tight [&_h2:first-child]:mt-0 [&_h3]:mt-6 [&_h3]:text-xl [&_h3]:font-semibold [&_h3]:tracking-tight [&_h3:first-child]:mt-0 [&_hr]:my-8 [&_hr]:border-border [&_img]:mx-auto [&_img]:my-6 [&_img]:max-w-full [&_li]:mt-2 [&_ol]:mt-4 [&_ol]:list-decimal [&_ol]:space-y-2 [&_ol]:pl-6 [&_p]:mt-4 [&_p]:text-[15px] [&_p]:leading-7 [&_p:first-child]:mt-0 [&_pre]:mt-5 [&_pre]:overflow-x-auto [&_pre]:rounded-2xl [&_pre]:border [&_pre]:border-border [&_pre]:bg-slate-950/95 [&_pre]:p-4 [&_pre]:text-sm [&_pre]:text-slate-100 [&_table]:mt-5 [&_table]:w-full [&_table]:border-collapse [&_td]:border [&_td]:border-border [&_td]:px-3 [&_td]:py-2 [&_th]:border [&_th]:border-border [&_th]:bg-card [&_th]:px-3 [&_th]:py-2 [&_ul]:mt-4 [&_ul]:list-disc [&_ul]:space-y-2 [&_ul]:pl-6 break-words text-[15px] leading-7 text-foreground/92"
              data-testid="text-viewer-content"
              dangerouslySetInnerHTML={{ __html: sanitizedHtml }}
            />
          </article>
        ) : renderMode === "markdown" ? (
          <article className="min-h-full w-full max-w-none rounded-2xl border border-border bg-card/70 px-5 py-6 shadow-[0_18px_42px_rgba(15,23,42,0.05)] sm:px-7 sm:py-7 lg:px-8">
            <ReactMarkdown components={MARKDOWN_COMPONENTS}>{content}</ReactMarkdown>
          </article>
        ) : (
          <article className="min-h-full w-full max-w-none rounded-2xl border border-border bg-card/70 px-5 py-6 shadow-[0_18px_42px_rgba(15,23,42,0.05)] sm:px-7 sm:py-7 lg:px-8">
            <div className="space-y-4 text-[15px] leading-7 text-foreground/92" data-testid="text-viewer-content">
              {plainBlocks.map((block, index) => (
                <p
                  key={`${index}-${block.text.slice(0, 24)}`}
                  className={block.preserveLineBreaks ? "whitespace-pre-wrap break-words" : "break-words"}
                >
                  {block.text}
                </p>
              ))}
            </div>
          </article>
        )}
      </div>
    </div>
  );
}
