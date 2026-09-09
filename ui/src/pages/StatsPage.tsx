import { useQuery } from "@tanstack/react-query";
import {
  Activity,
  BarChart3,
  Brain,
  Building2,
  CheckCircle2,
  Database,
  Eye,
  FileText,
  Film,
  Fingerprint,
  HardDrive,
  Headphones,
  Heart,
  ImageIcon,
  Images,
  Layers,
  ScanSearch,
  Sparkles,
  Tags,
  Users,
} from "lucide-react";
import type { LucideIcon } from "lucide-react";
import { system } from "../api/client";
import type { Stats } from "../api/types";
import { formatDuration, formatFileSize } from "../components/shared";
import type { Route } from "../router/location";

type MetricTone = "cyan" | "emerald" | "amber" | "rose" | "violet" | "sky" | "slate";

interface MetricCardProps {
  label: string;
  value: string;
  detail?: string;
  icon: LucideIcon;
  tone?: MetricTone;
}

const toneClasses: Record<MetricTone, string> = {
  cyan: "border-cyan-500/30 bg-cyan-500/10 text-cyan-300",
  emerald: "border-emerald-500/30 bg-emerald-500/10 text-emerald-300",
  amber: "border-amber-500/30 bg-amber-500/10 text-amber-300",
  rose: "border-rose-500/30 bg-rose-500/10 text-rose-300",
  violet: "border-violet-500/30 bg-violet-500/10 text-violet-300",
  sky: "border-sky-500/30 bg-sky-500/10 text-sky-300",
  slate: "border-border bg-card text-secondary",
};

export function StatsPage(_props?: { onNavigate?: (route: Route) => void }) {
  const { data: stats, isLoading, error } = useQuery({ queryKey: ["stats"], queryFn: system.stats });

  if (isLoading) {
    return <div className="p-6 text-secondary">Loading stats...</div>;
  }

  if (error) {
    return <div className="p-6 text-red-300">Failed to load stats: {(error as Error).message}</div>;
  }

  if (!stats) {
    return <div className="p-6 text-secondary">No stats available.</div>;
  }

  const totalEntities =
    stats.videoCount +
    stats.imageCount +
    stats.galleryCount +
    stats.performerCount +
    stats.studioCount +
    stats.tagCount +
    stats.groupCount +
    stats.audioCount +
    stats.textCount +
    stats.segmentCount;

  return (
    <div className="space-y-8 p-6">
      <div className="flex items-center justify-between gap-4">
        <div>
          <h1 className="text-3xl font-bold text-foreground">Stats</h1>
          <p className="mt-1 text-sm text-secondary">{formatCount(totalEntities)} total entities</p>
        </div>
        <div className="inline-flex items-center gap-2 rounded-lg border border-border bg-card px-3 py-2 text-sm text-secondary">
          <BarChart3 className="h-4 w-4 text-accent" />
          Library totals
        </div>
      </div>

      <MetricSection title="Entities" icon={Database} metrics={entityMetrics(stats)} />
      <MetricSection title="Files" icon={HardDrive} metrics={fileMetrics(stats)} />
      <MetricSection title="Playback" icon={Activity} metrics={engagementMetrics(stats)} />
      <MetricSection title="AI Data" icon={Sparkles} metrics={aiMetrics(stats)} />
      <MetricSection title="Engagement" icon={Heart} metrics={likeMetrics(stats)} />
    </div>
  );
}

function MetricSection({
  title,
  icon: Icon,
  metrics,
}: {
  title: string;
  icon: LucideIcon;
  metrics: MetricCardProps[];
}) {
  return (
    <section className="space-y-3">
      <div className="flex items-center gap-2">
        <Icon className="h-5 w-5 text-accent" />
        <h2 className="text-lg font-semibold text-foreground">{title}</h2>
      </div>
      <div className="grid gap-3 sm:grid-cols-2 xl:grid-cols-4">
        {metrics.map((metric) => (
          <MetricCard key={`${title}-${metric.label}`} {...metric} />
        ))}
      </div>
    </section>
  );
}

function MetricCard({ label, value, detail, icon: Icon, tone = "slate" }: MetricCardProps) {
  return (
    <div className="rounded-lg border border-border bg-surface p-4">
      <div className="mb-3 flex items-center justify-between gap-3">
        <div className="min-w-0 text-sm font-medium text-secondary">{label}</div>
        <div className={`shrink-0 rounded-md border p-2 ${toneClasses[tone]}`}>
          <Icon className="h-4 w-4" />
        </div>
      </div>
      <div className="truncate text-2xl font-semibold text-foreground">{value}</div>
      {detail ? <div className="mt-1 truncate text-xs text-muted">{detail}</div> : null}
    </div>
  );
}

