export type ServerAvailability = "available" | "unavailable" | "reconnecting";

export const API_REQUEST_TIMEOUT_MS = 15_000;
const SERVER_PROBE_TIMEOUT_MS = 4_000;

export interface ServerAwareFetchOptions extends RequestInit {
  timeoutMs?: number | null;
}

let availability: ServerAvailability = "available";
const listeners = new Set<() => void>();
let serverEvidenceVersion = 0;
let connectionFailureConfirmation: { version: number; promise: Promise<void> } | null = null;

function setAvailability(next: ServerAvailability) {
  if (availability === next) return;
  availability = next;
  listeners.forEach((listener) => listener());
}

export function getServerAvailability(): ServerAvailability {
  return availability;
}

export function subscribeToServerAvailability(listener: () => void): () => void {
  listeners.add(listener);
  return () => listeners.delete(listener);
}

export function reportServerResponse(response: Response): void {
  serverEvidenceVersion += 1;
  // A real response invalidates any older in-flight confirmation. If another
  // connection failure arrives after this evidence, it must start a fresh
  // status check rather than reusing the stale one.
  connectionFailureConfirmation = null;
  if ([502, 504].includes(response.status)) {
    setAvailability("unavailable");
    return;
  }

  // Any other HTTP response proves that the API server is reachable, even if
  // the particular request was unauthorized, invalid, or failed internally.
  setAvailability("available");
}

async function fetchServerStatus(): Promise<Response> {
  const controller = new AbortController();
  const timeoutId = window.setTimeout(() => {
    controller.abort(new DOMException(`Server status check timed out after ${SERVER_PROBE_TIMEOUT_MS} ms.`, "TimeoutError"));
  }, SERVER_PROBE_TIMEOUT_MS);

  try {
    // Do not use serverAwareFetch here: this probe is what decides whether a
    // connection failure should change the global server state.
    return await fetch("/api/system/status", { cache: "no-store", signal: controller.signal });
  } finally {
    window.clearTimeout(timeoutId);
  }
}

function confirmServerConnectionFailure(): Promise<void> {
  serverEvidenceVersion += 1;
  if (connectionFailureConfirmation) {
    connectionFailureConfirmation.version = serverEvidenceVersion;
    return connectionFailureConfirmation.promise;
  }

  const confirmation = { version: serverEvidenceVersion, promise: Promise.resolve() };
  connectionFailureConfirmation = confirmation;
  confirmation.promise = fetchServerStatus()
    .then((response) => {
      if (confirmation.version === serverEvidenceVersion) {
        reportServerResponse(response);
      }
    })
    .catch(() => {
      if (confirmation.version === serverEvidenceVersion) {
        setAvailability("unavailable");
      }
    })
    .finally(() => {
      if (connectionFailureConfirmation === confirmation) {
        connectionFailureConfirmation = null;
      }
    });

  return confirmation.promise;
}

export function reportServerConnectionFailure(): void {
  // Let the request fail immediately for its caller, but only declare a
  // server-wide outage after the independent status endpoint also fails.
  void confirmServerConnectionFailure();
}

function combineAbortSignals(signals: AbortSignal[]): AbortSignal | undefined {
  if (signals.length === 0) return undefined;
  if (signals.length === 1) return signals[0];

  if (typeof AbortSignal.any === "function") {
    return AbortSignal.any(signals);
  }

  const controller = new AbortController();
  signals.forEach((signal) => {
    const abort = () => controller.abort(signal.reason);
    if (signal.aborted) abort();
    else signal.addEventListener("abort", abort, { once: true });
  });
  return controller.signal;
}

export async function serverAwareFetch(input: RequestInfo | URL, options: ServerAwareFetchOptions = {}): Promise<Response> {
  const { timeoutMs = API_REQUEST_TIMEOUT_MS, signal: callerSignal, ...init } = options;
  const requestSignal = input instanceof Request ? input.signal : null;
  const callerSignals = [requestSignal, callerSignal].filter((signal): signal is AbortSignal => signal != null);
  const requestController = new AbortController();
  if (timeoutMs != null) {
    window.setTimeout(() => {
      requestController.abort(new DOMException(`API request timed out after ${timeoutMs} ms.`, "TimeoutError"));
    }, timeoutMs);
  }
  const signal = combineAbortSignals([
    ...callerSignals,
    requestController.signal,
  ]);

  try {
    const response = await fetch(input, { ...init, signal });
    if ([502, 504].includes(response.status)) {
      reportServerConnectionFailure();
    } else {
      reportServerResponse(response);
    }
    return response;
  } catch (error) {
    if (!callerSignals.some((candidate) => candidate.aborted)) {
      reportServerConnectionFailure();
    }
    throw error;
  }
}

export function beginServerReconnect(): void {
  setAvailability("reconnecting");
}

export async function runServerProbe({ showReconnecting = true }: { showReconnecting?: boolean } = {}): Promise<boolean> {
  serverEvidenceVersion += 1;
  const probeVersion = serverEvidenceVersion;
  if (showReconnecting) {
    beginServerReconnect();
  }
  try {
    const response = await fetchServerStatus();
    if (probeVersion === serverEvidenceVersion) {
      reportServerResponse(response);
    }
    return getServerAvailability() === "available";
  } catch {
    if (probeVersion === serverEvidenceVersion) {
      setAvailability("unavailable");
    }
    return false;
  }
}

export function resetServerAvailabilityForTests(): void {
  serverEvidenceVersion += 1;
  connectionFailureConfirmation = null;
  setAvailability("available");
}
