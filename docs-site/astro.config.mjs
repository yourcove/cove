import { defineConfig } from 'astro/config';
import mdx from '@astrojs/mdx';
import sitemap from '@astrojs/sitemap';
import starlight from '@astrojs/starlight';
import { isProductionDeployment, PREVIEW_ROBOTS, PRODUCTION_ROBOTS } from './src/lib/deployment.ts';
import { COVE_REPO, COVE_SITE } from './src/lib/site.ts';
import { DEFAULT_OG_IMAGE, SITE_DESCRIPTION, SITE_NAME, getAbsoluteUrl, getDocsSiteSchema } from './src/lib/seo.ts';

const [, repo] = (process.env.GITHUB_REPOSITORY ?? '').split('/');
const configuredSite = process.env.SITE_URL ?? COVE_SITE;
const isProduction = isProductionDeployment();
const site = isProduction ? configuredSite : undefined;
const isGitHubPages = configuredSite.includes('github.io');
const base = isGitHubPages && repo ? `/${repo}/` : '/';
const docsOgImage = getAbsoluteUrl(DEFAULT_OG_IMAGE, site);
const sitemapUrl = new URL('sitemap-index.xml', `${configuredSite.replace(/\/+$/, '')}/`).href;
// Starlight otherwise adds its sitemap integration automatically, even when a preview
// intentionally has no public site URL. This named no-op keeps previews warning-free.
const previewSitemap = { name: '@astrojs/sitemap', hooks: {} };

export default defineConfig({
  site,
  base,
  integrations: [
    starlight({
      title: SITE_NAME,
      description: SITE_DESCRIPTION,
      titleDelimiter: '\u00B7',
      favicon: '/favicon.svg',
      components: {
        Banner: './src/components/starlight/Banner.astro',
        Head: './src/components/starlight/Head.astro',
        SiteTitle: './src/components/starlight/SiteTitle.astro',
      },
      customCss: ['./src/styles/global.css'],
      social: [
        {
          icon: 'github',
          label: 'GitHub',
          href: COVE_REPO,
        },
      ],
      head: [
        {
          tag: 'meta',
          attrs: {
            name: 'robots',
            content: isProduction ? PRODUCTION_ROBOTS : PREVIEW_ROBOTS,
          },
        },
        ...(isProduction
          ? [{
              tag: 'link',
              attrs: {
                rel: 'sitemap',
                href: sitemapUrl,
              },
            }]
          : []),
        {
          tag: 'meta',
          attrs: {
            property: 'og:image',
            content: docsOgImage,
          },
        },
        {
          tag: 'meta',
          attrs: {
            name: 'twitter:image',
            content: docsOgImage,
          },
        },
        {
          tag: 'meta',
          attrs: {
            name: 'application-name',
            content: SITE_NAME,
          },
        },
        {
          tag: 'script',
          attrs: {
            type: 'application/ld+json',
          },
          content: JSON.stringify(getDocsSiteSchema()),
        },
      ],
      sidebar: [
        {
          label: 'Docs home',
          items: [
            { link: '/docs/', label: 'Documentation home' },
          ],
        },
        {
          label: 'User docs',
          items: [
            { link: '/docs/user/', label: 'User guide' },
            {
              label: 'Getting started',
              items: [
                { link: '/docs/user/getting-started/install/', label: 'Install' },
                { link: '/docs/user/getting-started/first-scan/', label: 'First scan' },
                { link: '/docs/user/getting-started/import-existing-library/', label: 'Import existing library' },
              ],
            },
            {
              label: 'Library',
              items: [
                { link: '/docs/user/library/media-types/', label: 'Media types' },
                { link: '/docs/user/library/organizing/', label: 'Organizing your library' },
                { link: '/docs/user/library/search-and-filters/', label: 'Search and filters' },
                { link: '/docs/user/library/dynamic-groups/', label: 'Dynamic groups' },
                { link: '/docs/user/library/segments-and-compilations/', label: 'Segments and compilations' },
              ],
            },
            {
              label: 'Metadata',
              items: [
                { link: '/docs/user/metadata/provenance/', label: 'Metadata provenance' },
                { link: '/docs/user/metadata/providers-scrapers-downloaders/', label: 'Providers, scrapers, and downloaders' },
              ],
            },
            { link: '/docs/user/security/users-roles-permissions/', label: 'Users and permissions' },
            { link: '/docs/user/admin/backups-migrations-upgrades/', label: 'Backups and upgrades' },
            { link: '/docs/user/troubleshooting/', label: 'Troubleshooting' },
          ],
        },
        {
          label: 'Reference',
          items: [
            { link: '/docs/reference/', label: 'Reference overview' },
            { link: '/docs/terminology/', label: 'Terminology' },
            { link: '/docs/reference/videos/', label: 'Videos' },
          ],
        },
        {
          label: 'Developer docs',
          items: [
            { link: '/docs/developer/', label: 'Developer guide' },
            {
              label: 'Local development',
              items: [
                { link: '/docs/developer/getting-started/local-development/', label: 'Run Cove locally' },
              ],
            },
            {
              label: 'Extensions',
              items: [
                { link: '/docs/developer/extensions/create-extension/', label: 'Create an extension' },
                { link: '/docs/developer/extensions/create-scraper/', label: 'Create a scraper' },
                { link: '/docs/developer/extensions/create-downloader/', label: 'Create a downloader' },
                { link: '/docs/developer/extensions/overview/', label: 'Architecture' },
                { link: '/docs/developer/extensions/packaging/', label: 'Packaging' },
                { link: '/docs/developer/extensions/permissions/', label: 'Permissions' },
                { link: '/docs/developer/extensions/extension-points/', label: 'Extension point catalog' },
                { link: '/docs/developer/extensions/ui-extension-points/', label: 'UI extension points' },
              ],
            },
            { link: '/docs/developer/api/overview/', label: 'API surface' },
            { link: '/docs/developer/contributing/website/', label: 'Documentation style guide' },
          ],
        },
      ],
      lastUpdated: true,
      credits: false,
    }),
    mdx(),
    isProduction ? sitemap() : previewSitemap,
  ],
});
