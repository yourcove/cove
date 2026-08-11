import type { ReactNode } from "react";
import { ListLoadError } from "./ListLoadError";

interface ListQueryStateProps {
  header?: ReactNode;
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
  header,
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
  let content = children;
  if (isLoading) content = loading;
  else if (loadError) content = <ListLoadError error={loadError} onRetry={onRetry} title={errorTitle} className={errorClassName} />;
  else if (isEmpty) content = empty;

  return <>{header}{content}</>;
}
