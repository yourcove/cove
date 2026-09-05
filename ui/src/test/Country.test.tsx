import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { fireEvent, render, screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { beforeEach, describe, expect, it, vi } from "vitest";
import { performers } from "../api/client";
import { CountryFlag, CountryLabel, CountrySelect, countryFlag } from "../components/Country";

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
  vi.spyOn(performers, "countries").mockResolvedValue(options);
  document.documentElement.lang = "en-US";
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
