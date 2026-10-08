import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { render, screen } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { beforeEach, describe, expect, it, vi } from "vitest";
import { EntityReferenceMultiSelector } from "../components/EntityReferenceSelector";

const api = vi.hoisted(() => ({ findTags: vi.fn(), createTag: vi.fn() }));

vi.mock("../api/client", () => ({
  tags: { find: api.findTags, create: api.createTag },
}));

const renderSelector = () => {
  const onChange = vi.fn();
  render(
    <QueryClientProvider client={new QueryClient({ defaultOptions: { queries: { retry: false } } })}>
      <EntityReferenceMultiSelector entityType="tag" values={[]} onChange={onChange} placeholder="Search tags..." />
    </QueryClientProvider>,
  );
  return { onChange, input: screen.getByPlaceholderText("Search tags...") };
};

describe("EntityReferenceMultiSelector and Enter", () => {
  beforeEach(() => {
    vi.clearAllMocks();
    api.findTags.mockResolvedValue({
      items: [
        { id: 4, name: "Slave Training" },
        { id: 3, name: "Slave" },
      ],
      count: 2,
    });
  });

  it("adds the tag named exactly as typed without an arrow key first", async () => {
    const { onChange, input } = renderSelector();
    await userEvent.type(input, "slave");
    await screen.findByRole("option", { name: "Slave" });
    await userEvent.keyboard("{Enter}");
    expect(onChange).toHaveBeenCalledWith([3]);
  });

  it("neither adds a partial match nor creates a tag on Enter", async () => {
    const { onChange, input } = renderSelector();
    await userEvent.type(input, "sla");
    await screen.findByRole("option", { name: "Slave" });
    await userEvent.keyboard("{Enter}");

    await userEvent.clear(input);
    api.findTags.mockResolvedValue({ items: [], count: 0 });
    await userEvent.type(input, "Brand new");
    await screen.findByRole("option", { name: /Create/ });
    await userEvent.keyboard("{Enter}");

    expect(onChange).not.toHaveBeenCalled();
    expect(api.createTag).not.toHaveBeenCalled();
  });

  it("shows a note on a chip only when the owner gives one", () => {
    render(
      <QueryClientProvider client={new QueryClient()}>
        <EntityReferenceMultiSelector
          entityType="tag"
          values={[2, 5]}
          onChange={vi.fn()}
          seedOptions={[
            { id: 2, label: "Tit Worship" },
            { id: 5, label: "Old tag" },
          ]}
          valueTitles={{ 2: "Scraped as “Tit Tease”" }}
        />
      </QueryClientProvider>,
    );
    expect(screen.getByTitle("Scraped as “Tit Tease”")).toHaveTextContent("Tit Worship");
    expect(screen.getByText("Old tag").closest("[title]")).toBeNull();
  });
});
