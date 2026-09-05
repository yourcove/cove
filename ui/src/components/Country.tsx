import { useEffect, useId, useMemo, useRef, useState } from "react";
import { useQuery } from "@tanstack/react-query";
import { Check, ChevronDown, X } from "lucide-react";
import { performers } from "../api/client";
import type { PerformerCountryOption } from "../api/types";
import { AutocompleteDropdown } from "./AutocompleteDropdown";
import { useOptionalAppConfig } from "../state/AppConfigContext";

function useCountryOptions() {
  return useQuery({
    queryKey: ["performer-country-options"],
    queryFn: performers.countries,
    staleTime: 5_000,
  });
}

function localizedCountryName(option: PerformerCountryOption, language: string) {
  if (!option.code || typeof Intl.DisplayNames !== "function") return option.name;
  try {
    const displayNames = new Intl.DisplayNames([language, "en"], { type: "region" });
    return displayNames.of(option.code) || option.name;
  } catch {
    return option.name;
  }
}

export function countryFlag(code?: string | null) {
  if (!code || !/^[A-Z]{2}$/.test(code)) return "";
  return String.fromCodePoint(...[...code].map((character) => 0x1f1e6 + character.charCodeAt(0) - 65));
}

export function CountryLabel({ value, className = "" }: { value?: string | null; className?: string }) {
  const { data = [] } = useCountryOptions();
  const language = useOptionalAppConfig()?.config?.interface?.language || "en-US";
  if (!value) return null;
  const option = data.find((item) => item.value.localeCompare(value, undefined, { sensitivity: "accent" }) === 0);
  if (!option?.code)
    return (
      <span className={className} title={value}>
        {value}
      </span>
    );
  const name = localizedCountryName(option, language);
  return (
    <span
      className={`inline-flex items-center gap-1 whitespace-nowrap ${className}`.trim()}
      title={`${name} (${option.code})`}
    >
      <span aria-hidden="true">{countryFlag(option.code)}</span>
      <span>{name}</span>
    </span>
  );
}

export function CountryFlag({ value, className = "" }: { value?: string | null; className?: string }) {
  const { data = [] } = useCountryOptions();
  const language = useOptionalAppConfig()?.config?.interface?.language || "en-US";
  if (!value) return null;
  const option = data.find((item) => item.value.localeCompare(value, undefined, { sensitivity: "accent" }) === 0);
  if (!option?.code) return null;
  const name = localizedCountryName(option, language);
  return (
    <span className={className} title={name} aria-label={name}>
      {countryFlag(option.code)}
    </span>
  );
}

interface CountrySelectProps {
  value?: string;
  onChange: (value: string) => void;
  placeholder?: string;
  allowClear?: boolean;
  className?: string;
}

