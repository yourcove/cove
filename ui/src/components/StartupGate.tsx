import type { ReactNode } from "react";
import { useAppConfig } from "../state/AppConfigContext";
import { StartupConnectionScreen } from "./StartupConnectionScreen";

export function StartupGate({ children }: { children: ReactNode }) {
  const { status, statusError, statusLoading, statusRetrying, retryStatus } = useAppConfig();

  if (statusLoading) {
    return (
      <div className="flex min-h-screen items-center justify-center bg-background">
        <div className="h-8 w-8 animate-spin rounded-full border-b-2 border-accent" />
      </div>
    );
  }

  if (!status && statusError) {
    return <StartupConnectionScreen retrying={statusRetrying} onRetry={retryStatus} />;
  }

  return <>{children}</>;
}
