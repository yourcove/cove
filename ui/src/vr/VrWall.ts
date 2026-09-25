/**
 * The video list as a wall in the headset: the same page of the same list the browser shows, as a
 * curved grid of cards. Move around with the thumbstick or by pointing; the focused card plays its
 * preview; the trigger opens it. Opening a card takes the browser to the video's page and hands the
 * session to its player, which draws into this wall's renderer from inside this wall's frame loop;
 * back in the video returns to the wall and brings the browser back to the list.
 *
 * Loaded on demand, since three.js is the only reason this file is heavy.
 */
import * as THREE from "three";
import type { Video } from "../api/types";
import { navigateToUrl } from "../router/location";
import {
  cancelSessionHandoff,
  formatVrLayout,
  offerSessionHandoff,
  requestImmersiveSession,
  type SessionPresenter,
  type SessionReturnReason,
} from "./immersiveVideo";
import type { VrListSource } from "./vrListRegistry";

// Layout, in metres. Everything sits on a cylinder around the viewer so it faces them at the same distance.
const RADIUS = 2.6;
const GAP = 0.05;
// Cards keep a 16:9 cover with a band under it for the title, details and performers.
const BAND_RATIO = 0.15 / 0.66;
const COVER_RATIO = 9 / 16;
// The rows sit around this height.
const GRID_CENTRE_Y = -0.02;
// The band of heights the focused row is kept within by scrolling the grid.
const VIEW_TOP = 0.62;
const VIEW_BOTTOM = -0.66;

/** Card sizes to pick from on the toolbar; every page fits in view at once. */
interface CardSize {
  label: string;
  width: number;
  columns: number;
  rows: number;
}
export const CARD_SIZES: readonly CardSize[] = [
  { label: "S", width: 0.5, columns: 8, rows: 3 },
  { label: "M", width: 0.66, columns: 6, rows: 3 },
  { label: "L", width: 0.9, columns: 4, rows: 2 },
];
const DEFAULT_SIZE = 1;
const TOOLBAR_Y = 1.0;
const HEADER_Y = 1.2;
const BUTTON_HEIGHT = 0.12;
const FOCUS_SCALE = 1.06;
const FOCUS_RING = 0.03;
// Rest on a card this long before its preview clip starts playing on it.
const PREVIEW_DELAY_MS = 350;
// Thumbstick: deflection that counts as a move, and how often a held stick repeats.
const STICK_THRESHOLD = 0.6;
const STICK_REPEAT_MS = 320;
// How long an offered session waits for the video page before the wall takes over again.
const HANDOFF_TIMEOUT_MS = 10_000;

// Canvas texture size per card. 512 wide is sharp at these sizes on current headsets without costing much memory.
const TEXTURE_WIDTH = 512;
const TEXTURE_HEIGHT = Math.round(TEXTURE_WIDTH * (COVER_RATIO + BAND_RATIO));
const COVER_PX = Math.round(TEXTURE_WIDTH * COVER_RATIO);

// xr-standard gamepad: 5 is B/Y, 4 is A/X, 3 is the thumbstick click. B or the stick click leaves;
// A opens the focused card like the trigger. Never the grip: on the Valve Index it reads as pressed
// while the controller is simply held.
const BACK_BUTTONS = [5, 3];
const OPEN_BUTTON = 4;

const STEREO_CARDS_KEY = "cove.vr.wall.stereo";
const SHOW_FLAT_KEY = "cove.vr.wall.flat";
const SIZE_KEY = "cove.vr.wall.size";

type Action =
  | { kind: "video"; video: Video }
  | { kind: "page"; delta: number }
  | { kind: "stereo" }
  | { kind: "flat" }
  | { kind: "size"; index: number };

type Plane = THREE.Mesh<THREE.PlaneGeometry, THREE.MeshBasicMaterial>;

/** Anything the pointer or the stick can land on: a card or a toolbar button. */
interface Widget {
  mesh: Plane;
  action: Action;
  /** Where it is on the wall, for stick navigation; cards' y is before scrolling. */
  angle: number;
  y: number;
  scrolls: boolean;
  ring: Plane;
  /** The plane a card's preview clip plays on, made when the card is first focused. */
  preview: Plane | null;
  dispose: () => void;
}

export interface VrWallHooks {
  /** Bring the browser to a video's page. */
  navigate: (target: { page: string; id?: number }) => void;
  /** The session ended, whichever side ended it. */
  onEnd: () => void;
  onError: (message: string) => void;
  /** The headset went to a video (the browser is on its page) or came back to the wall. */
  onStateChange: (state: WallState) => void;
}

export type WallState = "browsing" | "watching";

// One wall at a time. It outlives the list page so the browser can show a video's page while the
// headset stays in the session, and so the page can pick the wall up again when it comes back.
let activeWall: VrWall | null = null;

export function getActiveWall(): VrWall | null {
  return activeWall;
}

function setActiveWall(wall: VrWall | null) {
  activeWall = wall;
}

// One renderer for every wall session on the page. A fresh WebGL context per session leaves desktop
// Chrome unable to start a second immersive session until the page is reloaded.
let sharedRenderer: THREE.WebGLRenderer | null = null;

function getRenderer(): THREE.WebGLRenderer {
  if (sharedRenderer) return sharedRenderer;
  const renderer = new THREE.WebGLRenderer({ antialias: true, alpha: false });
  renderer.xr.enabled = true;
  renderer.xr.setReferenceSpaceType("local");
  renderer.setPixelRatio(1);
  sharedRenderer = renderer;
  return renderer;
}

