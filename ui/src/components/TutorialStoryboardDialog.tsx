import { useEffect, useMemo, useRef, useState } from "react";
import {
  BookOpen,
  Check,
  ChevronLeft,
  ChevronRight,
  Database,
  ExternalLink,
  FolderOpen,
  HelpCircle,
  ImageIcon,
  LayoutGrid,
  Play,
  RefreshCw,
  Search,
  Settings,
  Tag,
  X,
} from "lucide-react";
import ReactMarkdown from "react-markdown";
import type { ExtensionTutorialTopic } from "../api/types";
import { normalizeManualContext, uniqueManualContexts, type TutorialOpenRequest } from "./ManualContext";

const sharedFeatureGuideImages = import.meta.glob("../../../docs/feature-guides/assets/**/*", {
  eager: true,
  import: "default",
  query: "?url",
}) as Record<string, string>;

function resolveSharedFeatureGuideImage(source?: string) {
  if (!source) return undefined;
  if (source.startsWith("/") || /^(?:https?:|data:)/.test(source)) return source;
  return sharedFeatureGuideImages[`../../../docs/feature-guides/${source}`];
}

export const TUTORIAL_STORYBOARD_STORAGE_KEY = "cove-tutorial-storyboard-complete";
export const TUTORIAL_STORYBOARD_EVENT = "cove:tutorial-storyboard-open";

export type TutorialSlideMockKind =
  "tasks" | "feed" | "metadata" | "settings" | "videoPlayer" | "tagging" | "images" | "extension";
type ManualBoxTone = "green" | "blue" | "purple" | "orange" | "pink" | "teal";
type ManualBoxPointContent = {
  tone?: ManualBoxTone;
  text: string;
};

export type { TutorialOpenRequest } from "./ManualContext";

export interface TutorialStoryboardSlide {
  id: string;
  title: string;
  caption?: string;
  bodyMarkdown?: string;
  imageSrc?: string;
  imageAlt?: string;
  mockKind?: TutorialSlideMockKind;
  points?: string[];
  links?: { label: string; url: string }[];
  topicLinks?: { label: string; topicId: string; slideId?: string }[];
  guideArticle?: SharedFeatureGuideArticle;
}

type SharedFeatureGuideContentBlock =
  | { type: "paragraph" | "note"; text: string }
  | { type: "steps"; items: string[] }
  | { type: "heading"; level: 3 | 4; text: string }
  | { type: "list"; items: string[] }
  | {
      type: "links";
      items: { label: string; href: string; appHref?: string; topicId?: string; slideId?: string }[];
    }
  | { type: "image"; src: string; alt: string; caption?: string }
  | { type: "table"; caption?: string; columns: string[]; rows: string[][] }
  | { type: "code"; language?: string; lines: string[] };

type SharedFeatureGuideBlock =
  | SharedFeatureGuideContentBlock
  | {
      type: "recipes";
      items: {
        id: string;
        title: string;
        description?: string;
        blocks: SharedFeatureGuideContentBlock[];
      }[];
    };

interface SharedFeatureGuideArticle {
  description: string;
  sections: { id: string; title: string; blocks: SharedFeatureGuideBlock[] }[];
}

interface SharedFeatureGuideSource extends SharedFeatureGuideArticle {
  schemaVersion: 1;
  id: string;
  title: string;
  order: number;
  pages?: string[];
  contexts?: string[];
  parentTopicId?: string;
}

function resolveSharedFeatureGuideBlockImages(block: SharedFeatureGuideContentBlock): SharedFeatureGuideContentBlock;
function resolveSharedFeatureGuideBlockImages(block: SharedFeatureGuideBlock): SharedFeatureGuideBlock;
function resolveSharedFeatureGuideBlockImages(block: SharedFeatureGuideBlock): SharedFeatureGuideBlock {
  if (block.type === "image") {
    return { ...block, src: resolveSharedFeatureGuideImage(block.src) ?? block.src };
  }
  if (block.type === "recipes") {
    return {
      ...block,
      items: block.items.map((recipe) => ({
        ...recipe,
        blocks: recipe.blocks.map((recipeBlock) => resolveSharedFeatureGuideBlockImages(recipeBlock)),
      })),
    };
  }
  return block;
}

const sharedFeatureGuideModules = import.meta.glob("../../../docs/feature-guides/*.json", {
  eager: true,
  import: "default",
}) as Record<string, unknown>;

const sharedFeatureGuideTopics = Object.values(sharedFeatureGuideModules)
  .filter((guide): guide is SharedFeatureGuideSource => {
    if (!guide || typeof guide !== "object") return false;
    const candidate = guide as Partial<SharedFeatureGuideSource>;
    return candidate.schemaVersion === 1 && typeof candidate.id === "string" && Array.isArray(candidate.sections);
  })
  .map<TutorialStoryboardTopic>((guide) => ({
    id: guide.id,
    title: guide.title,
    description: guide.description,
    pages: guide.pages,
    contexts: guide.contexts,
    parentTopicId: guide.parentTopicId,
    order: guide.order,
    slides: [
      {
        id: "guide",
        title: guide.title,
        guideArticle: {
          description: guide.description,
          sections: guide.sections.map((section) => ({
            ...section,
            blocks: section.blocks.map(resolveSharedFeatureGuideBlockImages),
          })),
        },
      },
    ],
  }));

export interface TutorialStoryboardTopic {
  id: string;
  title: string;
  description?: string;
  pages?: string[];
  contexts?: string[];
  extensionId?: string;
  parentTopicId?: string;
  /** When "setup", this topic is an extension's setup guide (surfaced after install). */
  kind?: string;
  order: number;
  slides: TutorialStoryboardSlide[];
}

interface TutorialTopicEntry {
  topic: TutorialStoryboardTopic;
  depth: number;
}

export const builtinTutorialTopics: TutorialStoryboardTopic[] = sharedFeatureGuideTopics;

export function hasCompletedTutorialStoryboard() {
  return localStorage.getItem(TUTORIAL_STORYBOARD_STORAGE_KEY) === "true";
}

export function openTutorialStoryboard(request?: TutorialOpenRequest | string) {
  const detail = typeof request === "string" ? { topicId: request } : request;
  window.dispatchEvent(new CustomEvent<TutorialOpenRequest | undefined>(TUTORIAL_STORYBOARD_EVENT, { detail }));
}

interface Props {
  open: boolean;
  onClose: () => void;
  request?: TutorialOpenRequest;
  currentPage?: string;
  extensionTopics?: ExtensionTutorialTopic[];
  onTopicChange?: (topicId: string, slideId?: string) => void;
  onAppNavigate?: (href: string) => void;
}

