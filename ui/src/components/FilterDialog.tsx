import { useState, useMemo, useCallback, useEffect, type ReactNode } from "react";
import { useQuery } from "@tanstack/react-query";
import { X, ChevronDown, ChevronRight, Search, Pin, PinOff, Plus, Minus, Star } from "lucide-react";
import { tags as tagsApi, performers as performersApi, studios as studiosApi, groups as groupsApi } from "../api/client";
import { useAppConfig } from "../state/AppConfigContext";
import {
  normalizeRatingOptions,
  defaultRatingSystemOptions,
  convertToRatingFormat,
  convertFromRatingFormat,
  getRatingMax,
  getRatingStep,
  getRatingPrecision,
} from "./Rating";
import type {
  CriterionModifier,
  IntCriterion,
  StringCriterion,
  BoolCriterion,
  MultiIdCriterion,
  DateCriterion,
  TimestampCriterion,
  SceneFilterCriteria,
  PerformerFilterCriteria,
  TagFilterCriteria,
  StudioFilterCriteria,
  GalleryFilterCriteria,
  ImageFilterCriteria,
  GroupFilterCriteria,
} from "../api/types";

// ===== Criterion definitions =====

export type CriterionType = "string" | "number" | "bool" | "date" | "timestamp" | "duration" | "rating" | "resolution" | "multiId" | "enum";
export type EntityType = "tags" | "performers" | "studios" | "groups" | "galleries" | "scenes";

export interface CriterionDefinition<TFilterKey extends string = string> {
  id: string;
  label: string;
  type: CriterionType;
  entityType?: EntityType;
  filterKey: TFilterKey;
  options?: { value: string; label: string }[];
}

type CriteriaDefinitionList<TFilterCriteria> = CriterionDefinition<Extract<keyof TFilterCriteria, string>>[];

// Modifier labels
const MODIFIER_LABELS: Record<CriterionModifier, string> = {
  EQUALS: "=",
  NOT_EQUALS: "≠",
  GREATER_THAN: ">",
  LESS_THAN: "<",
  INCLUDES: "Includes",
  EXCLUDES: "Excludes",
  INCLUDES_ALL: "Includes All",
  EXCLUDES_ALL: "Excludes All",
  IS_NULL: "Is Null",
  NOT_NULL: "Not Null",
  BETWEEN: "Between",
  NOT_BETWEEN: "Not Between",
  MATCHES_REGEX: "Regex",
  NOT_MATCHES_REGEX: "Not Regex",
};

// Which modifiers each type supports
const TYPE_MODIFIERS: Record<CriterionType, CriterionModifier[]> = {
  string: ["EQUALS", "NOT_EQUALS", "INCLUDES", "EXCLUDES", "MATCHES_REGEX", "NOT_MATCHES_REGEX", "IS_NULL", "NOT_NULL"],
  number: ["EQUALS", "NOT_EQUALS", "GREATER_THAN", "LESS_THAN", "BETWEEN", "NOT_BETWEEN", "IS_NULL", "NOT_NULL"],
  bool: ["EQUALS"],
  date: ["EQUALS", "NOT_EQUALS", "GREATER_THAN", "LESS_THAN", "BETWEEN", "NOT_BETWEEN", "IS_NULL", "NOT_NULL"],
  timestamp: ["EQUALS", "NOT_EQUALS", "GREATER_THAN", "LESS_THAN", "BETWEEN", "NOT_BETWEEN", "IS_NULL", "NOT_NULL"],
  duration: ["EQUALS", "NOT_EQUALS", "GREATER_THAN", "LESS_THAN", "BETWEEN", "NOT_BETWEEN"],
  rating: ["EQUALS", "NOT_EQUALS", "GREATER_THAN", "LESS_THAN", "BETWEEN", "NOT_BETWEEN", "IS_NULL", "NOT_NULL"],
  resolution: ["EQUALS", "NOT_EQUALS", "GREATER_THAN", "LESS_THAN"],
  multiId: ["INCLUDES", "INCLUDES_ALL", "EXCLUDES", "EXCLUDES_ALL"],
  enum: ["EQUALS", "NOT_EQUALS", "IS_NULL", "NOT_NULL"],
};

// Scene criterion definitions
export const SCENE_CRITERIA: CriteriaDefinitionList<SceneFilterCriteria> = [
  { id: "title", label: "Title", type: "string", filterKey: "titleCriterion" },
  { id: "code", label: "Scene Code", type: "string", filterKey: "codeCriterion" },
  { id: "details", label: "Details", type: "string", filterKey: "detailsCriterion" },
  { id: "director", label: "Director", type: "string", filterKey: "directorCriterion" },
  { id: "path", label: "Path", type: "string", filterKey: "pathCriterion" },
  { id: "rating", label: "Rating", type: "rating", filterKey: "ratingCriterion" },
  { id: "oCounter", label: "O-Counter", type: "number", filterKey: "oCounterCriterion" },
  { id: "organized", label: "Organized", type: "bool", filterKey: "organizedCriterion" },
  { id: "duration", label: "Duration", type: "duration", filterKey: "durationCriterion" },
  { id: "resolution", label: "Resolution", type: "resolution", filterKey: "resolutionCriterion" },
  { id: "playCount", label: "Play Count", type: "number", filterKey: "playCountCriterion" },
  { id: "performerCount", label: "Performer Count", type: "number", filterKey: "performerCountCriterion" },
  { id: "tagCount", label: "Tag Count", type: "number", filterKey: "tagCountCriterion" },
  { id: "hasMarkers", label: "Has Markers", type: "bool", filterKey: "hasMarkersCriterion" },
  { id: "isMissing", label: "Is Missing", type: "bool", filterKey: "isMissingCriterion" },
  { id: "interactive", label: "Interactive", type: "bool", filterKey: "interactiveCriterion" },
  { id: "tags", label: "Tags", type: "multiId", entityType: "tags", filterKey: "tagsCriterion" },
  { id: "performers", label: "Performers", type: "multiId", entityType: "performers", filterKey: "performersCriterion" },
  { id: "studios", label: "Studios", type: "multiId", entityType: "studios", filterKey: "studiosCriterion" },
  { id: "groups", label: "Groups", type: "multiId", entityType: "groups", filterKey: "groupsCriterion" },
  { id: "galleries", label: "Galleries", type: "multiId", entityType: "galleries", filterKey: "galleriesCriterion" },
  { id: "url", label: "URL", type: "string", filterKey: "urlCriterion" },
  { id: "remoteId", label: "Remote ID", type: "string", filterKey: "remoteIdCriterion" },
  { id: "date", label: "Date", type: "date", filterKey: "dateCriterion" },
  { id: "videoCodec", label: "Video Codec", type: "string", filterKey: "videoCodecCriterion" },
  { id: "audioCodec", label: "Audio Codec", type: "string", filterKey: "audioCodecCriterion" },
  { id: "frameRate", label: "Frame Rate", type: "number", filterKey: "frameRateCriterion" },
  { id: "bitrate", label: "Bitrate", type: "number", filterKey: "bitrateInterval" },
  { id: "fileCount", label: "File Count", type: "number", filterKey: "fileCountCriterion" },
  { id: "performerFavorite", label: "Performer Favorite", type: "bool", filterKey: "performerFavoriteCriterion" },
  { id: "resumeTime", label: "Resume Time", type: "number", filterKey: "resumeTimeCriterion" },
  { id: "playDuration", label: "Play Duration", type: "duration", filterKey: "playDurationCriterion" },
  { id: "lastPlayedAt", label: "Last Played", type: "timestamp", filterKey: "lastPlayedAtCriterion" },
  { id: "createdAt", label: "Created At", type: "timestamp", filterKey: "createdAtCriterion" },
  { id: "updatedAt", label: "Updated At", type: "timestamp", filterKey: "updatedAtCriterion" },
  { id: "performerTags", label: "Performer Tags", type: "multiId", entityType: "tags", filterKey: "performerTagsCriterion" },
  { id: "performerAge", label: "Performer Age", type: "number", filterKey: "performerAgeCriterion" },
  { id: "captions", label: "Captions", type: "string", filterKey: "captionsCriterion" },
  { id: "interactiveSpeed", label: "Interactive Speed", type: "number", filterKey: "interactiveSpeedCriterion" },
];

