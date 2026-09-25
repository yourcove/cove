import { formatVrLayout, type VrDescriptor, type VrProjection, type VrStereoMode } from "../vr/immersiveVideo";

const PROJECTIONS: { value: VrProjection; label: string; defaultFov: number }[] = [
  { value: "equirectangular", label: "Equirectangular", defaultFov: 180 },
  { value: "fisheye", label: "Fisheye", defaultFov: 190 },
  { value: "mkx200", label: "MKX200", defaultFov: 200 },
];

const STEREO_MODES: { value: VrStereoMode; label: string }[] = [
  { value: "sideBySide", label: "Side by side" },
  { value: "topBottom", label: "Top / bottom" },
  { value: "mono", label: "Mono" },
];

/** The layout a video would play with when no explicit one is stored: what the server detected. */
export function detectedVrLayout(video: { vr?: VrDescriptor | null }): VrDescriptor | null {
  return video.vr?.inferred ? video.vr : null;
}

/** The explicit layout stored on the video, or null when it follows detection. */
export function explicitVrLayout(video: { vr?: VrDescriptor | null }): VrDescriptor | null {
  return video.vr && !video.vr.inferred ? video.vr : null;
}

/**
 * Editor for a VR video's layout: projection, field of view and stereo packing. `value` null means
 * "follow detection"; the detected layout is shown so the choice is informed. Only rendered for videos
 * marked VR, so it costs no space otherwise.
 */
export function VrLayoutFields({
  value,
  detected,
  onChange,
  inputClassName,
  compact = false,
}: {
  value: VrDescriptor | null;
  detected: VrDescriptor | null;
  onChange: (value: VrDescriptor | null) => void;
  inputClassName: string;
  compact?: boolean;
}) {
  const mode = value ? "custom" : "auto";
  const current: VrDescriptor = value ?? detected ?? { projection: "equirectangular", fieldOfView: 180, stereoMode: "sideBySide" };

  const update = (patch: Partial<VrDescriptor>) => {
    const { inferred: _inferred, ...explicit } = current;
    onChange({ ...explicit, ...patch });
  };

  return (
    <div className={compact ? "space-y-2" : "space-y-3"}>
      <label className="block space-y-1">
        <span className="text-xs text-secondary">VR layout</span>
        <select
          value={mode}
          onChange={(event) => {
            if (event.target.value === "auto") onChange(null);
            else update({});
          }}
          className={inputClassName}
        >
          <option value="auto">{detected ? `Auto (detected ${formatVrLayout(detected)})` : "Auto (detect from file)"}</option>
          <option value="custom">Set manually</option>
        </select>
      </label>
      {mode === "custom" ? (
        <div className={`grid gap-2 ${compact ? "grid-cols-3" : "grid-cols-1 sm:grid-cols-3"}`}>
          <label className="block space-y-1">
            <span className="text-xs text-secondary">Projection</span>
            <select
              value={current.projection}
              onChange={(event) => {
                const projection = event.target.value as VrProjection;
                const preset = PROJECTIONS.find((candidate) => candidate.value === projection);
                const keepFov = projection === "equirectangular" && (current.fieldOfView === 180 || current.fieldOfView === 360);
                update({ projection, fieldOfView: keepFov ? current.fieldOfView : (preset?.defaultFov ?? 180) });
              }}
              className={inputClassName}
            >
              {PROJECTIONS.map((projection) => (
                <option key={projection.value} value={projection.value}>
                  {projection.label}
                </option>
              ))}
            </select>
          </label>
          <label className="block space-y-1">
            <span className="text-xs text-secondary">Field of view (°)</span>
            {current.projection === "equirectangular" ? (
              <select
                value={current.fieldOfView === 360 ? "360" : "180"}
                onChange={(event) => update({ fieldOfView: Number(event.target.value) })}
                className={inputClassName}
              >
                <option value="180">180</option>
                <option value="360">360</option>
              </select>
            ) : (
              <input
                type="number"
                min={120}
                max={360}
                step={1}
                value={current.fieldOfView}
                onChange={(event) => {
                  const fov = Number(event.target.value);
                  if (Number.isFinite(fov) && fov > 0) update({ fieldOfView: Math.round(fov) });
                }}
                className={inputClassName}
              />
            )}
          </label>
          <label className="block space-y-1">
            <span className="text-xs text-secondary">Stereo</span>
            <select
              value={current.stereoMode}
              onChange={(event) => update({ stereoMode: event.target.value as VrStereoMode })}
              className={inputClassName}
            >
              {STEREO_MODES.map((stereo) => (
                <option key={stereo.value} value={stereo.value}>
                  {stereo.label}
                </option>
              ))}
            </select>
          </label>
        </div>
      ) : null}
    </div>
  );
}
