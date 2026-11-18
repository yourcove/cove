import type { ReactNode } from "react";
import { Crosshair, Fingerprint, Tag as TagIcon, User } from "lucide-react";
import type { EntityEngagement, Face, FaceSuggestion, ResolvedSpan, SegmentRecord, Video } from "../../api/types";
import { SuggestedNameMarker } from "../../components/EntityCards";
import { SegmentIcon } from "../../components/segmentViewIcons";
import { formatSourceKeyLabel } from "../../utils/sourceKeyLabel";
import { hasConflictingMatch } from "../../utils/faceSuggestions";
import {
  DetailNotice,
  DetailRow,
  DetailSection,
  EntityLink,
  formatConfidencePercent,
  type NearbyGroup,
  NearbySection,
  SummaryLine,
  TechnicalDetails,
  VideoSection,
} from "./SegmentDetailSections";
import {
  formatDate,
  formatSegmentRange,
  formatSegmentTime,
  rawSegmentFaceId,
  readSegmentPayloadString,
} from "./segmentDisplayUtils";
import { LoadedImage } from "../../components/LoadedImage";

/** How the Segments view shows a raw segment under the current display profile. */
export type SegmentMembership =
  | { state: "loading" }
  | { state: "hidden" }
  | { state: "shown"; span: ResolvedSpan; profileId: number };

interface SegmentFaceProps {
  face?: Face;
  faceLoading: boolean;
  /** The face's suggested performers, best first; empty unless the face still needs a match. */
  suggestions: FaceSuggestion[];
  decisionPending: boolean;
  decisionError?: Error | null;
  onAcceptSuggestion: (suggestion: FaceSuggestion) => void;
  onRejectSuggestion: (suggestion: FaceSuggestion) => void;
}

interface Props extends SegmentFaceProps {
  segment: SegmentRecord;
  /** The name the page heading shows for the segment. */
  heading: string;
  canReadFaces: boolean;
  canWriteFaces: boolean;
  canReadPerformers: boolean;
  canReadTags: boolean;
  canReadVideos: boolean;
  video?: Video;
  videoEngagement?: EntityEngagement;
  /** 0-based position of the segment among its video's raw segments in timeline order, with their count. */
  position?: { index: number; total: number };
  membership: SegmentMembership;
  nearby: NearbyGroup[];
  /** Shown in place of the nearby raw segments when they could not be loaded. */
  nearbyError?: ReactNode;
  onSeek?: (timeSec: number) => void;
  onNavigate: (route: any) => void;
}

/** The raw segment page: what the segment is, its video, how the Segments view shows it, and what is nearby. */
export function SegmentOverview(props: Props) {
  const { segment, video, videoEngagement, position, canReadVideos, onNavigate } = props;
  const openVideo = canReadVideos
    ? () => onNavigate({ page: "video", id: segment.hostId, seekTo: segment.startSec })
    : undefined;

  return (
    <div className="space-y-6">
      {rawSegmentFaceId(segment) != null ? (
        <FaceSection {...props} faceSegment={segment} bestFrameSeek={props.onSeek} />
      ) : null}
      {segment.hostType === "video" ? (
        <VideoSection
          video={video}
          videoTitle={segment.hostTitle || `Video #${segment.hostId}`}
          engagement={videoEngagement}
          position={
            position && position.total > 1
              ? `${position.index + 1} of ${position.total} raw segments in this video`
              : undefined
          }
          onOpen={openVideo}
          onNavigate={onNavigate}
        />
      ) : null}
      <MembershipSection {...props} />
      {props.nearbyError ?? <NearbySection groups={props.nearby} />}
      <TechnicalDetails
        entries={[
          ["Added", formatDate(segment.createdAt)],
          ["Edited", wasEditedLater(segment) ? formatDate(segment.updatedAt) : undefined],
          ["Run", segment.sourceRunId],
          ["Model", readSegmentPayloadString(segment.payload, "modelKey")],
          ["Face key", readSegmentPayloadString(segment.payload, "refKey")],
          ["Frames sampled", readSegmentPayloadString(segment.payload, "sampleCount")],
          ["Track", readSegmentPayloadString(segment.payload, "trackKey")],
        ]}
      />
    </div>
  );
}

