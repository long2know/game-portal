import "./shared.css";
import { apiCode } from "../api";
import { catalog } from "../catalog";
import type { ExpansionId } from "../catalog";
import { button, el, text } from "../dom";

export function gameFrame(game: ExpansionId, category: string) {
  const { name, description } = catalog[game];
  const heading = el("h1", "", name);
  heading.tabIndex = -1;
  const notice = el("p", "room-notice");
  notice.setAttribute("role", "status");
  notice.hidden = true;
  const root = el("article", "feature", el("header", "feature-head",
    el("p", "eyebrow", category), heading, el("p", "intro", description)), notice);
  return {
    root, heading,
    say(message: string) { notice.hidden = !message; text(notice, message); },
  };
}

export function field(caption: string, input: HTMLInputElement | HTMLSelectElement) {
  return el("label", "field", el("span", "", caption), input);
}

export function expired(error: unknown): boolean { return apiCode(error) === "not_authenticated"; }

export function messageFor(error: unknown, fallback: string): string {
  switch (apiCode(error)) {
    case "not_authenticated": return "Your guest session ended. Reconnect to continue.";
    case "busy": return "The court is busy. Wait a moment, then try again.";
    default: return fallback;
  }
}

export function invitationField() {
  const input = el("input");
  input.readOnly = true;
  input.spellcheck = false;
  const status = el("p", "copy-status");
  status.setAttribute("role", "status");
  const copy = button("Copy invite link", "button primary", async () => {
    try {
      await navigator.clipboard.writeText(input.value);
      text(status, "Link copied.");
    } catch {
      input.focus();
      input.select();
      text(status, "Link selected. Copy it to send your invitation.");
    }
  });
  return {
    root: el("section", "feature-panel invite-panel", el("h2", "", "Send the invitation"),
      el("p", "muted", "Anyone with this link can take an open seat."), field("Invite link", input), copy, status),
    setValue(value: string) { if (input.value !== value) { input.value = value; text(status, ""); } },
  };
}

export function makeGrid(rows: number, columns: number, cell: (index: number) => HTMLButtonElement, name: string) {
  const root = el("div", "feature-grid");
  root.setAttribute("role", "group");
  root.setAttribute("aria-label", name);
  root.style.setProperty("--columns", String(columns));
  const cells = Array.from({ length: rows * columns }, (_, index) => cell(index));
  let active = 0;
  for (const [index, control] of cells.entries()) {
    control.tabIndex = index === 0 ? 0 : -1;
    control.addEventListener("focus", () => {
      cells[active].tabIndex = -1;
      active = index;
      control.tabIndex = 0;
    });
  }
  root.append(...cells);
  root.addEventListener("keydown", event => {
    const column = active % columns;
    const next = {
      ArrowLeft: active - Number(column > 0),
      ArrowRight: active + Number(column < columns - 1),
      ArrowUp: active - columns,
      ArrowDown: active + columns,
      Home: event.ctrlKey ? 0 : active - column,
      End: event.ctrlKey ? cells.length - 1 : active + columns - 1 - column,
    }[event.key];
    if (next === undefined) return;
    event.preventDefault();
    cells[next]?.focus();
  });
  return { root, cells };
}