export const PERFORMER_CRITERIA: CriteriaDefinitionList<PerformerFilterCriteria> = [
  { id: "rating", label: "Rating", type: "rating", filterKey: "ratingCriterion" },
  { id: "favorite", label: "Favorite", type: "bool", filterKey: "favoriteCriterion" },
  { id: "age", label: "Age", type: "number", filterKey: "ageCriterion" },
  { id: "gender", label: "Gender", type: "enum", filterKey: "genderCriterion", options: [
    { value: "Male", label: "Male" },
    { value: "Female", label: "Female" },
    { value: "TransgenderMale", label: "Transgender Male" },
    { value: "TransgenderFemale", label: "Transgender Female" },
    { value: "Intersex", label: "Intersex" },
    { value: "NonBinary", label: "Non-Binary" },
  ] },
  { id: "ethnicity", label: "Ethnicity", type: "string", filterKey: "ethnicityCriterion" },
  { id: "country", label: "Country", type: "string", filterKey: "countryCriterion" },
  { id: "tags", label: "Tags", type: "multiId", entityType: "tags", filterKey: "tagsCriterion" },
  { id: "studios", label: "Studios", type: "multiId", entityType: "studios", filterKey: "studiosCriterion" },
  { id: "sceneCount", label: "Scene Count", type: "number", filterKey: "sceneCountCriterion" },
  { id: "imageCount", label: "Image Count", type: "number", filterKey: "imageCountCriterion" },
  { id: "galleryCount", label: "Gallery Count", type: "number", filterKey: "galleryCountCriterion" },
  { id: "birthdate", label: "Birthdate", type: "date", filterKey: "birthdateCriterion" },
  { id: "height", label: "Height (cm)", type: "number", filterKey: "heightCriterion" },
  { id: "weight", label: "Weight", type: "number", filterKey: "weightCriterion" },
  { id: "remoteId", label: "Remote ID", type: "string", filterKey: "remoteIdCriterion" },
  { id: "path", label: "Path", type: "string", filterKey: "pathCriterion" },
  { id: "url", label: "URL", type: "string", filterKey: "urlCriterion" },
  { id: "createdAt", label: "Created At", type: "timestamp", filterKey: "createdAtCriterion" },
  { id: "updatedAt", label: "Updated At", type: "timestamp", filterKey: "updatedAtCriterion" },
  { id: "disambiguation", label: "Disambiguation", type: "string", filterKey: "disambiguationCriterion" },
  { id: "details", label: "Details", type: "string", filterKey: "detailsCriterion" },
  { id: "eyeColor", label: "Eye Color", type: "string", filterKey: "eyeColorCriterion" },
  { id: "hairColor", label: "Hair Color", type: "string", filterKey: "hairColorCriterion" },
  { id: "measurements", label: "Measurements", type: "string", filterKey: "measurementsCriterion" },
  { id: "fakeTits", label: "Fake Tits", type: "string", filterKey: "fakeTitsCriterion" },
  { id: "penisLength", label: "Penis Length", type: "number", filterKey: "penisLengthCriterion" },
  { id: "circumcised", label: "Circumcised", type: "enum", filterKey: "circumcisedCriterion", options: [
    { value: "Cut", label: "Cut" },
    { value: "Uncut", label: "Uncut" },
  ] },
  { id: "careerStart", label: "Career Start", type: "date", filterKey: "careerStartCriterion" },
  { id: "careerEnd", label: "Career End", type: "date", filterKey: "careerEndCriterion" },
  { id: "tattoos", label: "Tattoos", type: "string", filterKey: "tattooCriterion" },
  { id: "piercings", label: "Piercings", type: "string", filterKey: "piercingsCriterion" },
  { id: "aliases", label: "Aliases", type: "string", filterKey: "aliasesCriterion" },
  { id: "deathDate", label: "Death Date", type: "date", filterKey: "deathDateCriterion" },
  { id: "markerCount", label: "Marker Count", type: "number", filterKey: "markerCountCriterion" },
  { id: "playCount", label: "Play Count", type: "number", filterKey: "playCountCriterion" },
  { id: "oCounter", label: "O-Counter", type: "number", filterKey: "oCounterCriterion" },
  { id: "groups", label: "Groups", type: "multiId", entityType: "groups", filterKey: "groupsCriterion" },
  { id: "ignoreAutoTag", label: "Ignore Auto Tag", type: "bool", filterKey: "ignoreAutoTagCriterion" },
  { id: "tagCount", label: "Tag Count", type: "number", filterKey: "tagCountCriterion" },
  { id: "isMissing", label: "Is Missing", type: "bool", filterKey: "isMissingCriterion" },
];

export const TAG_CRITERIA: CriteriaDefinitionList<TagFilterCriteria> = [
  { id: "favorite", label: "Favorite", type: "bool", filterKey: "favoriteCriterion" },
  { id: "sceneCount", label: "Scene Count", type: "number", filterKey: "sceneCountCriterion" },
  { id: "markerCount", label: "Marker Count", type: "number", filterKey: "markerCountCriterion" },
  { id: "performerCount", label: "Performer Count", type: "number", filterKey: "performerCountCriterion" },
  { id: "parents", label: "Parent Tags", type: "multiId", entityType: "tags", filterKey: "parentsCriterion" },
  { id: "children", label: "Child Tags", type: "multiId", entityType: "tags", filterKey: "childrenCriterion" },
  { id: "createdAt", label: "Created At", type: "timestamp", filterKey: "createdAtCriterion" },
  { id: "updatedAt", label: "Updated At", type: "timestamp", filterKey: "updatedAtCriterion" },
  { id: "name", label: "Name", type: "string", filterKey: "nameCriterion" },
  { id: "sortName", label: "Sort Name", type: "string", filterKey: "sortNameCriterion" },
  { id: "aliases", label: "Aliases", type: "string", filterKey: "aliasesCriterion" },
  { id: "description", label: "Description", type: "string", filterKey: "descriptionCriterion" },
  { id: "imageCount", label: "Image Count", type: "number", filterKey: "imageCountCriterion" },
  { id: "galleryCount", label: "Gallery Count", type: "number", filterKey: "galleryCountCriterion" },
  { id: "studioCount", label: "Studio Count", type: "number", filterKey: "studioCountCriterion" },
  { id: "groupCount", label: "Group Count", type: "number", filterKey: "groupCountCriterion" },
  { id: "parentCount", label: "Parent Count", type: "number", filterKey: "parentCountCriterion" },
  { id: "childCount", label: "Child Count", type: "number", filterKey: "childCountCriterion" },
  { id: "ignoreAutoTag", label: "Ignore Auto Tag", type: "bool", filterKey: "ignoreAutoTagCriterion" },
  { id: "isMissing", label: "Is Missing", type: "bool", filterKey: "isMissingCriterion" },
];