export function TutorialStoryboardDialog({
  open,
  onClose,
  request,
  currentPage,
  extensionTopics = [],
  onTopicChange,
  onAppNavigate,
}: Props) {
  const topics = useMemo(() => mergeTutorialTopics(extensionTopics), [extensionTopics]);
  const availableTopicIds = useMemo(() => new Set(topics.map((topic) => topic.id)), [topics]);
  const topicEntries = useMemo(() => buildTopicEntries(topics), [topics]);
  const orderedTopicIds = useMemo(() => topicEntries.map((entry) => entry.topic.id), [topicEntries]);
  const parentByChild = useMemo(() => {
    const map = new Map<string, string>();
    for (const topic of topics) {
      if (topic.parentTopicId && topics.some((candidate) => candidate.id === topic.parentTopicId)) {
        map.set(topic.id, topic.parentTopicId);
      }
    }
    return map;
  }, [topics]);
  const parentIdsWithChildren = useMemo(() => new Set(parentByChild.values()), [parentByChild]);

  const [selectedTopicId, setSelectedTopicId] = useState(() => pickInitialTopicId(topics, request, currentPage));
  const [index, setIndex] = useState(0);
  const [search, setSearch] = useState("");
  const [mobileTopicSearchOpen, setMobileTopicSearchOpen] = useState(false);
  const mobileTopicSearchRef = useRef<HTMLDivElement>(null);
  const articleScrollRef = useRef<HTMLDivElement>(null);
  // Nested topics start collapsed. Selecting a child opens its ancestors, while the
  // user can still collapse that branch without changing the selected article.
  const [expandedTopicIds, setExpandedTopicIds] = useState<Set<string>>(() => new Set());

  const selectedTopic = topics.find((topic) => topic.id === selectedTopicId) ?? topics[0];
  const slide = selectedTopic.slides[index] ?? selectedTopic.slides[0];
  const isLast = index === selectedTopic.slides.length - 1;
  const progressLabel = `${index + 1} of ${selectedTopic.slides.length}`;
  const currentOrderIndex = orderedTopicIds.indexOf(selectedTopic.id);
  const isVeryFirst = currentOrderIndex <= 0 && index === 0;
  const isVeryLast = currentOrderIndex === orderedTopicIds.length - 1 && isLast;

  const isTopicOpen = (topicId: string) => expandedTopicIds.has(topicId);
  const trimmedSearch = search.trim();
  const visibleEntries = trimmedSearch
    ? topicEntries.filter(({ topic }) => matchesTopicSearch(topic, trimmedSearch))
    : topicEntries.filter(({ topic }) =>
        ancestorsOf(topic.id, parentByChild).every((ancestorId) => isTopicOpen(ancestorId)),
      );
  const mobileVisibleEntries = trimmedSearch
    ? topicEntries.filter(({ topic }) => matchesTopicSearch(topic, trimmedSearch))
    : topicEntries;

  useEffect(() => {
    if (!open) return;
    const previousOverflow = document.body.style.overflow;
    document.body.style.overflow = "hidden";
    return () => {
      document.body.style.overflow = previousOverflow;
    };
  }, [open]);

  useEffect(() => {
    if (open && articleScrollRef.current) {
      articleScrollRef.current.scrollTop = 0;
    }
  }, [index, open, selectedTopicId]);

  useEffect(() => {
    if (!open) return;
    const topicIdsToOpen = ancestorsOf(selectedTopic.id, parentByChild);
    if (parentIdsWithChildren.has(selectedTopic.id)) topicIdsToOpen.push(selectedTopic.id);
    if (topicIdsToOpen.length === 0) return;
    setExpandedTopicIds((current) => {
      if (topicIdsToOpen.every((topicId) => current.has(topicId))) return current;
      const next = new Set(current);
      topicIdsToOpen.forEach((topicId) => next.add(topicId));
      return next;
    });
  }, [open, parentByChild, parentIdsWithChildren, selectedTopic.id]);

  useEffect(() => {
    if (!open) return;
    const nextTopicId = pickInitialTopicId(topics, request, currentPage);
    const nextTopic = topics.find((topic) => topic.id === nextTopicId) ?? topics[0];
    const nextSlideIndex = request?.slideId
      ? Math.max(
          0,
          nextTopic.slides.findIndex((item) => item.id === request.slideId),
        )
      : 0;
    setSelectedTopicId(nextTopic.id);
    setIndex(nextSlideIndex);
    setSearch("");
    setMobileTopicSearchOpen(false);
  }, [currentPage, open, request, topics]);

  useEffect(() => {
    if (!open) return;
    const handleKeyDown = (event: KeyboardEvent) => {
      const target = event.target as HTMLElement | null;
      if (target && (target.tagName === "INPUT" || target.tagName === "TEXTAREA" || target.tagName === "SELECT"))
        return;
      if (event.key === "Escape") {
        markCompleteAndClose();
      } else if (event.key === "ArrowRight") {
        goToNext();
      } else if (event.key === "ArrowLeft") {
        goToPrevious();
      }
    };

    window.addEventListener("keydown", handleKeyDown);
    return () => window.removeEventListener("keydown", handleKeyDown);
  }, [open, selectedTopic.id, index, isLast, currentOrderIndex, orderedTopicIds]);

  useEffect(() => {
    if (!mobileTopicSearchOpen) return;
    const handlePointerDown = (event: PointerEvent) => {
      if (event.target instanceof Node && !mobileTopicSearchRef.current?.contains(event.target)) {
        setMobileTopicSearchOpen(false);
      }
    };

    document.addEventListener("pointerdown", handlePointerDown);
    return () => document.removeEventListener("pointerdown", handlePointerDown);
  }, [mobileTopicSearchOpen]);

  if (!open || !selectedTopic || !slide) return null;

  function markCompleteAndClose() {
    localStorage.setItem(TUTORIAL_STORYBOARD_STORAGE_KEY, "true");
    onClose();
  }

  function chooseTopic(topicId: string, slideId?: string) {
    const target = topics.find((topic) => topic.id === topicId);
    const slideIndex =
      slideId && target
        ? Math.max(
            0,
            target.slides.findIndex((item) => item.id === slideId),
          )
        : 0;
    setSelectedTopicId(topicId);
    setIndex(slideIndex);
    if (parentIdsWithChildren.has(topicId)) {
      setExpandedTopicIds((current) => (current.has(topicId) ? current : new Set(current).add(topicId)));
    }
    onTopicChange?.(topicId, slideId);
  }

  function toggleTopicCollapse(topicId: string) {
    setExpandedTopicIds((current) => {
      const next = new Set(current);
      if (next.has(topicId)) next.delete(topicId);
      else next.add(topicId);
      return next;
    });
  }

  function goToNext() {
    if (!isLast) {
      setIndex((current) => Math.min(selectedTopic.slides.length - 1, current + 1));
      return;
    }
    const nextTopicId = orderedTopicIds[currentOrderIndex + 1];
    if (!nextTopicId) {
      markCompleteAndClose();
      return;
    }
    chooseTopic(nextTopicId);
  }

  function goToPrevious() {
    if (index > 0) {
      setIndex((current) => Math.max(0, current - 1));
      return;
    }
    const previousTopicId = orderedTopicIds[currentOrderIndex - 1];
    if (!previousTopicId) return;
    const previousTopic = topics.find((topic) => topic.id === previousTopicId);
    const lastSlideId = previousTopic?.slides[previousTopic.slides.length - 1]?.id;
    chooseTopic(previousTopicId, lastSlideId);
  }

  return (
    <div
      className="fixed inset-0 z-[80] flex items-center justify-center bg-black/70 px-3 py-4"
      role="dialog"
      aria-modal="true"
      aria-labelledby="tutorial-storyboard-title"
    >
      <div className="flex h-[90vh] max-h-[92vh] w-[96vw] max-w-[96rem] flex-col overflow-hidden rounded-xl border border-border bg-background shadow-2xl">
        <div className="flex items-center justify-between gap-3 border-b border-border px-4 py-3">
          <div className="flex min-w-0 items-center gap-3">
            <div className="flex h-9 w-9 items-center justify-center rounded-lg bg-accent/15 text-accent">
              <BookOpen className="h-5 w-5" />
            </div>
            <div className="min-w-0">
              <div className="text-xs font-semibold uppercase tracking-wide text-muted">Cove User Guide</div>
              <h2 id="tutorial-storyboard-title" className="truncate text-base font-semibold text-foreground">
                {selectedTopic.title}
              </h2>
            </div>
          </div>
          <button
            type="button"
            onClick={markCompleteAndClose}
            className="rounded p-2 text-muted transition-colors hover:bg-surface hover:text-foreground"
            title="Close User Guide"
          >
            <X className="h-5 w-5" />
          </button>
        </div>

        <div className="grid min-h-0 flex-1 overflow-hidden lg:grid-cols-[18rem_minmax(0,1.45fr)_minmax(18rem,0.55fr)] xl:grid-cols-[22rem_minmax(0,1.45fr)_minmax(18rem,0.55fr)]">
          <aside className="hidden min-h-0 flex-col border-r border-border bg-nav/40 p-3 lg:flex">
            <div className="mb-2 px-2 text-xs font-semibold uppercase tracking-wide text-muted">Topics</div>
            <div className="relative mb-2">
              <Search className="pointer-events-none absolute left-2.5 top-1/2 h-4 w-4 -translate-y-1/2 text-muted" />
              <input
                type="search"
                value={search}
                onChange={(event) => setSearch(event.target.value)}
                placeholder="Search topics"
                aria-label="Search User Guide topics"
                className="w-full rounded-lg border border-border bg-input py-1.5 pl-8 pr-2 text-sm text-foreground placeholder:text-muted focus:border-accent focus:outline-none"
              />
            </div>
            <div className="space-y-1 overflow-y-auto pr-1">
              {visibleEntries.length === 0 ? (
                <div className="px-3 py-2 text-sm text-muted">No topics match “{trimmedSearch}”.</div>
              ) : (
                visibleEntries.map(({ topic, depth }) => {
                  const hasChildren = parentIdsWithChildren.has(topic.id);
                  const expanded = isTopicOpen(topic.id);
                  const showToggle = hasChildren && !trimmedSearch;
                  return (
                    <div
                      key={topic.id}
                      className="flex items-stretch gap-1"
                      style={{ paddingLeft: `${depth * 1.1}rem` }}
                    >
                      {showToggle ? (
                        <button
                          type="button"
                          onClick={() => toggleTopicCollapse(topic.id)}
                          className="flex w-6 shrink-0 items-center justify-center rounded-md text-muted transition-colors hover:bg-card hover:text-foreground"
                          aria-label={expanded ? `Collapse ${topic.title}` : `Expand ${topic.title}`}
                          aria-expanded={expanded}
                        >
                          <ChevronRight className={`h-4 w-4 transition-transform ${expanded ? "rotate-90" : ""}`} />
                        </button>
                      ) : (
                        <span className="w-6 shrink-0" aria-hidden="true" />
                      )}
                      <button
                        type="button"
                        onClick={() => chooseTopic(topic.id)}
                        data-topic-depth={depth}
                        className={`min-w-0 flex-1 rounded-lg px-3 py-2 text-left transition-colors ${topic.id === selectedTopic.id ? "bg-accent/15 text-accent" : "text-secondary hover:bg-card hover:text-foreground"}`}
                      >
                        <span className="block whitespace-normal break-words text-sm font-medium leading-5">
                          {topic.title}
                        </span>
                        {topic.description ? (
                          <span className="mt-0.5 line-clamp-2 block text-xs text-muted">{topic.description}</span>
                        ) : null}
                        {topic.extensionId ? (
                          <span className="mt-1 block text-[11px] text-muted">Extension</span>
                        ) : null}
                      </button>
                    </div>
                  );
                })
              )}
            </div>
          </aside>

          <div
            ref={articleScrollRef}
            role="region"
            aria-label="User Guide article"
            className={`min-h-0 overflow-y-auto bg-black/20 p-4 sm:p-6 ${slide.guideArticle ? "lg:col-span-2" : ""}`}
          >
            <div
              ref={mobileTopicSearchRef}
              className="mb-3 space-y-2 lg:hidden"
              onBlur={(event) => {
                if (event.relatedTarget && !event.currentTarget.contains(event.relatedTarget as Node)) {
                  setMobileTopicSearchOpen(false);
                }
              }}
            >
              <div className="relative">
                <Search className="pointer-events-none absolute left-2.5 top-1/2 h-4 w-4 -translate-y-1/2 text-muted" />
                <input
                  type="search"
                  value={search}
                  onChange={(event) => setSearch(event.target.value)}
                  onFocus={() => setMobileTopicSearchOpen(true)}
                  onKeyDown={(event) => {
                    if (event.key === "Escape") {
                      setMobileTopicSearchOpen(false);
                      event.currentTarget.blur();
                    }
                  }}
                  placeholder="Search topics"
                  aria-label="Search User Guide topics on mobile"
                  aria-expanded={mobileTopicSearchOpen}
                  aria-controls="mobile-topic-search-results"
                  className="w-full rounded-lg border border-border bg-input py-2 pl-8 pr-2 text-sm text-foreground placeholder:text-muted focus:border-accent focus:outline-none"
                />
              </div>
              {mobileTopicSearchOpen ? (
                <div
                  id="mobile-topic-search-results"
                  className="max-h-48 space-y-1 overflow-y-auto rounded-lg border border-border bg-input p-1"
                  role="region"
                  aria-label="Mobile topic search results"
                >
                  {mobileVisibleEntries.length === 0 ? (
                    <div className="px-3 py-2 text-sm text-muted">No topics match “{trimmedSearch}”.</div>
                  ) : (
                    mobileVisibleEntries.map(({ topic }) => (
                      <button
                        key={topic.id}
                        type="button"
                        onClick={() => {
                          setSearch("");
                          setMobileTopicSearchOpen(false);
                          chooseTopic(topic.id);
                        }}
                        className={`block w-full rounded-lg px-3 py-2 text-left transition-colors ${topic.id === selectedTopic.id ? "bg-accent/15 text-accent" : "text-secondary hover:bg-card hover:text-foreground"}`}
                      >
                        <span className="block text-sm font-medium">{topic.title}</span>
                        {topic.description ? (
                          <span className="mt-0.5 line-clamp-2 block text-xs text-muted">{topic.description}</span>
                        ) : null}
                      </button>
                    ))
                  )}
                </div>
              ) : null}
            </div>
            <StoryboardPreview
              slide={slide}
              onChooseTopic={chooseTopic}
              onAppNavigate={onAppNavigate}
              availableTopicIds={availableTopicIds}
            />
          </div>

          <aside
            aria-hidden={slide.guideArticle ? true : undefined}
            className={`${slide.guideArticle ? "hidden" : "flex"} min-h-0 flex-col overflow-y-auto border-t border-border p-4 lg:border-l lg:border-t-0 sm:p-6`}
          >
            <div className="text-xs font-semibold uppercase tracking-wide text-muted">{progressLabel}</div>
            <h3 className="mt-2 text-xl font-semibold text-foreground">{slide.title}</h3>
            {slide.caption ? <p className="mt-3 text-sm leading-6 text-secondary">{slide.caption}</p> : null}
            {slide.bodyMarkdown ? <ManualMarkdown markdown={slide.bodyMarkdown} /> : null}
            {(slide.points?.length ?? 0) > 0 ? (
              <div className="mt-5 space-y-2">
                {slide.points!.map((point) => (
                  <ManualBoxPoint key={point} point={point} />
                ))}
              </div>
            ) : null}
            {(slide.links?.length ?? 0) > 0 ? (
              <div className="mt-5 space-y-2">
                {slide.links!.map((link) => (
                  <a
                    key={`${link.label}:${link.url}`}
                    href={link.url}
                    target="_blank"
                    rel="noopener noreferrer"
                    className="inline-flex w-full items-center justify-between gap-3 rounded-lg border border-border bg-card/70 px-3 py-2 text-sm text-accent transition-colors hover:border-accent hover:bg-card"
                  >
                    <span className="truncate">{link.label}</span>
                    <ExternalLink className="h-4 w-4 shrink-0" />
                  </a>
                ))}
              </div>
            ) : null}

            {(() => {
              const topicLinks = (slide.topicLinks ?? []).filter((link) =>
                topics.some((topic) => topic.id === link.topicId),
              );
              if (topicLinks.length === 0) return null;
              return (
                <div className="mt-5 space-y-2">
                  <div className="text-xs font-semibold uppercase tracking-wide text-muted">Keep going</div>
                  {topicLinks.map((link) => (
                    <button
                      key={`${link.topicId}:${link.slideId ?? ""}:${link.label}`}
                      type="button"
                      onClick={() => chooseTopic(link.topicId, link.slideId)}
                      className="inline-flex w-full items-center justify-between gap-3 rounded-lg border border-border bg-card/70 px-3 py-2 text-sm text-accent transition-colors hover:border-accent hover:bg-card"
                    >
                      <span className="truncate">{link.label}</span>
                      <ChevronRight className="h-4 w-4 shrink-0" />
                    </button>
                  ))}
                </div>
              );
            })()}

            <div className="mt-auto pt-6">
              <div className="mb-4 flex gap-1.5">
                {selectedTopic.slides.map((item, itemIndex) => (
                  <button
                    key={item.id}
                    type="button"
                    onClick={() => setIndex(itemIndex)}
                    className={`h-1.5 flex-1 rounded-full transition-colors ${itemIndex === index ? "bg-accent" : "bg-border hover:bg-muted"}`}
                    aria-label={`Go to slide ${itemIndex + 1}`}
                  />
                ))}
              </div>
              <div className="flex flex-wrap items-center justify-between gap-2">
                <button
                  type="button"
                  onClick={goToPrevious}
                  disabled={isVeryFirst}
                  className="inline-flex items-center gap-1.5 rounded-lg border border-border px-3 py-2 text-sm text-secondary transition-colors hover:border-accent hover:text-foreground disabled:cursor-not-allowed disabled:opacity-45"
                >
                  <ChevronLeft className="h-4 w-4" />
                  Back
                </button>
                <button
                  type="button"
                  onClick={goToNext}
                  className="inline-flex items-center gap-1.5 rounded-lg bg-accent px-4 py-2 text-sm font-medium text-white transition-colors hover:bg-accent-hover"
                >
                  {isVeryLast ? "Done" : "Next"}
                  {isVeryLast ? <Check className="h-4 w-4" /> : <ChevronRight className="h-4 w-4" />}
                </button>
              </div>
            </div>
          </aside>
        </div>
      </div>
    </div>
  );
}

