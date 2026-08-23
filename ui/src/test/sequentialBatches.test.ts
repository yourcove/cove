import { describe, expect, it, vi } from "vitest";
import { runSequentialBatches } from "../utils/sequentialBatches";

describe("runSequentialBatches", () => {
  it("runs bounded batches sequentially and reports progress after success", async () => {
    const active = new Set<number>();
    const batches: number[][] = [];
    const progress: number[] = [];

    await runSequentialBatches(
      Array.from({ length: 125 }, (_, index) => index + 1),
      async (batch) => {
        active.add(batch[0]);
        expect(active.size).toBe(1);
        batches.push(batch);
        await Promise.resolve();
        active.delete(batch[0]);
      },
      { batchSize: 50, onProgress: ({ completed }) => progress.push(completed) },
    );

    expect(batches.map((batch) => batch.length)).toEqual([50, 50, 25]);
    expect(progress).toEqual([50, 100, 125]);
  });

  it("stops on failure and can retry from the last completed item", async () => {
    const ids = Array.from({ length: 120 }, (_, index) => index + 1);
    const firstAttempt = vi.fn(async (batch: number[]) => {
      if (batch[0] === 51) throw new Error("request timed out");
    });
    let completed = 0;

    await expect(runSequentialBatches(ids, firstAttempt, {
      batchSize: 50,
      onProgress: (progress) => { completed = progress.completed; },
    })).rejects.toThrow("request timed out");

    expect(completed).toBe(50);
    expect(firstAttempt.mock.calls.map(([batch]) => batch[0])).toEqual([1, 51]);

    const retried: number[][] = [];
    await runSequentialBatches(ids, async (batch) => { retried.push(batch); }, {
      batchSize: 50,
      startIndex: completed,
      onProgress: (progress) => { completed = progress.completed; },
    });

    expect(retried.map((batch) => [batch[0], batch.length])).toEqual([[51, 50], [101, 20]]);
    expect(completed).toBe(120);
  });

  it("rejects invalid batch sizes", async () => {
    await expect(runSequentialBatches([1], async () => {}, { batchSize: 0 }))
      .rejects.toThrow("positive integer");
  });
});
