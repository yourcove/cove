import type { ReactNode } from "react";
import type { QueryLoadState } from "../utils/queryLoadState";
import { ListLoadError } from "./ListLoadError";

interface QueryStateProps<TData> {
  state: QueryLoadState<TData>;
  loading: ReactNode;
  empty?: ReactNode;
  children: ReactNode | ((data: TData) => ReactNode);
  errorTitle?: string;
  errorClassName?: string;
}

function renderContent<TData>(children: QueryStateProps<TData>["children"], data: TData) {
  return typeof children === "function" ? children(data) : children;
}

export function QueryState<TData>({
  state,
  loading,
  empty,
  children,
  errorTitle,
  errorClassName,
}: QueryStateProps<TData>) {
  if (state.status === "pending") return <>{loading}</>;
  if (state.status === "error") {
    return <ListLoadError error={state.error} onRetry={state.retry} title={errorTitle} className={errorClassName} />;
  }
  if (state.status === "empty" && empty !== undefined) return <>{empty}</>;
  return <>{renderContent(children, state.data)}</>;
}
