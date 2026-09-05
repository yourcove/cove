import { entityEngagement, playback } from "../api/client";
import type { EngagementInteractionWrite, PlaybackIntervalInput, PlaybackIntervalsRequest } from "../api/types";
import { authStore } from "../auth/authStore";
import { serverAwareFetch } from "../state/serverAvailability";

export interface PlaybackTrackingTarget {
  hostType: string;
  hostId: number;
  surface?: string;
  scopeKey?: string;
  parentHostType?: string;
  parentHostId?: number;
  itemHostType?: string;
  itemHostId?: number;
  groupItemId?: number;
  segmentId?: number;
  clipStartSec?: number;
  clipEndSec?: number | null;
  autoplay?: boolean;
  muted?: boolean;
  fullscreen?: boolean;
  playbackRate?: number;
  route?: string;
  referrer?: string;
  recommendationSource?: string;
  context?: Record<string, unknown>;
}

export interface PlaybackTrackingBatch extends PlaybackIntervalsRequest {}

export type PlaybackTrackingMode = "default" | "keepalive";

interface PlaybackTrackingSnapshot {
  target: PlaybackTrackingTarget;
  sessionId: string;
  mediaDurationSec: number;
  currentPositionSec: number;
  state: string;
  intervals: PlaybackIntervalInput[];
}

interface PlaybackTrackerOptions {
  flushIntervalMs?: number;
  maxBatchSize?: number;
  sendBatch?: (batch: PlaybackTrackingBatch, mode: PlaybackTrackingMode) => Promise<void>;
}

function buildKeepaliveHeaders() {
  const headers = new Headers({ "Content-Type": "application/json" });
  const shareToken = authStore.getShareToken();
  const sharePassword = authStore.getSharePassword();
  const accessToken = authStore.getAccessToken();

  if (shareToken) {
    headers.set("X-Share-Token", shareToken);
    if (sharePassword) {
      headers.set("X-Share-Password", sharePassword);
    }
  } else if (accessToken) {
    headers.set("Authorization", `Bearer ${accessToken}`);
  }

  return headers;
}

async function sendPlaybackBatch(batch: PlaybackTrackingBatch, mode: PlaybackTrackingMode) {
  if (import.meta.env.MODE === "test") {
    return;
  }

  if (mode === "keepalive") {
    await serverAwareFetch("/api/playback/intervals", {
      method: "POST",
      keepalive: true,
      headers: buildKeepaliveHeaders(),
      body: JSON.stringify(batch),
    });
    return;
  }

  await playback.recordIntervals(batch);
}

export function createPlaybackSessionId() {
  if (typeof crypto !== "undefined" && typeof crypto.randomUUID === "function") {
    return crypto.randomUUID();
  }

  return "xxxxxxxx-xxxx-4xxx-yxxx-xxxxxxxxxxxx".replace(/[xy]/g, (character) => {
    const random = (Math.random() * 16) | 0;
    const value = character === "x" ? random : (random & 0x3) | 0x8;
    return value.toString(16);
  });
}

function toScopeKey(target: PlaybackTrackingTarget | null) {
  return target?.scopeKey ?? (target ? `${target.hostType}:${target.hostId}` : null);
}

function targetSignature(target: PlaybackTrackingTarget | null) {
  if (!target) {
    return null;
  }

  return JSON.stringify({
    hostType: target.hostType,
    hostId: target.hostId,
    surface: target.surface,
    scopeKey: target.scopeKey,
    parentHostType: target.parentHostType,
    parentHostId: target.parentHostId,
    itemHostType: target.itemHostType,
    itemHostId: target.itemHostId,
    groupItemId: target.groupItemId,
    segmentId: target.segmentId,
    clipStartSec: target.clipStartSec,
    clipEndSec: target.clipEndSec,
    autoplay: target.autoplay,
    muted: target.muted,
    fullscreen: target.fullscreen,
    playbackRate: target.playbackRate,
    route: target.route,
    referrer: target.referrer,
    recommendationSource: target.recommendationSource,
    context: target.context,
  });
}

function applyTargetContext(batch: PlaybackTrackingBatch, target: PlaybackTrackingTarget) {
  const assign = <K extends keyof PlaybackTrackingBatch>(key: K, value: PlaybackTrackingBatch[K]) => {
    if (value !== undefined) {
      batch[key] = value;
    }
  };

  assign("surface", target.surface);
  assign("scopeKey", target.scopeKey);
  assign("parentHostType", target.parentHostType);
  assign("parentHostId", target.parentHostId);
  assign("itemHostType", target.itemHostType);
  assign("itemHostId", target.itemHostId);
  assign("groupItemId", target.groupItemId);
  assign("segmentId", target.segmentId);
  assign("clipStartSec", target.clipStartSec);
  assign("clipEndSec", target.clipEndSec);
  assign("autoplay", target.autoplay);
  assign("muted", target.muted);
  assign("fullscreen", target.fullscreen);
  assign("playbackRate", target.playbackRate);
  assign("route", target.route);
  assign("referrer", target.referrer);
  assign("recommendationSource", target.recommendationSource);
  assign("context", target.context);
}

