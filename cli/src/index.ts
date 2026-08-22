#!/usr/bin/env bun
import { input, password } from "@inquirer/prompts";
import { Command, CommanderError, Option } from "commander";
import { copyFile, link, lstat, rename, unlink, writeFile } from "node:fs/promises";
import { constants as fsConstants } from "node:fs";
import { basename, dirname, join } from "node:path";
import { randomInt, randomUUID } from "node:crypto";
import { audiosForCriteria } from "./audios";
import { CoveClient } from "./client";
import { parseCompletionShell, renderCompletion, setCompletionChoices } from "./completions";
import { ConfigStore, normalizeServer, resolveProfile, selectedProfileName } from "./config";
import { CliError, toCliError } from "./errors";
import { listEntities, mergeObjectFilters } from "./entity-list";
import { filterByCompletionValues, filterByObjectFilter } from "./filter-by";
import type { FilterByResource } from "./filter-by";
import { renderAudio, renderAudios, renderAuthSummary, renderCatalogDetail, renderGalleries, renderGlobalSearch, renderGroups, renderImages, renderLoginSummary, renderLogoutSummary, renderPerformer, renderPerformers, renderProfileChange, renderProfiles, renderSavedFilters, renderSegments, renderSimilarImages, renderSimilarVideos, renderStudios, renderTags, renderTexts, renderVideo, renderVideoResults } from "./output";
import type { CatalogEntityKind, RenderContext } from "./output";
import { resolveResultWindow } from "./pagination";
import type { ResultWindowOptions } from "./pagination";
import { defaultSavedFilter, listSavedFilters, queryForSavedFilter, resolveSavedFilter, savedFilterSummary } from "./saved-filters";
import { isSegmentRecord, listSegments } from "./segments";
import { fetchThemeAccent } from "./theme";
import type { Audio, GalleryRecord, GlobalSearchResponse, GroupRecord, ImageRecord, JobInfo, ListQueryOptions, LoginResponse, MeResponse, MetadataServerSummary, Performer, SavedFilter, SegmentRecord, SimilarImageResult, SimilarVideoResult, StoredProfile, StudioRecord, SystemStatus, Tag, TextRecord, Video } from "./types";
import { cleanInline, configureCliHelp, DEFAULT_ACCENT, terminalColorsEnabled, terminalHyperlinksEnabled, uiPalette } from "./ui";
import type { UiColor } from "./ui";
import { resolvePerformer, resolveTag, videosForCriteria } from "./videos";

type OutputFormat = "human" | "json" | "jsonl";
type HyperlinkMode = "auto" | "always" | "never";
const BEST_EFFORT_TIMEOUT_MS = 750;
const BUILT_IN_LIST_SORTS: Partial<Record<FilterByResource, { key: string; direction: "desc" }>> = {
  videos: { key: "date", direction: "desc" },
  audios: { key: "date", direction: "desc" },
  images: { key: "date", direction: "desc" },
  galleries: { key: "date", direction: "desc" },
  groups: { key: "date", direction: "desc" },
  texts: { key: "date", direction: "desc" },
  performers: { key: "latest_video_date", direction: "desc" },
  studios: { key: "latest_video_date", direction: "desc" },
  tags: { key: "latest_video_date", direction: "desc" },
};

interface GlobalOptions { profile?: string; server?: string; json?: boolean; output?: OutputFormat; color?: boolean; hyperlinks?: HyperlinkMode }
interface CatalogListOptions extends ResultWindowOptions { query?: string; filter?: string; filterBy: string[]; savedFilter?: string; defaultFilter?: boolean; sortBy: Array<{ key: string; direction: "asc" | "desc" }>; tag?: string[]; excludeTag?: string[]; performer?: string[]; excludePerformer?: string[] }
interface TagFilterOptions extends CatalogListOptions { tag: string[]; excludeTag: string[] }
interface EntityFilterOptions extends CatalogListOptions { tag: string[]; excludeTag: string[]; performer: string[]; excludePerformer: string[] }
interface SimilarOptions { by: "visual" | "audio"; type?: "videos" | "images"; limit: number }

interface ExtendedHelp { fields: string[]; heading: string }

const extendedHelp = new WeakMap<Command, ExtendedHelp>();

function globals(command: Command): GlobalOptions {
  const options = command.optsWithGlobals<GlobalOptions>();
  outputFormat(options);
  return options;
}

function outputFormat(options: GlobalOptions): OutputFormat {
  if (options.json && options.output && options.output !== "json") throw new CliError("INVALID_ARGUMENT", "--json cannot be combined with a non-JSON --output format.");
  return options.json ? "json" : options.output ?? "human";
}

function parseOutputFormat(value: string): OutputFormat {
  if (value === "human" || value === "json" || value === "jsonl") return value;
  throw new CliError("INVALID_ARGUMENT", "--output must be one of human, json, or jsonl.");
}

function parseHyperlinkMode(value: string): HyperlinkMode {
  if (value === "auto" || value === "always" || value === "never") return value;
  throw new CliError("INVALID_ARGUMENT", "--hyperlinks must be one of auto, always, or never.");
}

function print(value: unknown, options: GlobalOptions, human: () => string, records?: unknown[]): void {
  const format = outputFormat(options);
  if (format === "human") {
    process.stdout.write(`${human()}\n`);
    return;
  }
  if (format === "json") {
    process.stdout.write(`${JSON.stringify(value ?? null, null, 2)}\n`);
    return;
  }
  const lines = (records ?? [value]).map(record => JSON.stringify(record ?? null));
  if (lines.length) process.stdout.write(`${lines.join("\n")}\n`);
}

function colorsFor(options: GlobalOptions, stream: NodeJS.WriteStream = process.stdout): boolean {
  return outputFormat(options) === "human" && terminalColorsEnabled(options.color !== false, stream);
}

function themedColorsFor(options: GlobalOptions, accent: string, stream: NodeJS.WriteStream = process.stdout): UiColor {
  return colorsFor(options, stream) ? accent : false;
}

function hyperlinksFor(options: GlobalOptions, stream: NodeJS.WriteStream = process.stdout): boolean {
  if (outputFormat(options) !== "human" || options.hyperlinks === "never") return false;
  return options.hyperlinks === "always" || terminalHyperlinksEnabled(stream);
}

function presentationFor(options: GlobalOptions, accent: string, context: Omit<RenderContext, "color" | "hyperlinks"> = {}): RenderContext {
  return {
    ...context,
    color: themedColorsFor(options, accent),
    hyperlinks: hyperlinksFor(options),
    ...(process.stdout.columns === undefined ? {} : { terminalWidth: process.stdout.columns }),
  };
}

function warnForHttp(server: string, options: GlobalOptions): void {
  if (!server.startsWith("http://") || outputFormat(options) !== "human") return;
  const paint = uiPalette(colorsFor(options, process.stderr));
  process.stderr.write(`${paint.warning("warning:")} credentials will be sent over plain HTTP.\n`);
}

function clientFor(store: ConfigStore, options: GlobalOptions): Promise<{ client: CoveClient; name: string; profile: StoredProfile }> {
  return store.load().then(config => {
    const resolved = resolveProfile(config, options);
    warnForHttp(resolved.profile.server, options);
    return {
      name: resolved.name,
      profile: resolved.profile,
      client: new CoveClient({ store, profileName: resolved.name, profile: resolved.profile, transientCredential: resolved.transientCredential }),
    };
  });
}

async function startMaintenanceJob(store: ConfigStore, command: Command, path: string, label: string): Promise<void> {
  const options = globals(command);
  const { client } = await clientFor(store, options);
  const result = await client.post<unknown>(path);
  print(result, options, () => renderMutation(label, result));
}

async function metadataServersFor(client: CoveClient): Promise<MetadataServerSummary[]> {
  try {
    const config = await client.get<unknown>("system/config", BEST_EFFORT_TIMEOUT_MS);
    if (!isRecord(config) || !isRecord(config.scraping) || !Array.isArray(config.scraping.metadataServers)) return [];
    return config.scraping.metadataServers.flatMap(value => {
      if (!isRecord(value) || typeof value.endpoint !== "string") return [];
      return [{
        endpoint: value.endpoint,
        ...(typeof value.name === "string" && value.name.trim() ? { name: value.name } : {}),
      }];
    });
  } catch {
    return [];
  }
}

function hasRemoteIds(value: unknown): boolean {
  return Array.isArray(value) && value.length > 0;
}

function withExamples(command: Command, examples: string[]): Command {
  return command.addHelpText("after", `\nExamples:\n${examples.map(example => `  ${example}`).join("\n")}`);
}

function commandInvocation(command: Command): string {
  const names: string[] = [];
  for (let current: Command | null = command; current; current = current.parent) names.unshift(current.name());
  return names.join(" ");
}

function withSortFields(command: Command, fields: string[], heading = "Sort fields", repeatable = true): Command {
  extendedHelp.set(command, { fields, heading });
  const path = commandInvocation(command).split(" ").slice(1).join(" ");
  return command.addHelpText("after", `\nSort:\n  Use --sort-by <field>[:<asc|desc>]${repeatable ? " (repeatable)" : ""}; direction defaults to asc.\n  Run \`cove-cli help ${path}\` to see every field.`);
}

function collect(value: string, previous: string[]): string[] {
  return [...previous, value];
}

function queryPair(value: string, previous: string[]): string[] {
  if (!value.includes("=")) throw new CliError("INVALID_ARGUMENT", "--param must use name=value.");
  return [...previous, value];
}

function positiveInteger(option: string, maximum?: number): (value: string) => number {
  return value => {
    if (!/^\d+$/.test(value) || Number(value) < 1) throw new CliError("INVALID_ARGUMENT", `${option} must be a positive integer.`);
    const parsed = Number(value);
    if (!Number.isSafeInteger(parsed)) throw new CliError("INVALID_ARGUMENT", `${option} must be a positive integer.`);
    if (maximum !== undefined && parsed > maximum) throw new CliError("INVALID_ARGUMENT", `${option} must be between 1 and ${maximum}.`);
    return parsed;
  };
}

function entityId(value: string): number {
  return positiveInteger("ID", 2_147_483_647)(value);
}

const MUTABLE_RESOURCES = new Set(["videos", "audios", "images", "galleries", "tags", "performers", "studios", "groups", "texts"]);
const MERGEABLE_RESOURCES = new Set(["videos", "tags", "performers", "studios"]);

function mutableResource(value: string): string {
  const normalized = value.toLowerCase();
  if (!MUTABLE_RESOURCES.has(normalized)) throw new CliError("INVALID_ARGUMENT", `Unsupported library resource “${value}”.`);
  return normalized;
}

function similaritySource(value: string): "video" | "image" {
  const normalized = value.trim().toLowerCase();
  if (normalized === "video" || normalized === "image") return normalized;
  throw new CliError("INVALID_ARGUMENT", "Similarity source must be video or image.");
}

