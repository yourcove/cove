import { defineConfig } from 'astro/config';
import mdx from '@astrojs/mdx';
import sitemap from '@astrojs/sitemap';
import starlight from '@astrojs/starlight';

const [owner, repo] = (process.env.GITHUB_REPOSITORY ?? '').split('/');
const site = process.env.SITE_URL ?? (owner ? `https://${owner}.github.io` : 'https://example.com');
const base = process.env.GITHUB_ACTIONS === 'true' && repo ? `/${repo}/` : '/';

export default defineConfig({
  site,
  base,
  integrations: [
    starlight({
      title: 'Cove',
      description: 'Content Organization for Virtual Entertainment',
      titleDelimiter: '·',
      favicon: '/favicon.svg',
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
          items: [{ autogenerate: { directory: 'docs/user' } }],
        },
        {
          label: 'Developer Docs',
          items: [{ autogenerate: { directory: 'docs/developer' } }],
        },
      ],
      lastUpdated: true,
      credits: false,
    }),
    mdx(),
    sitemap(),
  ],
});
