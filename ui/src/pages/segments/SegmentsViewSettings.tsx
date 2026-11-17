import { useId, useRef, useState } from "react";
import { Settings } from "lucide-react";
import type { SegmentDisplayProfile } from "../../api/types";
import { FloatingActionMenu } from "../../components/FloatingActionMenu";

interface Props {
  profiles: SegmentDisplayProfile[];
  activeProfileId?: number;
  onProfileChange: (profileId: number) => void;
  /** The raw view lists segments as observed, so no profile applies to it. */
  isRawView: boolean;
}

/**
 * The Segments page's rarely changed view settings, behind a gear so they do not crowd the toolbar. A dot
 * on the gear says a profile other than the default is in use, since the list then differs from the usual.
 */
export function SegmentsViewSettings({ profiles, activeProfileId, onProfileChange, isRawView }: Props) {
  const anchorRef = useRef<HTMLDivElement>(null);
  const [open, setOpen] = useState(false);
  const selectId = useId();
  const activeProfile = profiles.find((profile) => profile.id === activeProfileId);
  const nonDefaultProfile = !isRawView && activeProfile != null && !activeProfile.isDefault;

  return (
    <div className="relative" ref={anchorRef}>
      <button
        type="button"
        onClick={() => setOpen((current) => !current)}
        aria-label="View settings"
        aria-haspopup="dialog"
        aria-expanded={open}
        title={nonDefaultProfile ? `View settings: ${activeProfile.name} profile` : "View settings"}
        className="relative inline-flex items-center rounded-lg border border-border bg-card/70 p-2 text-secondary transition-colors hover:border-accent hover:text-foreground"
      >
        <Settings className="h-4 w-4" aria-hidden="true" />
        {nonDefaultProfile ? (
          <span aria-hidden="true" className="absolute right-1 top-1 h-1.5 w-1.5 rounded-full bg-accent" />
        ) : null}
      </button>
      <FloatingActionMenu
        open={open}
        anchorRef={anchorRef}
        onClose={() => setOpen(false)}
        role="dialog"
        className="w-72 p-3"
      >
        <label htmlFor={selectId} className="block text-xs font-semibold uppercase tracking-wide text-muted">
          Display profile
        </label>
        <select
          id={selectId}
          value={activeProfileId ?? ""}
          onChange={(event) => onProfileChange(Number(event.target.value))}
          disabled={isRawView || profiles.length === 0}
          className="mt-1.5 w-full rounded-md border border-border bg-card px-2 py-1.5 text-sm text-foreground focus:border-accent focus:outline-none disabled:cursor-not-allowed disabled:opacity-60"
        >
          {profiles.map((profile) => (
            <option key={profile.id} value={profile.id}>
              {profile.name}
              {profile.isDefault ? " (Default)" : ""}
            </option>
          ))}
        </select>
        <p className="mt-2 text-xs text-muted">
          {isRawView
            ? "Raw view shows segments as observed; profiles and derived queries do not apply."
            : "Decides how raw segments combine into the segments listed here."}
        </p>
      </FloatingActionMenu>
    </div>
  );
}
