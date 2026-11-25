import "./mahjong-solitaire.css";
import { faceName, tileFace } from "../../tile";
import { Confirmation } from "../confirm";
import { button, el, text } from "../dom";
import type { PortalHost } from "../host";
import { mahjongSolitaireProgressKey } from "./mahjong-solitaire-save";
import { MahjongStack } from "./mahjong-stack";
import type { TilePair } from "./mahjong-stack";

const freshSeed = () => crypto.getRandomValues(new Uint32Array(1))[0];

interface DailyStack {
  seed: number;
  saved: string | null;
  current: () => boolean;
  storageAvailable: boolean;
  save: (snapshot: string, won: boolean) => boolean;
}

export function mount({ main, mount }: Pick<PortalHost, "main" | "mount">, daily?: DailyStack): void {
  const key = mahjongSolitaireProgressKey(mount);
  let saved: MahjongStack | null = null;
  if (daily) {
    saved = MahjongStack.restore(daily.saved);
    if (saved?.seed !== daily.seed) saved = null;
  } else {
    try { saved = MahjongStack.restore(localStorage.getItem(key)); } catch { /* Storage is optional. */ }
  }
  let stack = saved ?? new MahjongStack(daily?.seed ?? freshSeed());
  let chosen: number | null = null;
  let hinted: TilePair | undefined;
  let focused: number | null = null;

  const rule = "Match two identical tiles that have nothing above them and at least one open side.";
  const heading = el("h1", "", "Mahjong Solitaire");
  heading.id = "stack-title";
  const header = el("header", "stack-header", el("p", "eyebrow", "The lantern stack"), heading,
    ...(daily ? [el("p", "intro", "Clear today’s four-tier stack by matching all 78 tiles.")] : []));
  const well = el("div", "stack-board");
  well.setAttribute("role", "group");
  well.setAttribute("aria-label", "Lantern stack tiles");
  well.setAttribute("aria-describedby", "stack-rules stack-keys");
  const tally = el("p", "stack-count");
  const feedback = el("p", "stack-feedback");
  feedback.setAttribute("role", "status");
  const storage = el("p", "muted stack-storage", "Browser storage is unavailable. Progress lasts until you leave or reload.");
  const confirmation = new Confirmation();
  const hint = button("Hint", "button secondary", suggest);
  const undo = button("Undo", "button secondary", takeBack);
  const restartLabel = daily ? "Restart today’s deal" : "Restart deal";
  const restart = button(restartLabel, "button secondary", () => confirmation.show(restartLabel,
    "This brings back every tile in the same deal and clears your moves.", restart, () => reset(stack.seed, "This deal has been restarted.")));
  const newGame = daily ? null : button("New game", "button primary", askNew);
  const nextDeal = newGame ?? restart;
  const instructions = el("p", "stack-rule", rule);
  instructions.id = "stack-rules";
  const keyboard = el("p", "stack-keys", "Arrow keys move among free tiles. Enter or Space selects; Escape clears selection.");
  keyboard.id = "stack-keys";
  const panel = el("section", "stack-panel",
    el("div", "stack-ledger", el("h2", "", "Your deal"), tally),
    feedback, el("div", "stack-actions", hint, undo, restart, ...(newGame ? [newGame] : [])),
    instructions, el("p", "stack-keys", "Open tiles have a small corner mark. Darker tiles are covered or blocked on both sides."),
    keyboard, storage);
  const game = el("section", "mahjong-solitaire", header,
    el("div", "stack-layout", el("div", "board-frame stack-frame", well), panel), confirmation.root);
  game.setAttribute("aria-labelledby", heading.id);
  const tiles = stack.tiles.map(({ id, x, y, level }) => {
    const tile = button("", "stack-tile", () => select(id));
    tile.dataset.tile = String(id);
    tile.dataset.level = String(level);
    tile.style.setProperty("--tier", String(level));
    tile.style.setProperty("--x", String(x));
    tile.style.setProperty("--y", String(y));
    tile.tabIndex = -1;
    tile.addEventListener("focus", () => {
      if (stack.state(id) === "free") rove(id);
    });
    return tile;
  });
  well.append(...tiles);
  well.addEventListener("keydown", navigate);
  main.append(game);
  paintFaces();
  render(saved?.canUndo ? "Your saved deal is ready." : "Choose any open tile to start.");
  if (daily) storage.hidden = daily.storageAvailable;
  else {
    persist();
    const url = new URL(location.href);
    if (url.searchParams.get("fresh") === "1") {
      url.searchParams.delete("fresh");
      history.replaceState(history.state, "", url);
      askNew();
    }
  }

  function persist(): void {
    if (daily) {
      if (daily.current()) storage.hidden = daily.save(stack.save(), stack.remaining === 0);
      return;
    }
    try {
      localStorage.setItem(key, stack.save());
      storage.hidden = true;
    } catch { storage.hidden = false; }
  }

  function paintFaces(): void {
    for (const { id, face } of stack.tiles) tiles[id].replaceChildren(tileFace(face));
  }

  function rove(id: number | null): void {
    if (focused !== null) tiles[focused].tabIndex = -1;
    if (id !== null) tiles[id].tabIndex = 0;
    focused = id;
  }

  function render(notice: string): void {
    const restoreFocus = well.contains(document.activeElement);
    const available = !!stack.hint(null);
    const outcome = stack.remaining === 0 ? "won" : available ? "playing" : "stuck";
    game.dataset.state = outcome;
    for (const tile of stack.tiles) {
      const { id, face, x, y, level } = tile;
      const control = tiles[id];
      const state = stack.state(id);
      control.hidden = state === "removed";
      control.dataset.state = state;
      control.toggleAttribute("data-selected", chosen === id);
      control.toggleAttribute("data-hinted", hinted?.includes(id) ?? false);
      control.setAttribute("aria-label", `${faceName(face)}, tier ${level + 1}, row ${Math.floor(y / 2) + 1}, column ${Math.floor(x / 2) + 1}, ` +
        (state === "boxed" ? "blocked on both sides" : state === "free" ? "open" : state));
      control.setAttribute("aria-disabled", String(state !== "free"));
      if (state === "free") control.setAttribute("aria-pressed", String(chosen === id));
      else control.removeAttribute("aria-pressed");
    }
    if (focused === null || stack.state(focused) !== "free") {
      const next = stack.tiles.find(({ id }) => stack.state(id) === "free")?.id ?? null;
      rove(next);
      if (restoreFocus) (next === null ? nextDeal : tiles[next]).focus();
    }
    if (document.activeElement === hint && !available) (stack.canUndo ? undo : nextDeal).focus();
    if (document.activeElement === undo && !stack.canUndo) hint.focus();
    text(tally, `${stack.remaining} / ${tiles.length} tiles left`);
    text(feedback, outcome === "won" ? "Stack cleared. You matched every tile!" :
      outcome === "stuck" ? daily ? "No free pair remains. Undo a match or restart today’s deal." :
        "No free pair remains. Undo a match, restart this deal, or start a new one." : notice);
    hint.disabled = !available;
    undo.disabled = !stack.canUndo;
  }

  function select(id: number): void {
    if (daily && !daily.current()) return;
    const state = stack.state(id);
    if (state === "removed") return;
    if (state !== "free") {
      render(state === "covered" ? "That tile is covered by a higher tier." : "That tile needs an open side.");
      return;
    }
    rove(id);
    hinted = undefined;
    if (chosen === id) {
      chosen = null;
      render("Selection cleared.");
    } else if (chosen !== null && stack.match(chosen, id)) {
      chosen = null;
      render(`Matched ${faceName(stack.tiles[id].face)}.`);
      persist();
    } else {
      const prefix = chosen === null ? "Selected" : "Not a match. Selected";
      chosen = id;
      render(`${prefix} ${faceName(stack.tiles[id].face)}.`);
    }
  }

  function suggest(): void {
    if (daily && !daily.current()) return;
    hinted = stack.hint(chosen);
    if (!hinted) return;
    const first = hinted[0];
    if (chosen !== first) chosen = null;
    rove(first);
    render(`Hint: try the two marked ${faceName(stack.tiles[first].face)} tiles.`);
    tiles[first].focus();
  }

  function takeBack(): void {
    if (daily && !daily.current()) return;
    const pair = stack.undo();
    if (!pair) return;
    chosen = null;
    hinted = undefined;
    rove(pair[0]);
    render("Last pair restored.");
    persist();
    tiles[pair[0]].focus();
  }

  function reset(seed: number, notice: string): void {
    if (daily && !daily.current()) return;
    stack = new MahjongStack(seed);
    chosen = null;
    hinted = undefined;
    rove(null);
    paintFaces();
    render(notice);
    persist();
  }

  function askNew(): void {
    if (!newGame) return;
    confirmation.show("New game", "Start a new deal? This replaces the deal saved in this browser.", newGame, () => {
      const seed = freshSeed();
      reset(seed === stack.seed ? (seed + 1) >>> 0 : seed, "A new solvable deal is ready.");
    });
  }

  function navigate(event: KeyboardEvent): void {
    if (!(event.target instanceof HTMLButtonElement) || event.target.dataset.tile === undefined) return;
    if (event.key === "Escape") {
      if (chosen === null && !hinted) return;
      event.preventDefault();
      chosen = null;
      hinted = undefined;
      render("Selection cleared.");
      return;
    }
    const axis = event.key === "ArrowLeft" || event.key === "ArrowRight" ? "x" :
      event.key === "ArrowUp" || event.key === "ArrowDown" ? "y" : null;
    if (!axis) return;
    event.preventDefault();
    const from = stack.tiles[Number(event.target.dataset.tile)];
    const cross = axis === "x" ? "y" : "x";
    const sign = event.key === "ArrowLeft" || event.key === "ArrowUp" ? -1 : 1;
    const score = (tile: typeof from) => Math.abs(tile[axis] - from[axis]) +
      Math.abs(tile[cross] - from[cross]) * 2 + Math.abs(tile.level - from.level) / 4;
    const next = stack.tiles.filter(tile => stack.state(tile.id) === "free" && (tile[axis] - from[axis]) * sign > 0)
      .sort((a, b) => score(a) - score(b))[0];
    if (next) tiles[next.id].focus();
  }
}
