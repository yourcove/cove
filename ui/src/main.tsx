import { startFrontendBuildMonitor } from "./frontendBuild";
import { showFrontendUpdate } from "./frontendUpdate";

// Install the detector before importing the application or any lazy chunks.
if (import.meta.env.PROD) {
  startFrontendBuildMonitor(__COVE_FRONTEND_BUILD_ID__, showFrontendUpdate);
}

void import("./mount");