/** The facts shown under a raw segment's title: what it names when the heading does not, its source and confidence. */
export function SegmentSummary(
  props: Pick<Props, "segment" | "heading" | "canReadTags" | "canReadPerformers" | "onNavigate">,
) {
  const { segment } = props;
  return (
    <SummaryLine
      items={[
        referenceLink(props),
        formatSourceKeyLabel(segment.sourceKey),
        formatConfidencePercent(segment.confidence),
      ]}
    />
  );
}

// The link to what a tag or performer segment names. The heading already shows its name, so the link only repeats
// it when it differs; a face segment's links are in its face section.
function referenceLink({
  segment,
  heading,
  canReadTags,
  canReadPerformers,
  onNavigate,
}: Pick<Props, "segment" | "heading" | "canReadTags" | "canReadPerformers" | "onNavigate">) {
  if (rawSegmentFaceId(segment) != null) return null;
  const tagId = segment.tagId;
  // The header's tag button opens the tag; the line names it only when the heading shows something else.
  if (segment.tagName && segment.tagName.trim().toLowerCase() === heading.trim().toLowerCase()) return null;
  if (segment.tagName) {
    const openTag = canReadTags && tagId != null ? () => onNavigate({ page: "tag", id: tagId }) : undefined;
    return (
      <EntityLink
        key="tag"
        label={linkLabel(segment.tagName, heading, "tag", !!openTag)}
        icon={<TagIcon className="h-3.5 w-3.5" />}
        onClick={openTag}
      />
    );
  }
  const performerId = segment.performerId;
  if (segment.performerName) {
    const openPerformer =
      canReadPerformers && performerId != null ? () => onNavigate({ page: "performer", id: performerId }) : undefined;
    return (
      <EntityLink
        key="performer"
        label={linkLabel(segment.performerName, heading, "performer", !!openPerformer)}
        icon={<User className="h-3.5 w-3.5" />}
        onClick={openPerformer}
      />
    );
  }
  return null;
}

/**
 * The face a face segment or face span names: its crop, the face and performer, and while it has no performer its
 * top suggestion to accept or reject.
 */
