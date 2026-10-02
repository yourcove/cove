/**
 * Immersive (WebXR) playback of a VR video element.
 *
 * Two paths:
 * - **WebGL** (the default, everywhere): the frame is uploaded as a texture every frame and a full-screen
 *   shader reprojects it per eye, into a projection layer where the browser has the Layers module (Quest
 *   Browser) and a plain `XRWebGLLayer` otherwise. Handles every layout, draws a timeline in the room while
 *   seeking or paused, zooms and recentres, the same whether playback started on the video's page or in a
 *   gallery that lent its renderer.
 * - **Media layer** (only on request, with {@link InSessionVideoOptions.preferMediaLayer}): the video
 *   element is bound to an equirect layer that the compositor samples directly. No per-frame texture
 *   upload, so 8K plays as well as in a native player, but equirectangular only and with no zoom,
 *   recentre or timeline.
 *
 * Controls are controller-only: trigger or A/X toggles play, stick left/right seeks, stick up/down
 * zooms, and B/Y or clicking the stick leaves the video (ending the session, or handing back to
 * whoever opened it).
 *
 * Exposed to extensions as `@cove/runtime/webxr`, so a gallery extension hands off to exactly the same
 * playback as the core player. A gallery can also hand a live session *to* the video page
 * ({@link offerSessionHandoff}), so the browser shows the video's page while the headset plays it.
 */

/**
 * How a video's frame maps around the viewer. "flat" is a video on a virtual screen: an ordinary one,
 * or with a stereo mode a 3D film, each eye seeing its own half of the frame.
 */
export type VrProjection = "equirectangular" | "fisheye" | "mkx200" | "flat";
export type VrStereoMode = "mono" | "sideBySide" | "topBottom";

/** Mirrors `VrDescriptorDto` on the server. */
export interface VrDescriptor {
  projection: VrProjection;
  /** Horizontal coverage in degrees: 180 or 360 for equirectangular, the lens angle for fisheye. */
  fieldOfView: number;
  stereoMode: VrStereoMode;
  /** True when the layout was detected from the file rather than set explicitly. */
  inferred?: boolean;
}

/** An ordinary, non-VR video: shown on a virtual screen in front of the viewer. */
export const FLAT_VR: VrDescriptor = { projection: "flat", fieldOfView: 0, stereoMode: "mono" };

/** Short human label for a layout, e.g. "180° SBS", "Fisheye 190° SBS", "MKX200 TB", "3D SBS", "2D". */
export function formatVrLayout(vr: VrDescriptor | null | undefined): string {
  if (!vr) return "VR";
  if (vr.projection === "flat")
    return vr.stereoMode === "sideBySide" ? "3D SBS" : vr.stereoMode === "topBottom" ? "3D TB" : "2D";
  const projection =
    vr.projection === "equirectangular"
      ? `${vr.fieldOfView}°`
      : vr.projection === "mkx200"
        ? `MKX${vr.fieldOfView}`
        : `Fisheye ${vr.fieldOfView}°`;
  const stereo = vr.stereoMode === "sideBySide" ? "SBS" : vr.stereoMode === "topBottom" ? "TB" : "Mono";
  return `${projection} ${stereo}`;
}

/**
 * How the headset reads and moves the playhead. The 2D player supplies one when the video is streamed
 * as a transcode, where the element's own time and duration only cover the segment being streamed.
 */
export interface PlaybackTransport {
  /** Position in the video, in seconds. */
  currentTime(): number;
  /** Length of the video, in seconds. */
  duration(): number;
  /** Moves to a position in the video, in seconds. */
  seek(seconds: number): void;
  /** Whether the last seek is still being carried out. Defaults to the element's `seeking`. */
  isSeeking?(): boolean;
}

export interface InSessionVideoOptions {
  /**
   * Called when the viewer presses B/Y (or the menu button) to leave the video. The playback has already
   * been stopped and the session's previous layers restored by then.
   */
  onExit?: () => void;
  /** Playhead access that understands the stream; defaults to the element's own time. */
  transport?: PlaybackTransport;
  /** The element to draw, re-read every frame, for players that swap their element mid-playback. */
  element?: () => HTMLVideoElement | null;
  /**
   * Draw inside the session owner's own frame loop, context and layer instead of taking the session
   * over. A gallery hands this along with its session so its renderer stays in charge and its loop
   * keeps running; see {@link SessionPresenter}.
   */
  presenter?: SessionPresenter;
  /** Seconds skipped per thumbstick flick. Defaults to 10. */
  seekStepSeconds?: number;
  /**
   * Hand an equirectangular video to the compositor as a media layer where the browser supports one,
   * for the smoothest 8K, at the cost of zoom, recentre and the timeline. Ignored with a presenter.
   */
  preferMediaLayer?: boolean;
  /** @deprecated WebGL is the default now; kept so extensions that pass it keep compiling. */
  preferWebGl?: boolean;
  /** Shown on the in-headset timeline. */
  title?: string;
}

export interface ImmersiveVideoOptions extends Omit<InSessionVideoOptions, "onExit"> {
  /** Called once the session has ended, whichever side ended it. */
  onEnd?: () => void;
  /**
   * Called when the viewer presses back. Return true to keep the session alive and take it over (say,
   * to show a list in it); otherwise the session ends.
   */
  onBack?: (session: object) => boolean;
}

export interface InSessionVideoPlayback {
  /** Which renderer the playback ended up with. */
  readonly mode: "media-layer" | "webgl";
  /** Stops drawing the video and restores the layers the session had before. Does not pause the element. */
  stop(): void;
}

export interface ImmersiveVideoSession {
  /** Which renderer the session ended up with. */
  readonly mode: "media-layer" | "webgl";
  /** Ends the session. Resolves once the runtime has shut it down. */
  end(): Promise<void>;
}

export type ImmersiveSupport =
  | { supported: true }
  | { supported: false; reason: "no-webxr" | "insecure-context" | "no-immersive-vr" };

// ---- Minimal WebXR typings (lib.dom does not ship them) -------------------------------------------------

interface XRSystemLike {
  isSessionSupported(mode: string): Promise<boolean>;
  requestSession(
    mode: string,
    init?: { requiredFeatures?: string[]; optionalFeatures?: string[] },
  ): Promise<XRSessionLike>;
}
export interface XRGamepadLike {
  buttons: ReadonlyArray<{ pressed: boolean }>;
  axes: ReadonlyArray<number>;
}
export interface XRInputSourceLike {
  handedness: string;
  profiles?: ReadonlyArray<string>;
  gamepad?: XRGamepadLike | null;
}
interface XRViewLike {
  eye: "left" | "right" | "none";
  projectionMatrix: Float32Array;
  transform: { inverse: { matrix: Float32Array } };
}
interface XRViewerPoseLike {
  views: ReadonlyArray<XRViewLike>;
}
interface XRFrameLike {
  getViewerPose(space: unknown): XRViewerPoseLike | null;
}
interface XRWebGlLayerLike {
  framebuffer: WebGLFramebuffer | null;
  getViewport(view: XRViewLike): { x: number; y: number; width: number; height: number } | null;
}
export interface XRSessionLike extends EventTarget {
  enabledFeatures?: ReadonlyArray<string>;
  inputSources: Iterable<XRInputSourceLike>;
  renderState: { baseLayer?: XRWebGlLayerLike | null };
  requestReferenceSpace(type: string): Promise<unknown>;
  updateRenderState(state: Record<string, unknown>): void;
  requestAnimationFrame(callback: (time: number, frame: XRFrameLike) => void): number;
  end(): Promise<void>;
}
type XRMediaBindingCtor = new (session: XRSessionLike) => {
  createEquirectLayer(video: HTMLVideoElement, init: Record<string, unknown>): unknown;
};
type XRWebGlLayerCtor = new (session: XRSessionLike, gl: WebGL2RenderingContext) => XRWebGlLayerLike;
/** A layer from the WebXR Layers module: drawn through a binding's per-view sub-images, not a framebuffer. */
interface XRViewSubImageLike {
  colorTexture: WebGLTexture;
  viewport: { x: number; y: number; width: number; height: number };
}
interface XRWebGlBindingLike {
  getViewSubImage(layer: object, view: XRViewLike): XRViewSubImageLike;
  createProjectionLayer?(init: Record<string, unknown>): object;
}
type XRWebGlBindingCtor = new (session: XRSessionLike, gl: WebGL2RenderingContext) => XRWebGlBindingLike;

