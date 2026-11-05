import type { ExtensionFeatureGuide } from "../api/types";

export type SharedFeatureGuideContentBlock =
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

export type SharedFeatureGuideBlock =
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

export interface SharedFeatureGuideSection {
  id: string;
  title: string;
  blocks: SharedFeatureGuideBlock[];
}

export interface SharedFeatureGuideArticle {
  description: string;
  sections: SharedFeatureGuideSection[];
}

export interface NormalizedFeatureGuide extends SharedFeatureGuideArticle {
  id: string;
  title: string;
  order: number;
  pages?: string[];
  contexts?: string[];
  parentTopicId?: string;
}

const slugPattern = /^[a-z0-9]+(?:-[a-z0-9]+)*$/;
const websiteHrefPattern = /^(?:\/docs\/|https?:\/\/)/;

/**
 * Rebuilds an extension's feature guide from only the fields and blocks this Cove can render. Cove's
 * own guides are schema-checked when the site and app build; an extension guide is checked by the
 * host only for its outline, so each block is validated here and unknown or malformed blocks are
 * dropped instead of breaking the User Guide. Relative image sources resolve inside the extension
 * package, and links keep the same href rules as the schema.
 */
export function normalizeExtensionFeatureGuide(entry: ExtensionFeatureGuide): NormalizedFeatureGuide | null {
  const guide = asRecord(entry.guide);
  if (!guide || guide.schemaVersion !== 1) return null;
  const id = slug(guide.id);
  const title = text(guide.title);
  const description = text(guide.description);
  const order =
    typeof guide.order === "number" && Number.isInteger(guide.order) && guide.order >= 0 ? guide.order : undefined;
  if (!id || !title || !description || order === undefined || !Array.isArray(guide.sections)) return null;

  const resolveImage = (source: string) => resolveExtensionGuideImage(entry.extensionId, entry.path, source);
  const sections = guide.sections.flatMap<SharedFeatureGuideSection>((value) => {
    const section = asRecord(value);
    const sectionId = slug(section?.id);
    const sectionTitle = text(section?.title);
    if (!section || !sectionId || !sectionTitle || !Array.isArray(section.blocks)) return [];
    const blocks = section.blocks.flatMap((block) => normalizeBlock(block, resolveImage, true));
    return blocks.length > 0 ? [{ id: sectionId, title: sectionTitle, blocks }] : [];
  });
  if (sections.length === 0) return null;

  return {
    id,
    title,
    description,
    order,
    pages: strings(guide.pages),
    contexts: strings(guide.contexts),
    parentTopicId: slug(guide.parentTopicId),
    sections: withUniqueIds(sections),
  };
}

/**
 * Section IDs become element IDs and React keys, so a guide that repeats one would render two elements
 * with the same ID. Later repeats get a numbered suffix instead of being dropped.
 */
function withUniqueIds<T extends { id: string }>(items: T[]): T[] {
  const taken = new Set(items.map((item) => item.id));
  const seen = new Set<string>();
  const nextSuffix = new Map<string, number>();
  return items.map((item) => {
    if (!seen.has(item.id)) {
      seen.add(item.id);
      return item;
    }
    // Resume from the last suffix used for this ID, so a guide repeating one ID many times stays linear.
    let suffix = nextSuffix.get(item.id) ?? 2;
    while (taken.has(`${item.id}-${suffix}`)) suffix++;
    nextSuffix.set(item.id, suffix + 1);
    const id = `${item.id}-${suffix}`;
    taken.add(id);
    seen.add(id);
    return { ...item, id };
  });
}

