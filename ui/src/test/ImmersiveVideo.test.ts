import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { sharedModuleSpecifiers } from "../generated/extensions/runtime/v1/contract";
import {
  canUseMediaLayer,
  cancelSessionHandoff,
  claimHandedOffSession,
  createControllerMapping,
  equirectLayerInit,
  flatEyeAspect,
  formatTime,
  formatVrLayout,
  GazeSettler,
  gazeOrientation,
  hasHandedOffSession,
  inverseViewProjection,
  offerSessionHandoff,
  placeHud,
  playVideoInSession,
  seekStepAfter,
  steeringAxes,
  type VrDescriptor,
  type XRSessionLike,
} from "../vr/immersiveVideo";
import { secureUrlFor } from "../components/EnterVrButton";

const identity = new Float32Array([1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1]);

function perspective(fovY: number, aspect: number, near: number, far: number) {
  const f = 1 / Math.tan(fovY / 2);
  const nf = 1 / (near - far);
  return new Float32Array([f / aspect, 0, 0, 0, 0, f, 0, 0, 0, 0, (far + near) * nf, -1, 0, 0, 2 * far * near * nf, 0]);
}

function apply(m: Float32Array, v: [number, number, number, number]) {
  const out = [0, 0, 0, 0];
  for (let row = 0; row < 4; row++) for (let k = 0; k < 4; k++) out[row] += m[k * 4 + row] * v[k];
  return out;
}

