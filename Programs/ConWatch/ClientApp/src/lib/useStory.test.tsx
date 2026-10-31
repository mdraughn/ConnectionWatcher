import { afterEach, describe, expect, it, vi } from "vitest";
import { act } from "react";
import { createRoot, type Root } from "react-dom/client";
import { useStory, type Story } from "./story";

const api = vi.hoisted(() => ({
  fetchTransitions: vi.fn(async () => []),
  fetchGlitches: vi.fn(async () => []),
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
    expect(api.fetchTransitions).toHaveBeenCalledTimes(1);

    await act(async () => { root!.render(<Probe refreshKey={2} />); });
    await vi.waitFor(() => expect(api.fetchTransitions).toHaveBeenCalledTimes(2));
  });

  it("ignores a result that arrives after unmount", async () => {
    let release!: () => void;
    api.fetchTransitions.mockImplementationOnce(
      () => new Promise((r) => { release = () => r([]); }));
    const el = document.createElement("div");
    root = createRoot(el);
    act(() => root!.render(<Probe refreshKey={1} />));
    act(() => root!.unmount());
    root = null;
    const errors = vi.spyOn(console, "error").mockImplementation(() => undefined);
    release();
    await vi.waitFor(() => expect(api.fetchGlitches).toHaveBeenCalled());
    await Promise.resolve();
    expect(seen).toBeNull();
    expect(errors).not.toHaveBeenCalled();
    errors.mockRestore();
  });
});
