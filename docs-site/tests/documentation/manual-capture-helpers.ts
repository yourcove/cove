import { mkdir } from "node:fs/promises";
import { fileURLToPath } from "node:url";
import path from "node:path";
import { expect, type Cookie, type Locator, type Page } from "@playwright/test";
import sharp from "sharp";
import {
  openAuthenticatedPage,
  prepareDefaultAppearance,
  waitForVisibleImages,
} from "./capture-helpers";

type ManualCalloutTone =
  | "green"
  | "blue"
  | "purple"
  | "orange"
  | "pink"
  | "teal";

export interface ManualCallout {
  label: string;
  tone: ManualCalloutTone;
  targets: Locator | Locator[];
  padding?: number;
  labelPlacement?: "above" | "below";
  labelAlign?: "left" | "right";
}

export interface ManualVisualOverrides {
  checkboxValues?: { target: Locator; checked: boolean }[];
  inputValues?: { target: Locator; value: string }[];
  textValues?: { target: Locator; value: string }[];
}

interface ResolvedManualCallout {
  label: string;
  tone: ManualCalloutTone;
  top: number;
  left: number;
  width: number;
  height: number;
  labelPlacement?: "above" | "below";
  labelAlign?: "left" | "right";
}

const docsSiteRoot = path.resolve(
  path.dirname(fileURLToPath(import.meta.url)),
  "../..",
);
const manualScreenshotDirectory = path.resolve(
  docsSiteRoot,
  "../ui/public/manual/screenshots",
);
let authenticationCookies: Cookie[] | undefined;

const calloutColors: Record<ManualCalloutTone, string> = {
  green: "#22c55e",
  blue: "#3b82f6",
  purple: "#a855f7",
  orange: "#f97316",
  pink: "#ec4899",
  teal: "#14b8a6",
};

export async function prepareManualCapture(page: Page) {
  await page.setViewportSize({ width: 1700, height: 2000 });
  await page.addStyleTag({
    content:
      '*, *::before, *::after { animation: none !important; caret-color: transparent !important; scroll-behavior: auto !important; transition: none !important; } [aria-label^="Page Visits"] { display: none !important; }',
  });
  await waitForVisibleImages(page);
}

export async function openManualCapturePage(
  page: Page,
  pagePath: string,
  ready: Locator,
) {
  if (authenticationCookies) {
    await page.context().addCookies(authenticationCookies);
    await page.goto(pagePath);
    await expect(ready).toBeVisible();
  } else {
    await openAuthenticatedPage(page, pagePath, ready);
    authenticationCookies = await page.context().cookies();
  }
  await prepareDefaultAppearance(page);
  await prepareManualCapture(page);
}