describe("immersive video", () => {
  it("publishes the helper to extensions", () => {
    expect(sharedModuleSpecifiers).toContain("@cove/runtime/webxr");
  });

  it("maps layouts onto equirect media layers", () => {
    const sbs180: VrDescriptor = { projection: "equirectangular", fieldOfView: 180, stereoMode: "sideBySide" };
    const tb360: VrDescriptor = { projection: "equirectangular", fieldOfView: 360, stereoMode: "topBottom" };

    expect(equirectLayerInit(sbs180, "space")).toMatchObject({
      layout: "stereo-left-right",
      centralHorizontalAngle: Math.PI,
    });
    expect(equirectLayerInit(tb360, "space")).toMatchObject({
      layout: "stereo-top-bottom",
      centralHorizontalAngle: Math.PI * 2,
    });
    expect(canUseMediaLayer(sbs180)).toBe(true);
    expect(canUseMediaLayer({ projection: "mkx200", fieldOfView: 200, stereoMode: "sideBySide" })).toBe(false);
  });

  it("casts the screen centre straight ahead and ignores head position", () => {
    // Head moved 2m sideways: the video sits at infinity, so the ray must not shift.
    const view = {
      projectionMatrix: perspective(Math.PI / 2, 1, 0.1, 1000),
      transform: { inverse: { matrix: Float32Array.from([...identity.slice(0, 12), -2, 0, 0, 1]) } },
    };
    const [x, y, z, w] = apply(inverseViewProjection(view), [0, 0, 1, 1]);

    expect(Math.abs(x / w)).toBeLessThan(1e-3);
    expect(Math.abs(y / w)).toBeLessThan(1e-3);
    expect(z / w).toBeLessThan(0);
  });

  it("follows head rotation", () => {
    // Looking 90° to the right: the view matrix (inverse of the pose) rotates the world by +90° about Y.
    const view = {
      projectionMatrix: perspective(Math.PI / 2, 1, 0.1, 1000),
      transform: { inverse: { matrix: new Float32Array([0, 0, -1, 0, 0, 1, 0, 0, 1, 0, 0, 0, 0, 0, 0, 1]) } },
    };
    const [x, , z, w] = apply(inverseViewProjection(view), [0, 0, 1, 1]);

    expect(x / w).toBeGreaterThan(0);
    expect(Math.abs(z / w)).toBeLessThan(1e-2 * Math.abs(x / w));
  });

  it("shapes a 3D film's screen like one eye's picture, whether packed at full or half size", () => {
    // Full side by side: two 16:9 eyes next to each other.
    expect(flatEyeAspect(3840, 1080, "sideBySide")).toBeCloseTo(16 / 9);
    // Half side by side: two squeezed eyes in a 16:9 frame.
    expect(flatEyeAspect(1920, 1080, "sideBySide")).toBeCloseTo(16 / 9);
    // A scope film at full size keeps its width.
    expect(flatEyeAspect(2 * 1920, 804, "sideBySide")).toBeCloseTo(1920 / 804);
    // Full and half over-under.
    expect(flatEyeAspect(1920, 2160, "topBottom")).toBeCloseTo(16 / 9);
    expect(flatEyeAspect(1920, 1080, "topBottom")).toBeCloseTo(16 / 9);
    expect(flatEyeAspect(1280, 720, "mono")).toBeCloseTo(16 / 9);
    expect(flatEyeAspect(0, 0, "mono")).toBeCloseTo(16 / 9);
  });

  it("labels layouts and times", () => {
    expect(formatVrLayout({ projection: "equirectangular", fieldOfView: 180, stereoMode: "sideBySide" })).toBe(
      "180° SBS",
    );
    expect(formatVrLayout({ projection: "fisheye", fieldOfView: 190, stereoMode: "topBottom" })).toBe(
      "Fisheye 190° TB",
    );
    expect(formatVrLayout({ projection: "mkx200", fieldOfView: 200, stereoMode: "mono" })).toBe("MKX200 Mono");
    expect(formatVrLayout(null)).toBe("VR");
    expect(formatVrLayout({ projection: "flat", fieldOfView: 0, stereoMode: "mono" })).toBe("2D");
    expect(formatVrLayout({ projection: "flat", fieldOfView: 0, stereoMode: "sideBySide" })).toBe("3D SBS");
    expect(formatVrLayout({ projection: "flat", fieldOfView: 0, stereoMode: "topBottom" })).toBe("3D TB");
    expect(formatTime(65)).toBe("1:05");
    expect(formatTime(3725)).toBe("1:02:05");
    expect(formatTime(Number.NaN)).toBe("0:00");
  });

  it("hands a session from one page to the next exactly once", () => {
    const session = {};
    let returned = 0;
    offerSessionHandoff(session, { onReturn: () => returned++ });
    expect(hasHandedOffSession()).toBe(true);

    const claimed = claimHandedOffSession();
    expect(claimed?.session).toBe(session);
    expect(hasHandedOffSession()).toBe(false);
    expect(claimHandedOffSession()).toBeNull();

    claimed!.returnToOwner();
    claimed!.returnToOwner();
    expect(returned).toBe(1);
  });

  it("lets an unclaimed offer expire or be withdrawn", () => {
    vi.useFakeTimers();
    try {
      let timedOut = 0;
      offerSessionHandoff({}, { onReturn: () => {}, onTimeout: () => timedOut++, timeoutMs: 50 });
      vi.advanceTimersByTime(60);
      expect(timedOut).toBe(1);
      expect(hasHandedOffSession()).toBe(false);

      offerSessionHandoff({}, { onReturn: () => {}, onTimeout: () => timedOut++, timeoutMs: 50 });
      cancelSessionHandoff();
      vi.advanceTimersByTime(60);
      expect(timedOut).toBe(1);
    } finally {
      vi.useRealTimers();
    }
  });

  it("passes the owner's presenter along with the session", () => {
    const presenter = { gl: {} as WebGL2RenderingContext, layer: {}, referenceSpace: {}, setFrameRenderer: () => {} };
    offerSessionHandoff({}, { onReturn: () => {}, presenter });
    expect(claimHandedOffSession()?.presenter).toBe(presenter);
  });

  it("reports which page gave a session back and why", () => {
    const reasons: string[] = [];
    offerSessionHandoff({}, { onReturn: (reason) => reasons.push(reason) });
    claimHandedOffSession()!.returnToOwner("abandoned");
    offerSessionHandoff({}, { onReturn: (reason) => reasons.push(reason) });
    claimHandedOffSession()!.returnToOwner();
    expect(reasons).toEqual(["abandoned", "exit"]);
  });

  it("steers with the thumbstick and ignores a touchpad that sits under the thumb", () => {
    expect(steeringAxes([0, 0, 0.9, 0])).toEqual([0.9, 0]);
    expect(steeringAxes([0, -0.8, 0, 0])).toEqual([0, 0]);
    // A controller with only a touchpad still steers with it.
    expect(steeringAxes([0.7, -0.2])).toEqual([0.7, -0.2]);
  });

  it("aims the video along the viewer's gaze, pitch included", () => {
    const apply3 = (q: Float32Array, v: number[]) =>
      [0, 1, 2].map((row) => q[row] * v[0] + q[3 + row] * v[1] + q[6 + row] * v[2]);
    // Looking straight ahead: no change.
    expect(
      apply3(gazeOrientation({ transform: { inverse: { matrix: identity } } }), [0, 0, -1]).map((n) => +n.toFixed(6)),
    ).toEqual([0, 0, -1]);
    // A view matrix of Rx(30°) is a head pitched 30° down, gazing along (0, -sin, -cos). That gaze must
    // land on the video's centre (0, 0, -1), and a world direction to the right stays to the right.
    const c = Math.cos(Math.PI / 6);
    const s = Math.sin(Math.PI / 6);
    const lookingDown = new Float32Array([1, 0, 0, 0, 0, c, s, 0, 0, -s, c, 0, 0, 0, 0, 1]);
    const q = gazeOrientation({ transform: { inverse: { matrix: lookingDown } } });
    const gaze = apply3(q, [0, -s, -c]);
    expect(gaze[0]).toBeCloseTo(0);
    expect(gaze[1]).toBeCloseTo(0);
    expect(gaze[2]).toBeCloseTo(-1);
    expect(apply3(q, [1, 0, 0]).map((n) => +n.toFixed(6))).toEqual([1, 0, 0]);
  });

  it("aims the video only once the head has held still, or on request", () => {
    const level = { transform: { inverse: { matrix: identity } } };
    const turned = {
      transform: { inverse: { matrix: new Float32Array([0, 0, -1, 0, 0, 1, 0, 0, 1, 0, 0, 0, 0, 0, 0, 1]) } },
    };
    const settler = new GazeSettler();
    expect(settler.settled(level, 0)).toBe(false);
    expect(settler.settled(level, 300)).toBe(false);
    // Moving resets the wait.
    expect(settler.settled(turned, 600)).toBe(false);
    expect(settler.settled(turned, 900)).toBe(false);
    expect(settler.settled(turned, 1400)).toBe(true);
    // Captured once; later frames leave it alone until asked.
    expect(settler.settled(level, 2000)).toBe(false);
    settler.recaptureNow();
    expect(settler.settled(level, 2100)).toBe(true);
    expect(settler.settled(level, 2200)).toBe(false);
  });

  it("gives up waiting for a still head after a while", () => {
    const settler = new GazeSettler();
    const frames = [0, 500, 1000, 1500, 2000, 2500, 3000, 3500, 4000, 4500, 5000, 5500, 6100];
    const results = frames.map((t, i) => {
      // A head that never stops moving: alternate two directions every frame.
      const matrix = i % 2 === 0 ? identity : new Float32Array([0, 0, -1, 0, 0, 1, 0, 0, 1, 0, 0, 0, 0, 0, 0, 1]);
      return settler.settled({ transform: { inverse: { matrix } } }, t);
    });
    expect(results.slice(0, -1).every((r) => r === false)).toBe(true);
    expect(results[results.length - 1]).toBe(true);
  });

  it("speeds the seek up the longer the stick is held", () => {
    expect(seekStepAfter(0, 10)).toBe(10);
    expect(seekStepAfter(1999, 10)).toBe(10);
    expect(seekStepAfter(2000, 10)).toBe(30);
    expect(seekStepAfter(4999, 10)).toBe(30);
    expect(seekStepAfter(5000, 10)).toBe(120);
    expect(seekStepAfter(60_000, 10)).toBe(120);
  });

  it("moves a held stick's playhead in ramping steps and seeks the element only when it is free", () => {
    let now = 0;
    const clock = vi.spyOn(performance, "now").mockImplementation(() => now);
    try {
      let position = 0;
      let busy = false;
      const seeks: number[] = [];
      const targets: number[] = [];
      const gamepad = { buttons: Array.from({ length: 6 }, () => ({ pressed: false })), axes: [0, 0, 1, 0] };
      const session = { inputSources: [{ handedness: "right", gamepad }] } as unknown as XRSessionLike;
      const transport = {
        currentTime: () => position,
        duration: () => 100_000,
        seek: (seconds: number) => {
          seeks.push(seconds);
          position = seconds;
          busy = true;
        },
      };
      const controls = createControllerMapping(session, transport, 10, () => busy, {
        exit() {},
        togglePlay() {},
        zoom() {},
        recenter() {},
        seeked: (_step, target) => {
          if (target != null) targets.push(target);
        },
      });
      for (now = 0; now <= 7000; now += 100) {
        // The element takes half a second over each seek.
        if (busy && now % 500 === 0) busy = false;
        controls.poll();
      }
      // Steps of 10 s at first, 120 s by the end.
      expect(targets[1] - targets[0]).toBe(10);
      expect(targets[targets.length - 1] - targets[targets.length - 2]).toBe(120);
      // The element was asked far less often than the playhead moved.
      expect(seeks.length).toBeLessThan(targets.length);
      // Releasing lands on the final position.
      gamepad.axes[2] = 0;
      now += 100;
      controls.poll();
      expect(seeks[seeks.length - 1]).toBe(targets[targets.length - 1]);
    } finally {
      clock.mockRestore();
    }
  });

  it("places the timeline ahead of and below the viewer's gaze", () => {
    // Identity view: eye at the origin looking down -z.
    const ahead = placeHud({ transform: { inverse: { matrix: identity } } });
    expect(ahead.center[0]).toBeCloseTo(0);
    expect(ahead.center[1]).toBeCloseTo(-0.5);
    expect(ahead.center[2]).toBeCloseTo(-1.6);
    expect(ahead.right[0]).toBeCloseTo(1);
    expect(ahead.right[2]).toBeCloseTo(0);

    // Looking 90° to the right (+x): the strip sits along +x and its right axis points to +z.
    const turned = placeHud({
      transform: { inverse: { matrix: new Float32Array([0, 0, -1, 0, 0, 1, 0, 0, 1, 0, 0, 0, 0, 0, 0, 1]) } },
    });
    expect(turned.center[0]).toBeCloseTo(1.6);
    expect(turned.center[2]).toBeCloseTo(0);
    expect(turned.right[2]).toBeCloseTo(1);

    // Pitched 30° down: the strip follows the gaze down and sits just below it.
    const c = Math.cos(Math.PI / 6);
    const s = Math.sin(Math.PI / 6);
    const down = placeHud({
      transform: { inverse: { matrix: new Float32Array([1, 0, 0, 0, 0, c, s, 0, 0, -s, c, 0, 0, 0, 0, 1]) } },
    });
    // Gaze is (0, -s, -c); "up" in the gaze frame is (0, c, -s).
    expect(down.center[1]).toBeCloseTo(-s * 1.6 - c * 0.5);
    expect(down.center[2]).toBeCloseTo(-c * 1.6 + s * 0.5);
    expect(down.up[1]).toBeCloseTo(c);
  });

  it("builds the HTTPS address of the current page", () => {
    const location = { hostname: "192.168.1.20", pathname: "/videos/5", search: "?t=1", hash: "" };
    expect(secureUrlFor(location, 5443)).toBe("https://192.168.1.20:5443/videos/5?t=1");
  });
});