function similarityApiBase(manifest: unknown, signal: SimilarOptions["by"]): string {
  const key = `${signal}-similarity`;
  const features = isRecord(manifest) && Array.isArray(manifest.features) ? manifest.features : [];
  const feature = features.find(value => isRecord(value) && typeof value.key === "string" && value.key.toLowerCase() === key);
  const options = isRecord(feature) && isRecord(feature.options) ? feature.options : undefined;
  const raw = typeof options?.apiBasePath === "string" ? options.apiBasePath.trim() : "";
  if (!raw) throw new CliError("FEATURE_UNAVAILABLE", `No ${signal} similarity provider is available.`);
  const normalized = raw.replace(/^\/+/, "").replace(/^api\/+/, "").replace(/\/+$/, "");
  let segments: string[];
  try {
    segments = normalized.split("/").map(segment => decodeURIComponent(segment));
  } catch {
    throw new CliError("INVALID_RESPONSE", `The ${signal} similarity provider advertised an invalid API path.`);
  }
  if (!normalized || raw.includes("://") || /[?#]/.test(raw) || segments.some(segment => !segment || segment === "." || segment === "..")) {
    throw new CliError("INVALID_RESPONSE", `The ${signal} similarity provider advertised an invalid API path.`);
  }
  return normalized;
}

function entityKindForResource(resource: string): string {
  return resource === "galleries" ? "gallery" : resource.replace(/s$/, "");
}

function commaSeparatedIds(value: string, option = "IDs"): number[] {
  const ids = value.split(",").map(item => item.trim()).filter(Boolean).map(entityId);
  if (!ids.length) throw new CliError("INVALID_ARGUMENT", `${option} must contain at least one ID.`);
  return [...new Set(ids)];
}

function mutationBody(value: string): Record<string, unknown> {
  return objectFilter(value);
}

function renderMutation(label: string, value: unknown): string {
  return `${label}\n${value === undefined ? "Done" : JSON.stringify(value, null, 2)}`;
}

function isJob(value: unknown): value is JobInfo {
  return isRecord(value) && typeof value.id === "string" && typeof value.type === "string" && typeof value.description === "string" && typeof value.status === "string" && typeof value.progress === "number";
}

function renderJobs(items: JobInfo[]): string {
  if (!items.length) return "No jobs";
  return items.map(job => `${job.id}\t${job.status}\t${Math.round(job.progress * 100)}%\t${cleanInline(job.description)}`).join("\n");
}

function genericRecords(value: unknown): unknown[] | undefined {
  if (Array.isArray(value)) return value;
  if (isRecord(value) && Array.isArray(value.items)) return value.items;
  return undefined;
}

function renderGeneric(label: string, value: unknown): string {
  const records = genericRecords(value);
  if (records?.length === 0) return `No ${label.toLowerCase()}`;
  return `${label}\n${JSON.stringify(value, null, 2)}`;
}

async function saveDownload(path: string, body: ReadableStream<Uint8Array>, force: boolean): Promise<void> {
  const temporary = join(dirname(path), `.${basename(path)}.cove-cli-${process.pid}-${randomUUID()}.tmp`);
  if (!force) {
    try {
      await lstat(path);
      throw new CliError("FILE_EXISTS", `File “${path}” already exists. Use --force to replace it.`);
    } catch (error) {
      if (error instanceof CliError) throw error;
      if (!isRecord(error) || error.code !== "ENOENT") throw new CliError("FILE_WRITE_FAILED", `Could not inspect “${path}”.`, { details: error instanceof Error ? error.message : undefined });
    }
  }
  try {
    await writeFile(temporary, body, { flag: "wx" });
    if (force) await rename(temporary, path);
    else {
      try {
        await link(temporary, path);
      } catch (error) {
        const code = isRecord(error) ? error.code : undefined;
        if (!new Set(["EPERM", "ENOSYS", "EOPNOTSUPP", "ENOTSUP"]).has(String(code))) throw error;
        await copyFile(temporary, path, fsConstants.COPYFILE_EXCL);
      }
      await unlink(temporary);
    }
  } catch (error) {
    await unlink(temporary).catch(() => undefined);
    const code = isRecord(error) ? error.code : undefined;
    if (code === "EEXIST") throw new CliError("FILE_EXISTS", `File “${path}” already exists. Use --force to replace it.`);
    throw new CliError("FILE_WRITE_FAILED", `Could not write “${path}”.`, { details: error instanceof Error ? error.message : undefined });
  }
}

function collectSort(value: string, previous: EntityFilterOptions["sortBy"]): EntityFilterOptions["sortBy"] {
  if (previous.length >= 5) throw new CliError("INVALID_ARGUMENT", "Provide at most 5 --sort-by options.");
  const match = /^([^:]+?)(?::(asc|desc))?$/i.exec(value.trim());
  if (!match) throw new CliError("INVALID_ARGUMENT", "--sort-by must use field, field:asc, or field:desc.");
  const key = match[1]!.trim();
  if (!key) throw new CliError("INVALID_ARGUMENT", "--sort-by must use field, field:asc, or field:desc.");
  if (previous.some(sort => sort.key.toLowerCase() === key.toLowerCase())) throw new CliError("INVALID_ARGUMENT", `--sort-by field “${key}” may only be used once.`);
  return [...previous, { key, direction: (match[2]?.toLowerCase() ?? "asc") as "asc" | "desc" }];
}

function withEntityFilterOptions(command: Command, resource: FilterByResource): Command {
  return withPagingSortOptions(command
    .option("--tag <id-or-name>", "require tag ID, exact name, or exact alias (repeatable)", collect, [])
    .option("--exclude-tag <id-or-name>", "exclude tag ID, exact name, or exact alias (repeatable)", collect, [])
    .option("--performer <id-or-name>", "require performer ID, exact name, or exact alias (repeatable)", collect, [])
    .option("--exclude-performer <id-or-name>", "exclude performer ID, exact name, or exact alias (repeatable)", collect, []), resource);
}

function withPagingSortOptions(command: Command, resource: FilterByResource): Command {
  const filterBy = new Option("--filter-by <field:operator=value>", "filter a text field (repeatable; filters combine with AND)")
    .argParser(collect)
    .default([]);
  setCompletionChoices(filterBy, filterByCompletionValues(resource));
  return withPagingOptions(command)
    .option("--filter <json>", "advanced Cove object-filter JSON")
    .option("--saved-filter <id-or-name>", "start with a saved filter, then apply explicit list filters")
    .option("--no-default-filter", "ignore the account-backed default for this view")
    .addOption(filterBy)
    .option("--sort-by <field[:direction]>", "sort clause; direction defaults to asc (repeatable, max 5)", collectSort, []);
}

function withPagingOptions(command: Command, defaultPerPage = 25): Command {
  return withResultVolumeOptions(command)
    .option("--page <number>", "return one result page (default: 1)", positiveInteger("--page"))
    .option("--per-page <number>", `results per page, 1–250 (default: ${defaultPerPage})`, positiveInteger("--per-page", 250));
}

function objectFilter(value: string | undefined): Record<string, unknown> {
  if (!value) return {};
  try {
    const parsed: unknown = JSON.parse(value);
    if (!isRecord(parsed)) throw new Error("not an object");
    return parsed;
  } catch {
    throw new CliError("INVALID_ARGUMENT", "--filter must be a JSON object.");
  }
}

function listObjectFilter(options: Pick<CatalogListOptions, "filter" | "filterBy">, resource: FilterByResource): Record<string, unknown> {
  const raw = objectFilter(options.filter);
  const explicit = filterByObjectFilter(options.filterBy, resource);
  return mergeObjectFilters(raw, explicit);
}

async function queryOptionsForList(client: CoveClient, options: CatalogListOptions, resource: FilterByResource, volume: ResultWindowOptions, describeFilters = false, relationFilter: Record<string, unknown> = {}): Promise<ListQueryOptions> {
  const explicitObjectFilter = mergeObjectFilters(listObjectFilter(options, resource), relationFilter);
  const explicitRandomSeed = options.sortBy[0]?.key.toLowerCase() === "random" ? randomInt(1, 2_147_483_647) : undefined;
  const hasRelationFilter = [options.tag, options.excludeTag, options.performer, options.excludePerformer].some(values => values?.length);
  const hasExplicitState = !!options.query || !!options.filter || !!options.filterBy.length || options.sortBy.length > 0 || hasRelationFilter;
  const savedFilter = options.savedFilter
    ? await resolveSavedFilter(client, options.savedFilter, resource)
    : options.defaultFilter !== false && !hasExplicitState ? await defaultSavedFilter(client, resource) : undefined;
  if (!savedFilter) {
    const sorts = hasExplicitState ? options.sortBy : BUILT_IN_LIST_SORTS[resource] ? [BUILT_IN_LIST_SORTS[resource]!] : [];
    return { q: options.query, ...volume, sorts, ...(explicitRandomSeed === undefined ? {} : { seed: explicitRandomSeed }), ...(!hasExplicitState && sorts.length ? { stabilizeSort: false } : {}), objectFilter: explicitObjectFilter };
  }
  const saved = queryForSavedFilter(savedFilter, resource);
  const explicitSort = options.sortBy.length > 0;
  const finalQuery = {
    q: options.query ?? saved.q,
    sorts: explicitSort ? options.sortBy : saved.sorts,
    objectFilter: mergeObjectFilters(saved.objectFilter, explicitObjectFilter),
  };
  return {
    ...(describeFilters ? { appliedFilterSummary: await savedFilterSummary(client, finalQuery, options.savedFilter && "name" in savedFilter ? `Saved filter “${savedFilter.name}”` : "Default filter", resource) } : {}),
    defaultFilterApplied: !options.savedFilter,
    q: finalQuery.q,
    ...volume,
    sorts: finalQuery.sorts,
    ...(explicitRandomSeed !== undefined ? { seed: explicitRandomSeed } : !explicitSort && saved.seed !== undefined ? { seed: saved.seed } : {}),
    ...(!explicitSort ? { stabilizeSort: false } : {}),
    objectFilter: finalQuery.objectFilter,
  };
}

function relationObjectFilter(criteria: { tagIds: number[]; excludedTagIds: number[]; performerIds?: number[]; excludedPerformerIds?: number[] }): Record<string, unknown> {
  const filter: Record<string, unknown> = {};
  if (criteria.tagIds.length || criteria.excludedTagIds.length) filter.tagsCriterion = { value: [], modifier: "includes", requiredIds: criteria.tagIds, excludes: criteria.excludedTagIds };
  if (criteria.performerIds?.length || criteria.excludedPerformerIds?.length) filter.performersCriterion = { value: [], modifier: "includes", requiredIds: criteria.performerIds ?? [], excludes: criteria.excludedPerformerIds ?? [] };
  return filter;
}

function jsonObjectOption(value: string | undefined, option: string): string | undefined {
  if (value === undefined) return undefined;
  try {
    const parsed: unknown = JSON.parse(value);
    if (!isRecord(parsed)) throw new Error("not an object");
    return JSON.stringify(parsed);
  } catch {
    throw new CliError("INVALID_ARGUMENT", `${option} must be a JSON object.`);
  }
}

function withResultVolumeOptions(command: Command): Command {
  return command
    .option("--limit <number>", "maximum results to return (default: 25)", positiveInteger("--limit", 2_147_483_647))
    .option("--unlimited", "return every matching result in successive batches");
}

function resultVolume(options: ResultWindowOptions): ResultWindowOptions {
  const explicit = {
    ...(options.page === undefined ? {} : { page: options.page }),
    ...(options.perPage === undefined ? {} : { perPage: options.perPage }),
    ...(options.limit === undefined ? {} : { limit: options.limit }),
    ...(options.unlimited ? { unlimited: true } : {}),
  };
  resolveResultWindow(explicit);
  return Object.keys(explicit).length ? explicit : { limit: 25 };
}

function listRenderContext(server: string, totalCount: number, options: ResultWindowOptions, pageDefault: number | false = 25, defaultFilterApplied = false, appliedFilterSummary?: string): Omit<RenderContext, "color" | "hyperlinks"> {
  const paged = pageDefault !== false && (options.page !== undefined || options.perPage !== undefined || options.limit === undefined && !options.unlimited);
  if (!paged) return { server, totalCount, defaultFilterApplied, ...(appliedFilterSummary ? { appliedFilterSummary } : {}), listPosition: { offset: 0 } };
  const page = options.page ?? 1;
  const perPage = options.perPage ?? pageDefault;
  return { server, totalCount, defaultFilterApplied, ...(appliedFilterSummary ? { appliedFilterSummary } : {}), listPosition: { offset: (page - 1) * perPage, page, perPage } };
}

function withSingleSortOption(command: Command): Command {
  return command.option("--sort-by <field[:direction]>", "sort by field; direction defaults to asc", (value: string) => collectSort(value, [])[0]);
}

function withTagFilterOptions(command: Command, resource: "performers" | "studios"): Command {
  return withPagingSortOptions(command
    .option("--query <text>", "search records")
    .option("--tag <id-or-name>", "require tag ID, exact name, or exact alias (repeatable)", collect, [])
    .option("--exclude-tag <id-or-name>", "exclude tag ID, exact name, or exact alias (repeatable)", collect, []), resource);
}

async function resolveTagCriteria(client: CoveClient, options: TagFilterOptions) {
  const [tags, excludedTags] = await Promise.all([
    Promise.all(options.tag.map(reference => resolveTag(client, reference))),
    Promise.all(options.excludeTag.map(reference => resolveTag(client, reference))),
  ]);
  const tagIds = [...new Set(tags.map(item => item.id))];
  const excludedTagIds = [...new Set(excludedTags.map(item => item.id))];
  if (tagIds.some(id => excludedTagIds.includes(id))) throw new CliError("FILTER_CONFLICT", "The same tag cannot be both required and excluded.");
  return { tagIds, excludedTagIds };
}

async function resolveEntityCriteria(client: CoveClient, options: EntityFilterOptions) {
  const [tags, excludedTags, performers, excludedPerformers] = await Promise.all([
    Promise.all(options.tag.map(reference => resolveTag(client, reference))),
    Promise.all(options.excludeTag.map(reference => resolveTag(client, reference))),
    Promise.all(options.performer.map(reference => resolvePerformer(client, reference))),
    Promise.all(options.excludePerformer.map(reference => resolvePerformer(client, reference))),
  ]);
  const uniqueIds = (items: Array<{ id: number }>) => [...new Set(items.map(item => item.id))];
  const criteria = {
    tagIds: uniqueIds(tags), excludedTagIds: uniqueIds(excludedTags),
    performerIds: uniqueIds(performers), excludedPerformerIds: uniqueIds(excludedPerformers),
  };
  if (criteria.tagIds.some(id => criteria.excludedTagIds.includes(id)) || criteria.performerIds.some(id => criteria.excludedPerformerIds.includes(id))) {
    throw new CliError("FILTER_CONFLICT", "The same tag or performer cannot be both required and excluded.");
  }
  return criteria;
}

export function createProgram(store = new ConfigStore(), helpColor = terminalColorsEnabled(), accent = DEFAULT_ACCENT): Command {
  const renderFor = (options: GlobalOptions, context: Omit<RenderContext, "color" | "hyperlinks"> = {}): RenderContext => presentationFor(options, accent, context);
  const program = new Command()
    .name("cove-cli")
    .description("Explore and manage your Cove library")
    .optionsGroup("Global options:")
    .version("0.0.1")
    .option("--profile <name>", "named Cove server profile")
    .option("--server <url>", "Cove server URL")
    .option("-o, --output <format>", "output format: human, json, or jsonl", parseOutputFormat)
    .option("--hyperlinks <mode>", "OSC-8 hyperlinks: auto, always, or never", parseHyperlinkMode, "auto")
    .option("--json", "alias for --output json")
    .option("--no-color", "disable colored output")
    .exitOverride();

  const addSavedFilterCommands = (parent: Command, mode: FilterByResource, singular: string): void => {
    const filters = parent.command("filters").description(`Manage saved ${singular} filters`);
    withExamples(filters.command("list").description(`List saved ${singular} filters`), [
      `cove-cli ${mode} filters list --profile personal`,
      `cove-cli ${mode} filters list --json`,
    ]).action(async (_options, command: Command) => {
      const global = globals(command);
      const { client } = await clientFor(store, global);
      const items = await listSavedFilters(client, mode);
      print({ savedFilters: items, totalCount: items.length }, global, () => renderSavedFilters(items, renderFor(global), `Saved ${singular} filters`), items);
    });
    withExamples(filters.command("show <id-or-name>").description(`Show one saved ${singular} filter`), [
      `cove-cli ${mode} filters show 42 --profile personal`,
    ]).action(async (reference: string, _options, command: Command) => {
      const global = globals(command);
      const { client } = await clientFor(store, global);
      const item = await resolveSavedFilter(client, reference, mode);
      print(item, global, () => renderSavedFilters([item], renderFor(global), `Saved ${singular} filter`));
    });
    withExamples(filters.command("create").description(`Create a saved ${singular} filter`)
      .requiredOption("--name <name>", "saved-filter name")
      .option("--find-filter <json>", "find-filter JSON")
      .option("--object-filter <json>", "object-filter JSON")
      .option("--ui-options <json>", "UI-options JSON"), [
      `cove-cli ${mode} filters create --name "Recently added" --find-filter '{"sort":"created_at","direction":"desc"}'`,
    ]).action(async (options: { name: string; findFilter?: string; objectFilter?: string; uiOptions?: string }, command: Command) => {
      const global = globals(command);
      const { client } = await clientFor(store, global);
      const item = await client.post<SavedFilter>("savedfilters", { mode, name: options.name, findFilter: jsonObjectOption(options.findFilter, "--find-filter"), objectFilter: jsonObjectOption(options.objectFilter, "--object-filter"), uiOptions: jsonObjectOption(options.uiOptions, "--ui-options") });
      print(item, global, () => renderSavedFilters([item], renderFor(global), `Saved ${singular} filter`));
    });
    withExamples(filters.command("update <id>").description(`Update a saved ${singular} filter`)
      .option("--name <name>", "new saved-filter name")
      .option("--find-filter <json>", "replacement find-filter JSON")
      .option("--object-filter <json>", "replacement object-filter JSON")
      .option("--ui-options <json>", "replacement UI-options JSON"), [
      `cove-cli ${mode} filters update 42 --name "Recently added"`,
    ]).action(async (reference: string, options: { name?: string; findFilter?: string; objectFilter?: string; uiOptions?: string }, command: Command) => {
      const global = globals(command);
      const id = entityId(reference);
      if (Object.values(options).every(value => value === undefined)) throw new CliError("INVALID_ARGUMENT", "Provide at least one saved-filter field to update.");
      const { client } = await clientFor(store, global);
      await resolveSavedFilter(client, reference, mode);
      const item = await client.put<SavedFilter>(`savedfilters/${id}`, { ...options, findFilter: jsonObjectOption(options.findFilter, "--find-filter"), objectFilter: jsonObjectOption(options.objectFilter, "--object-filter"), uiOptions: jsonObjectOption(options.uiOptions, "--ui-options") });
      print(item, global, () => renderSavedFilters([item], renderFor(global), `Saved ${singular} filter`));
    });
    withExamples(filters.command("delete <id>").description(`Delete a saved ${singular} filter`), [
      `cove-cli ${mode} filters delete 42`,
    ]).action(async (reference: string, _options, command: Command) => {
      const global = globals(command);
      const id = entityId(reference);
      const { client } = await clientFor(store, global);
      await resolveSavedFilter(client, reference, mode);
      await client.delete(`savedfilters/${id}`);
      print({ id, deleted: true }, global, () => `Deleted saved ${singular} filter ${id}`);
    });
    withExamples(filters.command("default").description(`Show the account-backed default ${singular} filter`), [
      `cove-cli ${mode} filters default --profile personal`,
      `cove-cli ${mode} filters default --json`,
    ]).action(async (_options, command: Command) => {
      const global = globals(command);
      const { client } = await clientFor(store, global);
      const item = await defaultSavedFilter(client, mode);
      print({ defaultFilter: item ?? null }, global, () => renderGeneric(`Default ${singular} filter`, item ?? null));
    });
  };

  const auth = program.command("auth").description("Sign in and manage authentication").helpGroup("Account:");
  const login = auth.command("login")
    .description("Log in and save a named profile")
    .option("--username <name>", "Cove username")
    .option("--token <token>", "Cove API token (prefer COVE_TOKEN for automation)");
  withExamples(login, [
    "cove-cli auth login --server https://cove.example --profile personal",
    "COVE_TOKEN=... cove-cli auth login --server https://cove.example --profile automation",
  ]).action(async (options: { username?: string; token?: string }, command: Command) => {
      const global = globals(command);
      const config = await store.load();
      const name = selectedProfileName(config, global.profile);
      const existing = config.profiles[name];
      const serverValue = global.server ?? process.env.COVE_SERVER ?? existing?.server;
      if (!serverValue) throw new CliError("SERVER_REQUIRED", "Provide the Cove server with --server or COVE_SERVER.");
      const server = normalizeServer(serverValue);
      warnForHttp(server, global);
      const temporary = new CoveClient({ store, profileName: name, profile: { server } });
      const status = await temporary.get<SystemStatus>("system/status");
      const suppliedToken = options.token ?? process.env.COVE_TOKEN;
      let profile: StoredProfile;
      let me: MeResponse | undefined;
      let kind: "anonymous" | "apiToken" | "session";

      if (suppliedToken) {
        profile = { server, credential: { type: "apiToken", token: suppliedToken } };
        me = await new CoveClient({ store, profileName: name, profile, transientCredential: true }).get<MeResponse>("auth/me");
        kind = "apiToken";
      } else if (status.authEnabled === false) {
        profile = { server };
        kind = "anonymous";
      } else {
        if (!process.stdin.isTTY || !process.stdout.isTTY) {
          throw new CliError("INTERACTIVE_LOGIN_REQUIRED", "Password login requires an interactive terminal. Use COVE_TOKEN for non-interactive authentication.");
        }
        const promptContext = { input: process.stdin, output: process.stderr };
        const username = options.username ?? await input({ message: "Username:" }, promptContext);
        const secret = await password({ message: "Password:", mask: "*" }, promptContext);
        const login = await temporary.post<LoginResponse>("auth/login", { username, password: secret });
        if (!login.token || !login.refreshToken) throw new CliError("INVALID_RESPONSE", "Cove returned an invalid login response.");
        profile = { server, credential: { type: "session", accessToken: login.token, refreshToken: login.refreshToken, accessExpires: login.accessExpires, refreshExpires: login.refreshExpires } };
        me = await new CoveClient({ store, profileName: name, profile }).get<MeResponse>("auth/me");
        kind = "session";
      }
      await store.update(next => {
        next.profiles[name] = profile;
        next.defaultProfile ??= name;
      });
      const result = { profile: name, server, authentication: { kind, username: me?.user?.username } };
      print(result, global, () => renderLoginSummary(server, name, me?.user?.username, renderFor(global)));
    });

  withExamples(auth.command("status").description("Show server and authentication status"), [
    "cove-cli auth status --profile personal",
    "cove-cli auth status --profile personal --json",
  ]).action(async (_options, command) => {
    const global = globals(command);
    const { client, name, profile } = await clientFor(store, global);
    const status = await client.get<SystemStatus>("system/status");
    let me: MeResponse | undefined;
    if (profile.credential || status.authEnabled !== false) {
      try {
        me = await client.get<MeResponse>("auth/me");
      } catch (error) {
        if (!(error instanceof CliError) || error.status !== 401 || profile.credential) throw error;
      }
    }
    const result = { profile: name, server: profile.server, serverVersion: status.version, authEnabled: status.authEnabled, authenticated: !!me, user: me?.user, permissions: me?.permissions };
    const authStatus = me ? `authenticated as ${me.user.username ?? "unknown"}` : status.authEnabled === false ? "authentication disabled" : "not authenticated";
    print(result, global, () => renderAuthSummary(profile.server, name, status.version, authStatus, !!me, renderFor(global)));
  });

  withExamples(auth.command("logout").description("Revoke the session and remove local credentials"), [
    "cove-cli auth logout --profile personal",
  ]).action(async (_options, command) => {
    const global = globals(command);
    const config = await store.load();
    const resolved = resolveProfile(config, global);
    const loggedOutCredential = resolved.profile.credential;
    if (resolved.profile.credential?.type === "session" && !resolved.transientCredential) {
      const client = new CoveClient({ store, profileName: resolved.name, profile: resolved.profile });
      await client.post("auth/logout", { refreshToken: resolved.profile.credential.refreshToken });
    }
    if (!resolved.transientCredential && resolved.profile.credential) {
      await store.update(next => {
        const profile = next.profiles[resolved.name];
        if (profile && normalizeServer(profile.server) === resolved.profile.server && credentialsEqual(profile.credential, loggedOutCredential)) delete profile.credential;
      });
    }
    const result = { profile: resolved.name, loggedOut: true };
    print(result, global, () => renderLogoutSummary(resolved.name, renderFor(global)));
  });

  const profiles = program.command("profiles").description("Manage Cove server profiles").helpGroup("Account:");
  withExamples(profiles.command("list").description("List profiles"), [
    "cove-cli profiles list",
    "cove-cli profiles list --json",
  ]).action(async (_options, command) => {
    const global = globals(command);
    const config = await store.load();
    const items = Object.entries(config.profiles).sort(([a], [b]) => a.localeCompare(b)).map(([name, profile]) => ({ name, server: profile.server, default: name === config.defaultProfile, authentication: profile.credential?.type ?? "anonymous" }));
    print({ profiles: items }, global, () => renderProfiles(items, renderFor(global)), items);
  });
  withExamples(profiles.command("use <name>").description("Select the default profile"), [
    "cove-cli profiles use personal",
  ]).action(async (name: string, _options, command) => {
    const global = globals(command);
    await store.update(config => {
      if (!config.profiles[name]) throw new CliError("PROFILE_NOT_FOUND", `Profile “${name}” does not exist.`);
      config.defaultProfile = name;
    });
    print({ profile: name, default: true }, global, () => renderProfileChange("Default profile updated", name, renderFor(global)));
  });

  withExamples(program.command("search <query>")
    .description("Search across your Cove library")
    .helpGroup("Explore:")
    .option("--per-type <number>", "maximum results per entity type, 1–25 (default: 8)", positiveInteger("--per-type", 25), 8), [
    "cove-cli search \"Example\" --profile personal",
    "cove-cli search \"Example\" --per-type 20 --json",
  ]).action(async (query: string, options: { perType: number }, command: Command) => {
    const global = globals(command);
    if (query.trim().length < 2) throw new CliError("INVALID_ARGUMENT", "Search query must contain at least 2 non-whitespace characters.");
    const { client } = await clientFor(store, global);
    const result = await client.get<GlobalSearchResponse>(`search/global?q=${encodeURIComponent(query.trim())}&perType=${options.perType}`);
    if (!isGlobalSearchResponse(result)) throw new CliError("INVALID_RESPONSE", "Cove returned an invalid global search response.");
    const records = result.groups.flatMap(group => group.items.map(item => ({ ...item, type: group.type })));
    print(result, global, () => renderGlobalSearch(result, renderFor(global, { server: client.server })), records);
  });
  const similarityBy = setCompletionChoices(new Option("--by <signal>", "similarity signal: visual or audio").choices(["visual", "audio"]).default("visual"), ["visual", "audio"]);
  const similarityType = setCompletionChoices(new Option("--type <type>", "result type: videos or images").choices(["videos", "images"]), ["videos", "images"]);
  withExamples(program.command("similar <host> <id>")
    .description("Find visually or audibly similar media")
    .helpGroup("Explore:")
    .addOption(similarityBy)
    .addOption(similarityType)
    .option("--limit <number>", "maximum matches, 1–100 (default: 20)", positiveInteger("--limit", 100), 20), [
    "cove-cli similar video 42 --profile personal",
    "cove-cli similar video 42 --by visual --type images --limit 20",
    "cove-cli similar video 42 --by audio --json",
  ]).action(async (hostValue: string, reference: string, options: SimilarOptions, command: Command) => {
    const global = globals(command);
    const host = similaritySource(hostValue);
    const target = options.type ?? `${host}s` as "videos" | "images";
    if (options.by === "audio" && (host !== "video" || target !== "videos")) {
      throw new CliError("INVALID_ARGUMENT", "Audio similarity supports video sources and video results only.");
    }
    const id = entityId(reference);
    const { client } = await clientFor(store, global);
    const manifest = await client.get<unknown>("extensions/manifest");
    const apiBase = similarityApiBase(manifest, options.by);
    const result = await client.get<unknown>(`${apiBase}/${host}s/${id}/similar-${target}?perPage=${options.limit}`);
    if (target === "videos") {
      if (!isSimilarVideoResponse(result)) throw new CliError("INVALID_RESPONSE", `Cove returned invalid ${options.by} similarity results.`);
      print(result, global, () => renderSimilarVideos(result.items, options.by, renderFor(global, { server: client.server })), result.items);
      return;
    }
    if (!isSimilarImageResponse(result)) throw new CliError("INVALID_RESPONSE", "Cove returned invalid visual similarity results.");
    print(result, global, () => renderSimilarImages(result.items, renderFor(global, { server: client.server })), result.items);
  });
  withExamples(profiles.command("remove <name>").description("Remove a local profile"), [
    "cove-cli profiles remove old-server",
  ]).action(async (name: string, _options, command) => {
    const global = globals(command);
    await store.update(config => {
      if (!config.profiles[name]) throw new CliError("PROFILE_NOT_FOUND", `Profile “${name}” does not exist.`);
      delete config.profiles[name];
      if (config.defaultProfile === name) config.defaultProfile = Object.keys(config.profiles).sort()[0];
    });
    print({ profile: name, removed: true }, global, () => renderProfileChange("Profile removed", name, renderFor(global)));
  });

  const library = program.command("library").description("Create, edit, merge, and bulk-update library records").helpGroup("Manage:");
  library.command("create <resource>").description("Create a library record from JSON").requiredOption("--data <json>", "Cove create DTO as JSON")
    .action(async (resourceValue: string, options: { data: string }, command: Command) => {
      const global = globals(command);
      const resource = mutableResource(resourceValue);
      const { client } = await clientFor(store, global);
      const result = await client.post<unknown>(resource, mutationBody(options.data));
      print(result, global, () => renderMutation(`Created ${resource}`, result));
    });
  library.command("update <resource> <id>").description("Update a library record from JSON").requiredOption("--data <json>", "Cove update DTO as JSON")
    .action(async (resourceValue: string, reference: string, options: { data: string }, command: Command) => {
      const global = globals(command);
      const resource = mutableResource(resourceValue);
      const id = entityId(reference);
      const { client } = await clientFor(store, global);
      const result = await client.put<unknown>(`${resource}/${id}`, mutationBody(options.data));
      print(result, global, () => renderMutation(`Updated ${resource} ${id}`, result));
    });
  library.command("delete <resource> <id>").description("Delete a library record").requiredOption("--yes", "confirm permanent deletion")
    .action(async (resourceValue: string, reference: string, _options, command: Command) => {
      const global = globals(command);
      const resource = mutableResource(resourceValue);
      const id = entityId(reference);
      const { client } = await clientFor(store, global);
      await client.delete(`${resource}/${id}`);
      print({ resource, id, deleted: true }, global, () => `Deleted ${resource} ${id}`);
    });
  library.command("bulk-update <resource>").description("Bulk-update records from JSON").requiredOption("--data <json>", "Cove bulk-update DTO as JSON")
    .action(async (resourceValue: string, options: { data: string }, command: Command) => {
      const global = globals(command);
      const resource = mutableResource(resourceValue);
      const { client } = await clientFor(store, global);
      const result = await client.post<unknown>(`${resource}/bulk`, mutationBody(options.data));
      print(result, global, () => renderMutation(`Updated ${resource}`, result));
    });
  library.command("bulk-delete <resource>").description("Delete multiple records").requiredOption("--ids <ids>", "comma-separated record IDs").requiredOption("--yes", "confirm permanent deletion")
    .action(async (resourceValue: string, options: { ids: string }, command: Command) => {
      const global = globals(command);
      const resource = mutableResource(resourceValue);
      const ids = commaSeparatedIds(options.ids);
      const { client } = await clientFor(store, global);
      const result = resource === "videos"
        ? await client.post<unknown>("videos/destroy", { ids })
        : await client.request<unknown>(`${resource}/bulk`, { method: "DELETE", body: JSON.stringify({ ids }) });
      print(result, global, () => renderMutation(`Deleted ${resource}`, result));
    });
  library.command("merge <resource>").description("Merge source records into a target").requiredOption("--target <id>", "target record ID", entityId).requiredOption("--sources <ids>", "comma-separated source IDs").requiredOption("--yes", "confirm source records will be deleted")
    .action(async (resourceValue: string, options: { target: number; sources: string }, command: Command) => {
      const global = globals(command);
      const resource = mutableResource(resourceValue);
      if (!MERGEABLE_RESOURCES.has(resource)) throw new CliError("INVALID_ARGUMENT", `${resource} do not support merge.`);
      const sourceIds = commaSeparatedIds(options.sources);
      if (sourceIds.includes(options.target)) throw new CliError("INVALID_ARGUMENT", "The merge target cannot also be a source.");
      const { client } = await clientFor(store, global);
      const result = await client.post<unknown>(`${resource}/merge`, { targetId: options.target, sourceIds });
      print(result, global, () => renderMutation(`Merged ${resource} into ${options.target}`, result));
    });
  library.command("tag <resource>").description("Add, remove, or replace tags in bulk").requiredOption("--ids <ids>", "comma-separated record IDs").requiredOption("--tags <ids>", "comma-separated tag IDs").requiredOption("--mode <mode>", "add, remove, or set")
    .action(async (resourceValue: string, options: { ids: string; tags: string; mode: string }, command: Command) => {
      const global = globals(command);
      const resource = mutableResource(resourceValue);
      if (resource === "tags") throw new CliError("INVALID_ARGUMENT", "Tags cannot be tagged.");
      const mode = options.mode.toLowerCase();
      if (!new Set(["add", "remove", "set"]).has(mode)) throw new CliError("INVALID_ARGUMENT", "--mode must be add, remove, or set.");
      const body = { ids: commaSeparatedIds(options.ids), tagIds: commaSeparatedIds(options.tags, "--tags"), tagMode: mode };
      const { client } = await clientFor(store, global);
      const result = await client.post<unknown>(`${resource}/bulk`, body);
      print(result, global, () => renderMutation(`${mode} tags for ${resource}`, result));
    });
  library.command("favorite <resource> <id>").description("Set an entity's favorite state").requiredOption("--value <boolean>", "true or false")
    .action(async (resourceValue: string, reference: string, options: { value: string }, command: Command) => {
      const global = globals(command);
      const resource = entityKindForResource(mutableResource(resourceValue));
      if (options.value !== "true" && options.value !== "false") throw new CliError("INVALID_ARGUMENT", "--value must be true or false.");
      const id = entityId(reference);
      const { client } = await clientFor(store, global);
      const result = await client.put<unknown>(`engagement/${resource}/${id}/favorite`, { isFavorite: options.value === "true" });
      print(result, global, () => renderMutation(`Updated favorite for ${resource} ${id}`, result));
    });
  library.command("rate <resource> <id>").description("Set an entity rating").requiredOption("--value <number>", "rating value")
    .action(async (resourceValue: string, reference: string, options: { value: string }, command: Command) => {
      const global = globals(command);
      const resource = entityKindForResource(mutableResource(resourceValue));
      const value = Number(options.value);
      if (!Number.isInteger(value) || value < 0 || value > 100) throw new CliError("INVALID_ARGUMENT", "--value must be an integer from 0 to 100.");
      const id = entityId(reference);
      const { client } = await clientFor(store, global);
      const result = await client.put<unknown>(`engagement/${resource}/${id}/rating`, { value });
      print(result, global, () => renderMutation(`Updated rating for ${resource} ${id}`, result));
    });

  const jobs = program.command("jobs").description("Inspect and control background jobs").helpGroup("Manage:");
  jobs.command("list").description("List active and queued jobs").action(async (_options, command: Command) => {
    const global = globals(command);
    const { client } = await clientFor(store, global);
    const items = await client.get<unknown>("jobs");
    if (!Array.isArray(items) || !items.every(isJob)) throw new CliError("INVALID_RESPONSE", "Cove returned an invalid job list.");
    print({ jobs: items }, global, () => renderJobs(items), items);
  });
  jobs.command("history").description("List completed job history").action(async (_options, command: Command) => {
    const global = globals(command);
    const { client } = await clientFor(store, global);
    const items = await client.get<unknown>("jobs/history");
    if (!Array.isArray(items) || !items.every(isJob)) throw new CliError("INVALID_RESPONSE", "Cove returned an invalid job history.");
    print({ jobs: items }, global, () => renderJobs(items), items);
  });
  jobs.command("show <id>").description("Show one background job").action(async (id: string, _options, command: Command) => {
    const global = globals(command);
    const { client } = await clientFor(store, global);
    const job = await client.get<unknown>(`jobs/${encodeURIComponent(id)}`);
    if (!isJob(job)) throw new CliError("INVALID_RESPONSE", "Cove returned an invalid job.");
    print(job, global, () => renderJobs([job]));
  });
  jobs.command("wait <id>").description("Wait for a job to finish").option("--interval <seconds>", "poll interval in seconds", positiveInteger("--interval", 300), 2)
    .action(async (id: string, options: { interval: number }, command: Command) => {
      const global = globals(command);
      const { client } = await clientFor(store, global);
      let job: JobInfo;
      for (;;) {
        const value = await client.get<unknown>(`jobs/${encodeURIComponent(id)}`);
        if (!isJob(value)) throw new CliError("INVALID_RESPONSE", "Cove returned an invalid job.");
        job = value;
        if (!new Set(["pending", "running"]).has(job.status)) break;
        await new Promise(resolve => setTimeout(resolve, options.interval * 1_000));
      }
      print(job, global, () => renderJobs([job]));
      if (job.status !== "completed") throw new CliError("JOB_FAILED", job.error ?? `Job ended with status ${job.status}.`);
    });
  jobs.command("cancel <id>").description("Cancel a background job").requiredOption("--yes", "confirm cancellation").action(async (id: string, _options, command: Command) => {
    const global = globals(command);
    const { client } = await clientFor(store, global);
    await client.delete(`jobs/${encodeURIComponent(id)}`);
    print({ id, cancelled: true }, global, () => `Cancelled job ${id}`);
  });

  const maintenance = program.command("maintenance").description("Run library maintenance operations").helpGroup("Manage:");
  maintenance.command("scan").description("Start a library scan").option("--generate-previews", "generate previews during the scan")
    .action(async (options: { generatePreviews?: boolean }, command: Command) => startMaintenanceJob(store, command, `jobs/scan?generatePreviews=${options.generatePreviews === true}`, "Started scan"));
  maintenance.command("generate <kind>").description("Generate thumbnails, video phashes, or image phashes")
    .action(async (kind: string, _options, command: Command) => {
      const paths: Record<string, string> = { thumbnails: "generate-thumbnails", "video-phashes": "generate-video-phashes", "image-phashes": "generate-image-phashes" };
      const path = paths[kind];
      if (!path) throw new CliError("INVALID_ARGUMENT", "Generation kind must be thumbnails, video-phashes, or image-phashes.");
      return startMaintenanceJob(store, command, `jobs/${path}`, `Started ${kind}`);
    });
  maintenance.command("clean").description("Start library cleanup").option("--dry-run", "report changes without applying them")
    .action(async (options: { dryRun?: boolean }, command: Command) => startMaintenanceJob(store, command, `jobs/clean?dryRun=${options.dryRun === true}`, "Started cleanup"));
  maintenance.command("backup").description("Start a database backup")
    .action(async (_options, command: Command) => startMaintenanceJob(store, command, "jobs/backup", "Started backup"));
  maintenance.command("stats").description("Show system and library statistics").action(async (_options, command: Command) => {
    const global = globals(command);
    const { client } = await clientFor(store, global);
    const result = await client.get<unknown>("system/stats");
    print(result, global, () => renderMutation("Cove statistics", result));
  });
  maintenance.command("logs").description("Show recent server logs").option("--level <level>", "filter by log level").option("--limit <number>", "maximum entries", positiveInteger("--limit", 10_000), 200)
    .action(async (options: { level?: string; limit: number }, command: Command) => {
      const global = globals(command);
      const { client } = await clientFor(store, global);
      const query = new URLSearchParams({ limit: String(options.limit), ...(options.level ? { level: options.level } : {}) });
      const result = await client.get<unknown>(`logs?${query}`);
      print(result, global, () => renderMutation("Recent logs", result), Array.isArray(result) ? result : undefined);
    });

  const media = program.command("media").description("Download authenticated media and API files").helpGroup("Manage:");
  media.command("download <kind> <id>").description("Download an original video, image, audio, or text file").requiredOption("--file <path>", "destination file path").option("--force", "replace an existing file")
    .action(async (kindValue: string, reference: string, options: { file: string; force?: boolean }, command: Command) => {
      const global = globals(command);
      if (outputFormat(global) !== "human") throw new CliError("INVALID_ARGUMENT", "Media downloads do not support --output or --json.");
      const id = entityId(reference);
      const paths: Record<string, string> = { video: `stream/video/${id}`, image: `stream/image/${id}`, audio: `audios/${id}/stream`, text: `texts/${id}/file` };
      const path = paths[kindValue.toLowerCase()];
      if (!path) throw new CliError("INVALID_ARGUMENT", "Media kind must be video, image, audio, or text.");
      const { client } = await clientFor(store, global);
      const result = await client.download(path);
      await saveDownload(options.file, result.body, options.force === true);
      process.stdout.write(`Downloaded${result.contentLength === undefined ? "" : ` ${result.contentLength} bytes`} to ${options.file}\n`);
    });
  media.command("fetch <api-path>").description("Download a binary Cove API response").requiredOption("--file <path>", "destination file path").option("--force", "replace an existing file")
    .action(async (path: string, options: { file: string; force?: boolean }, command: Command) => {
      const global = globals(command);
      if (outputFormat(global) !== "human") throw new CliError("INVALID_ARGUMENT", "Media downloads do not support --output or --json.");
      const { client } = await clientFor(store, global);
      const result = await client.download(path);
      await saveDownload(options.file, result.body, options.force === true);
      process.stdout.write(`Downloaded${result.contentLength === undefined ? "" : ` ${result.contentLength} bytes`} to ${options.file}\n`);
    });

  const readFamilies = [
    { command: "tag-groups", path: "taggroups", label: "Tag groups", detail: true },
    { command: "custom-fields", path: "custom-fields", label: "Custom fields", detail: true },
    { command: "bookmarks", path: "me/bookmarks", label: "Bookmarks", detail: false },
    { command: "share-links", path: "share-links", label: "Share links", detail: false },
    { command: "ai-runs", path: "ai-runs", label: "AI runs", detail: true },
    { command: "audit", path: "audit", label: "Audit events", detail: false },
    { command: "users", path: "users", label: "Users", detail: true },
    { command: "roles", path: "roles", label: "Roles", detail: true },
  ];
  for (const family of readFamilies) {
    const parent = program.command(family.command).description(`Browse Cove ${family.label.toLowerCase()}`).helpGroup("Catalog:");
    parent.command("list").description(`List ${family.label.toLowerCase()}`).option("--param <name=value>", "append a server query parameter (repeatable)", queryPair, [])
      .action(async (options: { param: string[] }, command: Command) => {
        const global = globals(command);
        const { client } = await clientFor(store, global);
        const query = new URLSearchParams(options.param.map(pair => {
          const split = pair.indexOf("=");
          return [pair.slice(0, split), pair.slice(split + 1)];
        }));
        const result = await client.get<unknown>(`${family.path}${query.size ? `?${query}` : ""}`);
        print(result, global, () => renderGeneric(family.label, result), genericRecords(result));
      });
    if (family.detail) parent.command("show <id>").description(`Show one ${family.label.toLowerCase().replace(/s$/, "")}`)
      .action(async (reference: string, _options, command: Command) => {
        const global = globals(command);
        const id = entityId(reference);
        const { client } = await clientFor(store, global);
        const result = await client.get<unknown>(`${family.path}/${id}`);
        print(result, global, () => renderGeneric(family.label, result));
      });
  }
  const api = program.command("api").description("Inspect newer or extension REST resources").helpGroup("Help:");
  api.command("get <path>").description("Make an authenticated GET request under /api").option("--param <name=value>", "append a query parameter (repeatable)", queryPair, [])
    .action(async (path: string, options: { param: string[] }, command: Command) => {
      const global = globals(command);
      const { client } = await clientFor(store, global);
      const query = new URLSearchParams(options.param.map(pair => {
        const split = pair.indexOf("=");
        return [pair.slice(0, split), pair.slice(split + 1)];
      }));
      const result = await client.get<unknown>(`${path}${query.size ? `${path.includes("?") ? "&" : "?"}${query}` : ""}`);
      print(result, global, () => renderGeneric("API response", result), genericRecords(result));
    });

  const videoCommands = program.command("videos").description("Browse Cove videos").helpGroup("Explore:");
  const videoList = withEntityFilterOptions(videoCommands.command("list").description("List videos matching filters"), "videos");
  withExamples(videoList, [
    "cove-cli videos list --performer 42 --profile personal",
    "cove-cli videos list --tag \"Featured\" --tag \"Favorite\" --performer \"Example Performer\"",
    "cove-cli videos list --saved-filter \"Recently Added\" --filter-by 'title:excludes=[cyoa]'",
    "cove-cli videos list --filter-by 'title:excludes=[cyoa]' --limit 50 --json",
    "cove-cli videos list --tag 12 --exclude-tag 34 --page 2 --per-page 50 --sort-by date:desc --sort-by title:asc --json",
  ]);
  withSortFields(videoList, [
    "title", "rating", "play_count", "like_counter", "last_like_at", "last_played_at", "play_duration", "resume_time",
    "date", "organized", "duration", "file_size", "file_mod_time", "file_count", "path", "resolution", "framerate",
    "bitrate", "tag_count", "performer_count", "studio", "code", "studio_code", "created_at", "updated_at",
  ]).action(async (options: EntityFilterOptions, command: Command) => {
      const global = globals(command);
      const volume = resultVolume(options);
      const { client } = await clientFor(store, global);
      const criteria = await resolveEntityCriteria(client, options);
      const queryOptions = await queryOptionsForList(client, options, "videos", volume, outputFormat(global) === "human", relationObjectFilter(criteria));
      const result = await videosForCriteria(client, criteria, queryOptions);
      print({ videos: result.items, totalCount: result.totalCount }, global, () => renderVideoResults("Videos", result.items, renderFor(global, listRenderContext(client.server, result.totalCount, options, 25, queryOptions.defaultFilterApplied, queryOptions.appliedFilterSummary))), result.items);
  });

  withExamples(videoCommands.command("show <id>").description("Show one video by ID"), [
    "cove-cli videos show 42 --profile personal",
    "cove-cli videos show 42 --profile personal --json",
  ]).action(async (reference: string, _options, command: Command) => {
    const global = globals(command);
    const id = entityId(reference);
    const { client } = await clientFor(store, global);
    const video = await client.get<Video>(`videos/${id}`);
    if (!isVideoRecord(video)) throw new CliError("INVALID_RESPONSE", "Cove returned an invalid video object.");
    const metadataServers = outputFormat(global) === "human" && hasRemoteIds(video.remoteIds) ? await metadataServersFor(client) : [];
    print(video, global, () => renderVideo(video, renderFor(global, { server: client.server, metadataServers })));
  });
  addSavedFilterCommands(videoCommands, "videos", "video");

  const audioCommands = program.command("audios").description("Browse Cove audios").helpGroup("Explore:");
  const audioList = withEntityFilterOptions(audioCommands.command("list").description("List audios matching tags and performers"), "audios");
  withExamples(audioList, [
    "cove-cli audios list --performer 42 --profile personal",
    "cove-cli audios list --tag \"Featured\" --tag \"Favorite\" --performer \"Example Performer\"",
    "cove-cli audios list --tag 12 --exclude-tag 34 --page 1 --per-page 100 --sort-by date:desc --json",
  ]);
  withSortFields(audioList, [
    "title", "rating", "play_count", "like_counter", "play_duration", "last_played_at", "date", "duration", "file_size",
    "file_mod_time", "file_count", "path", "bitrate", "has_video_files", "track_count", "tag_count", "performer_count",
    "created_at", "updated_at",
  ]).action(async (options: EntityFilterOptions, command: Command) => {
    const global = globals(command);
    const volume = resultVolume(options);
    const { client } = await clientFor(store, global);
    const criteria = await resolveEntityCriteria(client, options);
    const queryOptions = await queryOptionsForList(client, options, "audios", volume, outputFormat(global) === "human", relationObjectFilter(criteria));
    const result = await audiosForCriteria(client, criteria, queryOptions);
    print({ audios: result.items, totalCount: result.totalCount }, global, () => renderAudios(result.items, renderFor(global, listRenderContext(client.server, result.totalCount, options, 25, queryOptions.defaultFilterApplied, queryOptions.appliedFilterSummary))), result.items);
  });

  withExamples(audioCommands.command("show <id>").description("Show one audio by ID"), [
    "cove-cli audios show 42 --profile personal",
    "cove-cli audios show 42 --profile personal --json",
  ]).action(async (reference: string, _options, command: Command) => {
    const global = globals(command);
    const id = entityId(reference);
    const { client } = await clientFor(store, global);
    const audio = await client.get<Audio>(`audios/${id}`);
    if (!isAudioRecord(audio)) throw new CliError("INVALID_RESPONSE", "Cove returned an invalid audio object.");
    print(audio, global, () => renderAudio(audio, renderFor(global, { server: client.server })));
  });
  addSavedFilterCommands(audioCommands, "audios", "audio");

  const imageCommands = program.command("images").description("Browse Cove images").helpGroup("Explore:");
  const imageList = withEntityFilterOptions(imageCommands.command("list").description("List images matching tags and performers"), "images");
  withExamples(imageList, [
    "cove-cli images list --profile personal",
    "cove-cli images list --tag \"Featured\" --exclude-performer 56",
    "cove-cli images list --page 2 --per-page 50 --sort-by date:desc --sort-by title:asc --json",
  ]);
  withSortFields(imageList, [
    "updated_at", "rating", "like_counter", "created_at", "date", "file_mod_time", "file_size", "resolution", "path", "title", "performer_count", "tag_count",
  ]).action(async (options: EntityFilterOptions, command: Command) => {
    const global = globals(command);
    const volume = resultVolume(options);
    const { client } = await clientFor(store, global);
    const criteria = await resolveEntityCriteria(client, options);
    const relationFilter = relationObjectFilter(criteria);
    const queryOptions = await queryOptionsForList(client, options, "images", volume, outputFormat(global) === "human", relationFilter);
    const result = await listEntities<ImageRecord>(client, "images", relationFilter, queryOptions, "image", isImageRecord);
    print({ images: result.items, totalCount: result.totalCount }, global, () => renderImages(result.items, renderFor(global, listRenderContext(client.server, result.totalCount, options, 25, queryOptions.defaultFilterApplied, queryOptions.appliedFilterSummary))), result.items);
  });
  addIdShowCommand<ImageRecord>(store, imageCommands, "image", isImageRecord, renderCatalogDetail, accent);
  addSavedFilterCommands(imageCommands, "images", "image");

  const galleryCommands = program.command("galleries").description("Browse Cove galleries").helpGroup("Explore:");
  const galleryList = withEntityFilterOptions(galleryCommands.command("list").description("List galleries matching tags and performers"), "galleries");
  withExamples(galleryList, [
    "cove-cli galleries list --profile personal",
    "cove-cli galleries list --tag \"Featured\" --exclude-performer 56",
    "cove-cli galleries list --page 2 --per-page 50 --sort-by date:desc --sort-by title:asc --json",
  ]);
  withSortFields(galleryList, [
    "updated_at", "rating", "created_at", "date", "studio", "file_mod_time", "file_count", "path", "title", "code", "photographer", "organized", "image_count", "video_count", "performer_count", "tag_count", "like_counter", "last_like_at",
  ]).action(async (options: EntityFilterOptions, command: Command) => {
    const global = globals(command);
    const volume = resultVolume(options);
    const { client } = await clientFor(store, global);
    const criteria = await resolveEntityCriteria(client, options);
    const relationFilter = relationObjectFilter(criteria);
    const queryOptions = await queryOptionsForList(client, options, "galleries", volume, outputFormat(global) === "human", relationFilter);
    const result = await listEntities<GalleryRecord>(client, "galleries", relationFilter, queryOptions, "gallery", isGalleryRecord);
    print({ galleries: result.items, totalCount: result.totalCount }, global, () => renderGalleries(result.items, renderFor(global, listRenderContext(client.server, result.totalCount, options, 25, queryOptions.defaultFilterApplied, queryOptions.appliedFilterSummary))), result.items);
  });
  addIdShowCommand<GalleryRecord>(store, galleryCommands, "gallery", isGalleryRecord, renderCatalogDetail, accent);
  addSavedFilterCommands(galleryCommands, "galleries", "gallery");

  const tagCommands = program.command("tags").description("Browse Cove tags").helpGroup("Explore:");
  const tagList = withPagingSortOptions(tagCommands.command("list").description("List tags")
    .option("--query <text>", "search tags"), "tags");
  withExamples(tagList, [
    "cove-cli tags list --profile personal",
    "cove-cli tags list --query \"Example\" --sort-by name:asc",
    "cove-cli tags list --page 2 --per-page 50 --sort-by video_count:desc --json",
  ]);
  withSortFields(tagList, ["name", "rating", "video_count", "gallery_count", "group_count", "image_count", "performer_count", "studio_count", "latest_video_date", "total_file_size", "created_at", "updated_at"])
    .action(async (options: CatalogListOptions, command: Command) => {
      const global = globals(command);
      const volume = resultVolume(options);
      const { client } = await clientFor(store, global);
      const queryOptions = await queryOptionsForList(client, options, "tags", volume, outputFormat(global) === "human");
      const result = await listEntities<Tag>(client, "tags", {}, queryOptions, "tag", isTagRecord);
      print({ tags: result.items, totalCount: result.totalCount }, global, () => renderTags(result.items, renderFor(global, listRenderContext(client.server, result.totalCount, options, 25, queryOptions.defaultFilterApplied, queryOptions.appliedFilterSummary))), result.items);
    });
  withExamples(tagCommands.command("show <id-or-name>").description("Show one tag by ID, exact name, or exact alias"), [
    "cove-cli tags show 42 --profile personal",
    "cove-cli tags show \"Featured\" --json",
  ]).action(async (reference: string, _options, command: Command) => {
    const global = globals(command);
    const { client } = await clientFor(store, global);
    const tag = await resolveTag(client, reference);
    print(tag, global, () => renderCatalogDetail(tag, "tag", renderFor(global, { server: client.server })));
  });
  addSavedFilterCommands(tagCommands, "tags", "tag");

  const performerCommands = program.command("performers").description("Browse Cove performers").helpGroup("Explore:");
  const performerList = withTagFilterOptions(performerCommands.command("list").description("List performers"), "performers");
  withExamples(performerList, [
    "cove-cli performers list --profile personal",
    "cove-cli performers list --query \"Example\" --tag \"Featured\" --sort-by name:asc",
    "cove-cli performers list --page 2 --per-page 50 --sort-by video_count:desc --json",
  ]);
  withSortFields(performerList, ["name", "rating", "created_at", "updated_at", "birthdate", "video_count", "image_count", "gallery_count", "latest_video_date", "total_file_size", "height", "weight", "tag_count", "like_counter", "play_count", "last_like_at", "last_played_at"])
    .action(async (options: TagFilterOptions, command: Command) => {
      const global = globals(command);
      const volume = resultVolume(options);
      const { client } = await clientFor(store, global);
      const criteria = await resolveTagCriteria(client, options);
      const relationFilter = relationObjectFilter(criteria);
      const queryOptions = await queryOptionsForList(client, options, "performers", volume, outputFormat(global) === "human", relationFilter);
      const result = await listEntities<Performer>(client, "performers", relationFilter, queryOptions, "performer", isPerformerRecord);
      print({ performers: result.items, totalCount: result.totalCount }, global, () => renderPerformers(result.items, renderFor(global, listRenderContext(client.server, result.totalCount, options, 25, queryOptions.defaultFilterApplied, queryOptions.appliedFilterSummary))), result.items);
    });

  withExamples(performerCommands.command("show <id-or-name>").description("Show one performer by ID, exact name, or exact alias"), [
    "cove-cli performers show 42 --profile personal",
    "cove-cli performers show \"Example Performer\" --profile personal --json",
  ]).action(async (reference: string, _options, command: Command) => {
    const global = globals(command);
    const { client } = await clientFor(store, global);
    const performer = await resolvePerformer(client, reference);
    const metadataServers = outputFormat(global) === "human" && hasRemoteIds(performer.remoteIds) ? await metadataServersFor(client) : [];
    print(performer, global, () => renderPerformer(performer, renderFor(global, { server: client.server, metadataServers })));
  });
  addSavedFilterCommands(performerCommands, "performers", "performer");

  const studioCommands = program.command("studios").description("Browse Cove studios").helpGroup("Explore:");
  const studioList = withTagFilterOptions(studioCommands.command("list").description("List studios"), "studios");
  withExamples(studioList, [
    "cove-cli studios list --profile personal",
    "cove-cli studios list --query \"Example\" --tag \"Featured\" --sort-by name:asc",
    "cove-cli studios list --page 2 --per-page 50 --sort-by video_count:desc --json",
  ]);
  withSortFields(studioList, ["name", "rating", "video_count", "gallery_count", "image_count", "latest_video_date", "total_file_size", "parent_count", "child_count", "tag_count", "created_at", "updated_at"])
    .action(async (options: TagFilterOptions, command: Command) => {
      const global = globals(command);
      const volume = resultVolume(options);
      const { client } = await clientFor(store, global);
      const criteria = await resolveTagCriteria(client, options);
      const relationFilter = relationObjectFilter(criteria);
      const queryOptions = await queryOptionsForList(client, options, "studios", volume, outputFormat(global) === "human", relationFilter);
      const result = await listEntities<StudioRecord>(client, "studios", relationFilter, queryOptions, "studio", isStudioRecord);
      print({ studios: result.items, totalCount: result.totalCount }, global, () => renderStudios(result.items, renderFor(global, listRenderContext(client.server, result.totalCount, options, 25, queryOptions.defaultFilterApplied, queryOptions.appliedFilterSummary))), result.items);
    });
  addIdShowCommand<StudioRecord>(store, studioCommands, "studio", isStudioRecord, renderCatalogDetail, accent);
  addSavedFilterCommands(studioCommands, "studios", "studio");

  const groupCommands = program.command("groups").description("Browse Cove groups").helpGroup("Explore:");
  const groupList = withPagingSortOptions(groupCommands.command("list").description("List groups").option("--query <text>", "search groups"), "groups");
  withExamples(groupList, ["cove-cli groups list --query \"Example\"", "cove-cli groups list --page 2 --per-page 50 --json"])
  withSortFields(groupList, ["name", "rating", "duration", "item_count", "created_at", "updated_at", "random"])
    .action(async (options: CatalogListOptions, command: Command) => {
      const global = globals(command);
      const volume = resultVolume(options);
      const { client } = await clientFor(store, global);
      const queryOptions = await queryOptionsForList(client, options, "groups", volume, outputFormat(global) === "human");
      const result = await listEntities<GroupRecord>(client, "groups", {}, queryOptions, "group", isGroupRecord);
      print({ groups: result.items, totalCount: result.totalCount }, global, () => renderGroups(result.items, renderFor(global, listRenderContext(client.server, result.totalCount, options, 25, queryOptions.defaultFilterApplied, queryOptions.appliedFilterSummary))), result.items);
    });
  addIdShowCommand<GroupRecord>(store, groupCommands, "group", isGroupRecord, renderCatalogDetail, accent);
  addSavedFilterCommands(groupCommands, "groups", "group");

  const textCommands = program.command("texts").description("Browse Cove texts").helpGroup("Explore:");
  const textList = withPagingSortOptions(textCommands.command("list").description("List text documents").option("--query <text>", "search texts"), "texts");
  withExamples(textList, ["cove-cli texts list --query \"Example\"", "cove-cli texts list --sort-by words:desc --json"]);
  withSortFields(textList, ["title", "date", "words", "pages", "rating", "read_count", "like_counter", "file_size", "file_count", "path", "tag_count", "performer_count", "created_at", "updated_at"])
    .action(async (options: CatalogListOptions, command: Command) => {
      const global = globals(command);
      const volume = resultVolume(options);
      const { client } = await clientFor(store, global);
      const queryOptions = await queryOptionsForList(client, options, "texts", volume, outputFormat(global) === "human");
      const result = await listEntities<TextRecord>(client, "texts", {}, queryOptions, "text", isTextRecord);
      print({ texts: result.items, totalCount: result.totalCount }, global, () => renderTexts(result.items, renderFor(global, listRenderContext(client.server, result.totalCount, options, 25, queryOptions.defaultFilterApplied, queryOptions.appliedFilterSummary))), result.items);
    });
  addIdShowCommand<TextRecord>(store, textCommands, "text", isTextRecord, renderCatalogDetail, accent);
  addSavedFilterCommands(textCommands, "texts", "text");

  const segmentCommands = program.command("segments").description("Browse Cove segments").helpGroup("Explore:");
  const segmentList = withSingleSortOption(withPagingOptions(segmentCommands.command("list").description("List video segments")
    .option("--query <text>", "search segments")
    .option("--video <id>", "filter by video ID", entityId)
    .option("--tag <id>", "filter by tag ID", entityId)
    .option("--kind <kind>", "filter by segment kind")
    .option("--source-key <key>", "filter by source key"), 48));
  withExamples(segmentList, ["cove-cli segments list --video 42", "cove-cli segments list --kind face --sort-by confidence:desc --json"]);
  withSortFields(segmentList, ["updated_at", "created_at", "start_sec", "end_sec", "duration", "confidence", "title", "video_title", "kind", "source_key", "tag_name", "performer", "ref"], "Sort fields", false)
    .action(async (options: Omit<CatalogListOptions, "sortBy"> & { video?: number; tag?: number; kind?: string; sourceKey?: string; sortBy?: { key: string; direction: "asc" | "desc" } }, command: Command) => {
      const global = globals(command);
      const volume = resultVolume(options);
      const { client } = await clientFor(store, global);
      const result = await listSegments(client, { query: options.query, videoId: options.video, tagId: options.tag, kind: options.kind, sourceKey: options.sourceKey, ...volume, sort: options.sortBy });
      print({ segments: result.items, totalCount: result.totalCount }, global, () => renderSegments(result.items, renderFor(global, listRenderContext(client.server, result.totalCount, options, 48))), result.items);
    });
  addIdShowCommand<SegmentRecord>(store, segmentCommands, "segment", isSegmentRecord, renderCatalogDetail, accent);

  withExamples(program.command("completion <shell>")
    .description("Generate shell completion for bash, zsh, or fish")
    .helpGroup("Help:"), [
    "cove-cli completion bash > ~/.local/share/bash-completion/completions/cove-cli",
    "cove-cli completion zsh > ~/.zfunc/_cove-cli",
    "cove-cli completion fish > ~/.config/fish/completions/cove-cli.fish",
  ]).action((shellValue: string, _options, command: Command) => {
    const global = globals(command);
    const shell = parseCompletionShell(shellValue);
    const script = renderCompletion(program, shell);
    print({ shell, script }, global, () => script.trimEnd());
  });

  program.command("help [command...]")
    .description("Show detailed help for a command")
    .helpGroup("Help:")
    .action((path: string[] | undefined) => {
      const target = resolveHelpTarget(program, path ?? []);
      target.outputHelp();
      const details = extendedHelp.get(target);
      if (details) process.stdout.write(`\n${details.heading === "Sort fields" ? "All sort fields" : details.heading}:\n${formatFieldList(details.fields)}\n`);
    });
  program.addHelpText("after", "\nRun `cove-cli help <command>` for detailed help.");
  disableImplicitHelpCommands(program);
  configureCliHelp(program, helpColor ? accent : false);
  return program;
}

function disableImplicitHelpCommands(command: Command): void {
  command.helpCommand(false);
  command.commands.forEach(disableImplicitHelpCommands);
}

function resolveHelpTarget(root: Command, path: string[]): Command {
  let target = root;
  for (const segment of path) {
    const next = target.commands.find(command => command.name() === segment || command.aliases().includes(segment));
    if (!next || next.name() === "help") {
      const suggestion = closestCommand(segment, target);
      throw new CliError("INVALID_ARGUMENT", `No command matches “${path.join(" ")}”.`, suggestion ? { details: { suggestion } } : undefined);
    }
    target = next;
  }
  return target;
}

function closestCommand(value: string, parent: Command): string | undefined {
  const maximumDistance = Math.max(1, Math.min(3, Math.floor(value.length / 3)));
  const candidates = parent.commands
    .filter(command => command.name() !== "help")
    .flatMap(command => [command.name(), ...command.aliases()])
    .filter(candidate => Math.abs(candidate.length - value.length) <= maximumDistance);
  const ranked = candidates
    .map(candidate => ({ candidate, distance: editDistance(value.toLowerCase(), candidate.toLowerCase()) }))
    .sort((left, right) => left.distance - right.distance || left.candidate.localeCompare(right.candidate));
  const closest = ranked[0];
  return closest && closest.distance <= maximumDistance ? closest.candidate : undefined;
}

function editDistance(left: string, right: string): number {
  const distances = Array.from({ length: left.length + 1 }, (_, row) =>
    Array.from({ length: right.length + 1 }, (_, column) => row === 0 ? column : column === 0 ? row : 0));
  for (let row = 1; row <= left.length; row += 1) {
    for (let column = 1; column <= right.length; column += 1) {
      const substitution = distances[row - 1]![column - 1]! + (left[row - 1] === right[column - 1] ? 0 : 1);
      distances[row]![column] = Math.min(distances[row - 1]![column]! + 1, distances[row]![column - 1]! + 1, substitution);
      if (row > 1 && column > 1 && left[row - 1] === right[column - 2] && left[row - 2] === right[column - 1]) {
        distances[row]![column] = Math.min(distances[row]![column]!, distances[row - 2]![column - 2]! + 1);
      }
    }
  }
  return distances[left.length]![right.length]!;
}

function formatFieldList(fields: string[], terminalWidth = process.stdout.columns ?? 100): string {
  const width = Math.max(30, terminalWidth);
  const lines: string[] = [];
  let line = "  ";
  for (const [index, field] of fields.entries()) {
    const token = `${line === "  " ? "" : " "}${field}${index === fields.length - 1 ? "" : ","}`;
    if (line.length + token.length > width && line !== "  ") {
      lines.push(line);
      line = `  ${field}${index === fields.length - 1 ? "" : ","}`;
    } else {
      line += token;
    }
  }
  if (line !== "  ") lines.push(line);
  return lines.join("\n");
}

function addIdShowCommand<T extends { id: number }>(store: ConfigStore, parent: Command, resource: CatalogEntityKind, validate: (value: unknown) => value is T, renderer: (item: T, kind: CatalogEntityKind, context?: RenderContext) => string, accent: string): void {
  withExamples(parent.command("show <id>").description(`Show one ${resource} by ID`), [
    `cove-cli ${parent.name()} show 42 --profile personal`,
    `cove-cli ${parent.name()} show 42 --json`,
  ]).action(async (reference: string, _options, command: Command) => {
    const global = globals(command);
    const id = entityId(reference);
    const { client } = await clientFor(store, global);
    const item = await client.get<T>(`${parent.name()}/${id}`);
    if (!validate(item)) throw new CliError("INVALID_RESPONSE", `Cove returned an invalid ${resource} object.`);
    print(item, global, () => renderer(item, resource, presentationFor(global, accent, { server: client.server })));
  });
}

function isImageRecord(value: unknown): value is ImageRecord {
  const item = value as Partial<ImageRecord> | undefined;
  return !!item && typeof item.id === "number" && namedRecords(item.performers) && records(item.files)
    && optionalNamedRecords(item.tags) && optionalRecords(item.galleries) && optionalRecords(item.groups);
}

function isVideoRecord(value: unknown): value is Video {
  const item = value as Partial<Video> | undefined;
  return !!item && typeof item.id === "number"
    && Array.isArray(item.performers) && item.performers.every(isNamedRecord)
    && Array.isArray(item.files) && item.files.every(isRecord)
    && (item.tags === undefined || (Array.isArray(item.tags) && item.tags.every(tag => isNamedRecord(tag) && typeof tag.id === "number")));
}

function isSimilarityDistance(value: unknown): value is number {
  return typeof value === "number" && Number.isFinite(value);
}

function isOptionalTime(value: unknown): value is number | undefined {
  return value === undefined || isSimilarityDistance(value);
}

function isSimilarVideoResponse(value: unknown): value is { items: SimilarVideoResult[] } {
  return isRecord(value) && Array.isArray(value.items) && value.items.every(result => isRecord(result)
    && isVideoRecord(result.video)
    && isSimilarityDistance(result.distance)
    && Number.isInteger(result.sectionIndex) && (result.sectionIndex as number) >= 0
    && isOptionalTime(result.startSec) && isOptionalTime(result.endSec));
}

function isSimilarImageResponse(value: unknown): value is { items: SimilarImageResult[] } {
  return isRecord(value) && Array.isArray(value.items) && value.items.every(result => isRecord(result)
    && isImageRecord(result.image)
    && isSimilarityDistance(result.distance));
}

function isAudioRecord(value: unknown): value is Audio {
  const item = value as Partial<Audio> | undefined;
  return !!item && typeof item.id === "number"
    && Array.isArray(item.performers) && item.performers.every(isNamedRecord)
    && Array.isArray(item.files) && item.files.every(isRecord)
    && Array.isArray(item.tracks) && item.tracks.every(isRecord)
    && (item.tags === undefined || (Array.isArray(item.tags) && item.tags.every(tag => isNamedRecord(tag) && typeof tag.id === "number")));
}

function isRecord(value: unknown): value is Record<string, unknown> {
  return !!value && typeof value === "object" && !Array.isArray(value);
}

function isNamedRecord(value: unknown): value is Record<string, unknown> & { name: string } {
  return isRecord(value) && typeof value.name === "string";
}

function isGalleryRecord(value: unknown): value is GalleryRecord {
  const item = value as Partial<GalleryRecord> | undefined;
  return !!item && typeof item.id === "number" && namedRecords(item.performers) && records(item.files) && optionalNamedRecords(item.tags);
}

function isTagRecord(value: unknown): value is Tag {
  const item = value as Partial<Tag> | undefined;
  return !!item && typeof item.id === "number" && typeof item.name === "string" && strings(item.aliases) && optionalNamedRecords(item.tags);
}

function isPerformerRecord(value: unknown): value is Performer {
  const item = value as Partial<Performer> | undefined;
  return !!item && typeof item.id === "number" && typeof item.name === "string" && Array.isArray(item.aliases);
}

function isStudioRecord(value: unknown): value is StudioRecord {
  const item = value as Partial<StudioRecord> | undefined;
  return !!item && typeof item.id === "number" && typeof item.name === "string" && strings(item.aliases) && optionalNamedRecords(item.tags);
}

function isGroupRecord(value: unknown): value is GroupRecord {
  const item = value as Partial<GroupRecord> | undefined;
  return !!item && typeof item.id === "number" && typeof item.name === "string" && namedRecords(item.tags);
}

function isTextRecord(value: unknown): value is TextRecord {
  const item = value as Partial<TextRecord> | undefined;
  return !!item && typeof item.id === "number" && namedRecords(item.performers) && namedRecords(item.tags) && records(item.groups) && records(item.files);
}

function records(value: unknown): value is Record<string, unknown>[] {
  return Array.isArray(value) && value.every(isRecord);
}

function optionalRecords(value: unknown): boolean {
  return value === undefined || records(value);
}

function namedRecords(value: unknown): value is Array<Record<string, unknown> & { name: string }> {
  return Array.isArray(value) && value.every(isNamedRecord);
}

function optionalNamedRecords(value: unknown): boolean {
  return value === undefined || namedRecords(value);
}

function strings(value: unknown): value is string[] {
  return Array.isArray(value) && value.every(item => typeof item === "string");
}

function isGlobalSearchResponse(value: unknown): value is GlobalSearchResponse {
  const result = value as Partial<GlobalSearchResponse> | undefined;
  return !!result && Array.isArray(result.failedTypes) && result.failedTypes.every(item => typeof item === "string")
    && Array.isArray(result.groups) && result.groups.every(group => typeof group?.type === "string" && Array.isArray(group.items)
      && group.items.every(item => typeof item?.id === "number" && typeof item.title === "string" && (item.subtitle === undefined || item.subtitle === null || typeof item.subtitle === "string")));
}

function credentialsEqual(left: StoredProfile["credential"], right: StoredProfile["credential"]): boolean {
  if (!left || !right || left.type !== right.type) return false;
  return left.type === "apiToken" && right.type === "apiToken"
    ? left.token === right.token
    : left.type === "session" && right.type === "session"
      && left.accessToken === right.accessToken && left.refreshToken === right.refreshToken;
}

function silenceCommanderErrors(command: Command): void {
  command.configureOutput({ ...command.configureOutput(), writeErr: () => undefined });
  command.commands.forEach(silenceCommanderErrors);
}

function bareGroupTarget(program: Command, argv: string[]): Command | undefined {
  if (argv.includes("--version") || argv.includes("-V")) return undefined;
  let target = program;
  let unmatched = false;
  const options: GlobalOptions = {};
  const args = argv.slice(2);
  for (let index = 0; index < args.length; index += 1) {
    const value = args[index]!;
    if (value === "--profile" || value === "--server") {
      if (args[index + 1] === undefined || args[index + 1]!.startsWith("-")) return undefined;
      index += 1;
      continue;
    }
    if (value === "--output" || value === "-o") {
      if (args[index + 1] === undefined || args[index + 1]!.startsWith("-")) return undefined;
      options.output = parseOutputFormat(args[index + 1]!);
      index += 1;
      continue;
    }
    if (value === "--hyperlinks") {
      if (args[index + 1] === undefined || args[index + 1]!.startsWith("-")) return undefined;
      options.hyperlinks = parseHyperlinkMode(args[index + 1]!);
      index += 1;
      continue;
    }
    if (value.startsWith("--hyperlinks=")) {
      options.hyperlinks = parseHyperlinkMode(value.slice("--hyperlinks=".length));
      continue;
    }
    if (value.startsWith("--output=") || /^-o.+/.test(value)) {
      options.output = parseOutputFormat(value.replace(/^(?:--output=|-o=?)/, ""));
      continue;
    }
    if (value === "--json") {
      options.json = true;
      continue;
    }
    if (value.startsWith("--profile=") || value.startsWith("--server=") || value === "--no-color" || value === "--help" || value === "-h") continue;
    if (value.startsWith("-")) return undefined;
    const next = target.commands.find(command => command.name() === value || command.aliases().includes(value));
    if (!next) {
      unmatched = true;
      break;
    }
    target = next;
  }
  outputFormat(options);
  return !unmatched && target.commands.length > 0 ? target : undefined;
}

function helpPathFor(program: Command, argv: string[]): string {
  let target = program;
  const args = argv.slice(2);
  for (let index = 0; index < args.length; index += 1) {
    const value = args[index]!;
    if (value === "--profile" || value === "--server" || value === "--output" || value === "-o" || value === "--hyperlinks") {
      index += 1;
      continue;
    }
    if (value.startsWith("-")) continue;
    const next = target.commands.find(command => command.name() === value || command.aliases().includes(value));
    if (!next) break;
    if (next.name() === "help") continue;
    target = next;
  }
  return `${commandInvocation(target)} --help`;
}

function commanderCliError(error: CommanderError, help: string): CliError {
  const lines = error.message.split(/\r?\n/).map(cleanInline).filter(Boolean);
  const message = (lines[0] ?? "Invalid command usage.").replace(/^error:\s*/i, "");
  const suggestionLine = lines.find(line => /^\(?did you mean .+\?\)?$/i.test(line));
  const suggestion = suggestionLine?.replace(/^\(?did you mean\s+/i, "").replace(/\?\)?$/, "");
  return new CliError("INVALID_ARGUMENT", message, { details: { ...(suggestion ? { suggestion } : {}), help } });
}

function renderHumanError(error: CliError, color: UiColor): string {
  const paint = uiPalette(color);
  const details = error.details && typeof error.details === "object" && !Array.isArray(error.details) ? error.details as Record<string, unknown> : {};
  const suggestion = typeof details.suggestion === "string" ? cleanInline(details.suggestion) : undefined;
  const help = typeof details.help === "string" ? cleanInline(details.help) : undefined;
  const lines = [`${paint.error("error:")} ${cleanInline(error.message)}`];
  if (suggestion || help) lines.push("");
  if (suggestion) lines.push(`  ${paint.accent("tip:")} did you mean ${suggestion}?`);
  if (help) lines.push(`  ${paint.dim("help:")} run \`${help}\` for usage.`);
  return `${lines.join("\n")}\n`;
}

function isUsageError(error: CliError): boolean {
  return error.code === "INVALID_ARGUMENT" || error.code === "FILTER_REQUIRED" || error.code === "FILTER_CONFLICT";
}

function withUsageHelp(error: CliError, help: string): CliError {
  if (!isUsageError(error)) return error;
  const existingDetails = error.details && typeof error.details === "object" && !Array.isArray(error.details)
    ? error.details as Record<string, unknown>
    : {};
  return new CliError(error.code, error.message, { status: error.status, details: { ...existingDetails, help } });
}

function requestedMachineOutput(argv: string[]): "json" | "jsonl" | undefined {
  let alias = false;
  let format: OutputFormat | undefined;
  for (let index = 2; index < argv.length; index += 1) {
    const value = argv[index]!;
    if (value === "--") break;
    if (value === "--json") {
      alias = true;
      continue;
    }
    if (value === "--output" || value === "-o") {
      const candidate = argv[index + 1];
      if (candidate === "human" || candidate === "json" || candidate === "jsonl") format = candidate;
      else format = undefined;
      index += 1;
      continue;
    }
    const match = /^(?:--output=|-o=?)(human|jsonl?)$/.exec(value);
    if (match) format = match[1] as OutputFormat;
  }
  if (alias) return "json";
  return format === "json" || format === "jsonl" ? format : undefined;
}

function invocationCommand(argv: string[]): string | undefined {
  for (let index = 2; index < argv.length; index += 1) {
    const value = argv[index]!;
    if (value === "--") return undefined;
    if (value === "--profile" || value === "--server" || value === "--output" || value === "-o" || value === "--hyperlinks") {
      index += 1;
      continue;
    }
    if (value.startsWith("-")) continue;
    return value;
  }
  return undefined;
}

export function invocationNeedsTheme(argv: string[], colorAvailable: boolean): boolean {
  if (!colorAvailable || requestedMachineOutput(argv)) return false;
  for (const value of argv.slice(2)) {
    if (value === "--") break;
    if (value === "--version" || value === "-V") return false;
  }
  return invocationCommand(argv) !== "completion";
}

function invocationProfileOptions(argv: string[]): Pick<GlobalOptions, "profile" | "server"> {
  const options: Pick<GlobalOptions, "profile" | "server"> = {};
  for (let index = 2; index < argv.length; index += 1) {
    const value = argv[index]!;
    if (value === "--") break;
    for (const key of ["profile", "server"] as const) {
      if (value === `--${key}` && argv[index + 1]) {
        options[key] = argv[index + 1];
        index += 1;
        break;
      }
      if (value.startsWith(`--${key}=`)) options[key] = value.slice(key.length + 3);
    }
  }
  return options;
}

async function accentForInvocation(store: ConfigStore, argv: string[]): Promise<string> {
  try {
    const config = await store.load();
    const resolved = resolveProfile(config, invocationProfileOptions(argv));
    if (!resolved.profile.server.startsWith("https://")) return DEFAULT_ACCENT;
    const client = new CoveClient({
      store,
      profileName: resolved.name,
      profile: resolved.profile,
      transientCredential: resolved.transientCredential,
      timeoutMs: BEST_EFFORT_TIMEOUT_MS,
    });
    return await fetchThemeAccent(client);
  } catch {
    return DEFAULT_ACCENT;
  }
}

export async function main(argv = process.argv): Promise<number> {
  const machineOutput = requestedMachineOutput(argv);
  const colorRequested = !argv.includes("--no-color");
  const stdoutColor = !machineOutput && terminalColorsEnabled(colorRequested);
  const stderrColor = !machineOutput && terminalColorsEnabled(colorRequested, process.stderr);
  const store = new ConfigStore();
  const accent = invocationNeedsTheme(argv, stdoutColor || stderrColor) ? await accentForInvocation(store, argv) : DEFAULT_ACCENT;
  const program = createProgram(store, stdoutColor, accent);
  silenceCommanderErrors(program);
  try {
    const bareGroup = bareGroupTarget(program, argv);
    if (bareGroup) {
      bareGroup.outputHelp();
      return 0;
    }
    await program.parseAsync(argv);
    return 0;
  } catch (error) {
    if (error instanceof CommanderError && (error.code === "commander.help" || error.code === "commander.helpDisplayed" || error.code === "commander.version")) return 0;
    const help = helpPathFor(program, argv);
    const cliError = withUsageHelp(error instanceof CommanderError ? commanderCliError(error, help) : toCliError(error), help);
    if (machineOutput) process.stderr.write(`${JSON.stringify({ error: cliError.toJSON() }, null, machineOutput === "json" ? 2 : undefined)}\n`);
    else process.stderr.write(renderHumanError(cliError, stderrColor ? accent : false));
    return error instanceof CommanderError || isUsageError(cliError) ? 2 : 1;
  }
}

if (import.meta.main) process.exitCode = await main();