export const STUDIO_CRITERIA: CriteriaDefinitionList<StudioFilterCriteria> = [
  { id: "name", label: "Name", type: "string", filterKey: "nameCriterion" },
  { id: "rating", label: "Rating", type: "rating", filterKey: "ratingCriterion" },
  { id: "favorite", label: "Favorite", type: "bool", filterKey: "favoriteCriterion" },
  { id: "tags", label: "Tags", type: "multiId", entityType: "tags", filterKey: "tagsCriterion" },
  { id: "sceneCount", label: "Scene Count", type: "number", filterKey: "sceneCountCriterion" },
  { id: "url", label: "URL", type: "string", filterKey: "urlCriterion" },
  { id: "remoteId", label: "Remote ID", type: "string", filterKey: "remoteIdCriterion" },
  { id: "createdAt", label: "Created At", type: "timestamp", filterKey: "createdAtCriterion" },
  { id: "updatedAt", label: "Updated At", type: "timestamp", filterKey: "updatedAtCriterion" },
  { id: "details", label: "Details", type: "string", filterKey: "detailsCriterion" },
  { id: "aliases", label: "Aliases", type: "string", filterKey: "aliasesCriterion" },
  { id: "parents", label: "Parent Studios", type: "multiId", entityType: "studios", filterKey: "parentsCriterion" },
  { id: "tagCount", label: "Tag Count", type: "number", filterKey: "tagCountCriterion" },
  { id: "childCount", label: "Child Count", type: "number", filterKey: "childCountCriterion" },
  { id: "groupCount", label: "Group Count", type: "number", filterKey: "groupCountCriterion" },
  { id: "galleryCount", label: "Gallery Count", type: "number", filterKey: "galleryCountCriterion" },
  { id: "imageCount", label: "Image Count", type: "number", filterKey: "imageCountCriterion" },
  { id: "isMissing", label: "Is Missing", type: "bool", filterKey: "isMissingCriterion" },
  { id: "ignoreAutoTag", label: "Ignore Auto Tag", type: "bool", filterKey: "ignoreAutoTagCriterion" },
  { id: "organized", label: "Organized", type: "bool", filterKey: "organizedCriterion" },
];

export const GALLERY_CRITERIA: CriteriaDefinitionList<GalleryFilterCriteria> = [
  { id: "title", label: "Title", type: "string", filterKey: "titleCriterion" },
  { id: "code", label: "Code", type: "string", filterKey: "codeCriterion" },
  { id: "details", label: "Details", type: "string", filterKey: "detailsCriterion" },
  { id: "photographer", label: "Photographer", type: "string", filterKey: "photographerCriterion" },
  { id: "path", label: "Path", type: "string", filterKey: "pathCriterion" },
  { id: "url", label: "URL", type: "string", filterKey: "urlCriterion" },
  { id: "rating", label: "Rating", type: "rating", filterKey: "ratingCriterion" },
  { id: "organized", label: "Organized", type: "bool", filterKey: "organizedCriterion" },
  { id: "isMissing", label: "Is Missing", type: "bool", filterKey: "isMissingCriterion" },
  { id: "tags", label: "Tags", type: "multiId", entityType: "tags", filterKey: "tagsCriterion" },
  { id: "performers", label: "Performers", type: "multiId", entityType: "performers", filterKey: "performersCriterion" },
  { id: "studios", label: "Studios", type: "multiId", entityType: "studios", filterKey: "studiosCriterion" },
  { id: "scenes", label: "Scenes", type: "multiId", entityType: "scenes", filterKey: "scenesCriterion" },
  { id: "performerTags", label: "Performer Tags", type: "multiId", entityType: "tags", filterKey: "performerTagsCriterion" },
  { id: "performerFavorite", label: "Performer Favorite", type: "bool", filterKey: "performerFavoriteCriterion" },
  { id: "imageCount", label: "Image Count", type: "number", filterKey: "imageCountCriterion" },
  { id: "fileCount", label: "File Count", type: "number", filterKey: "fileCountCriterion" },
  { id: "tagCount", label: "Tag Count", type: "number", filterKey: "tagCountCriterion" },
  { id: "performerCount", label: "Performer Count", type: "number", filterKey: "performerCountCriterion" },
  { id: "date", label: "Date", type: "date", filterKey: "dateCriterion" },
  { id: "createdAt", label: "Created At", type: "timestamp", filterKey: "createdAtCriterion" },
  { id: "updatedAt", label: "Updated At", type: "timestamp", filterKey: "updatedAtCriterion" },
];

export const IMAGE_CRITERIA: CriteriaDefinitionList<ImageFilterCriteria> = [
  { id: "title", label: "Title", type: "string", filterKey: "titleCriterion" },
  { id: "code", label: "Code", type: "string", filterKey: "codeCriterion" },
  { id: "details", label: "Details", type: "string", filterKey: "detailsCriterion" },
  { id: "photographer", label: "Photographer", type: "string", filterKey: "photographerCriterion" },
  { id: "path", label: "Path", type: "string", filterKey: "pathCriterion" },
  { id: "url", label: "URL", type: "string", filterKey: "urlCriterion" },
  { id: "rating", label: "Rating", type: "rating", filterKey: "ratingCriterion" },
  { id: "organized", label: "Organized", type: "bool", filterKey: "organizedCriterion" },
  { id: "isMissing", label: "Is Missing", type: "bool", filterKey: "isMissingCriterion" },
  { id: "oCounter", label: "O-Counter", type: "number", filterKey: "oCounterCriterion" },
  { id: "resolution", label: "Resolution", type: "resolution", filterKey: "resolutionCriterion" },
  { id: "tags", label: "Tags", type: "multiId", entityType: "tags", filterKey: "tagsCriterion" },
  { id: "performers", label: "Performers", type: "multiId", entityType: "performers", filterKey: "performersCriterion" },
  { id: "studios", label: "Studios", type: "multiId", entityType: "studios", filterKey: "studiosCriterion" },
  { id: "galleries", label: "Galleries", type: "multiId", entityType: "galleries", filterKey: "galleriesCriterion" },
  { id: "performerTags", label: "Performer Tags", type: "multiId", entityType: "tags", filterKey: "performerTagsCriterion" },
  { id: "performerFavorite", label: "Performer Favorite", type: "bool", filterKey: "performerFavoriteCriterion" },
  { id: "fileCount", label: "File Count", type: "number", filterKey: "fileCountCriterion" },
  { id: "tagCount", label: "Tag Count", type: "number", filterKey: "tagCountCriterion" },
  { id: "performerCount", label: "Performer Count", type: "number", filterKey: "performerCountCriterion" },
  { id: "date", label: "Date", type: "date", filterKey: "dateCriterion" },
  { id: "createdAt", label: "Created At", type: "timestamp", filterKey: "createdAtCriterion" },
  { id: "updatedAt", label: "Updated At", type: "timestamp", filterKey: "updatedAtCriterion" },
];