export function CountrySelect({
  value = "",
  onChange,
  placeholder = "Search countries or enter a custom value…",
  allowClear = true,
  className = "",
}: CountrySelectProps) {
  const { data = [], isLoading } = useCountryOptions();
  const language = useOptionalAppConfig()?.config?.interface?.language || "en-US";
  const [query, setQuery] = useState("");
  const [open, setOpen] = useState(false);
  const [activeIndex, setActiveIndex] = useState(-1);
  const listboxId = `country-options-${useId().replace(/:/g, "")}`;
  const rootRef = useRef<HTMLDivElement>(null);
  const dropdownRef = useRef<HTMLDivElement>(null);
  const inputRef = useRef<HTMLInputElement>(null);
  const selected = data.find((option) => option.value.localeCompare(value, undefined, { sensitivity: "accent" }) === 0);
  const displayValue = query || (selected ? localizedCountryName(selected, language) : value);
  const normalizedQuery = query.trim().toLocaleLowerCase();
  const filtered = useMemo(() => {
    const sorted = [...data].sort((left, right) => {
      if (left.isCustom !== right.isCustom) return left.isCustom ? 1 : -1;
      return localizedCountryName(left, language).localeCompare(localizedCountryName(right, language));
    });
    if (!normalizedQuery) return sorted;
    return sorted.filter(
      (option) =>
        option.value.toLocaleLowerCase().includes(normalizedQuery) ||
        option.name.toLocaleLowerCase().includes(normalizedQuery) ||
        localizedCountryName(option, language).toLocaleLowerCase().includes(normalizedQuery),
    );
  }, [data, language, normalizedQuery]);
  const exactOption = data.find(
    (option) =>
      option.value.toLocaleLowerCase() === normalizedQuery ||
      option.name.toLocaleLowerCase() === normalizedQuery ||
      localizedCountryName(option, language).toLocaleLowerCase() === normalizedQuery,
  );
  const showCustomOption = !exactOption && Boolean(query.trim());
  const selectableOptionCount = filtered.length + (showCustomOption ? 1 : 0);

  useEffect(() => {
    const close = (event: MouseEvent) => {
      if (!rootRef.current?.contains(event.target as Node) && !dropdownRef.current?.contains(event.target as Node)) {
        setOpen(false);
        setQuery("");
      }
    };
    document.addEventListener("mousedown", close);
    return () => document.removeEventListener("mousedown", close);
  }, []);

  const choose = (nextValue: string) => {
    onChange(nextValue.trim());
    setQuery("");
    setOpen(false);
    setActiveIndex(-1);
  };

  return (
    <div ref={rootRef} className={`relative w-full min-w-0 ${className}`.trim()}>
      <div className="flex items-center rounded border border-border bg-card focus-within:border-accent">
        {selected?.code ? (
          <span className="ml-3 w-5 shrink-0 text-base leading-none" aria-hidden="true">
            {countryFlag(selected.code)}
          </span>
        ) : null}
        <input
          ref={inputRef}
          role="combobox"
          aria-label="Country"
          aria-expanded={open}
          aria-controls={listboxId}
          aria-activedescendant={activeIndex >= 0 ? `${listboxId}-${activeIndex}` : undefined}
          value={displayValue}
          placeholder={placeholder}
          onFocus={(event) => {
            setOpen(true);
            event.currentTarget.select();
          }}
          onChange={(event) => {
            setQuery(event.target.value);
            setOpen(true);
            setActiveIndex(-1);
          }}
          onKeyDown={(event) => {
            if (event.key === "ArrowDown" || event.key === "ArrowUp") {
              event.preventDefault();
              setOpen(true);
              setActiveIndex((current) => {
                const direction = event.key === "ArrowDown" ? 1 : -1;
                return Math.max(
                  0,
                  Math.min(
                    selectableOptionCount - 1,
                    current < 0 ? (direction > 0 ? 0 : selectableOptionCount - 1) : current + direction,
                  ),
                );
              });
            } else if (event.key === "Enter" && (query.trim() || activeIndex >= 0)) {
              event.preventDefault();
              choose(
                activeIndex >= 0 && activeIndex < filtered.length
                  ? filtered[activeIndex].value
                  : (exactOption?.value ?? query),
              );
            }
            if (event.key === "Escape") {
              setOpen(false);
              setQuery("");
              setActiveIndex(-1);
            }
          }}
          className="min-w-0 flex-1 bg-transparent px-3 py-2 text-sm text-foreground outline-none"
        />
        {allowClear && value ? (
          <button
            type="button"
            aria-label="Clear country"
            className="p-1 text-muted hover:text-foreground"
            onClick={() => choose("")}
          >
            <X className="h-3.5 w-3.5" />
          </button>
        ) : null}
        <button
          type="button"
          aria-label="Show countries"
          className="p-2 text-muted hover:text-foreground"
          onClick={() => {
            setOpen((current) => !current);
            inputRef.current?.focus();
          }}
        >
          <ChevronDown className="h-4 w-4" />
        </button>
      </div>
      {open ? (
        <AutocompleteDropdown
          anchorRef={rootRef}
          containerRef={dropdownRef}
          id={listboxId}
          role="listbox"
          aria-label="Country options"
          className="rounded border border-border bg-surface"
        >
          {isLoading ? <div className="px-3 py-2 text-xs text-muted">Loading countries…</div> : null}
          {filtered.map((option, index) => {
            const name = localizedCountryName(option, language);
            return (
              <button
                id={`${listboxId}-${index}`}
                key={option.value}
                type="button"
                role="option"
                aria-selected={option.value === value}
                className={`flex w-full items-center gap-2 px-3 py-2 text-left text-sm hover:bg-card ${activeIndex === index ? "bg-card" : ""}`}
                onMouseEnter={() => setActiveIndex(index)}
                onClick={() => choose(option.value)}
              >
                <span className="w-5 shrink-0" aria-hidden="true">
                  {countryFlag(option.code)}
                </span>
                <span className="min-w-0 flex-1 truncate">{name}</span>
                {option.value === value ? <Check className="h-3.5 w-3.5 text-accent" /> : null}
              </button>
            );
          })}
          {showCustomOption ? (
            <button
              id={`${listboxId}-${filtered.length}`}
              type="button"
              role="option"
              aria-label={`${query.trim()} Custom value`}
              aria-selected="false"
              className={`flex w-full items-center gap-2 px-3 py-2 text-left text-sm hover:bg-card ${activeIndex === filtered.length ? "bg-card" : ""}`}
              onMouseEnter={() => setActiveIndex(filtered.length)}
              onClick={() => choose(query)}
            >
              <span className="min-w-0 flex-1 truncate">{query.trim()}</span>
              <span className="shrink-0 text-xs text-muted">Custom value</span>
            </button>
          ) : null}
        </AutocompleteDropdown>
      ) : null}
    </div>
  );
}
