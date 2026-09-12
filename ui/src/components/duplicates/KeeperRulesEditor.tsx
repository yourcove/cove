import { useState, type ReactNode } from "react";
import { ArrowDown, ArrowUp, Plus, RotateCcw, X } from "lucide-react";
import type { DuplicateKeeperRule, DuplicateKeeperRuleType } from "../../api/types";
import { CODEC_CHOICES, DEFAULT_KEEPER_RULES, KEEPER_RULES, formatCodec, ruleDefinition } from "./duplicateModel";

/**
 * Ordered keeper rules. The first rule that separates the copies decides; later rules only break ties,
 * which is the same lexicographic evaluation the server performs.
 */
export function KeeperRulesEditor({
  rules,
  onChange,
  canReadFiles,
}: {
  rules: DuplicateKeeperRule[];
  onChange: (rules: DuplicateKeeperRule[]) => void;
  canReadFiles: boolean;
}) {
  const available = KEEPER_RULES.filter(
    (definition) =>
      !rules.some((rule) => rule.type === definition.type) && (canReadFiles || !definition.requiresFilesRead),
  );

  const move = (index: number, offset: number) => {
    const target = index + offset;
    if (target < 0 || target >= rules.length) return;
    const next = [...rules];
    [next[index], next[target]] = [next[target], next[index]];
    onChange(next);
  };

  const add = (type: DuplicateKeeperRuleType) => {
    const definition = ruleDefinition(type);
    const values =
      definition?.requiresValues === "codec"
        ? ["av1", "hevc", "h264"]
        : definition?.requiresValues === "path"
          ? []
          : undefined;
    onChange([...rules, { type, values }]);
  };

  return (
    <div className="space-y-2">
      <ol className="space-y-1.5">
        {rules.map((rule, index) => {
          const definition = ruleDefinition(rule.type);
          if (!definition) return null;
          return (
            <li key={rule.type} className="rounded-lg border border-border bg-card/60 px-3 py-2">
              <div className="flex items-center gap-2">
                <span className="flex h-5 w-5 shrink-0 items-center justify-center rounded-full bg-accent/15 text-[11px] font-semibold text-accent">
                  {index + 1}
                </span>
                <div className="min-w-0 flex-1">
                  <div className="text-sm font-medium text-foreground">{definition.label}</div>
                  <div className="truncate text-xs text-muted">{definition.description}</div>
                </div>
                <div className="flex shrink-0 items-center">
                  <IconButton label="Move up" disabled={index === 0} onClick={() => move(index, -1)}>
                    <ArrowUp className="h-3.5 w-3.5" />
                  </IconButton>
                  <IconButton label="Move down" disabled={index === rules.length - 1} onClick={() => move(index, 1)}>
                    <ArrowDown className="h-3.5 w-3.5" />
                  </IconButton>
                  <IconButton
                    label={`Remove ${definition.label}`}
                    onClick={() => onChange(rules.filter((_, position) => position !== index))}
                  >
                    <X className="h-3.5 w-3.5" />
                  </IconButton>
                </div>
              </div>
              {definition.requiresValues === "codec" ? (
                <CodecPreference
                  values={rule.values ?? []}
                  onChange={(values) =>
                    onChange(rules.map((item, position) => (position === index ? { ...item, values } : item)))
                  }
                />
              ) : null}
              {definition.requiresValues === "path" ? (
                <PathPreference
                  values={rule.values ?? []}
                  onChange={(values) =>
                    onChange(rules.map((item, position) => (position === index ? { ...item, values } : item)))
                  }
                />
              ) : null}
            </li>
          );
        })}
      </ol>
      {rules.length === 0 ? (
        <p className="rounded-lg border border-dashed border-border px-3 py-4 text-center text-sm text-muted">
          Without rules, the copy Cove found first is kept.
        </p>
      ) : null}
      <div className="flex flex-wrap items-center gap-2 pt-1">
        {available.length > 0 ? (
          <label className="relative inline-flex items-center">
            <Plus className="pointer-events-none absolute left-2.5 h-3.5 w-3.5 text-muted" />
            <select
              value=""
              onChange={(event) => {
                if (event.target.value) add(event.target.value as DuplicateKeeperRuleType);
              }}
              aria-label="Add a keeper rule"
              className="rounded-md border border-border bg-surface py-1.5 pl-7 pr-3 text-sm text-secondary hover:border-accent focus:border-accent focus:outline-none"
            >
              <option value="">Add rule…</option>
              {available.map((definition) => (
                <option key={definition.type} value={definition.type}>
                  {definition.label}
                </option>
              ))}
            </select>
          </label>
        ) : null}
        <button
          type="button"
          onClick={() => onChange(DEFAULT_KEEPER_RULES)}
          className="inline-flex items-center gap-1.5 rounded-md px-2 py-1.5 text-sm text-muted hover:text-foreground"
        >
          <RotateCcw className="h-3.5 w-3.5" />
          Reset to defaults
        </button>
      </div>
    </div>
  );
}