export function FaceSection({
  faceSegment,
  heading,
  face,
  faceLoading,
  suggestions,
  decisionPending,
  decisionError,
  canReadFaces,
  canWriteFaces,
  canReadPerformers,
  onAcceptSuggestion,
  onRejectSuggestion,
  bestFrameSeek,
  onNavigate,
}: SegmentFaceProps & {
  /** The raw face segment the section is about, or one of a span's. */
  faceSegment: SegmentRecord;
  heading: string;
  canReadFaces: boolean;
  canWriteFaces: boolean;
  canReadPerformers: boolean;
  /** Shows the segment's best frame in the player; left out where there is no player or no single best frame. */
  bestFrameSeek?: (timeSec: number) => void;
  onNavigate: (route: any) => void;
}) {
  const faceId = rawSegmentFaceId(faceSegment);
  if (faceId == null) return null;
  const performerId = face?.performerId ?? faceSegment.performerId;
  const performerName = face?.performerName ?? faceSegment.performerName;
  const faceLabel = face?.label?.trim() || faceSegment.refLabel?.trim() || `Face #${faceId}`;
  const topSuggestion = performerId == null && face && !face.ignored ? suggestions[0] : undefined;
  const openFace = canReadFaces ? () => onNavigate({ page: "face", id: faceId }) : undefined;
  const openPerformer =
    canReadPerformers && performerId != null ? () => onNavigate({ page: "performer", id: performerId }) : undefined;
  const mergedIntoFaceId = face?.mergedIntoFaceId;
  const bestTimeText = readSegmentPayloadString(faceSegment.payload, "bestTimeSec");
  const bestTime = Number(bestTimeText);

  return (
    <DetailSection title="Face">
      <div className="flex items-start gap-3">
        {openFace ? (
          <button
            type="button"
            onClick={openFace}
            aria-label={`Open face ${faceLabel}`}
            className="h-16 w-16 shrink-0 overflow-hidden rounded-xl border border-border bg-surface hover:border-accent"
          >
            <FaceCrop face={face} />
          </button>
        ) : (
          <div className="h-16 w-16 shrink-0 overflow-hidden rounded-xl border border-border bg-surface">
            <FaceCrop face={face} />
          </div>
        )}
        <dl className="min-w-0 flex-1 space-y-1.5 text-sm">
          <DetailRow label="Face">
            <EntityLink label={linkLabel(faceLabel, heading, "face", !!openFace)} onClick={openFace} />
          </DetailRow>
          <DetailRow label="Performer">
            {performerName ? (
              <EntityLink
                label={linkLabel(performerName, heading, "performer", !!openPerformer)}
                onClick={openPerformer}
              />
            ) : (
              <span className="text-secondary">{faceLoading ? "Loading..." : "Not linked"}</span>
            )}
          </DetailRow>
          {bestFrameSeek && bestTimeText != null && Number.isFinite(bestTime) ? (
            <DetailRow label="Best frame">
              <span className="inline-flex items-center gap-2">
                {formatSegmentTime(bestTime)}
                <button
                  type="button"
                  onClick={() => bestFrameSeek(bestTime)}
                  aria-label={`Show the best frame at ${formatSegmentTime(bestTime)}`}
                  className="inline-flex items-center gap-1 text-accent hover:underline"
                >
                  <Crosshair className="h-3.5 w-3.5" />
                  Show
                </button>
              </span>
            </DetailRow>
          ) : null}
        </dl>
      </div>

      {mergedIntoFaceId != null ? (
        <DetailNotice>
          This face was merged into{" "}
          <EntityLink
            label="another face"
            onClick={canReadFaces ? () => onNavigate({ page: "face", id: mergedIntoFaceId }) : undefined}
          />
          .
        </DetailNotice>
      ) : face?.ignored ? (
        <DetailNotice>This face is ignored, so it is not suggested or matched to performers.</DetailNotice>
      ) : null}

      {topSuggestion ? (
        <SuggestionCard
          suggestion={topSuggestion}
          otherCount={suggestions.length - 1}
          // A match that would import a new performer, or one that another reference source disagrees with, goes
          // through the face page; a match to a performer already in the library is decided here.
          canDecideHere={canWriteFaces && canDecideOnSegmentPage(topSuggestion, suggestions)}
          pending={decisionPending}
          error={decisionError}
          onAccept={() => onAcceptSuggestion(topSuggestion)}
          onReject={() => onRejectSuggestion(topSuggestion)}
          onOpenPerformer={
            canReadPerformers && topSuggestion.performerId > 0
              ? () => onNavigate({ page: "performer", id: topSuggestion.performerId })
              : undefined
          }
          onReview={openFace}
        />
      ) : null}
    </DetailSection>
  );
}

// How the Segments view shows this raw segment: as part of a segment, as a segment of its own, or not at all.
function MembershipSection({ segment, membership, onNavigate }: Props) {
  if (segment.hostType !== "video" || membership.state === "loading") return null;
  if (membership.state === "hidden") {
    return (
      <DetailSection title="In Segments">
        <p className="text-sm text-secondary">Not shown: the display profile leaves this raw segment out.</p>
      </DetailSection>
    );
  }
  const { span, profileId } = membership;
  const range = formatSegmentRange(span.startSec, span.endSec);
  const joined = span.segmentIds.length > 1;
  return (
    <DetailSection title="In Segments">
      <p className="flex flex-wrap items-center gap-x-2 text-sm">
        <EntityLink
          label={range}
          ariaLabel={`Open segment ${range}`}
          icon={<SegmentIcon className="h-3.5 w-3.5" />}
          onClick={() => onNavigate({ page: "video-span", id: segment.hostId, spanKey: span.spanKey, profileId })}
        />
        <span className="text-secondary">
          {joined ? `joins ${span.segmentIds.length} raw segments` : "shown on its own"}
        </span>
      </p>
    </DetailSection>
  );
}