function mergeTutorialTopics(extensionTopics: ExtensionTutorialTopic[]): TutorialStoryboardTopic[] {
  const normalizedExtensionTopics = extensionTopics
    .filter((topic) => topic.id && topic.title && (topic.slides?.length ?? 0) > 0)
    .map<TutorialStoryboardTopic>((topic) => ({
      id: topic.id,
      title: topic.title,
      description: topic.description,
      pages: topic.pages,
      contexts: normalizeManualContexts(topic.contexts),
      extensionId: topic.extensionId,
      parentTopicId: topic.parentTopicId,
      kind: topic.kind,
      order: topic.order ?? 100,
      slides: (topic.slides ?? []).map((slide) => ({
        id: slide.id,
        title: slide.title,
        caption: slide.caption,
        bodyMarkdown: slide.bodyMarkdown,
        imageSrc: resolveManualImageSrc(slide.imageSrc, topic.extensionId),
        imageAlt: slide.imageAlt,
        mockKind: normalizeMockKind(slide.mockKind),
        points: slide.points?.length ? slide.points : [],
        links: normalizeManualLinks(slide.links),
      })),
    }));

  return [...builtinTutorialTopics, ...normalizedExtensionTopics].sort(
    (left, right) => left.order - right.order || left.title.localeCompare(right.title),
  );
}

