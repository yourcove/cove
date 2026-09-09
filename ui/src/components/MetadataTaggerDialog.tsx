import type { ReactNode } from "react";
import { Search, X } from "lucide-react";
import type { Performer, Video, Studio, Tag } from "../api/types";
import { PerformerTagger } from "./PerformerTagger";
import { VideoTagger } from "./VideoTagger";
import { StudioTagger } from "./StudioTagger";
import { TagTagger } from "./TagTagger";

function MetadataTaggerShell({
  open,
  title,
  subtitle,
  onClose,
  children,
}: {
  open: boolean;
  title: string;
  subtitle: string;
  onClose: () => void;
  children: ReactNode;
}) {
  if (!open) return null;

  return (
    <div
      className="fixed inset-0 z-50 flex items-center justify-center bg-black/70 p-4"
      role="dialog"
      aria-modal="true"
      aria-label={title}
    >
      <div className="flex max-h-[92vh] w-full max-w-7xl flex-col overflow-hidden rounded-xl border border-border bg-surface shadow-2xl">
        <div className="flex items-start justify-between gap-4 border-b border-border px-5 py-4">
          <div>
            <h2 className="flex items-center gap-2 text-lg font-bold text-foreground">
              <Search className="h-5 w-5 text-accent" />
              {title}
            </h2>
            <p className="mt-0.5 text-xs text-secondary">{subtitle}</p>
          </div>
          <button
            type="button"
            onClick={onClose}
            className="text-muted hover:text-foreground"
            aria-label="Close metadata tagger dialog"
          >
            <X className="h-5 w-5" />
          </button>
        </div>
        <div className="min-h-0 flex-1 overflow-y-auto bg-background/30">{children}</div>
      </div>
    </div>
  );
}

export function VideoMetadataTaggerDialog({
  open,
  onClose,
  video,
  onNavigate,
}: {
  open: boolean;
  onClose: () => void;
  video: Video;
  onNavigate: (route: any) => void;
}) {
  return (
    <MetadataTaggerShell
      open={open}
      onClose={onClose}
      title="Scrape / Metadata"
      subtitle="Search scrapers or metadata providers and choose which fields to apply to this video."
    >
      <VideoTagger
        videos={[video]}
        onNavigate={(videoId) => onNavigate({ page: "video", id: videoId })}
        mode="detail"
      />
    </MetadataTaggerShell>
  );
}

export function PerformerMetadataTaggerDialog({
  open,
  onClose,
  performer,
  onNavigate,
}: {
  open: boolean;
  onClose: () => void;
  performer: Performer;
  onNavigate: (route: any) => void;
}) {
  return (
    <MetadataTaggerShell
      open={open}
      onClose={onClose}
      title="Scrape / Metadata"
      subtitle="Search scrapers or metadata providers and choose which fields to apply to this performer."
    >
      <PerformerTagger
        performers={[performer]}
        onNavigate={(performerId) => onNavigate({ page: "performer", id: performerId })}
        mode="detail"
      />
    </MetadataTaggerShell>
  );
}

export function StudioMetadataTaggerDialog({
  open,
  onClose,
  studio,
}: {
  open: boolean;
  onClose: () => void;
  studio: Studio;
}) {
  return (
    <MetadataTaggerShell
      open={open}
      onClose={onClose}
      title="Metadata"
      subtitle="Search metadata providers and choose which fields to apply to this studio."
    >
      <StudioTagger studios={[studio]} mode="detail" />
    </MetadataTaggerShell>
  );
}

export function TagMetadataTaggerDialog({ open, onClose, tag }: { open: boolean; onClose: () => void; tag: Tag }) {
  return (
    <MetadataTaggerShell
      open={open}
      onClose={onClose}
      title="Metadata"
      subtitle="Search metadata providers and apply the selected match to this tag."
    >
      <TagTagger tags={[tag]} mode="detail" />
    </MetadataTaggerShell>
  );
}