function isWebGlLayer(layer: object): layer is XRWebGlLayerLike {
  return typeof (layer as XRWebGlLayerLike).getViewport === "function";
}

function xrSystem(): XRSystemLike | undefined {
  return (navigator as Navigator & { xr?: XRSystemLike }).xr;
}

function xrGlobal<T>(name: string): T | undefined {
  return (globalThis as Record<string, unknown>)[name] as T | undefined;
}

// ---- Support detection ---------------------------------------------------------------------------------

/** Whether this browser can start an immersive VR session, and why not when it cannot. */
export async function getImmersiveVrSupport(): Promise<ImmersiveSupport> {
  const xr = xrSystem();
  if (!xr) {
    // Browsers hide navigator.xr entirely outside a secure context, which is the usual reason on a LAN.
    return { supported: false, reason: window.isSecureContext ? "no-webxr" : "insecure-context" };
  }
  try {
    return (await xr.isSessionSupported("immersive-vr"))
      ? { supported: true }
      : { supported: false, reason: "no-immersive-vr" };
  } catch {
    return { supported: false, reason: "no-immersive-vr" };
  }
}

/**
 * The support answer when it needs no asking: a browser with no `navigator.xr` cannot start a session.
 * Lets the buttons render their final state at once instead of after a query settles.
 */
export function immersiveVrSupportIfKnown(): ImmersiveSupport | undefined {
  if (xrSystem()) return undefined;
  return { supported: false, reason: window.isSecureContext ? "no-webxr" : "insecure-context" };
}

export async function isImmersiveVrSupported(): Promise<boolean> {
  return (await getImmersiveVrSupport()).supported;
}

/** Equirect media layers can only show equirectangular video; everything else needs the WebGL path. */
export function canUseMediaLayer(vr: VrDescriptor): boolean {
  return vr.projection === "equirectangular";
}

/** Horizontal half-angle of the virtual screen a flat video is shown on, at zoom 1. */
export const FLAT_SCREEN_HALF_ANGLE = (28 * Math.PI) / 180;

/**
 * Width / height of the picture one eye of a flat video sees. 3D films come packed at full size (each
 * eye keeps its own pixels, doubling the frame) or at half size (each eye squeezed into half the
 * frame, restored on playback). An eye narrower than 1.2:1 side by side, or wider than 2.5:1 over
 * and under, is taken for a squeezed one: no film is shot that tall or that wide. Exported for tests.
 */
export function flatEyeAspect(width: number, height: number, stereoMode: VrStereoMode): number {
  const frame = width > 0 && height > 0 ? width / height : 16 / 9;
  if (stereoMode === "sideBySide") return frame / 2 < 1.2 ? frame : frame / 2;
  if (stereoMode === "topBottom") return frame * 2 > 2.5 ? frame : frame * 2;
  return frame;
}

/** The `XRMediaBinding.createEquirectLayer` init for a layout. Exported for extensions and tests. */
export function equirectLayerInit(vr: VrDescriptor, space: unknown): Record<string, unknown> {
  const full = vr.fieldOfView >= 360;
  return {
    space,
    layout:
      vr.stereoMode === "sideBySide"
        ? "stereo-left-right"
        : vr.stereoMode === "topBottom"
          ? "stereo-top-bottom"
          : "mono",
    centralHorizontalAngle: full ? Math.PI * 2 : Math.PI,
    upperVerticalAngle: Math.PI / 2,
    lowerVerticalAngle: -Math.PI / 2,
    radius: 0, // infinite sphere: no parallax against the room
  };
}

// ---- Session -------------------------------------------------------------------------------------------

/**
 * Options to request an immersive session with so video can later use a media layer. Extensions that open
 * their own session (a 3D gallery, say) should pass these, then hand off with {@link playVideoInSession}.
 */
export function immersiveSessionInit(): { optionalFeatures: string[] } {
  return { optionalFeatures: ["layers", "local-floor"] };
}

const SESSION_REQUEST_TIMEOUT_MS = 15_000;

// Only one immersive session can exist per page. Requesting a second one while the runtime is still
// tearing the previous one down is refused (or hangs) on desktop OpenXR, so requests queue behind the
// previous session's end.
let activeSession: XRSessionLike | null = null;
let activeSessionEnded: Promise<void> | null = null;

/** The immersive session this page currently has open, if any. */
export function currentImmersiveSession(): object | null {
  return activeSession;
}

/**
 * Requests an immersive VR session, ending any session this page still has open first and failing with
 * a clear message instead of hanging when the runtime never answers. Call from a user gesture.
 */
export async function requestImmersiveSession(): Promise<object> {
  const xr = xrSystem();
  if (!xr) throw new Error("WebXR is not available. Immersive playback needs a secure context (HTTPS or localhost).");

  if (activeSession) {
    const previous = activeSession;
    const previousEnded = activeSessionEnded;
    await previous.end().catch(() => {});
    await previousEnded;
  }

  const session = await withTimeout(
    xr.requestSession("immersive-vr", immersiveSessionInit()),
    SESSION_REQUEST_TIMEOUT_MS,
    "The browser did not start a VR session. Check that the headset is awake and SteamVR (or the Oculus runtime) is running, then try again. If it keeps failing, reload the page.",
  );
  activeSession = session;
  activeSessionEnded = new Promise<void>((resolve) => {
    session.addEventListener("end", () => {
      if (activeSession === session) {
        activeSession = null;
        activeSessionEnded = null;
      }
      // Let the runtime finish its own teardown before the next request goes out.
      setTimeout(resolve, 250);
    });
  });
  return session;
}

function withTimeout<T>(promise: Promise<T>, ms: number, message: string): Promise<T> {
  return new Promise<T>((resolve, reject) => {
    const timer = setTimeout(() => reject(new Error(message)), ms);
    promise.then(
      (value) => {
        clearTimeout(timer);
        resolve(value);
      },
      (reason: unknown) => {
        clearTimeout(timer);
        reject(reason instanceof Error ? reason : new Error(String(reason)));
      },
    );
  });
}

/**
 * Starts immersive playback of `video` in a new session. Call from a user gesture (a click), because both
 * `requestSession` and `video.play()` need one. The element keeps its state: when the session ends the 2D
 * player carries on from the same position.
 */
export async function startImmersiveVideo(
  video: HTMLVideoElement,
  vr: VrDescriptor,
  options: ImmersiveVideoOptions = {},
): Promise<ImmersiveVideoSession> {
  const session = (await requestImmersiveSession()) as XRSessionLike;
  let playback: InSessionVideoPlayback | null = null;
  let ended = false;
  session.addEventListener("end", () => {
    if (ended) return;
    ended = true;
    playback?.stop();
    options.onEnd?.();
  });

  try {
    playback = await playVideoInSession(session, video, vr, {
      ...options,
      onExit: () => {
        if (options.onBack?.(session)) {
          // Someone else owns the session from here on; report the playback as over.
          if (!ended) {
            ended = true;
            options.onEnd?.();
          }
          return;
        }
        void session.end().catch(() => {});
      },
    });
    return {
      mode: playback.mode,
      end: async () => {
        if (!ended) await session.end().catch(() => {});
      },
    };
  } catch (error) {
    await session.end().catch(() => {});
    throw error;
  }
}

/**
 * Plays `video` inside an immersive session someone else opened, replacing what the session shows until
 * {@link InSessionVideoPlayback.stop} or the viewer's exit button. The caller should pause its own rendering
 * meanwhile (for three.js, `renderer.setAnimationLoop(null)`), since its output is not composited.
 *
 * `session` is typed loosely so callers can pass a DOM or three.js `XRSession` without casting.
 */