function buildTopicEntries(topics: TutorialStoryboardTopic[]): TutorialTopicEntry[] {
  const sorted = [...topics].sort((left, right) => left.order - right.order || left.title.localeCompare(right.title));
  const byId = new Map(sorted.map((topic) => [topic.id, topic]));
  const childrenByParent = new Map<string, TutorialStoryboardTopic[]>();
  const roots: TutorialStoryboardTopic[] = [];

  for (const topic of sorted) {
    if (topic.parentTopicId && byId.has(topic.parentTopicId)) {
      const children = childrenByParent.get(topic.parentTopicId) ?? [];
      children.push(topic);
      childrenByParent.set(topic.parentTopicId, children);
    } else {
      roots.push(topic);
    }
  }

  const entries: TutorialTopicEntry[] = [];
  const visited = new Set<string>();
  const visit = (topic: TutorialStoryboardTopic, depth: number) => {
    if (visited.has(topic.id)) return;
    visited.add(topic.id);
    entries.push({ topic, depth });
    for (const child of childrenByParent.get(topic.id) ?? []) {
      visit(child, depth + 1);
    }
  };

  for (const topic of roots) {
    visit(topic, 0);
  }

  for (const topic of sorted) {
    visit(topic, 0);
  }

  return entries;
}

function ancestorsOf(topicId: string, parentByChild: Map<string, string>): string[] {
  const result: string[] = [];
  let current = parentByChild.get(topicId);
  let guard = 0;
  while (current && guard++ < 32) {
    result.push(current);
    current = parentByChild.get(current);
  }
  return result;
}