// ---- Theme: the same colours as the page, read from Cove's CSS variables -------------------------------------

interface Theme {
  background: string;
  card: string;
  border: string;
  foreground: string;
  secondary: string;
  muted: string;
  accent: string;
}

function readTheme(): Theme {
  const style = getComputedStyle(document.documentElement);
  const read = (name: string, fallback: string) => style.getPropertyValue(name).trim() || fallback;
  return {
    background: read("--color-background", "#16181d"),
    card: read("--color-card", "#1e2028"),
    border: read("--color-border", "#2a2d38"),
    foreground: read("--color-foreground", "#e8eaf0"),
    secondary: read("--color-secondary", "#9ea3b0"),
    muted: read("--color-muted", "#6b7085"),
    accent: read("--color-accent", "#4f8ff7"),
  };
}

function readFlag(key: string, fallback: boolean): boolean {
  try {
    const value = localStorage.getItem(key);
    return value == null ? fallback : value === "1";
  } catch {
    return fallback;
  }
}

function writeFlag(key: string, value: boolean) {
  try {
    localStorage.setItem(key, value ? "1" : "0");
  } catch {
    // The choice still applies to this session.
  }
}

function readSize(): number {
  try {
    const value = Number(localStorage.getItem(SIZE_KEY));
    return Number.isInteger(value) && value >= 0 && value < CARD_SIZES.length ? value : DEFAULT_SIZE;
  } catch {
    return DEFAULT_SIZE;
  }
}

export class VrWall {
  private readonly renderer = getRenderer();
  private readonly theme = readTheme();
  private readonly scene = new THREE.Scene();
  private readonly camera = new THREE.PerspectiveCamera(70, 1, 0.05, 100);
  private readonly cardGroup = new THREE.Group();
  private readonly raycaster = new THREE.Raycaster();
  private readonly controllers: THREE.XRTargetRaySpace[] = [];
  private readonly controllerListeners: { controller: THREE.XRTargetRaySpace; listener: () => void }[] = [];
  private readonly exitHeld = new Set<string>();
  private readonly openHeld = new Set<string>();
  private readonly stickMovedAt = new Map<string, number>();
  private readonly describedSources = new WeakSet<object>();
  private readonly previewVideo = document.createElement("video");
  private previewTextures: { left: THREE.VideoTexture; right: THREE.VideoTexture } | null = null;
  private previewWidget: Widget | null = null;
  private previewTimer: number | null = null;
  private videos: Video[] = [];
  private cards: Widget[] = [];
  private toolbar: Widget[] = [];
  private focused: Widget | null = null;
  private header: Plane | null = null;
  private page: number;
  /** The browser page we last asked for, so its echo is not taken for the user paging in the browser. */
  private announcedBrowserPage: number | null = null;
  private totalCount = 0;
  private scrollTarget = 0;
  private stereoCards = readFlag(STEREO_CARDS_KEY, true);
  private showFlat = readFlag(SHOW_FLAT_KEY, false);
  private sizeIndex = readSize();
  private hasBackButton = false;
  private session: XRSession | null = null;
  private loading = false;
  private disposed = false;
  private stateValue: WallState = "browsing";
  private hooks: VrWallHooks;
  /** While a video page borrows the session, it draws the video from inside our frame loop. */
  private frameRenderer: ((frame: object) => void) | null = null;

  constructor(
    private source: VrListSource,
    /** The list page's path and query, so leaving a video brings the browser back to it. */
    private returnUrl: string,
    hooks: VrWallHooks,
  ) {
    this.hooks = hooks;
    this.page = wallPageFor(source, this.pageSize);
    this.scene.background = new THREE.Color(this.theme.background);
    this.scene.add(this.cardGroup);
    this.scene.add(createFloorRing(this.theme.border));

    this.previewVideo.muted = true;
    this.previewVideo.loop = true;
    this.previewVideo.playsInline = true;
    this.previewVideo.preload = "auto";
    this.previewVideo.addEventListener("loadedmetadata", () => this.onPreviewReady());
    this.previewVideo.addEventListener("error", () => {
      // No clip yet (Cove makes them in the generate job, never on request): the card keeps its still.
      this.previewWidget = null;
    });

    for (const index of [0, 1]) {
      const controller = this.renderer.xr.getController(index);
      controller.clear();
      controller.add(createPointerRay(this.theme.accent));
      const listener = () => this.onSelect(controller);
      controller.addEventListener("select", listener);
      this.controllerListeners.push({ controller, listener });
      this.scene.add(controller);
      this.controllers.push(controller);
    }
  }

  get state(): WallState {
    return this.stateValue;
  }

  private get size(): CardSize {
    return CARD_SIZES[this.sizeIndex] ?? CARD_SIZES[DEFAULT_SIZE]!;
  }

  /** Cards per wall page: a full grid at the chosen size, all in view at once. */
  private get pageSize(): number {
    return this.size.columns * this.size.rows;
  }

  /** Lets a newly mounted list page receive this wall's events and become where "back" leads. */
  attachPage(hooks: VrWallHooks, url: string) {
    this.hooks = hooks;
    this.returnUrl = url;
  }

  /**
   * The browser's list changed: a different list, page or filters, or a different list page
   * altogether. A new list reloads from its start; the browser paging by itself jumps the wall to the
   * wall page holding that browser page's first item; our own page announcements are ignored.
   */
  setSource(source: VrListSource, url?: string) {
    const previous = this.source;
    this.source = source;
    if (url) this.returnUrl = url;
    if (source.key !== previous.key) {
      this.announcedBrowserPage = null;
      this.page = wallPageFor(source, this.pageSize);
      void this.loadPage(this.page, false);
      return;
    }
    if (source.page !== previous.page && source.page !== this.announcedBrowserPage) {
      const target = wallPageFor(source, this.pageSize);
      if (target !== this.page) {
        this.page = target;
        void this.loadPage(this.page, false);
      }
    }
  }

