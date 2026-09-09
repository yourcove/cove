import { fileURLToPath } from "node:url";
import path from "node:path";
import { expect, type Page } from "@playwright/test";

export const heroFaceNames = [
  "Lucia Ferrer",
  "Darius King",
  "Elias Grant",
] as const;
export const heroFaceLabels = heroFaceNames.map(
  (name) => `Hero portraits: ${name}`,
);

const fixtureDirectory = fileURLToPath(
  new URL("./fixtures/face-headshots", import.meta.url),
);
const sourceKey = "documentation-capture-hero-portraits";

const heroes = [
  { name: "Lucia Ferrer", slug: "lucia-ferrer" },
  { name: "Darius King", slug: "darius-king" },
  { name: "Elias Grant", slug: "elias-grant" },
] as const;

type PerformerSummary = { id: number; name: string };
type FaceSummary = {
  id: number;
  label?: string;
  performerId?: number;
  ignored: boolean;
  primarySourceKey?: string;
};
type ImageSummary = {
  id: number;
  title?: string;
  files?: { path: string; width: number; height: number; size: number }[];
};

export async function arrangeHeroFaceFixtures(page: Page) {
  const performersResponse = await page.request.get("/api/performers", {
    params: { page: 1, perPage: 1000 },
  });
  expect(performersResponse.ok()).toBe(true);
  const performersPayload = (await performersResponse.json()) as {
    items: PerformerSummary[];
  };

  const facesResponse = await page.request.get("/api/faces", {
    params: { page: 1, perPage: 1000 },
  });
  expect(facesResponse.ok()).toBe(true);
  const facesPayload = (await facesResponse.json()) as
    | { items?: FaceSummary[] }
    | FaceSummary[];
  const faces = Array.isArray(facesPayload)
    ? facesPayload
    : (facesPayload.items ?? []);

  const imagesResponse = await page.request.get("/api/images", {
    params: { page: 1, perPage: 1000 },
  });
  expect(imagesResponse.ok()).toBe(true);
  const imagesPayload = (await imagesResponse.json()) as {
    items: ImageSummary[];
  };
  const images = imagesPayload.items;

  for (const hero of heroes) {
    const performer = performersPayload.items.find(
      (candidate) => candidate.name === hero.name,
    );
    expect(performer, `Missing demo performer ${hero.name}`).toBeTruthy();

    const label = `Hero portraits: ${hero.name}`;
    let face = faces.find((candidate) => candidate.label === label);
    if (!face) {
      const response = await page.request.post("/api/faces", {
        data: {
          label,
          performerId: performer!.id,
          ignored: false,
          primarySourceKey: sourceKey,
        },
      });
      expect(response.ok()).toBe(true);
      face = (await response.json()) as FaceSummary;
      faces.push(face);
    } else {
      const response = await page.request.put(`/api/faces/${face.id}`, {
        data: {
          label,
          performerId: performer!.id,
          ignored: false,
          primarySourceKey: sourceKey,
        },
      });
      expect(response.ok()).toBe(true);
    }

    for (let index = 1; index <= 3; index += 1) {
      const suffix = String(index).padStart(2, "0");
      const title = `${hero.name} recognition sample ${index}`;
      const filePath = path.join(
        fixtureDirectory,
        `${hero.slug}-${suffix}.jpg`,
      );
      const candidates = images.filter(
        (candidate) =>
          candidate.title === title ||
          candidate.files?.some((file) => file.path === filePath),
      );
      for (const duplicate of candidates) {
        const deleteResponse = await page.request.delete(
          `/api/images/${duplicate.id}`,
          { params: { deleteGenerated: true } },
        );
        expect(deleteResponse.ok()).toBe(true);
        images.splice(
          images.findIndex((candidate) => candidate.id === duplicate.id),
          1,
        );
      }

      const response = await page.request.post("/api/images/from-file", {
        data: { filePath },
      });
      expect(response.ok()).toBe(true);
      const image = (await response.json()) as ImageSummary;
      images.push(image);
      expect(image.files).toContainEqual(
        expect.objectContaining({ path: filePath, width: 640, height: 640 }),
      );
      expect(
        image.files?.find((file) => file.path === filePath)?.size,
      ).toBeGreaterThan(0);

      const updateResponse = await page.request.put(`/api/images/${image.id}`, {
        data: {
          title,
          date: `2026-08-${String(20 + index).padStart(2, "0")}`,
          details:
            "A deterministic portrait sample used to demonstrate grouped face recognition.",
          organized: true,
          performerIds: [performer!.id],
        },
      });
      expect(updateResponse.ok()).toBe(true);

      const detectionSourceKey = `${sourceKey}:${hero.slug}:${suffix}`;
      const detectionsResponse = await page.request.get(
        `/api/images/${image.id}/detections`,
      );
      expect(detectionsResponse.ok()).toBe(true);
      const detections = (await detectionsResponse.json()) as {
        id: number;
        sourceKey?: string;
      }[];
      const existing = detections.find(
        (detection) => detection.sourceKey === detectionSourceKey,
      );
      const detection = {
        frameWidth: 640,
        frameHeight: 640,
        class: "person",
        score: 0.99,
        x: 0.12,
        y: 0.03,
        w: 0.76,
        h: 0.8,
        refKind: "face",
        refId: face!.id,
        groupKey: `${sourceKey}:${hero.slug}`,
        sourceKey: detectionSourceKey,
      };
      const detectionResponse = existing
        ? await page.request.put(
            `/api/images/${image.id}/detections/${existing.id}`,
            { data: detection },
          )
        : await page.request.post(`/api/images/${image.id}/detections`, {
            data: detection,
          });
      expect(detectionResponse.ok()).toBe(true);
    }
  }
}