export function createPlaybackTracker(options: PlaybackTrackerOptions = {}) {
  const flushIntervalMs = options.flushIntervalMs ?? 5000;
  const maxBatchSize = options.maxBatchSize ?? 20;
  const dispatchBatch = options.sendBatch ?? sendPlaybackBatch;

  let target: PlaybackTrackingTarget | null = null;
  let scopeKey: string | null = null;
  let signature: string | null = null;
  let sessionId = createPlaybackSessionId();
  let mediaDurationSec = 0;
  let currentPositionSec = 0;
  let state = "active";
  let queuedIntervals: PlaybackIntervalInput[] = [];
  let flushTimer: ReturnType<typeof setTimeout> | null = null;

  function clearFlushTimer() {
    if (flushTimer !== null) {
      clearTimeout(flushTimer);
      flushTimer = null;
    }
  }

  function scheduleFlush() {
    if (flushTimer !== null || queuedIntervals.length === 0) {
      return;
    }

    flushTimer = window.setTimeout(() => {
      void flush();
    }, flushIntervalMs);
  }

  function takeSnapshot(): PlaybackTrackingSnapshot | null {
    if (!target || queuedIntervals.length === 0) {
      clearFlushTimer();
      return null;
    }

    const snapshot: PlaybackTrackingSnapshot = {
      target,
      sessionId,
      mediaDurationSec,
      currentPositionSec,
      state,
      intervals: queuedIntervals,
    };

    queuedIntervals = [];
    clearFlushTimer();
    return snapshot;
  }

  async function dispatchSnapshot(snapshot: PlaybackTrackingSnapshot, mode: PlaybackTrackingMode) {
    for (let index = 0; index < snapshot.intervals.length; index += maxBatchSize) {
      const batchIntervals = snapshot.intervals.slice(index, index + maxBatchSize);
      const batch: PlaybackTrackingBatch = {
        hostType: snapshot.target.hostType,
        hostId: snapshot.target.hostId,
        sessionId: snapshot.sessionId,
        mediaDurationSec: snapshot.mediaDurationSec,
        currentPositionSec: snapshot.currentPositionSec,
        state: snapshot.state,
        intervals: batchIntervals,
      };
      applyTargetContext(batch, snapshot.target);

      try {
        await dispatchBatch(batch, mode);
      } catch {}
    }
  }

  async function flush(mode: PlaybackTrackingMode = "default") {
    const snapshot = takeSnapshot();
    if (!snapshot) {
      return;
    }

    await dispatchSnapshot(snapshot, mode);
  }

  return {
    getSessionId() {
      return sessionId;
    },
    async setTarget(nextTarget: PlaybackTrackingTarget | null) {
      const nextScopeKey = toScopeKey(nextTarget);
      const nextSignature = targetSignature(nextTarget);
      const scopeChanged = nextScopeKey !== scopeKey;
      const targetChanged = !scopeChanged && signature !== nextSignature;

      const pendingFlush = scopeChanged || targetChanged ? flush() : Promise.resolve();

      if (scopeChanged) {
        sessionId = createPlaybackSessionId();
        mediaDurationSec = 0;
        currentPositionSec = 0;
        state = "active";
      }

      target = nextTarget;
      scopeKey = nextScopeKey;
      signature = nextSignature;
      await pendingFlush;
    },
    recordInterval(params: {
      startSec: number;
      endSec: number;
      mediaDurationSec: number;
      currentPositionSec: number;
      state: string;
      mode?: PlaybackTrackingMode;
    }) {
      if (!target) {
        return;
      }

      const startSec = Math.max(0, params.startSec);
      const endSec = Math.max(startSec, params.endSec);
      if (endSec <= startSec) {
        return;
      }

      mediaDurationSec = Math.max(0, params.mediaDurationSec);
      currentPositionSec = Math.max(0, params.currentPositionSec);
      state = params.state;
      queuedIntervals.push({ startSec, endSec });

      if (params.mode === "keepalive") {
        void flush("keepalive");
        return;
      }

      if (queuedIntervals.length >= maxBatchSize) {
        void flush();
        return;
      }

      scheduleFlush();
    },
    async flush(stateOverride?: string, mode: PlaybackTrackingMode = "default") {
      if (stateOverride) {
        state = stateOverride;
      }

      await flush(mode);
    },
    async dispose() {
      await flush("keepalive");
      clearFlushTimer();
    },
  };
}

export function trackInteraction(payload: EngagementInteractionWrite) {
  if (import.meta.env.MODE === "test") {
    return;
  }

  void entityEngagement.recordInteraction(payload).catch((error) => {
    if (import.meta.env.DEV) {
      console.warn("Failed to record interaction", payload, error);
    }
  });
}
