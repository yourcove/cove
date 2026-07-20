import type { APIRoute } from 'astro';
import { COVE_SITE } from '../lib/site';

export const GET: APIRoute = ({ site }) => {
  const origin = site ?? new URL(COVE_SITE);
  const body = `User-agent: *\nAllow: /\n\nSitemap: ${new URL('/sitemap-index.xml', origin).href}\n`;

  return new Response(body, {
    headers: {
      'Content-Type': 'text/plain; charset=utf-8',
    },
  });
};
