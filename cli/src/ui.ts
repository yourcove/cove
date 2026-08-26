import type { Command, HelpConfiguration, Option } from "commander";
import { Help } from "commander";
import pc from "picocolors";

export interface UiPalette {
  accent: (value: string) => string;
  brand: (value: string) => string;
  bold: (value: string) => string;
  dim: (value: string) => string;
  error: (value: string) => string;
  success: (value: string) => string;
  warning: (value: string) => string;
}

export type UiColor = boolean | string;

export const DEFAULT_ACCENT = "#4f8ff7";

export function uiPalette(color: UiColor): UiPalette {
  const enabled = color !== false;
  const colors = pc.createColors(enabled);
  const accent = typeof color === "string" ? color : DEFAULT_ACCENT;
  return {
    accent: value => colorizeHex(value, accent, color),
    brand: value => colorizeHex(value, accent, color),
    bold: colors.bold,
    dim: colors.dim,
    error: colors.red,
    success: colors.green,
    warning: colors.yellow,
  };
}

export function terminalRgb(value: string | null | undefined): [number, number, number] | undefined {
  const source = value?.trim() ?? "";
  const hex = /^#([0-9a-f]{3,4}|[0-9a-f]{6}|[0-9a-f]{8})$/i.exec(source)?.[1];
  if (hex) {
    const channels = hex.length <= 4
      ? [...hex.slice(0, 3)].map(channel => Number.parseInt(`${channel}${channel}`, 16))
      : [hex.slice(0, 2), hex.slice(2, 4), hex.slice(4, 6)].map(channel => Number.parseInt(channel, 16));
    return channels as [number, number, number];
  }

  const rgb = /^rgba?\((.*)\)$/i.exec(source)?.[1];
  if (!rgb) return undefined;
  const commaSeparated = rgb.includes(",");
  const channelSource = rgb.split("/")[0] ?? "";
  const channels = (commaSeparated ? channelSource.split(",") : channelSource.trim().split(/\s+/)).slice(0, 3);
  if (channels.length !== 3) return undefined;
  const parsed = channels.map(channel => {
    const normalized = channel.trim();
    const percentage = normalized.endsWith("%");
    const number = Number.parseFloat(percentage ? normalized.slice(0, -1) : normalized);
    const maximum = percentage ? 100 : 255;
    if (!Number.isFinite(number) || number < 0 || number > maximum) return undefined;
    return Math.round(percentage ? number * 2.55 : number);
  });
  return parsed.every(channel => channel !== undefined) ? parsed as [number, number, number] : undefined;
}

export function colorizeHex(value: string, hex: string | null | undefined, color: UiColor): string {
  const channels = terminalRgb(hex);
  if (color === false || !channels) return value;
  const [red, green, blue] = channels;
  return `\u001b[38;2;${red};${green};${blue}m${value}\u001b[39m`;
}

export function renderCoveWordmark(color: UiColor): string {
  const paint = uiPalette(color);
  return `${paint.brand(paint.bold("COVE"))}  ${paint.dim("CLI")}`;
}

export function terminalColorsEnabled(requested = true, stream: NodeJS.WriteStream = process.stdout): boolean {
  return requested && !!stream.isTTY && process.env.NO_COLOR === undefined && process.env.TERM !== "dumb";
}

export function terminalHyperlinksEnabled(stream: NodeJS.WriteStream = process.stdout): boolean {
  return !!stream.isTTY && process.env.TERM !== "dumb";
}

export function stripTerminalSequences(value: string): string {
  return value.replace(/\u001b(?:\[[0-?]*[ -/]*[@-~]|][^\u0007]*(?:\u0007|\u001b\\)?)/g, "");
}

export function cleanInline(value: string): string {
  return stripTerminalSequences(value)
    .replace(/[\r\n\t]+/g, " ")
    .replace(/[\u0000-\u001f\u007f-\u009f]/g, "")
    .trim();
}

export function configureCliHelp(root: Command, color: UiColor): void {
  const paint = uiPalette(color);
  const base = new Help();
  const groupOrder = new Map([
    ["Explore:", 0],
    ["Catalog:", 1],
    ["Account:", 2],
    ["Help:", 3],
  ]);
  const exploreOrder = new Map([
    ["search", 0],
    ["similar", 1],
    ["videos", 2],
    ["images", 3],
    ["audios", 4],
    ["texts", 5],
    ["galleries", 6],
    ["segments", 7],
    ["performers", 8],
    ["tags", 9],
    ["groups", 10],
    ["studios", 11],
  ]);
  (root.commands as Command[]).sort((left: Command, right: Command) => {
    const leftRank = groupOrder.get(left.helpGroup()) ?? 99;
    const rightRank = groupOrder.get(right.helpGroup()) ?? 99;
    if (leftRank !== rightRank) return leftRank - rightRank;
    if (left.helpGroup() !== "Explore:") return 0;
    return (exploreOrder.get(left.name()) ?? 99) - (exploreOrder.get(right.name()) ?? 99);
  });
  const shared: HelpConfiguration = {
    formatHelp: (command, helper) => {
      const rendered = base.formatHelp(command, helper);
      return command === root ? `${renderCoveWordmark(color)}\n\n${rendered}` : rendered;
    },
    optionDescription: (option: Option) => option.description,
    styleTitle: value => paint.bold(value),
    styleCommandText: value => paint.bold(value),
    styleSubcommandText: value => paint.accent(value),
    styleOptionText: value => paint.accent(value),
    styleArgumentText: value => paint.accent(value),
    visibleCommands: command => {
      const visible = base.visibleCommands(command);
      return command === root ? [...visible] : visible;
    },
  };

  const apply = (command: Command): void => {
    command.configureOutput({ getOutHasColors: () => color !== false, getErrHasColors: () => color !== false });
    command.configureHelp({ ...shared, showGlobalOptions: !!command.parent });
    command.commands.forEach(apply);
  };
  apply(root);
}