  /**
   * Shows the wall. Pass a live session to take one over (a video page handing back the session it
   * started); otherwise a new one is requested, which must happen from a click.
   */
  async start(existingSession?: object): Promise<void> {
    if (activeWall && activeWall !== this) await activeWall.end();
    setActiveWall(this);
    const session = (existingSession ?? (await requestImmersiveSession())) as XRSession;
    this.session = session;
    session.addEventListener("end", () => this.dispose());
    await this.renderer.xr.setSession(session);
    this.renderer.setAnimationLoop(this.renderFrame);
    this.buildToolbar();
    await this.loadPage(this.page, false);
  }

  async end(): Promise<void> {
    cancelSessionHandoff();
    await this.session?.end().catch(() => {});
    this.dispose();
  }

  // ---- Frame loop and input -----------------------------------------------------------------------------------

  private readonly renderFrame = (_time: number, frame?: XRFrame) => {
    if (this.frameRenderer) {
      // The video page paints into our layer with our context; our own scene stays out of the way.
      if (frame) this.frameRenderer(frame);
      this.renderer.resetState();
      return;
    }
    this.pollButtons();
    this.pollStick();
    this.pollPointer();
    // Ease the grid towards the row the focus is on.
    this.cardGroup.position.y += (this.scrollTarget - this.cardGroup.position.y) * 0.18;
    this.renderer.render(this.scene, this.camera);
  };

  /** What we lend to the video page: our context, layer, reference space and a slot in the frame loop. */
  private presenter(): SessionPresenter | undefined {
    const layer = this.renderer.xr.getBaseLayer();
    const referenceSpace = this.renderer.xr.getReferenceSpace();
    if (!layer || !referenceSpace) return undefined;
    return {
      gl: this.renderer.getContext() as WebGL2RenderingContext,
      layer,
      referenceSpace,
      setFrameRenderer: (render) => {
        this.frameRenderer = render;
      },
    };
  }

  private pollButtons() {
    for (const source of this.session?.inputSources ?? []) {
      if (!this.describedSources.has(source)) {
        this.describedSources.add(source);
        if ((source.gamepad?.buttons.length ?? 0) > 5 && !this.hasBackButton) {
          this.hasBackButton = true;
          this.refreshHeader();
        }
      }
      const pressed = BACK_BUTTONS.some((index) => source.gamepad?.buttons[index]?.pressed ?? false);
      if (pressed && !this.exitHeld.has(source.handedness)) void this.end();
      if (pressed) this.exitHeld.add(source.handedness);
      else this.exitHeld.delete(source.handedness);

      const open = source.gamepad?.buttons[OPEN_BUTTON]?.pressed ?? false;
      if (open && !this.openHeld.has(source.handedness) && this.focused && this.stateValue === "browsing") {
        this.activate(this.focused.action);
      }
      if (open) this.openHeld.add(source.handedness);
      else this.openHeld.delete(source.handedness);
    }
  }

  /** The thumbstick moves the focus between cards and buttons, like arrow keys on the list page. */
  private pollStick() {
    for (const source of this.session?.inputSources ?? []) {
      const axes = source.gamepad?.axes;
      if (!axes) continue;
      // Only the thumbstick (axes 2/3); the Index's touchpad reads a resting thumb as input.
      const [x, y] = axes.length >= 4 ? [axes[2] ?? 0, axes[3] ?? 0] : [0, 0];
      const key = source.handedness;
      if (Math.abs(x) < STICK_THRESHOLD && Math.abs(y) < STICK_THRESHOLD) {
        this.stickMovedAt.delete(key);
        continue;
      }
      const now = performance.now();
      const last = this.stickMovedAt.get(key);
      if (last != null && now - last < STICK_REPEAT_MS) continue;
      this.stickMovedAt.set(key, now);
      const direction = Math.abs(x) >= Math.abs(y) ? (x > 0 ? "right" : "left") : y < 0 ? "up" : "down";
      this.moveFocus(direction);
    }
  }

  /** Pointing at something focuses it; the stick then carries on from there. */
  private pollPointer() {
    for (const controller of this.controllers) {
      if (!controller.visible) continue;
      const hit = this.hitWidget(controller);
      if (hit && hit !== this.focused) {
        this.setFocus(hit);
        return;
      }
    }
  }

  private get widgets(): Widget[] {
    return [...this.cards, ...this.toolbar];
  }

  private hitWidget(controller: THREE.XRTargetRaySpace): Widget | null {
    this.raycaster.setFromXRController(controller);
    const widgets = this.widgets;
    const [intersection] = this.raycaster.intersectObjects(
      widgets.map((widget) => widget.mesh),
      false,
    );
    return intersection ? (widgets.find((widget) => widget.mesh === intersection.object) ?? null) : null;
  }

  private worldY(widget: Widget): number {
    return widget.y + (widget.scrolls ? this.scrollTarget : 0);
  }

