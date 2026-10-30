import { basename, resolve } from "node:path";
import { stat } from "node:fs/promises";
import type { CoveClient } from "./client";
import { CliError } from "./errors";
import type { Video } from "./types";

/** Registering a file probes and fingerprints it on the server, which can outlast ordinary requests. */
const IMPORT_TIMEOUT_MS = 300_000;

/** Resources with an uploadable cover or profile image, all served at `<resource>/<id>/image`. */
export const IMAGE_RESOURCES = ["videos", "performers", "studios", "tags", "galleries", "audios", "texts"] as const;

/** Detects the image type from the file's leading bytes, because Cove only accepts `image/*` uploads. */
export function imageContentType(bytes: Uint8Array): string | undefined {
  const ascii = (start: number, end: number) => String.fromCharCode(...bytes.subarray(start, end));
  if (bytes[0] === 0xff && bytes[1] === 0xd8 && bytes[2] === 0xff) return "image/jpeg";
  if (ascii(0, 8) === "\x89PNG\r\n\x1a\n") return "image/png";
  if (ascii(0, 6) === "GIF87a" || ascii(0, 6) === "GIF89a") return "image/gif";
  if (ascii(0, 4) === "RIFF" && ascii(8, 12) === "WEBP") return "image/webp";
  if (ascii(4, 8) === "ftyp" && new Set(["avif", "avis"]).has(ascii(8, 12))) return "image/avif";
  if (ascii(0, 2) === "BM") return "image/bmp";
  return undefined;
}

async function localFile(path: string): Promise<{ path: string; file: ReturnType<typeof Bun.file> }> {
  const absolute = resolve(path);
  let isFile = false;
  try {
    isFile = (await stat(absolute)).isFile();
  } catch {
    // Reported below as a missing file.
  }
  if (!isFile) throw new CliError("INVALID_ARGUMENT", `No file exists at “${path}”.`);
  return { path: absolute, file: Bun.file(absolute) };
}

/** Builds a one-file multipart form. Images get their detected type; anything else keeps the type its extension implies. */
export async function fileForm(path: string, field = "file", requireImage = false): Promise<{ form: FormData; contentType: string }> {
  const local = await localFile(path);
  const detected = imageContentType(new Uint8Array(await local.file.slice(0, 16).arrayBuffer()));
  if (requireImage && !detected) throw new CliError("INVALID_ARGUMENT", `“${path}” is not a JPEG, PNG, GIF, WebP, AVIF, or BMP image.`);
  const blob = detected ? Bun.file(local.path, { type: detected }) : local.file;
  const form = new FormData();
  form.append(field, blob, basename(local.path));
  return { form, contentType: blob.type || "application/octet-stream" };
}

export function assertImageResource(resource: string): void {
  if (!(IMAGE_RESOURCES as readonly string[]).includes(resource)) {
    throw new CliError("INVALID_ARGUMENT", `${resource} do not have an uploadable image. Supported resources: ${IMAGE_RESOURCES.join(", ")}.`);
  }
}

export async function uploadImage(client: CoveClient, resource: string, id: number, upload: { form: FormData; contentType: string }, path: string): Promise<{ resource: string; id: number; file: string; contentType: string; blobId?: string }> {
  const { form, contentType } = upload;
  const result = await client.request<unknown>(`${resource}/${id}/image`, { method: "POST", body: form });
  const blobId = result && typeof result === "object" ? (result as Record<string, unknown>).blobId : undefined;
  return { resource, id, file: path, contentType, ...(typeof blobId === "string" ? { blobId } : {}) };
}

/** True for a POSIX path from the root, a Windows drive path, or a UNC path. */
export function isServerAbsolutePath(path: string): boolean {
  return path.startsWith("/") || /^[A-Za-z]:[\\/]/.test(path) || path.startsWith("\\\\");
}

/**
 * Registers files that already exist on the server as videos, in order, stopping at the first failure.
 * Paths are read by the server, so they are sent as given rather than resolved locally.
 */
export function assertServerPaths(paths: string[]): void {
  for (const path of paths) {
    if (!isServerAbsolutePath(path)) throw new CliError("INVALID_ARGUMENT", `“${path}” is not an absolute path. Give the path as the Cove server sees it.`);
  }
}

export async function importVideoFiles(client: CoveClient, paths: string[], validate: (value: unknown) => value is Video): Promise<Video[]> {
  assertServerPaths(paths);
  const imported: Video[] = [];
  for (const path of paths) {
    try {
      const video = await client.request<unknown>("videos/from-file", { method: "POST", body: JSON.stringify({ filePath: path }) }, true, IMPORT_TIMEOUT_MS);
      if (!validate(video)) throw new CliError("INVALID_RESPONSE", "Cove returned an invalid video object.");
      imported.push(video);
    } catch (error) {
      if (!(error instanceof CliError)) throw error;
      const done = imported.length ? ` Already imported: ${imported.map(video => video.id).join(", ")}.` : "";
      throw new CliError(error.code, `${path}: ${error.message}${done}`, {
        status: error.status,
        details: { imported: imported.map((video, index) => ({ path: paths[index], id: video.id })), failedPath: path, failure: error.details ?? null },
      });
    }
  }
  return imported;
}
