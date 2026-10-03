import { useState, type ImgHTMLAttributes, type ReactNode } from "react";

interface FadeInImageProps extends ImgHTMLAttributes<HTMLImageElement> {
  /** Shown in place of the image if it fails to load; without one the failed image stays visible. */
  fallback?: ReactNode;
}

/**
 * An image that fades in once it has loaded, so the card surface behind it shows instead of a
 * half-decoded picture popping in. The fade is skipped for users who prefer reduced motion.
 */
export function FadeInImage({ className = "", onLoad, onError, fallback, ...imageProps }: FadeInImageProps) {
  const [status, setStatus] = useState<"loading" | "loaded" | "failed">("loading");
  const [prevSrc, setPrevSrc] = useState(imageProps.src);
  if (imageProps.src !== prevSrc) {
    setPrevSrc(imageProps.src);
    setStatus("loading");
  }

  if (status === "failed" && fallback !== undefined) return <>{fallback}</>;

  return (
    <img
      {...imageProps}
      className={`${className} transition-opacity duration-300 motion-reduce:transition-none ${status === "loading" ? "opacity-0" : "opacity-100"}`.trim()}
      onLoad={(event) => {
        setStatus("loaded");
        onLoad?.(event);
      }}
      onError={(event) => {
        setStatus("failed");
        onError?.(event);
      }}
    />
  );
}
