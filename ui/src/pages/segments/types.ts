import type {
  ResolvedSpan,
  SegmentDerivedQueryDescriptor,
  SegmentRecord,
  SegmentSpanDerivedQuery,
  SegmentSpanOperand,
  SegmentSpanOperator,
  SegmentSpanReference,
} from "../../api/types";

export interface DerivedSpanItem {
  id: string;
  key: string;
  kind: "profile" | "derivedQuery";
  videoId: number;
  videoTitle: string;
  videoUpdatedAt?: string;
  coverSegmentId?: number;
  coverVersion?: string;
  span: ResolvedSpan;
  reference?: SegmentSpanReference;
  profileId?: number;
  derivedQuery?: SegmentSpanDerivedQuery;
  derivedQueryDescriptor?: SegmentDerivedQueryDescriptor;
}

export interface RawSegmentItem extends SegmentRecord {
  key: string;
  videoId: number;
  videoTitle: string;
}

export type SegmentsPageContentView = "spans" | "raw";

export interface AppliedDerivedQuery {
  operator: SegmentSpanOperator;
  operands: SegmentSpanOperand[];
  mergeGapSec?: number;
  minDurationSec?: number;
}

export interface DerivedSpanOperandFilterValue {
  sourceKey?: string;
  kind?: string;
  tagIds: number[];
  performerIds: number[];
  faceIds: number[];
  minConfidence?: number;
}

export interface DerivedSpanQueryFilterValue {
  operator: SegmentSpanOperator;
  operands: DerivedSpanOperandFilterValue[];
  mergeGapSec?: number;
  minDurationSec?: number;
}