export async function playVideoInSession(
  session: object,
  video: HTMLVideoElement,
  vr: VrDescriptor,
  options: InSessionVideoOptions = {},
): Promise<InSessionVideoPlayback> {
  const xrSession = session as XRSessionLike;
  const presenter = options.presenter;
  // "local" keeps the sphere centred on where the viewer's head was at the start, which is what video wants.
  const space = presenter ? presenter.referenceSpace : await xrSession.requestReferenceSpace("local");
  const previousRenderState = presenter ? null : snapshotRenderState(xrSession);
  const useLayer =
    !presenter &&
    options.preferMediaLayer === true &&
    !options.preferWebGl &&
    canUseMediaLayer(vr) &&
    xrGlobal<XRMediaBindingCtor>("XRMediaBinding") != null &&
    (xrSession.enabledFeatures?.includes("layers") ?? false);

  const element = () => options.element?.() ?? video;
  const transport: PlaybackTransport = options.transport ?? {
    currentTime: () => element().currentTime,
    duration: () => element().duration,
    seek: (seconds) => {
      element().currentTime = seconds;
    },
    isSeeking: () => element().seeking,
  };
  const isSeeking = () => transport.isSeeking?.() ?? element().seeking;

  let render: ((frame: XRFrameLike) => void) | null = null;
  let dispose: () => void = () => {};
  let hud: PlaybackHud | null = null;
  let webGlRenderer: ReturnType<typeof createWebGlRenderer> | null = null;
  const zoom = new ZoomSetting();
  if (useLayer) {
    const MediaBinding = xrGlobal<XRMediaBindingCtor>("XRMediaBinding")!;
    const layer = new MediaBinding(xrSession).createEquirectLayer(video, equirectLayerInit(vr, space));
    xrSession.updateRenderState({ layers: [layer] });
  } else {
    hud = new PlaybackHud(element, transport, zoom, options.title);
    webGlRenderer = createWebGlRenderer(
      xrSession,
      element,
      vr,
      space,
      zoom,
      hud,
      presenter ? { gl: presenter.gl, layer: presenter.layer as XRWebGlLayerLike } : undefined,
    );
    render = webGlRenderer.render;
    dispose = webGlRenderer.dispose;
  }
  // The page's copy of the video is not looked at while the headset shows it; not painting it saves
  // the compositor some work (decoding happens once either way).
  const hiddenElement = video;
  const previousVisibility = hiddenElement.style.visibility;
  hiddenElement.style.visibility = "hidden";

  let stopped = false;
  // Releasing the trigger after a recentre hold raises "select" too; that one must not toggle playback.
  let ignoreSelectUntil = 0;
  const onSelect = () => {
    if (performance.now() < ignoreSelectUntil) return;
    void togglePlayback(element());
    hud?.touch();
  };
  const stop = () => {
    if (stopped) return;
    stopped = true;
    xrSession.removeEventListener("select", onSelect);
    hiddenElement.style.visibility = previousVisibility;
    presenter?.setFrameRenderer(null);
    dispose();
    if (previousRenderState) {
      try {
        xrSession.updateRenderState(previousRenderState);
      } catch {
        // The session is already ending; there is nothing left to restore.
      }
    }
  };
  const controls = createControllerMapping(xrSession, transport, options.seekStepSeconds ?? 10, isSeeking, {
    exit: () => {
      stop();
      options.onExit?.();
    },
    togglePlay: onSelect,
    recenter: () => {
      ignoreSelectUntil = performance.now() + 1500;
      webGlRenderer?.recenter();
      hud?.touch();
    },
    zoom: (direction) => {
      if (useLayer) return; // no zoom on the media-layer path
      zoom.step(direction);
      hud?.touch();
    },
    seeked: (signedStep, target) => {
      hud?.setSeeking(signedStep, target);
      hud?.touch();
    },
  });
  xrSession.addEventListener("select", onSelect);

  if (presenter) {
    // The owner's loop calls this every frame; nothing of ours runs outside it.
    presenter.setFrameRenderer((frame) => {
      if (stopped) return;
      controls.poll();
      render?.(frame as XRFrameLike);
    });
  } else {
    const loop = (_time: number, frame: XRFrameLike) => {
      if (stopped) return;
      controls.poll();
      render?.(frame);
      if (!stopped) xrSession.requestAnimationFrame(loop);
    };
    xrSession.requestAnimationFrame(loop);
  }
  hud?.touch();
  void video.play().catch(() => {});

  return { mode: useLayer ? "media-layer" : "webgl", stop };
}

function snapshotRenderState(session: XRSessionLike): Record<string, unknown> {
  const state = session.renderState as { baseLayer?: unknown; layers?: unknown[] };
  // A session uses either a base layer or a layers array, never both.
  return state.layers && state.layers.length > 0
    ? { layers: [...state.layers] }
    : { baseLayer: state.baseLayer ?? null };
}

function togglePlayback(video: HTMLVideoElement) {
  if (video.paused) return video.play().catch(() => {});
  video.pause();
}

// ---- Handing a session between pages ---------------------------------------------------------------------

/**
 * What a session's owner lends to whoever plays a video in it: its WebGL 2 context (already XR
 * compatible), the layer that is the session's base layer, the reference space the owner renders
 * in, and a slot in its frame loop. The owner calls the frame renderer from inside its own XR
 * animation frame while one is set, and resets its own GL state afterwards (three.js:
 * `renderer.resetState()`). The session's render state is never touched, so the owner's loop keeps
 * running and takes over again the moment the renderer is cleared.
 */
export interface SessionPresenter {
  gl: WebGL2RenderingContext;
  /** The layer the owner draws into: an `XRWebGLLayer`, or an `XRProjectionLayer` where the Layers module is in use. */
  layer: object;
  /** The `XRReferenceSpace` the owner renders in; "local" suits video. */
  referenceSpace: object;
  setFrameRenderer(render: ((frame: object) => void) | null): void;
}

/** Why a claimed session came back: the viewer pressed back, or the page holding it went away. */
export type SessionReturnReason = "exit" | "abandoned";

export interface SessionHandoffOptions {
  /**
   * Runs when the page that took the session gives it back. On "exit" the viewer asked to leave the
   * video, so the owner should also bring the browser back; on "abandoned" the browser has moved on
   * and the owner should just resume showing itself in the headset.
   */
  onReturn: (reason: SessionReturnReason) => void;
  /** Runs when nothing claims the session in time (the browser landed on a page without a VR player). */
  onTimeout?: () => void;
  /** How long a claim may take. Defaults to 10 s. */
  timeoutMs?: number;
  /** Lends the owner's renderer to the claimant; strongly recommended for owners with a frame loop. */
  presenter?: SessionPresenter;
}

export interface ClaimedSession {
  session: object;
  /** The owner's renderer to draw with, when it lent one. Pass it to {@link playVideoInSession}. */
  presenter?: SessionPresenter;
  /** Gives the session back to its owner, which resumes whatever it was showing. */
  returnToOwner(reason?: SessionReturnReason): void;
}

let pendingHandoff: { session: object; options: SessionHandoffOptions; timer: number } | null = null;

/**
 * Offers a live immersive session to whatever page the app navigates to next, so a gallery can open a
 * video's page in the browser while the headset stays in VR. The owner stops rendering, calls this, then
 * navigates; the video page claims the session with {@link claimHandedOffSession} and plays into it. When
 * the viewer leaves the video, `onReturn` runs and the owner resumes (the session's previous layers have
 * already been restored). If nothing claims it in time, `onTimeout` runs instead.
 */
export function offerSessionHandoff(session: object, options: SessionHandoffOptions): void {
  cancelSessionHandoff();
  const timer = window.setTimeout(() => {
    if (pendingHandoff?.session !== session) return;
    pendingHandoff = null;
    options.onTimeout?.();
  }, options.timeoutMs ?? 10_000);
  pendingHandoff = { session, options, timer };
}

/** Withdraws an unclaimed offer. */
export function cancelSessionHandoff(): void {
  if (!pendingHandoff) return;
  window.clearTimeout(pendingHandoff.timer);
  pendingHandoff = null;
}

/** Whether a session is waiting to be claimed. */
export function hasHandedOffSession(): boolean {
  return pendingHandoff != null;
}

