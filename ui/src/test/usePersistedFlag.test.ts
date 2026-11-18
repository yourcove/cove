import { act, renderHook } from "@testing-library/react";
import { afterEach, describe, expect, it } from "vitest";
import { usePersistedFlag } from "../hooks/usePersistedFlag";

const KEY = "test.persistedFlag";

afterEach(() => {
  window.localStorage.removeItem(KEY);
});

describe("usePersistedFlag", () => {
  it("follows a changing default until the viewer sets it, then keeps their choice", () => {
    const { result, rerender } = renderHook(({ fallback }) => usePersistedFlag(KEY, fallback), {
      initialProps: { fallback: false },
    });
    expect(result.current[0]).toBe(false);

    // For example once a video's faces have loaded and some of them have names.
    rerender({ fallback: true });
    expect(result.current[0]).toBe(true);

    act(() => result.current[1]((value) => !value));
    expect(result.current[0]).toBe(false);
    expect(window.localStorage.getItem(KEY)).toBe("false");

    rerender({ fallback: true });
    expect(result.current[0]).toBe(false);
  });

  it("starts from a choice stored earlier", () => {
    window.localStorage.setItem(KEY, "true");
    const { result } = renderHook(() => usePersistedFlag(KEY, false));
    expect(result.current[0]).toBe(true);
  });
});
