import { fireEvent, render, screen, waitFor } from "@testing-library/react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { AudioPlayer } from "../components/AudioPlayer";

const { mockUseAuth } = vi.hoisted(() => ({
  mockUseAuth: vi.fn(),
}));

vi.mock("../auth/AuthContext", () => ({
  useAuth: mockUseAuth,
}));

type MockAudioParam = {
  value: number;
  setTargetAtTime: ReturnType<typeof vi.fn>;
  setValueAtTime: ReturnType<typeof vi.fn>;
  cancelScheduledValues: ReturnType<typeof vi.fn>;
};

function createAudioParam(value = 0): MockAudioParam {
  return {
    value,
    setTargetAtTime: vi.fn(function setTarget(this: MockAudioParam, nextValue: number) {
      this.value = nextValue;
    }),
    setValueAtTime: vi.fn(function setValue(this: MockAudioParam, nextValue: number) {
      this.value = nextValue;
    }),
    cancelScheduledValues: vi.fn(),
  };
}

class MockAudioNode {
  connect = vi.fn(() => this);
  disconnect = vi.fn();
}

class MockGainNode extends MockAudioNode {
  gain = createAudioParam(1);
}

class MockDelayNode extends MockAudioNode {
  delayTime = createAudioParam(0);
}

class MockBufferSourceNode extends MockAudioNode {
  buffer: unknown = null;
  loop = false;
  start = vi.fn();
  stop = vi.fn();
}

class MockAudioBuffer {
  duration: number;
  private readonly data: Float32Array;

  constructor(length: number, sampleRate: number) {
    this.duration = length / sampleRate;
    this.data = new Float32Array(length);
  }

  getChannelData() {
    return this.data;
  }
}

class MockAudioContext {
  currentTime = 0;
  destination = new MockAudioNode();
  sampleRate = 48_000;
  state: AudioContextState = "running";
  createGain = vi.fn(() => new MockGainNode());
  createDelay = vi.fn(() => new MockDelayNode());
  createMediaElementSource = vi.fn(() => new MockAudioNode());
  createBuffer = vi.fn(
    (_channels: number, length: number, sampleRate: number) => new MockAudioBuffer(length, sampleRate),
  );
  createBufferSource = vi.fn(() => new MockBufferSourceNode());
  close = vi.fn(() => Promise.resolve());
  resume = vi.fn(() => Promise.resolve());
}

const localStorageMock = {
  getItem: vi.fn(() => null),
  setItem: vi.fn(),
  removeItem: vi.fn(),
  clear: vi.fn(),
};

describe("AudioPlayer", () => {
  beforeEach(() => {
    mockUseAuth.mockReturnValue({ user: { kind: "user", uiPreferences: {} } });
    vi.stubGlobal("AudioContext", MockAudioContext);
    vi.stubGlobal("localStorage", localStorageMock);
    Object.defineProperty(window, "localStorage", {
      configurable: true,
      value: localStorageMock,
    });
    localStorageMock.getItem.mockReturnValue(null);
    localStorageMock.setItem.mockClear();
    localStorageMock.removeItem.mockClear();
    localStorageMock.clear.mockClear();
    Object.defineProperty(HTMLMediaElement.prototype, "play", {
      configurable: true,
      value: vi.fn(() => Promise.resolve()),
    });
    Object.defineProperty(HTMLMediaElement.prototype, "pause", {
      configurable: true,
      value: vi.fn(),
    });
  });

  afterEach(() => {
    vi.restoreAllMocks();
    vi.unstubAllGlobals();
  });

  function renderPlayer() {
    return render(
      <AudioPlayer
        streamUrl="/api/audios/1/stream"
        format="mp3"
        title="Night Drive"
        subtitle="Riley Hart"
        duration={240}
        trackingEnabled={false}
      />,
    );
  }

  it("uses the configured skip interval for transport buttons", () => {
    mockUseAuth.mockReturnValue({ user: { kind: "user", uiPreferences: { playback: { skipSeconds: 42 } } } });

    renderPlayer();

    expect(screen.getByRole("button", { name: "Back 42 seconds" })).toBeInTheDocument();
    expect(screen.getByRole("button", { name: "Forward 42 seconds" })).toBeInTheDocument();
  });

  it("pins control flyouts on click until clicking outside", async () => {
    renderPlayer();

    const pitchButton = screen.getByRole("button", { name: "Pitch: 0 st" });
    const pitchControl = pitchButton.closest("[data-audio-control]")!;

    fireEvent.pointerEnter(pitchControl);
    expect(pitchButton).toHaveAttribute("aria-expanded", "true");

    fireEvent.pointerEnter(screen.getByLabelText("Pitch adjustment"));
    expect(pitchButton).toHaveAttribute("aria-expanded", "true");

    fireEvent.pointerLeave(pitchControl);
    expect(pitchButton).toHaveAttribute("aria-expanded", "false");

    fireEvent.pointerDown(pitchButton);
    expect(pitchButton).toHaveAttribute("aria-expanded", "true");

    fireEvent.pointerLeave(pitchControl);
    expect(pitchButton).toHaveAttribute("aria-expanded", "true");

    fireEvent.pointerDown(document.body);
    await waitFor(() => expect(pitchButton).toHaveAttribute("aria-expanded", "false"));
  });

  it("keeps manual pitch adjustment separate from playback speed", async () => {
    renderPlayer();

    const audio = document.querySelector("audio")!;
    const speed = screen.getByLabelText("Playback speed") as HTMLInputElement;
    const pitch = screen.getByLabelText("Pitch adjustment") as HTMLInputElement;

    fireEvent.input(speed, { target: { value: "13" } });
    await waitFor(() => expect(audio.playbackRate).toBe(1.5));
    expect(audio.preservesPitch).toBe(true);

    fireEvent.input(pitch, { target: { value: "7" } });
    await waitFor(() => expect(audio.dataset.pitchShiftSemitones).toBe("7"));
    expect(audio.dataset.pitchShiftActive).toBe("true");
    expect(audio.playbackRate).toBe(1.5);
    expect(audio.preservesPitch).toBe(true);
  });
});
