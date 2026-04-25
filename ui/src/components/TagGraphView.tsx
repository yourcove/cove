import { useEffect, useMemo, useRef, useState, type PointerEvent as ReactPointerEvent, type TouchEvent as ReactTouchEvent } from "react";
import { forceCollide, forceLink, forceManyBody, forceSimulation, forceX, forceY, type SimulationLinkDatum, type SimulationNodeDatum } from "d3-force";
import { ChevronDown, ChevronUp, ExternalLink, Eye, EyeOff, Heart, RotateCcw, Search, Tag as TagIcon, Trash2, ZoomIn, ZoomOut } from "lucide-react";
import type { TagGraphLink, TagGraphNode } from "../api/types";
import { createRouteLinkProps } from "./cardNavigation";

interface Props {
  nodes: TagGraphNode[];
  links: TagGraphLink[];
  totalCount: number;
  onNavigate: (route: any) => void;
  isLoading?: boolean;
  selectedIds?: Set<number>;
  onToggleSelect?: (id: number) => void;
  onDeleteNode?: (id: number) => void;
}

interface LayoutNode extends TagGraphNode, SimulationNodeDatum {
  radius: number;
  layoutRadius: number;
  degree: number;
  anchorId: number;
  clusterColor: string;
  isClusterAnchor: boolean;
  usageIntensity: number;
}

interface SimulationGraphLink extends SimulationLinkDatum<LayoutNode> {
  source: number | LayoutNode;
  target: number | LayoutNode;
  sourceId: number;
  targetId: number;
}

interface ClusterSummary {
  anchorId: number;
  anchorName: string;
  memberIds: number[];
  centerX: number;
  centerY: number;
  color: string;
}

interface PositionedLink {
  sourceId: number;
  targetId: number;
  source: LayoutNode;
  target: LayoutNode;
}

interface ClusterHalo {
  anchorId: number;
  anchorName: string;
  color: string;
  x: number;
  y: number;
  radius: number;
  memberCount: number;
}

interface ViewTransform {
  x: number;
  y: number;
  scale: number;
}

interface LayoutSettings {
  nodeScale: number;
  labelDensity: number;
  labelSize: number;
}

interface ClusterLayoutNode extends SimulationNodeDatum {
  id: number;
  memberCount: number;
}

interface ClusterLayoutLink extends SimulationLinkDatum<ClusterLayoutNode> {
  source: number | ClusterLayoutNode;
  target: number | ClusterLayoutNode;
  weight: number;
}

interface LabelPlacement {
  left: number;
  top: number;
}

interface ScreenLabel {
  id: number;
  left: number;
  top: number;
  width: number;
  height: number;
  textX: number;
  textY: number;
  fontSize: number;
  opacity: number;
  emphasized: boolean;
  selected: boolean;
  clusterColor: string;
  text: string;
}

interface ScreenLabelCandidate extends ScreenLabel {
  priority: number;
  alwaysShow: boolean;
  optional: boolean;
  placements: LabelPlacement[];
}

interface DragState {
  pointerId: number;
  startX: number;
  startY: number;
  originX: number;
  originY: number;
  moved: boolean;
}

interface TouchPanState {
  kind: "pan";
  touchId: number;
  startX: number;
  startY: number;
  originX: number;
  originY: number;
  moved: boolean;
}

interface TouchPinchState {
  kind: "pinch";
  touchIds: [number, number];
  startDistance: number;
  startCenterX: number;
  startCenterY: number;
  originView: ViewTransform;
}

type TouchGestureState = TouchPanState | TouchPinchState;

interface TouchPointLike {
  identifier: number;
  clientX: number;
  clientY: number;
}

const CLUSTER_COLORS = ["#5eead4", "#93c5fd", "#f9a8d4", "#fde68a", "#c4b5fd", "#86efac", "#fca5a5", "#7dd3fc"];
const MIN_SCALE = 0.12;
const MAX_SCALE = 2.4;
const FOCUS_MIN_SCALE = 0.68;
const MIN_GRAPH_HEIGHT = 520;
const MAX_GRAPH_HEIGHT = 860;
const CLUSTER_PARENT_THRESHOLD = 3;
const CLUSTER_PADDING = 140;
const CLUSTER_CHIP_LIMIT = 8;
const DRAG_THRESHOLD = 4;
const NODE_LABEL_GAP = 8;
const TAG_GRAPH_PREFS_KEY = "cove-tag-graph-prefs";
const DEFAULT_LAYOUT_SETTINGS: LayoutSettings = {
  nodeScale: 1,
  labelDensity: 0.55,
  labelSize: 1.15,
};

function clampLabelSize(value: number) {
  return clamp(value, 0.75, 1.6);
}

function normalizeLayoutSettings(value: Partial<LayoutSettings> | null | undefined): LayoutSettings {
  return {
    nodeScale: clamp(value?.nodeScale ?? DEFAULT_LAYOUT_SETTINGS.nodeScale, 0, 1),
    labelDensity: clamp(value?.labelDensity ?? DEFAULT_LAYOUT_SETTINGS.labelDensity, 0, 1),
    labelSize: clampLabelSize(value?.labelSize ?? DEFAULT_LAYOUT_SETTINGS.labelSize),
  };
}

function scaleViewAtPoint(currentView: ViewTransform, nextScale: number, pointerX: number, pointerY: number): ViewTransform {
  const clampedScale = clamp(nextScale, MIN_SCALE, MAX_SCALE);
  const worldX = (pointerX - currentView.x) / currentView.scale;
  const worldY = (pointerY - currentView.y) / currentView.scale;

  return {
    scale: clampedScale,
    x: pointerX - worldX * clampedScale,
    y: pointerY - worldY * clampedScale,
  };
}

function getTouchDistance(firstTouch: TouchPointLike, secondTouch: TouchPointLike) {
  return Math.hypot(secondTouch.clientX - firstTouch.clientX, secondTouch.clientY - firstTouch.clientY);
}

function getTouchCenter(firstTouch: TouchPointLike, secondTouch: TouchPointLike, rect: DOMRect) {
  return {
    x: (firstTouch.clientX + secondTouch.clientX) / 2 - rect.left,
    y: (firstTouch.clientY + secondTouch.clientY) / 2 - rect.top,
  };
}

function createTouchPanState(touch: TouchPointLike, currentView: ViewTransform): TouchPanState {
  return {
    kind: "pan",
    touchId: touch.identifier,
    startX: touch.clientX,
    startY: touch.clientY,
    originX: currentView.x,
    originY: currentView.y,
    moved: false,
  };
}

function createTouchPinchState(firstTouch: TouchPointLike, secondTouch: TouchPointLike, rect: DOMRect, currentView: ViewTransform): TouchPinchState {
  const center = getTouchCenter(firstTouch, secondTouch, rect);

  return {
    kind: "pinch",
    touchIds: [firstTouch.identifier, secondTouch.identifier],
    startDistance: Math.max(getTouchDistance(firstTouch, secondTouch), 1),
    startCenterX: center.x,
    startCenterY: center.y,
    originView: currentView,
  };
}

function sortByName(left: TagGraphNode, right: TagGraphNode) {
  return left.name.localeCompare(right.name);
}

function clamp(value: number, min: number, max: number) {
  return Math.min(max, Math.max(min, value));
}

function lerp(start: number, end: number, amount: number) {
  return start + (end - start) * amount;
}

function boxesOverlap(
  left: { left: number; top: number; right: number; bottom: number },
  right: { left: number; top: number; right: number; bottom: number },
  margin: number,
) {
  return !(left.right + margin < right.left || left.left > right.right + margin || left.bottom + margin < right.top || left.top > right.bottom + margin);
}

function fitBounds(
  bounds: { minX: number; maxX: number; minY: number; maxY: number },
  canvasSize: { width: number; height: number },
  options?: { padding?: number; minScale?: number; maxScale?: number },
): ViewTransform {
  const padding = options?.padding ?? CLUSTER_PADDING;
  const worldWidth = Math.max(1, bounds.maxX - bounds.minX + padding * 2);
  const worldHeight = Math.max(1, bounds.maxY - bounds.minY + padding * 2);
  const scale = clamp(
    Math.min(canvasSize.width / worldWidth, canvasSize.height / worldHeight, options?.maxScale ?? 1.15),
    options?.minScale ?? MIN_SCALE,
    options?.maxScale ?? MAX_SCALE,
  );
  const centerX = (bounds.minX + bounds.maxX) / 2;
  const centerY = (bounds.minY + bounds.maxY) / 2;

  return {
    scale,
    x: canvasSize.width / 2 - centerX * scale,
    y: canvasSize.height / 2 - centerY * scale,
  };
}

function describeCount(value: number, singular: string, plural: string) {
  return `${value} ${value === 1 ? singular : plural}`;
}

function formatUsageCount(value: number) {
  return new Intl.NumberFormat(undefined, { notation: value >= 1000 ? "compact" : "standard" }).format(value);
}

function deterministicUnit(seed: number) {
  const value = Math.sin(seed * 12.9898) * 43758.5453;
  return value - Math.floor(value);
}

function estimateNodeLabelMetrics(name: string, emphasized: boolean) {
  const fontSize = emphasized ? 12.5 : 11.1;
  const width = name.length * fontSize * 0.56 + (emphasized ? 18 : 14);
  const height = fontSize + (emphasized ? 11 : 9);

  return { width, height };
}