function matchesTopicSearch(topic: TutorialStoryboardTopic, query: string): boolean {
  const needle = query.toLowerCase();
  if (topic.title.toLowerCase().includes(needle)) return true;
  if (topic.description?.toLowerCase().includes(needle)) return true;
  return topic.slides.some(
    (slide) =>
      slide.title.toLowerCase().includes(needle) ||
      slide.caption?.toLowerCase().includes(needle) ||
      slide.points?.some((point) => point.toLowerCase().includes(needle)) ||
      (slide.guideArticle && JSON.stringify(slide.guideArticle).toLowerCase().includes(needle)),
  );
}

function normalizeManualLinks(links?: { label: string; url: string }[]) {
  return (links ?? [])
    .map((link) => ({ label: link.label?.trim(), url: normalizeManualLinkUrl(link.url) }))
    .filter((link): link is { label: string; url: string } => Boolean(link.label && link.url));
}

function normalizeManualLinkUrl(url?: string) {
  if (!url) return undefined;
  try {
    const parsed = new URL(url);
    return parsed.protocol === "http:" || parsed.protocol === "https:" ? parsed.toString() : undefined;
  } catch {
    return undefined;
  }
}

function ManualBoxPoint({ point }: { point: string }) {
  const parsed = parseManualBoxPoint(point);
  const tone = parsed.tone;
  const toneClasses = tone ? manualBoxToneClasses[tone] : "border-border bg-card/70";
  const dotClasses = tone ? manualBoxDotClasses[tone] : "bg-accent";

  return (
    <div
      data-box-tone={tone}
      className={`flex items-start gap-2 rounded-lg border px-3 py-2 text-sm text-secondary transition-colors ${toneClasses}`}
    >
      <span className={`mt-1.5 h-1.5 w-1.5 shrink-0 rounded-full ${dotClasses}`} aria-hidden="true" />
      <span>{parsed.text}</span>
    </div>
  );
}

function parseManualBoxPoint(point: string): ManualBoxPointContent {
  const trimmed = point.trim();
  const explicit = trimmed.match(/^\[(green|blue|purple|orange|pink|teal)\]\s*(.*)$/i);
  if (explicit) {
    return {
      tone: explicit[1].toLowerCase() as ManualBoxTone,
      text: explicit[2].trim() || trimmed,
    };
  }

  const legacy = trimmed.match(/^(?:the\s+)?(green|blue|purple|orange|pink|teal)\s+box(?:\s+is)?[:\s-]+(.*)$/i);
  if (legacy) {
    return {
      tone: legacy[1].toLowerCase() as ManualBoxTone,
      text: legacy[2].trim() || trimmed,
    };
  }

  return { text: trimmed };
}

const manualBoxToneClasses: Record<ManualBoxTone, string> = {
  green: "border-green-500/55 hover:border-green-400/75",
  blue: "border-blue-500/55 hover:border-blue-400/75",
  purple: "border-violet-500/55 hover:border-violet-400/75",
  orange: "border-orange-500/55 hover:border-orange-400/75",
  pink: "border-pink-500/55 hover:border-pink-400/75",
  teal: "border-teal-500/55 hover:border-teal-400/75",
};

const manualBoxDotClasses: Record<ManualBoxTone, string> = {
  green: "bg-green-400",
  blue: "bg-blue-400",
  purple: "bg-violet-400",
  orange: "bg-orange-400",
  pink: "bg-pink-400",
  teal: "bg-teal-400",
};