export const GROUP_CRITERIA: CriteriaDefinitionList<GroupFilterCriteria> = [
  { id: "name", label: "Name", type: "string", filterKey: "nameCriterion" },
  { id: "rating", label: "Rating", type: "rating", filterKey: "ratingCriterion" },
  { id: "director", label: "Director", type: "string", filterKey: "directorCriterion" },
  { id: "synopsis", label: "Synopsis", type: "string", filterKey: "synopsisCriterion" },
  { id: "duration", label: "Duration", type: "duration", filterKey: "durationCriterion" },
  { id: "date", label: "Date", type: "date", filterKey: "dateCriterion" },
  { id: "url", label: "URL", type: "string", filterKey: "urlCriterion" },
  { id: "studios", label: "Studios", type: "multiId", entityType: "studios", filterKey: "studiosCriterion" },
  { id: "tags", label: "Tags", type: "multiId", entityType: "tags", filterKey: "tagsCriterion" },
  { id: "performers", label: "Performers", type: "multiId", entityType: "performers", filterKey: "performersCriterion" },
  { id: "isMissing", label: "Is Missing", type: "bool", filterKey: "isMissingCriterion" },
  { id: "sceneCount", label: "Scene Count", type: "number", filterKey: "sceneCountCriterion" },
  { id: "tagCount", label: "Tag Count", type: "number", filterKey: "tagCountCriterion" },
  { id: "createdAt", label: "Created At", type: "timestamp", filterKey: "createdAtCriterion" },
  { id: "updatedAt", label: "Updated At", type: "timestamp", filterKey: "updatedAtCriterion" },
];

// ===== Filter Dialog =====

interface FilterDialogProps {
  open: boolean;
  onClose: () => void;
  criteria: CriterionDefinition[];
  activeFilter: Record<string, unknown>;
  onApply: (filter: Record<string, unknown>) => void;
  preselectCriterion?: string;
}

export function FilterDialog({ open, onClose, criteria, activeFilter, onApply, preselectCriterion }: FilterDialogProps) {
  const [editFilter, setEditFilter] = useState<Record<string, unknown>>({ ...activeFilter });
  const [search, setSearch] = useState("");
  const [expandedCriterion, setExpandedCriterion] = useState<string | null>(null);
  const [pinnedIds, setPinnedIds] = useState<Set<string>>(() => {
    try {
      const stored = localStorage.getItem("filter-pinned");
      return stored ? new Set(JSON.parse(stored)) : new Set<string>();
    } catch {
      return new Set<string>();
    }
  });

  const togglePin = useCallback(
    (id: string) => {
      setPinnedIds((prev) => {
        const next = new Set(prev);
        if (next.has(id)) next.delete(id);
        else next.add(id);
        localStorage.setItem("filter-pinned", JSON.stringify([...next]));
        return next;
      });
    },
    []
  );

  const filteredCriteria = useMemo(() => {
    const q = search.toLowerCase();
    const filtered = q ? criteria.filter((c) => c.label.toLowerCase().includes(q)) : criteria;
    // Sort: pinned first, then alphabetical
    return [...filtered].sort((a, b) => {
      const ap = pinnedIds.has(a.id) ? 0 : 1;
      const bp = pinnedIds.has(b.id) ? 0 : 1;
      if (ap !== bp) return ap - bp;
      return a.label.localeCompare(b.label);
    });
  }, [criteria, search, pinnedIds]);

  // Auto-expand preselected criterion when dialog opens
  useEffect(() => {
    if (open && preselectCriterion) {
      setExpandedCriterion(preselectCriterion);
    }
  }, [open, preselectCriterion]);

  const activeCriterionCount = useMemo(() => {
    return criteria.filter((c) => editFilter[c.filterKey] !== undefined).length;
  }, [criteria, editFilter]);

  const handleSetCriterion = useCallback((filterKey: string, value: unknown) => {
    setEditFilter((prev) => {
      if (value === undefined) {
        const { [filterKey]: _, ...rest } = prev;
        return rest;
      }
      return { ...prev, [filterKey]: value };
    });
  }, []);

  const handleApply = () => {
    onApply(editFilter);
    onClose();
  };

  const handleClear = () => {
    setEditFilter({});
  };

  if (!open) return null;

  return (
    <div className="fixed inset-0 z-50 flex items-end sm:items-center justify-center bg-black/60" onClick={onClose}>
      <div
        className="bg-surface border border-border sm:rounded-lg shadow-xl w-full sm:max-w-lg h-[85vh] sm:h-auto sm:max-h-[80vh] flex flex-col rounded-t-lg"
        onClick={(e) => e.stopPropagation()}
      >
        {/* Header */}
        <div className="flex items-center justify-between px-4 py-3 border-b border-border">
          <div className="flex items-center gap-2">
            <h2 className="text-sm font-semibold text-foreground">Edit Filter</h2>
            {activeCriterionCount > 0 && (
              <span className="px-1.5 py-0.5 rounded-full bg-accent text-white text-[10px] font-bold">
                {activeCriterionCount}
              </span>
            )}
          </div>
          <button onClick={onClose} className="p-1 hover:bg-card rounded text-muted hover:text-foreground">
            <X className="w-4 h-4" />
          </button>
        </div>

        {/* Search criteria */}
        <div className="px-4 py-2 border-b border-border">
          <div className="relative">
            <Search className="absolute left-2 top-1/2 -translate-y-1/2 w-3.5 h-3.5 text-muted" />
            <input
              type="text"
              value={search}
              onChange={(e) => setSearch(e.target.value)}
              placeholder="Search criteria..."
              className="w-full bg-input border border-border rounded pl-7 pr-3 py-1.5 text-xs text-foreground focus:outline-none focus:border-accent placeholder:text-muted"
            />
          </div>
        </div>

        {/* Active filter tags */}
        {activeCriterionCount > 0 && (
          <div className="px-4 py-2 border-b border-border flex flex-wrap gap-1">
            {criteria
              .filter((c) => editFilter[c.filterKey] !== undefined)
              .map((c) => (
                <span
                  key={c.id}
                  className="inline-flex items-center gap-1 px-2 py-0.5 rounded text-[10px] bg-accent/20 text-accent border border-accent/30"
                >
                  {c.label}
                  <button
                    onClick={() => handleSetCriterion(c.filterKey, undefined)}
                    className="hover:text-white"
                  >
                    <X className="w-3 h-3" />
                  </button>
                </span>
              ))}
          </div>
        )}

        {/* Criterion list */}
        <div className="flex-1 overflow-y-auto px-2 py-1">
          {/* Pinned divider */}
          {filteredCriteria.some((c) => pinnedIds.has(c.id)) && filteredCriteria.some((c) => !pinnedIds.has(c.id)) && (
            <>
              {filteredCriteria
                .filter((c) => pinnedIds.has(c.id))
                .map((criterion) => (
                  <CriterionRow
                    key={criterion.id}
                    criterion={criterion}
                    value={editFilter[criterion.filterKey]}
                    onChange={(v) => handleSetCriterion(criterion.filterKey, v)}
                    expanded={expandedCriterion === criterion.id}
                    onToggleExpand={() => setExpandedCriterion(expandedCriterion === criterion.id ? null : criterion.id)}
                    pinned
                    onTogglePin={() => togglePin(criterion.id)}
                  />
                ))}
              <div className="border-t border-border my-1" />
            </>
          )}
          {filteredCriteria
            .filter((c) => !(pinnedIds.has(c.id) && filteredCriteria.some((c2) => pinnedIds.has(c2.id)) && filteredCriteria.some((c2) => !pinnedIds.has(c2.id))))
            .map((criterion) => (
              <CriterionRow
                key={criterion.id}
                criterion={criterion}
                value={editFilter[criterion.filterKey]}
                onChange={(v) => handleSetCriterion(criterion.filterKey, v)}
                expanded={expandedCriterion === criterion.id}
                onToggleExpand={() => setExpandedCriterion(expandedCriterion === criterion.id ? null : criterion.id)}
                pinned={pinnedIds.has(criterion.id)}
                onTogglePin={() => togglePin(criterion.id)}
              />
            ))}
        </div>

        {/* Footer */}
        <div className="flex items-center justify-between px-4 py-3 border-t border-border">
          <button
            onClick={handleClear}
            className="px-3 py-1 rounded text-xs text-secondary hover:text-foreground hover:bg-card"
          >
            Clear All
          </button>
          <div className="flex items-center gap-2">
            <button onClick={onClose} className="px-3 py-1 rounded text-xs text-secondary hover:text-foreground border border-border">
              Cancel
            </button>
            <button onClick={handleApply} className="px-4 py-1 rounded text-xs font-medium bg-accent hover:bg-accent-hover text-white">
              Apply
            </button>
          </div>
        </div>
      </div>
    </div>
  );
}

