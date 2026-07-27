import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { fireEvent, render, screen, waitFor, within } from "@testing-library/react";
import { afterEach, describe, expect, it, vi } from "vitest";
import { AudioDetailPage } from "../pages/AudioDetailPage";
import { TextDetailPage } from "../pages/TextDetailPage";

const {
  mockAudios,
  mockTexts,
  mockTags,
  mockPerformers,
  mockGroups,
  mockEntityImages,
  mockPlayback,
  mockSetFavorite,
  mockSetRating,
  mockGoBack,
  mockTrackInteraction,
} = vi.hoisted(() => ({
  mockAudios: {
    get: vi.fn(),
    update: vi.fn(),
    streamUrl: vi.fn((id: number) => `/api/audios/${id}/stream`),
  },
  mockTexts: {
    get: vi.fn(),
    update: vi.fn(),
    content: vi.fn(),
    fileUrl: vi.fn((id: number) => `/api/texts/${id}/file`),
  },
  mockTags: {
    find: vi.fn(),
  },
  mockPerformers: {
    find: vi.fn(),
  },
  mockGroups: {
    find: vi.fn(),
  },
  mockEntityImages: {
    uploadAudioImage: vi.fn(),
    deleteAudioImage: vi.fn(),
    uploadTextImage: vi.fn(),
    deleteTextImage: vi.fn(),
    studioImageUrl: vi.fn((id: number) => `/studio-${id}.jpg`),
    groupFrontImageUrl: vi.fn((id: number) => `/group-${id}.jpg`),
    tagImageUrl: vi.fn((id: number) => `/tag-${id}.jpg`),
  },
  mockPlayback: {
    recordIntervals: vi.fn(() => Promise.resolve()),
  },
  mockSetFavorite: vi.fn(),
  mockSetRating: vi.fn(),
  mockGoBack: vi.fn(),
  mockTrackInteraction: vi.fn(),
}));

vi.mock("../hooks/useDocumentTitle", () => ({
  useDocumentTitle: () => {},
}));

vi.mock("../api/client", () => ({
  audios: mockAudios,
  texts: mockTexts,
  tags: mockTags,
  performers: mockPerformers,
  groups: mockGroups,
  entityImages: mockEntityImages,
  playback: mockPlayback,
}));

vi.mock("../hooks/useEntityEngagement", () => ({
  useEntityEngagement: () => ({
    engagement: { playCount: 2, playDuration: 120, pageVisitCount: 3, resumeTime: 18, likeCount: 0, derivedLikeCount: 0, completeCount: 0 },
    favorite: false,
    rating: 4,
    setFavorite: mockSetFavorite,
    setRating: mockSetRating,
    favoritePending: false,
  }),
}));

vi.mock("../components/BookmarkButton", () => ({
  BookmarkButton: () => <div>Bookmark</div>,
}));

vi.mock("../components/AspectRatingsPanel", () => ({
  AspectRatingsPanel: () => <div>Rating Breakdown</div>,
}));

vi.mock("../components/AudioPlayer", () => ({
  AudioPlayer: ({ title }: { title: string }) => <div>Audio Player {title}</div>,
}));

vi.mock("../components/VideoPlayer", () => ({
  VideoPlayer: () => <div>Video Player</div>,
}));

vi.mock("../components/Rating", () => ({
  InteractiveRating: ({ value }: { value: number }) => <div>Rating {value}</div>,
  RatingBanner: ({ rating }: { rating?: number }) => <div>Rating Banner {rating ?? ""}</div>,
}));

vi.mock("../hooks/useBackNavigation", () => ({
  useBackNavigation: () => ({
    backLabel: "Back",
    goBack: mockGoBack,
  }),
}));

vi.mock("../auth/AuthContext", () => ({
  useAuth: () => ({
    user: { kind: "user", uiPreferences: { tracking: { enabled: true } } },
    hasPermission: () => true,
  }),
}));

vi.mock("../utils/interactionTracking", async () => {
  const actual = await vi.importActual<typeof import("../utils/interactionTracking")>("../utils/interactionTracking");
  return {
    ...actual,
    createPlaybackSessionId: () => "test-session",
    trackInteraction: mockTrackInteraction,
  };
});

function buildAudio(overrides: Record<string, unknown> = {}) {
  return {
    id: 14,
    title: "Night Drive",
    organized: true,
    studioId: 9,
    studioName: "After Hours",
    date: "2026-05-04",
    urls: ["https://example.com/audio/14"],
    tags: [{ id: 2, name: "Synthwave", color: null, tagGroupColor: null, provenance: undefined }],
    performers: [{ id: 7, name: "Riley Hart", imagePath: undefined }],
    tracks: [{ id: 1, orderIndex: 0, title: "Intro", startSec: 0, endSec: 42 }],
    files: [{ id: 1, path: "C:/audio/night-drive.mp3", basename: "night-drive.mp3", format: "mp3", duration: 240, audioCodec: "mp3", bitRate: 320000, sampleRate: 44100, channels: 2, size: 6_291_456, hasVideoTrack: false }],
    groups: [{ id: 5, name: "Late Night Mix", videoIndex: 0 }],
    customFields: {},
    createdAt: "2026-05-04T00:00:00Z",
    updatedAt: "2026-05-04T01:00:00Z",
    fileCount: 1,
    maxDuration: 240,
    hasVideoFiles: false,
    details: "Cruising soundtrack.",
    code: undefined,
    ...overrides,
  };
}

