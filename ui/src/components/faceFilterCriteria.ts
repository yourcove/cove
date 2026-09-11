import type { CriterionModifier } from "../api/types";
import type { CriterionDefinition } from "./filterCriteriaTypes";

/** Path modifiers the faces API evaluates; IS_NULL/NOT_NULL have no meaning for a face's appearances. */
export const FACE_PATH_MODIFIERS: CriterionModifier[] = [
  "UNDER_PATH",
  "NOT_UNDER_PATH",
  "INCLUDES",
  "EXCLUDES",
  "MATCHES_REGEX",
  "NOT_MATCHES_REGEX",
];

/**
 * Faces have no file of their own: this matches the files of the videos and images a face appears in, so
 * "under D:\Videos\Jane" lists every face seen in Jane's folder.
 */
export const FACE_PATH_CRITERION: CriterionDefinition = {
  id: "path",
  label: "Path",
  type: "path",
  filterKey: "pathCriterion",
  modifiers: FACE_PATH_MODIFIERS,
};
