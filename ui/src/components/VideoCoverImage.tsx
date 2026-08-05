import { useEffect, useState, type ImgHTMLAttributes } from "react";
import { Film } from "lucide-react";

interface VideoCoverImageProps extends Omit<ImgHTMLAttributes<HTMLImageElement>, "alt" | "onError" | "src"> {
  src: string;
  alt: string;
  fallbackClassName?: string;
}

export function VideoCoverImage({ src, alt, className, fallbackClassName = "", ...imageProps }: VideoCoverImageProps) {
  const [failed, setFailed] = useState(false);

  useEffect(() => {
    setFailed(false);
  }, [src]);

  if (failed) {
    return (
      <div className={`${fallbackClassName} flex h-full w-full items-center justify-center bg-gradient-to-br from-surface to-card`.trim()}>
        <Film className="h-12 w-12 text-muted" aria-hidden="true" />
      </div>
    );
  }

  return <img {...imageProps} src={src} alt={alt} className={className} onError={() => setFailed(true)} />;
}
