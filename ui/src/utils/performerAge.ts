type CalendarDate = { year: number; month: number; day: number };
type CalendarDateBounds = { earliest: CalendarDate; latest: CalendarDate };

function compareCalendarDates(left: CalendarDate, right: CalendarDate) {
  return left.year - right.year || left.month - right.month || left.day - right.day;
}

function ageOnDate(reference: CalendarDate, birth: CalendarDate) {
  let age = reference.year - birth.year;
  if (reference.month < birth.month || (reference.month === birth.month && reference.day < birth.day)) age--;
  return age;
}

function parsePartialDateBounds(value?: string): CalendarDateBounds | null {
  if (!value) return null;
  const match = /^(\d{4})(?:-(\d{2})(?:-(\d{2}))?)?$/.exec(value);
  if (!match) return null;
  const year = Number(match[1]);
  const month = match[2] ? Number(match[2]) : null;
  const day = match[3] ? Number(match[3]) : null;
  if (year < 1 || year > 9999 || (month !== null && (month < 1 || month > 12))) return null;
  const isLeapYear = year % 4 === 0 && (year % 100 !== 0 || year % 400 === 0);
  const daysInMonth = month === null ? null : [31, isLeapYear ? 29 : 28, 31, 30, 31, 30, 31, 31, 30, 31, 30, 31][month - 1];
  if (day !== null && (day < 1 || day > daysInMonth!)) return null;
  return {
    earliest: { year, month: month ?? 1, day: day ?? 1 },
    latest: { year, month: month ?? 12, day: day ?? daysInMonth ?? 31 },
  };
}

function ageForBounds(reference: CalendarDateBounds, birth: CalendarDateBounds): number | string | null {
  if (compareCalendarDates(reference.latest, birth.earliest) < 0) return null;
  const minimumAge = Math.max(0, ageOnDate(reference.earliest, birth.latest));
  const maximumAge = ageOnDate(reference.latest, birth.earliest);
  if (maximumAge < 0) return null;
  return minimumAge === maximumAge ? minimumAge : `${minimumAge}–${maximumAge}`;
}

export function getUtcToday() {
  return new Date().toISOString().slice(0, 10);
}

export function getAgeAtDate(referenceDate?: string, birthdate?: string) {
  const reference = parsePartialDateBounds(referenceDate);
  const birth = parsePartialDateBounds(birthdate);
  return reference && birth ? ageForBounds(reference, birth) : null;
}

export function hasDeathOccurred(deathDate?: string, today = getUtcToday()) {
  const death = parsePartialDateBounds(deathDate);
  const currentDate = parsePartialDateBounds(today);
  return death != null && currentDate != null && compareCalendarDates(death.earliest, currentDate.earliest) <= 0;
}

export function getPerformerAge(birthdate?: string, deathDate?: string, today = getUtcToday()) {
  const currentDate = parsePartialDateBounds(today);
  const birth = parsePartialDateBounds(birthdate);
  const death = parsePartialDateBounds(deathDate);
  if (!currentDate || !birth || (deathDate && !death)) return null;
  if (!death || compareCalendarDates(death.earliest, currentDate.earliest) > 0) return ageForBounds(currentDate, birth);
  const effectiveDeath = {
    earliest: death.earliest,
    latest: compareCalendarDates(death.latest, currentDate.latest) <= 0 ? death.latest : currentDate.latest,
  };
  return ageForBounds(effectiveDeath, birth);
}