// ===== Criterion Row =====

function CriterionRow({
  criterion,
  value,
  onChange,
  expanded,
  onToggleExpand,
  pinned,
  onTogglePin,
}: {
  criterion: CriterionDefinition;
  value: unknown;
  onChange: (v: unknown) => void;
  expanded: boolean;
  onToggleExpand: () => void;
  pinned: boolean;
  onTogglePin: () => void;
}) {
  const isActive = value !== undefined;

  return (
    <div className={`rounded mb-0.5 ${isActive ? "bg-accent/5 border border-accent/20" : ""}`}>
      <div
        className="flex items-center gap-1 px-2 py-1.5 cursor-pointer hover:bg-card/50 rounded"
        onClick={onToggleExpand}
      >
        {expanded ? (
          <ChevronDown className="w-3 h-3 text-muted flex-shrink-0" />
        ) : (
          <ChevronRight className="w-3 h-3 text-muted flex-shrink-0" />
        )}
        <span className={`text-xs flex-1 ${isActive ? "text-accent font-medium" : "text-foreground"}`}>
          {criterion.label}
        </span>
        <button
          onClick={(e) => { e.stopPropagation(); onTogglePin(); }}
          className={`p-0.5 rounded hover:bg-card ${pinned ? "text-accent" : "text-muted opacity-0 group-hover:opacity-100"}`}
          title={pinned ? "Unpin" : "Pin"}
          style={{ opacity: pinned ? 1 : undefined }}
        >
          {pinned ? <Pin className="w-3 h-3" /> : <PinOff className="w-3 h-3" />}
        </button>
        {isActive && (
          <button
            onClick={(e) => { e.stopPropagation(); onChange(undefined); }}
            className="p-0.5 rounded hover:bg-red-900/20 text-muted hover:text-red-400"
          >
            <X className="w-3 h-3" />
          </button>
        )}
      </div>
      {expanded && (
        <div className="px-3 pb-2">
          <CriterionEditor criterion={criterion} value={value} onChange={onChange} />
        </div>
      )}
    </div>
  );
}

// ===== Criterion Editor =====

function CriterionEditor({
  criterion,
  value,
  onChange,
}: {
  criterion: CriterionDefinition;
  value: unknown;
  onChange: (v: unknown) => void;
}) {
  const { type, entityType } = criterion;

  switch (type) {
    case "bool":
      return <BoolEditor value={value as BoolCriterion | undefined} onChange={onChange} />;
    case "rating":
      return <RatingFilterEditor value={value as IntCriterion | undefined} onChange={onChange} />;
    case "number":
    case "duration":
    case "resolution":
      return <NumberEditor value={value as IntCriterion | undefined} onChange={onChange} type={type} />;
    case "string":
      return <StringEditor value={value as StringCriterion | undefined} onChange={onChange} />;
    case "enum":
      return <EnumEditor value={value as StringCriterion | undefined} onChange={onChange} options={criterion.options ?? []} />;
    case "date":
      return <DateEditor value={value as DateCriterion | undefined} onChange={onChange} />;
    case "timestamp":
      return <TimestampEditor value={value as TimestampCriterion | undefined} onChange={onChange} />;
    case "multiId":
      return <MultiIdEditor value={value as MultiIdCriterion | undefined} onChange={onChange} entityType={entityType!} />;
    default:
      return null;
  }
}

// ===== Bool Editor =====

function BoolEditor({ value, onChange }: { value?: BoolCriterion; onChange: (v: unknown) => void }) {
  return (
    <div className="flex items-center gap-2">
      <button
        onClick={() => onChange({ value: true })}
        className={`px-3 py-1 rounded text-xs border ${value?.value === true ? "bg-accent text-white border-accent" : "border-border text-secondary hover:text-foreground"}`}
      >
        True
      </button>
      <button
        onClick={() => onChange({ value: false })}
        className={`px-3 py-1 rounded text-xs border ${value?.value === false ? "bg-accent text-white border-accent" : "border-border text-secondary hover:text-foreground"}`}
      >
        False
      </button>
    </div>
  );
}

// ===== Number Editor =====

function NumberEditor({ value, onChange, type }: { value?: IntCriterion; onChange: (v: unknown) => void; type: CriterionType }) {
  const modifiers = TYPE_MODIFIERS[type];
  const modifier = value?.modifier ?? "EQUALS";
  const isBetween = modifier === "BETWEEN" || modifier === "NOT_BETWEEN";
  const isNull = modifier === "IS_NULL" || modifier === "NOT_NULL";

  const update = (patch: Partial<IntCriterion>) => {
    onChange({ value: value?.value ?? 0, modifier, ...value, ...patch });
  };

  return (
    <div className="space-y-2">
      <ModifierSelector modifiers={modifiers} selected={modifier} onSelect={(m) => update({ modifier: m })} />
      {!isNull && (
        <div className="flex items-center gap-2">
          {type === "duration" ? (
            <DurationInput value={value?.value ?? 0} onChange={(v) => update({ value: v })} />
          ) : type === "resolution" ? (
            <ResolutionSelect value={value?.value ?? 0} onChange={(v) => update({ value: v })} />
          ) : (
            <input
              type="number"
              value={value?.value ?? 0}
              onChange={(e) => update({ value: Number(e.target.value) })}
              className="w-24 bg-input border border-border rounded px-2 py-1 text-xs text-foreground focus:outline-none focus:border-accent"
            />
          )}
          {isBetween && (
            <>
              <span className="text-xs text-muted">and</span>
              {type === "duration" ? (
                <DurationInput value={value?.value2 ?? 0} onChange={(v) => update({ value2: v })} />
              ) : (
                <input
                  type="number"
                  value={value?.value2 ?? 0}
                  onChange={(e) => update({ value2: Number(e.target.value) })}
                  className="w-24 bg-input border border-border rounded px-2 py-1 text-xs text-foreground focus:outline-none focus:border-accent"
                />
              )}
            </>
          )}
        </div>
      )}
    </div>
  );
}