function estimateNodeLayoutRadius(node: Pick<LayoutNode, "name" | "radius" | "isClusterAnchor">) {
  const label = estimateNodeLabelMetrics(node.name, node.isClusterAnchor);
  const horizontalReach = label.width * 0.46;
  const verticalReach = node.radius + NODE_LABEL_GAP + label.height;
  const labelReach = Math.hypot(horizontalReach, verticalReach);

  return clamp(Math.max(node.radius + 10, labelReach), node.radius + 10, 96);
}

function normalizeDirection(direction: { x: number; y: number }) {
  const magnitude = Math.hypot(direction.x, direction.y);
  if (magnitude < 0.001) {
    return { x: 0, y: 1 };
  }

  return { x: direction.x / magnitude, y: direction.y / magnitude };
}

function rotateDirection(direction: { x: number; y: number }, quarterTurns: number) {
  let nextX = direction.x;
  let nextY = direction.y;
  const normalizedTurns = ((quarterTurns % 4) + 4) % 4;

  for (let turn = 0; turn < normalizedTurns; turn += 1) {
    const currentX = nextX;
    nextX = -nextY;
    nextY = currentX;
  }

  return { x: nextX, y: nextY };
}

function placeNodeLabel(
  centerX: number,
  centerY: number,
  width: number,
  height: number,
  nodeRadius: number,
  scale: number,
  direction: { x: number; y: number },
) {
  const gap = (nodeRadius + NODE_LABEL_GAP) * scale;

  if (Math.abs(direction.x) >= Math.abs(direction.y) * 0.9) {
    return direction.x >= 0
      ? { left: centerX + gap, top: centerY - height / 2 }
      : { left: centerX - gap - width, top: centerY - height / 2 };
  }

  return direction.y >= 0
    ? { left: centerX - width / 2, top: centerY + gap }
    : { left: centerX - width / 2, top: centerY - gap - height };
}

function createNodeLabelPlacements(
  centerX: number,
  centerY: number,
  width: number,
  height: number,
  nodeRadius: number,
  scale: number,
  direction: { x: number; y: number },
) {
  const baseDirection = normalizeDirection(direction);
  const candidateDirections = [
    baseDirection,
    rotateDirection(baseDirection, 1),
    rotateDirection(baseDirection, -1),
    rotateDirection(baseDirection, 2),
  ];
  const seen = new Set<string>();
  const placements: LabelPlacement[] = [];

  for (const candidateDirection of candidateDirections) {
    const placement = placeNodeLabel(centerX, centerY, width, height, nodeRadius, scale, candidateDirection);
    const key = `${Math.round(placement.left)}:${Math.round(placement.top)}`;
    if (seen.has(key)) {
      continue;
    }

    seen.add(key);
    placements.push(placement);
  }

  return placements;
}

function getNodeRadiusRange(nodeScale: number) {
  const normalizedScale = clamp(nodeScale, 0, 1);
  return {
    min: lerp(3.5, 10.5, normalizedScale),
    max: lerp(12.5, 30, normalizedScale),
  };
}

function getUsageBreakdown(node: TagGraphNode) {
  return [
    { label: "Scenes", value: node.sceneCount },
    { label: "Markers", value: node.sceneMarkerCount },
    { label: "Images", value: node.imageCount },
    { label: "Galleries", value: node.galleryCount },
    { label: "Groups", value: node.groupCount },
    { label: "Performers", value: node.performerCount },
    { label: "Studios", value: node.studioCount },
  ].filter((item) => item.value > 0);
}

function pickAnchorId(nodeId: number, nodeMap: Map<number, TagGraphNode>) {
  const startNode = nodeMap.get(nodeId);
  if (!startNode) {
    return nodeId;
  }

  const visited = new Set<number>();
  const queue: Array<{ id: number; depth: number }> = [{ id: nodeId, depth: 0 }];
  let bestNode = startNode;
  let bestDepth = 0;

  while (queue.length > 0 && visited.size < nodeMap.size) {
    const current = queue.shift()!;
    if (visited.has(current.id)) {
      continue;
    }

    visited.add(current.id);
    const candidate = nodeMap.get(current.id);
    if (!candidate) {
      continue;
    }

    const candidateChildCount = candidate.childIds.length;
    const bestChildCount = bestNode.childIds.length;
    const candidateWins =
      candidateChildCount > bestChildCount ||
      (candidateChildCount === bestChildCount && candidate.totalUsageCount > bestNode.totalUsageCount) ||
      (candidateChildCount === bestChildCount && candidate.totalUsageCount === bestNode.totalUsageCount && current.depth > bestDepth && candidate.parentIds.length === 0);

    if (candidateWins) {
      bestNode = candidate;
      bestDepth = current.depth;
    }

    for (const parentId of candidate.parentIds) {
      if (!visited.has(parentId)) {
        queue.push({ id: parentId, depth: current.depth + 1 });
      }
    }
  }

  if (bestNode.childIds.length >= CLUSTER_PARENT_THRESHOLD || bestNode.parentIds.length === 0) {
    return bestNode.id;
  }

  return nodeId;
}

function getEdgeEndpoints(source: LayoutNode, target: LayoutNode) {
  const dx = (target.x ?? 0) - (source.x ?? 0);
  const dy = (target.y ?? 0) - (source.y ?? 0);
  const distance = Math.max(Math.hypot(dx, dy), 0.001);
  const unitX = dx / distance;
  const unitY = dy / distance;
  const sourcePadding = source.radius + 4;
  const targetPadding = target.radius + 9;

  return {
    x1: (source.x ?? 0) + unitX * sourcePadding,
    y1: (source.y ?? 0) + unitY * sourcePadding,
    x2: (target.x ?? 0) - unitX * targetPadding,
    y2: (target.y ?? 0) - unitY * targetPadding,
  };
}

function TagDetailPanel({
  node,
  selectedForMerge,
  clusterName,
  onCenter,
  onToggleSelect,
  onDelete,
}: {
  node: TagGraphNode;
  selectedForMerge: boolean;
  clusterName?: string;
  onCenter: () => void;
  onToggleSelect?: () => void;
  onDelete?: () => void;
}) {
  const usageBreakdown = getUsageBreakdown(node);
  const detailLinkProps = createRouteLinkProps<HTMLAnchorElement>({ page: "tag", id: node.id });

  return (
    <div
      data-pan-ignore="true"
      onPointerDown={(event) => event.stopPropagation()}
      className="absolute bottom-4 right-4 z-10 w-88 max-w-[calc(100%-2rem)] rounded-2xl border border-border bg-card/95 p-4 shadow-2xl backdrop-blur"
    >
      <div className="min-w-0">
        <div className="flex items-center gap-2 text-sm font-semibold text-foreground">
          <span className="truncate">{node.name}</span>
          {node.favorite && <Heart className="h-3.5 w-3.5 fill-red-500 text-red-500" />}
        </div>
        {node.description && <p className="mt-1 text-xs text-secondary">{node.description}</p>}
        {clusterName && <p className="mt-2 text-[11px] uppercase tracking-[0.18em] text-muted">Cluster: {clusterName}</p>}
      </div>

      <div className="mt-4 grid grid-cols-3 gap-2 text-center text-xs">
        <div className="rounded-xl border border-border bg-background/70 px-2 py-2">
          <div className="text-[11px] uppercase tracking-[0.16em] text-muted">Usage</div>
          <div className="mt-1 text-sm font-semibold text-foreground">{formatUsageCount(node.totalUsageCount)}</div>
        </div>
        <div className="rounded-xl border border-border bg-background/70 px-2 py-2">
          <div className="text-[11px] uppercase tracking-[0.16em] text-muted">Parents</div>
          <div className="mt-1 text-sm font-semibold text-foreground">{node.parentIds.length}</div>
        </div>
        <div className="rounded-xl border border-border bg-background/70 px-2 py-2">
          <div className="text-[11px] uppercase tracking-[0.16em] text-muted">Sub-Tags</div>
          <div className="mt-1 text-sm font-semibold text-foreground">{node.childIds.length}</div>
        </div>
      </div>

      <div className="mt-4 flex flex-wrap gap-2 text-[11px] text-secondary">
        {usageBreakdown.length > 0 ? usageBreakdown.map((item) => (
          <span key={item.label} className="rounded-full border border-border bg-background/70 px-2 py-1">
            {item.label}: {formatUsageCount(item.value)}
          </span>
        )) : (
          <span className="rounded-full border border-border bg-background/70 px-2 py-1">No usage yet</span>
        )}
      </div>

      <div className="mt-4 grid gap-2 sm:grid-cols-2">
        <button
          data-pan-ignore="true"
          type="button"
          onClick={onCenter}
          className="rounded-lg border border-border px-3 py-2 text-xs text-secondary transition-colors hover:border-accent/40 hover:text-foreground"
        >
          Center Node
        </button>
        <a
          data-pan-ignore="true"
          {...detailLinkProps}
          className="flex items-center justify-center gap-2 rounded-lg bg-accent px-3 py-2 text-xs font-medium text-white transition-colors hover:bg-accent-hover"
        >
          <ExternalLink className="h-3.5 w-3.5" />
          Open Detail
        </a>
        {onToggleSelect && (
          <button
            data-pan-ignore="true"
            type="button"
            onClick={onToggleSelect}
            className={`rounded-lg border px-3 py-2 text-xs transition-colors ${selectedForMerge ? "border-yellow-500/60 bg-yellow-500/10 text-yellow-200 hover:bg-yellow-500/15" : "border-border text-secondary hover:border-accent/40 hover:text-foreground"}`}
          >
            {selectedForMerge ? "Selected" : "Select"}
          </button>
        )}
        <button
          data-pan-ignore="true"
          type="button"
          onClick={onDelete}
          disabled={!onDelete}
          className="flex items-center justify-center gap-2 rounded-lg border border-red-500/35 px-3 py-2 text-xs text-red-200 transition-colors hover:border-red-400/60 hover:bg-red-500/10 hover:text-red-100 disabled:cursor-not-allowed disabled:opacity-40"
        >
          <Trash2 className="h-3.5 w-3.5" />
          Delete
        </button>
      </div>
    </div>
  );
}

