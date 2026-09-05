import { useRef, useState, useCallback, useEffect } from "react";
import { useMutation } from "@tanstack/react-query";
import { Upload, X, Image as ImageIcon, Link, Clipboard } from "lucide-react";

interface ImageInputProps {
  currentImageUrl?: string;
  onUpload: (file: File) => Promise<unknown>;
  onDelete?: () => Promise<unknown>;
  onSuccess?: () => void;
  label?: string;
  className?: string;
  aspectRatio?: string; // e.g. "2/3", "16/9", "1/1"
  objectFit?: "cover" | "contain";
  deleteLabel?: string;
  disabled?: boolean;
  onBusyChange?: (busy: boolean) => void;
}

export function ImageInput({
  currentImageUrl,
  onUpload,
  onDelete,
  onSuccess,
  label = "Image",
  className = "",
  aspectRatio = "2/3",
  objectFit = "cover",
  deleteLabel = "Remove",
  disabled = false,
  onBusyChange,
}: ImageInputProps) {
  const fileRef = useRef<HTMLInputElement>(null);
  const [preview, setPreview] = useState<string | null>(null);
  const [showUrlInput, setShowUrlInput] = useState(false);
  const [urlValue, setUrlValue] = useState("");
  const [imgError, setImgError] = useState(false);
  const [inputError, setInputError] = useState<string | null>(null);
  const [preparationPending, setPreparationPending] = useState(false);

  useEffect(
    () => () => {
      if (preview?.startsWith("blob:")) URL.revokeObjectURL(preview);
    },
    [preview],
  );

  useEffect(() => {
    setImgError(false);
  }, [currentImageUrl]);

  const uploadMut = useMutation({
    meta: { suppressGlobalError: true },
    mutationFn: onUpload,
    onSuccess: () => {
      setPreview(null);
      setInputError(null);
      onSuccess?.();
    },
    onError: () => setPreview(null),
  });

  const deleteMut = useMutation({
    meta: { suppressGlobalError: true },
    mutationFn: onDelete ?? (() => Promise.resolve()),
    onSuccess: () => {
      setPreview(null);
      setImgError(false);
      setInputError(null);
      onSuccess?.();
    },
  });

  const isLoading = preparationPending || uploadMut.isPending || deleteMut.isPending;

  const processFile = useCallback(
    (file: File) => {
      if (!file.type.startsWith("image/")) {
        setInputError("Choose a valid image file.");
        return;
      }
      setInputError(null);
      const url = URL.createObjectURL(file);
      setPreview(url);
      setImgError(false);
      uploadMut.mutate(file);
    },
    [uploadMut],
  );

  const handleFile = useCallback(
    (file: File) => {
      if (disabled || isLoading) return;
      processFile(file);
    },
    [disabled, isLoading, processFile],
  );

  const handleFileChange = (e: React.ChangeEvent<HTMLInputElement>) => {
    const file = e.target.files?.[0];
    if (file) handleFile(file);
    e.target.value = "";
  };

  const handleDrop = useCallback(
    (e: React.DragEvent) => {
      e.preventDefault();
      const file = e.dataTransfer.files[0];
      if (file) handleFile(file);
    },
    [handleFile],
  );

  const handlePaste = useCallback(async () => {
    if (disabled || isLoading) return;
    setPreparationPending(true);
    try {
      const items = await navigator.clipboard.read();
      for (const item of items) {
        const imageType = item.types.find((t) => t.startsWith("image/"));
        if (imageType) {
          const blob = await item.getType(imageType);
          const file = new File([blob], "clipboard.png", { type: imageType });
          processFile(file);
          return;
        }
      }
      setInputError("The clipboard does not contain an image.");
    } catch (error) {
      setInputError(error instanceof Error ? error.message : "Unable to read an image from the clipboard.");
    } finally {
      setPreparationPending(false);
    }
  }, [disabled, isLoading, processFile]);

  const handleUrlSubmit = useCallback(async () => {
    if (!urlValue.trim() || disabled || isLoading) return;
    setPreparationPending(true);
    try {
      const res = await fetch(urlValue.trim());
      if (!res.ok) throw new Error(`Image request failed (${res.status}).`);
      const blob = await res.blob();
      if (!blob.type.startsWith("image/")) throw new Error("The URL did not return an image.");
      const ext = blob.type.split("/")[1] || "jpg";
      const file = new File([blob], `url-image.${ext}`, { type: blob.type });
      processFile(file);
      setShowUrlInput(false);
      setUrlValue("");
    } catch (error) {
      setInputError(error instanceof Error ? error.message : "Unable to load the image URL.");
    } finally {
      setPreparationPending(false);
    }
  }, [disabled, isLoading, processFile, urlValue]);

  const displayUrl = preview || (currentImageUrl && !imgError ? currentImageUrl : null);

  useEffect(() => onBusyChange?.(isLoading), [isLoading, onBusyChange]);

  return (
    <div className={`space-y-2 ${className}`}>
      <label className="block text-sm font-medium text-secondary">{label}</label>

      <div
        className="relative rounded-lg overflow-hidden bg-card border border-border border-dashed hover:border-border transition-colors cursor-pointer"
        style={{ aspectRatio }}
        onDrop={handleDrop}
        onDragOver={(e) => e.preventDefault()}
        onClick={() => !isLoading && !disabled && fileRef.current?.click()}
      >
        {displayUrl ? (
          <>
            <img
              src={displayUrl}
              alt={label}
              className={`w-full h-full ${objectFit === "contain" ? "object-contain p-2" : "object-cover"}`}
              onError={() => setImgError(true)}
            />
            {isLoading && (
              <div className="absolute inset-0 bg-black/50 flex items-center justify-center">
                <div className="animate-spin rounded-full h-8 w-8 border-b-2 border-accent" />
              </div>
            )}
          </>
        ) : (
          <div className="flex flex-col items-center justify-center w-full h-full text-secondary p-4">
            <ImageIcon className="w-8 h-8 mb-2" />
            <span className="text-xs text-center">Click or drag to upload</span>
          </div>
        )}
      </div>

      <input
        ref={fileRef}
        type="file"
        accept="image/jpeg,image/png,image/webp,image/gif"
        onChange={handleFileChange}
        disabled={isLoading || disabled}
        className="hidden"
      />

      {/* Action buttons */}
      <div className="flex gap-1.5 flex-wrap">
        <button
          type="button"
          onClick={(e) => {
            e.stopPropagation();
            fileRef.current?.click();
          }}
          disabled={isLoading || disabled}
          className="flex items-center gap-1 px-2 py-1 text-xs bg-card hover:bg-card-hover rounded transition-colors disabled:opacity-50"
          title="Upload from file"
        >
          <Upload className="w-3 h-3" /> File
        </button>
        <button
          type="button"
          onClick={(e) => {
            e.stopPropagation();
            setShowUrlInput(!showUrlInput);
          }}
          disabled={isLoading || disabled}
          className="flex items-center gap-1 px-2 py-1 text-xs bg-card hover:bg-card-hover rounded transition-colors disabled:opacity-50"
          title="Upload from URL"
        >
          <Link className="w-3 h-3" /> URL
        </button>
        <button
          type="button"
          onClick={(e) => {
            e.stopPropagation();
            handlePaste();
          }}
          disabled={isLoading || disabled}
          className="flex items-center gap-1 px-2 py-1 text-xs bg-card hover:bg-card-hover rounded transition-colors disabled:opacity-50"
          title="Paste from clipboard"
        >
          <Clipboard className="w-3 h-3" /> Paste
        </button>
        {onDelete && displayUrl && (
          <button
            type="button"
            onClick={(e) => {
              e.stopPropagation();
              deleteMut.mutate();
            }}
            disabled={isLoading || disabled}
            className="flex items-center gap-1 px-2 py-1 text-xs bg-red-900/50 hover:bg-red-800/50 text-red-400 rounded transition-colors disabled:opacity-50 ml-auto"
            title="Remove image"
          >
            <X className="w-3 h-3" /> {deleteLabel}
          </button>
        )}
      </div>

      {/* URL input */}
      {showUrlInput && (
        <div className="flex gap-2">
          <input
            type="url"
            disabled={isLoading || disabled}
            value={urlValue}
            onChange={(e) => setUrlValue(e.target.value)}
            placeholder="https://..."
            className="flex-1 bg-card border border-border rounded px-2 py-1 text-xs text-foreground focus:outline-none focus:border-accent"
            onKeyDown={(e) => e.key === "Enter" && handleUrlSubmit()}
          />
          <button
            type="button"
            onClick={handleUrlSubmit}
            disabled={isLoading || disabled}
            className="px-2 py-1 text-xs bg-accent hover:bg-accent-hover rounded transition-colors"
          >
            Load
          </button>
        </div>
      )}

      {(uploadMut.error || deleteMut.error || inputError) && (
        <p role="alert" className="text-xs text-red-400">
          {inputError ?? ((uploadMut.error || deleteMut.error) as Error).message}
        </p>
      )}
    </div>
  );
}
