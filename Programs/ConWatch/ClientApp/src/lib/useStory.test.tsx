import { afterEach, describe, expect, it, vi } from "vitest";
import { act } from "react";
import { createRoot, type Root } from "react-dom/client";
import { useStory, type Story } from "./story";

const api = vi.hoisted(() => ({
  fetchTransitionsNewestFirst: vi.fn(async () => []),
  fetchGlitchesOrNull: vi.fn(async () => []),
}));
vi.mock("./api", () => api);

(globalThis as { IS_REACT_ACT_ENVIRONMENT?: boolean }).IS_REACT_ACT_ENVIRONMENT = true;

let seen: Story | null = null;
function Probe({ refreshKey }: { refreshKey: number }) {
  seen = useStory({}, 7, refreshKey);
  return null;
}

let root: Root | null = null;
afterEach(() => {
  if (root) act(() => root!.unmount());
  root = null; seen = null; vi.clearAllMocks();
});

describe("useStory", () => {
  it("fetches on mount and again when refreshKey changes", async () => {
    const el = document.createElement("div");
    root = createRoot(el);
    await act(async () => { root!.render(<Probe refreshKey={1} />); });
    await vi.waitFor(() => expect(seen).not.toBeNull());
    expect(api.fetchTransitionsNewestFirst).toHaveBeenCalledTimes(1);

    await act(async () => { root!.render(<Probe refreshKey={2} />); });
    await vi.waitFor(() => expect(api.fetchTransitionsNewestFirst).toHaveBeenCalledTimes(2));
  });

  it("does not let a superseded fetch overwrite the newer story", async () => {
    vi.useFakeTimers({ toFake: ["Date"] });
    try {
      let releaseOld!: () => void;
      vi.setSystemTime(1_000_000);
      api.fetchTransitionsNewestFirst.mockImplementationOnce(
        () => new Promise((r) => { releaseOld = () => r([]); }));
      const el = document.createElement("div");
      root = createRoot(el);
      await act(async () => { root!.render(<Probe refreshKey={1} />); });

      vi.setSystemTime(2_000_000);
      await act(async () => { root!.render(<Probe refreshKey={2} />); });
      await vi.waitFor(() => expect(seen?.now.getTime()).toBe(2_000_000));

      await act(async () => { releaseOld(); });
      await Promise.resolve();
      expect(seen?.now.getTime()).toBe(2_000_000);
    } finally {
      vi.useRealTimers();
    }
  });

  it("leaves the story null, and nothing unhandled, when the fetch fails", async () => {
    const unhandled: unknown[] = [];
    const onUnhandled = (e: PromiseRejectionEvent) => { unhandled.push(e.reason); };
    window.addEventListener("unhandledrejection", onUnhandled);
    api.fetchTransitionsNewestFirst.mockRejectedValueOnce(new Error("boom"));
    const el = document.createElement("div");
    root = createRoot(el);
    await act(async () => { root!.render(<Probe refreshKey={1} />); });
    await vi.waitFor(() => expect(api.fetchGlitchesOrNull).toHaveBeenCalled());
    await new Promise((r) => setTimeout(r, 0));
    window.removeEventListener("unhandledrejection", onUnhandled);
    expect(seen).toBeNull();
    expect(unhandled).toEqual([]);
  });
});
