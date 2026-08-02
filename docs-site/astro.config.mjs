import { defineConfig } from 'astro/config';
import mdx from '@astrojs/mdx';
import sitemap from '@astrojs/sitemap';
import starlight from '@astrojs/starlight';
import { isProductionDeployment, PREVIEW_ROBOTS, PRODUCTION_ROBOTS, resolveSiteUrl } from './src/lib/deployment.ts';
import { COVE_REPO, COVE_SITE } from './src/lib/site.ts';
import { DEFAULT_OG_IMAGE, SITE_DESCRIPTION, SITE_NAME, getAbsoluteUrl, getDocsSiteSchema } from './src/lib/seo.ts';

const [, repo] = (process.env.GITHUB_REPOSITORY ?? '').split('/');
const configuredSite = resolveSiteUrl(process.env.SITE_URL ?? COVE_SITE);
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
        Sidebar: './src/components/starlight/Sidebar.astro',
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
          label: 'Get Started',
          items: [
            { link: '/docs/get-started/', label: 'Get started overview' },
            {
              label: 'Choose an installation',
              items: [
                { link: '/docs/user/getting-started/install/', label: 'Installation overview' },
                { link: '/docs/user/getting-started/install-windows/', label: 'Windows' },
                { link: '/docs/user/getting-started/install-macos/', label: 'macOS' },
                { link: '/docs/user/getting-started/install-linux/', label: 'Linux' },
                { link: '/docs/user/getting-started/install-docker/', label: 'Docker' },
              ],
            },
            {
              label: 'Complete first-run setup',
              items: [
                { link: '/docs/user/getting-started/first-run-setup/', label: 'Choose a setup path' },
                { link: '/docs/user/getting-started/import-existing-library/', label: 'Import from Stash' },
              ],
            },
            {
              label: 'Learn Cove',
              items: [
                { link: '/docs/tutorial/', label: 'Your first hour with Cove' },
                { link: '/docs/user/getting-started/first-scan/', label: 'Scan your first library' },
                { link: '/docs/tutorial/explore-your-library/', label: 'Explore your library' },
                { link: '/docs/tutorial/find-anything/', label: 'Find anything' },
                { link: '/docs/tutorial/organize-a-collection/', label: 'Organize a collection' },
              ],
            },
          ],
        },
        {
          label: 'User Guide',
          items: [
            { link: '/docs/user/', label: 'User guide overview' },
            {
              label: 'Browse and play',
              items: [
                { link: '/docs/user/library/media-types/', label: 'Media types' },
              ],
            },
            {
              label: 'Organize your library',
              items: [
                { link: '/docs/user/library/organizing/', label: 'Tags and groups' },
                { link: '/docs/user/library/dynamic-groups/', label: 'When to use dynamic groups' },
                { link: '/docs/user/library/segments-and-compilations/', label: 'Segments and compilations' },
              ],
            },
            {
              label: 'Search and filters',
              items: [
                { link: '/docs/user/library/search-and-filters/', label: 'Search and filters' },
              ],
            },
            {
              label: 'Metadata',
              items: [
                { link: '/docs/user/metadata/provenance/', label: 'Metadata provenance' },
                { link: '/docs/user/metadata/providers-scrapers-downloaders/', label: 'Providers, scrapers, and downloaders' },
              ],
            },
            {
              label: 'Users and sharing',
              items: [
                { link: '/docs/user/security/users-roles-permissions/', label: 'Users, roles, and permissions' },
              ],
            },
            {
              label: 'Operations and backups',
              items: [
                { link: '/docs/user/admin/backups-migrations-upgrades/', label: 'Backups, migrations, and upgrades' },
              ],
            },
            { link: '/docs/user/troubleshooting/', label: 'Troubleshooting' },
          ],
        },
        {
          label: 'Developer',
          items: [
            { link: '/docs/developer/', label: 'Developer overview' },
            {
              label: 'Develop Cove locally',
              items: [
                { link: '/docs/developer/getting-started/local-development/', label: 'Run Cove locally' },
                { link: '/docs/developer/contributing/core-development/', label: 'Core contribution policy' },
              ],
            },
            {
              label: 'Extension tutorials',
              items: [
                { link: '/docs/developer/extensions/create-extension/', label: 'Create an extension' },
                { link: '/docs/developer/extensions/create-scraper/', label: 'Create a scraper' },
                { link: '/docs/developer/extensions/create-downloader/', label: 'Create a downloader' },
              ],
            },
            {
              label: 'Architecture and packaging',
              items: [
                { link: '/docs/developer/extensions/overview/', label: 'Extension architecture' },
                { link: '/docs/developer/architecture/logging/', label: 'Logging policy' },
                { link: '/docs/developer/extensions/packaging/', label: 'Package an extension' },
                { link: '/docs/developer/extensions/permissions/', label: 'Extension permissions' },
                { link: '/docs/developer/extensions/extension-points/', label: 'Extension point catalog' },
                { link: '/docs/developer/extensions/events/', label: 'Extension events' },
                { link: '/docs/developer/extensions/ui-extension-points/', label: 'UI extension points' },
                { link: '/docs/developer/extensions/frontend-runtime/', label: 'Frontend runtime API' },
              ],
            },
            {
              label: 'API',
              items: [
                { link: '/docs/developer/api/overview/', label: 'API surface' },
              ],
            },
            {
              label: 'Documentation',
              items: [
                { link: '/docs/guides/', label: 'Documentation map' },
                { link: '/docs/developer/contributing/website/', label: 'Documentation style guide' },
              ],
            },
          ],
        },
        {
          label: 'Reference',
          items: [
            { link: '/docs/reference/', label: 'Reference overview' },
            { link: '/docs/terminology/', label: 'Terminology' },
            {
              label: 'Library and media',
              items: [
                { link: '/docs/reference/library/', label: 'Library' },
                { link: '/docs/reference/library-paths/', label: 'Library paths' },
                { link: '/docs/reference/media/', label: 'Media' },
                { link: '/docs/reference/videos/', label: 'Videos' },
                { link: '/docs/reference/images/', label: 'Images' },
                { link: '/docs/reference/galleries/', label: 'Galleries' },
                { link: '/docs/reference/audio/', label: 'Audio' },
                { link: '/docs/reference/text/', label: 'Text' },
                { link: '/docs/reference/generated-media/', label: 'Generated media' },
              ],
            },
            {
              label: 'Organization',
              items: [
                { link: '/docs/reference/tags/', label: 'Tags' },
                { link: '/docs/reference/performers/', label: 'Performers' },
                { link: '/docs/reference/studios/', label: 'Studios' },
                { link: '/docs/reference/groups/', label: 'Groups' },
                { link: '/docs/reference/raw-segments/', label: 'Raw segments' },
                { link: '/docs/reference/segments/', label: 'Segments' },
                { link: '/docs/reference/sub-videos/', label: 'Sub-videos' },
                { link: '/docs/reference/compilations/', label: 'Compilations' },
                { link: '/docs/reference/dynamic-groups/', label: 'Dynamic groups' },
                { link: '/docs/reference/saved-filters/', label: 'Saved filters' },
              ],
            },
            {
              label: 'Intake and metadata',
              items: [
                { link: '/docs/reference/scans/', label: 'Scans' },
                { link: '/docs/reference/intake/', label: 'Intake' },
                { link: '/docs/reference/metadata/', label: 'Metadata' },
                { link: '/docs/reference/metadata-providers/', label: 'Metadata providers' },
                { link: '/docs/reference/scrapers/', label: 'Scrapers' },
                { link: '/docs/reference/downloaders/', label: 'Downloaders' },
                { link: '/docs/reference/metadata-servers/', label: 'Metadata servers' },
                { link: '/docs/reference/provenance/', label: 'Provenance' },
              ],
            },
            {
              label: 'Extensions',
              items: [
                { link: '/docs/reference/extensions/', label: 'Extensions' },
                { link: '/docs/reference/extension-points/', label: 'Extension points' },
                { link: '/docs/reference/extension-events/', label: 'Extension event envelope' },
                { link: '/docs/reference/entity-lifecycle-event-payloads/', label: 'Lifecycle event payloads' },
                { link: '/docs/reference/rating-event-payloads/', label: 'Rating event payloads' },
                { link: '/docs/reference/contributions/', label: 'Contributions' },
                { link: '/docs/reference/manifests/', label: 'Manifests' },
                { link: '/docs/reference/ui-extensions/', label: 'UI extensions' },
                { link: '/docs/reference/manifest-only-extensions/', label: 'Manifest-only extensions' },
              ],
            },
            {
              label: 'Access and operation',
              items: [
                { link: '/docs/reference/instances/', label: 'Instances' },
                { link: '/docs/reference/users/', label: 'Users' },
                { link: '/docs/reference/owners/', label: 'Owner' },
                { link: '/docs/reference/roles/', label: 'Roles' },
                { link: '/docs/reference/role-assignments/', label: 'Role assignments' },
                { link: '/docs/reference/permissions/', label: 'Permissions' },
                { link: '/docs/reference/content-rules/', label: 'Content rules' },
                { link: '/docs/reference/api-tokens/', label: 'API tokens' },
                { link: '/docs/reference/share-links/', label: 'Share links' },
              ],
            },
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
