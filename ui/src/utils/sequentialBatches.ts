export interface SequentialBatchProgress {
  completed: number;
  total: number;
}

interface SequentialBatchOptions {
  batchSize: number;
  startIndex?: number;
  onProgress?: (progress: SequentialBatchProgress) => void;
}

/** Runs bounded batches one at a time and only advances progress after a batch succeeds. */
export async function runSequentialBatches<T>(
  items: readonly T[],
  executeBatch: (batch: T[]) => Promise<unknown>,
  options: SequentialBatchOptions,
): Promise<void> {
  if (!Number.isInteger(options.batchSize) || options.batchSize <= 0) {
    throw new Error("Batch size must be a positive integer.");
  }

  const startIndex = Math.min(Math.max(0, options.startIndex ?? 0), items.length);
  for (let index = startIndex; index < items.length; index += options.batchSize) {
    const batch = items.slice(index, index + options.batchSize);
    await executeBatch(batch);
    options.onProgress?.({
      completed: Math.min(index + batch.length, items.length),
      total: items.length,
    });
  }
}
