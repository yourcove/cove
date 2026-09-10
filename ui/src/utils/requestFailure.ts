import type { ServerAvailability } from "../state/serverAvailability";

export const SERVER_UNAVAILABLE_DETAIL = "Cove can’t reach the server right now.";

function getApiErrorDetails(message: string): { status: number; detail?: string; code?: string } | null {
  const match = message.match(/^API Error (\d{3})\s*:\s*([\s\S]*)$/i);
  if (!match) return null;

  const status = Number(match[1]);
  const body = match[2].trim();
  if (!body) return { status };

  try {
    const parsed = JSON.parse(body) as unknown;
    if (parsed && typeof parsed === "object") {
      const response = parsed as { code?: unknown; message?: unknown; errors?: unknown };
      const code = typeof response.code === "string" && response.code.trim() ? response.code.trim() : undefined;
      const detail = response.message;
      if (typeof detail === "string" && detail.trim()) return { status, detail: detail.trim(), code };

      const errors = response.errors;
      if (errors && typeof errors === "object") {
        for (const fieldErrors of Object.values(errors)) {
          if (!Array.isArray(fieldErrors)) continue;
          const validationDetail = fieldErrors.find((value) => typeof value === "string" && value.trim());
          if (typeof validationDetail === "string") return { status, detail: validationDetail.trim(), code };
        }
      }

      return { status, code };
    }
  } catch {
    // Non-JSON response bodies are implementation details, so keep the generic copy below.
  }

  return { status };
}

export function getApiValidationFailureDetail(error: unknown): string {
  const message = error instanceof Error ? error.message : String(error ?? "");
  const apiError = getApiErrorDetails(message);
  if (apiError && [400, 409, 422].includes(apiError.status) && apiError.detail) return apiError.detail;
  return getRequestFailureDetail(error, "available");
}

export function getRequestFailureDetail(error: unknown, availability: ServerAvailability): string {
  if (availability !== "available") return SERVER_UNAVAILABLE_DETAIL;

  const message = error instanceof Error ? error.message : String(error ?? "");
  const apiError = getApiErrorDetails(message);
  if ((error instanceof DOMException && error.name === "TimeoutError") || /\btimed out\b/i.test(message)) {
    return "The request timed out. Please try again.";
  }
  if (error instanceof TypeError) {
    return "The request could not reach the server. Please try again.";
  }

  const status = apiError?.status ?? Number(message.match(/API Error (\d{3})\b/i)?.[1]);
  if (status === 403) return "You don’t have permission to access this information.";
  if (status === 404) return "The requested information could not be found.";
  if (status === 409 && apiError?.code === "TAG_DELETE_EXTENSION_REFERENCES" && apiError.detail) {
    return apiError.detail;
  }
  if (status >= 500) return "The server returned an error. Please try again.";
  if (Number.isFinite(status)) return "Cove couldn’t complete the request. Please try again.";
  return "Cove couldn’t complete the request. Please try again.";
}
