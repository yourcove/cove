import type { ImgHTMLAttributes } from "react";
import { Film } from "lucide-react";
import { LoadedImage } from "./LoadedImage";

interface VideoCoverImageProps extends Omit<ImgHTMLAttributes<HTMLImageElement>, "alt" | "onError" | "src"> {
  src: string;
  alt: string;
  fallbackClassName?: string;
}

export function VideoCoverImage({ src, alt, fallbackClassName = "", ...imageProps }: VideoCoverImageProps) {
  return (
    <LoadedImage
      {...imageProps}
      src={src}
      alt={alt}
      fallback={
        <div
          className={`${fallbackClassName} flex h-full w-full items-center justify-center bg-gradient-to-br from-surface to-card`.trim()}
        >
          <Film className="h-12 w-12 text-muted" aria-hidden="true" />
        </div>
      }
    />
  );
}
