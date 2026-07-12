export interface LightboxPageBounds { first: number; last: number }

export function extendLightboxPageBounds(bounds: LightboxPageBounds, page: number, direction: "previous" | "next") {
  return direction === "previous" ? { ...bounds, first: page } : { ...bounds, last: page };
}