/** Takes the offered session, if any. The claimant must eventually call `returnToOwner`. */
export function claimHandedOffSession(): ClaimedSession | null {
  if (!pendingHandoff) return null;
  const { session, options, timer } = pendingHandoff;
  window.clearTimeout(timer);
  pendingHandoff = null;
  let returned = false;
  return {
    session,
    presenter: options.presenter,
    returnToOwner: (reason = "exit") => {
      if (returned) return;
      returned = true;
      options.onReturn(reason);
    },
  };
}

// ---- Controls -------------------------------------------------------------------------------------------

// xr-standard gamepad mapping: buttons 0 trigger, 1 squeeze, 2 touchpad, 3 thumbstick, 4 A/X, 5 B/Y;
// axes 0/1 touchpad, 2/3 thumbstick. A/X plays and pauses like the trigger; B/Y goes back, and so
// does clicking the thumbstick, because not every runtime exposes B (Chrome on OpenXR binds the
// Index's B elsewhere). The grip is deliberately not a button here: on the Valve Index it reads as
// pressed whenever the controller is held firmly.
const BACK_BUTTONS = [5, 3];
const PLAY_BUTTON = 4;
const STICK_THRESHOLD = 0.6;
const SEEK_REPEAT_MS = 400;
// Holding the stick sideways speeds the seek up in steps, like fast-forward on a remote: the base step
// per repeat for the first while, then 3× it, then 12× it.
const SEEK_TIERS: ReadonlyArray<{ afterMs: number; multiplier: number }> = [
  { afterMs: 0, multiplier: 1 },
  { afterMs: 2000, multiplier: 3 },
  { afterMs: 5000, multiplier: 12 },
];

/** The seek step after the stick has been held for `heldMs`, given the base step. Exported for tests. */
export function seekStepAfter(heldMs: number, baseStep: number): number {
  let multiplier = 1;
  for (const tier of SEEK_TIERS) if (heldMs >= tier.afterMs) multiplier = tier.multiplier;
  return baseStep * multiplier;
}
const ZOOM_REPEAT_MS = 250;

/**
 * The thumbstick as [x, y]. The touchpad (axes 0/1) is only used on controllers that have nothing else:
 * on the Valve Index a resting thumb registers on it.
 */
export function steeringAxes(axes: ReadonlyArray<number>): [number, number] {
  if (axes.length >= 4) return [axes[2] ?? 0, axes[3] ?? 0];
  return [axes[0] ?? 0, axes[1] ?? 0];
}

const describedSources = new WeakSet<object>();
// Whether any controller seen so far exposes a B/Y button (index 5). Chromium's Valve Index binding does
// not, so hints then name only the stick click.
let sawBackButton = false;

/** How to describe "back" in hints, given the controllers seen so far. */
export function backButtonHint(): string {
  return sawBackButton ? "B or stick click" : "Stick click";
}

/** Notes what each controller offers, once, so hints can name the right buttons. */
function describeInputSource(source: XRInputSourceLike) {
  if (describedSources.has(source)) return;
  describedSources.add(source);
  if ((source.gamepad?.buttons.length ?? 0) > 5) sawBackButton = true;
}

export interface ControlHandlers {
  exit: () => void;
  togglePlay: () => void;
  zoom: (direction: 1 | -1) => void;
  /** The held stick moved the playhead to `target` in steps of `step`; both null once released. */
  seeked: (step: number | null, target: number | null) => void;
  /** Aim the video at the current gaze again. */
  recenter: () => void;
}

const TRIGGER_BUTTON = 0;
// Holding the trigger this long recentres instead of toggling playback.
const RECENTER_HOLD_MS = 700;

// While the stick is held the playhead moves every repeat, but the element is only asked to seek this
// often at most, and only once its previous seek finished: seeking an 8K file every repeat would never
// let a frame decode.
const SEEK_COMMIT_MS = 600;

export function createControllerMapping(
  session: XRSessionLike,
  transport: PlaybackTransport,
  step: number,
  isSeeking: () => boolean,
  handlers: ControlHandlers,
) {
  const exitHeld = new Set<string>();
  const playHeld = new Set<string>();
  const triggerDownSince = new Map<string, number>();
  const triggerRecentred = new Set<string>();
  const lastSeekAt = new Map<string, number>();
  const seekHeldSince = new Map<string, number>();
  // The position the held stick has reached, ahead of what the element has been asked for.
  let target: number | null = null;
  let lastCommitAt = 0;
  const commit = (now: number) => {
    if (target == null) return;
    transport.seek(target);
    lastCommitAt = now;
  };
  const lastZoomAt = new Map<string, number>();
  return {
    poll() {
      for (const source of session.inputSources) {
        const gamepad = source.gamepad;
        if (!gamepad) continue;
        const key = source.handedness;
        describeInputSource(source);

        const exitPressed = BACK_BUTTONS.some((index) => gamepad.buttons[index]?.pressed ?? false);
        if (exitPressed && !exitHeld.has(key)) {
          handlers.exit();
          return;
        }
        if (exitPressed) exitHeld.add(key);
        else exitHeld.delete(key);

        const playPressed = gamepad.buttons[PLAY_BUTTON]?.pressed ?? false;
        if (playPressed && !playHeld.has(key)) handlers.togglePlay();
        if (playPressed) playHeld.add(key);
        else playHeld.delete(key);

        const now = performance.now();

        // A held trigger recentres; a short pull is the ordinary "select" the runtime raises on release.
        const triggerPressed = gamepad.buttons[TRIGGER_BUTTON]?.pressed ?? false;
        if (triggerPressed) {
          const since = triggerDownSince.get(key) ?? now;
          triggerDownSince.set(key, since);
          if (now - since >= RECENTER_HOLD_MS && !triggerRecentred.has(key)) {
            triggerRecentred.add(key);
            handlers.recenter();
          }
        } else {
          triggerDownSince.delete(key);
          triggerRecentred.delete(key);
        }
        const [x, y] = steeringAxes(gamepad.axes);
        if (Math.abs(x) < STICK_THRESHOLD) {
          if (seekHeldSince.has(key)) {
            // Released: land on the position reached.
            commit(now);
            target = null;
            handlers.seeked(null, null);
          }
          lastSeekAt.delete(key);
          seekHeldSince.delete(key);
        } else {
          const heldSince = seekHeldSince.get(key) ?? now;
          seekHeldSince.set(key, heldSince);
          const last = lastSeekAt.get(key);
          if (last == null || now - last >= SEEK_REPEAT_MS) {
            lastSeekAt.set(key, now);
            const duration = transport.duration();
            const limit = Number.isFinite(duration) && duration > 0 ? duration : Infinity;
            const signedStep = Math.sign(x) * seekStepAfter(now - heldSince, step);
            target = Math.min(Math.max(0, (target ?? transport.currentTime()) + signedStep), limit);
            if (now - lastCommitAt >= SEEK_COMMIT_MS && !isSeeking()) commit(now);
            handlers.seeked(signedStep, target);
          }
        }

        if (Math.abs(y) < STICK_THRESHOLD) {
          lastZoomAt.delete(key);
        } else {
          const last = lastZoomAt.get(key);
          if (last == null || now - last >= ZOOM_REPEAT_MS) {
            lastZoomAt.set(key, now);
            // Stick up (negative y) brings the scene closer.
            handlers.zoom(y < 0 ? 1 : -1);
          }
        }
      }
    },
  };
}

// ---- Zoom -----------------------------------------------------------------------------------------------

const ZOOM_STORAGE_KEY = "cove.vr.zoom";
const ZOOM_MIN = 0.6;
const ZOOM_MAX = 2.0;
const ZOOM_STEP = 0.1;

/** How much closer the scene appears. 1 is the natural size; persisted per browser. */
class ZoomSetting {
  value = 1;

  constructor() {
    try {
      const stored = Number(localStorage.getItem(ZOOM_STORAGE_KEY));
      if (Number.isFinite(stored) && stored > 0) this.value = clampZoom(stored);
    } catch {
      // Storage may be unavailable; the default is fine.
    }
  }

