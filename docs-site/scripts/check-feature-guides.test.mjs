import assert from "node:assert/strict";
import { readFile, readdir, stat } from "node:fs/promises";
import path from "node:path";
import test from "node:test";
import { fileURLToPath } from "node:url";
import Ajv2020 from "ajv/dist/2020.js";

const siteRoot = path.resolve(
  path.dirname(fileURLToPath(import.meta.url)),
  "..",
);
const repositoryRoot = path.resolve(siteRoot, "..");
const guidesRoot = path.join(repositoryRoot, "docs", "feature-guides");
const assetsRoot = path.join(guidesRoot, "assets");
const schema = JSON.parse(
  await readFile(path.join(guidesRoot, "schema.json"), "utf8"),
);
const validateGuide = new Ajv2020({ allErrors: true }).compile(schema);

function assertNonEmptyString(value, label) {
  assert.equal(typeof value, "string", `${label} must be a string`);
  assert.notEqual(value.trim(), "", `${label} must not be empty`);
}

function riffChunkTypes(image) {
  const chunks = [];
  for (let offset = 12; offset + 8 <= image.length;) {
    const type = image.toString("ascii", offset, offset + 4);
    const size = image.readUInt32LE(offset + 4);
    chunks.push(type);
    offset += 8 + size + (size % 2);
  }
  return chunks;
}

async function findFiles(directory, extension) {
  const entries = await readdir(directory, { withFileTypes: true });
  const files = await Promise.all(
    entries.map(async (entry) => {
      const entryPath = path.join(directory, entry.name);
      return entry.isDirectory()
        ? findFiles(entryPath, extension)
        : entry.name.endsWith(extension)
          ? [entryPath]
          : [];
    }),
  );
  return files.flat();
}

function* walkGuideBlocks(blocks) {
  for (const block of blocks) {
    yield block;
    if (block.type === "recipes") {
      for (const recipe of block.items) yield* walkGuideBlocks(recipe.blocks);
    }
  }
}

test("every shared feature guide follows the schema and references a bundled image", async () => {
  const guideFiles = (await readdir(guidesRoot)).filter(
    (name) => name.endsWith(".json") && name !== "schema.json",
  );
  assert.ok(
    guideFiles.length > 0,
    "at least one shared feature guide must exist",
  );

  for (const guideFile of guideFiles) {
    const guide = JSON.parse(
      await readFile(path.join(guidesRoot, guideFile), "utf8"),
    );
    assert.ok(
      validateGuide(guide),
      `${guideFile} does not match schema.json: ${JSON.stringify(validateGuide.errors)}`,
    );

    const sectionIds = new Set();
    let imageCount = 0;
    for (const section of guide.sections) {
      assert.ok(
        !sectionIds.has(section.id),
        `${guideFile} section id ${section.id} must be unique`,
      );
      sectionIds.add(section.id);
      const recipeIds = new Set();
      for (const block of section.blocks) {
        if (block.type !== "recipes") continue;
        for (const recipe of block.items) {
          assert.ok(
            !recipeIds.has(recipe.id),
            `${guideFile} recipe id ${recipe.id} must be unique within its section`,
          );
          recipeIds.add(recipe.id);
        }
      }
      for (const [index, block] of [
        ...walkGuideBlocks(section.blocks),
      ].entries()) {
        if (block.type !== "image") continue;
        imageCount += 1;
        assert.match(
          block.src,
          /^assets\//,
          `${guideFile} image ${index} must be stored below the shared assets directory`,
        );
        assert.match(
          block.src,
          /\.(?:png|webp)$/i,
          `${guideFile} image ${index} must be a PNG or WebP screenshot`,
        );
        const imagePath = path.resolve(guidesRoot, block.src);
        const relativeImagePath = path.relative(assetsRoot, imagePath);
        assert.ok(
          relativeImagePath &&
            !relativeImagePath.startsWith(`..${path.sep}`) &&
            !path.isAbsolute(relativeImagePath),
          `${guideFile} image ${index} must remain below the shared assets directory`,
        );
        assert.ok(
          (await stat(imagePath)).isFile(),
          `${guideFile} image ${index} must reference a regular file`,
        );
        if (/\.webp$/i.test(block.src)) {
          const image = await readFile(imagePath);
          assert.equal(
            image.toString("ascii", 0, 4),
            "RIFF",
            `${guideFile} image ${index} must be a WebP file`,
          );
          assert.equal(
            image.toString("ascii", 8, 12),
            "WEBP",
            `${guideFile} image ${index} must be a WebP file`,
          );
          assert.ok(
            riffChunkTypes(image).includes("VP8L"),
            `${guideFile} image ${index} must use lossless WebP encoding`,
          );
        }
      }
    }
    assert.ok(
      imageCount > 0,
      `${guideFile} must include at least one representative screenshot`,
    );
  }
});