function buildText(overrides: Record<string, unknown> = {}) {
  return {
    id: 22,
    title: "Project Notes",
    organized: true,
    studioId: 3,
    studioName: "Docs Lab",
    date: "2026-05-06",
    urls: ["https://example.com/text/22"],
    tags: [{ id: 4, name: "Reference", color: null, tagGroupColor: null, provenance: undefined }],
    performers: [{ id: 7, name: "Dana Lee", imagePath: undefined }],
    files: [{ id: 2, path: "C:/docs/project-notes.md", basename: "project-notes.md", format: "md", pageCount: 4, wordCount: 1200, excerptText: "Working notes for wave 5.", size: 4096 }],
    groups: [{ id: 8, name: "Wave 5", videoIndex: 0 }],
    customFields: {},
    createdAt: "2026-05-06T00:00:00Z",
    updatedAt: "2026-05-06T01:00:00Z",
    fileCount: 1,
    maxWordCount: 1200,
    maxPageCount: 4,
    details: "Project planning notes.",
    code: undefined,
    ...overrides,
  };
}

function renderWithQueryClient(ui: React.ReactNode) {
  const queryClient = new QueryClient({
    defaultOptions: {
      queries: { retry: false },
      mutations: { retry: false },
    },
  });

  return render(<QueryClientProvider client={queryClient}>{ui}</QueryClientProvider>);
}

describe("Audio and text detail pages", () => {
  afterEach(() => {
    vi.clearAllMocks();
  });

  it("renders the audio detail flow with the reusable audio player and tracks tab", async () => {
    mockAudios.get.mockResolvedValue(buildAudio());

    renderWithQueryClient(<AudioDetailPage id={14} onNavigate={vi.fn()} />);

    expect(await screen.findByRole("heading", { name: "Night Drive" })).toBeInTheDocument();
    expect(screen.getByText("Audio Player Night Drive")).toBeInTheDocument();
    expect(screen.getByRole("tab", { name: /edit/i })).toBeInTheDocument();
    expect(screen.queryByRole("tab", { name: /related/i })).not.toBeInTheDocument();
    expect(mockTrackInteraction).toHaveBeenCalledWith(expect.objectContaining({ hostType: "audio", hostId: 14, kind: "pageVisit" }));

    expect(await screen.findByRole("heading", { name: "Groups" })).toBeInTheDocument();
    expect(screen.getByText("Late Night Mix")).toBeInTheDocument();
    expect(screen.getByRole("heading", { name: "Performers" })).toBeInTheDocument();

    const tabs = screen.getByRole("tablist", { name: /detail tabs/i });
    fireEvent.click(within(tabs).getByRole("tab", { name: /tracks/i }));

    expect(await screen.findByText("Intro")).toBeInTheDocument();
  });

  it("switches to the shared video player when an audio file carries video", async () => {
    mockAudios.get.mockResolvedValue(buildAudio({
      files: [{ id: 3, path: "C:/audio/live-session.mp4", basename: "live-session.mp4", format: "mp4", duration: 180, audioCodec: "aac", bitRate: 192000, sampleRate: 48000, channels: 2, size: 7_340_032, hasVideoTrack: true }],
      hasVideoFiles: true,
    }));

    renderWithQueryClient(<AudioDetailPage id={14} onNavigate={vi.fn()} />);

    expect(await screen.findByRole("heading", { name: "Night Drive" })).toBeInTheDocument();
    expect(screen.getByText("Video Player")).toBeInTheDocument();
    expect(screen.queryByText(/Audio Player/i)).not.toBeInTheDocument();
    expect(screen.queryByTestId("media-detail-layout-media-frame")).not.toBeInTheDocument();
  });

  it("renders markdown text content and records a reading interval on exit", async () => {
    mockTexts.get.mockResolvedValue(buildText());
    mockTexts.content.mockResolvedValue({
      format: "md",
      renderMode: "markdown",
      content: "# Chapter One\n\nHello from the reader.",
    });

    const view = renderWithQueryClient(<TextDetailPage id={22} onNavigate={vi.fn()} />);

    expect(await screen.findByRole("heading", { name: "Project Notes" })).toBeInTheDocument();
    expect(await screen.findAllByText("Chapter One")).not.toHaveLength(0);
    expect(screen.getByRole("tab", { name: /edit/i })).toBeInTheDocument();
    expect(screen.queryByRole("tab", { name: /related/i })).not.toBeInTheDocument();

    const tabs = screen.getByRole("tablist", { name: /detail tabs/i });
    fireEvent.click(within(tabs).getByRole("tab", { name: /details/i }));
    expect(await screen.findByRole("heading", { name: "Groups" })).toBeInTheDocument();
    expect(screen.getByText("Wave 5")).toBeInTheDocument();
    expect(screen.getByRole("heading", { name: "Performers" })).toBeInTheDocument();

    view.unmount();

    await waitFor(() => expect(mockPlayback.recordIntervals).toHaveBeenCalledWith(expect.objectContaining({ hostType: "text", hostId: 22 })));
  });
});
