import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { render } from "@testing-library/react";
import { expect, it, vi } from "vitest";
import { ActiveObjectFilterChips } from "../components/ActiveObjectFilterChips";
import { createSegmentCriteria } from "../pages/segments/segmentCriteriaDefinitions";

vi.mock("../state/AppConfigContext", () => ({
  useOptionalAppConfig: () => ({ config: { interface: { language: "en-US" }, ui: {} } }),
}));

// The segment kind and source options load from the server after the page applies a filter from its URL, so
// the chip names the applied value the same way before and after they arrive.
it.each([
  ["before the options load", {}],
  [
    "after the options load",
    {
      kindOptions: [{ value: "tag", label: "Tag" }],
      sourceOptions: [{ value: "ext:ai.tagging", label: "AI Tagging" }],
    },
  ],
])("names segment kind and source chips readably %s", (_, options) => {
  const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } });

  render(
    <QueryClientProvider client={queryClient}>
      <ActiveObjectFilterChips
        criteriaDefinitions={createSegmentCriteria(options)}
        objectFilter={{
          rawKindCriterion: { value: "tag", modifier: "EQUALS" },
          rawSourceCriterion: { value: "ext:ai.tagging", modifier: "EQUALS" },
        }}
        onRemove={vi.fn()}
        onEdit={vi.fn()}
      />
    </QueryClientProvider>,
  );

  const chipText = document.body.textContent ?? "";
  expect(chipText).toContain("Tag");
  expect(chipText).toContain("AI Tagging");
  expect(chipText).not.toMatch(/\btag\b/);
  expect(chipText).not.toContain("ext:ai.tagging");
});