  private moveFocus(direction: "left" | "right" | "up" | "down") {
    const from = this.focused ?? this.cards[0] ?? this.toolbar[0];
    if (!from) return;
    if (!this.focused) {
      this.setFocus(from);
      return;
    }
    // Along the wall, one metre of arc counts the same as one metre of height; off-axis distance is
    // penalised so the stick lands on the neighbour, not the nearest thing in a diagonal.
    let best: Widget | null = null;
    let bestScore = Infinity;
    for (const widget of this.widgets) {
      if (widget === from) continue;
      const dx = (widget.angle - from.angle) * RADIUS;
      const dy = this.worldY(widget) - this.worldY(from);
      let along: number;
      let across: number;
      switch (direction) {
        case "right":
          along = dx;
          across = dy;
          break;
        case "left":
          along = -dx;
          across = dy;
          break;
        case "up":
          along = dy;
          across = dx;
          break;
        case "down":
          along = -dy;
          across = dx;
          break;
      }
      if (along < 0.02) continue;
      const score = along + Math.abs(across) * 2.5;
      if (score < bestScore) {
        bestScore = score;
        best = widget;
      }
    }
    if (best) this.setFocus(best);
  }

  private setFocus(widget: Widget | null) {
    if (widget === this.focused) return;
    if (this.focused) {
      this.focused.mesh.scale.setScalar(1);
      this.focused.ring.visible = false;
    }
    this.focused = widget;
    this.stopPreview();
    if (!widget) return;
    widget.mesh.scale.setScalar(FOCUS_SCALE);
    widget.ring.visible = true;
    if (widget.scrolls) {
      // Keep the focused row inside the view band.
      const cardHeight = this.size.width * (COVER_RATIO + BAND_RATIO);
      const top = widget.y + cardHeight / 2 + this.scrollTarget;
      const bottom = widget.y - cardHeight / 2 + this.scrollTarget;
      if (top > VIEW_TOP) this.scrollTarget -= top - VIEW_TOP;
      else if (bottom < VIEW_BOTTOM) this.scrollTarget += VIEW_BOTTOM - bottom;
    }
    if (widget.action.kind === "video") {
      this.previewTimer = window.setTimeout(() => this.startPreview(widget), PREVIEW_DELAY_MS);
    }
  }

  private onSelect(controller: THREE.XRTargetRaySpace) {
    if (this.stateValue !== "browsing" || this.disposed) return;
    // The trigger acts on what the controller points at, or on what the stick moved the focus to.
    const widget = this.hitWidget(controller) ?? this.focused;
    if (widget) this.activate(widget.action);
  }

  private activate(action: Action) {
    switch (action.kind) {
      case "video":
        if (!this.loading) this.openVideo(action.video);
        break;
      case "page": {
        const pages = Math.max(1, Math.ceil(this.totalCount / this.pageSize));
        const next = this.page + action.delta;
        if (!this.loading && next >= 1 && next <= pages) void this.loadPage(next, true);
        break;
      }
      case "size": {
        if (action.index === this.sizeIndex || this.loading) break;
        // Keep the first card in view on the new grid.
        const firstItem = (this.page - 1) * this.pageSize;
        this.sizeIndex = action.index;
        try {
          localStorage.setItem(SIZE_KEY, String(action.index));
        } catch {
          // The choice still applies to this session.
        }
        void this.loadPage(Math.floor(firstItem / this.pageSize) + 1, true);
        break;
      }
      case "stereo":
        this.stereoCards = !this.stereoCards;
        writeFlag(STEREO_CARDS_KEY, this.stereoCards);
        this.layoutCards();
        this.refreshToolbar();
        break;
      case "flat":
        // VR only is a server-side restriction, so the page and count change with it.
        this.showFlat = !this.showFlat;
        writeFlag(SHOW_FLAT_KEY, this.showFlat);
        void this.loadPage(1, true);
        break;
    }
  }

  // ---- Toolbar ---------------------------------------------------------------------------------------------------

  private button(label: string, width: number, angle: number, y: number, action: Action, accent = false): Widget {
    const mesh = new THREE.Mesh(
      new THREE.PlaneGeometry(width, BUTTON_HEIGHT),
      new THREE.MeshBasicMaterial({
        map: labelTexture(
          label,
          width,
          BUTTON_HEIGHT,
          accent ? this.theme.accent : this.theme.card,
          this.theme.foreground,
        ),
      }),
    );
    placeOnWall(mesh, angle, y);
    const ring = makeRing(width, BUTTON_HEIGHT, this.theme.accent);
    mesh.add(ring);
    this.scene.add(mesh);
    return { mesh, action, angle, y, scrolls: false, ring, preview: null, dispose: () => mesh.material.map?.dispose() };
  }

  private buildToolbar() {
    const focusedAction = this.focused && this.toolbar.includes(this.focused) ? this.focused.action.kind : null;
    this.clearWidgets(this.toolbar);
    const pages = Math.max(1, Math.ceil(this.totalCount / this.pageSize));
    const buttons: { label: string; width: number; action: Action; accent?: boolean }[] = [
      { label: "◀", width: 0.14, action: { kind: "page", delta: -1 } },
      { label: `Page ${this.page} / ${pages}`, width: 0.42, action: { kind: "page", delta: 0 } },
      { label: "▶", width: 0.14, action: { kind: "page", delta: 1 } },
      {
        label: this.stereoCards ? "Cards: 3D" : "Cards: 2D",
        width: 0.34,
        action: { kind: "stereo" },
        accent: this.stereoCards,
      },
      {
        label: this.showFlat ? "Showing: all videos" : "Showing: VR only",
        width: 0.5,
        action: { kind: "flat" },
        accent: this.showFlat,
      },
      ...CARD_SIZES.map((size, index) => ({
        label: size.label,
        width: 0.12,
        action: { kind: "size", index } as Action,
        accent: index === this.sizeIndex,
      })),
    ];
    const total = buttons.reduce((sum, button) => sum + button.width, 0) + GAP * (buttons.length - 1);
    let offset = -total / 2;
    for (const button of buttons) {
      const centre = offset + button.width / 2;
      const widget = this.button(button.label, button.width, centre / RADIUS, TOOLBAR_Y, button.action, button.accent);
      this.toolbar.push(widget);
      if (focusedAction && button.action.kind === focusedAction) this.setFocus(widget);
      offset += button.width + GAP;
    }
  }

