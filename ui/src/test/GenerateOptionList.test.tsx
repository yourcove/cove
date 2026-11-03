import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { render, screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { useState, type ReactNode } from "react";
import { beforeEach, describe, expect, it, vi } from "vitest";
import type { GenerateEstimate, GenerateOptions } from "../api/client";
import { GenerateDialog } from "../components/GenerateDialog";
import { GenerateOptionList, GenerateRunSummary } from "../components/GenerateOptionList";
import { formatCount, optionWork, runSize, sizeEach, sizeRun } from "../components/generateOptionCatalog";

const mocks = vi.hoisted(() => ({ generate: vi.fn(), estimateGenerate: vi.fn() }));

vi.mock("../api/client", () => ({
  metadata: { generate: mocks.generate, estimateGenerate: mocks.estimateGenerate },
}));

const count = (missing: number, total: number, missingBytes = 0, totalBytes = 0) => ({
  missing,
  total,
  missingBytes,
  totalBytes,
});

const LIBRARY: GenerateEstimate = {
  thumbnails: count(120, 2400),
  previews: count(1850, 2400),
  sprites: count(0, 2400),
  vrStereo: count(0, 0),
  segmentCovers: [
    { kind: "face", missing: 22034, total: 22034 },
    { kind: "tag", missing: 10235, total: 10238 },
  ],
  phashes: count(1100, 2400),
  md5: count(2400, 2400, 2400 * 1024 ** 3, 2400 * 1024 ** 3),
};

function renderWithClient(ui: ReactNode) {
  const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  return render(<QueryClientProvider client={queryClient}>{ui}</QueryClientProvider>);
}

function List({ initial, estimate = LIBRARY }: { initial: GenerateOptions; estimate?: GenerateEstimate }) {
  const [options, setOptions] = useState(initial);
  const keys = ["thumbnails", "previews", "sprites", "vrStereo", "segmentCovers", "phashes", "md5"] as const;
  return (
    <>
      <GenerateOptionList
        idPrefix="test"
        groups={[{ title: "Video options", keys: [...keys] }]}
        sizing="run"
        options={options}
        estimate={estimate}
        isChecked={(key) => !!options[key]}
        onCheckedChange={(key, checked) => setOptions({ ...options, [key]: checked })}
        onSegmentCoverKindsChange={(kinds) => setOptions({ ...options, segmentCoverKinds: kinds })}
      />
      <GenerateRunSummary keys={[...keys]} options={options} estimate={estimate} />
      <output data-testid="kinds">{JSON.stringify(options.segmentCoverKinds ?? null)}</output>
    </>
  );
}

describe("Generate option sizes", () => {
  it("sizes the work for one item apart from the run it adds up to", () => {
    expect(sizeEach(0.8)).toBe("XS");
    expect(sizeEach(30)).toBe("M");
    expect(sizeRun(30)).toBe("XS");
    expect(sizeRun(8 * 3600)).toBe("L");
    expect(sizeRun(20 * 3600)).toBe("XL");

    // A segment cover is a quick frame, but ten thousand of them take hours.
    const segments = optionWork("segmentCovers", LIBRARY, { segmentCovers: true })!;
    expect(segments.count).toBe(10235);
    expect(sizeEach(segments.secondsEach)).toBe("XS");
    expect(sizeRun(segments.seconds)).toBe("L");
  });

  it("shows sampled counts as rough figures", () => {
    expect(formatCount(6983)).toBe("6,983");
    expect(formatCount(6983, true)).toBe("~7,000");
    expect(formatCount(392, true)).toBe("~390");
    expect(formatCount(31, true)).toBe("~31");
    expect(formatCount(0, true)).toBe("0");
  });

  it("marks a sampled count as rough in its row", () => {
    renderWithClient(
      <List initial={{}} estimate={{ ...LIBRARY, previews: { ...count(1850, 2400), estimated: true } }} />,
    );
    expect(screen.getByText("× ~1,900")).toBeInTheDocument();
    expect(
      screen.getByRole("img", { name: "M each, about 1,900 videos to make: XL, half a day or more" }),
    ).toBeInTheDocument();
  });

  it("counts face segments only when they are chosen, and everything when overwriting", () => {
    expect(optionWork("segmentCovers", LIBRARY, { segmentCoverKinds: ["face", "tag"] })!.count).toBe(32269);
    expect(optionWork("thumbnails", LIBRARY, { overwrite: true })!.count).toBe(2400);
    // MD5 follows the bytes it reads: 1 GB a file is a few seconds each.
    expect(sizeEach(optionWork("md5", LIBRARY, {})!.secondsEach)).toBe("S");
    expect(runSize(["thumbnails"], LIBRARY, { thumbnails: true })).toBe("S");
    expect(optionWork("imageThumbnails", LIBRARY, {})).toBeNull();
  });
});

describe("GenerateOptionList", () => {
  it("shows each × how many → the run for every option", () => {
    renderWithClient(<List initial={{ segmentCovers: true }} />);

    expect(screen.getByRole("img", { name: "XS each, 10,235 segments to make: L, several hours" })).toBeInTheDocument();
    expect(screen.getByRole("img", { name: "Already made" })).toBeInTheDocument();
    expect(screen.getByRole("img", { name: "Nothing in scope" })).toBeInTheDocument();
    expect(screen.getByText("several hours")).toBeInTheDocument();
  });

  it("lets face segments be chosen for segment covers", async () => {
    const user = userEvent.setup();
    renderWithClient(<List initial={{ segmentCovers: true }} />);

    const face = screen.getByRole("button", { name: /Face segments/ });
    expect(face).toHaveAttribute("aria-pressed", "false");
    expect(screen.getByRole("button", { name: /Tag segments/ })).toHaveAttribute("aria-pressed", "true");

    await user.click(face);

    expect(screen.getByTestId("kinds")).toHaveTextContent('["face","tag"]');
    expect(screen.getByRole("img", { name: /32,269 segments to make/ })).toBeInTheDocument();

    await user.click(face);
    await user.click(screen.getByRole("button", { name: /Tag segments/ }));
    expect(screen.getByTestId("kinds")).toHaveTextContent("[]");
    expect(screen.getByRole("img", { name: "No segment kinds picked" })).toBeInTheDocument();
  });

  it("explains what an option makes and where Cove uses it", async () => {
    const user = userEvent.setup();
    renderWithClient(<List initial={{}} />);

    const details = screen.getByRole("button", { name: "Details: Sprite sheets" });
    expect(details).toHaveAttribute("aria-expanded", "false");
    await user.click(details);

    expect(details).toHaveAttribute("aria-expanded", "true");
    expect(screen.getByText(/hovering the player's seek bar/)).toBeInTheDocument();
    expect(screen.getByText(/fall back to the video's cover/)).toBeInTheDocument();
  });
});

describe("GenerateDialog", () => {
  beforeEach(() => {
    mocks.generate.mockReset();
    mocks.generate.mockResolvedValue({ jobId: "generate-job" });
    mocks.estimateGenerate.mockReset();
    mocks.estimateGenerate.mockResolvedValue({
      ...LIBRARY,
      segmentCovers: [
        { kind: "face", missing: 37, total: 37 },
        { kind: "tag", missing: 4, total: 4 },
      ],
    });
  });

  it("counts for the selected video and sends the chosen segment kinds", async () => {
    const user = userEvent.setup();
    renderWithClient(<GenerateDialog open videoIds={[7]} onClose={vi.fn()} />);

    await waitFor(() =>
      expect(mocks.estimateGenerate).toHaveBeenCalledWith({ videoIds: [7] }, expect.any(AbortSignal)),
    );
    await user.click(screen.getByLabelText("Segment covers"));
    await user.click(await screen.findByRole("button", { name: /Face segments/ }));
    const buttons = screen.getAllByRole("button", { name: "Generate" });
    await user.click(buttons[buttons.length - 1]);

    await waitFor(() => expect(mocks.generate).toHaveBeenCalledOnce());
    const request = mocks.generate.mock.calls[0][0];
    expect(request.videoIds).toEqual([7]);
    expect(request.segmentCovers).toBe(true);
    expect(request.segmentCoverKinds).toEqual(["face", "tag"]);
  });
});
