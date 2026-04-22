import { useQuery, useMutation, useQueryClient } from "@tanstack/react-query";
import { images } from "../api/client";
import type { Image as ImageModel } from "../api/types";
import { formatDate, TagBadge, CustomFieldsDisplay } from "../components/shared";
import { ArrowLeft, Pencil, Trash2, Link as LinkIcon, Heart, Check, Maximize, X } from "lucide-react";
import { useEffect, useState, useCallback } from "react";
import { ConfirmDialog } from "../components/ConfirmDialog";
import { ImageEditModal } from "./ImageEditModal";
import { ExtensionSlot } from "../router/RouteRegistry";
import { InteractiveRating } from "../components/Rating";
import { createCardNavigationHandlers } from "../components/cardNavigation";
import { getImageDisplayTitle } from "../utils/imageDisplay";
import { useBackNavigation } from "../hooks/useBackNavigation";

interface Props {
  id: number;
  onNavigate: (r: any) => void;
}

export function ImageDetailPage({ id, onNavigate }: Props) {
  const { data: image, isLoading } = useQuery({
    queryKey: ["image", id],
    queryFn: () => images.get(id),
  });
  const [editing, setEditing] = useState(false);
  const [confirmDelete, setConfirmDelete] = useState(false);
  const [lightboxOpen, setLightboxOpen] = useState(false);
  const queryClient = useQueryClient();
  const { backLabel, goBack } = useBackNavigation({ page: "images" }, onNavigate);
  const deleteMut = useMutation({
    mutationFn: () => images.delete(id),
    onSuccess: () => { queryClient.invalidateQueries({ queryKey: ["images"] }); goBack(); },
  });
  const updateMut = useMutation({
    mutationFn: (data: { organized?: boolean; rating?: number }) => images.update(id, data),
    onSuccess: () => queryClient.invalidateQueries({ queryKey: ["image", id] }),
  });
  const incrementOMut = useMutation({
    mutationFn: () => images.incrementO(id),
    onSuccess: (newCount) => {
      queryClient.setQueryData<ImageModel>(["image", id], (current) => current ? { ...current, oCounter: newCount } : current);
      queryClient.invalidateQueries({ queryKey: ["images"] });
    },
  });
  const displayTitle = image ? getImageDisplayTitle(image) : `Image ${id}`;

  useEffect(() => {
    if (image) document.title = `${displayTitle} | Cove`;
    return () => { document.title = "Cove"; };
  }, [displayTitle, image]);

  // Keyboard shortcuts
  useEffect(() => {
    const handler = (e: KeyboardEvent) => {
      const el = (e.target as HTMLElement).tagName;
      if (el === "INPUT" || el === "TEXTAREA" || el === "SELECT") return;
      switch (e.key) {
        case "e": setEditing((v) => !v); break;
        case "o": incrementOMut.mutate(); break;
        case "f": setLightboxOpen((v) => !v); break;
        case "Escape": setLightboxOpen(false); break;
      }
    };
    window.addEventListener("keydown", handler);
    return () => window.removeEventListener("keydown", handler);
  }, []);

  if (isLoading) {
    return (
      <div className="flex items-center justify-center h-64">
        <div className="animate-spin rounded-full h-8 w-8 border-b-2 border-accent" />
      </div>
    );
  }

  if (!image) return <div className="text-center text-secondary py-16">Image not found</div>;

  return (
    <div className="overflow-hidden">
      {image && <ImageEditModal image={image} open={editing} onClose={() => setEditing(false)} />}
      <ConfirmDialog open={confirmDelete} title="Delete Image" message={`Delete "${displayTitle}"? This cannot be undone.`} onConfirm={() => deleteMut.mutate()} onCancel={() => setConfirmDelete(false)} />

      {/* Lightbox overlay */}
      {lightboxOpen && (
        <div className="fixed inset-0 z-50 bg-black flex items-center justify-center" onClick={() => setLightboxOpen(false)} onKeyDown={(e) => { if (e.key === "Escape") setLightboxOpen(false); }} tabIndex={0} ref={(el) => el?.focus()}>
          <img
            src={images.imageUrl(id)}
            alt={image.title || "Image"}
            className="w-[95vw] h-[95vh] object-contain"
          />
          <button
            onClick={(e) => { e.stopPropagation(); setLightboxOpen(false); }}
            className="absolute top-4 right-4 p-2 bg-black/60 text-white rounded hover:bg-black/80"
            title="Close (Esc)"
          >
            <X className="w-6 h-6" />
          </button>
        </div>
      )}

      {/* Side-by-side layout: image left, metadata right */}
      <div className="image-detail-layout">
        {/* Image viewer — fills available space */}
        <div className="image-detail-viewer flex-1 min-w-0 min-h-[50vh] bg-black/90 flex items-center justify-center relative group">
          {/* Floating back button */}
          <button
            onClick={(e) => { e.stopPropagation(); goBack(); }}
            className="absolute top-3 left-3 z-10 flex items-center gap-1.5 px-3 py-1.5 text-sm bg-black/60 text-white hover:bg-black/80 rounded-lg backdrop-blur-sm transition-opacity opacity-0 group-hover:opacity-100"
          >
            <ArrowLeft className="w-4 h-4" /> {backLabel}
          </button>
          <img
            src={images.imageUrl(id)}
            alt={displayTitle}
            className="image-detail-primary-image max-h-[calc(100vh-64px)] max-w-full object-contain select-none cursor-zoom-in"
            onClick={() => setLightboxOpen(true)}
            onError={(e) => {
              (e.target as HTMLImageElement).style.display = "none";
              (e.target as HTMLImageElement).parentElement!.innerHTML = '<div class="flex items-center justify-center h-64"><span class="text-muted">Image unavailable</span></div>';
            }}
          />
          <button
            onClick={(e) => { e.stopPropagation(); setLightboxOpen(true); }}
            className="absolute top-3 right-3 p-2 bg-black/60 text-white rounded opacity-0 group-hover:opacity-100 transition-opacity hover:bg-black/80"
            title="View fullscreen (F)"
          >
            <Maximize className="w-5 h-5" />
          </button>
        </div>

        {/* Right sidebar — all metadata */}
        <aside className="image-detail-sidebar shrink-0 bg-surface overflow-y-auto">
          <div className="p-4 space-y-4 divide-y divide-border [&>*:not(:first-child)]:pt-4">
            {/* Title + action buttons */}
            <div>
              <h1 className="text-lg font-bold text-foreground break-words">{displayTitle}</h1>
              <div className="flex flex-wrap items-center gap-2 text-sm text-secondary mt-1">
                {image.date && <span>{formatDate(image.date)}</span>}
                {image.studioName && image.studioId && <button onClick={() => onNavigate({ page: "studio", id: image.studioId })} className="text-accent hover:underline">{image.studioName}</button>}
                {image.photographer && <span>Photo: {image.photographer}</span>}
              </div>
              <div className="flex items-center gap-2 mt-3">
                <ExtensionSlot slot="image-detail-actions" context={{ image, onNavigate }} />
                <button
                  onClick={() => setEditing(true)}
                  className="flex items-center gap-1.5 px-3 py-1.5 text-sm bg-accent text-white hover:bg-accent-hover rounded"
                >
                  <Pencil className="w-3.5 h-3.5" /> Edit
                </button>
                <button
                  onClick={() => setConfirmDelete(true)}
                  className="flex items-center gap-1.5 px-3 py-1.5 text-sm bg-card border border-border text-secondary hover:text-red-300 hover:border-red-500 rounded"
                >
                  <Trash2 className="w-3.5 h-3.5" /> Delete
                </button>
              </div>
            </div>

            {/* Rating + Favorites + Organized */}
            <div className="space-y-2">
              <InteractiveRating value={image.rating} onChange={(value) => updateMut.mutate({ rating: value })} />
              <div className="flex items-center gap-3">
                <button
                  onClick={() => incrementOMut.mutate()}
                  className="flex items-center gap-1 text-sm text-secondary hover:text-accent"
                  title="Add favorite"
                >
                  <Heart className={`w-4 h-4 ${image.oCounter > 0 ? "fill-accent text-accent" : ""}`} />
                  <span>{image.oCounter}</span>
                </button>
                <button
                  onClick={() => updateMut.mutate({ organized: !image.organized })}
                  className={`p-1.5 rounded transition-colors ${image.organized ? "bg-green-600 text-white" : "bg-card text-muted hover:text-foreground border border-border"}`}
                  title={image.organized ? "Organized" : "Not organized"}
                >
                  <Check className="w-4 h-4" />
                </button>
              </div>
            </div>

            {/* Description */}
            {image.details && (
              <p className="text-sm text-secondary whitespace-pre-wrap leading-relaxed">{image.details}</p>
            )}

            {/* Performers */}
            {image.performers.length > 0 && (
              <div>
                <h3 className="text-xs font-semibold uppercase tracking-wide text-muted mb-2">Performers</h3>
                <div className="flex flex-wrap gap-2">
                  {image.performers.map((p) => {
                    const navigationHandlers = createCardNavigationHandlers<HTMLButtonElement>({ page: "performer", id: p.id }, () => onNavigate({ page: "performer", id: p.id }));

                    return (
                      <button
                        key={p.id}
                        type="button"
                        {...navigationHandlers}
                        className="flex items-center gap-2 bg-card border border-border rounded-lg px-3 py-2 hover:border-accent/60 transition-colors"
                      >
                        <div className="w-7 h-7 rounded-full bg-surface overflow-hidden flex items-center justify-center text-xs text-muted">
                          {p.imagePath ? (
                            <img src={p.imagePath} alt="" className="w-full h-full object-cover" />
                          ) : (
                            p.name[0]
                          )}
                        </div>
                        <span className="text-sm text-foreground">{p.name}</span>
                      </button>
                    );
                  })}
                </div>
              </div>
            )}

            {/* Tags */}
            {image.tags.length > 0 && (
              <div>
                <h3 className="text-xs font-semibold uppercase tracking-wide text-muted mb-2">Tags</h3>
                <div className="flex flex-wrap gap-1.5">
                  {image.tags.map((tag) => (
                    <TagBadge key={tag.id} name={tag.name} onClick={() => onNavigate({ page: "tag", id: tag.id })} />
                  ))}
                </div>
              </div>
            )}

            {/* File Info */}
            {image.files && image.files.length > 0 && (
              <div>
                <h3 className="text-xs font-semibold uppercase tracking-wide text-muted mb-2">File Info</h3>
                {image.files.map((f) => (
                  <dl key={f.id} className="space-y-1.5 text-xs">
                    <div><dt className="text-muted">Path</dt><dd className="text-foreground font-mono text-[11px] break-all mt-0.5">{f.path}</dd></div>
                    <div className="flex justify-between"><dt className="text-muted">Dimensions</dt><dd className="text-foreground">{f.width} x {f.height}</dd></div>
                    <div className="flex justify-between"><dt className="text-muted">Format</dt><dd className="text-foreground">{f.format}</dd></div>
                    <div className="flex justify-between"><dt className="text-muted">Size</dt><dd className="text-foreground">{(f.size / 1024 / 1024).toFixed(2)} MB</dd></div>
                  </dl>
                ))}
              </div>
            )}

            {/* Metadata */}
            <div>
              <h3 className="text-xs font-semibold uppercase tracking-wide text-muted mb-2">Details</h3>
              <dl className="space-y-1.5 text-xs">
                <div className="flex justify-between"><dt className="text-muted">Favorites</dt><dd className="text-foreground">{image.oCounter}</dd></div>
                <div className="flex justify-between"><dt className="text-muted">Organized</dt><dd className="text-foreground">{image.organized ? "Yes" : "No"}</dd></div>
                <div className="flex justify-between"><dt className="text-muted">Created</dt><dd className="text-foreground">{formatDate(image.createdAt)}</dd></div>
                <div className="flex justify-between"><dt className="text-muted">Updated</dt><dd className="text-foreground">{formatDate(image.updatedAt)}</dd></div>
              </dl>
            </div>

            {/* URLs */}
            {image.urls.length > 0 && (
              <div>
                <h3 className="text-xs font-semibold uppercase tracking-wide text-muted mb-2 flex items-center gap-1.5"><LinkIcon className="w-3.5 h-3.5" /> URLs</h3>
                <div className="space-y-1">
                  {image.urls.map((url, i) => (
                    <a key={i} href={url} target="_blank" rel="noopener noreferrer"
                      className="text-accent hover:underline text-xs block truncate">{url}</a>
                  ))}
                </div>
              </div>
            )}

            <CustomFieldsDisplay customFields={image.customFields} />
            <ExtensionSlot slot="image-detail-sidebar-bottom" context={{ image, onNavigate }} />
            <ExtensionSlot slot="image-detail-main-bottom" context={{ image, onNavigate }} />
          </div>
        </aside>
      </div>
    </div>
  );
}