export async function captureAnnotatedManualScreenshot(
  page: Page,
  name: string,
  callouts: ManualCallout[],
  visualOverrides: ManualVisualOverrides = {},
) {
  await page.evaluate(() => {
    const walker = document.createTreeWalker(
      document.body,
      NodeFilter.SHOW_TEXT,
    );
    for (let node = walker.nextNode(); node; node = walker.nextNode()) {
      const value = node.nodeValue;
      if (!value) continue;
      node.nodeValue = value
        .replace(
          /\bApplied \d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2} UTC\b/g,
          "Applied 2026-02-20 12:00:00 UTC",
        )
        .replace(/\b(Created|Updated) \d{4}-\d{2}-\d{2}\b/g, "$1 2026-02-20");
    }
  });
  await waitForVisibleImages(page);
  const resolved = await Promise.all(callouts.map(resolveCallout));

  await page.evaluate(
    ({ annotations, colors }) => {
      document
        .querySelectorAll("[data-manual-capture-callout]")
        .forEach((element) => element.remove());

      for (const annotation of annotations) {
        const color = colors[annotation.tone];
        const box = document.createElement("div");
        box.dataset.manualCaptureCallout = annotation.tone;
        Object.assign(box.style, {
          position: "fixed",
          top: `${annotation.top}px`,
          left: `${annotation.left}px`,
          width: `${annotation.width}px`,
          height: `${annotation.height}px`,
          boxSizing: "border-box",
          border: `3px solid ${color}`,
          borderRadius: "8px",
          pointerEvents: "none",
          zIndex: "2147483647",
        });

        const label = document.createElement("span");
        label.textContent = annotation.label;
        const placeBelow =
          annotation.labelPlacement === "below" ||
          (annotation.labelPlacement === undefined && annotation.top < 32);
        const alignRight = annotation.labelAlign === "right";
        Object.assign(label.style, {
          position: "absolute",
          top: placeBelow ? "100%" : "-27px",
          left: alignRight ? "auto" : "0",
          right: alignRight ? "0" : "auto",
          maxWidth: "max-content",
          padding: "3px 9px",
          borderRadius: placeBelow ? "0 0 6px 6px" : "6px 6px 0 0",
          background: color,
          color: "#ffffff",
          fontFamily: "Manrope, ui-sans-serif, system-ui, sans-serif",
          fontSize: "15px",
          fontWeight: "700",
          lineHeight: "21px",
          whiteSpace: "nowrap",
        });
        box.append(label);
        document.body.append(box);
      }
    },
    { annotations: resolved, colors: calloutColors },
  );
  await expect(page.locator("[data-manual-capture-callout]")).toHaveCount(
    callouts.length,
  );

  for (const override of visualOverrides.inputValues ?? []) {
    await override.target.evaluate((input: HTMLInputElement, value) => {
      input.value = value;
    }, override.value);
    await expect(override.target).toHaveValue(override.value);
  }
  for (const override of visualOverrides.checkboxValues ?? []) {
    await override.target.evaluate((checkbox: HTMLInputElement, checked) => {
      checkbox.checked = checked;
    }, override.checked);
    if (override.checked) await expect(override.target).toBeChecked();
    else await expect(override.target).not.toBeChecked();
  }
  for (const override of visualOverrides.textValues ?? []) {
    await override.target.evaluate((element, value) => {
      element.textContent = value;
    }, override.value);
    await expect(override.target).toHaveText(override.value);
  }

  await mkdir(manualScreenshotDirectory, { recursive: true });
  const outputPath = path.join(manualScreenshotDirectory, `${name}.png`);
  await page.screenshot({
    path: outputPath,
    animations: "disabled",
    caret: "hide",
  });
  await expect
    .poll(async () => sharp(outputPath).metadata())
    .toMatchObject({ width: 1700, height: 2000, format: "png" });
}

async function resolveCallout(
  callout: ManualCallout,
): Promise<ResolvedManualCallout> {
  const targets = Array.isArray(callout.targets)
    ? callout.targets
    : [callout.targets];
  const boxes = await Promise.all(
    targets.map(async (target) => {
      await expect(target).toBeVisible();
      const box = await target.boundingBox();
      if (!box)
        throw new Error(
          `Could not resolve the callout target for “${callout.label}”.`,
        );
      return box;
    }),
  );
  const padding = callout.padding ?? 6;
  const viewport = targets[0].page().viewportSize();
  const left = Math.max(0, Math.min(...boxes.map((box) => box.x)) - padding);
  const top = Math.max(0, Math.min(...boxes.map((box) => box.y)) - padding);
  const right = Math.min(
    viewport?.width ?? Number.POSITIVE_INFINITY,
    Math.max(...boxes.map((box) => box.x + box.width)) + padding,
  );
  const bottom = Math.min(
    viewport?.height ?? Number.POSITIVE_INFINITY,
    Math.max(...boxes.map((box) => box.y + box.height)) + padding,
  );

  return {
    label: callout.label,
    tone: callout.tone,
    left,
    top,
    width: right - left,
    height: bottom - top,
    labelPlacement: callout.labelPlacement,
    labelAlign: callout.labelAlign,
  };
}
