import { describe, expect, it } from "vitest";
import { sharedModuleSpecifiers } from "../generated/extensions/runtime/v1/contract";
import { canUseMediaLayer, equirectLayerInit, inverseViewProjection, type VrDescriptor } from "../vr/immersiveVideo";
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

  it("builds the HTTPS address of the current page", () => {
    const location = { hostname: "192.168.1.20", pathname: "/videos/5", search: "?t=1", hash: "" };
    expect(secureUrlFor(location, 5443)).toBe("https://192.168.1.20:5443/videos/5?t=1");
  });
});
