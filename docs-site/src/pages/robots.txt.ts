import type { APIRoute } from 'astro';
import { isProductionDeployment } from '../lib/deployment';
import { withBase } from '../lib/paths';
import { COVE_SITE } from '../lib/site';

export const GET: APIRoute = ({ site }) => {
  if (!isProductionDeployment()) {
    return new Response('User-agent: *\nDisallow: /\n', {
      headers: {
        'Content-Type': 'text/plain; charset=utf-8',
      },
    });
  }

  const origin = site ?? new URL(COVE_SITE);
  const body = `User-agent: *\nAllow: /\n\nSitemap: ${new URL(withBase('sitemap-index.xml'), origin).href}\n`;

  return new Response(body, {
    headers: {
      'Content-Type': 'text/plain; charset=utf-8',
    },
  });
};
