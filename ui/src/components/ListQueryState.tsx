import type { ReactNode } from "react";
import { ListLoadError } from "./ListLoadError";

interface ListQueryStateProps {
  isLoading: boolean;
  loadError: Error | null;
  isEmpty: boolean;
  loading: ReactNode;
  empty: ReactNode;
  children: ReactNode;
  onRetry?: () => void;
  errorTitle?: string;
  errorClassName?: string;
}

export function ListQueryState({
  isLoading,
  loadError,
  isEmpty,
  loading,
  empty,
  children,
  onRetry,
  errorTitle,
  errorClassName = "mt-3",
}: ListQueryStateProps) {
  if (isLoading) return <>{loading}</>;
  if (loadError) return <ListLoadError error={loadError} onRetry={onRetry} title={errorTitle} className={errorClassName} />;
  if (isEmpty) return <>{empty}</>;
  return <>{children}</>;
}