  step(direction: 1 | -1) {
    this.value = clampZoom(Math.round((this.value + direction * ZOOM_STEP) * 100) / 100);
    try {
      localStorage.setItem(ZOOM_STORAGE_KEY, String(this.value));
    } catch {
      // Ignore: the value still applies to this session.
    }
  }
}

function clampZoom(value: number) {
  return Math.min(ZOOM_MAX, Math.max(ZOOM_MIN, value));
}

// ---- In-headset timeline ---------------------------------------------------------------------------------

const HUD_WIDTH = 1024;
const HUD_HEIGHT = 150;
const HUD_VISIBLE_MS = 2500;
// Where the strip sits once shown: this far ahead of the viewer and this far below eye level, in metres.
const HUD_DISTANCE = 1.6;
const HUD_DROP = 0.5;
const HUD_WORLD_WIDTH = 1.2;

/**
 * A strip with the title, a progress bar, the time, the zoom and the controls. It appears in front of
 * wherever the viewer is looking and then stays put in the room (it does not follow the head), for a
 * moment after any control and for as long as the video is paused.
 */
class PlaybackHud {
  readonly canvas = document.createElement("canvas");
  private readonly context: CanvasRenderingContext2D;
  private visibleUntil = 0;
  private drawnAt = -1;
  private drawnKey = "";
  private seeking: number | null = null;
  private seekTarget: number | null = null;

  constructor(
    private readonly element: () => HTMLVideoElement,
    private readonly transport: PlaybackTransport,
    private readonly zoom: ZoomSetting,
    private readonly title?: string,
  ) {
    this.canvas.width = HUD_WIDTH;
    this.canvas.height = HUD_HEIGHT;
    this.context = this.canvas.getContext("2d")!;
  }

  touch() {
    this.visibleUntil = performance.now() + HUD_VISIBLE_MS;
  }

  /** The signed seek step and the position reached while the stick is held; both null once released. */
  setSeeking(step: number | null, target: number | null) {
    this.seeking = step;
    this.seekTarget = target;
  }

  get visible(): boolean {
    return this.element().paused || performance.now() < this.visibleUntil;
  }

  /** Redraws when something changed; returns true when the texture needs re-uploading. */
  update(): boolean {
    const now = performance.now();
    // Progress moves slowly; a few redraws per second is plenty.
    if (now - this.drawnAt < 200) return false;
    const key = `${Math.floor(this.seekTarget ?? this.transport.currentTime())}|${this.transport.duration()}|${this.element().paused}|${this.zoom.value}|${backButtonHint()}|${this.seeking}`;
    if (key === this.drawnKey) return false;
    this.drawnAt = now;
    this.drawnKey = key;
    this.draw();
    return true;
  }