function resolveManualImageSrc(imageSrc: string | undefined, extensionId: string | undefined) {
  const value = imageSrc?.trim();
  if (!value) return undefined;
  if (isAbsoluteManualAssetUrl(value) || value.startsWith("/")) return value;
  if (!extensionId) return value;

  const normalizedPath = value.replace(/^\.\//, "").split("/").filter(Boolean).map(encodeURIComponent).join("/");
  return `/api/extensions/assets/${encodeURIComponent(extensionId)}/${normalizedPath}`;
}

function isAbsoluteManualAssetUrl(value: string) {
  try {
    const parsed = new URL(value);
    return parsed.protocol === "http:" || parsed.protocol === "https:" || parsed.protocol === "data:";
  } catch {
    return false;
  }
}

function ManualMarkdown({ markdown }: { markdown: string }) {
  return (
    <div className="mt-4 text-sm leading-6 text-secondary">
      <ReactMarkdown
        components={{
          p: ({ children }) => <p className="mb-3 last:mb-0">{children}</p>,
          ul: ({ children }) => <ul className="mb-3 list-disc space-y-1 pl-5 last:mb-0">{children}</ul>,
          ol: ({ children }) => <ol className="mb-3 list-decimal space-y-1 pl-5 last:mb-0">{children}</ol>,
          li: ({ children }) => <li>{children}</li>,
          strong: ({ children }) => <strong className="font-semibold text-foreground">{children}</strong>,
          code: ({ children }) => (
            <code className="rounded bg-card px-1 py-0.5 text-xs text-foreground">{children}</code>
          ),
          a: ({ href, children }) => {
            const safeHref = normalizeManualLinkUrl(href);
            return safeHref ? (
              <a href={safeHref} target="_blank" rel="noopener noreferrer" className="text-accent hover:underline">
                {children}
              </a>
            ) : (
              <span>{children}</span>
            );
          },
        }}
      >
        {markdown}
      </ReactMarkdown>
    </div>
  );
}

function normalizeMockKind(value?: string): TutorialSlideMockKind | undefined {
  const knownKinds = new Set<TutorialSlideMockKind>([
    "tasks",
    "feed",
    "metadata",
    "settings",
    "videoPlayer",
    "tagging",
    "images",
    "extension",
  ]);
  return knownKinds.has(value as TutorialSlideMockKind) ? (value as TutorialSlideMockKind) : undefined;
}

function normalizeManualContexts(contexts?: string[]) {
  return uniqueManualContexts(contexts ?? []);
}

const legacyTopicAliases: Record<string, string> = {
  "content-types": "media-types",
  "content-images": "media-types",
  "content-galleries": "media-types",
  "content-audio": "media-types",
  "content-texts": "media-types",
  tagging: "organizing",
  groups: "dynamic-groups",
  segments: "segments-and-compilations",
  "segments-raw-derived": "segments-and-compilations",
  "segments-display-profiles": "segments-and-compilations",
  "segments-compilations": "segments-and-compilations",
  search: "search-and-filters",
  downloaders: "providers-scrapers-downloaders",
  metadata: "providers-scrapers-downloaders",
  "metadata-scrapers": "providers-scrapers-downloaders",
  "metadata-servers": "providers-scrapers-downloaders",
  "metadata-tagger": "providers-scrapers-downloaders",
  security: "users-roles-permissions",
  "security-users": "users-roles-permissions",
  "security-roles-permissions": "users-roles-permissions",
  "security-content-rules": "users-roles-permissions",
  "security-sharing": "users-roles-permissions",
  "backups-upgrades": "backups-migrations-upgrades",
};

function pickInitialTopicId(topics: TutorialStoryboardTopic[], request?: TutorialOpenRequest, currentPage?: string) {
  const requestedTopicId = request?.topicId ? (legacyTopicAliases[request.topicId] ?? request.topicId) : undefined;
  if (requestedTopicId && topics.some((topic) => topic.id === requestedTopicId)) {
    return requestedTopicId;
  }

  const contextTopicId = pickTopicIdForContexts(topics, request, currentPage);
  if (contextTopicId) {
    return contextTopicId;
  }

  const page = request?.page ?? currentPage;
  if (page) {
    const pageTopic = topics.find((topic) => topic.pages?.includes(page));
    if (pageTopic) return pageTopic.id;
  }

  return topics.find((topic) => topic.id === "getting-started")?.id ?? topics[0]?.id ?? "getting-started";
}

function pickTopicIdForContexts(
  topics: TutorialStoryboardTopic[],
  request?: TutorialOpenRequest,
  currentPage?: string,
) {
  const contexts = uniqueManualContexts([
    request?.context,
    ...(request?.contexts ?? []),
    currentPage ? `page:${currentPage}` : undefined,
  ]);

  let bestMatch: { topicId: string; score: number } | undefined;

  topics.forEach((topic, topicIndex) => {
    contexts.forEach((context, contextIndex) => {
      const score = scoreTopicContextMatch(topic, context, contextIndex, topicIndex);
      if (score == null) return;
      if (!bestMatch || score > bestMatch.score) {
        bestMatch = { topicId: topic.id, score };
      }
    });
  });

  return bestMatch?.topicId;
}

function scoreTopicContextMatch(
  topic: TutorialStoryboardTopic,
  context: string,
  contextIndex: number,
  topicIndex: number,
) {
  const normalizedContext = normalizeManualContext(context);
  if (!normalizedContext) return undefined;

  if (topic.contexts?.some((topicContext) => normalizeManualContext(topicContext) === normalizedContext)) {
    return 10000 - contextIndex * 10 - topicIndex / 1000;
  }

  if (normalizedContext.startsWith("page:")) {
    const page = normalizedContext.slice("page:".length);
    if (topic.pages?.some((topicPage) => topicPage.toLowerCase() === page)) {
      return 1000 - contextIndex * 10 - topicIndex / 1000;
    }
  }

  return undefined;
}

function SlideImage({ src, alt }: { src: string; alt: string }) {
  const [failed, setFailed] = useState(false);
  useEffect(() => setFailed(false), [src]);
  const fileName = src.split("/").pop() ?? src;

  if (failed) {
    return (
      <div className="flex h-full min-h-[34rem] flex-col items-center justify-center gap-4 rounded-lg border border-dashed border-border bg-card p-10 text-center shadow-xl">
        <div className="flex h-16 w-16 items-center justify-center rounded-2xl bg-accent/10 text-accent">
          <ImageIcon className="h-8 w-8" />
        </div>
        <div className="max-w-md">
          <div className="text-sm font-semibold uppercase tracking-wide text-muted">Screenshot pending</div>
          <div className="mt-2 text-base font-medium text-foreground">{alt}</div>
          <div className="mt-2 rounded bg-background px-3 py-1.5 font-mono text-xs text-secondary">{fileName}</div>
        </div>
      </div>
    );
  }

  return (
    <div className="flex h-full min-h-[34rem] items-center justify-center overflow-hidden rounded-lg border border-border bg-card shadow-xl">
      <img
        src={src}
        alt={alt}
        onError={() => setFailed(true)}
        className="block max-h-full w-full object-contain bg-black"
      />
    </div>
  );
}

function StoryboardPreview({
  slide,
  onChooseTopic,
  onAppNavigate,
  availableTopicIds,
}: {
  slide: TutorialStoryboardSlide;
  onChooseTopic: (topicId: string, slideId?: string) => void;
  onAppNavigate?: (href: string) => void;
  availableTopicIds: ReadonlySet<string>;
}) {
  if (slide.guideArticle) {
    return (
      <SharedFeatureGuideArticle
        title={slide.title}
        article={slide.guideArticle}
        onChooseTopic={onChooseTopic}
        onAppNavigate={onAppNavigate}
        availableTopicIds={availableTopicIds}
      />
    );
  }

  if (slide.imageSrc) {
    return <SlideImage src={slide.imageSrc} alt={slide.imageAlt ?? slide.title} />;
  }

  if (!slide.mockKind) {
    return (
      <div className="flex h-full min-h-[34rem] flex-col items-center justify-center gap-4 rounded-lg border border-border bg-card p-10 text-center shadow-xl">
        <div className="flex h-16 w-16 items-center justify-center rounded-2xl bg-accent/15 text-accent">
          <BookOpen className="h-8 w-8" />
        </div>
        <div className="max-w-md">
          <div className="text-xl font-semibold text-foreground">{slide.title}</div>
          {slide.caption ? <p className="mt-2 text-sm leading-6 text-secondary">{slide.caption}</p> : null}
        </div>
      </div>
    );
  }

  return (
    <div className="mx-auto h-full min-h-[34rem] max-w-5xl overflow-hidden rounded-lg border border-border bg-card shadow-xl">
      <div className="flex items-center gap-2 border-b border-border bg-nav px-3 py-2">
        <div className="h-2.5 w-2.5 rounded-full bg-red-400/80" />
        <div className="h-2.5 w-2.5 rounded-full bg-amber-300/80" />
        <div className="h-2.5 w-2.5 rounded-full bg-green-400/80" />
        <div className="ml-2 h-6 flex-1 rounded bg-background px-3 text-xs leading-6 text-muted">cove.local</div>
      </div>
      <div className="grid min-h-[calc(100%-2.5rem)] grid-cols-[10rem_minmax(0,1fr)] bg-background">
        <div className="border-r border-border bg-nav/90 p-3">
          <div className="mb-4 h-7 rounded bg-accent/25" />
          {["Videos", "Images", "Texts", "Settings"].map((item, index) => (
            <div
              key={item}
              className={`mb-2 flex items-center gap-2 rounded px-2 py-2 text-xs ${index === 0 ? "bg-accent/20 text-accent" : "text-secondary"}`}
            >
              <div className="h-3 w-3 rounded bg-current opacity-60" />
              <span>{item}</span>
            </div>
          ))}
        </div>
        <div className="p-4">
          {slide.mockKind === "tasks" ? <TasksMock /> : null}
          {slide.mockKind === "feed" ? <FeedMock /> : null}
          {slide.mockKind === "metadata" ? <MetadataMock /> : null}
          {slide.mockKind === "settings" ? <SettingsMock /> : null}
          {slide.mockKind === "videoPlayer" ? <VideoPlayerMock /> : null}
          {slide.mockKind === "tagging" ? <TaggingMock /> : null}
          {slide.mockKind === "images" ? <ImagesMock /> : null}
          {slide.mockKind === "extension" || !slide.mockKind ? <ExtensionMock /> : null}
        </div>
      </div>
    </div>
  );
}

function SharedFeatureGuideBlocks({
  blocks,
  keyPrefix,
  onChooseTopic,
  onAppNavigate,
  availableTopicIds,
}: {
  blocks: SharedFeatureGuideBlock[] | SharedFeatureGuideContentBlock[];
  keyPrefix: string;
  onChooseTopic: (topicId: string, slideId?: string) => void;
  onAppNavigate?: (href: string) => void;
  availableTopicIds: ReadonlySet<string>;
}) {
  return blocks.map((block, index) => {
    const key = `${keyPrefix}:${index}`;
    if (block.type === "paragraph") return <p key={key}>{block.text}</p>;
    if (block.type === "steps") {
      return (
        <ol key={key} className="list-decimal space-y-2 pl-6">
          {block.items.map((item) => (
            <li key={item}>{item}</li>
          ))}
        </ol>
      );
    }
    if (block.type === "heading") {
      return block.level === 4 ? (
        <h6 key={key} className="pt-2 text-base font-semibold text-foreground">
          {block.text}
        </h6>
      ) : (
        <h5 key={key} className="pt-2 text-lg font-semibold text-foreground">
          {block.text}
        </h5>
      );
    }
    if (block.type === "list") {
      return (
        <ul key={key} className="list-disc space-y-2 pl-6">
          {block.items.map((item) => (
            <li key={item}>{item}</li>
          ))}
        </ul>
      );
    }
    if (block.type === "links") {
      return (
        <div key={key} className="flex flex-wrap gap-2">
          {block.items.map((item) => {
            const appHref = resolveInAppRouteHref(item.appHref);
            return item.topicId && availableTopicIds.has(item.topicId) ? (
              <button
                key={item.label}
                type="button"
                onClick={() => onChooseTopic(item.topicId!, item.slideId)}
                className="rounded-lg border border-border bg-card px-3 py-2 text-sm text-accent transition-colors hover:border-accent"
              >
                {item.label}
              </button>
            ) : (
              <a
                key={item.label}
                href={appHref ?? resolveInAppGuideHref(item.href)}
                target={appHref ? undefined : "_blank"}
                rel={appHref ? undefined : "noopener noreferrer"}
                onClick={
                  appHref && onAppNavigate
                    ? (event) => {
                        event.preventDefault();
                        onAppNavigate(appHref);
                      }
                    : undefined
                }
                className="rounded-lg border border-border bg-card px-3 py-2 text-sm text-accent transition-colors hover:border-accent"
              >
                {item.label}
              </a>
            );
          })}
        </div>
      );
    }
    if (block.type === "note") {
      return (
        <aside key={key} className="rounded-r-lg border-l-4 border-accent bg-accent/10 px-4 py-3">
          {block.text}
        </aside>
      );
    }
    if (block.type === "image") {
      return (
        <figure key={key} className="overflow-hidden rounded-lg border border-border bg-card">
          <img src={block.src} alt={block.alt} className="mx-auto block h-auto max-w-full" />
          {block.caption ? <figcaption className="px-4 py-3 text-sm text-muted">{block.caption}</figcaption> : null}
        </figure>
      );
    }
    if (block.type === "table") {
      return (
        <div key={key} className="overflow-x-auto rounded-lg border border-border">
          <table className="min-w-[38rem] border-collapse text-left text-sm">
            {block.caption ? (
              <caption className="px-4 py-3 text-left font-semibold text-foreground">{block.caption}</caption>
            ) : null}
            <thead className="bg-card text-foreground">
              <tr>
                {block.columns.map((column) => (
                  <th key={column} scope="col" className="border-b border-border px-4 py-3 font-semibold">
                    {column}
                  </th>
                ))}
              </tr>
            </thead>
            <tbody>
              {block.rows.map((row, rowIndex) => (
                <tr key={`${key}:row:${rowIndex}`} className="border-b border-border last:border-b-0">
                  {row.map((cell, cellIndex) => (
                    <td key={`${key}:cell:${rowIndex}:${cellIndex}`} className="px-4 py-3 align-top">
                      {cell}
                    </td>
                  ))}
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      );
    }
    if (block.type === "code") {
      return (
        <pre
          key={key}
          className="overflow-x-auto rounded-lg border border-border bg-card p-4 text-xs text-foreground sm:text-sm"
        >
          <code>{block.lines.join("\n")}</code>
        </pre>
      );
    }
    if (block.type === "recipes") {
      return (
        <ul key={key} aria-label="Recipes" className="list-none space-y-3 p-0">
          {block.items.map((recipe) => (
            <li key={recipe.id}>
              <details className="group overflow-hidden rounded-lg border border-border bg-card/50 open:bg-card">
                <summary className="cursor-pointer px-4 py-3 text-foreground marker:text-accent hover:bg-card">
                  <span className="font-semibold">{recipe.title}</span>
                  {recipe.description ? (
                    <span className="mt-1 block text-sm font-normal text-muted">{recipe.description}</span>
                  ) : null}
                </summary>
                <div className="space-y-4 border-t border-border px-4 py-4">
                  <SharedFeatureGuideBlocks
                    blocks={recipe.blocks}
                    keyPrefix={`${key}:${recipe.id}`}
                    onChooseTopic={onChooseTopic}
                    onAppNavigate={onAppNavigate}
                    availableTopicIds={availableTopicIds}
                  />
                </div>
              </details>
            </li>
          ))}
        </ul>
      );
    }
    return null;
  });
}

function SharedFeatureGuideArticle({
  title,
  article,
  onChooseTopic,
  onAppNavigate,
  availableTopicIds,
}: {
  title: string;
  article: SharedFeatureGuideArticle;
  onChooseTopic: (topicId: string, slideId?: string) => void;
  onAppNavigate?: (href: string) => void;
  availableTopicIds: ReadonlySet<string>;
}) {
  return (
    <article className="mx-auto max-w-4xl rounded-xl border border-border bg-background p-5 shadow-xl sm:p-8">
      <h3 className="text-2xl font-semibold text-foreground sm:text-3xl">{title}</h3>
      <p className="mt-3 text-base leading-7 text-secondary">{article.description}</p>
      <div className="mt-8 space-y-10">
        {article.sections.map((section) => (
          <section key={section.id} aria-labelledby={`manual-guide-${section.id}`}>
            <h4 id={`manual-guide-${section.id}`} className="text-xl font-semibold text-foreground">
              {section.title}
            </h4>
            <div className="mt-3 space-y-4 text-sm leading-6 text-secondary sm:text-base sm:leading-7">
              <SharedFeatureGuideBlocks
                blocks={section.blocks}
                keyPrefix={section.id}
                onChooseTopic={onChooseTopic}
                onAppNavigate={onAppNavigate}
                availableTopicIds={availableTopicIds}
              />
            </div>
          </section>
        ))}
      </div>
    </article>
  );
}

function resolveInAppGuideHref(href: string) {
  return href.startsWith("/docs/") ? `https://yourcove.net${href}` : href;
}

function resolveInAppRouteHref(href: string | undefined) {
  return href?.startsWith("/") && !href.startsWith("//") ? href : undefined;
}

function TasksMock() {
  return (
    <div className="space-y-3">
      <div className="flex items-center justify-between">
        <div>
          <div className="text-lg font-semibold text-foreground">Scan & Generate</div>
          <div className="text-xs text-muted">Library indexing</div>
        </div>
        <RefreshCw className="h-5 w-5 text-accent" />
      </div>
      {["Scan library", "Generate previews", "Build hashes"].map((label, index) => (
        <div key={label} className="rounded-lg border border-border bg-card p-3">
          <div className="flex items-center justify-between gap-3">
            <div className="flex items-center gap-3">
              <FolderOpen className="h-5 w-5 text-accent" />
              <div>
                <div className="text-sm font-medium text-foreground">{label}</div>
                <div className="text-xs text-muted">{index === 0 ? "Run first" : "Queue when ready"}</div>
              </div>
            </div>
            <div className="rounded bg-accent px-3 py-1 text-xs font-medium text-white">Run</div>
          </div>
        </div>
      ))}
    </div>
  );
}

function FeedMock() {
  return (
    <div className="space-y-3">
      <div className="flex flex-wrap gap-2 text-xs">
        {["Grid", "Feed", "Wall", "Infinite"].map((label, index) => (
          <div
            key={label}
            className={`rounded px-2.5 py-1 ${index === 1 || index === 3 ? "bg-accent text-white" : "bg-card text-secondary"}`}
          >
            {label}
          </div>
        ))}
      </div>
      <div className="grid gap-3 md:grid-cols-2">
        {[0, 1].map((item) => (
          <div key={item} className="overflow-hidden rounded-lg border border-border bg-card">
            <div className="aspect-video bg-gradient-to-br from-accent/70 via-cyan-500/35 to-rose-400/45" />
            <div className="space-y-2 p-3">
              <div className="h-2.5 w-3/4 rounded bg-foreground/75" />
              <div className="flex gap-1.5">
                <span className="rounded border border-border px-2 py-0.5 text-[11px] text-muted">tag</span>
                <span className="rounded border border-border px-2 py-0.5 text-[11px] text-muted">rating</span>
              </div>
            </div>
          </div>
        ))}
      </div>
    </div>
  );
}

function MetadataMock() {
  return (
    <div className="grid gap-3 md:grid-cols-[1.1fr_0.9fr]">
      <div className="overflow-hidden rounded-lg border border-border bg-card">
        <div className="aspect-[4/3] bg-gradient-to-br from-sky-500/50 via-accent/40 to-fuchsia-400/40" />
        <div className="space-y-2 p-3">
          <div className="h-2.5 w-4/5 rounded bg-foreground/80" />
          <div className="h-2.5 w-2/3 rounded bg-foreground/35" />
        </div>
      </div>
      <div className="rounded-lg border border-border bg-card p-3">
        <div className="mb-3 flex items-center gap-2 text-sm font-semibold text-foreground">
          <Database className="h-4 w-4 text-accent" /> Metadata
        </div>
        {["Scrape", "Identify", "Apply fields"].map((label, index) => (
          <div
            key={label}
            className={`mb-2 rounded px-3 py-2 text-sm ${index === 0 ? "bg-accent text-white" : "bg-background text-secondary"}`}
          >
            {label}
          </div>
        ))}
      </div>
    </div>
  );
}

function SettingsMock() {
  return (
    <div className="space-y-3">
      <div className="flex items-center gap-2 text-lg font-semibold text-foreground">
        <Settings className="h-5 w-5 text-accent" /> Settings
      </div>
      <div className="grid gap-3 md:grid-cols-2">
        {["Navigation", "Video Player", "Feed & Viewer", "Extensions"].map((label, index) => (
          <div key={label} className="rounded-lg border border-border bg-card p-3">
            <div className="mb-3 flex items-center gap-2 text-sm font-medium text-foreground">
              {index === 0 ? <LayoutGrid className="h-4 w-4 text-accent" /> : <Play className="h-4 w-4 text-accent" />}
              {label}
            </div>
            <div className="space-y-2">
              <div className="h-2 rounded bg-foreground/60" />
              <div className="h-2 w-2/3 rounded bg-foreground/30" />
            </div>
          </div>
        ))}
      </div>
    </div>
  );
}

function VideoPlayerMock() {
  return (
    <div className="space-y-3">
      <div className="overflow-hidden rounded-lg border border-border bg-card">
        <div className="flex aspect-video items-center justify-center bg-gradient-to-br from-slate-900 via-indigo-950 to-cyan-950">
          <Play className="h-14 w-14 rounded-full bg-black/40 p-3 text-white" />
        </div>
        <div className="space-y-2 p-3">
          <div className="h-2 rounded bg-accent" />
          <div className="flex justify-between text-xs text-muted">
            <span>04:12</span>
            <span>38:20</span>
          </div>
        </div>
      </div>
      <div className="grid gap-3 md:grid-cols-3">
        {["Resume", "Segments", "Details"].map((label) => (
          <div key={label} className="rounded-lg border border-border bg-card p-3 text-sm text-secondary">
            {label}
          </div>
        ))}
      </div>
    </div>
  );
}

function TaggingMock() {
  return (
    <div className="space-y-3">
      <div className="flex items-center gap-2 text-lg font-semibold text-foreground">
        <Tag className="h-5 w-5 text-accent" /> Tagger
      </div>
      <div className="grid gap-2 md:grid-cols-3">
        {Array.from({ length: 9 }, (_, index) => (
          <div key={index} className="rounded-lg border border-border bg-card p-3">
            <div className="mb-3 aspect-video rounded bg-gradient-to-br from-teal-500/35 via-accent/25 to-amber-300/30" />
            <div className="h-2 rounded bg-foreground/60" />
          </div>
        ))}
      </div>
    </div>
  );
}

function ImagesMock() {
  return (
    <div className="space-y-3">
      <div className="flex items-center gap-2 text-lg font-semibold text-foreground">
        <ImageIcon className="h-5 w-5 text-accent" /> Images
      </div>
      <div className="columns-3 gap-2">
        {[1.1, 0.75, 1.35, 0.9, 1.25, 0.8, 1.45, 1].map((ratio, index) => (
          <div key={index} className="mb-2 break-inside-avoid overflow-hidden rounded-lg border border-border bg-card">
            <div
              style={{ aspectRatio: `1 / ${ratio}` }}
              className="bg-gradient-to-br from-emerald-500/40 via-sky-400/25 to-rose-400/35"
            />
          </div>
        ))}
      </div>
    </div>
  );
}

function ExtensionMock() {
  return (
    <div className="flex h-full min-h-[28rem] flex-col items-center justify-center rounded-lg border border-border bg-card p-6 text-center">
      <HelpCircle className="mb-4 h-12 w-12 text-accent" />
      <div className="text-lg font-semibold text-foreground">Extension Topic</div>
      <div className="mt-2 max-w-md text-sm leading-6 text-secondary">
        This slide can be supplied by a Cove extension with its own screenshots, points, and page targeting.
      </div>
    </div>
  );
}
