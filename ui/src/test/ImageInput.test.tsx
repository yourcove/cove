import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { fireEvent, render, screen, waitFor } from "@testing-library/react";
import { afterEach, describe, expect, it, vi } from "vitest";
import { ImageInput } from "../components/ImageInput";

function renderInput(props: Partial<React.ComponentProps<typeof ImageInput>> = {}) {
  const client = new QueryClient({ defaultOptions: { mutations: { retry: false } } });
  return render(
    <QueryClientProvider client={client}>
      <ImageInput currentImageUrl="/current.jpg" onUpload={vi.fn()} label="Cover" {...props} />
    </QueryClientProvider>,
  );
}

describe("ImageInput", () => {
  afterEach(() => vi.restoreAllMocks());

  it("restores the persisted cover when an upload fails", async () => {
    vi.spyOn(URL, "createObjectURL").mockReturnValue("blob:preview");
    vi.spyOn(URL, "revokeObjectURL").mockImplementation(() => undefined);
    const onUpload = vi.fn().mockRejectedValue(new Error("Upload failed"));
    const { container } = renderInput({ onUpload });
    const fileInput = container.querySelector('input[type="file"]') as HTMLInputElement;

    fireEvent.change(fileInput, { target: { files: [new File(["image"], "cover.png", { type: "image/png" })] } });
    expect(screen.getByRole("img", { name: "Cover" })).toHaveAttribute("src", "blob:preview");

    await waitFor(() => expect(screen.getByRole("img", { name: "Cover" })).toHaveAttribute("src", "/current.jpg"));
    expect(screen.getByRole("alert")).toHaveTextContent("Upload failed");
  });

  it("retries image display when the persisted URL changes", () => {
    const { rerender } = renderInput();
    fireEvent.error(screen.getByRole("img", { name: "Cover" }));
    expect(screen.queryByRole("img", { name: "Cover" })).not.toBeInTheDocument();

    rerender(
      <QueryClientProvider client={new QueryClient()}>
        <ImageInput currentImageUrl="/refreshed.jpg" onUpload={vi.fn()} label="Cover" />
      </QueryClientProvider>,
    );

    expect(screen.getByRole("img", { name: "Cover" })).toHaveAttribute("src", "/refreshed.jpg");
  });

  it("ignores a second drop while an upload is pending", async () => {
    vi.spyOn(URL, "createObjectURL").mockReturnValue("blob:preview");
    vi.spyOn(URL, "revokeObjectURL").mockImplementation(() => undefined);
    let finishUpload: (() => void) | undefined;
    const onUpload = vi.fn(() => new Promise<void>((resolve) => { finishUpload = resolve; }));
    const { container } = renderInput({ onUpload });
    const dropTarget = container.querySelector(".border-dashed") as HTMLElement;
    const file = new File(["image"], "cover.png", { type: "image/png" });

    fireEvent.drop(dropTarget, { dataTransfer: { files: [file] } });
    await waitFor(() => expect(onUpload).toHaveBeenCalledOnce());
    fireEvent.drop(dropTarget, { dataTransfer: { files: [file] } });
    expect(onUpload).toHaveBeenCalledOnce();
    finishUpload?.();
  });

  it("disables other inputs while loading an image URL", async () => {
    vi.stubGlobal("fetch", vi.fn(() => new Promise<Response>(() => undefined)));
    renderInput();
    fireEvent.click(screen.getByRole("button", { name: "URL" }));
    fireEvent.change(screen.getByPlaceholderText("https://..."), { target: { value: "https://example.invalid/cover.jpg" } });
    fireEvent.click(screen.getByRole("button", { name: "Load" }));

    await waitFor(() => expect(screen.getByRole("button", { name: "File" })).toBeDisabled());
    expect(screen.getByRole("button", { name: "Paste" })).toBeDisabled();
  });

  it.each([
    ["a failed response", new Response(null, { status: 404 }), "Image request failed (404)."],
    ["non-image content", new Response("not an image", { status: 200, headers: { "Content-Type": "text/plain" } }), "The URL did not return an image."],
  ])("rejects %s without uploading", async (_case, response, expectedError) => {
    const onUpload = vi.fn();
    vi.stubGlobal("fetch", vi.fn().mockResolvedValue(response));
    renderInput({ onUpload });
    fireEvent.click(screen.getByRole("button", { name: "URL" }));
    fireEvent.change(screen.getByPlaceholderText("https://..."), { target: { value: "https://example.invalid/cover" } });
    fireEvent.click(screen.getByRole("button", { name: "Load" }));

    expect(await screen.findByRole("alert")).toHaveTextContent(expectedError);
    expect(onUpload).not.toHaveBeenCalled();
  });
});
