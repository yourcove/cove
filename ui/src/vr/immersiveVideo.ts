/**
 * Immersive (WebXR) playback of a VR video element.
 *
 * Two paths, chosen per session:
 * - **Media layer** (Quest Browser, and any runtime with WebXR Layers): the video element is bound to an
 *   equirect layer that the compositor samples directly. No per-frame texture upload, so 8K plays as well as
 *   in a native player. Equirectangular only.
 * - **WebGL** (desktop Chrome/Edge driving SteamVR through OpenXR, which ship WebXR without media layers):
 *   the frame is uploaded as a texture every frame and a full-screen shader reprojects it per eye. Handles
 *   fisheye and MKX200 too, but costs a texture upload per frame, which is fine on a PC GPU and heavy on a
 *   standalone headset.
 *
 * Controls are controller-only: trigger toggles play, thumbstick left/right seeks, B/Y or the menu button
 * leaves the video (ending the session, or handing back to whoever opened it). Exposed to extensions as `@cove/runtime/webxr`, so a gallery
 * extension hands off to exactly the same playback as the core player.
 */

export type VrProjection = "equirectangular" | "fisheye" | "mkx200";
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

export interface InSessionVideoOptions {
  /**
   * Called when the viewer presses B/Y (or the menu button) to leave the video. The playback has already
   * been stopped and the session's previous layers restored by then.
   */
  onExit?: () => void;
  /** Seconds skipped per thumbstick flick. Defaults to 10. */
  seekStepSeconds?: number;
  /** Force the WebGL path even when media layers are available. Mostly for debugging. */
  preferWebGl?: boolean;
}

