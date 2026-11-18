import {
  useCallback,
  useEffect,
  useLayoutEffect,
  useRef,
  useState,
  type ImgHTMLAttributes,
  type ReactNode,
  type Ref,
} from "react";

// A responsive srcSet would let the element pick a picture other than the one loaded off screen, so it is left out.
export interface LoadedImageProps extends Omit<ImgHTMLAttributes<HTMLImageElement>, "srcSet" | "sizes"> {
  ref?: Ref<HTMLImageElement>;
  /** Shown in place of the image if it fails to load; without one a failed image stays invisible. */
  fallback?: ReactNode;
}

type Status = "loading" | "loaded" | "failed";

/**
 * The app's image element. It shows a picture only once the browser has loaded it, so a missing or failed image
 * never shows the browser's broken-image icon: the browser can paint that icon before the error event reaches any
 * handler. Until then it is transparent; after a failure it shows the fallback, or nothing.
 *
 * When the source of a shown picture changes, the old picture stays up while the new one loads off screen, as a
 * plain image element would keep it, and the new one replaces it only once it has loaded.
 */
export function LoadedImage({ fallback, src, style, onLoad, onError, ref, ...imageProps }: LoadedImageProps) {
  // The source on the element and how far it got. A new source replaces it at once unless a picture is showing.
  const [shown, setShown] = useState<{ src: string | undefined; status: Status }>(() => ({
    src,
    status: src ? "loading" : "failed",
  }));
  const [prevSrc, setPrevSrc] = useState(src);
  if (src !== prevSrc) {
    setPrevSrc(src);
    if (!src) setShown({ src, status: "failed" });
    else if (shown.status !== "loaded") setShown({ src, status: "loading" });
  }
  const pendingSrc = src && src !== shown.src && shown.status === "loaded" ? src : null;

  // Load a replacement off screen and swap it in once the browser has it, so the swap paints at once.
  const { crossOrigin, referrerPolicy } = imageProps;
  useEffect(() => {
    if (!pendingSrc) return;
    let current = true;
    const preload = new Image();
    if (crossOrigin !== undefined) preload.crossOrigin = crossOrigin;
    if (referrerPolicy !== undefined) preload.referrerPolicy = referrerPolicy;
    preload.onload = () => current && setShown({ src: pendingSrc, status: "loaded" });
    // Hand a failed source to the hidden element so it reports the failure through onError and the fallback.
    preload.onerror = () => current && setShown({ src: pendingSrc, status: "loading" });
    preload.src = pendingSrc;
    return () => {
      current = false;
      preload.onload = preload.onerror = null;
      // Stop a download that is no longer wanted, as a plain image element would when its source changes.
      preload.src = "";
    };
  }, [pendingSrc, crossOrigin, referrerPolicy]);

  // A cached picture can be complete before the load event arrives; show it on the first frame instead.
  const imageRef = useRef<HTMLImageElement | null>(null);
  const setImageRef = useCallback(
    (image: HTMLImageElement | null) => {
      imageRef.current = image;
      if (typeof ref === "function") return ref(image);
      if (ref) ref.current = image;
    },
    [ref],
  );
  useLayoutEffect(() => {
    const image = imageRef.current;
    if (shown.status === "loading" && image?.complete && image.naturalWidth > 0) {
      setShown((current) => (current.src === shown.src ? { ...current, status: "loaded" } : current));
    }
  }, [shown]);

  if (shown.status === "failed" && fallback !== undefined) return <>{fallback}</>;

  return (
    <img
      {...imageProps}
      ref={setImageRef}
      src={shown.src || undefined}
      style={shown.status === "loaded" ? style : { ...style, opacity: 0 }}
      onLoad={(event) => {
        const loadedSrc = shown.src;
        setShown((current) =>
          current.src === loadedSrc && current.status !== "loaded" ? { ...current, status: "loaded" } : current,
        );
        onLoad?.(event);
      }}
      onError={(event) => {
        const failedSrc = shown.src;
        setShown((current) => (current.src === failedSrc ? { ...current, status: "failed" } : current));
        onError?.(event);
      }}
    />
  );
}