// ===== Rating Editor — uses the user's configured rating system =====

function RatingStarInput({
  displayValue,
  onChangeDisplay,
  step,
  sizeClass,
}: {
  displayValue: number;
  onChangeDisplay: (v: number) => void;
  step: number;
  sizeClass?: string;
}) {
  const [hoverValue, setHoverValue] = useState<number | null>(null);
  const activeValue = hoverValue ?? displayValue;
  const cls = sizeClass ?? "h-4 w-4";

  return (
    <div className="flex items-center gap-0.5" onMouseLeave={() => setHoverValue(null)}>
      {[1, 2, 3, 4, 5].map((star) => (
        <button
          key={star}
          type="button"
          onMouseMove={(e) => {
            const rect = e.currentTarget.getBoundingClientRect();
            const ratio = Math.min(1, Math.max(0, (e.clientX - rect.left) / rect.width));
            const segments = Math.max(1, Math.ceil(ratio / step));
            const frac = Math.min(1, Number((segments * step).toFixed(2)));
            setHoverValue(star - 1 + frac);
          }}
          onMouseLeave={() => setHoverValue(null)}
          onClick={(e) => {
            const rect = e.currentTarget.getBoundingClientRect();
            const ratio = Math.min(1, Math.max(0, (e.clientX - rect.left) / rect.width));
            const segments = Math.max(1, Math.ceil(ratio / step));
            const frac = Math.min(1, Number((segments * step).toFixed(2)));
            const next = star - 1 + frac;
            onChangeDisplay(next === displayValue ? 0 : next);
          }}
          className="relative text-accent transition-transform hover:scale-110"
        >
          <Star className={`${cls} text-muted`} />
          <span
            className="absolute inset-y-0 left-0 overflow-hidden"
            style={{ width: `${Math.max(0, Math.min(1, activeValue - (star - 1))) * 100}%` }}
          >
            <Star className={`${cls} fill-current text-accent`} />
          </span>
        </button>
      ))}
      {hoverValue != null && (
        <span className="text-xs text-secondary ml-1">{hoverValue.toFixed(step < 1 ? 1 : 0)}</span>
      )}
    </div>
  );
}

function RatingFilterInput({
  rawValue,
  onChangeRaw,
}: {
  rawValue: number;
  onChangeRaw: (v: number) => void;
}) {
  const { config } = useAppConfig();
  const options = normalizeRatingOptions(config?.ui.ratingSystemOptions ?? defaultRatingSystemOptions);
  const displayValue = convertToRatingFormat(rawValue || undefined, options) ?? 0;
  const max = getRatingMax(options);
  const step = getRatingStep(options);

  const setDisplay = (v: number) => {
    const clamped = Math.min(max, Math.max(0, Number(v.toFixed(2))));
    onChangeRaw(convertFromRatingFormat(clamped, options));
  };

  if (options.type === "stars") {
    return (
      <div className="flex items-center gap-2">
        <RatingStarInput
          displayValue={displayValue}
          onChangeDisplay={setDisplay}
          step={getRatingPrecision(options.starPrecision)}
        />
        <input
          type="number"
          value={displayValue || ""}
          min={0}
          max={max}
          step={step}
          onChange={(e) => {
            const v = Number(e.target.value);
            if (Number.isFinite(v)) setDisplay(v);
          }}
          className="w-16 bg-input border border-border rounded px-2 py-1 text-xs text-foreground focus:outline-none focus:border-accent"
        />
      </div>
    );
  }

  // Decimal mode
  return (
    <input
      type="number"
      value={displayValue || ""}
      min={0}
      max={max}
      step={step}
      onChange={(e) => {
        const v = Number(e.target.value);
        if (Number.isFinite(v)) setDisplay(v);
      }}
      className="w-24 bg-input border border-border rounded px-2 py-1 text-xs text-foreground focus:outline-none focus:border-accent"
    />
  );
}

function RatingFilterEditor({ value, onChange }: { value?: IntCriterion; onChange: (v: unknown) => void }) {
  const modifiers = TYPE_MODIFIERS.rating;
  const modifier = value?.modifier ?? "EQUALS";
  const isBetween = modifier === "BETWEEN" || modifier === "NOT_BETWEEN";
  const isNull = modifier === "IS_NULL" || modifier === "NOT_NULL";

  const update = (patch: Partial<IntCriterion>) => {
    onChange({ value: value?.value ?? 0, modifier, ...value, ...patch });
  };

  return (
    <div className="space-y-2">
      <ModifierSelector modifiers={modifiers} selected={modifier} onSelect={(m) => update({ modifier: m })} />
      {!isNull && (
        <div className="space-y-2">
          <RatingFilterInput rawValue={value?.value ?? 0} onChangeRaw={(v) => update({ value: v })} />
          {isBetween && (
            <>
              <span className="text-xs text-muted">and</span>
              <RatingFilterInput rawValue={value?.value2 ?? 0} onChangeRaw={(v) => update({ value2: v })} />
            </>
          )}
        </div>
      )}
    </div>
  );
}

// ===== String Editor =====

function StringEditor({ value, onChange }: { value?: StringCriterion; onChange: (v: unknown) => void }) {
  const modifiers = TYPE_MODIFIERS.string;
  const modifier = value?.modifier ?? "EQUALS";
  const isNull = modifier === "IS_NULL" || modifier === "NOT_NULL";

  return (
    <div className="space-y-2">
      <ModifierSelector modifiers={modifiers} selected={modifier} onSelect={(m) => onChange({ value: value?.value ?? "", modifier: m })} />
      {!isNull && (
        <input
          type="text"
          value={value?.value ?? ""}
          onChange={(e) => onChange({ value: e.target.value, modifier })}
          className="w-full bg-input border border-border rounded px-2 py-1 text-xs text-foreground focus:outline-none focus:border-accent"
          placeholder="Value..."
        />
      )}
    </div>
  );
}

// ===== Enum Editor =====