  private draw() {
    const c = this.context;
    c.clearRect(0, 0, HUD_WIDTH, HUD_HEIGHT);
    roundedRect(c, 0, 0, HUD_WIDTH, HUD_HEIGHT, 20);
    c.fillStyle = "rgba(12, 14, 20, 0.82)";
    c.fill();

    c.fillStyle = "#e8ebf2";
    c.font = "600 30px system-ui, sans-serif";
    c.textBaseline = "middle";
    c.textAlign = "left";
    const label = this.element().paused ? "❚❚" : "▶";
    c.fillText(label, 28, 34);
    if (this.title) c.fillText(ellipsize(c, this.title, HUD_WIDTH - 260), 84, 34);

    c.font = "500 26px system-ui, sans-serif";
    c.textAlign = "right";
    c.fillStyle = "#c9cfdb";
    c.fillText(
      this.seeking != null
        ? `${this.seeking < 0 ? "◀◀" : "▶▶"} ${formatStep(Math.abs(this.seeking))}`
        : `Zoom ${this.zoom.value.toFixed(1)}×`,
      HUD_WIDTH - 28,
      34,
    );

    const duration = Number.isFinite(this.transport.duration()) ? this.transport.duration() : 0;
    // While the stick is held the strip follows the position being seeked to, ahead of the picture.
    const currentTime = this.seekTarget ?? this.transport.currentTime();
    const progress = duration > 0 ? Math.min(1, currentTime / duration) : 0;
    const barX = 28;
    const barY = 70;
    const barWidth = HUD_WIDTH - 56 - 250;
    roundedRect(c, barX, barY, barWidth, 12, 6);
    c.fillStyle = "rgba(255, 255, 255, 0.22)";
    c.fill();
    if (progress > 0) {
      roundedRect(c, barX, barY, Math.max(12, barWidth * progress), 12, 6);
      c.fillStyle = "#8ab4ff";
      c.fill();
    }
    c.fillStyle = "#e8ebf2";
    c.textAlign = "right";
    c.fillText(`${formatTime(currentTime)} / ${formatTime(duration)}`, HUD_WIDTH - 28, 76);

    c.font = "500 22px system-ui, sans-serif";
    c.fillStyle = "#7d8597";
    c.textAlign = "left";
    c.fillText(
      `Trigger or A: play / pause   Hold trigger: recentre   ◀ ▶ seek (hold to speed up)   ▲ ▼ zoom   ${backButtonHint()}: back`,
      28,
      122,
    );
  }
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

function ellipsize(c: CanvasRenderingContext2D, text: string, maxWidth: number) {
  if (c.measureText(text).width <= maxWidth) return text;
  let end = text.length;
  while (end > 1 && c.measureText(`${text.slice(0, end)}…`).width > maxWidth) end--;
  return `${text.slice(0, end)}…`;
}

/** "10 s", "30 s", "2 min". */
function formatStep(seconds: number): string {
  return seconds >= 60 && seconds % 60 === 0 ? `${seconds / 60} min` : `${seconds} s`;
}

/** m:ss or h:mm:ss. Exported for tests. */
export function formatTime(seconds: number): string {
  if (!Number.isFinite(seconds) || seconds < 0) return "0:00";
  const total = Math.floor(seconds);
  const hours = Math.floor(total / 3600);
  const minutes = Math.floor((total % 3600) / 60);
  const secs = String(total % 60).padStart(2, "0");
  return hours > 0 ? `${hours}:${String(minutes).padStart(2, "0")}:${secs}` : `${minutes}:${secs}`;
}

// ---- WebGL path ---------------------------------------------------------------------------------------

const VERTEX_SHADER = `#version 300 es
in vec2 a_position;
out vec2 v_ndc;
void main() {
  v_ndc = a_position;
  gl_Position = vec4(a_position, 0.0, 1.0);
}`;

// Casts a ray per pixel through the inverse view-projection (rotation only, so the video stays at
// infinity) and looks the direction up in the source frame for this eye. Zoom scales the angles, which
// makes the picture larger without moving the viewer off the sphere's centre.
const FRAGMENT_SHADER = `#version 300 es
precision highp float;
in vec2 v_ndc;
out vec4 outColor;
uniform sampler2D u_video;
uniform mat4 u_inverseViewProjection;
uniform int u_projection;   // 0 equirect, 1 fisheye (equidistant; MKX200 approximated), 2 flat screen
uniform float u_screenHalfWidth; // flat: half the screen width at unit distance, zoom applied
uniform float u_aspect;     // flat: the frame's width / height
uniform float u_fov;        // radians
uniform int u_stereo;       // 0 mono, 1 side by side, 2 top/bottom
uniform float u_eye;        // 0 left, 1 right
uniform float u_zoom;       // 1 = natural size
uniform mat3 u_orientation; // world to video: the video's centre sits where the viewer looked at the start
const float PI = 3.141592653589793;

void main() {
  vec4 far = u_inverseViewProjection * vec4(v_ndc, 1.0, 1.0);
  vec3 dir = normalize(u_orientation * (far.xyz / far.w));
  vec2 uv;
  if (u_projection == 2) {
    // A screen on the plane z = -1 in the video's frame, u_screenHalfWidth wide each side of centre.
    if (dir.z >= -0.001) { outColor = vec4(0.0, 0.0, 0.0, 1.0); return; }
    float t = -1.0 / dir.z;
    float x = dir.x * t;
    float y = dir.y * t;
    float halfHeight = u_screenHalfWidth / u_aspect;
    if (abs(x) > u_screenHalfWidth || abs(y) > halfHeight) { outColor = vec4(0.02, 0.02, 0.03, 1.0); return; }
    uv = vec2(x / (2.0 * u_screenHalfWidth) + 0.5, 0.5 - y / (2.0 * halfHeight));
  } else if (u_projection == 0) {
    float lon = atan(dir.x, -dir.z) / u_zoom;
    float lat = asin(clamp(dir.y, -1.0, 1.0)) / u_zoom;
    if (abs(lon) > u_fov * 0.5 || abs(lat) > PI * 0.5) { outColor = vec4(0.0, 0.0, 0.0, 1.0); return; }
    uv = vec2(lon / u_fov + 0.5, 0.5 - lat / PI);
  } else {
    float theta = acos(clamp(-dir.z, -1.0, 1.0)) / u_zoom;
    float r = theta / u_fov;
    if (r > 0.5) { outColor = vec4(0.0, 0.0, 0.0, 1.0); return; }
    float phi = atan(dir.y, dir.x);
    uv = vec2(0.5 + r * cos(phi), 0.5 - r * sin(phi));
  }
  if (u_stereo == 1) uv.x = uv.x * 0.5 + u_eye * 0.5;
  else if (u_stereo == 2) uv.y = uv.y * 0.5 + u_eye * 0.5;
  outColor = texture(u_video, uv);
}`;

// The strip is a quad placed in the reference space, so it stays where it was when it appeared.
const HUD_VERTEX_SHADER = `#version 300 es
in vec2 a_position;
uniform mat4 u_viewProjection;
uniform vec3 u_center;
uniform vec3 u_right;
uniform vec3 u_up;
uniform vec2 u_size;
out vec2 v_uv;
void main() {
  v_uv = vec2(a_position.x, 1.0 - a_position.y);
  vec3 world = u_center + (a_position.x - 0.5) * u_size.x * u_right + (a_position.y - 0.5) * u_size.y * u_up;
  gl_Position = u_viewProjection * vec4(world, 1.0);
}`;

const HUD_FRAGMENT_SHADER = `#version 300 es
precision mediump float;
in vec2 v_uv;
out vec4 outColor;
uniform sampler2D u_hud;
void main() {
  outColor = texture(u_hud, v_uv);
}`;

// One WebGL context for every session on the page. Creating a fresh xrCompatible context per session is
// what left desktop Chrome unable to start a second session until the page was reloaded.
let sharedContext: WebGL2RenderingContext | null = null;

function getSharedContext(): WebGL2RenderingContext {
  if (sharedContext && !sharedContext.isContextLost()) return sharedContext;
  const canvas = document.createElement("canvas");
  const gl = canvas.getContext("webgl2", {
    xrCompatible: true,
    antialias: false,
    alpha: false,
    preserveDrawingBuffer: false,
  } as WebGLContextAttributes);
  if (!gl) throw new Error("This browser cannot render WebXR through WebGL 2.");
  sharedContext = gl;
  return gl;
}

function createWebGlRenderer(
  session: XRSessionLike,
  element: () => HTMLVideoElement,
  vr: VrDescriptor,
  space: unknown,
  zoom: ZoomSetting,
  hud: PlaybackHud,
  borrowed?: { gl: WebGL2RenderingContext; layer: object },
) {
  const gl = borrowed?.gl ?? getSharedContext();
  const WebGlLayer = xrGlobal<XRWebGlLayerCtor>("XRWebGLLayer");
  if (!WebGlLayer) throw new Error("This browser cannot render WebXR through WebGL 2.");

  const program = linkProgram(gl, VERTEX_SHADER, FRAGMENT_SHADER);
  const buffer = gl.createBuffer();
  gl.bindBuffer(gl.ARRAY_BUFFER, buffer);
  gl.bufferData(gl.ARRAY_BUFFER, new Float32Array([-1, -1, 3, -1, -1, 3]), gl.STATIC_DRAW);
  const vao = gl.createVertexArray();
  gl.bindVertexArray(vao);
  const position = gl.getAttribLocation(program, "a_position");
  gl.enableVertexAttribArray(position);
  gl.vertexAttribPointer(position, 2, gl.FLOAT, false, 0, 0);

  const texture = gl.createTexture();
  gl.bindTexture(gl.TEXTURE_2D, texture);
  gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_MIN_FILTER, gl.LINEAR);
  gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_MAG_FILTER, gl.LINEAR);
  gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_WRAP_S, gl.CLAMP_TO_EDGE);
  gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_WRAP_T, gl.CLAMP_TO_EDGE);

  gl.useProgram(program);
  const uniforms = {
    inverse: gl.getUniformLocation(program, "u_inverseViewProjection"),
    eye: gl.getUniformLocation(program, "u_eye"),
    zoom: gl.getUniformLocation(program, "u_zoom"),
    orientation: gl.getUniformLocation(program, "u_orientation"),
    screenHalfWidth: gl.getUniformLocation(program, "u_screenHalfWidth"),
    aspect: gl.getUniformLocation(program, "u_aspect"),
  };
  // Where the video's centre points: the viewer's gaze, pitch included, taken once the head has been
  // still for a moment after the video appears (the first frames land while the viewer is still looking
  // at the screen they clicked on), and again whenever they ask to recentre.
  let orientation: Float32Array | null = null;
  const gaze = new GazeSettler();
  gl.uniform1i(gl.getUniformLocation(program, "u_video"), 0);
  gl.uniform1i(
    gl.getUniformLocation(program, "u_projection"),
    vr.projection === "flat" ? 2 : vr.projection === "equirectangular" ? 0 : 1,
  );
  gl.uniform1f(gl.getUniformLocation(program, "u_fov"), (Math.min(vr.fieldOfView, 360) * Math.PI) / 180);
  gl.uniform1i(
    gl.getUniformLocation(program, "u_stereo"),
    vr.stereoMode === "sideBySide" ? 1 : vr.stereoMode === "topBottom" ? 2 : 0,
  );

  // HUD quad: unit square scaled into place per view.
  const hudProgram = linkProgram(gl, HUD_VERTEX_SHADER, HUD_FRAGMENT_SHADER);
  const hudBuffer = gl.createBuffer();
  gl.bindBuffer(gl.ARRAY_BUFFER, hudBuffer);
  gl.bufferData(gl.ARRAY_BUFFER, new Float32Array([0, 0, 1, 0, 0, 1, 1, 0, 1, 1, 0, 1]), gl.STATIC_DRAW);
  const hudVao = gl.createVertexArray();
  gl.bindVertexArray(hudVao);
  const hudPosition = gl.getAttribLocation(hudProgram, "a_position");
  gl.enableVertexAttribArray(hudPosition);
  gl.vertexAttribPointer(hudPosition, 2, gl.FLOAT, false, 0, 0);
  const hudTexture = gl.createTexture();
  gl.bindTexture(gl.TEXTURE_2D, hudTexture);
  gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_MIN_FILTER, gl.LINEAR);
  gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_MAG_FILTER, gl.LINEAR);
  gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_WRAP_S, gl.CLAMP_TO_EDGE);
  gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_WRAP_T, gl.CLAMP_TO_EDGE);
  gl.useProgram(hudProgram);
  gl.uniform1i(gl.getUniformLocation(hudProgram, "u_hud"), 0);
  const hudUniforms = {
    viewProjection: gl.getUniformLocation(hudProgram, "u_viewProjection"),
    center: gl.getUniformLocation(hudProgram, "u_center"),
    right: gl.getUniformLocation(hudProgram, "u_right"),
    up: gl.getUniformLocation(hudProgram, "u_up"),
    size: gl.getUniformLocation(hudProgram, "u_size"),
  };
  let hudPlacement: HudPlacement | null = null;

  // Where each view is drawn. A plain XRWebGLLayer has one framebuffer with a viewport per view; a
  // projection layer (WebXR Layers module, which three.js prefers where available) hands out a
  // colour texture per view through a binding, which we attach to a framebuffer of our own.
  let layer: object;
  let binding: XRWebGlBindingLike | null = null;
  let ownFramebuffer: WebGLFramebuffer | null = null;
  const Binding = xrGlobal<XRWebGlBindingCtor>("XRWebGLBinding");
  if (borrowed) {
    layer = borrowed.layer;
    if (!isWebGlLayer(layer)) {
      if (!Binding) throw new Error("This browser lent a projection layer but has no XRWebGLBinding to draw into it.");
      binding = new Binding(session, gl);
      ownFramebuffer = gl.createFramebuffer();
    }
  } else if (Binding && "createProjectionLayer" in Binding.prototype) {
    // Where the Layers module exists, draw the way a gallery's three.js renderer does: into a
    // projection layer, which is what the Quest Browser composites best.
    binding = new Binding(session, gl);
    layer = binding.createProjectionLayer!({ colorFormat: gl.RGBA8, depthFormat: 0, scaleFactor: 1 });
    session.updateRenderState({ layers: [layer] });
    ownFramebuffer = gl.createFramebuffer();
  } else {
    const webGlLayer = new WebGlLayer(session, gl);
    session.updateRenderState({ baseLayer: webGlLayer });
    layer = webGlLayer;
  }
  const targets = (view: XRViewLike): { x: number; y: number; width: number; height: number } | null => {
    if (isWebGlLayer(layer)) {
      gl.bindFramebuffer(gl.FRAMEBUFFER, layer.framebuffer);
      return layer.getViewport(view);
    }
    const subImage = binding!.getViewSubImage(layer, view);
    gl.bindFramebuffer(gl.FRAMEBUFFER, ownFramebuffer);
    gl.framebufferTexture2D(gl.FRAMEBUFFER, gl.COLOR_ATTACHMENT0, gl.TEXTURE_2D, subImage.colorTexture, 0);
    return subImage.viewport;
  };
  let hasFrame = false;
  let hudUploaded = false;

  return {
    render(frame: XRFrameLike) {
      const pose = frame.getViewerPose(space);
      if (!pose) return;
      // A borrowed context may have any state left from its owner's last draw.
      gl.disable(gl.BLEND);
      gl.disable(gl.DEPTH_TEST);
      gl.disable(gl.CULL_FACE);
      gl.disable(gl.SCISSOR_TEST);
      gl.disable(gl.STENCIL_TEST);
      gl.colorMask(true, true, true, true);
      gl.activeTexture(gl.TEXTURE0);
      gl.bindTexture(gl.TEXTURE_2D, texture);
      // Upload state is per context and three.js leaves "flip Y" on for its own textures, which turns
      // a video uploaded through a borrowed context upside down.
      gl.pixelStorei(gl.UNPACK_FLIP_Y_WEBGL, false);
      gl.pixelStorei(gl.UNPACK_PREMULTIPLY_ALPHA_WEBGL, false);
      gl.pixelStorei(gl.UNPACK_COLORSPACE_CONVERSION_WEBGL, gl.BROWSER_DEFAULT_WEBGL);
      const video = element();
      if (video.readyState >= HTMLMediaElement.HAVE_CURRENT_DATA) {
        gl.texImage2D(gl.TEXTURE_2D, 0, gl.RGBA, gl.RGBA, gl.UNSIGNED_BYTE, video);
        hasFrame = true;
      }
      if (!hasFrame) {
        for (const view of pose.views) {
          const viewport = targets(view);
          if (!viewport) continue;
          gl.enable(gl.SCISSOR_TEST);
          gl.scissor(viewport.x, viewport.y, viewport.width, viewport.height);
          gl.clearColor(0, 0, 0, 1);
          gl.clear(gl.COLOR_BUFFER_BIT);
          gl.disable(gl.SCISSOR_TEST);
        }
        return;
      }
      // The video faces wherever the viewer looked once they had settled, pitch included.
      if (gaze.settled(pose.views[0])) orientation = gazeOrientation(pose.views[0]);
      orientation ??= LEVEL_ORIENTATION;
      gl.useProgram(program);
      gl.bindVertexArray(vao);
      gl.uniform1f(uniforms.zoom, zoom.value);
      gl.uniformMatrix3fv(uniforms.orientation, false, orientation);
      if (vr.projection === "flat") {
        // Zoom makes the screen larger; the frame's own aspect ratio shapes it.
        gl.uniform1f(uniforms.screenHalfWidth, Math.tan(FLAT_SCREEN_HALF_ANGLE) * zoom.value);
        gl.uniform1f(uniforms.aspect, flatEyeAspect(video.videoWidth, video.videoHeight, vr.stereoMode));
      }
      for (const view of pose.views) {
        const viewport = targets(view);
        if (!viewport) continue;
        gl.viewport(viewport.x, viewport.y, viewport.width, viewport.height);
        gl.uniformMatrix4fv(uniforms.inverse, false, inverseViewProjection(view));
        gl.uniform1f(uniforms.eye, view.eye === "right" ? 1 : 0);
        gl.drawArrays(gl.TRIANGLES, 0, 3);
      }

      if (!hud.visible) {
        hudPlacement = null;
        gl.bindVertexArray(null);
        return;
      }
      // Place the strip where the viewer is looking when it appears; it then stays there.
      hudPlacement ??= placeHud(pose.views[0]);
      const changed = hud.update();
      gl.bindTexture(gl.TEXTURE_2D, hudTexture);
      if (changed || !hudUploaded) {
        gl.texImage2D(gl.TEXTURE_2D, 0, gl.RGBA, gl.RGBA, gl.UNSIGNED_BYTE, hud.canvas);
        hudUploaded = true;
      }
      gl.enable(gl.BLEND);
      gl.blendFunc(gl.SRC_ALPHA, gl.ONE_MINUS_SRC_ALPHA);
      gl.useProgram(hudProgram);
      gl.bindVertexArray(hudVao);
      gl.uniform3fv(hudUniforms.center, hudPlacement.center);
      gl.uniform3fv(hudUniforms.right, hudPlacement.right);
      gl.uniform3fv(hudUniforms.up, hudPlacement.up);
      gl.uniform2f(hudUniforms.size, HUD_WORLD_WIDTH, (HUD_WORLD_WIDTH * HUD_HEIGHT) / HUD_WIDTH);
      for (const view of pose.views) {
        const viewport = targets(view);
        if (!viewport) continue;
        gl.viewport(viewport.x, viewport.y, viewport.width, viewport.height);
        gl.uniformMatrix4fv(
          hudUniforms.viewProjection,
          false,
          multiply4(view.projectionMatrix, view.transform.inverse.matrix),
        );
        gl.drawArrays(gl.TRIANGLES, 0, 6);
      }
      gl.disable(gl.BLEND);
      gl.bindVertexArray(null);
    },
    /** Aim the video at the current gaze on the next frame. */
    recenter() {
      gaze.recaptureNow();
    },
    dispose() {
      if (ownFramebuffer) gl.deleteFramebuffer(ownFramebuffer);
      gl.deleteTexture(texture);
      gl.deleteTexture(hudTexture);
      gl.deleteBuffer(buffer);
      gl.deleteBuffer(hudBuffer);
      gl.deleteVertexArray(vao);
      gl.deleteVertexArray(hudVao);
      gl.deleteProgram(program);
      gl.deleteProgram(hudProgram);
    },
  };
}

