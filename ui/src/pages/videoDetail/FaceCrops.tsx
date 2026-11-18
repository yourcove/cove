import { Fingerprint } from "lucide-react";
import type { Face } from "../../api/types";
import { LoadedImage } from "../../components/LoadedImage";

/** A face's round crop, or a placeholder while it has none. */
export function FaceCrop({ face, className }: { face?: Face; className: string }) {
  return face?.coverImageUrl ? (
    <LoadedImage
      src={face.coverImageUrl}
      alt=""
      className={`${className} shrink-0 rounded-full object-cover`}
      loading="lazy"
    />
  ) : (
    <span className={`${className} flex shrink-0 items-center justify-center rounded-full bg-surface text-muted`}>
      <Fingerprint className="h-3 w-3" />
    </span>
  );
}

/** Up to three of a person's face crops, overlapping. */
export function FaceStack({
  faces,
  size = "h-5 w-5",
}: {
  faces: Array<{ faceId: number; face?: Face }>;
  size?: string;
}) {
  return (
    <span aria-hidden className="flex -space-x-1.5">
      {faces.slice(0, 3).map((entry) => (
        <FaceCrop key={entry.faceId} face={entry.face} className={`${size} ring-1 ring-card`} />
      ))}
    </span>
  );
}