export interface ImmersiveVideoOptions extends Omit<InSessionVideoOptions, "onExit"> {
  /** Called once the session has ended, whichever side ended it. */
  onEnd?: () => void;
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
interface XRGamepadLike {
  buttons: ReadonlyArray<{ pressed: boolean }>;
  axes: ReadonlyArray<number>;
}
interface XRInputSourceLike {
  handedness: string;
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
interface XRSessionLike extends EventTarget {
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

export async function isImmersiveVrSupported(): Promise<boolean> {
  return (await getImmersiveVrSupport()).supported;
}

/** Equirect media layers can only show equirectangular video; everything else needs the WebGL path. */
export function canUseMediaLayer(vr: VrDescriptor): boolean {
  return vr.projection === "equirectangular";
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
  const xr = xrSystem();
  if (!xr) throw new Error("WebXR is not available. Immersive playback needs a secure context (HTTPS or localhost).");

  const session = await xr.requestSession("immersive-vr", immersiveSessionInit());
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
      onExit: () => void session.end().catch(() => {}),
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
  // "local" keeps the sphere centred on where the viewer's head was at the start, which is what video wants.
  const space = await xrSession.requestReferenceSpace("local");
  const previousRenderState = snapshotRenderState(xrSession);
  const useLayer =
    !options.preferWebGl &&
    canUseMediaLayer(vr) &&
    xrGlobal<XRMediaBindingCtor>("XRMediaBinding") != null &&
    (xrSession.enabledFeatures?.includes("layers") ?? false);

  let render: ((frame: XRFrameLike) => void) | null = null;
  let dispose: () => void = () => {};
  if (useLayer) {
    const MediaBinding = xrGlobal<XRMediaBindingCtor>("XRMediaBinding")!;
    const layer = new MediaBinding(xrSession).createEquirectLayer(video, equirectLayerInit(vr, space));
    xrSession.updateRenderState({ layers: [layer] });
  } else {
    const renderer = createWebGlRenderer(xrSession, video, vr, space);
    render = renderer.render;
    dispose = renderer.dispose;
  }

  let stopped = false;
  const onSelect = () => void togglePlayback(video);
  const stop = () => {
    if (stopped) return;
    stopped = true;
    xrSession.removeEventListener("select", onSelect);
    dispose();
    try {
      xrSession.updateRenderState(previousRenderState);
    } catch {
      // The session is already ending; there is nothing left to restore.
    }
  };
  const controls = createControllerMapping(xrSession, video, options.seekStepSeconds ?? 10, () => {
    stop();
    options.onExit?.();
  });
  xrSession.addEventListener("select", onSelect);

  const loop = (_time: number, frame: XRFrameLike) => {
    if (stopped) return;
    controls.poll();
    render?.(frame);
    if (!stopped) xrSession.requestAnimationFrame(loop);
  };
  xrSession.requestAnimationFrame(loop);
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

// xr-standard gamepad mapping: 0 trigger, 1 squeeze, 3 thumbstick press, 4 A/X, 5 B/Y; axes 2/3 thumbstick.
const EXIT_BUTTON = 5;
const STICK_X = 2;
const STICK_THRESHOLD = 0.6;
const SEEK_REPEAT_MS = 400;

function createControllerMapping(session: XRSessionLike, video: HTMLVideoElement, step: number, exit: () => void) {
  const exitHeld = new Set<string>();
  const lastSeekAt = new Map<string, number>();
  return {
    poll() {
      for (const source of session.inputSources) {
        const gamepad = source.gamepad;
        if (!gamepad) continue;
        const key = source.handedness;

        const exitPressed = gamepad.buttons[EXIT_BUTTON]?.pressed ?? false;
        if (exitPressed && !exitHeld.has(key)) {
          exit();
          return;
        }
        if (exitPressed) exitHeld.add(key);
        else exitHeld.delete(key);

        const x = gamepad.axes[STICK_X] ?? 0;
        if (Math.abs(x) < STICK_THRESHOLD) {
          lastSeekAt.delete(key);
          continue;
        }
        const now = performance.now();
        const last = lastSeekAt.get(key);
        if (last != null && now - last < SEEK_REPEAT_MS) continue;
        lastSeekAt.set(key, now);
        const duration = Number.isFinite(video.duration) ? video.duration : Infinity;
        video.currentTime = Math.min(Math.max(0, video.currentTime + Math.sign(x) * step), duration);
      }
    },
  };
}

// ---- WebGL fallback ------------------------------------------------------------------------------------

const VERTEX_SHADER = `#version 300 es
in vec2 a_position;
out vec2 v_ndc;
void main() {
  v_ndc = a_position;
  gl_Position = vec4(a_position, 0.0, 1.0);
}`;

// Casts a ray per pixel through the inverse view-projection (rotation only, so the video stays at
// infinity) and looks the direction up in the source frame for this eye.
const FRAGMENT_SHADER = `#version 300 es
precision highp float;
in vec2 v_ndc;
out vec4 outColor;
uniform sampler2D u_video;
uniform mat4 u_inverseViewProjection;
uniform int u_projection;   // 0 equirect, 1 fisheye (equidistant; MKX200 approximated)
uniform float u_fov;        // radians
uniform int u_stereo;       // 0 mono, 1 side by side, 2 top/bottom
uniform float u_eye;        // 0 left, 1 right
const float PI = 3.141592653589793;

void main() {
  vec4 far = u_inverseViewProjection * vec4(v_ndc, 1.0, 1.0);
  vec3 dir = normalize(far.xyz / far.w);
  vec2 uv;
  if (u_projection == 0) {
    float lon = atan(dir.x, -dir.z);
    float lat = asin(clamp(dir.y, -1.0, 1.0));
    if (abs(lon) > u_fov * 0.5) { outColor = vec4(0.0, 0.0, 0.0, 1.0); return; }
    uv = vec2(lon / u_fov + 0.5, 0.5 - lat / PI);
  } else {
    float theta = acos(clamp(-dir.z, -1.0, 1.0));
    float r = theta / u_fov;
    if (r > 0.5) { outColor = vec4(0.0, 0.0, 0.0, 1.0); return; }
    float phi = atan(dir.y, dir.x);
    uv = vec2(0.5 + r * cos(phi), 0.5 - r * sin(phi));
  }
  if (u_stereo == 1) uv.x = uv.x * 0.5 + u_eye * 0.5;
  else if (u_stereo == 2) uv.y = uv.y * 0.5 + u_eye * 0.5;
  outColor = texture(u_video, uv);
}`;

function createWebGlRenderer(session: XRSessionLike, video: HTMLVideoElement, vr: VrDescriptor, space: unknown) {
  const canvas = document.createElement("canvas");
  const gl = canvas.getContext("webgl2", {
    xrCompatible: true,
    antialias: false,
    alpha: false,
  } as WebGLContextAttributes);
  const WebGlLayer = xrGlobal<XRWebGlLayerCtor>("XRWebGLLayer");
  if (!gl || !WebGlLayer) throw new Error("This browser cannot render WebXR through WebGL 2.");

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
  };
  gl.uniform1i(gl.getUniformLocation(program, "u_video"), 0);
  gl.uniform1i(gl.getUniformLocation(program, "u_projection"), vr.projection === "equirectangular" ? 0 : 1);
  gl.uniform1f(gl.getUniformLocation(program, "u_fov"), (Math.min(vr.fieldOfView, 360) * Math.PI) / 180);
  gl.uniform1i(
    gl.getUniformLocation(program, "u_stereo"),
    vr.stereoMode === "sideBySide" ? 1 : vr.stereoMode === "topBottom" ? 2 : 0,
  );

  const layer = new WebGlLayer(session, gl);
  session.updateRenderState({ baseLayer: layer });
  let hasFrame = false;

  return {
    render(frame: XRFrameLike) {
      const pose = frame.getViewerPose(space);
      if (!pose) return;
      gl.bindFramebuffer(gl.FRAMEBUFFER, layer.framebuffer);
      gl.bindTexture(gl.TEXTURE_2D, texture);
      if (video.readyState >= HTMLMediaElement.HAVE_CURRENT_DATA) {
        gl.texImage2D(gl.TEXTURE_2D, 0, gl.RGBA, gl.RGBA, gl.UNSIGNED_BYTE, video);
        hasFrame = true;
      }
      if (!hasFrame) {
        gl.clearColor(0, 0, 0, 1);
        gl.clear(gl.COLOR_BUFFER_BIT);
        return;
      }
      gl.useProgram(program);
      gl.bindVertexArray(vao);
      for (const view of pose.views) {
        const viewport = layer.getViewport(view);
        if (!viewport) continue;
        gl.viewport(viewport.x, viewport.y, viewport.width, viewport.height);
        gl.uniformMatrix4fv(uniforms.inverse, false, inverseViewProjection(view));
        gl.uniform1f(uniforms.eye, view.eye === "right" ? 1 : 0);
        gl.drawArrays(gl.TRIANGLES, 0, 3);
      }
    },
    dispose() {
      gl.deleteTexture(texture);
      gl.deleteBuffer(buffer);
      gl.deleteVertexArray(vao);
      gl.deleteProgram(program);
    },
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