function entityMetrics(stats: Stats): MetricCardProps[] {
  return [
    { label: "Videos", value: formatCount(stats.videoCount), icon: Film, tone: "cyan" },
    { label: "Images", value: formatCount(stats.imageCount), icon: ImageIcon, tone: "emerald" },
    { label: "Audios", value: formatCount(stats.audioCount), icon: Headphones, tone: "amber" },
    { label: "Texts", value: formatCount(stats.textCount), icon: FileText, tone: "rose" },
    { label: "Galleries", value: formatCount(stats.galleryCount), icon: Images, tone: "sky" },
    { label: "Performers", value: formatCount(stats.performerCount), icon: Users, tone: "violet" },
    { label: "Studios", value: formatCount(stats.studioCount), icon: Building2, tone: "cyan" },
    { label: "Tags", value: formatCount(stats.tagCount), icon: Tags, tone: "emerald" },
    { label: "Groups", value: formatCount(stats.groupCount), icon: Layers, tone: "amber" },
  ];
}

function fileMetrics(stats: Stats): MetricCardProps[] {
  return [
    { label: "Total File Size", value: formatFileSize(stats.totalFileSize), icon: HardDrive, tone: "slate" },
    {
      label: "Video Files",
      value: formatFileSize(stats.videoFileSize),
      detail: formatDuration(stats.videoDuration),
      icon: Film,
      tone: "cyan",
    },
    { label: "Image Files", value: formatFileSize(stats.imageFileSize), icon: ImageIcon, tone: "emerald" },
    {
      label: "Audio Files",
      value: formatFileSize(stats.audioFileSize),
      detail: formatDuration(stats.audioDuration),
      icon: Headphones,
      tone: "amber",
    },
    { label: "Text Files", value: formatFileSize(stats.textFileSize), icon: FileText, tone: "rose" },
  ];
}

function engagementMetrics(stats: Stats): MetricCardProps[] {
  return [
    {
      label: "Video Plays",
      value: formatCount(stats.videoPlayCount),
      detail: `${formatCount(stats.videoCompleteCount)} completed`,
      icon: Film,
      tone: "cyan",
    },
    {
      label: "Audio Plays",
      value: formatCount(stats.audioPlayCount),
      detail: `${formatCount(stats.audioCompleteCount)} completed`,
      icon: Headphones,
      tone: "amber",
    },
    {
      label: "Text Reads",
      value: formatCount(stats.textReadCount),
      detail: `${formatCount(stats.textCompleteCount)} completed`,
      icon: FileText,
      tone: "rose",
    },
    {
      label: "Image Views",
      value: formatCount(stats.imageViewCount),
      detail: `${formatCount(stats.imageCompleteCount)} completed`,
      icon: Eye,
      tone: "emerald",
    },
    {
      label: "Segment Plays",
      value: formatCount(stats.segmentViewCount),
      detail: `${formatCount(stats.segmentCompleteCount)} completed`,
      icon: Layers,
      tone: "cyan",
    },
    { label: "Video Watch Time", value: formatDuration(stats.videoConsumedSeconds), icon: CheckCircle2, tone: "sky" },
    { label: "Audio Listen Time", value: formatDuration(stats.audioConsumedSeconds), icon: Activity, tone: "violet" },
    { label: "Text Read Time", value: formatDuration(stats.textConsumedSeconds), icon: FileText, tone: "slate" },
    { label: "Image View Time", value: formatDuration(stats.imageConsumedSeconds), icon: ImageIcon, tone: "emerald" },
    { label: "Segment Watch Time", value: formatDuration(stats.segmentConsumedSeconds), icon: Layers, tone: "cyan" },
  ];
}

function aiMetrics(stats: Stats): MetricCardProps[] {
  return [
    { label: "AI Runs", value: formatCount(stats.aiRunCount), icon: Sparkles, tone: "violet" },
    { label: "Segments", value: formatCount(stats.segmentCount), icon: Layers, tone: "cyan" },
    { label: "Embeddings", value: formatCount(stats.embeddingCount), icon: Brain, tone: "sky" },
    { label: "Detections", value: formatCount(stats.detectionCount), icon: ScanSearch, tone: "amber" },
    { label: "Tag Applications", value: formatCount(stats.tagApplicationCount), icon: Tags, tone: "emerald" },
    { label: "Faces", value: formatCount(stats.faceCount), icon: Fingerprint, tone: "rose" },
    { label: "Face Appearances", value: formatCount(stats.faceAppearanceCount), icon: Eye, tone: "slate" },
  ];
}

function likeMetrics(stats: Stats): MetricCardProps[] {
  return [
    { label: "Likes", value: formatCount(stats.totalLikes), icon: Heart, tone: "rose" },
    { label: "Derived Likes", value: formatCount(stats.totalDerivedLikes), icon: Activity, tone: "amber" },
    { label: "Favorites", value: formatCount(stats.totalFavorites), icon: CheckCircle2, tone: "emerald" },
    { label: "Consumed Time", value: formatDuration(stats.totalPlayDuration), icon: BarChart3, tone: "sky" },
  ];
}

function formatCount(value: number) {
  return Math.round(value || 0).toLocaleString();
}