function RangeControl({
  label,
  hint,
  min,
  max,
  step,
  value,
  onChange,
  formatValue,
}: {
  label: string;
  hint: string;
  min: number;
  max: number;
  step: number;
  value: number;
  onChange: (value: number) => void;
  formatValue: (value: number) => string;
}) {
  return (
    <label className="rounded-2xl border border-border bg-background/65 px-3 py-3">
      <div className="flex items-center justify-between gap-3 text-xs font-medium text-foreground">
        <span>{label}</span>
        <span className="text-secondary">{formatValue(value)}</span>
      </div>
      <div className="mt-1 text-[11px] text-muted">{hint}</div>
      <input
        type="range"
        min={min}
        max={max}
        step={step}
        value={value}
        onChange={(event) => onChange(Number(event.target.value))}
        className="mt-3 h-2 w-full cursor-pointer accent-accent"
      />
    </label>
  );
}

export function TagGraphView({
  nodes,
  links,
  totalCount,
  onNavigate,
  isLoading = false,
  selectedIds,
  onToggleSelect,
  onDeleteNode,
}: Props) {
  const containerRef = useRef<HTMLDivElement | null>(null);
  const dragStateRef = useRef<DragState | null>(null);
  const touchGestureRef = useRef<TouchGestureState | null>(null);
  const initializedViewKeyRef = useRef<string | null>(null);
  const restoredPrefsRef = useRef(false);
  const [canvasSize, setCanvasSize] = useState({ width: 1200, height: 720 });
  const [view, setView] = useState<ViewTransform>({ x: 0, y: 0, scale: 1 });
  const viewRef = useRef<ViewTransform>({ x: 0, y: 0, scale: 1 });
  const [selectedId, setSelectedId] = useState<number | null>(null);
  const [hoveredId, setHoveredId] = useState<number | null>(null);
  const [searchText, setSearchText] = useState("");
  const [showClusterHalos, setShowClusterHalos] = useState(false);
  const [focusedClusterId, setFocusedClusterId] = useState<number | null>(null);
  const [isolateFocusedCluster, setIsolateFocusedCluster] = useState(false);
  const [isPanning, setIsPanning] = useState(false);
  const [showLayoutTuning, setShowLayoutTuning] = useState(false);
  const [layoutSettings, setLayoutSettings] = useState<LayoutSettings>(DEFAULT_LAYOUT_SETTINGS);

  useEffect(() => {
    viewRef.current = view;
  }, [view]);

  useEffect(() => {
    if (restoredPrefsRef.current) {
      return;
    }

    restoredPrefsRef.current = true;

    try {
      const raw = localStorage.getItem(TAG_GRAPH_PREFS_KEY);
      if (!raw) {
        return;
      }

      const parsed = JSON.parse(raw) as {
        layoutSettings?: Partial<LayoutSettings>;
        showLayoutTuning?: boolean;
      };

      if (parsed.layoutSettings) {
        setLayoutSettings(normalizeLayoutSettings(parsed.layoutSettings));
      }

      if (typeof parsed.showLayoutTuning === "boolean") {
        setShowLayoutTuning(parsed.showLayoutTuning);
      }
    } catch {
      // Ignore invalid persisted graph preferences.
    }
  }, []);

  useEffect(() => {
    if (!restoredPrefsRef.current) {
      return;
    }

    localStorage.setItem(
      TAG_GRAPH_PREFS_KEY,
      JSON.stringify({
        layoutSettings,
        showLayoutTuning,
      }),
    );
  }, [layoutSettings, showLayoutTuning]);

  useEffect(() => {
    const element = containerRef.current;
    if (!element) {
      return;
    }

    const updateSize = (width: number) => {
      const graphHeight = clamp(Math.round(width * 0.64), MIN_GRAPH_HEIGHT, MAX_GRAPH_HEIGHT);
      setCanvasSize({ width: Math.max(Math.round(width), 320), height: graphHeight });
    };

    updateSize(element.getBoundingClientRect().width);

    const observer = new ResizeObserver((entries) => {
      const entry = entries[0];
      if (entry) {
        updateSize(entry.contentRect.width);
      }
    });

    observer.observe(element);
    return () => observer.disconnect();
  }, []);

  const graph = useMemo(() => {
    if (nodes.length === 0) {
      return {
        layoutNodes: [] as LayoutNode[],
        layoutNodeMap: new Map<number, LayoutNode>(),
        positionedLinks: [] as PositionedLink[],
        parentChildCount: 0,
        halos: [] as ClusterHalo[],
        clusters: [] as ClusterSummary[],
        adjacentIdsByNodeId: new Map<number, Set<number>>(),
        bounds: { minX: -100, maxX: 100, minY: -100, maxY: 100 },
      };
    }

    const sortedNodes = [...nodes].sort(sortByName);
    const nodeMap = new Map(sortedNodes.map((node) => [node.id, node]));
    const parentChildLinks = links.filter((link) => nodeMap.has(link.sourceId) && nodeMap.has(link.targetId));
    const maxUsage = Math.max(1, ...sortedNodes.map((node) => node.totalUsageCount));

    const clusterMembers = new Map<number, number[]>();
    const anchorIdByNodeId = new Map<number, number>();

    for (const node of sortedNodes) {
      const anchorId = pickAnchorId(node.id, nodeMap);
      anchorIdByNodeId.set(node.id, anchorId);
      const memberIds = clusterMembers.get(anchorId) ?? [];
      memberIds.push(node.id);
      clusterMembers.set(anchorId, memberIds);
    }

    const clusters = [...clusterMembers.entries()]
      .map(([anchorId, memberIds]) => ({
        anchorId,
        anchor: nodeMap.get(anchorId) ?? nodeMap.get(memberIds[0])!,
        memberIds,
      }))
      .sort((left, right) => right.memberIds.length - left.memberIds.length || right.anchor.childIds.length - left.anchor.childIds.length || left.anchor.name.localeCompare(right.anchor.name));

    const clusterLayoutNodes: ClusterLayoutNode[] = clusters.map((cluster, index) => {
      let centerX = 0;
      let centerY = 0;

      if (index > 0) {
        const angle = index * 2.399963229728653;
        const spread = 280 * Math.sqrt(index) + Math.sqrt(cluster.memberIds.length) * 54;
        centerX = Math.cos(angle) * spread;
        centerY = Math.sin(angle) * spread;
      }

      return {
        id: cluster.anchorId,
        memberCount: cluster.memberIds.length,
        x: centerX,
        y: centerY,
      };
    });

    const clusterLayoutNodeMap = new Map(clusterLayoutNodes.map((node) => [node.id, node]));
    const clusterLayoutLinkMap = new Map<string, ClusterLayoutLink>();
    const registerClusterLink = (sourceAnchorId: number, targetAnchorId: number) => {
      if (sourceAnchorId === targetAnchorId) {
        return;
      }

      const leftId = Math.min(sourceAnchorId, targetAnchorId);
      const rightId = Math.max(sourceAnchorId, targetAnchorId);
      const key = `${leftId}-${rightId}`;
      const existing = clusterLayoutLinkMap.get(key);
      if (existing) {
        existing.weight += 1;
        return;
      }

      clusterLayoutLinkMap.set(key, {
        source: leftId,
        target: rightId,
        weight: 1,
      });
    };

    parentChildLinks.forEach((link) => registerClusterLink(anchorIdByNodeId.get(link.sourceId) ?? link.sourceId, anchorIdByNodeId.get(link.targetId) ?? link.targetId));

    if (clusterLayoutNodes.length > 1) {
      const clusterSimulation = forceSimulation(clusterLayoutNodes)
        .force("charge", forceManyBody<ClusterLayoutNode>().strength((node) => -920 - Math.sqrt(node.memberCount) * 140))
        .force(
          "links",
          forceLink<ClusterLayoutNode, ClusterLayoutLink>([...clusterLayoutLinkMap.values()])
            .id((node) => node.id)
            .distance((link) => {
              const source = link.source as ClusterLayoutNode;
              const target = link.target as ClusterLayoutNode;
              const sizeFactor = Math.sqrt(source.memberCount) + Math.sqrt(target.memberCount);
              return clamp(230 + sizeFactor * 18 - link.weight * 15, 150, 520);
            })
            .strength((link) => clamp(0.08 + link.weight * 0.05, 0.08, 0.42)),
        )
        .force("collide", forceCollide<ClusterLayoutNode>().radius((node) => 114 + Math.sqrt(node.memberCount) * 28).iterations(2))
        .force("x", forceX<ClusterLayoutNode>(0).strength(0.04))
        .force("y", forceY<ClusterLayoutNode>(0).strength(0.04))
        .stop();

      for (let tick = 0; tick < 220; tick += 1) {
        clusterSimulation.tick();
      }
    }

    const clusterSummaryById = new Map<number, ClusterSummary>();
    clusters.forEach((cluster, index) => {
      const clusterLayoutNode = clusterLayoutNodeMap.get(cluster.anchorId);
      clusterSummaryById.set(cluster.anchorId, {
        anchorId: cluster.anchorId,
        anchorName: cluster.anchor.name,
        memberIds: cluster.memberIds,
        centerX: clusterLayoutNode?.x ?? 0,
        centerY: clusterLayoutNode?.y ?? 0,
        color: CLUSTER_COLORS[index % CLUSTER_COLORS.length],
      });
    });

    const nodeRadiusRange = getNodeRadiusRange(layoutSettings.nodeScale);

    const layoutNodes: LayoutNode[] = sortedNodes.map((node) => {
      const anchorId = anchorIdByNodeId.get(node.id) ?? pickAnchorId(node.id, nodeMap);
      const cluster = clusterSummaryById.get(anchorId)!;
      const isClusterAnchor = node.id === anchorId;
      const usageIntensity = Math.log(node.totalUsageCount + 2) / Math.log(maxUsage + 2);
      const prominence = clamp(
        usageIntensity * 0.82 +
        Math.min(node.childIds.length + node.parentIds.length, 10) * 0.02 +
        (node.favorite ? 0.06 : 0) +
        (isClusterAnchor ? 0.08 : 0),
        0,
        1,
      );
      const radius = lerp(nodeRadiusRange.min, nodeRadiusRange.max, prominence);
      const layoutRadius = estimateNodeLayoutRadius({ name: node.name, radius, isClusterAnchor });
      const initialAngle = deterministicUnit(node.id) * Math.PI * 2;
      const initialDistance = 14 + layoutRadius * 0.42 + deterministicUnit(node.id * 7) * (isClusterAnchor ? Math.max(18, layoutRadius * 0.34) : Math.max(72, layoutRadius * 1.95));

      return {
        ...node,
        radius,
        layoutRadius,
        degree: node.parentIds.length + node.childIds.length,
        anchorId,
        clusterColor: cluster.color,
        isClusterAnchor,
        usageIntensity,
        x: cluster.centerX + Math.cos(initialAngle) * initialDistance,
        y: cluster.centerY + Math.sin(initialAngle) * initialDistance,
      };
    });

    const layoutNodeMap = new Map(layoutNodes.map((node) => [node.id, node]));
    const simulationParentLinks: SimulationGraphLink[] = parentChildLinks.map((link) => ({
      source: link.sourceId,
      target: link.targetId,
      sourceId: link.sourceId,
      targetId: link.targetId,
    }));

    const simulation = forceSimulation(layoutNodes)
      .force("charge", forceManyBody<LayoutNode>().strength((node) => -42 - node.layoutRadius * 10.6 - node.degree * 3.1 - (node.isClusterAnchor ? 72 : 0)))
      .force(
        "parent-links",
        forceLink<LayoutNode, SimulationGraphLink>(simulationParentLinks)
          .id((node) => node.id)
          .distance((link) => {
            const source = link.source as LayoutNode;
            const target = link.target as LayoutNode;
            const size = Math.max(source.layoutRadius, target.layoutRadius);
            return source.anchorId === target.anchorId
              ? 36 + size * 1.55
              : 72 + size * 2.05;
          })
          .strength((link) => {
            const source = link.source as LayoutNode;
            return source.childIds.length >= 6 ? 0.36 : 0.24;
          }),
      )
      .force("collide", forceCollide<LayoutNode>().radius((node) => node.layoutRadius * 1.08 + (node.isClusterAnchor ? 6 : 3)).iterations(3))
      .force("x", forceX<LayoutNode>((node) => clusterSummaryById.get(node.anchorId)?.centerX ?? 0).strength((node) => node.isClusterAnchor ? 0.2 : 0.07))
      .force("y", forceY<LayoutNode>((node) => clusterSummaryById.get(node.anchorId)?.centerY ?? 0).strength((node) => node.isClusterAnchor ? 0.2 : 0.07))
      .stop();

    for (let tick = 0; tick < 360; tick += 1) {
      simulation.tick();
    }

    const positionedLinks = simulationParentLinks.map((link) => ({
      sourceId: (link.source as LayoutNode).id,
      targetId: (link.target as LayoutNode).id,
      source: link.source as LayoutNode,
      target: link.target as LayoutNode,
    }));

    const adjacentIdsByNodeId = new Map<number, Set<number>>();
    const registerAdjacent = (leftId: number, rightId: number) => {
      const leftSet = adjacentIdsByNodeId.get(leftId) ?? new Set<number>();
      leftSet.add(rightId);
      adjacentIdsByNodeId.set(leftId, leftSet);
      const rightSet = adjacentIdsByNodeId.get(rightId) ?? new Set<number>();
      rightSet.add(leftId);
      adjacentIdsByNodeId.set(rightId, rightSet);
    };

    positionedLinks.forEach((link) => registerAdjacent(link.sourceId, link.targetId));

    const halos = clusters
      .map((cluster) => {
        const members = cluster.memberIds.map((id) => layoutNodeMap.get(id)).filter((node): node is LayoutNode => node != null);
        if (members.length === 0) {
          return null;
        }

        const haloX = members.reduce((sum, member) => sum + (member.x ?? 0), 0) / members.length;
        const haloY = members.reduce((sum, member) => sum + (member.y ?? 0), 0) / members.length;
        const haloRadius = Math.max(
          36,
          ...members.map((member) => Math.hypot((member.x ?? 0) - haloX, (member.y ?? 0) - haloY) + member.radius + 8),
        );

        return {
          anchorId: cluster.anchorId,
          anchorName: cluster.anchor.name,
          color: clusterSummaryById.get(cluster.anchorId)?.color ?? CLUSTER_COLORS[0],
          x: haloX,
          y: haloY,
          radius: haloRadius,
          memberCount: members.length,
        };
      })
      .filter((halo): halo is ClusterHalo => halo != null && (halo.memberCount >= 4 || (layoutNodeMap.get(halo.anchorId)?.childIds.length ?? 0) >= CLUSTER_PARENT_THRESHOLD));

    const bounds = layoutNodes.reduce(
      (accumulator, node) => ({
        minX: Math.min(accumulator.minX, (node.x ?? 0) - node.layoutRadius),
        maxX: Math.max(accumulator.maxX, (node.x ?? 0) + node.layoutRadius),
        minY: Math.min(accumulator.minY, (node.y ?? 0) - node.layoutRadius),
        maxY: Math.max(accumulator.maxY, (node.y ?? 0) + node.layoutRadius),
      }),
      { minX: Number.POSITIVE_INFINITY, maxX: Number.NEGATIVE_INFINITY, minY: Number.POSITIVE_INFINITY, maxY: Number.NEGATIVE_INFINITY },
    );

    halos.forEach((halo) => {
      bounds.minX = Math.min(bounds.minX, halo.x - halo.radius);
      bounds.maxX = Math.max(bounds.maxX, halo.x + halo.radius);
      bounds.minY = Math.min(bounds.minY, halo.y - halo.radius);
      bounds.maxY = Math.max(bounds.maxY, halo.y + halo.radius);
    });

    return {
      layoutNodes,
      layoutNodeMap,
      positionedLinks,
      parentChildCount: parentChildLinks.length,
      halos,
      clusters: clusters.map((cluster) => clusterSummaryById.get(cluster.anchorId)!).filter(Boolean),
      adjacentIdsByNodeId,
      bounds,
    };
  }, [layoutSettings.nodeScale, links, nodes]);

  const fullGraphView = useMemo(
    () => fitBounds(graph.bounds, canvasSize, { padding: CLUSTER_PADDING, minScale: MIN_SCALE, maxScale: 1.05 }),
    [canvasSize, graph.bounds],
  );

  const focusBounds = useMemo(() => {
    const preferredAnchorId = focusedClusterId ?? graph.clusters[0]?.anchorId ?? null;
    if (preferredAnchorId == null) {
      return graph.bounds;
    }

    const halo = graph.halos.find((entry) => entry.anchorId === preferredAnchorId);
    if (halo) {
      return {
        minX: halo.x - halo.radius,
        maxX: halo.x + halo.radius,
        minY: halo.y - halo.radius,
        maxY: halo.y + halo.radius,
      };
    }

    const clusterNodes = graph.layoutNodes.filter((node) => node.anchorId === preferredAnchorId);
    if (clusterNodes.length === 0) {
      return graph.bounds;
    }

    return clusterNodes.reduce(
      (accumulator, node) => ({
        minX: Math.min(accumulator.minX, (node.x ?? 0) - node.radius),
        maxX: Math.max(accumulator.maxX, (node.x ?? 0) + node.radius),
        minY: Math.min(accumulator.minY, (node.y ?? 0) - node.radius),
        maxY: Math.max(accumulator.maxY, (node.y ?? 0) + node.radius),
      }),
      { minX: Number.POSITIVE_INFINITY, maxX: Number.NEGATIVE_INFINITY, minY: Number.POSITIVE_INFINITY, maxY: Number.NEGATIVE_INFINITY },
    );
  }, [focusedClusterId, graph.bounds, graph.clusters, graph.halos, graph.layoutNodes]);

  const defaultView = useMemo(
    () => fitBounds(focusBounds, canvasSize, { padding: 120, minScale: FOCUS_MIN_SCALE, maxScale: 1.3 }),
    [canvasSize, focusBounds],
  );

  useEffect(() => {
    if (graph.layoutNodes.length === 0) {
      initializedViewKeyRef.current = null;
      return;
    }

    const viewKey = `${nodes.length}:${links.length}:${nodes[0]?.id ?? 0}:${nodes[nodes.length - 1]?.id ?? 0}`;
    if (initializedViewKeyRef.current === viewKey) {
      return;
    }

    initializedViewKeyRef.current = viewKey;
    setView(defaultView);
  }, [defaultView, graph.layoutNodes.length, links.length, nodes]);

  useEffect(() => {
    if (graph.layoutNodes.length === 0) {
      setSelectedId(null);
      return;
    }

    setSelectedId((currentSelectedId) => {
      if (currentSelectedId != null && graph.layoutNodeMap.has(currentSelectedId)) {
        return currentSelectedId;
      }

      return graph.clusters[0]?.anchorId ?? graph.layoutNodes[0]?.id ?? null;
    });
  }, [graph.clusters, graph.layoutNodeMap, graph.layoutNodes]);

  useEffect(() => {
    if (focusedClusterId == null) {
      setIsolateFocusedCluster(false);
    }
  }, [focusedClusterId]);

  const searchQuery = searchText.trim().toLowerCase();
  const searchMatches = useMemo(() => {
    if (!searchQuery) {
      return [] as LayoutNode[];
    }

    return graph.layoutNodes
      .filter((node) => node.name.toLowerCase().includes(searchQuery) || (node.description ?? "").toLowerCase().includes(searchQuery))
      .sort((left, right) => right.totalUsageCount - left.totalUsageCount || left.name.localeCompare(right.name))
      .slice(0, 10);
  }, [graph.layoutNodes, searchQuery]);

  const selectedNode = selectedId == null ? null : graph.layoutNodeMap.get(selectedId) ?? null;
  const connectedIds = useMemo(() => {
    if (!selectedNode) {
      return new Set<number>();
    }

    return new Set([selectedNode.id, ...(graph.adjacentIdsByNodeId.get(selectedNode.id) ?? [])]);
  }, [graph.adjacentIdsByNodeId, selectedNode]);

  const visibleLabels = useMemo(() => {
    const showAllLabels = layoutSettings.labelDensity >= 0.995;
    const showOptionalLabels = layoutSettings.labelDensity > 0;
    const optionalLabelBudget = showAllLabels ? Number.POSITIVE_INFINITY : Math.round(graph.layoutNodes.length * layoutSettings.labelDensity);
    const overlapMargin = showAllLabels ? 0 : clamp(9 - layoutSettings.labelDensity * 7, 1, 9);
    const placedBoxes: Array<{ left: number; top: number; right: number; bottom: number }> = [];
    const visibleItems: ScreenLabel[] = [];
    let optionalLabelsPlaced = 0;
    const clusterCenterById = new Map(graph.clusters.map((cluster) => [cluster.anchorId, cluster]));
    const graphCenterX = (graph.bounds.minX + graph.bounds.maxX) / 2;
    const graphCenterY = (graph.bounds.minY + graph.bounds.maxY) / 2;

    const candidates = graph.layoutNodes
      .flatMap((node) => {
        const hiddenByCluster = isolateFocusedCluster && focusedClusterId != null && node.anchorId !== focusedClusterId;
        if (hiddenByCluster) {
          return [] as ScreenLabelCandidate[];
        }

        const inspected = selectedId === node.id;
        const hovered = hoveredId === node.id;
        const searchMatched = searchQuery.length > 0 && (node.name.toLowerCase().includes(searchQuery) || (node.description ?? "").toLowerCase().includes(searchQuery));
        const connected = connectedIds.has(node.id);
        const queued = selectedIds?.has(node.id) ?? false;
        const dimmedBySelection = selectedNode != null && !connected && selectedNode.anchorId !== node.anchorId;
        const dimmedByCluster = focusedClusterId != null && node.anchorId !== focusedClusterId;
        const alwaysShow = hovered || (layoutSettings.labelDensity > 0 && (inspected || searchMatched));
        const optional = !alwaysShow;
        const fontSize = (inspected || hovered || node.isClusterAnchor ? 12.5 : 11.1) * (0.94 + layoutSettings.labelDensity * 0.18);
        const screenFontSize = fontSize * layoutSettings.labelSize * clamp(Math.pow(view.scale, 0.8), 0.55, 1.9);
        const labelOpacity = dimmedByCluster ? 0.2 : dimmedBySelection ? 0.35 : 1;

        if (optional && !showOptionalLabels) {
          return [];
        }

        const centerX = view.x + (node.x ?? 0) * view.scale;
        const centerY = view.y + (node.y ?? 0) * view.scale;
        const width = node.name.length * screenFontSize * 0.56 + (alwaysShow || node.isClusterAnchor ? 18 : 14);
        const height = screenFontSize + (alwaysShow || node.isClusterAnchor ? 11 : 9);
        const cluster = clusterCenterById.get(node.anchorId);
        let directionX = (node.x ?? 0) - (cluster?.centerX ?? graphCenterX);
        let directionY = (node.y ?? 0) - (cluster?.centerY ?? graphCenterY);

        if (Math.hypot(directionX, directionY) < 14) {
          directionX = (node.x ?? 0) - graphCenterX;
          directionY = (node.y ?? 0) - graphCenterY;
        }

        if (Math.hypot(directionX, directionY) < 0.001) {
          directionY = 1;
        }

        const priority =
          (hovered ? 14000 : 0) +
          (inspected ? 12000 : 0) +
          (searchMatched ? 9000 : 0) +
          (connected ? 4200 : 0) +
          (node.isClusterAnchor ? 2600 : 0) +
          (queued ? 800 : 0) +
          node.totalUsageCount * 0.22 +
          node.radius * 18;

        const placements = createNodeLabelPlacements(centerX, centerY, width, height, node.radius, view.scale, { x: directionX, y: directionY });

        return [{
          id: node.id,
          priority,
          alwaysShow,
          optional,
          placements,
          left: placements[0]?.left ?? centerX,
          top: placements[0]?.top ?? centerY,
          width,
          height,
          fontSize: screenFontSize,
          opacity: labelOpacity,
          emphasized: inspected || hovered || searchMatched || node.isClusterAnchor,
          selected: inspected,
          clusterColor: node.clusterColor,
          text: node.name,
            textX: (placements[0]?.left ?? centerX) + width / 2,
          textY: (placements[0]?.top ?? centerY) + height / 2 + screenFontSize * 0.08,
          }] satisfies ScreenLabelCandidate[];
      })
      .sort((left, right) => right.priority - left.priority);

    for (const candidate of candidates) {
      if (candidate.optional && optionalLabelsPlaced >= optionalLabelBudget) {
        continue;
      }

      let placedCandidate: ScreenLabel | null = null;

      for (const placement of candidate.placements) {
        const box = {
          left: placement.left,
          top: placement.top,
          right: placement.left + candidate.width,
          bottom: placement.top + candidate.height,
        };

        if (!showAllLabels && placedBoxes.some((placedBox) => boxesOverlap(box, placedBox, overlapMargin))) {
          continue;
        }

        placedCandidate = {
          ...candidate,
          left: placement.left,
          top: placement.top,
          textX: placement.left + candidate.width / 2,
          textY: placement.top + candidate.height / 2 + candidate.fontSize * 0.08,
        };
        placedBoxes.push(box);
        break;
      }

      if (!placedCandidate) {
        if (candidate.optional) {
          continue;
        }

        const fallbackPlacement = candidate.placements[0];
        if (!fallbackPlacement) {
          continue;
        }

        placedCandidate = {
          ...candidate,
          left: fallbackPlacement.left,
          top: fallbackPlacement.top,
          textX: fallbackPlacement.left + candidate.width / 2,
          textY: fallbackPlacement.top + candidate.height / 2 + candidate.fontSize * 0.08,
        };
        placedBoxes.push({
          left: fallbackPlacement.left,
          top: fallbackPlacement.top,
          right: fallbackPlacement.left + candidate.width,
          bottom: fallbackPlacement.top + candidate.height,
        });
      }

      visibleItems.push(placedCandidate);
      if (candidate.optional) {
        optionalLabelsPlaced += 1;
      }
    }

    return visibleItems;
  }, [connectedIds, focusedClusterId, graph.bounds, graph.clusters, graph.layoutNodes, hoveredId, isolateFocusedCluster, layoutSettings.labelDensity, searchQuery, selectedId, selectedIds, view.scale, view.x, view.y]);

  const centerOnNode = (nodeId: number) => {
    const node = graph.layoutNodeMap.get(nodeId);
    if (!node) {
      return;
    }

    setView((currentView) => ({
      ...currentView,
      x: canvasSize.width / 2 - (node.x ?? 0) * currentView.scale,
      y: canvasSize.height / 2 - (node.y ?? 0) * currentView.scale,
    }));
  };

  const focusCluster = (clusterId: number) => {
    const cluster = graph.clusters.find((entry) => entry.anchorId === clusterId);
    if (!cluster) {
      return;
    }

    setFocusedClusterId((currentClusterId) => currentClusterId === clusterId ? null : clusterId);
    setSelectedId(cluster.anchorId);

    setView((currentView) => ({
      ...currentView,
      x: canvasSize.width / 2 - cluster.centerX * currentView.scale,
      y: canvasSize.height / 2 - cluster.centerY * currentView.scale,
    }));
  };

  const zoomAtPoint = (nextScale: number, pointerX: number, pointerY: number) => {
    setView((currentView) => scaleViewAtPoint(currentView, nextScale, pointerX, pointerY));
  };

  const selectNode = (nodeId: number) => {
    const node = graph.layoutNodeMap.get(nodeId);
    setSelectedId(nodeId);

    if (isolateFocusedCluster && node) {
      setFocusedClusterId(node.anchorId);
    }
  };

  useEffect(() => {
    const element = containerRef.current;
    if (!element) {
      return;
    }

    const handleWheel = (event: WheelEvent) => {
      event.preventDefault();
      const rect = element.getBoundingClientRect();
      const pointerX = event.clientX - rect.left;
      const pointerY = event.clientY - rect.top;
      const direction = event.deltaY > 0 ? 0.9 : 1.1;
      zoomAtPoint(view.scale * direction, pointerX, pointerY);
    };

    element.addEventListener("wheel", handleWheel, { passive: false });
    return () => element.removeEventListener("wheel", handleWheel);
  }, [view.scale]);

  const handlePointerDown = (event: ReactPointerEvent<HTMLDivElement>) => {
    if (event.pointerType === "touch") {
      return;
    }

    if (event.button !== 0) {
      return;
    }

    const target = event.target as Element;
    if (target.closest("[data-pan-ignore='true'], [data-node-interactive='true']")) {
      return;
    }

    dragStateRef.current = {
      pointerId: event.pointerId,
      startX: event.clientX,
      startY: event.clientY,
      originX: view.x,
      originY: view.y,
      moved: false,
    };
    setIsPanning(false);
    event.preventDefault();
    event.currentTarget.setPointerCapture(event.pointerId);
  };

  const handlePointerMove = (event: ReactPointerEvent<HTMLDivElement>) => {
    if (event.pointerType === "touch") {
      return;
    }

    const dragState = dragStateRef.current;
    if (!dragState || dragState.pointerId !== event.pointerId) {
      return;
    }

    const deltaX = event.clientX - dragState.startX;
    const deltaY = event.clientY - dragState.startY;

    if (!dragState.moved && Math.hypot(deltaX, deltaY) >= DRAG_THRESHOLD) {
      dragState.moved = true;
      setIsPanning(true);
    }

    if (dragState.moved) {
      event.preventDefault();
      setView((currentView) => ({
        ...currentView,
        x: dragState.originX + deltaX,
        y: dragState.originY + deltaY,
      }));
    }
  };

  const handlePointerUp = (event: ReactPointerEvent<HTMLDivElement>) => {
    if (event.pointerType === "touch") {
      return;
    }

    const dragState = dragStateRef.current;
    if (!dragState || dragState.pointerId !== event.pointerId) {
      return;
    }

    const target = event.target as Element;
    const clickedBackground = !dragState.moved && !target.closest("[data-pan-ignore='true'], [data-node-interactive='true']");

    dragStateRef.current = null;
    setIsPanning(false);
    if (event.currentTarget.hasPointerCapture(event.pointerId)) {
      event.currentTarget.releasePointerCapture(event.pointerId);
    }

    if (clickedBackground) {
      setSelectedId(null);
    }
  };

  const handleTouchStart = (event: ReactTouchEvent<HTMLDivElement>) => {
    const target = event.target as Element;
    if (target.closest("[data-pan-ignore='true'], [data-node-interactive='true']")) {
      return;
    }

    const rect = event.currentTarget.getBoundingClientRect();
    if (event.touches.length >= 2) {
      touchGestureRef.current = createTouchPinchState(event.touches[0], event.touches[1], rect, viewRef.current);
      setIsPanning(false);
      return;
    }

    const touch = event.touches[0];
    if (!touch) {
      return;
    }

    touchGestureRef.current = createTouchPanState(touch, viewRef.current);
    setIsPanning(false);
  };

  const handleTouchMove = (event: ReactTouchEvent<HTMLDivElement>) => {
    if (event.touches.length >= 2) {
      const rect = event.currentTarget.getBoundingClientRect();
      const firstTouch = event.touches[0];
      const secondTouch = event.touches[1];
      let gesture = touchGestureRef.current;

      if (
        !gesture ||
        gesture.kind !== "pinch" ||
        !gesture.touchIds.includes(firstTouch.identifier) ||
        !gesture.touchIds.includes(secondTouch.identifier)
      ) {
        gesture = createTouchPinchState(firstTouch, secondTouch, rect, viewRef.current);
        touchGestureRef.current = gesture;
      }

      const center = getTouchCenter(firstTouch, secondTouch, rect);
      const scaledView = scaleViewAtPoint(
        gesture.originView,
        gesture.originView.scale * (getTouchDistance(firstTouch, secondTouch) / gesture.startDistance),
        gesture.startCenterX,
        gesture.startCenterY,
      );

      event.preventDefault();
      setIsPanning(true);
      setView({
        ...scaledView,
        x: scaledView.x + (center.x - gesture.startCenterX),
        y: scaledView.y + (center.y - gesture.startCenterY),
      });
      return;
    }

    const gesture = touchGestureRef.current;
    if (!gesture || gesture.kind !== "pan") {
      return;
    }

    const touch = Array.from(event.touches).find((candidateTouch) => candidateTouch.identifier === gesture.touchId) ?? event.touches[0];
    if (!touch) {
      return;
    }

    const deltaX = touch.clientX - gesture.startX;
    const deltaY = touch.clientY - gesture.startY;

    if (!gesture.moved && Math.hypot(deltaX, deltaY) >= DRAG_THRESHOLD) {
      gesture.moved = true;
      setIsPanning(true);
    }

    if (gesture.moved) {
      event.preventDefault();
      setView((currentView) => ({
        ...currentView,
        x: gesture.originX + deltaX,
        y: gesture.originY + deltaY,
      }));
    }
  };

  const handleTouchEnd = (event: ReactTouchEvent<HTMLDivElement>) => {
    if (event.touches.length >= 2) {
      const rect = event.currentTarget.getBoundingClientRect();
      touchGestureRef.current = createTouchPinchState(event.touches[0], event.touches[1], rect, viewRef.current);
      setIsPanning(true);
      return;
    }

    if (event.touches.length === 1) {
      touchGestureRef.current = createTouchPanState(event.touches[0], viewRef.current);
      setIsPanning(false);
      return;
    }

    const gesture = touchGestureRef.current;
    if (gesture?.kind === "pan" && !gesture.moved) {
      setSelectedId(null);
    }

    touchGestureRef.current = null;
    setIsPanning(false);
  };

  if (isLoading) {
    return (
      <div className="flex h-64 items-center justify-center rounded-xl border border-border bg-card">
        <div className="h-8 w-8 animate-spin rounded-full border-b-2 border-accent" />
      </div>
    );
  }

  if (nodes.length === 0) {
    return (
      <div className="rounded-xl border border-border bg-card p-8 text-center text-secondary">
        <TagIcon className="mx-auto mb-3 h-10 w-10 opacity-40" />
        <p>No tag relationships match the current filters.</p>
      </div>
    );
  }

  const focusedCluster = focusedClusterId == null ? null : graph.clusters.find((cluster) => cluster.anchorId === focusedClusterId) ?? null;

  return (
    <div className="space-y-4">
      <div className="flex flex-wrap items-center gap-2 text-xs text-secondary">
        <span className="rounded-full border border-border bg-card px-2 py-1">{nodes.length} nodes</span>
        <span className="rounded-full border border-border bg-card px-2 py-1">{graph.parentChildCount} parent-child links</span>
        <span className="rounded-full border border-border bg-card px-2 py-1">{graph.clusters.length} clusters</span>
        {totalCount > nodes.length && (
          <span className="rounded-full border border-yellow-500/30 bg-yellow-500/10 px-2 py-1 text-yellow-300">
            Showing first {nodes.length} matching tags in graph view
          </span>
        )}
      </div>

      <div className="rounded-xl border border-border/70 bg-card/60 px-3 py-2 text-xs text-secondary">
        <div className="flex flex-wrap items-center gap-x-5 gap-y-2">
          <span className="inline-flex items-center gap-2">
            <span className="inline-flex items-center gap-1.5">
              <span className="h-px w-6 bg-[rgba(186,244,220,0.78)]" />
              <span className="text-[10px] text-[rgba(186,244,220,0.9)]">→</span>
            </span>
            <span>Solid arrow: parent to child</span>
          </span>
          <span className="text-muted">Clusters are now shaped only by the tag hierarchy, so the overview stays stable and cheaper to compute.</span>
        </div>
      </div>

      <div className="flex flex-wrap items-center gap-2">
        <label className="flex min-w-[16rem] items-center gap-2 rounded-full border border-border bg-card px-3 py-2 text-sm text-foreground">
          <Search className="h-4 w-4 text-secondary" />
          <input
            value={searchText}
            onChange={(event) => setSearchText(event.target.value)}
            placeholder="Find a tag in the graph"
            className="w-full bg-transparent outline-none placeholder:text-muted"
          />
        </label>
        <button
          type="button"
          onClick={() => setShowClusterHalos((currentValue) => !currentValue)}
          className={`rounded-full border px-3 py-2 text-xs transition-colors ${showClusterHalos ? "border-accent/50 bg-accent/10 text-foreground" : "border-border bg-card text-secondary hover:text-foreground"}`}
        >
          {showClusterHalos ? <Eye className="mr-1 inline h-3.5 w-3.5" /> : <EyeOff className="mr-1 inline h-3.5 w-3.5" />}
          {showClusterHalos ? "Hide Cluster Halos" : "Show Cluster Halos"}
        </button>
        <button
          type="button"
          onClick={() => zoomAtPoint(view.scale * 1.12, canvasSize.width / 2, canvasSize.height / 2)}
          className="rounded-full border border-border bg-card p-2 text-secondary transition-colors hover:border-accent/40 hover:text-foreground"
          title="Zoom in"
        >
          <ZoomIn className="h-4 w-4" />
        </button>
        <button
          type="button"
          onClick={() => zoomAtPoint(view.scale / 1.12, canvasSize.width / 2, canvasSize.height / 2)}
          className="rounded-full border border-border bg-card p-2 text-secondary transition-colors hover:border-accent/40 hover:text-foreground"
          title="Zoom out"
        >
          <ZoomOut className="h-4 w-4" />
        </button>
        <button
          type="button"
          onClick={() => setView(defaultView)}
          className="rounded-full border border-border bg-card p-2 text-secondary transition-colors hover:border-accent/40 hover:text-foreground"
          title="Focus overview"
        >
          <RotateCcw className="h-4 w-4" />
        </button>
        <button
          type="button"
          onClick={() => setView(fullGraphView)}
          className="rounded-full border border-border bg-card px-3 py-2 text-xs text-secondary transition-colors hover:border-accent/40 hover:text-foreground"
        >
          Fit All
        </button>
        {focusedCluster && (
          <>
            <button
              type="button"
              onClick={() => setIsolateFocusedCluster((currentValue) => !currentValue)}
              className={`rounded-full border px-3 py-2 text-xs transition-colors ${isolateFocusedCluster ? "border-accent/50 bg-accent/10 text-foreground" : "border-border bg-card text-secondary hover:text-foreground"}`}
            >
              {isolateFocusedCluster ? "Show All Clusters" : "Only Focused Cluster"}
            </button>
            <button
              type="button"
              onClick={() => setFocusedClusterId(null)}
              className="rounded-full border border-border bg-card px-3 py-2 text-xs text-secondary transition-colors hover:text-foreground"
            >
              Clear Cluster Focus ({focusedCluster.anchorName})
            </button>
          </>
        )}
      </div>

      <div className="rounded-2xl border border-border bg-card/70 p-3">
        <div className="flex flex-wrap items-start justify-between gap-3">
          <button
            type="button"
            onClick={() => setShowLayoutTuning((currentValue) => !currentValue)}
            className="flex min-w-0 items-start gap-2 text-left"
          >
            <div>
              <div className="text-xs font-semibold uppercase tracking-[0.18em] text-muted">Layout Tuning</div>
              <div className="mt-1 max-w-3xl text-xs text-secondary">
                Node Size {Math.round(layoutSettings.nodeScale * 100)}%, Label Density {Math.round(layoutSettings.labelDensity * 100)}%, and Label Size {Math.round(layoutSettings.labelSize * 100)}%.
              </div>
            </div>
            {showLayoutTuning ? <ChevronUp className="mt-0.5 h-4 w-4 text-secondary" /> : <ChevronDown className="mt-0.5 h-4 w-4 text-secondary" />}
          </button>
          <button
            type="button"
            onClick={() => setLayoutSettings(DEFAULT_LAYOUT_SETTINGS)}
            className="rounded-full border border-border bg-background/70 px-3 py-2 text-xs text-secondary transition-colors hover:border-accent/40 hover:text-foreground"
          >
            Reset Tuning
          </button>
        </div>

        {showLayoutTuning && (
          <>
            <div className="mt-3 max-w-3xl text-xs text-secondary">
              Tune the absolute node size range and how many labels stay visible in the overview. Label density now runs from hover-only at 0% to every label at 100%.
            </div>

            <div className="mt-3 grid gap-3 md:grid-cols-3">
              <RangeControl
                label="Node Size"
                hint="Control the minimum and maximum node radii used across the graph."
                min={0}
                max={1}
                step={0.05}
                value={layoutSettings.nodeScale}
                onChange={(nodeScale) => setLayoutSettings((currentSettings) => ({ ...currentSettings, nodeScale }))}
                formatValue={(value) => `${Math.round(value * 100)}%`}
              />
              <RangeControl
                label="Label Size"
                hint="Scale the label text and pill size independently from zoom level."
                min={0.75}
                max={1.6}
                step={0.05}
                value={layoutSettings.labelSize}
                onChange={(labelSize) => setLayoutSettings((currentSettings) => ({ ...currentSettings, labelSize: clampLabelSize(labelSize) }))}
                formatValue={(value) => `${Math.round(value * 100)}%`}
              />
              <RangeControl
                label="Label Density"
                hint="0% keeps labels hover-only; 100% keeps every label visible at every zoom level."
                min={0}
                max={1}
                step={0.05}
                value={layoutSettings.labelDensity}
                onChange={(labelDensity) => setLayoutSettings((currentSettings) => ({ ...currentSettings, labelDensity }))}
                formatValue={(value) => `${Math.round(value * 100)}%`}
              />
            </div>
          </>
        )}
      </div>

      <div className="flex flex-wrap gap-2 text-xs">
        {graph.clusters.slice(0, CLUSTER_CHIP_LIMIT).map((cluster) => (
          <button
            key={cluster.anchorId}
            type="button"
            onClick={() => focusCluster(cluster.anchorId)}
            className={`rounded-full border px-3 py-2 transition-colors ${focusedClusterId === cluster.anchorId ? "border-accent/60 bg-accent/10 text-foreground" : "border-border bg-card text-secondary hover:border-accent/40 hover:text-foreground"}`}
            style={{ boxShadow: focusedClusterId === cluster.anchorId ? `0 0 0 1px ${cluster.color} inset` : undefined }}
          >
            {cluster.anchorName} ({cluster.memberIds.length})
          </button>
        ))}
      </div>

      <div
        ref={containerRef}
        className={`relative overflow-hidden rounded-2xl border border-border bg-[#202a33] shadow-[inset_0_1px_0_rgba(255,255,255,0.03)] select-none ${isPanning ? "cursor-grabbing" : "cursor-grab"}`}
        style={{ height: canvasSize.height, touchAction: "none", userSelect: "none", WebkitUserSelect: "none" }}
        onPointerDown={handlePointerDown}
        onPointerMove={handlePointerMove}
        onPointerUp={handlePointerUp}
        onPointerCancel={handlePointerUp}
        onPointerLeave={handlePointerUp}
        onTouchStart={handleTouchStart}
        onTouchMove={handleTouchMove}
        onTouchEnd={handleTouchEnd}
        onTouchCancel={handleTouchEnd}
      >
        <svg width={canvasSize.width} height={canvasSize.height} viewBox={`0 0 ${canvasSize.width} ${canvasSize.height}`} role="img" aria-label="Tag relationship graph">
          <defs>
            <linearGradient id="tag-graph-background" x1="0" x2="1" y1="0" y2="1">
              <stop offset="0%" stopColor="#24333d" />
              <stop offset="100%" stopColor="#182129" />
            </linearGradient>
            <marker id="tag-graph-parent-arrow" markerWidth="6" markerHeight="6" refX="5.1" refY="3" orient="auto" markerUnits="strokeWidth">
              <path d="M 0 0 L 6 3 L 0 6 z" fill="rgba(189, 244, 221, 0.86)" />
            </marker>
          </defs>
          <rect width={canvasSize.width} height={canvasSize.height} fill="url(#tag-graph-background)" />
          <g transform={`translate(${view.x} ${view.y}) scale(${view.scale})`}>
            {showClusterHalos && graph.halos.map((halo) => {
              const isFocused = focusedClusterId === halo.anchorId;
              const hidden = isolateFocusedCluster && focusedClusterId != null && halo.anchorId !== focusedClusterId;
              if (hidden) {
                return null;
              }

              const dimmed = focusedClusterId != null && halo.anchorId !== focusedClusterId;
              const labelWidth = Math.max(88, halo.anchorName.length * 7.4 + 24);

              return (
                <g key={`halo-${halo.anchorId}`} opacity={dimmed ? 0.18 : 1}>
                  <circle cx={halo.x} cy={halo.y} r={halo.radius} fill={halo.color} fillOpacity={isFocused ? 0.1 : 0.055} stroke={halo.color} strokeOpacity={isFocused ? 0.42 : 0.16} strokeWidth={isFocused ? 2 : 1} pointerEvents="none" />
                  <g
                    data-pan-ignore="true"
                    onClick={() => focusCluster(halo.anchorId)}
                    style={{ cursor: "pointer" }}
                    transform={`translate(${halo.x - halo.radius + 12} ${halo.y - halo.radius + 8})`}
                  >
                    <rect width={labelWidth} height={22} rx={11} fill="rgba(13, 20, 27, 0.72)" stroke={halo.color} strokeOpacity={isFocused ? 0.8 : 0.45} />
                    <text x={12} y={14} fill="rgba(211, 255, 232, 0.92)" fontSize="11" fontWeight="600" style={{ userSelect: "none", pointerEvents: "none" }}>
                      {halo.anchorName}
                    </text>
                  </g>
                </g>
              );
            })}

            {graph.positionedLinks.map((link) => {
              const hiddenByCluster = isolateFocusedCluster && focusedClusterId != null && link.source.anchorId !== focusedClusterId && link.target.anchorId !== focusedClusterId;
              if (hiddenByCluster) {
                return null;
              }

              const endpoints = getEdgeEndpoints(link.source, link.target);
              const selected = selectedNode != null && (link.sourceId === selectedNode.id || link.targetId === selectedNode.id);
              const dimmedBySelection = selectedNode != null && !selected;
              const dimmedByCluster = focusedClusterId != null && link.source.anchorId !== focusedClusterId && link.target.anchorId !== focusedClusterId;
              const opacity = dimmedByCluster ? 0.07 : dimmedBySelection ? 0.1 : selected ? 0.95 : view.scale < 0.85 ? 0.24 : 0.38;
              const showDirectionMarker = selected || view.scale >= 0.92;
              return (
                <line
                  key={`parent-${link.sourceId}-${link.targetId}`}
                  x1={endpoints.x1}
                  y1={endpoints.y1}
                  x2={endpoints.x2}
                  y2={endpoints.y2}
                  stroke={selected ? link.source.clusterColor : "rgba(186, 244, 220, 0.78)"}
                  strokeOpacity={opacity}
                  strokeWidth={selected ? 2.25 : 1.1}
                  markerEnd={showDirectionMarker ? "url(#tag-graph-parent-arrow)" : undefined}
                />
              );
            })}

            {graph.layoutNodes.map((node) => {
              const hiddenByCluster = isolateFocusedCluster && focusedClusterId != null && node.anchorId !== focusedClusterId;
              if (hiddenByCluster) {
                return null;
              }

              const inspected = selectedId === node.id;
              const queued = selectedIds?.has(node.id) ?? false;
              const hovered = hoveredId === node.id;
              const connected = connectedIds.has(node.id);
              const searchMatched = searchQuery.length > 0 && (node.name.toLowerCase().includes(searchQuery) || (node.description ?? "").toLowerCase().includes(searchQuery));
              const dimmedBySelection = selectedNode != null && !connected && selectedNode.anchorId !== node.anchorId;
              const dimmedByCluster = focusedClusterId != null && node.anchorId !== focusedClusterId;
              const opacity = dimmedByCluster ? 0.18 : dimmedBySelection ? 0.28 : 1;

              return (
                <g
                  key={node.id}
                  data-node-interactive="true"
                  transform={`translate(${node.x ?? 0} ${node.y ?? 0})`}
                  onMouseEnter={() => setHoveredId(node.id)}
                  onMouseLeave={() => setHoveredId((currentHoveredId) => currentHoveredId === node.id ? null : currentHoveredId)}
                  onClick={() => selectNode(node.id)}
                  onDoubleClick={() => onNavigate({ page: "tag", id: node.id })}
                  style={{ cursor: "pointer" }}
                  opacity={opacity}
                >
                  {node.isClusterAnchor && (
                    <rect
                      x={-node.radius * 0.95}
                      y={-node.radius * 0.95}
                      width={node.radius * 1.9}
                      height={node.radius * 1.9}
                      rx={node.radius * 0.45}
                      fill={node.clusterColor}
                      fillOpacity={inspected ? 0.34 : 0.17}
                      transform="rotate(45)"
                    />
                  )}
                  <circle r={node.radius + (inspected ? 7 : queued ? 5 : hovered ? 4 : 2)} fill={node.clusterColor} fillOpacity={inspected ? 0.28 : queued ? 0.18 : 0.08} />
                  <circle r={node.radius} fill="rgba(17, 25, 35, 0.96)" stroke={queued ? "rgba(250, 204, 21, 0.92)" : node.clusterColor} strokeWidth={inspected ? 3 : queued ? 2.4 : node.isClusterAnchor ? 2 : 1.5} />
                  <circle r={node.radius * 0.72} fill={node.clusterColor} fillOpacity={0.24 + node.usageIntensity * 0.32} />
                  <circle cx={-node.radius * 0.28} cy={-node.radius * 0.28} r={Math.max(2.5, node.radius * 0.26)} fill="rgba(255, 255, 255, 0.18)" />
                  {node.favorite && <circle cx={node.radius * 0.42} cy={-node.radius * 0.42} r={Math.max(2.2, node.radius * 0.22)} fill="rgba(239, 68, 68, 0.95)" />}
                </g>
              );
            })}
          </g>
        </svg>
        <svg width={canvasSize.width} height={canvasSize.height} className="absolute inset-0" aria-hidden="true">
          {visibleLabels.map((label) => (
            <g
              key={`label-${label.id}`}
              data-node-interactive="true"
              opacity={label.opacity}
              onClick={() => selectNode(label.id)}
              onDoubleClick={() => onNavigate({ page: "tag", id: label.id })}
              style={{ cursor: "pointer" }}
            >
              <rect
                x={label.left}
                y={label.top}
                width={label.width}
                height={label.height}
                rx={label.height / 2}
                fill={label.selected ? "rgba(7, 14, 20, 0.92)" : label.emphasized ? "rgba(8, 14, 20, 0.86)" : "rgba(8, 14, 20, 0.76)"}
                stroke={label.emphasized ? label.clusterColor : "rgba(255,255,255,0.06)"}
                strokeOpacity={label.emphasized ? 0.55 : 0.4}
              />
              <text
                x={label.textX}
                y={label.textY}
                textAnchor="middle"
                dominantBaseline="middle"
                fill={label.selected ? "#f7fffb" : label.emphasized ? "rgba(225,255,241,0.97)" : "rgba(190,245,219,0.95)"}
                fontSize={label.fontSize}
                fontWeight={label.emphasized ? 620 : 540}
                style={{ letterSpacing: "0.01em" }}
              >
                {label.text}
              </text>
            </g>
          ))}
        </svg>

        {searchQuery && (
          <div
            data-pan-ignore="true"
            onPointerDown={(event) => event.stopPropagation()}
            className="absolute left-4 top-4 z-10 w-80 max-w-[calc(100%-2rem)] rounded-2xl border border-border bg-card/95 p-3 shadow-xl backdrop-blur"
          >
            <div className="mb-2 text-xs font-semibold uppercase tracking-[0.18em] text-muted">Search Results</div>
            {searchMatches.length > 0 ? (
              <div className="space-y-2">
                {searchMatches.map((node) => (
                  <button
                    key={node.id}
                    data-pan-ignore="true"
                    type="button"
                    onClick={() => {
                      selectNode(node.id);
                      centerOnNode(node.id);
                    }}
                    className={`w-full rounded-xl border px-3 py-2 text-left ${selectedId === node.id ? "border-accent bg-accent/10" : "border-border bg-background/70 hover:border-accent/40 hover:bg-card"}`}
                  >
                    <div className="flex items-center gap-2 text-sm font-medium text-foreground">
                      <span className="truncate">{node.name}</span>
                      {node.favorite && <Heart className="h-3.5 w-3.5 fill-red-500 text-red-500" />}
                    </div>
                    <div className="mt-1 text-xs text-secondary">
                      Usage {formatUsageCount(node.totalUsageCount)} · {describeCount(node.childIds.length, "sub-tag", "sub-tags")}
                    </div>
                  </button>
                ))}
              </div>
            ) : (
              <div className="rounded-xl border border-dashed border-border px-3 py-4 text-sm text-secondary">No tags match "{searchText.trim()}".</div>
            )}
          </div>
        )}

        <div
          data-pan-ignore="true"
          onPointerDown={(event) => event.stopPropagation()}
          className="absolute bottom-4 left-4 z-10 rounded-2xl border border-border bg-card/92 px-3 py-2 text-[11px] text-secondary shadow-xl backdrop-blur"
        >
          <div>Drag empty space to pan. Scroll or pinch to zoom. Tap a node to inspect it.</div>
          <div className="mt-1">Cluster focus dims the rest. Hover, search, or use halos to pull specific parts of the hierarchy back into view.</div>
        </div>

        {selectedNode && (
          <TagDetailPanel
            node={selectedNode}
            selectedForMerge={selectedIds?.has(selectedNode.id) ?? false}
            clusterName={graph.clusters.find((cluster) => cluster.anchorId === selectedNode.anchorId)?.anchorName}
            onCenter={() => centerOnNode(selectedNode.id)}
            onToggleSelect={onToggleSelect ? () => onToggleSelect(selectedNode.id) : undefined}
            onDelete={onDeleteNode ? () => onDeleteNode(selectedNode.id) : undefined}
          />
        )}
      </div>
    </div>
  );
}