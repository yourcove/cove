export function videoEditClearFields(date: string, studioId: number | undefined): string[] {
  return [
    !date && "date",
    studioId === undefined && "studioId",
  ].filter((field): field is string => Boolean(field));
}