export function describeRules(rules: DuplicateKeeperRule[]) {
  if (rules.length === 0) return "first found";
  return rules
    .map((rule) => ruleDefinition(rule.type)?.short ?? rule.type)
    .slice(0, 4)
    .join(" → ")
    .concat(rules.length > 4 ? " → …" : "");
}

function IconButton({
  label,
  disabled,
  onClick,
  children,
}: {
  label: string;
  disabled?: boolean;
  onClick: () => void;
  children: ReactNode;
}) {
  return (
    <button
      type="button"
      aria-label={label}
      title={label}
      disabled={disabled}
      onClick={onClick}
      className="rounded p-1 text-muted hover:bg-surface hover:text-foreground disabled:cursor-not-allowed disabled:opacity-30"
    >
      {children}
    </button>
  );
}

function CodecPreference({ values, onChange }: { values: string[]; onChange: (values: string[]) => void }) {
  const unused = CODEC_CHOICES.filter((codec) => !values.includes(codec));
  return (
    <div className="mt-2 flex flex-wrap items-center gap-1.5 pl-7">
      {values.map((codec, index) => (
        <span
          key={codec}
          className="inline-flex items-center gap-1 rounded-full border border-border bg-surface py-0.5 pl-2 pr-1 text-xs text-foreground"
        >
          <span className="text-muted">{index + 1}.</span>
          {formatCodec(codec)}
          <button
            type="button"
            aria-label={`Prefer ${formatCodec(codec)} earlier`}
            disabled={index === 0}
            onClick={() => {
              const next = [...values];
              [next[index - 1], next[index]] = [next[index], next[index - 1]];
              onChange(next);
            }}
            className="rounded p-0.5 text-muted hover:text-foreground disabled:opacity-30"
          >
            <ArrowUp className="h-3 w-3" />
          </button>
          <button
            type="button"
            aria-label={`Remove ${formatCodec(codec)}`}
            onClick={() => onChange(values.filter((value) => value !== codec))}
            className="rounded p-0.5 text-muted hover:text-foreground"
          >
            <X className="h-3 w-3" />
          </button>
        </span>
      ))}
      {unused.length > 0 ? (
        <select
          value=""
          aria-label="Add codec"
          onChange={(event) => event.target.value && onChange([...values, event.target.value])}
          className="rounded-full border border-dashed border-border bg-transparent px-2 py-0.5 text-xs text-muted focus:outline-none"
        >
          <option value="">+ codec</option>
          {unused.map((codec) => (
            <option key={codec} value={codec}>
              {formatCodec(codec)}
            </option>
          ))}
        </select>
      ) : null}
    </div>
  );
}

function PathPreference({ values, onChange }: { values: string[]; onChange: (values: string[]) => void }) {
  const [draft, setDraft] = useState("");
  const commit = () => {
    const value = draft.trim();
    if (value && !values.includes(value)) onChange([...values, value]);
    setDraft("");
  };
  return (
    <div className="mt-2 space-y-1.5 pl-7">
      {values.map((value, index) => (
        <div key={value} className="flex items-center gap-2 text-xs">
          <span className="text-muted">{index + 1}.</span>
          <code className="min-w-0 flex-1 truncate rounded bg-surface px-1.5 py-0.5 text-foreground" title={value}>
            {value}
          </code>
          <button
            type="button"
            aria-label={`Remove ${value}`}
            onClick={() => onChange(values.filter((item) => item !== value))}
            className="rounded p-0.5 text-muted hover:text-foreground"
          >
            <X className="h-3 w-3" />
          </button>
        </div>
      ))}
      <div className="flex items-center gap-2">
        <input
          value={draft}
          onChange={(event) => setDraft(event.target.value)}
          onKeyDown={(event) => {
            if (event.key === "Enter") {
              event.preventDefault();
              commit();
            }
          }}
          placeholder="Part of a path, e.g. /Library/Sorted"
          className="min-w-0 flex-1 rounded-md border border-border bg-surface px-2 py-1 text-xs text-foreground placeholder:text-muted focus:border-accent focus:outline-none"
        />
        <button
          type="button"
          onClick={commit}
          disabled={!draft.trim()}
          className="rounded-md border border-border px-2 py-1 text-xs text-secondary hover:border-accent disabled:opacity-40"
        >
          Add
        </button>
      </div>
      {values.length === 0 ? (
        <p className="text-[11px] text-amber-400">Add at least one folder for this rule to count.</p>
      ) : null}
    </div>
  );
}
