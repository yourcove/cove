import { authStore } from "../../../auth/authStore";
import type { SegmentDisplayRuleCreate } from "../../../api/types";
import { serverAwareFetch } from "../../../state/serverAvailability";

export async function bulkCreateDisplayProfileRules(profileId: number, data: SegmentDisplayRuleCreate[]) {
  const headers = new Headers({
    "Content-Type": "application/json",
  });
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

  const response = await serverAwareFetch(`/api/segment-display-profiles/${profileId}/rules/bulk`, {
    method: "POST",
    headers,
    body: JSON.stringify(data),
  });

  if (response.status === 401) {
    window.dispatchEvent(new CustomEvent("cove-auth-required"));
  }

  if (!response.ok) {
    throw new Error(`API Error ${response.status}: ${await response.text()}`);
  }
}