function EnumEditor({ value, onChange, options }: { value?: StringCriterion; onChange: (v: unknown) => void; options: { value: string; label: string }[] }) {
  const modifiers = TYPE_MODIFIERS.enum;
  const modifier = value?.modifier ?? "EQUALS";
  const isNull = modifier === "IS_NULL" || modifier === "NOT_NULL";

  return (
    <div className="space-y-2">
      <ModifierSelector modifiers={modifiers} selected={modifier} onSelect={(m) => onChange({ value: value?.value ?? "", modifier: m })} />
      {!isNull && (
        <select
          value={value?.value ?? ""}
          onChange={(e) => onChange({ value: e.target.value, modifier })}
          className="w-full bg-input border border-border rounded px-2 py-1 text-xs text-foreground focus:outline-none focus:border-accent"
        >
          <option value="">Select...</option>
          {options.map((opt) => (
            <option key={opt.value} value={opt.value}>{opt.label}</option>
          ))}
        </select>
      )}
    </div>
  );
}

// ===== Date Editor =====

function DateEditor({ value, onChange }: { value?: DateCriterion; onChange: (v: unknown) => void }) {
  const modifiers = TYPE_MODIFIERS.date;
  const modifier = value?.modifier ?? "EQUALS";
  const isBetween = modifier === "BETWEEN" || modifier === "NOT_BETWEEN";
  const isNull = modifier === "IS_NULL" || modifier === "NOT_NULL";

  return (
    <div className="space-y-2">
      <ModifierSelector modifiers={modifiers} selected={modifier} onSelect={(m) => onChange({ value: value?.value ?? "", modifier: m })} />
      {!isNull && (
        <div className="flex items-center gap-2">
          <input
            type="date"
            value={value?.value ?? ""}
            onChange={(e) => onChange({ value: e.target.value, value2: value?.value2, modifier })}
            className="bg-input border border-border rounded px-2 py-1 text-xs text-foreground focus:outline-none focus:border-accent"
          />
          {isBetween && (
            <>
              <span className="text-xs text-muted">and</span>
              <input
                type="date"
                value={value?.value2 ?? ""}
                onChange={(e) => onChange({ value: value?.value, value2: e.target.value, modifier })}
                className="bg-input border border-border rounded px-2 py-1 text-xs text-foreground focus:outline-none focus:border-accent"
              />
            </>
          )}
        </div>
      )}
    </div>
  );
}

// ===== Timestamp Editor =====

function TimestampEditor({ value, onChange }: { value?: TimestampCriterion; onChange: (v: unknown) => void }) {
  const modifiers = TYPE_MODIFIERS.timestamp;
  const modifier = value?.modifier ?? "EQUALS";
  const isBetween = modifier === "BETWEEN" || modifier === "NOT_BETWEEN";
  const isNull = modifier === "IS_NULL" || modifier === "NOT_NULL";

  return (
    <div className="space-y-2">
      <ModifierSelector modifiers={modifiers} selected={modifier} onSelect={(m) => onChange({ value: value?.value ?? "", modifier: m })} />
      {!isNull && (
        <div className="flex items-center gap-2">
          <input
            type="datetime-local"
            value={value?.value ?? ""}
            onChange={(e) => onChange({ value: e.target.value, value2: value?.value2, modifier })}
            className="bg-input border border-border rounded px-2 py-1 text-xs text-foreground focus:outline-none focus:border-accent"
          />
          {isBetween && (
            <>
              <span className="text-xs text-muted">and</span>
              <input
                type="datetime-local"
                value={value?.value2 ?? ""}
                onChange={(e) => onChange({ value: value?.value, value2: e.target.value, modifier })}
                className="bg-input border border-border rounded px-2 py-1 text-xs text-foreground focus:outline-none focus:border-accent"
              />
            </>
          )}
        </div>
      )}
    </div>
  );
}

// ===== MultiId Editor =====

function MultiIdEditor({ value, onChange, entityType }: { value?: MultiIdCriterion; onChange: (v: unknown) => void; entityType: EntityType }) {
  const modifier = value?.modifier ?? "INCLUDES_ALL";
  const includedIds = value?.value ?? [];
  const excludedIds = value?.excludes ?? [];
  const includeSubTags = (value as any)?.depth === -1;
  const existingNames: Record<string, string> = (value as any)?._names ?? {};
  const [searchText, setSearchText] = useState("");

  // Fetch entities for selection
  const { data: entities } = useQuery({
    queryKey: [entityType, "all"],
    queryFn: async () => {
      switch (entityType) {
        case "tags": return (await tagsApi.find({ perPage: 5000, sort: "name", direction: "asc" })).items;
        case "performers": return (await performersApi.find({ perPage: 5000, sort: "name", direction: "asc" })).items;
        case "studios": return (await studiosApi.find({ perPage: 5000, sort: "name", direction: "asc" })).items;
        case "groups": return (await groupsApi.find({ perPage: 5000, sort: "name", direction: "asc" })).items;
        default: return [];
      }
    },
    staleTime: 60000,
  });

  // Build a name lookup from available entities
  const nameMap = useMemo(() => {
    const map: Record<string, string> = { ...existingNames };
    if (entities) for (const e of entities as any[]) map[String(e.id)] = e.name || e.title || `#${e.id}`;
    return map;
  }, [entities, existingNames]);

  const buildCriterion = (inc: number[], exc: number[], mod: string, subTags: boolean) => {
    // Include _names so filter chips can display entity names without waiting for queries
    const names: Record<string, string> = {};
    for (const id of [...inc, ...exc]) {
      if (nameMap[String(id)]) names[String(id)] = nameMap[String(id)];
    }
    return {
      value: inc,
      modifier: mod,
      excludes: exc.length > 0 ? exc : undefined,
      ...(subTags ? { depth: -1 } : {}),
      _names: Object.keys(names).length > 0 ? names : undefined,
    };
  };

  const filteredEntities = useMemo(() => {
    if (!entities) return [];
    const q = searchText.toLowerCase();
    return q ? entities.filter((e: any) => (e.name || e.title || "").toLowerCase().includes(q)) : entities;
  }, [entities, searchText]);

  const addInclude = (id: number) => {
    const nextInc = includedIds.includes(id) ? includedIds : [...includedIds, id];
    const nextExc = excludedIds.filter((i) => i !== id);
    onChange(buildCriterion(nextInc, nextExc, modifier, includeSubTags));
  };

  const addExclude = (id: number) => {
    const nextInc = includedIds.filter((i) => i !== id);
    const nextExc = excludedIds.includes(id) ? excludedIds : [...excludedIds, id];
    onChange(buildCriterion(nextInc, nextExc, modifier, includeSubTags));
  };

  const removeId = (id: number) => {
    const nextInc = includedIds.filter((i) => i !== id);
    const nextExc = excludedIds.filter((i) => i !== id);
    onChange(buildCriterion(nextInc, nextExc, modifier, includeSubTags));
  };

  const getName = (e: any) => e.name || e.title || `#${e.id}`;

  return (
    <div className="space-y-2">
      {/* Include/Exclude mode toggle */}
      <div className="flex flex-wrap gap-1">
        {(["INCLUDES", "INCLUDES_ALL"] as CriterionModifier[]).map((m) => (
          <button
            key={m}
            onClick={() => onChange(buildCriterion(includedIds, excludedIds, m, includeSubTags))}
            className={`px-2 py-0.5 rounded text-[10px] border ${
              m === modifier
                ? "bg-accent text-white border-accent"
                : "border-border text-secondary hover:text-foreground hover:border-accent/50"
            }`}
          >
            {MODIFIER_LABELS[m]}
          </button>
        ))}
      </div>
      {/* Sub-tag checkbox (only for tags) */}
      {entityType === "tags" && (
        <label className="flex items-center gap-1.5 text-xs text-secondary cursor-pointer select-none">
          <input
            type="checkbox"
            checked={includeSubTags}
            onChange={(e) => {
              onChange(buildCriterion(includedIds, excludedIds, modifier, e.target.checked));
            }}
            className="accent-accent w-3.5 h-3.5"
          />
          Include sub-tags (child tags)
        </label>
      )}
      {/* Selected items: included */}
      {includedIds.length > 0 && (
        <div className="flex flex-wrap gap-1">
          {includedIds.map((id) => {
            const entity = entities?.find((e: any) => e.id === id);
            return (
              <span key={id} className="inline-flex items-center gap-1 px-2 py-0.5 rounded text-[10px] bg-green-900/50 text-green-300 border border-green-700">
                {entity ? getName(entity) : `#${id}`}
                <button onClick={() => removeId(id)} className="hover:text-red-400">
                  <X className="w-2.5 h-2.5" />
                </button>
              </span>
            );
          })}
        </div>
      )}
      {/* Selected items: excluded */}
      {excludedIds.length > 0 && (
        <div className="flex flex-wrap gap-1">
          {excludedIds.map((id) => {
            const entity = entities?.find((e: any) => e.id === id);
            return (
              <span key={id} className="inline-flex items-center gap-1 px-2 py-0.5 rounded text-[10px] bg-red-900/50 text-red-300 border border-red-700">
                {entity ? getName(entity) : `#${id}`}
                <button onClick={() => removeId(id)} className="hover:text-red-400">
                  <X className="w-2.5 h-2.5" />
                </button>
              </span>
            );
          })}
        </div>
      )}
      {/* Search + add */}
      <div className="relative">
        <input
          type="text"
          value={searchText}
          onChange={(e) => setSearchText(e.target.value)}
          placeholder={`Search ${entityType}...`}
          className="w-full bg-input border border-border rounded px-2 py-1 text-xs text-foreground focus:outline-none focus:border-accent placeholder:text-muted"
        />
      </div>
      <div className="max-h-32 overflow-y-auto border border-border rounded bg-input">
        {filteredEntities.slice(0, 50).map((entity: any) => {
          const isIncluded = includedIds.includes(entity.id);
          const isExcluded = excludedIds.includes(entity.id);
          return (
            <div
              key={entity.id}
              className={`w-full px-2 py-1 text-xs flex items-center gap-1 ${isIncluded ? "text-green-300" : isExcluded ? "text-red-300" : "text-foreground"}`}
            >
              <button
                onClick={() => isIncluded ? removeId(entity.id) : addInclude(entity.id)}
                className={`hover:text-green-400 ${isIncluded ? "text-green-400" : "text-muted"}`}
                title="Include"
              >
                <Plus className="w-3 h-3" />
              </button>
              <button
                onClick={() => isExcluded ? removeId(entity.id) : addExclude(entity.id)}
                className={`hover:text-red-400 ${isExcluded ? "text-red-400" : "text-muted"}`}
                title="Exclude"
              >
                <Minus className="w-3 h-3" />
              </button>
              <span className="flex-1">{getName(entity)}</span>
            </div>
          );
        })}
        {filteredEntities.length === 0 && (
          <div className="px-2 py-2 text-xs text-muted text-center">No results</div>
        )}
      </div>
    </div>
  );
}

