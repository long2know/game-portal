import "./words.css";
import { catalog } from "../catalog";
import { button, el, text } from "../dom";
import type { PortalHost } from "../host";
import { Multiplayer, nameAt } from "./multiplayer";
import type { MatchView, MultiplayerTable } from "./multiplayer";
import { makeGrid } from "./shared";

type Tile = { id: number; letter: string; points: number; blank: boolean };
type DraftTile = { tileId: number; letter?: string };
type Placement = DraftTile & { row: number; column: number };
const premiumMarks = {
  "double-letter": ["2L", "Double letter"],
  "triple-letter": ["3L", "Triple letter"],
  "double-word": ["2W", "Double word"],
  "triple-word": ["3W", "Triple word"],
} as const;
type Premium = keyof typeof premiumMarks;
type Position = {
  variant: "duel" | "classic" | "quick"; rackSize: number; exchangeLimit: number;
  rows: number; columns: number; turn: number | null; status: string; winners: number[]; detail: string | null;
  premiums: { row: number; column: number; kind: Premium }[];
  board: { row: number; column: number; tile: Tile; seat: number; recent?: boolean }[]; rack: Tile[];
  scores: number[]; rackSizes: number[]; bagCount: number; lastMove: string | null;
};
type Action = { kind: "place"; placements: Placement[] } | { kind: "pass" } | { kind: "exchange"; exchange: number[] };

function tileName({ letter, points, blank }: Tile): string {
  if (blank) return letter === "?" ? "Blank, 0 points" : `${letter}, blank, 0 points`;
  return `${letter}, ${points} ${points === 1 ? "point" : "points"}`;
}

function tileArt({ letter, points, blank }: Tile): HTMLElement {
  const tile = el("span", "word-tile", el("span", "", letter), el("small", "", String(blank ? 0 : points)));
  tile.toggleAttribute("data-blank", blank);
  tile.setAttribute("aria-hidden", "true");
  return tile;
}

