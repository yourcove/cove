export const PREVIEW_ROBOTS = 'noindex, nofollow, noarchive';
export const PRODUCTION_ROBOTS =
  'index, follow, max-image-preview:large, max-snippet:-1, max-video-preview:-1';

const HTTP_PREFIX = 'http://';
const LOCAL_HOSTNAMES = new Set(['localhost', '127.0.0.1', '[::1]']);

// GitHub Pages reports its base URL over http whenever "Enforce HTTPS" is unavailable,
// which is the case for a custom domain served through a CDN. Publishing that value as-is
// emits http Open Graph URLs and sitemap entries for a site that only ever answers over
// https, so upgrade the scheme for every origin that is not a local preview.
export function resolveSiteUrl(configuredSite: string) {
  const { protocol, hostname } = new URL(configuredSite);

  if (protocol !== 'http:' || LOCAL_HOSTNAMES.has(hostname)) {
    return configuredSite;
  }

  return `https://${configuredSite.slice(HTTP_PREFIX.length)}`;
}

export function isProductionDeployment(
  deployment = process.env.COVE_DOCS_DEPLOYMENT ?? 'preview',
) {
  if (deployment !== 'preview' && deployment !== 'production') {
    throw new Error(
      `COVE_DOCS_DEPLOYMENT must be "preview" or "production"; received ${JSON.stringify(deployment)}.`,
    );
  }

  return deployment === 'production';
}
