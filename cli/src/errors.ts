export interface ErrorBody {
  code: string;
  message: string;
  status?: number;
  details?: unknown;
}

export class CliError extends Error {
  readonly code: string;
  readonly status?: number;
  readonly details?: unknown;

  constructor(code: string, message: string, options?: { status?: number; details?: unknown }) {
    super(message);
    this.name = "CliError";
    this.code = code;
    this.status = options?.status;
    this.details = options?.details;
  }

  toJSON(): ErrorBody {
    return {
      code: this.code,
      message: this.message,
      ...(this.status === undefined ? {} : { status: this.status }),
      ...(this.details === undefined ? {} : { details: this.details }),
    };
  }
}

export function toCliError(error: unknown): CliError {
  if (error instanceof CliError) return error;
  if (error instanceof Error && error.name === "AbortError") {
    return new CliError("REQUEST_TIMEOUT", "The Cove server did not respond before the request timed out.");
  }
  if (error instanceof Error) return new CliError("UNEXPECTED_ERROR", error.message);
  return new CliError("UNEXPECTED_ERROR", "An unexpected error occurred.");
}
