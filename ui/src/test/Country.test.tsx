import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { fireEvent, render, screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { useState } from "react";
import type { CountryCriterion } from "../api/types";
import { CountryEditor } from "../components/PrimitiveCriterionEditors";
import { isCriterionValueValid } from "../components/filterCriterionState";
import { PERFORMER_CRITERIA } from "../components/filterCriteriaCatalogs";
import { ActiveObjectFilterChips, formatFilterChipValue } from "../components/ActiveObjectFilterChips";
import { describeFilterExpressionCondition } from "../components/filterExpressionExplanation";
import { defaultRatingSystemOptions } from "../components/Rating";
import { beforeEach, describe, expect, it, vi } from "vitest";
import { performers } from "../api/client";
import { CountryFlag, CountryLabel, CountrySelect, countryFlag } from "../components/Country";

const appConfigMock = vi.hoisted(() => ({ language: "en-US" }));

vi.mock("../state/AppConfigContext", () => ({
  useOptionalAppConfig: () => ({ config: { interface: { language: appConfigMock.language }, ui: {} } }),
}));

const options = [
  { value: "CA", code: "CA", name: "Canada", performerCount: 12, isCustom: false },
  { value: "GQ", code: "GQ", name: "Equatorial Guinea", performerCount: 0, isCustom: false },
  { value: "GN", code: "GN", name: "Guinea", performerCount: 0, isCustom: false },
  { value: "US", code: "US", name: "United States", performerCount: 42, isCustom: false },
  { value: "Atlantis", code: null, name: "Atlantis", performerCount: 2, isCustom: true },
];

function renderWithQueryClient(ui: React.ReactElement) {
  const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  return render(<QueryClientProvider client={queryClient}>{ui}</QueryClientProvider>);
}

beforeEach(() => {
  appConfigMock.language = "en-US";
  vi.spyOn(performers, "countries").mockResolvedValue(options);
  document.documentElement.lang = "en-US";
});

it("validates country lists and preserves nullable legacy filters", () => {
  const criterion = PERFORMER_CRITERIA.find((item) => item.id === "country")!;
  const list = { value: "", values: ["CA", "US"], modifier: "INCLUDES" };
  expect(isCriterionValueValid(list, criterion)).toBe(true);
  expect(isCriterionValueValid({ ...list, values: [] }, criterion)).toBe(false);
  expect(isCriterionValueValid({ value: "CA", values: null, modifier: "INCLUDES" }, criterion)).toBe(true);
  expect(isCriterionValueValid({ ...list, values: [], modifier: "IS_NULL" }, criterion)).toBe(true);
  expect(formatFilterChipValue(criterion, list)).toBe("Includes CA, US");
  expect(
    describeFilterExpressionCondition({ countryCriterion: list }, [criterion], defaultRatingSystemOptions, []),
  ).toBe("Country includes CA or US");
});

describe("Country", () => {
  it("renders flags only for catalog-recognized codes", async () => {
    const { rerender } = renderWithQueryClient(<CountryLabel value="US" />);
    await waitFor(() => expect(screen.getByText("United States")).toBeVisible());
    expect(screen.getByText(countryFlag("US"))).toBeVisible();

    rerender(
      <QueryClientProvider client={new QueryClient()}>
        <CountryLabel value="Atlantis" />
      </QueryClientProvider>,
    );
    expect(screen.getByText("Atlantis")).toBeVisible();
    expect(screen.queryByText(countryFlag("AT"))).not.toBeInTheDocument();
  });

  it("renders a flag-only country marker with the readable name as its tooltip", async () => {
    renderWithQueryClient(<CountryFlag value="CA" className="card-country-flag" />);

    const marker = await screen.findByLabelText("Canada");
    expect(marker).toHaveTextContent(countryFlag("CA"));
    expect(marker).toHaveAttribute("title", "Canada");
    expect(marker).toHaveClass("card-country-flag");
    expect(marker).not.toHaveTextContent("Canada");
  });

  it("searches readable names and returns the stored ISO code", async () => {
    const onChange = vi.fn();
    renderWithQueryClient(<CountrySelect onChange={onChange} />);
    const input = screen.getByRole("combobox", { name: "Country" });

    await userEvent.type(input, "United States");
    await userEvent.click((await screen.findByText("United States")).closest("button")!);

    expect(onChange).toHaveBeenCalledWith("US");
  });

  it("shows the selected country's flag in a full-width editor control", async () => {
    const { container } = renderWithQueryClient(<CountrySelect value="CA" onChange={vi.fn()} />);

    await waitFor(() => expect(screen.getByRole("combobox", { name: "Country" })).toHaveValue("Canada"));
    expect(screen.getByText(countryFlag("CA"))).toBeVisible();
    expect(container.firstElementChild).toHaveClass("w-full", "min-w-0");
  });

  it("keeps dropdown rows focused on flags and readable names", async () => {
    renderWithQueryClient(<CountrySelect onChange={vi.fn()} />);

    await userEvent.click(screen.getByRole("button", { name: "Show countries" }));
    const canada = await screen.findByRole("option", { name: "Canada" });
    expect(canada).toHaveTextContent(`${countryFlag("CA")}Canada`);
    expect(canada).not.toHaveTextContent("CA");
    expect(canada).not.toHaveTextContent("12");
  });

  it("sorts catalog and custom countries together by readable name", async () => {
    renderWithQueryClient(<CountrySelect onChange={vi.fn()} />);

    await userEvent.click(screen.getByRole("button", { name: "Show countries" }));
    const countryNames = (await screen.findAllByRole("option")).map((option) => option.textContent?.trim());

    expect(countryNames).toEqual([
      "Atlantis",
      `${countryFlag("CA")}Canada`,
      `${countryFlag("GQ")}Equatorial Guinea`,
      `${countryFlag("GN")}Guinea`,
      `${countryFlag("US")}United States`,
    ]);
  });

  it("sorts localized country names with the configured language", async () => {
    appConfigMock.language = "sv-SE";
    vi.spyOn(performers, "countries").mockResolvedValue([
      { value: "AX", code: "AX", name: "Åland Islands", performerCount: 0, isCustom: false },
      { value: "AL", code: "AL", name: "Albania", performerCount: 0, isCustom: false },
      { value: "ZW", code: "ZW", name: "Zimbabwe", performerCount: 0, isCustom: false },
    ]);
    renderWithQueryClient(<CountrySelect onChange={vi.fn()} />);

    await userEvent.click(screen.getByRole("button", { name: "Show countries" }));
    const countryNames = (await screen.findAllByRole("option")).map((option) => option.textContent?.trim());

    expect(countryNames).toEqual([
      `${countryFlag("AL")}Albanien`,
      `${countryFlag("ZW")}Zimbabwe`,
      `${countryFlag("AX")}Åland`,
    ]);
  });

  it("falls back to English sorting for an invalid configured language", async () => {
    appConfigMock.language = "x";
    renderWithQueryClient(<CountrySelect onChange={vi.fn()} />);

    await userEvent.click(screen.getByRole("button", { name: "Show countries" }));
    const countryNames = (await screen.findAllByRole("option")).map((option) => option.textContent?.trim());

    expect(countryNames).toEqual([
      "Atlantis",
      `${countryFlag("CA")}Canada`,
      `${countryFlag("GQ")}Equatorial Guinea`,
      `${countryFlag("GN")}Guinea`,
      `${countryFlag("US")}United States`,
    ]);
  });

  it("allows an unmatched custom value", async () => {
    const onChange = vi.fn();
    renderWithQueryClient(<CountrySelect onChange={onChange} />);
    const input = screen.getByRole("combobox", { name: "Country" });

    fireEvent.change(input, { target: { value: "Moon Colony" } });
    await userEvent.click(screen.getByRole("option", { name: "Moon Colony Custom value" }));

    expect(onChange).toHaveBeenCalledWith("Moon Colony");
  });

  it("places the custom value last and supports selecting it with the keyboard", async () => {
    const onChange = vi.fn();
    renderWithQueryClient(<CountrySelect onChange={onChange} />);
    const input = screen.getByRole("combobox", { name: "Country" });

    await userEvent.type(input, "United");
    const options = await screen.findAllByRole("option");
    expect(options.at(-1)).toHaveAccessibleName("United Custom value");

    await userEvent.type(input, "{ArrowUp}{Enter}");
    expect(onChange).toHaveBeenCalledWith("United");
  });

  it("prefers an exact country-name match when Enter is pressed", async () => {
    const onChange = vi.fn();
    renderWithQueryClient(<CountrySelect onChange={onChange} />);
    const input = screen.getByRole("combobox", { name: "Country" });

    await userEvent.type(input, "Guinea{Enter}");

    expect(onChange).toHaveBeenCalledWith("GN");
  });
});

function CountryFilterHarness({ initialValues = [] }: { initialValues?: string[] }) {
  const [value, setValue] = useState<CountryCriterion>({ value: "", values: initialValues, modifier: "INCLUDES" });
  return (
    <>
      <CountryEditor
        value={value}
        onChange={(next) => setValue(next as CountryCriterion)}
        modifiers={["EQUALS", "INCLUDES", "EXCLUDES", "IS_NULL"]}
      />
      <output>{JSON.stringify(value)}</output>
    </>
  );
}

it("sorts selected countries alphabetically in the filter editor", async () => {
  vi.spyOn(performers, "countries").mockResolvedValue([
    { value: "FI", code: "FI", name: "Finland", performerCount: 1, isCustom: false },
    { value: "SE", code: "SE", name: "Sweden", performerCount: 1, isCustom: false },
  ]);
  renderWithQueryClient(<CountryFilterHarness initialValues={["SE", "FI"]} />);

  await screen.findByText("Finland");
  expect(screen.getAllByRole("button", { name: /^Remove (?:FI|SE)$/ }).map((button) => button.ariaLabel)).toEqual([
    "Remove FI",
    "Remove SE",
  ]);
});

it("sorts selected countries alphabetically in applied filter chips", async () => {
  vi.spyOn(performers, "countries").mockResolvedValue([
    { value: "FI", code: "FI", name: "Finland", performerCount: 1, isCustom: false },
    { value: "SE", code: "SE", name: "Sweden", performerCount: 1, isCustom: false },
  ]);
  renderWithQueryClient(
    <ActiveObjectFilterChips
      criteriaDefinitions={PERFORMER_CRITERIA}
      objectFilter={{ countryCriterion: { value: "", values: ["SE", "FI"], modifier: "INCLUDES" } }}
      onRemove={vi.fn()}
      onEdit={vi.fn()}
    />,
  );

  await screen.findByText("Finland");
  const chipText = screen.getByRole("button", { name: "Edit filter: Country" }).textContent ?? "";
  expect(chipText.indexOf("Finland")).toBeLessThan(chipText.indexOf("Sweden"));
});

it("adds, deduplicates, removes and switches multiple country selections", async () => {
  renderWithQueryClient(<CountryFilterHarness />);
  const user = userEvent.setup();
  for (const name of ["Canada", "United States", "Canada"]) {
    await user.click(screen.getByRole("button", { name: "Show countries" }));
    await user.click(await screen.findByRole("option", { name }));
  }
  expect(screen.getByRole("status")).toHaveTextContent('"values":["CA","US"]');
  await user.click(screen.getByRole("button", { name: "Excludes" }));
  expect(screen.getByRole("status")).toHaveTextContent('"values":["CA","US"]');
  await user.click(screen.getByRole("button", { name: "Remove CA" }));
  expect(screen.getByRole("status")).toHaveTextContent('"values":["US"]');
  await user.click(screen.getByRole("button", { name: "=" }));
  expect(screen.getByRole("combobox", { name: "Country" })).toHaveValue("United States");
  expect(screen.getByRole("status")).not.toHaveTextContent('"values"');
});
