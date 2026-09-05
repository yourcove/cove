const COMPLETED_RESUME_TOLERANCE_SECONDS = 0.05;

export function normalizeStoredResumeTime(resumeTime: number | undefined, duration: number | undefined) {
  if (typeof resumeTime !== "number" || !Number.isFinite(resumeTime) || resumeTime <= 0) {
    return undefined;
  }

  if (
    typeof duration === "number" &&
    Number.isFinite(duration) &&
    duration > 0 &&
    resumeTime >= Math.max(0, duration - COMPLETED_RESUME_TOLERANCE_SECONDS)
  ) {
    return undefined;
  }

  return resumeTime;
}
