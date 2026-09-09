import { Component, type ErrorInfo, type ReactNode } from "react";

interface Props {
  extensionId?: string;
  fallback?: ReactNode;
  fallbackRender?: () => ReactNode;
  resetKey?: unknown;
  children: ReactNode;
}

interface State {
  hasError: boolean;
  error: Error | null;
}

/**
 * Error boundary that catches errors in extension-rendered components.
 * Prevents a crashing extension from taking down the host page.
 */
export class ExtensionErrorBoundary extends Component<Props, State> {
  state: State = { hasError: false, error: null };

  static getDerivedStateFromError(error: Error): State {
    return { hasError: true, error };
  }

  componentDidCatch(error: Error, errorInfo: ErrorInfo) {
    console.error(
      `[Extension${this.props.extensionId ? ` ${this.props.extensionId}` : ""}] Component error:`,
      error,
      errorInfo.componentStack,
    );
  }

  componentDidUpdate(previousProps: Props) {
    if (this.state.hasError && previousProps.resetKey !== this.props.resetKey) {
      this.setState({ hasError: false, error: null });
    }
  }

  render() {
    if (this.state.hasError) {
      if (this.props.fallbackRender) return this.props.fallbackRender();
      if (this.props.fallback !== undefined) return this.props.fallback;
      return (
        <div className="px-3 py-2 text-xs text-red-400 bg-red-500/10 rounded border border-red-500/20">
          Extension error{this.props.extensionId ? ` (${this.props.extensionId})` : ""}
        </div>
      );
    }
    return this.props.children;
  }
}