// ===== Shared Components =====

function ModifierSelector({ modifiers, selected, onSelect }: { modifiers: CriterionModifier[]; selected: CriterionModifier; onSelect: (m: CriterionModifier) => void }) {
  return (
    <div className="flex flex-wrap gap-1">
      {modifiers.map((m) => (
        <button
          key={m}
          onClick={() => onSelect(m)}
          className={`px-2 py-0.5 rounded text-[10px] border ${
            m === selected
              ? "bg-accent text-white border-accent"
              : "border-border text-secondary hover:text-foreground hover:border-accent/50"
          }`}
        >
          {MODIFIER_LABELS[m]}
        </button>
      ))}
    </div>
  );
}

function DurationInput({ value, onChange }: { value: number; onChange: (v: number) => void }) {
  const h = Math.floor(value / 3600);
  const m = Math.floor((value % 3600) / 60);
  const s = value % 60;
  const text = h > 0 ? `${h}:${String(m).padStart(2, "0")}:${String(s).padStart(2, "0")}` : `${m}:${String(s).padStart(2, "0")}`;

  const parse = (str: string) => {
    const parts = str.split(":").map(Number);
    if (parts.length === 3) return parts[0] * 3600 + parts[1] * 60 + parts[2];
    if (parts.length === 2) return parts[0] * 60 + parts[1];
    return parts[0] || 0;
  };

  return (
    <input
      type="text"
      defaultValue={text}
      onBlur={(e) => onChange(parse(e.target.value))}
      placeholder="H:MM:SS"
      className="w-24 bg-input border border-border rounded px-2 py-1 text-xs text-foreground focus:outline-none focus:border-accent"
    />
  );
}

function ResolutionSelect({ value, onChange }: { value: number; onChange: (v: number) => void }) {
  const options = [
    { label: "Any", value: 0 },
    { label: "144p", value: 144 },
    { label: "240p", value: 240 },
    { label: "360p", value: 360 },
    { label: "480p", value: 480 },
    { label: "720p", value: 720 },
    { label: "1080p", value: 1080 },
    { label: "1440p", value: 1440 },
    { label: "4K", value: 2160 },
    { label: "5K", value: 2880 },
    { label: "6K", value: 3384 },
    { label: "8K", value: 4320 },
  ];

  return (
    <select
      value={value}
      onChange={(e) => onChange(Number(e.target.value))}
      className="bg-input border border-border rounded px-2 py-1 text-xs text-foreground focus:outline-none focus:border-accent"
    >
      {options.map((o) => (
        <option key={o.value} value={o.value}>{o.label}</option>
      ))}
    </select>
  );
}

// ===== Filter Button for ListPage =====

export function FilterButton({
  activeCount,
  onClick,
}: {
  activeCount: number;
  onClick: () => void;
}) {
  return (
    <button
      onClick={onClick}
      className={`flex items-center gap-1 px-2 py-1 rounded text-xs border ${
        activeCount > 0
          ? "border-accent bg-accent/10 text-accent"
          : "border-border bg-card/70 text-secondary hover:border-accent hover:text-foreground"
      }`}
    >
      <svg className="w-3.5 h-3.5" fill="none" viewBox="0 0 24 24" stroke="currentColor" strokeWidth={2}>
        <path strokeLinecap="round" strokeLinejoin="round" d="M3 4a1 1 0 011-1h16a1 1 0 011 1v2.586a1 1 0 01-.293.707l-6.414 6.414a1 1 0 00-.293.707V17l-4 4v-6.586a1 1 0 00-.293-.707L3.293 7.293A1 1 0 013 6.586V4z" />
      </svg>
      Filter
      {activeCount > 0 && (
        <span className="px-1 py-0 rounded-full bg-accent text-white text-[10px] font-bold min-w-[16px] text-center">
          {activeCount}
        </span>
      )}
    </button>
  );
}
