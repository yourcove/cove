export function formatDate(dateStr?: string): string {
  if (!dateStr) return "";
  if (/^\d{4}(?:-\d{2})?$/.test(dateStr)) return dateStr;
  const date = new Date(dateStr);
  if (Number.isNaN(date.getTime())) return "Invalid Date";
  return dateStr.match(/^\d{4}-\d{2}-\d{2}/)?.[0] ?? date.toISOString().slice(0, 10);
}

export function formatDateTime(dateStr?: string): string {
  if (!dateStr) return "";
  const date = new Date(dateStr);
  if (Number.isNaN(date.getTime())) return "Invalid Date";
  return `${date.toISOString().slice(0, 19).replace("T", " ")} UTC`;
}