function view(play: (action: Action) => void): MatchView<Position> {
  let table: MultiplayerTable<Position> | undefined;
  let position: Position | undefined;
  let canPlay = false;
  let selected: number | undefined;
  let exchangeMode = false;
  const draft = new Map<number, DraftTile>();
  const exchange = new Set<number>();
  let choice: { index: number; tileId: number; square: HTMLButtonElement } | undefined;
  const board = el("div", "word-board-host");
  const pan = el("div", "feature-scroll word-board-pan", board);
  let grid: ReturnType<typeof makeGrid> | undefined;
  let center = 0;
  const rackButtons = new Map<number, HTMLButtonElement>();
  const rack = el("div", "word-rack");
  rack.setAttribute("role", "group");
  rack.setAttribute("aria-label", "Your tile rack");
  const rackHeading = el("h2", "", "Your rack");
  rackHeading.tabIndex = -1;
  const variant = el("p", "eyebrow");
  const turn = el("p", "feature-status");
  const last = el("p", "word-last");
  const detail = el("p", "feature-hint");
  const bag = el("p", "word-bag");
  const scores = el("ul", "word-scores");
  scores.setAttribute("role", "list");
  scores.setAttribute("aria-label", "Scores and rack sizes");
  turn.setAttribute("role", "status");
  const hint = el("p", "feature-hint");
  hint.setAttribute("role", "status");
  const instructions = el("p", "feature-hint",
    "Swipe to pan, or use arrows and Enter.");
  instructions.id = "word-board-help";
  const recenter = button("Center board", "button small secondary", () => {
    centerBoard();
    grid?.cells[center].focus({ preventScroll: true });
  });
  const legend = el("ul", "word-legend", ...Object.entries(premiumMarks).map(([kind, [mark, meaning]]) => {
    const swatch = el("span", "word-swatch", mark);
    swatch.dataset.premium = kind;
    return el("li", "", swatch, el("span", "", meaning));
  }));
  legend.setAttribute("role", "list");
  legend.setAttribute("aria-label", "Premium squares");
  const rules = el("p", "feature-hint");
  const recall = button("Recall tiles", "button secondary", () => {
    if (canPlay && draft.size) { resetDraft(); update(); }
  });
  const exchangeToggle = button("Exchange tiles", "button secondary", () => {
    if (!canPlay || draft.size || (!exchangeMode && !position?.exchangeLimit)) return;
    selected = undefined;
    exchange.clear();
    exchangeMode = !exchangeMode;
    update();
  });
  const pass = button("Pass", "button secondary", () => {
    if (canPlay && !draft.size && !exchangeMode) play({ kind: "pass" });
  });
  const submit = button("", "button primary", () => {
    if (!canPlay || !position) return;
    const { columns, exchangeLimit } = position;
    if (exchangeMode && exchange.size && exchange.size <= exchangeLimit)
      play({ kind: "exchange", exchange: [...exchange] });
    else if (!exchangeMode && draft.size)
      play({ kind: "place", placements: [...draft].map(([index, tile]) =>
        ({ row: Math.floor(index / columns), column: index % columns, ...tile })) });
  });
  const pickerTitle = el("h2", "", "Choose a blank letter");
  pickerTitle.id = "word-blank-title";
  const pickerHelp = el("p", "feature-hint", "Pick or type A–Z. A blank always scores 0.");
  pickerHelp.id = "word-blank-help";
  const letters = Array.from("ABCDEFGHIJKLMNOPQRSTUVWXYZ", letter => button(letter, "word-letter", () => assignBlank(letter)));
  const cancel = button("Cancel", "button secondary", () => dismissPicker());
  cancel.autofocus = true;
  const picker = el("dialog", "word-picker", pickerTitle, pickerHelp, el("div", "word-alphabet", ...letters), cancel);
  picker.setAttribute("aria-labelledby", pickerTitle.id);
  picker.setAttribute("aria-describedby", pickerHelp.id);
  picker.addEventListener("cancel", event => {
    event.preventDefault();
    dismissPicker();
  });
  picker.addEventListener("keydown", event => {
    if (event.key === "Escape") {
      event.preventDefault();
      event.stopPropagation();
      dismissPicker();
    } else if (!event.ctrlKey && !event.metaKey && !event.altKey && !event.isComposing && /^[a-z]$/i.test(event.key)) {
      event.preventDefault();
      assignBlank(event.key.toUpperCase());
    }
  });
  const root = el("div", "feature-stack word-layout",
    el("section", "feature-panel word-felt", el("div", "word-meta", turn, el("div", "word-tools", bag, recenter)),
      scores, pan),
    el("section", "feature-panel word-actions", el("div", "word-meta", rackHeading, variant), rack,
      el("div", "feature-controls", submit, recall, exchangeToggle, pass), hint, last, detail,
      el("div", "word-key", legend, rules, instructions)),
    picker);
  // The shared room can hide the match without showing a new position.
  const visibility = new MutationObserver(() => {
    if (root.hidden || root.parentElement?.hidden) dismissPicker(false);
  });

  function centerBoard(): void {
    pan.scrollLeft = (pan.scrollWidth - pan.clientWidth) / 2;
    pan.scrollTop = (pan.scrollHeight - pan.clientHeight) / 2;
  }

  function dismissPicker(restoreFocus = true): void {
    if (!choice) return;
    const { square } = choice;
    choice = undefined;
    visibility.disconnect();
    picker.close();
    if (restoreFocus && square.isConnected) square.focus({ preventScroll: true });
  }

  function assignBlank(letter: string): void {
    if (!canPlay || !choice) return;
    const { index, tileId } = choice;
    draft.set(index, { tileId, letter });
    selected = undefined;
    dismissPicker();
    update();
  }

  function resetDraft(): void {
    dismissPicker();
    selected = undefined;
    exchangeMode = false;
    draft.clear();
    exchange.clear();
  }

  function chooseTile(id: number): void {
    if (!canPlay || !position) return;
    if (exchangeMode) {
      if (!exchange.delete(id) && exchange.size < position.exchangeLimit) exchange.add(id);
    } else {
      selected = selected === id ? undefined : id;
    }
    update();
  }

  function place(index: number, square: HTMLButtonElement): void {
    square.focus({ preventScroll: true });
    if (!canPlay || !position || square.getAttribute("aria-disabled") === "true") return;
    const previous = draft.get(index);
    if (previous) {
      draft.delete(index);
      selected ??= previous.tileId;
      update();
      return;
    }
    const tile = position.rack.find(item => item.id === selected);
    if (!tile) return;
    if (tile.blank) {
      choice = { index, tileId: tile.id, square };
      picker.showModal();
      cancel.focus({ preventScroll: true });
      visibility.observe(root, { attributes: true, attributeFilter: ["hidden"] });
      if (root.parentElement)
        visibility.observe(root.parentElement, { attributes: true, attributeFilter: ["hidden"] });
    } else {
      draft.set(index, { tileId: tile.id });
      selected = undefined;
      update();
    }
  }

  function update(newBoard = false): void {
    if (!table || !position) return;
    const { rows, columns, exchangeLimit, variant: mode, rackSize } = position;
    const { seat } = table;
    const outcome = position.winners.length !== 1 ? "The game is tied." :
      position.winners[0] === seat ? "You won." : `${nameAt(table, position.winners[0])} wins.`;
    text(turn, table.status === "finished" || position.status === "finished" ? outcome :
      position.turn === seat ? "Your turn" : position.turn != null ? `${nameAt(table, position.turn)}’s turn` : "Waiting for the next turn");
    text(last, position.lastMove ? `Last move: ${position.lastMove}` : "No moves yet.");
    text(detail, position.detail ?? "");
    detail.hidden = !position.detail;
    text(bag, `Bag: ${position.bagCount}`);
    const quick = mode === "quick";
    text(variant, `${mode === "duel" ? "Duel" : quick ? "Quick" : "Classic"} · ${rackSize} tiles`);
    legend.hidden = quick;
    text(rules, !quick
      ? `Open through the center. Use each premium once; play all seven tiles for +${mode === "duel" ? 35 : 50}. Blanks are worth 0.`
      : "Open across the center, then connect to letters on the board. Quick uses six tiles and no premium squares.");
    scores.replaceChildren(...table.seats.map(item => {
      const score = el("li", "", el("strong", "word-score-name", nameAt(table!, item.seat)),
        el("span", "word-score-value", String(position!.scores[item.seat]), el("small", "", " pts")),
        el("span", "word-score-rack", `${position!.rackSizes[item.seat]} tiles`));
      score.toggleAttribute("data-active", position!.turn === item.seat);
      return score;
    }));

    if (!grid || newBoard) {
      center = Math.floor(rows / 2) * columns + Math.floor(columns / 2);
      grid = makeGrid(rows, columns, index => {
        const cell = button("", "word-cell", () => place(index, cell));
        cell.dataset.wordIndex = String(index);
        return cell;
      }, `${catalog["word-tiles"].name} board`);
      grid.root.setAttribute("aria-describedby", instructions.id);
      const start = grid.cells[center];
      grid.cells[0].tabIndex = -1;
      start.tabIndex = 0;
      grid.root.addEventListener("focusin", event => { if (event.target !== start) start.tabIndex = -1; }, { once: true });
      board.style.setProperty("--columns", String(columns));
      board.replaceChildren(grid.root);
      centerBoard();
    }
    const occupied = new Map(position.board.map(cell => [cell.row * columns + cell.column, cell]));
    const premiums = new Map((position.premiums ?? []).map(cell => [cell.row * columns + cell.column, cell.kind]));
    const inRack = new Map(position.rack.map(tile => [tile.id, tile]));
    for (const [index, cell] of grid.cells.entries()) {
      const current = occupied.get(index);
      const drafted = draft.get(index);
      const fromRack = drafted && inRack.get(drafted.tileId);
      const tile = current?.tile ?? (fromRack && drafted?.letter ? { ...fromRack, letter: drafted.letter } : fromRack);
      const premium = !tile && premiums.get(index);
      const isCenter = index === center;
      cell.toggleAttribute("data-center", isCenter && !tile);
      cell.toggleAttribute("data-draft", !!drafted);
      cell.toggleAttribute("data-recent", !!current?.recent);
      cell.setAttribute("aria-disabled", String(!canPlay || exchangeMode || !!current || (!drafted && selected === undefined)));
      if (premium) cell.dataset.premium = premium;
      else cell.removeAttribute("data-premium");
      const coordinate = `Row ${Math.floor(index / columns) + 1}, column ${index % columns + 1}`;
      const label = `${coordinate}, ${tile ? `${tileName(tile)}${drafted ? ", draft" : current?.recent ? ", last play" : ""}` :
        `${isCenter ? "center, " : ""}empty${premium ? `, ${premiumMarks[premium][1].toLowerCase()}` : ""}`}`;
      if (cell.getAttribute("aria-label") !== label) {
        cell.setAttribute("aria-label", label);
        cell.replaceChildren(tile ? tileArt(tile) : premium ? premiumMarks[premium][0] :
          isCenter ? mode === "duel" ? el("span", "word-start", "✦") : "START" : "");
      }
    }
    const active = document.activeElement;
    const activeTile = active instanceof HTMLElement && active.parentElement === rack ? Number(active.dataset.tile) : undefined;
    const used = new Set([...draft.values()].map(tile => tile.tileId));
    for (const [id, control] of rackButtons) {
      if (!inRack.has(id)) { control.remove(); rackButtons.delete(id); }
    }
    position.rack.forEach((tile, index) => {
      let control = rackButtons.get(tile.id);
      if (!control) {
        control = button("", "word-rack-tile", () => chooseTile(tile.id));
        control.dataset.tile = String(tile.id);
        control.append(tileArt(tile));
        rackButtons.set(tile.id, control);
      }
      if (rack.children[index] !== control) rack.insertBefore(control, rack.children[index] ?? null);
      control.toggleAttribute("data-used", used.has(tile.id));
      control.setAttribute("aria-label", `${tileName(tile)} · tile ${index + 1}`);
      control.setAttribute("aria-pressed", String(exchangeMode ? exchange.has(tile.id) : selected === tile.id));
      control.setAttribute("aria-disabled", String(!canPlay));
    });
    if (activeTile !== undefined && document.activeElement !== active)
      (rackButtons.get(activeTile) ?? rackHeading).focus({ preventScroll: true });
    text(hint, !canPlay ? "You can review the board and rack." : exchangeMode ?
      `Select up to ${exchangeLimit} to exchange. ${exchange.size} selected.` :
      selected !== undefined ? inRack.get(selected)?.blank
        ? "Blank selected. Choose an empty square, then pick its letter."
        : "Choose an empty square for your tile." :
        draft.size ? `${draft.size} in your draft. Tap a draft tile to recall it.` :
          "Choose a rack tile to start a draft.");
    text(exchangeToggle, exchangeMode ? "Cancel exchange" : "Exchange tiles");
    text(submit, exchangeMode ? "Exchange selected" : "Submit word");
    recall.setAttribute("aria-disabled", String(!canPlay || !draft.size));
    exchangeToggle.setAttribute("aria-disabled", String(!canPlay || !!draft.size || (!exchangeMode && !exchangeLimit)));
    pass.setAttribute("aria-disabled", String(!canPlay || !!draft.size || exchangeMode));
    submit.setAttribute("aria-disabled", String(!canPlay || (exchangeMode ? !exchange.size || exchange.size > exchangeLimit : !draft.size)));
    for (const letter of letters) letter.setAttribute("aria-disabled", String(!canPlay));
  }

  root.addEventListener("keydown", event => {
    if (event.key === "Escape" && canPlay && (draft.size || exchangeMode || selected !== undefined)) {
      event.preventDefault();
      resetDraft();
      update();
    }
  });
  return {
    root,
    show(next, enabled) {
      const incoming = next.position!;
      const newBoard = table?.id !== next.id || position?.rows !== incoming.rows || position?.columns !== incoming.columns;
      if (newBoard || table?.revision !== next.revision) resetDraft();
      table = next;
      position = incoming;
      canPlay = enabled && incoming.turn === next.seat;
      update(newBoard);
    },
  };
}

export function mount(host: PortalHost): void {
  new Multiplayer(host, { game: "word-tiles", endpoint: "words", playerCounts: [2], view });
}
