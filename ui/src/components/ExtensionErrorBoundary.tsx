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
  resetKey: unknown;
}

/**
 * Error boundary that catches errors in extension-rendered components.
 * Prevents a crashing extension from taking down the host page.
 */
export class ExtensionErrorBoundary extends Component<Props, State> {
  state: State = { hasError: false, error: null, resetKey: this.props.resetKey };

  static getDerivedStateFromError(error: Error): Partial<State> {
    return { hasError: true, error };
  }

  // A new resetKey clears a caught error before rendering, so the children get another try without
  // first rendering the fallback for the new key.
  static getDerivedStateFromProps(props: Props, state: State): Partial<State> | null {
    if (Object.is(props.resetKey, state.resetKey)) return null;
    return { hasError: false, error: null, resetKey: props.resetKey };
  }

  componentDidCatch(error: Error, errorInfo: ErrorInfo) {
    console.error(
      `[Extension${this.props.extensionId ? ` ${this.props.extensionId}` : ""}] Component error:`,
      error,
      errorInfo.componentStack,
    );
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