  private refreshToolbar() {
    if (this.toolbar.length > 0) this.buildToolbar();
  }

  // ---- Previews: the focused card plays its clip, one eye per half for VR videos ------------------------------

  private startPreview(widget: Widget) {
    if (this.disposed || widget.action.kind !== "video" || this.focused !== widget) return;
    this.previewWidget = widget;
    this.previewVideo.src = previewUrl(widget.action.video);
    this.previewVideo.load();
  }

  private onPreviewReady() {
    const widget = this.previewWidget;
    if (!widget || this.focused !== widget) return;
    const video = this.previewVideo;
    // Two 16:9 views side by side, or a single view for mono and flat videos.
    const stereo = video.videoWidth >= video.videoHeight * 3;
    if (!this.previewTextures) {
      const make = () => {
        const texture = new THREE.VideoTexture(video);
        texture.colorSpace = THREE.SRGBColorSpace;
        return texture;
      };
      this.previewTextures = { left: make(), right: make() };
    }
    const { left, right } = this.previewTextures;
    left.repeat.set(stereo ? 0.5 : 1, 1);
    left.offset.set(0, 0);
    right.repeat.set(stereo ? 0.5 : 1, 1);
    // In 2D mode both eyes see the left view.
    right.offset.set(stereo && this.stereoCards ? 0.5 : 0, 0);

    if (!widget.preview) {
      const width = this.size.width;
      const coverHeight = width * COVER_RATIO;
      const cardHeight = width * (COVER_RATIO + BAND_RATIO);
      const plane = new THREE.Mesh(
        new THREE.PlaneGeometry(width, coverHeight),
        new THREE.MeshBasicMaterial({ map: left }),
      );
      plane.position.set(0, cardHeight / 2 - coverHeight / 2, 0.003);
      plane.onBeforeRender = (_renderer, _scene, camera) => {
        plane.material.map = isRightEye(camera) ? right : left;
      };
      widget.mesh.add(plane);
      widget.preview = plane;
    }
    widget.preview.visible = true;
    void video.play().catch(() => {});
  }

  private stopPreview() {
    if (this.previewTimer != null) {
      window.clearTimeout(this.previewTimer);
      this.previewTimer = null;
    }
    if (this.previewWidget?.preview) this.previewWidget.preview.visible = false;
    this.previewWidget = null;
    if (this.previewVideo.src) {
      this.previewVideo.pause();
      this.previewVideo.removeAttribute("src");
      this.previewVideo.load();
    }
  }

  // ---- Opening a video --------------------------------------------------------------------------------------

  /**
   * Hands the session to the video's page: the browser navigates there, its player claims the session
   * and paints into our loop, and when the viewer presses back the page gives the session back and we
   * resume, bringing the browser back to the list.
   */
  private openVideo(video: Video) {
    const session = this.session;
    if (!session) return;
    this.stopPreview();
    this.setState("watching");
    offerSessionHandoff(session, {
      presenter: this.presenter(),
      timeoutMs: HANDOFF_TIMEOUT_MS,
      onReturn: (reason) => this.resume(reason),
      onTimeout: () => {
        this.hooks.onError("The video page did not take over the headset; back to the list.");
        this.resume("abandoned");
      },
    });
    this.hooks.navigate({ page: "video", id: video.id });
  }

  /** Shows the wall again. After a deliberate exit the browser comes back to the list page too. */
  private resume(reason: SessionReturnReason) {
    if (this.disposed) return;
    // The button that brought us here may still be held; don't let it act on the wall as well.
    for (const source of this.session?.inputSources ?? []) {
      this.exitHeld.add(source.handedness);
      this.openHeld.add(source.handedness);
    }
    this.frameRenderer = null;
    this.renderer.resetState();
    this.setState("browsing");
    if (reason === "exit") navigateToUrl(this.returnUrl);
  }

  private setState(state: WallState) {
    this.stateValue = state;
    this.hooks.onStateChange(state);
  }

  // ---- The wall -----------------------------------------------------------------------------------------------

  private async loadPage(page: number, announce: boolean) {
    this.loading = true;
    const source = this.source;
    try {
      const result = await source.fetchPage(page, this.pageSize, !this.showFlat);
      // The list changed while this page was on its way; whatever replaced it is loading its own.
      if (this.disposed || this.source !== source) return;
      this.page = page;
      this.totalCount = result.totalCount;
      this.videos = result.items;
      this.scrollTarget = 0;
      this.cardGroup.position.y = 0;
      this.layoutCards();
      this.refreshToolbar();
      if (announce && source.setPage) {
        // Keep the browser on the page holding this wall page's first card.
        const browserPage = Math.floor(((page - 1) * this.pageSize) / Math.max(1, source.perPage)) + 1;
        if (browserPage !== source.page) {
          this.announcedBrowserPage = browserPage;
          source.setPage(browserPage);
        }
      }
    } catch (error) {
      this.hooks.onError(error instanceof Error ? error.message : String(error));
    } finally {
      this.loading = false;
    }
  }