test("every shared feature guide has one thin website route with synchronized metadata", async () => {
  const guideFiles = (await readdir(guidesRoot)).filter(
    (name) => name.endsWith(".json") && name !== "schema.json",
  );
  const userGuideRoutes = await findFiles(
    path.join(siteRoot, "src", "content", "docs", "docs"),
    ".mdx",
  );

  for (const guideFile of guideFiles) {
    const guide = JSON.parse(
      await readFile(path.join(guidesRoot, guideFile), "utf8"),
    );
    const matchingRoutes = [];
    for (const routePath of userGuideRoutes) {
      const route = await readFile(routePath, "utf8");
      if (route.includes(`/docs/feature-guides/${guideFile}`))
        matchingRoutes.push({ routePath, route });
    }

    assert.equal(
      matchingRoutes.length,
      1,
      `${guideFile} must be imported by exactly one User Guide route`,
    );
    const [{ routePath, route }] = matchingRoutes;
    assertNonEmptyString(guide.title, `${guideFile} title`);
    assertNonEmptyString(guide.description, `${guideFile} description`);
    assert.match(
      route,
      new RegExp(
        `^title: ${guide.title.replace(/[.*+?^${}()|[\]\\]/g, "\\$&")}$`,
        "m",
      ),
    );
    assert.match(
      route,
      new RegExp(
        `^description: ${guide.description.replace(/[.*+?^${}()|[\]\\]/g, "\\$&")}$`,
        "m",
      ),
    );
    assert.match(
      route,
      /<FeatureGuide guide={guide} \/>/,
      `${path.relative(siteRoot, routePath)} must use FeatureGuide`,
    );
  }
});

test("every page listed in the website User Guide is backed by a shared guide", async () => {
  const config = await readFile(
    path.join(siteRoot, "astro.config.mjs"),
    "utf8",
  );
  const userGuideStart = config.indexOf("label: 'User Guide'");
  const developerStart = config.indexOf("label: 'Developer'", userGuideStart);
  assert.ok(
    userGuideStart >= 0 && developerStart > userGuideStart,
    "User Guide sidebar section must exist",
  );

  const userGuideConfig = config.slice(userGuideStart, developerStart);
  const links = [
    ...userGuideConfig.matchAll(/\{ link: '([^']+)', label: '([^']+)' \}/g),
  ].map(([, link, label]) => ({ link, label }));
  assert.ok(links.length > 0, "User Guide sidebar must list pages");
  assert.equal(
    new Set(links.map(({ link }) => link)).size,
    links.length,
    "User Guide sidebar links must be unique",
  );

  const contentRoot = path.join(siteRoot, "src", "content", "docs");
  for (const { link, label } of links) {
    const relativeRoute = link.replace(/^\//, "").replace(/\/$/, "");
    const candidates = [
      path.join(contentRoot, `${relativeRoute}.mdx`),
      path.join(contentRoot, relativeRoute, "index.mdx"),
    ];
    let route;
    let routePath;
    for (const candidate of candidates) {
      try {
        route = await readFile(candidate, "utf8");
        routePath = candidate;
        break;
      } catch (error) {
        if (error?.code !== "ENOENT") throw error;
      }
    }
    assert.ok(routePath, `${label} (${link}) must resolve to an MDX route`);
    const guideImport = route.match(
      /\/docs\/feature-guides\/([a-z0-9-]+\.json)/,
    );
    assert.ok(
      guideImport,
      `${path.relative(siteRoot, routePath)} must import a shared feature guide`,
    );
    const guide = JSON.parse(
      await readFile(path.join(guidesRoot, guideImport[1]), "utf8"),
    );
    assert.equal(
      label,
      guide.title,
      `${link} sidebar label must match its shared in-app topic title`,
    );
  }
});
