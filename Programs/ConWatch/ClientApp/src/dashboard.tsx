import { createRoot } from "react-dom/client";

const el = document.getElementById("dashboard-root");
if (el) {
  createRoot(el).render(<p>island pending</p>);
}
