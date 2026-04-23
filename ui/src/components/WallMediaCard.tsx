import type { HTMLAttributes, ReactNode } from "react";

interface WallMediaCardProps extends HTMLAttributes<HTMLDivElement> {
  title: string;
  imageSrc?: string | null;
  aspectRatio?: string;
  fallback?: ReactNode;
  imageClassName?: string;
}

export function WallMediaCard({
  title,
  imageSrc,
  aspectRatio = "1 / 1",
  fallback,
  imageClassName = "object-cover",
  className,
  children,
  ...props
}: WallMediaCardProps) {
  return (
    <div
      {...props}
      className={`cursor-pointer rounded overflow-hidden border border-border hover:border-accent/60 transition-all ${className ?? ""}`.trim()}
      title={title}
    >
      <div className="relative w-full bg-surface" style={{ aspectRatio }}>
        {imageSrc ? (
          <img
            src={imageSrc}
            alt={title}
            className={`absolute inset-0 h-full w-full ${imageClassName}`}
            loading="lazy"
          />
        ) : (
          <div className="absolute inset-0 flex items-center justify-center">
            {fallback}
          </div>
        )}
        {children}
      </div>
    </div>
  );
}