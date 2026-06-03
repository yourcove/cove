import { defineConfig } from 'astro/config';
import mdx from '@astrojs/mdx';
import sitemap from '@astrojs/sitemap';
import starlight from '@astrojs/starlight';

const [owner, repo] = (process.env.GITHUB_REPOSITORY ?? '').split('/');
const site = process.env.SITE_URL ?? (owner ? `https://${owner}.github.io` : 'https://example.com');
const isGitHubPages = site.includes('github.io');
const base = isGitHubPages && repo ? `/${repo}/` : '/';

export default defineConfig({
  site,
  base,
  integrations: [
    starlight({
      title: 'Cove',
      description: 'Content Organization for Virtual Entertainment',
      titleDelimiter: '·',
      favicon: '/favicon.svg',
      components: {
        SiteTitle: './src/components/starlight/SiteTitle.astro',
      },
      customCss: ['./src/styles/global.css'],
      social: [
        {
          icon: 'github',
          label: 'GitHub',
          href: 'https://github.com/yourcove/cove',
        },
      ],
      sidebar: [
        {
          label: 'Docs Home',
          items: [{ link: '/docs/', label: 'Overview' }],
        },
        {
          label: 'User Docs',
          items: [
            { link: '/docs/user/', label: 'Overview' },
            {
              label: 'Getting Started',
              items: [
                { link: '/docs/user/getting-started/install/', label: 'Install' },
                { link: '/docs/user/getting-started/first-scan/', label: 'First Scan' },
                { link: '/docs/user/getting-started/import-existing-library/', label: 'Import Existing Library' },
              ],
            },
            {
              label: 'Library',
              items: [
                { link: '/docs/user/library/media-types/', label: 'Media Types' },
                { link: '/docs/user/library/organizing/', label: 'Organizing Your Library' },
                { link: '/docs/user/library/search-and-filters/', label: 'Search and Filters' },
                { link: '/docs/user/library/dynamic-groups/', label: 'Dynamic Groups' },
                { link: '/docs/user/library/segments-and-compilations/', label: 'Segments and Compilations' },
              ],
            },
            {
              label: 'Metadata',
              items: [
                { link: '/docs/user/metadata/provenance/', label: 'Metadata Provenance' },
                { link: '/docs/user/metadata/providers-scrapers-downloaders/', label: 'Providers, Scrapers, and Downloaders' },
              ],
            },
            { link: '/docs/user/security/users-roles-permissions/', label: 'Users and Permissions' },
            { link: '/docs/user/admin/backups-migrations-upgrades/', label: 'Backups and Upgrades' },
            { link: '/docs/user/troubleshooting/', label: 'Troubleshooting' },
          ],
        },
        {
          label: 'Developer Docs',
          items: [
            { link: '/docs/developer/', label: 'Overview' },
            {
              label: 'Local Development',
              items: [
                { link: '/docs/developer/getting-started/local-development/', label: 'Run Cove Locally' },
              ],
            },
            {
              label: 'Extensions',
              items: [
                { link: '/docs/developer/extensions/overview/', label: 'Architecture' },
                { link: '/docs/developer/extensions/packaging/', label: 'Packaging' },
                { link: '/docs/developer/extensions/permissions/', label: 'Permissions' },
                { link: '/docs/developer/extensions/create-extension/', label: 'Create an Extension' },
                { link: '/docs/developer/extensions/create-scraper/', label: 'Create a Scraper' },
                { link: '/docs/developer/extensions/create-downloader/', label: 'Create a Downloader' },
              ],
            },
            { link: '/docs/developer/api/overview/', label: 'API Surface' },
            { link: '/docs/developer/contributing/website/', label: 'Website Contributions' },
          ],
        },
      ],
      lastUpdated: true,
      credits: false,
    }),
    mdx(),
    sitemap(),
  ],
});
