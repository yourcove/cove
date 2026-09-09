import { copyFile, mkdir } from "node:fs/promises";
import { fileURLToPath } from "node:url";
import path from "node:path";
import { expect, type Page } from "@playwright/test";

export const verticalVideoTitle = "Night Courier";

const verticalFixturePath = fileURLToPath(
  new URL("./fixtures/vertical-courier.mp4", import.meta.url),
);

export async function arrangeVerticalVideo(page: Page) {
  const libraryRoot = process.env.COVE_DEV_DEMO_LIBRARY;
  if (!libraryRoot)
    throw new Error(
      "COVE_DEV_DEMO_LIBRARY must be set to arrange the vertical-view documentation fixture.",
    );

  const destinationDirectory = path.join(libraryRoot, "documentation-capture");
  const destinationPath = path.join(destinationDirectory, "vertical-courier.mp4");
  await mkdir(destinationDirectory, { recursive: true });
  await copyFile(verticalFixturePath, destinationPath);

  const listResponse = await page.request.get("/api/videos", {
    params: { page: 1, perPage: 1000 },
  });
  expect(listResponse.ok()).toBe(true);
  const list = (await listResponse.json()) as {
    items: { id: number; files: { path: string }[] }[];
  };
  let video = list.items.find((item) =>
    item.files.some(
      (file) => path.resolve(file.path) === path.resolve(destinationPath),
    ),
  );

  if (!video) {
    const createResponse = await page.request.post("/api/videos/from-file", {
      data: { filePath: destinationPath },
    });
    expect(createResponse.ok()).toBe(true);
    video = (await createResponse.json()) as {
      id: number;
      files: { path: string }[];
    };
  }

  const updateResponse = await page.request.put(`/api/videos/${video.id}`, {
    data: {
      title: verticalVideoTitle,
      date: "2026-08-30",
      details:
        "A courier races through a rain-lit city with one last delivery before midnight.",
      organized: true,
    },
  });
  expect(updateResponse.ok()).toBe(true);

  const coverResponse = await page.request.post(
    `/api/videos/${video.id}/cover/from-frame`,
    { data: { atSeconds: 2 } },
  );
  expect(coverResponse.ok()).toBe(true);
}