  private layoutCards() {
    const focusedId = this.focused?.action.kind === "video" ? this.focused.action.video.id : null;
    this.clearWidgets(this.cards);
    const { width, columns, rows } = this.size;
    const cardHeight = width * (COVER_RATIO + BAND_RATIO);
    const step = (width + GAP) / RADIUS;
    this.videos.forEach((video, index) => {
      const row = Math.floor(index / columns);
      const column = index % columns;
      const angle = (column - (columns - 1) / 2) * step;
      const y = GRID_CENTRE_Y + ((rows - 1) / 2 - row) * (cardHeight + GAP);
      const mesh = new THREE.Mesh(
        new THREE.PlaneGeometry(width, cardHeight),
        new THREE.MeshBasicMaterial({ color: 0xffffff }),
      );
      placeOnWall(mesh, angle, y);
      const textures = videoCardTextures(video, this.stereoCards, this.theme);
      mesh.material.map = textures.left;
      // Each eye samples its own view of the scene, which is what makes a VR card look solid.
      mesh.onBeforeRender = (_renderer, _scene, camera) => {
        mesh.material.map = isRightEye(camera) ? textures.right : textures.left;
      };
      mesh.material.needsUpdate = true;
      const ring = makeRing(width, cardHeight, this.theme.accent);
      mesh.add(ring);
      this.cardGroup.add(mesh);
      const widget: Widget = {
        mesh,
        action: { kind: "video", video },
        angle,
        y,
        scrolls: true,
        ring,
        preview: null,
        dispose: textures.dispose,
      };
      this.cards.push(widget);
      if (video.id === focusedId) this.setFocus(widget);
    });
    if (!this.focused && this.cards[0]) this.setFocus(this.cards[0]);
    this.refreshHeader();
  }

  private refreshHeader() {
    const pages = Math.max(1, Math.ceil(this.totalCount / this.pageSize));
    this.header?.removeFromParent();
    disposeMesh(this.header);
    const back = this.hasBackButton ? "B or stick click" : "Stick click";
    const counts = this.showFlat ? `${this.totalCount} videos` : `${this.totalCount} VR videos`;
    this.header = createHeader(
      `${this.source.label}  ·  ${counts}  ·  page ${this.page}/${pages}`,
      `Stick: move  ·  Trigger or A: open  ·  ${back}: leave VR`,
      this.theme,
    );
    this.scene.add(this.header);
  }

  private clearWidgets(widgets: Widget[]) {
    if (widgets === this.cards) this.stopPreview();
    for (const widget of widgets) {
      if (this.focused === widget) this.focused = null;
      widget.mesh.removeFromParent();
      widget.dispose();
      widget.ring.geometry.dispose();
      widget.ring.material.dispose();
      if (widget.preview) {
        widget.preview.geometry.dispose();
        widget.preview.material.dispose();
      }
      widget.mesh.geometry.dispose();
      widget.mesh.material.dispose();
    }
    widgets.length = 0;
  }

  private dispose() {
    if (this.disposed) return;
    this.disposed = true;
    cancelSessionHandoff();
    this.renderer.setAnimationLoop(null);
    for (const { controller, listener } of this.controllerListeners) {
      controller.removeEventListener("select", listener);
      controller.clear();
      controller.removeFromParent();
    }
    this.clearWidgets(this.cards);
    this.clearWidgets(this.toolbar);
    this.previewTextures?.left.dispose();
    this.previewTextures?.right.dispose();
    this.previewTextures = null;
    disposeMesh(this.header);
    if (activeWall === this) setActiveWall(null);
    this.hooks.onEnd();
  }
}

/** The wall page holding the first item of the browser's current page. Exported for tests. */
export function wallPageFor(source: Pick<VrListSource, "page" | "perPage">, pageSize: number): number {
  const firstItem = Math.max(0, source.page - 1) * Math.max(1, source.perPage);
  return Math.floor(firstItem / Math.max(1, pageSize)) + 1;
}

// ---- URLs -----------------------------------------------------------------------------------------------------

const coverUrl = (video: Video, max = 640) =>
  `/api/videos/${video.id}/image?max=${max}&v=${encodeURIComponent(video.updatedAt)}`;
/** Both eyes' flat view of the scene side by side (each 16:9); made by the generate job. */
const stereoCardUrl = (video: Video) =>
  `/api/stream/video/${video.id}/vr-card?v=${encodeURIComponent(video.updatedAt)}`;
/** The stereoscopic clip for VR videos, the ordinary preview clip for flat ones. */
const previewUrl = (video: Video) =>
  video.isVr
    ? `/api/stream/video/${video.id}/vr-preview?v=${encodeURIComponent(video.updatedAt)}`
    : `/api/stream/video/${video.id}/preview?v=${encodeURIComponent(video.updatedAt)}`;

// ---- Geometry helpers -----------------------------------------------------------------------------------------

/** Puts a plane on the wall at an angle (radians, 0 = straight ahead) and height, facing the viewer. */
function placeOnWall(mesh: THREE.Object3D, angle: number, y: number, radius = RADIUS) {
  mesh.position.set(Math.sin(angle) * radius, y, -Math.cos(angle) * radius);
  mesh.lookAt(0, y, 0);
}

/** three.js enables layer 1 on the left XR camera and layer 2 on the right one. */
function isRightEye(camera: THREE.Camera): boolean {
  return camera.layers.isEnabled(2) && !camera.layers.isEnabled(1);
}

