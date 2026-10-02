import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { render, screen } from "@testing-library/react";
import type { ComponentType } from "react";
import { beforeEach, describe, expect, it, vi } from "vitest";
import { PerformerCreateModal } from "../pages/PerformersPage";
import { StudioCreateModal } from "../pages/StudiosPage";
import { TagCreateModal } from "../pages/TagsPage";

const mocks = vi.hoisted(() => ({
  tagGroupsList: vi.fn(),
}));

vi.mock("../api/client", async (importOriginal) => {
  const actual = await importOriginal<typeof import("../api/client")>();
  return {
    ...actual,
    tagGroups: { ...actual.tagGroups, list: mocks.tagGroupsList },
  };
});

vi.mock("../components/EntityReferenceSelector", () => ({
  EntityReferenceSelector: () => null,
  EntityReferenceMultiSelector: () => null,
}));

vi.mock("../components/Country", () => ({
  CountrySelect: () => null,
}));

vi.mock("../components/shared", () => ({
  CustomFieldsEditor: () => null,
}));

type CreateModalProps = {
  open: boolean;
  initialName?: string;
  onClose: () => void;
  onCreated: (id: number) => void;
};

const modals: [string, ComponentType<CreateModalProps>][] = [
  ["performer", PerformerCreateModal],
  ["studio", StudioCreateModal],
  ["tag", TagCreateModal],
];

function renderModal(Modal: ComponentType<CreateModalProps>, open: boolean, initialName: string) {
  const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  const element = (nextOpen: boolean, nextInitialName: string) => (
    <QueryClientProvider client={queryClient}>
      <Modal open={nextOpen} initialName={nextInitialName} onClose={vi.fn()} onCreated={vi.fn()} />
    </QueryClientProvider>
  );
  const view = render(element(open, initialName));
  return {
    rerender: (nextOpen: boolean, nextInitialName: string) => view.rerender(element(nextOpen, nextInitialName)),
  };
}

describe("entity create search prefills", () => {
  beforeEach(() => {
    mocks.tagGroupsList.mockResolvedValue([]);
  });

  it.each(modals)("prefills the trimmed %s name when mounted open", (_kind, Modal) => {
    renderModal(Modal, true, "  New entity  ");

    expect(screen.getByDisplayValue("New entity")).toBeInTheDocument();
  });

  it.each(modals)("prefills the %s name when opened later and follows a new search while open", (_kind, Modal) => {
    const { rerender } = renderModal(Modal, false, "");

    rerender(true, "  First search  ");
    expect(screen.getByDisplayValue("First search")).toBeInTheDocument();

    rerender(true, "Second search");
    expect(screen.getByDisplayValue("Second search")).toBeInTheDocument();
  });
});