function SuggestionCard({
  suggestion,
  otherCount,
  canDecideHere,
  pending,
  error,
  onAccept,
  onReject,
  onOpenPerformer,
  onReview,
}: {
  suggestion: FaceSuggestion;
  otherCount: number;
  canDecideHere: boolean;
  pending: boolean;
  error?: Error | null;
  onAccept: () => void;
  onReject: () => void;
  onOpenPerformer?: () => void;
  onReview?: () => void;
}) {
  return (
    <div className="space-y-3 rounded-xl border border-border bg-surface/50 p-3">
      <div className="flex items-start gap-3">
        <div className="h-12 w-12 shrink-0 overflow-hidden rounded-lg bg-surface">
          {suggestion.coverImageUrl ? (
            <LoadedImage
              src={suggestion.coverImageUrl}
              alt=""
              className="h-full w-full object-cover object-top"
              loading="lazy"
            />
          ) : (
            <span className="flex h-full w-full items-center justify-center text-muted">
              <User className="h-5 w-5" />
            </span>
          )}
        </div>
        <div className="min-w-0 flex-1">
          <div className="flex flex-wrap items-center gap-1.5 text-sm">
            <EntityLink label={suggestion.performerName} onClick={onOpenPerformer} />
            <SuggestedNameMarker />
            <span className="text-xs text-secondary">{formatSuggestionConfidence(suggestion.confidence)}</span>
          </div>
          {suggestion.why ? <p className="mt-1 line-clamp-3 text-xs text-secondary">{suggestion.why}</p> : null}
        </div>
      </div>
      <div className="flex flex-wrap items-center gap-2">
        {canDecideHere ? (
          <>
            <button
              type="button"
              onClick={onAccept}
              disabled={pending}
              className="rounded-lg bg-accent px-3 py-1.5 text-sm font-medium text-white transition-colors hover:bg-accent-hover disabled:cursor-not-allowed disabled:opacity-50"
            >
              Accept
            </button>
            <button
              type="button"
              onClick={onReject}
              disabled={pending}
              className="rounded-lg border border-border px-3 py-1.5 text-sm text-foreground transition-colors hover:border-accent disabled:cursor-not-allowed disabled:opacity-50"
            >
              Not them
            </button>
          </>
        ) : null}
        {onReview ? (
          <button type="button" onClick={onReview} className="text-sm text-accent hover:underline">
            {otherCount > 0 ? `Review all ${otherCount + 1} matches` : "Review on face page"}
          </button>
        ) : null}
      </div>
      {error ? (
        <p role="alert" className="text-xs text-red-400">
          {error.message}
        </p>
      ) : null}
    </div>
  );
}

function FaceCrop({ face }: { face?: Face }) {
  return face?.coverImageUrl ? (
    <LoadedImage src={face.coverImageUrl} alt="" className="h-full w-full object-cover" loading="lazy" />
  ) : (
    <span className="flex h-full w-full items-center justify-center text-muted">
      <Fingerprint className="h-6 w-6" />
    </span>
  );
}

// The heading already names the segment, so a link repeats a name only when it says something the heading does not.
// Without a link the name stays, since "Open …" would promise something the row cannot do.
export function linkLabel(name: string, heading: string, noun: string, linked: boolean) {
  return linked && name.trim().toLowerCase() === heading.trim().toLowerCase() ? `Open ${noun}` : name;
}

function wasEditedLater(segment: SegmentRecord) {
  return (
    !!segment.createdAt && !!segment.updatedAt && Date.parse(segment.updatedAt) - Date.parse(segment.createdAt) > 60_000
  );
}

function formatSuggestionConfidence(value: number) {
  const percent = Math.max(0, Math.min(100, Math.round(value <= 1 ? value * 100 : value)));
  return `${percent}% match`;
}

// A suggestion of a performer already in the library that no other reference source disputes. Accepting it still
// records the reference link it carries, as the faces list does.
function canDecideOnSegmentPage(suggestion: FaceSuggestion, allSuggestions: readonly FaceSuggestion[]) {
  return suggestion.performerId > 0 && !hasConflictingMatch(suggestion, allSuggestions);
}
