import { COVE_RELEASES_LATEST, COVE_REPO } from './site';

export const SITE_NAME = 'Cove';
export const SITE_URL = 'https://yourcove.net';
export const SITE_TAGLINE = 'Content Organization for Virtual Entertainment';
export const SITE_DESCRIPTION =
  'Cove is a fast local media organizer for private libraries, shared collections on your network, and deeper organization as your library grows.';
export const DEFAULT_OG_IMAGE = '/images/screenshots/dashboard-home.png';

type SiteInput = URL | string | undefined;

interface PageSchemaOptions {
  title: string;
  description: string;
  pathname: string;
  imagePath?: string;
  pageType?: 'WebPage' | 'AboutPage' | 'CollectionPage';
}

interface DocsSchemaOptions {
  title: string;
  description: string;
  pathname: string;
}

function getSiteOrigin(site: SiteInput) {
  if (site instanceof URL) {
    return site;
  }

  if (typeof site === 'string' && site.length > 0) {
    return new URL(site);
  }

  return new URL(SITE_URL);
}

export function getAbsoluteUrl(pathname: string, site?: SiteInput) {
  return new URL(pathname, getSiteOrigin(site)).href;
}

export function getMarketingPageSchemas({
  title,
  description,
  pathname,
  imagePath = DEFAULT_OG_IMAGE,
  pageType = 'WebPage',
}: PageSchemaOptions) {
  const canonicalUrl = getAbsoluteUrl(pathname);
  const imageUrl = getAbsoluteUrl(imagePath);
  const docsUrl = getAbsoluteUrl('/docs/');

  return [
    {
      '@context': 'https://schema.org',
      '@type': 'Organization',
      name: SITE_NAME,
      url: SITE_URL,
      logo: getAbsoluteUrl('/favicon.svg'),
      sameAs: [COVE_REPO],
    },
    {
      '@context': 'https://schema.org',
      '@type': 'WebSite',
      name: SITE_NAME,
      url: SITE_URL,
      description: SITE_DESCRIPTION,
      publisher: {
        '@type': 'Organization',
        name: SITE_NAME,
      },
      inLanguage: 'en-US',
    },
    {
      '@context': 'https://schema.org',
      '@type': 'SoftwareApplication',
      name: SITE_NAME,
      url: SITE_URL,
      applicationCategory: 'MultimediaApplication',
      operatingSystem: 'Windows, macOS, Linux, Docker',
      description: SITE_DESCRIPTION,
      downloadUrl: COVE_RELEASES_LATEST,
      softwareHelp: docsUrl,
      screenshot: imageUrl,
      isAccessibleForFree: true,
      author: {
        '@type': 'Organization',
        name: SITE_NAME,
      },
      sameAs: [COVE_REPO],
      featureList: [
        'Organize videos, images, galleries, audio, and text in one local library',
        'Search by tags, performers, groups, studios, and other media details',
        'Share one library within your network using users, roles, permissions, and share links',
        'Browse in grid, feed, and vertical-style views',
        'Add downloaders, scrapers, themes, and tools through extensions',
      ],
    },
    {
      '@context': 'https://schema.org',
      '@type': pageType,
      name: title,
      url: canonicalUrl,
      description,
      isPartOf: {
        '@type': 'WebSite',
        name: SITE_NAME,
        url: SITE_URL,
      },
      primaryImageOfPage: imageUrl,
      about: {
        '@type': 'SoftwareApplication',
        name: SITE_NAME,
        url: SITE_URL,
      },
      inLanguage: 'en-US',
    },
  ];
}

export function getDocsSiteSchema() {
  return [
    {
      '@context': 'https://schema.org',
      '@type': 'Organization',
      name: SITE_NAME,
      url: SITE_URL,
      logo: getAbsoluteUrl('/favicon.svg'),
      sameAs: [COVE_REPO],
    },
    {
      '@context': 'https://schema.org',
      '@type': 'WebSite',
      name: `${SITE_NAME} Docs`,
      url: getAbsoluteUrl('/docs/'),
      description: 'Documentation for Cove, a local media organizer for private and shared libraries.',
      about: {
        '@type': 'SoftwareApplication',
        name: SITE_NAME,
        url: SITE_URL,
      },
      publisher: {
        '@type': 'Organization',
        name: SITE_NAME,
      },
      inLanguage: 'en-US',
    },
  ];
}

export function getDocPageSchema({ title, description, pathname }: DocsSchemaOptions) {
  return {
    '@context': 'https://schema.org',
    '@type': 'TechArticle',
    headline: title,
    description,
    url: getAbsoluteUrl(pathname),
    image: getAbsoluteUrl(DEFAULT_OG_IMAGE),
    author: {
      '@type': 'Organization',
      name: SITE_NAME,
    },
    publisher: {
      '@type': 'Organization',
      name: SITE_NAME,
      logo: {
        '@type': 'ImageObject',
        url: getAbsoluteUrl('/favicon.svg'),
      },
    },
    about: {
      '@type': 'SoftwareApplication',
      name: SITE_NAME,
      url: SITE_URL,
    },
    inLanguage: 'en-US',
  };
}
