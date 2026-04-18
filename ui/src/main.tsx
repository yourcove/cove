import React from "react";
import ReactDOM from "react-dom/client";
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import App from "./App";
import { ErrorBoundary } from "./components/ErrorBoundary";
import "./index.css";

// Prevent pinch-to-zoom on iOS (viewport meta alone is insufficient since iOS 10+)
// Use capture phase so we intercept before the browser processes the gesture
document.addEventListener("gesturestart", (e) => e.preventDefault(), { passive: false, capture: true });
document.addEventListener("gesturechange", (e) => e.preventDefault(), { passive: false, capture: true });
document.addEventListener("gestureend", (e) => e.preventDefault(), { passive: false, capture: true });
// Prevent multi-touch zoom on all platforms — including during active scroll.
// touchstart: block new touches when a second finger lands (prevents zoom initiation mid-scroll)
// touchmove: block multi-touch moves that slipped past touchstart
document.addEventListener("touchstart", (e) => { if (e.touches.length > 1) e.preventDefault(); }, { passive: false, capture: true });
document.addEventListener("touchmove", (e) => { if (e.touches.length > 1) e.preventDefault(); }, { passive: false, capture: true });
// Safari fallback: continuously monitor viewport scale and instantly reset if zoomed.
// This catches cases where the browser ignores preventDefault during momentum scroll.
if (window.visualViewport) {
  let resetPending = false;
  const resetZoom = () => {
    if (window.visualViewport && window.visualViewport.scale > 1.01) {
      if (!resetPending) {
        resetPending = true;
        // Use requestAnimationFrame to batch resets
        requestAnimationFrame(() => {
          const vp = document.querySelector('meta[name="viewport"]');
          if (vp) {
            // Toggle viewport to force Safari to recalculate
            vp.setAttribute("content", "width=device-width, initial-scale=1.0, minimum-scale=1.0, maximum-scale=1.0, user-scalable=no");
          }
          resetPending = false;
        });
      }
    }
  };
  window.visualViewport.addEventListener("resize", resetZoom);
  window.visualViewport.addEventListener("scroll", resetZoom);
}

const queryClient = new QueryClient({
  defaultOptions: {
    queries: {
      staleTime: 30_000,
      retry: 1,
    },
  },
});

ReactDOM.createRoot(document.getElementById("root")!).render(
  <React.StrictMode>
    <ErrorBoundary>
      <QueryClientProvider client={queryClient}>
        <App />
      </QueryClientProvider>
    </ErrorBoundary>
  </React.StrictMode>
);