describe("choosing how a video is drawn in a session it owns", () => {
  // Enough of WebGL 2 for the renderer to set itself up: every call succeeds and returns a handle.
  const fakeGl = new Proxy({} as Record<string | symbol, unknown>, {
    get: (target, key) => {
      if (key in target) return target[key];
      if (key === "isContextLost") return () => false;
      if (key === "getShaderParameter" || key === "getProgramParameter") return () => true;
      if (typeof key === "string" && /^[A-Z0-9_]+$/.test(key)) return 1;
      return () => ({});
    },
  });

  function fakeSession() {
    const renderStates: Record<string, unknown>[] = [];
    const session = Object.assign(new EventTarget(), {
      enabledFeatures: ["layers"],
      inputSources: [],
      renderState: { baseLayer: null, layers: [] },
      requestReferenceSpace: async () => ({}),
      updateRenderState: (state: Record<string, unknown>) => void renderStates.push(state),
      requestAnimationFrame: () => 0,
      end: async () => {},
    });
    return { session: session as unknown as XRSessionLike, renderStates };
  }

  const video = {
    style: { visibility: "" },
    play: () => Promise.resolve(),
    paused: true,
  } as unknown as HTMLVideoElement;
  const equirect: VrDescriptor = { projection: "equirectangular", fieldOfView: 180, stereoMode: "sideBySide" };
  const projectionLayer = { kind: "projection" };
  const mediaLayer = { kind: "equirect" };

  beforeEach(() => {
    vi.spyOn(HTMLCanvasElement.prototype, "getContext").mockReturnValue(fakeGl as never);
    vi.stubGlobal(
      "XRWebGLBinding",
      class {
        createProjectionLayer() {
          return projectionLayer;
        }
        getViewSubImage() {
          return { colorTexture: {}, viewport: { x: 0, y: 0, width: 1, height: 1 } };
        }
      },
    );
    vi.stubGlobal("XRWebGLLayer", class {});
    vi.stubGlobal(
      "XRMediaBinding",
      class {
        createEquirectLayer() {
          return mediaLayer;
        }
      },
    );
  });
  afterEach(() => {
    vi.unstubAllGlobals();
    vi.restoreAllMocks();
  });

  it("draws into a projection layer, with zoom and the timeline, even where media layers exist", async () => {
    const { session, renderStates } = fakeSession();

    const playback = await playVideoInSession(session, video, equirect);

    expect(playback.mode).toBe("webgl");
    expect(renderStates[0]).toEqual({ layers: [projectionLayer] });
    playback.stop();
  });

  it("hands the video to the compositor only when asked to", async () => {
    const { session, renderStates } = fakeSession();

    const playback = await playVideoInSession(session, video, equirect, { preferMediaLayer: true });

    expect(playback.mode).toBe("media-layer");
    expect(renderStates[0]).toEqual({ layers: [mediaLayer] });
    playback.stop();
  });
});
