import { describe, expect, it } from "vitest";
import { renderToStaticMarkup } from "react-dom/server";
import { NotesPanel } from "./NotesPanel";
import type { ModelWarning } from "../lib/types";

const w = (over: Partial<ModelWarning> = {}): ModelWarning => ({
  key: "hop-bypassed:1", kind: "HopBypassed", targetId: "1", targetName: "Inside",
  message: "Inside isn't answering its probe (tcp/80), but External beyond it is",
  since: "2026-10-08T23:42:16Z", acknowledged: false, ...over,
});

describe("NotesPanel", () => {
  it("renders nothing without notes", () => {
    expect(renderToStaticMarkup(<NotesPanel warnings={[]} onAcknowledge={() => {}} />)).toBe("");
  });
  it("lists a note with its message and an Acknowledge button for the admin", () => {
    const html = renderToStaticMarkup(<NotesPanel warnings={[w()]} onAcknowledge={() => {}} />);
    expect(html).toContain('id="notes"');
    expect(html).toContain("ConnectionWatcher notes");
    expect(html).toContain("Inside isn&#x27;t answering its probe (tcp/80)");
    expect(html).toContain("Acknowledge");
  });
  it("shows an acknowledged note dimmed, without a button", () => {
    const html = renderToStaticMarkup(<NotesPanel warnings={[w({ acknowledged: true })]} onAcknowledge={() => {}} />);
    expect(html).toContain("acknowledged");
    expect(html).not.toContain("<button");
  });
  it("offers no button without an acknowledge handler", () => {
    expect(renderToStaticMarkup(<NotesPanel warnings={[w()]} />)).not.toContain("<button");
  });
});
