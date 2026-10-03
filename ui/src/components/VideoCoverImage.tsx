import { useState, type ImgHTMLAttributes } from "react";
import { Film } from "lucide-react";
import { FadeInImage } from "./FadeInImage";

interface VideoCoverImageProps extends Omit<ImgHTMLAttributes<HTMLImageElement>, "alt" | "onError" | "src"> {
  src: string;
  alt: string;
  fallbackClassName?: string;
  /** Fade the cover in once it loads instead of showing it as it decodes. */
  fadeIn?: boolean;
}

export function VideoCoverImage({
  src,
  alt,
  className,
  fallbackClassName = "",
  fadeIn = false,
  ...imageProps
}: VideoCoverImageProps) {
  const [failed, setFailed] = useState(false);
  const [prevSrc, setPrevSrc] = useState(src);
  if (src !== prevSrc) {
    setPrevSrc(src);
    setFailed(false);
  }

  if (failed) {
    return (
      <div
        className={`${fallbackClassName} flex h-full w-full items-center justify-center bg-gradient-to-br from-surface to-card`.trim()}
      >
        <Film className="h-12 w-12 text-muted" aria-hidden="true" />
      </div>
    );
  }

  const Image = fadeIn ? FadeInImage : "img";
  return <Image {...imageProps} src={src} alt={alt} className={className} onError={() => setFailed(true)} />;
}
