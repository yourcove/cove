export function withBase(path: string) {
  const base = import.meta.env.BASE_URL;
  const normalized = path.replace(/^\/+/, '');

  if (normalized.length === 0) {
    return base;
  }

  return `${base}${normalized}`;
}
