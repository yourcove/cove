import type { SegmentDisplayProfile } from "../../api/types";

export function dedupeSegmentDisplayProfiles(profiles: SegmentDisplayProfile[]): SegmentDisplayProfile[] {
  const byName = new Map<string, SegmentDisplayProfile>();
  for (const profile of profiles) {
    const key = profile.name.trim().toLocaleLowerCase();
    const current = byName.get(key);
    if (!current || isPreferredSegmentDisplayProfile(profile, current)) {
      byName.set(key, profile);
    }
  }

  return profiles.filter((profile) => byName.get(profile.name.trim().toLocaleLowerCase())?.id === profile.id);
}

function isPreferredSegmentDisplayProfile(candidate: SegmentDisplayProfile, current: SegmentDisplayProfile): boolean {
  if (candidate.userId != null && current.userId == null) return true;
  if (candidate.userId == null && current.userId != null) return false;
  if (candidate.isDefault !== current.isDefault) return candidate.isDefault;
  return candidate.id < current.id;
}
