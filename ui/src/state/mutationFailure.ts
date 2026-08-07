export interface MutationFailure {
  id: number;
  error: unknown;
}

let nextFailureId = 0;
let currentFailure: MutationFailure | null = null;
const listeners = new Set<() => void>();

export function getMutationFailure(): MutationFailure | null {
  return currentFailure;
}

export function subscribeToMutationFailure(listener: () => void): () => void {
  listeners.add(listener);
  return () => listeners.delete(listener);
}

export function reportMutationFailure(error: unknown): void {
  currentFailure = {
    id: ++nextFailureId,
    error,
  };
  listeners.forEach((listener) => listener());
}

export function dismissMutationFailure(id?: number): void {
  if (!currentFailure || (id != null && currentFailure.id !== id)) return;
  currentFailure = null;
  listeners.forEach((listener) => listener());
}

export function resetMutationFailureForTests(): void {
  currentFailure = null;
  nextFailureId = 0;
  listeners.forEach((listener) => listener());
}