/** An accent-coloured plane just behind a widget, shown while it has the focus. */
function makeRing(width: number, height: number, colour: string): Plane {
  const ring = new THREE.Mesh(
    new THREE.PlaneGeometry(width + FOCUS_RING, height + FOCUS_RING),
    new THREE.MeshBasicMaterial({ color: colour }),
  );
  ring.position.z = -0.004;
  ring.visible = false;
  return ring;
}

// ---- Textures ----------------------------------------------------------------------------------------------

function toTexture(canvas: HTMLCanvasElement) {
  const texture = new THREE.CanvasTexture(canvas);
  texture.colorSpace = THREE.SRGBColorSpace;
  texture.anisotropy = 4;
  return texture;
}

/** A rounded label the size of a button; 512 px per metre keeps text crisp. */
function labelTexture(text: string, width: number, height: number, background: string, colour: string) {
  const canvas = document.createElement("canvas");
  canvas.width = Math.round(width * 512);
  canvas.height = Math.round(height * 512);
  const context = canvas.getContext("2d")!;
  context.fillStyle = background;
  roundedRect(context, 0, 0, canvas.width, canvas.height, 12);
  context.fill();
  context.fillStyle = colour;
  context.font = `600 ${Math.round(canvas.height * 0.42)}px system-ui, sans-serif`;
  context.textBaseline = "middle";
  context.textAlign = "center";
  context.fillText(ellipsize(context, text, canvas.width - 32), canvas.width / 2, canvas.height / 2);
  return toTexture(canvas);
}

function roundedRect(c: CanvasRenderingContext2D, x: number, y: number, w: number, h: number, r: number) {
  c.beginPath();
  c.moveTo(x + r, y);
  c.arcTo(x + w, y, x + w, y + h, r);
  c.arcTo(x + w, y + h, x, y + h, r);
  c.arcTo(x, y + h, x, y, r);
  c.arcTo(x, y, x + w, y, r);
  c.closePath();
}

interface CardTextures {
  left: THREE.CanvasTexture;
  right: THREE.CanvasTexture;
  dispose: () => void;
}

/** "8K", "4K", "1080p"… from the larger frame side, which for side-by-side VR is the width. */
export function resolutionLabel(width: number, height: number): string {
  const size = Math.max(width, height);
  if (size >= 7680) return "8K";
  if (size >= 5760) return "6K";
  if (size >= 4096) return "5K";
  if (size >= 3840) return "4K";
  if (size >= 2560) return "2.5K";
  if (size >= 1920) return "1080p";
  if (size >= 1280) return "720p";
  return `${height}p`;
}

function displayTitle(video: Video): string {
  if (video.title) return video.title;
  const file = video.files.find((candidate) => candidate.id === video.primaryFileId) ?? video.files[0];
  return file?.basename ?? `Video ${video.id}`;
}

/** The second line under a card's title, in the spirit of Cove's card footer. Exported for tests. */
export function cardDetails(
  video: Pick<Video, "studioName" | "date" | "files" | "primaryFileId"> & { rating?: number | null },
): string {
  const file = video.files.find((candidate) => candidate.id === video.primaryFileId) ?? video.files[0];
  const parts: string[] = [];
  if (typeof video.rating === "number") parts.push(`★ ${(video.rating / 20).toFixed(1)}`);
  if (video.studioName) parts.push(video.studioName);
  if (video.date) parts.push(video.date);
  if (file?.duration) parts.push(formatDuration(file.duration));
  if (file?.width && file?.height) parts.push(resolutionLabel(file.width, file.height));
  return parts.join("  ·  ");
}

/**
 * A card per eye. Both start with the flat cover; when Cove's stereoscopic card image arrives (each
 * eye's view side by side) the left half goes to the left eye and the right half to the right, so the
 * scene has depth on the wall. With 3D cards off, for mono videos and for flat videos, both eyes get
 * the same view.
 */
function videoCardTextures(video: Video, stereoCards: boolean, theme: Theme): CardTextures {
  const make = () => {
    const canvas = document.createElement("canvas");
    canvas.width = TEXTURE_WIDTH;
    canvas.height = TEXTURE_HEIGHT;
    return { context: canvas.getContext("2d")!, canvas };
  };
  const left = make();
  const right = make();
  const textures = { left: toTexture(left.canvas), right: toTexture(right.canvas) };
  const details = cardDetails(video);
  const performers = video.performers.map((performer) => performer.name).join(", ");

  const drawFrame = (
    target: { context: CanvasRenderingContext2D },
    texture: THREE.CanvasTexture,
    image?: HTMLImageElement,
    eye?: "left" | "right",
  ) => {
    const context = target.context;
    context.fillStyle = theme.card;
    context.fillRect(0, 0, TEXTURE_WIDTH, TEXTURE_HEIGHT);
    if (image) drawCover(context, image, TEXTURE_WIDTH, COVER_PX, eye);

    // Footer: title, details, performers.
    context.fillStyle = theme.card;
    context.fillRect(0, COVER_PX, TEXTURE_WIDTH, TEXTURE_HEIGHT - COVER_PX);
    context.fillStyle = theme.border;
    context.fillRect(0, COVER_PX, TEXTURE_WIDTH, 2);
    context.textAlign = "left";
    context.textBaseline = "middle";
    context.fillStyle = theme.foreground;
    context.font = "600 25px system-ui, sans-serif";
    context.fillText(ellipsize(context, displayTitle(video), TEXTURE_WIDTH - 24), 12, COVER_PX + 22);
    context.fillStyle = theme.secondary;
    context.font = "500 20px system-ui, sans-serif";
    context.fillText(ellipsize(context, details, TEXTURE_WIDTH - 24), 12, COVER_PX + 56);
    context.fillStyle = theme.accent;
    context.fillText(ellipsize(context, performers, TEXTURE_WIDTH - 24), 12, COVER_PX + 88);

    // Badge: layout for VR videos, "2D" for the rest.
    badge(context, video.isVr ? formatVrLayout(video.vr) : "2D", 10, 10);
    texture.needsUpdate = true;
  };

  const drawBoth = (image?: HTMLImageElement, imageIsStereo = false) => {
    // A side-by-side image is split per eye in 3D mode; otherwise both eyes get its left half.
    drawFrame(left, textures.left, image, imageIsStereo ? "left" : undefined);
    drawFrame(right, textures.right, image, imageIsStereo ? (stereoCards ? "right" : "left") : undefined);
  };

  drawBoth();
  let stereoLoaded = false;
  const cover = new Image();
  cover.decoding = "async";
  cover.onload = () => {
    if (!stereoLoaded) drawBoth(cover, cover.naturalWidth >= cover.naturalHeight * 3);
  };
  cover.src = coverUrl(video);

  const stereo = new Image();
  if (video.isVr) {
    stereo.decoding = "async";
    stereo.onload = () => {
      stereoLoaded = true;
      // Two 16:9 views side by side; a mono video comes back as a single 16:9 view.
      drawBoth(stereo, stereo.naturalWidth >= stereo.naturalHeight * 3);
    };
    stereo.src = stereoCardUrl(video);
  }

  return {
    ...textures,
    dispose: () => {
      cover.onload = null;
      stereo.onload = null;
      cover.src = "";
      stereo.src = "";
      textures.left.dispose();
      textures.right.dispose();
    },
  };
}

