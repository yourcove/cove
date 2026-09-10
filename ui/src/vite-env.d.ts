/// <reference types="vite/client" />

declare const __COVE_FRONTEND_BUILD_ID__: string;

declare module "virtual:changelog-raw" {
  const content: string;
  export default content;
}