/** No rotation: the video's centre straight ahead in the reference space, horizon level. */
const LEVEL_ORIENTATION = new Float32Array([1, 0, 0, 0, 1, 0, 0, 0, 1]);

// The gaze counts as settled once it has moved less than this for this long.
const GAZE_SETTLE_DEGREES = 3;
const GAZE_SETTLE_MS = 700;
// Give up waiting and take whatever the gaze is after this long.
const GAZE_SETTLE_TIMEOUT_MS = 6000;

/**
 * Decides when to read the viewer's gaze for aiming the video: after the head has held still for a
 * moment, or after a timeout, and again on request. Exported for tests.
 */
export class GazeSettler {
  private lastForward: number[] | null = null;
  private steadySince = 0;
  private startedAt: number | null = null;
  private done = false;
  private forced = false;

  /** Take the next frame's gaze regardless of movement. */
  recaptureNow() {
    this.forced = true;
  }

  /** Whether this frame's gaze should become the video's centre. */
  settled(view: Pick<XRViewLike, "transform">, now = performance.now()): boolean {
    if (this.forced) {
      this.forced = false;
      this.done = true;
      this.lastForward = null;
      return true;
    }
    if (this.done) return false;
    const m = view.transform.inverse.matrix;
    const forward = [-m[2], -m[6], -m[10]];
    this.startedAt ??= now;
    if (this.lastForward) {
      const dot = Math.max(
        -1,
        Math.min(
          1,
          forward[0] * this.lastForward[0] + forward[1] * this.lastForward[1] + forward[2] * this.lastForward[2],
        ),
      );
      const moved = (Math.acos(dot) * 180) / Math.PI;
      if (moved > GAZE_SETTLE_DEGREES) this.steadySince = now;
    } else {
      this.steadySince = now;
    }
    this.lastForward = forward;
    if (now - this.steadySince >= GAZE_SETTLE_MS || now - this.startedAt >= GAZE_SETTLE_TIMEOUT_MS) {
      this.done = true;
      return true;
    }
    return false;
  }
}

