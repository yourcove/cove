export const PREVIEW_ROBOTS = 'noindex, nofollow, noarchive';
export const PRODUCTION_ROBOTS =
  'index, follow, max-image-preview:large, max-snippet:-1, max-video-preview:-1';

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
