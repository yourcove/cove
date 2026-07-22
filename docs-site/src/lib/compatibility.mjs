export const COVE_DOCUMENTED_RELEASE_VERSION = '1.0.0';
export const COVE_DOCUMENTED_SDK_VERSION = '1.0.0';
export const COVE_SOURCE_VERSION = '1.0.0-dev';
export const COVE_SOURCE_REF = 'main';
export const COVE_REVIEWED_SOURCE_REVISION = '1ebd0d7251aa9ae2b1f5ea10f344978b03f6819c';

const COVE_REPOSITORY = 'https://github.com/yourcove/cove';

export function getCoveSourceUrl(sourcePath) {
  const encodedPath = sourcePath.split('/').map(encodeURIComponent).join('/');
  return `${COVE_REPOSITORY}/blob/${COVE_SOURCE_REF}/${encodedPath}`;
}

export const COVE_REVIEWED_SOURCE_URL = `${COVE_REPOSITORY}/commit/${COVE_REVIEWED_SOURCE_REVISION}`;