/** Cover-fit: fill the slot, crop the overflow. With `eye` set, only that half of the image is used. */
function drawCover(
  context: CanvasRenderingContext2D,
  image: HTMLImageElement,
  width: number,
  height: number,
  eye?: "left" | "right",
) {
  const sourceWidth = eye ? image.naturalWidth / 2 : image.naturalWidth;
  const sourceX = eye === "right" ? image.naturalWidth / 2 : 0;
  const scale = Math.max(width / sourceWidth, height / image.naturalHeight);
  const drawWidth = sourceWidth * scale;
  const drawHeight = image.naturalHeight * scale;
  context.save();
  context.beginPath();
  context.rect(0, 0, width, height);
  context.clip();
  context.drawImage(
    image,
    sourceX,
    0,
    sourceWidth,
    image.naturalHeight,
    (width - drawWidth) / 2,
    (height - drawHeight) / 2,
    drawWidth,
    drawHeight,
  );
  context.restore();
}

function badge(context: CanvasRenderingContext2D, text: string, x: number, y: number) {
  context.font = "600 20px system-ui, sans-serif";
  const width = context.measureText(text).width + 16;
  context.fillStyle = "rgba(0, 0, 0, 0.65)";
  context.fillRect(x, y, width, 30);
  context.fillStyle = "#ffffff";
  context.textAlign = "left";
  context.textBaseline = "middle";
  context.fillText(text, x + 8, y + 15);
}

function ellipsize(context: CanvasRenderingContext2D, text: string, maxWidth: number) {
  if (context.measureText(text).width <= maxWidth) return text;
  let end = text.length;
  while (end > 1 && context.measureText(`${text.slice(0, end)}…`).width > maxWidth) end--;
  return `${text.slice(0, end)}…`;
}

function formatDuration(seconds: number) {
  const total = Math.round(seconds);
  const hours = Math.floor(total / 3600);
  const minutes = Math.floor((total % 3600) / 60);
  const secs = String(total % 60).padStart(2, "0");
  return hours > 0 ? `${hours}:${String(minutes).padStart(2, "0")}:${secs}` : `${minutes}:${secs}`;
}

// ---- Scenery -----------------------------------------------------------------------------------------------

function createHeader(text: string, hint: string, theme: Theme) {
  const canvas = document.createElement("canvas");
  canvas.width = 1024;
  canvas.height = 96;
  const context = canvas.getContext("2d")!;
  context.fillStyle = theme.foreground;
  context.font = "600 40px system-ui, sans-serif";
  context.textAlign = "center";
  context.textBaseline = "middle";
  context.fillText(ellipsize(context, text, 1000), 512, 36);
  context.fillStyle = theme.muted;
  context.font = "28px system-ui, sans-serif";
  context.fillText(hint, 512, 78);
  const mesh = new THREE.Mesh(
    new THREE.PlaneGeometry(1.6, 0.15),
    new THREE.MeshBasicMaterial({ map: toTexture(canvas), transparent: true }),
  );
  placeOnWall(mesh, 0, HEADER_Y);
  return mesh;
}

function createFloorRing(colour: string) {
  const ring = new THREE.Mesh(
    new THREE.RingGeometry(1.2, 1.24, 64),
    new THREE.MeshBasicMaterial({ color: colour, side: THREE.DoubleSide }),
  );
  ring.rotation.x = -Math.PI / 2;
  ring.position.y = -1.5;
  return ring;
}

function createPointerRay(colour: string) {
  const geometry = new THREE.BufferGeometry().setFromPoints([new THREE.Vector3(0, 0, 0), new THREE.Vector3(0, 0, -1)]);
  const line = new THREE.Line(geometry, new THREE.LineBasicMaterial({ color: colour }));
  line.scale.z = RADIUS + 1;
  return line;
}

function disposeMesh(mesh: Plane | null) {
  if (!mesh) return;
  mesh.geometry.dispose();
  mesh.material.map?.dispose();
  mesh.material.dispose();
}
