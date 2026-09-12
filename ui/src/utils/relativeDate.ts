export type RelativeDatePart = { amount: number; unit: "y" | "m" | "w" | "d" | "h" };
export type RelativeDateExpression = { sign: "" | "+" | "-"; parts: RelativeDatePart[] };

export function parseRelativeDateExpression(value: string, allowHours = true): RelativeDateExpression | undefined {
  const match = /^([+-]?)((?:\d+[ymwdh])+)$/i.exec(value.trim());
  if (!match) return undefined;

  const parts: RelativeDatePart[] = [];
  const ranks = { y: 5, m: 4, w: 3, d: 2, h: 1 } as const;
  let previousRank = Number.POSITIVE_INFINITY;
  for (const partMatch of match[2].matchAll(/(\d+)([ymwdh])/gi)) {
    const amount = Number(partMatch[1]);
    const unit = partMatch[2].toLowerCase() as RelativeDatePart["unit"];
    if (
      !Number.isInteger(amount) ||
      amount > 2_147_483_647 ||
      (!allowHours && unit === "h") ||
      ranks[unit] >= previousRank
    )
      return undefined;
    parts.push({ amount, unit });
    previousRank = ranks[unit];
  }

  const sign = match[1] as RelativeDateExpression["sign"];
  if (sign === "" && (parts.length !== 1 || parts[0].amount !== 0 || parts[0].unit !== "d")) return undefined;
  return { sign, parts };
}

export function isValidRelativeDate(value: string, allowHours = false): boolean {
  return parseRelativeDateExpression(value, allowHours) !== undefined;
}

export function looksLikeRelativeDate(value: string): boolean {
  return /^[+-]|^\d+[ymwdh][\dymwdh+-]*$/i.test(value.trim());
}

/** Resolves an exact or relative filter value to an epoch timestamp. */
export function parseDateFilterValue(value: string | undefined, reference: Date, dateOnly = false): number {
  if (!value) return Number.NaN;

  const relative = parseRelativeDateExpression(value, !dateOnly);
  if (!relative) return Date.parse(value);

  const sign = relative.sign === "-" ? -1 : 1;
  const result = new Date(reference);
  if (dateOnly) result.setUTCHours(0, 0, 0, 0);

  for (const part of relative.parts) {
    const amount = sign * part.amount;
    if (part.unit === "h") {
      result.setUTCHours(result.getUTCHours() + amount);
    } else if (part.unit === "d" || part.unit === "w") {
      result.setUTCDate(result.getUTCDate() + amount * (part.unit === "w" ? 7 : 1));
    } else {
      addUtcCalendarUnits(result, amount, part.unit === "y");
    }
  }

  return result.getTime();
}

function addUtcCalendarUnits(value: Date, amount: number, years: boolean) {
  const originalDay = value.getUTCDate();
  value.setUTCDate(1);
  if (years) {
    value.setUTCFullYear(value.getUTCFullYear() + amount);
  } else {
    value.setUTCMonth(value.getUTCMonth() + amount);
  }

  const endOfTargetMonth = new Date(value);
  endOfTargetMonth.setUTCMonth(endOfTargetMonth.getUTCMonth() + 1, 0);
  value.setUTCDate(Math.min(originalDay, endOfTargetMonth.getUTCDate()));
}