function normalizeBlock(
  value: unknown,
  resolveImage: (source: string) => string | undefined,
  allowRecipes: true,
): SharedFeatureGuideBlock[];
function normalizeBlock(
  value: unknown,
  resolveImage: (source: string) => string | undefined,
  allowRecipes: false,
): SharedFeatureGuideContentBlock[];
function normalizeBlock(
  value: unknown,
  resolveImage: (source: string) => string | undefined,
  allowRecipes: boolean,
): SharedFeatureGuideBlock[] {
  const block = asRecord(value);
  if (!block) return [];

  switch (block.type) {
    case "paragraph":
    case "note": {
      const body = text(block.text);
      return body ? [{ type: block.type, text: body }] : [];
    }
    case "steps":
    case "list": {
      const items = strings(block.items);
      return items ? [{ type: block.type, items }] : [];
    }
    case "heading": {
      const body = text(block.text);
      return body && (block.level === 3 || block.level === 4)
        ? [{ type: "heading", level: block.level, text: body }]
        : [];
    }
    case "links": {
      const items = (Array.isArray(block.items) ? block.items : []).flatMap((item) => {
        const link = asRecord(item);
        const label = text(link?.label);
        const href = text(link?.href);
        if (!link || !label || !href || !websiteHrefPattern.test(href)) return [];
        const appHref = toInAppRoute(text(link.appHref));
        return [
          {
            label,
            href,
            ...(appHref ? { appHref } : {}),
            ...(slug(link.topicId) ? { topicId: slug(link.topicId) } : {}),
            ...(slug(link.slideId) ? { slideId: slug(link.slideId) } : {}),
          },
        ];
      });
      return items.length > 0 ? [{ type: "links", items }] : [];
    }
    case "image": {
      const source = text(block.src);
      const alt = text(block.alt);
      const src = source ? resolveImage(source) : undefined;
      const caption = text(block.caption);
      return src && alt ? [{ type: "image", src, alt, ...(caption ? { caption } : {}) }] : [];
    }
    case "table": {
      const columns = strings(block.columns);
      const rows = Array.isArray(block.rows)
        ? block.rows.filter(
            (row): row is string[] =>
              Array.isArray(row) && row.length === columns?.length && row.every((cell) => typeof cell === "string"),
          )
        : [];
      const caption = text(block.caption);
      return columns && rows.length > 0 ? [{ type: "table", columns, rows, ...(caption ? { caption } : {}) }] : [];
    }
    case "code": {
      const lines =
        Array.isArray(block.lines) && block.lines.every((line) => typeof line === "string") ? block.lines : [];
      const language = text(block.language);
      return lines.length > 0 ? [{ type: "code", lines, ...(language ? { language } : {}) }] : [];
    }
    case "recipes": {
      if (!allowRecipes || !Array.isArray(block.items)) return [];
      const items = block.items.flatMap((item) => {
        const recipe = asRecord(item);
        const id = slug(recipe?.id);
        const title = text(recipe?.title);
        if (!recipe || !id || !title || !Array.isArray(recipe.blocks)) return [];
        const blocks = recipe.blocks.flatMap((recipeBlock) => normalizeBlock(recipeBlock, resolveImage, false));
        const description = text(recipe.description);
        return blocks.length > 0 ? [{ id, title, blocks, ...(description ? { description } : {}) }] : [];
      });
      return items.length > 0 ? [{ type: "recipes", items: withUniqueIds(items) }] : [];
    }
    default:
      return [];
  }
}

const routeBase = "https://cove.invalid";

/**
 * Returns a guide's in-app link as a same-origin route, or undefined when it would leave Cove. Browsers
 * treat a backslash like a slash and ignore tabs and newlines in URLs, so `/\host` and `/\t/host` are
 * other sites even though they start with a single slash.
 */
export function toInAppRoute(href: string | undefined): string | undefined {
  if (!href || !href.startsWith("/") || href.includes("\\") || hasControlCharacter(href)) return undefined;
  try {
    // Return the checked string, not the normalized URL: removing dot segments from `/..//host` yields
    // `//host`, which a browser would open as another site.
    const url = new URL(href, routeBase);
    return url.origin === routeBase && !url.pathname.startsWith("//") ? href : undefined;
  } catch {
    return undefined;
  }
}

/**
 * Resolves an image the way a guide author reads it: relative to the guide file. A source that would
 * leave the extension package is dropped; https URLs and inline images are kept as written.
 */
export function resolveExtensionGuideImage(extensionId: string, guidePath: string, source: string): string | undefined {
  if (/^https:\/\//i.test(source) || /^data:image\//i.test(source)) return source;
  if (/^[a-z][a-z0-9+.-]*:/i.test(source) || source.startsWith("/") || source.startsWith("\\")) return undefined;

  const segments = guidePath.split("/").slice(0, -1);
  for (const segment of source.split(/[\\/]/)) {
    if (segment === "" || segment === ".") continue;
    if (segment === "..") {
      if (segments.length === 0) return undefined;
      segments.pop();
    } else {
      segments.push(segment);
    }
  }
  if (segments.length === 0) return undefined;
  return `/api/extensions/assets/${encodeURIComponent(extensionId)}/${segments.map(encodeURIComponent).join("/")}`;
}

function hasControlCharacter(value: string) {
  for (let index = 0; index < value.length; index++) {
    const code = value.charCodeAt(index);
    if (code < 0x20 || code === 0x7f) return true;
  }
  return false;
}

function asRecord(value: unknown): Record<string, unknown> | undefined {
  return value && typeof value === "object" && !Array.isArray(value) ? (value as Record<string, unknown>) : undefined;
}

function text(value: unknown) {
  return typeof value === "string" && value.trim() ? value : undefined;
}

function slug(value: unknown) {
  return typeof value === "string" && slugPattern.test(value) ? value : undefined;
}

function strings(value: unknown) {
  if (!Array.isArray(value)) return undefined;
  const items = value.filter((item): item is string => typeof item === "string" && item.trim() !== "");
  return items.length > 0 ? items : undefined;
}
