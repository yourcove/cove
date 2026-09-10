export interface AlignmentAnchor {
  sourceSec: number;
  targetSec: number;
  section: number;
}
export interface AlignmentState {
  primaryFileId: number | null;
  files: { id: number; basename: string; duration: number; available: boolean }[];
}
export interface AlignmentReview {
  sourceFileId: number;
  targetFileId: number;
  sourceDuration: number;
  targetDuration: number;
  anchors: AlignmentAnchor[];
}
export interface AlignmentAssessment {
  sourceFileId: number | null;
  targetFileId: number;
  sourceDuration: number | null;
  targetDuration: number;
  equivalent: boolean;
  canAlign: boolean;
  dependencyCount: number;
  dependencyCounts: Record<string, number>;
}
export interface AlignmentAnalysis {
  anchors: AlignmentAnchor[];
  message: string;
  sampleStep: number;
}
export interface AlignmentPreview {
  message: string;
  comparisons?: {
    kind: "segment" | "clip";
    id: number;
    title: string | null;
    sourceSec: number;
    targetSec: number;
    sourceThumbnail: string | null;
    targetThumbnail: string | null;
  }[];
  comparisonCounts?: { segment: number; clip: number };
  dependencies: {
    kind: string;
    id: number;
    title: string | null;
    startSec: number;
    endSec: number | null;
    mapped: { startSec: number; endSec: number } | null;
  }[];
}
