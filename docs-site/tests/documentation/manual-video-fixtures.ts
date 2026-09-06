import { expect, type Page } from "@playwright/test";

export const manualVideoId = 25;

export async function arrangeManualVideoFixtures(page: Page) {
  const segmentsResponse = await page.request.get(
    `/api/videos/${manualVideoId}/segments`,
  );
  expect(segmentsResponse.ok()).toBeTruthy();
  const segments = (await segmentsResponse.json()) as Array<{
    id: number;
    title: string;
    sourceKey?: string;
  }>;
  for (const desired of [
    { title: "The briefing", startSec: 2, endSec: 6 },
    { title: "The handoff", startSec: 9, endSec: 13 },
  ]) {
    const existing = segments.find(
      (segment) =>
        segment.title === desired.title &&
        segment.sourceKey === "documentation-capture",
    );
    const response = existing
      ? await page.request.put(
          `/api/videos/${manualVideoId}/segments/${existing.id}`,
          {
            data: {
              ...desired,
              kind: "chapter",
              sourceKey: "documentation-capture",
            },
          },
        )
      : await page.request.post(`/api/videos/${manualVideoId}/segments`, {
          data: {
            ...desired,
            kind: "chapter",
            sourceKey: "documentation-capture",
          },
        });
    expect(response.ok()).toBeTruthy();
  }
  const applicationsResponse = await page.request.get(
    `/api/tagapplications?hostType=video&hostId=${manualVideoId}&contextType=performer&contextId=21`,
  );
  expect(applicationsResponse.ok()).toBeTruthy();
  const applications = (await applicationsResponse.json()) as Array<{
    tag: { id: number };
  }>;
  if (!applications.some((application) => application.tag.id === 18))
    expect(
      (
        await page.request.post("/api/tagapplications", {
          data: {
            hostType: "video",
            hostId: manualVideoId,
            contextType: "performer",
            contextId: 21,
            tagId: 18,
            sourceKey: "documentation-capture",
          },
        })
      ).ok(),
    ).toBeTruthy();

  const profiles = (await (
    await page.request.get("/api/segment-display-profiles")
  ).json()) as Array<{ id: number; name: string }>;
  let profile = profiles.find(
    (candidate) => candidate.name === "Documentation capture",
  );
  if (!profile) {
    const created = await page.request.post("/api/segment-display-profiles", {
      data: {
        name: "Documentation capture",
        description: "Stable manual screenshot profile",
        isDefault: false,
      },
    });
    expect(created.ok()).toBeTruthy();
    profile = await created.json();
  }
  const rules = (await (
    await page.request.get(`/api/segment-display-profiles/${profile!.id}/rules`)
  ).json()) as Array<{ sourceKey?: string }>;
  if (!rules.some((rule) => rule.sourceKey === "documentation-capture"))
    expect(
      (
        await page.request.post(
          `/api/segment-display-profiles/${profile!.id}/rules`,
          {
            data: {
              sourceKey: "documentation-capture",
              kind: "chapter",
              hostType: "video",
              visible: true,
              mergeGapSec: 0,
              collapseToInstant: false,
              colorOverride: "#3b82f6",
              lane: 0,
              priority: 10,
            },
          },
        )
      ).ok(),
    ).toBeTruthy();

  await arrangeManualFaceFixtures(page);
  const detections = (await (
    await page.request.get(`/api/videos/${manualVideoId}/detections`)
  ).json()) as Array<{ sourceKey?: string; observedAtSec: number }>;
  if (
    !detections.some(
      (detection) =>
        detection.sourceKey === "documentation-capture-manual" &&
        detection.observedAtSec === 8,
    )
  )
    expect(
      (
        await page.request.post(`/api/videos/${manualVideoId}/detections`, {
          data: {
            observedAtSec: 8,
            class: "object",
            frameWidth: 1280,
            frameHeight: 720,
            score: 0.98,
            x: 0.2,
            y: 0.2,
            w: 0.25,
            h: 0.5,
            sourceKey: "documentation-capture-manual",
          },
        })
      ).ok(),
    ).toBeTruthy();
}

export async function arrangeManualFaceFixtures(page: Page) {
  const facesPayload = (await (
    await page.request.get("/api/faces?page=1&perPage=100")
  ).json()) as
    | { items?: Array<{ id: number; label?: string }> }
    | Array<{ id: number; label?: string }>;
  const faces = Array.isArray(facesPayload)
    ? facesPayload
    : (facesPayload.items ?? []);
  let face = faces.find((candidate) => candidate.label === "Lucia timeline");
  if (!face) {
    const created = await page.request.post("/api/faces", {
      data: {
        label: "Lucia timeline",
        performerId: 21,
        ignored: false,
        primarySourceKey: "documentation-capture-manual",
      },
    });
    expect(created.ok()).toBeTruthy();
    face = await created.json();
  }
  const detections = (await (
    await page.request.get(`/api/videos/${manualVideoId}/detections`)
  ).json()) as Array<{ sourceKey?: string; observedAtSec: number }>;
  for (const desired of [
    {
      observedAtSec: 3,
      class: "person",
      refKind: "face",
      refId: face!.id,
      groupKey: "documentation-capture-manual:lucia",
    },
    {
      observedAtSec: 5,
      class: "person",
      refKind: "face",
      refId: face!.id,
      groupKey: "documentation-capture-manual:lucia",
    },
  ]) {
    if (
      !detections.some(
        (detection) =>
          detection.sourceKey === "documentation-capture-manual" &&
          detection.observedAtSec === desired.observedAtSec,
      )
    )
      expect(
        (
          await page.request.post(`/api/videos/${manualVideoId}/detections`, {
            data: {
              ...desired,
              frameWidth: 1280,
              frameHeight: 720,
              score: 0.98,
              x: 0.2,
              y: 0.2,
              w: 0.25,
              h: 0.5,
              sourceKey: "documentation-capture-manual",
            },
          })
        ).ok(),
      ).toBeTruthy();
  }
}