/**
 * The rotation that takes world directions into the video's frame, so that the video's centre lies
 * along the viewer's gaze (yaw and pitch, never roll) at the moment it is captured. Column-major
 * 3×3. Exported for tests. Looking straight up or down keeps only the yaw.
 */
export function gazeOrientation(view: Pick<XRViewLike, "transform">): Float32Array {
  const m = view.transform.inverse.matrix;
  // The eye looks down -z in eye space; that direction in world space is minus the third row of R.
  const forward = [-m[2], -m[6], -m[10]];
  const length = Math.hypot(forward[0], forward[1], forward[2]) || 1;
  const f = forward.map((component) => component / length);
  // right = f × up, level with the floor; degenerate when looking straight up or down.
  let right = [-f[2], 0, f[0]];
  const rightLength = Math.hypot(right[0], right[2]);
  if (rightLength < 1e-3) {
    // Fall back to the eye's own right axis, flattened.
    right = [m[0], 0, m[8]];
    const fallback = Math.hypot(right[0], right[2]) || 1;
    right = [right[0] / fallback, 0, right[2] / fallback];
  } else {
    right = [right[0] / rightLength, 0, right[2] / rightLength];
  }
  // up = right × f
  const up = [right[1] * f[2] - right[2] * f[1], right[2] * f[0] - right[0] * f[2], right[0] * f[1] - right[1] * f[0]];
  // Q has columns [right, up, -f] (video axes in world space); we want Q^T, whose columns are Q's rows.
  return new Float32Array([right[0], up[0], -f[0], right[1], up[1], -f[1], right[2], up[2], -f[2]]);
}

interface HudPlacement {
  center: Float32Array;
  right: Float32Array;
  up: Float32Array;
}

/**
 * A spot ahead of and just below the viewer's current gaze, pitch included, facing them. Exported
 * for tests. The view matrix maps world to eye space; its rotation rows give the eye axes in world
 * space and its translation gives the eye position.
 */
export function placeHud(view: Pick<XRViewLike, "transform">): HudPlacement {
  const m = view.transform.inverse.matrix;
  // Eye position: -R^T t.
  const eye = [
    -(m[0] * m[12] + m[1] * m[13] + m[2] * m[14]),
    -(m[4] * m[12] + m[5] * m[13] + m[6] * m[14]),
    -(m[8] * m[12] + m[9] * m[13] + m[10] * m[14]),
  ];
  // The gaze frame: forward along the gaze, right level with the floor, up perpendicular to both.
  const q = gazeOrientation(view);
  // gazeOrientation returns Q^T column-major, so Q's columns are its rows: right, up, -forward.
  const right = [q[0], q[3], q[6]];
  const up = [q[1], q[4], q[7]];
  const forward = [-q[2], -q[5], -q[8]];
  return {
    center: new Float32Array([
      eye[0] + forward[0] * HUD_DISTANCE - up[0] * HUD_DROP,
      eye[1] + forward[1] * HUD_DISTANCE - up[1] * HUD_DROP,
      eye[2] + forward[2] * HUD_DISTANCE - up[2] * HUD_DROP,
    ]),
    right: new Float32Array(right),
    up: new Float32Array(up),
  };
}

function linkProgram(gl: WebGL2RenderingContext, vertexSource: string, fragmentSource: string) {
  const compile = (type: number, source: string) => {
    const shader = gl.createShader(type)!;
    gl.shaderSource(shader, source);
    gl.compileShader(shader);
    if (!gl.getShaderParameter(shader, gl.COMPILE_STATUS))
      throw new Error(gl.getShaderInfoLog(shader) ?? "shader error");
    return shader;
  };
  const program = gl.createProgram()!;
  gl.attachShader(program, compile(gl.VERTEX_SHADER, vertexSource));
  gl.attachShader(program, compile(gl.FRAGMENT_SHADER, fragmentSource));
  gl.linkProgram(program);
  if (!gl.getProgramParameter(program, gl.LINK_STATUS)) throw new Error(gl.getProgramInfoLog(program) ?? "link error");
  return program;
}

/** inverse(projection * view) with the view's translation dropped, column-major. Exported for tests. */
export function inverseViewProjection(view: Pick<XRViewLike, "projectionMatrix" | "transform">): Float32Array {
  const rotation = Float32Array.from(view.transform.inverse.matrix);
  rotation[12] = rotation[13] = rotation[14] = 0;
  return invert4(multiply4(view.projectionMatrix, rotation));
}

function multiply4(a: ArrayLike<number>, b: ArrayLike<number>): Float32Array {
  const out = new Float32Array(16);
  for (let column = 0; column < 4; column++)
    for (let row = 0; row < 4; row++) {
      let sum = 0;
      for (let k = 0; k < 4; k++) sum += a[k * 4 + row] * b[column * 4 + k];
      out[column * 4 + row] = sum;
    }
  return out;
}

function invert4(m: ArrayLike<number>): Float32Array {
  const [a00, a01, a02, a03, a10, a11, a12, a13, a20, a21, a22, a23, a30, a31, a32, a33] = Array.from(m);
  const b00 = a00 * a11 - a01 * a10;
  const b01 = a00 * a12 - a02 * a10;
  const b02 = a00 * a13 - a03 * a10;
  const b03 = a01 * a12 - a02 * a11;
  const b04 = a01 * a13 - a03 * a11;
  const b05 = a02 * a13 - a03 * a12;
  const b06 = a20 * a31 - a21 * a30;
  const b07 = a20 * a32 - a22 * a30;
  const b08 = a20 * a33 - a23 * a30;
  const b09 = a21 * a32 - a22 * a31;
  const b10 = a21 * a33 - a23 * a31;
  const b11 = a22 * a33 - a23 * a32;
  const det = b00 * b11 - b01 * b10 + b02 * b09 + b03 * b08 - b04 * b07 + b05 * b06;
  const inv = det === 0 ? 0 : 1 / det;
  return new Float32Array([
    (a11 * b11 - a12 * b10 + a13 * b09) * inv,
    (a02 * b10 - a01 * b11 - a03 * b09) * inv,
    (a31 * b05 - a32 * b04 + a33 * b03) * inv,
    (a22 * b04 - a21 * b05 - a23 * b03) * inv,
    (a12 * b08 - a10 * b11 - a13 * b07) * inv,
    (a00 * b11 - a02 * b08 + a03 * b07) * inv,
    (a32 * b02 - a30 * b05 - a33 * b01) * inv,
    (a20 * b05 - a22 * b02 + a23 * b01) * inv,
    (a10 * b10 - a11 * b08 + a13 * b06) * inv,
    (a01 * b08 - a00 * b10 - a03 * b06) * inv,
    (a30 * b04 - a31 * b02 + a33 * b00) * inv,
    (a21 * b02 - a20 * b04 - a23 * b00) * inv,
    (a11 * b07 - a10 * b09 - a12 * b06) * inv,
    (a00 * b09 - a01 * b07 + a02 * b06) * inv,
    (a31 * b01 - a30 * b03 - a32 * b00) * inv,
    (a20 * b03 - a21 * b01 + a22 * b00) * inv,
  ]);
}
