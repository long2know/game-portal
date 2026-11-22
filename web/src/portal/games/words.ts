import "./words.css";
import { button, el, text } from "../dom";
import type { PortalHost } from "../host";
import { Multiplayer, nameAt } from "./multiplayer";
import type { MatchView, MultiplayerTable } from "./multiplayer";
import { makeGrid } from "./shared";

type Tile = { id: number; letter: string; points: number };
type Placement = { row: number; column: number; tileId: number };
type Position = {
  rows: number; columns: number; turn: number | null; status: string; winners: number[]; detail: string | null;
  board: { row: number; column: number; tile: Tile; seat: number }[]; rack: Tile[];
  scores: number[]; rackSizes: number[]; bagCount: number; lastMove: string | null;
};
type Action = { kind: "place"; placements: Placement[] } | { kind: "pass" } | { kind: "exchange"; exchange: number[] };
const exchangeLimit = 3;

function tileArt({ letter, points }: Tile): HTMLElement {
  const tile = el("span", "word-tile", el("span", "", letter), el("small", "", String(points)));
  tile.setAttribute("aria-hidden", "true");
  return tile;
}

function view(play: (action: Action) => void): MatchView<Position> {
  let table: MultiplayerTable<Position> | undefined;
  let position: Position | undefined;
  let canPlay = false;
  let stamp = "";
  let selected: number | undefined;
  let exchangeMode = false;
  const draft = new Map<number, number>();
  const exchange = new Set<number>();
  const board = el("div", "word-board-host");
  const pan = el("div", "feature-scroll", board);
  let grid: ReturnType<typeof makeGrid> | undefined;
  let dimensions = "";
  const rack = el("div", "word-rack");
  rack.setAttribute("role", "group");
  rack.setAttribute("aria-label", "Your tile rack");
  const rackHeading = el("h2", "", "Your rack");
  rackHeading.tabIndex = -1;
  const turn = el("p", "feature-status");
  const last = el("p", "feature-hint");
  const detail = el("p", "feature-hint");
  const bag = el("p", "eyebrow");
  const scores = el("ul", "word-scores");
  scores.setAttribute("aria-label", "Scores and rack sizes");
  turn.setAttribute("role", "status");
  const hint = el("p", "feature-hint");
  hint.setAttribute("role", "status");
  const instructions = el("p", "feature-hint",
    "Build across the center to start, then join an existing word. Swipe or scroll the board, or use arrow keys and Enter.");
  instructions.id = "word-board-help";
  const recall = button("Recall tiles", "button secondary", () => { if (draft.size) { resetDraft(); update(); } });
  const exchangeToggle = button("Exchange tiles", "button secondary", () => {
    if (!canPlay || draft.size || (!exchangeMode && !position?.bagCount)) return;
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
    if (exchangeMode && exchange.size && exchange.size <= position.bagCount)
      play({ kind: "exchange", exchange: [...exchange] });
    else if (!exchangeMode && draft.size)
      play({ kind: "place", placements: [...draft].map(([index, tileId]) =>
        ({ row: Math.floor(index / position!.columns), column: index % position!.columns, tileId })) });
  });
  const root = el("div", "feature-stack word-layout",
    el("section", "feature-panel word-felt", turn, scores, bag, last, detail, instructions, pan),
    el("section", "feature-panel", rackHeading, rack, hint, el("div", "feature-controls", submit, recall, exchangeToggle, pass)));

  function resetDraft(): void {
    selected = undefined;
    exchangeMode = false;
    draft.clear();
    exchange.clear();
  }

  function chooseTile(id: number): void {
    if (!canPlay || !position) return;
    if (exchangeMode) {
      if (!exchange.delete(id) && exchange.size < Math.min(exchangeLimit, position.bagCount)) exchange.add(id);
    } else {
      selected = selected === id ? undefined : id;
    }
    update();
  }

  function place(index: number, row: number, column: number): void {
    if (!canPlay || !position || exchangeMode || position.board.some(cell => cell.row === row && cell.column === column)) return;
    if (draft.delete(index)) { update(); return; }
    if (selected === undefined) return;
    draft.set(index, selected);
    selected = undefined;
    update();
  }

  function update(): void {
    if (!table || !position) return;
    const { rows, columns } = position;
    const { seat } = table;
    const active = document.activeElement;
    const activeTile = active instanceof HTMLElement ? active.dataset.tile : undefined;
    const outcome = position.winners.length !== 1 ? "The game is tied." :
      position.winners[0] === seat ? "You won." : `${nameAt(table, position.winners[0])} wins.`;
    text(turn, table.status === "finished" || position.status === "finished" ? outcome :
      position.turn === seat ? "Your turn" : position.turn != null ? `${nameAt(table, position.turn)}’s turn` : "Waiting for the next turn");
    text(last, position.lastMove ? `Last move: ${position.lastMove}` : "No moves yet.");
    text(detail, position.detail ?? "");
    detail.hidden = !position.detail;
    text(bag, `${position.bagCount} tiles in the bag`);
    scores.replaceChildren(...table.seats.map(item => el("li", "", el("strong", "", nameAt(table!, item.seat)),
      el("span", "", `${position!.scores[item.seat]} pts · ${position!.rackSizes[item.seat]} tiles`))));

    if (!grid || dimensions !== `${rows}/${columns}`) {
      dimensions = `${rows}/${columns}`;
      grid = makeGrid(rows, columns, index => {
        const row = Math.floor(index / columns), column = index % columns;
        const cell = button("", "word-cell", () => place(index, row, column));
        cell.dataset.wordIndex = String(index);
        return cell;
      }, "Word Tiles board");
      grid.root.setAttribute("aria-describedby", instructions.id);
      board.replaceChildren(grid.root);
      pan.scrollLeft = (pan.scrollWidth - pan.clientWidth) / 2;
    }
    const occupied = new Map(position.board.map(cell => [cell.row * columns + cell.column, cell]));
    const inRack = new Map(position.rack.map(tile => [tile.id, tile]));
    const center = Math.floor(rows / 2) * columns + Math.floor(columns / 2);
    for (const [index, cell] of grid.cells.entries()) {
      const current = occupied.get(index);
      const drafted = draft.get(index);
      const tile = current?.tile ?? (drafted === undefined ? undefined : inRack.get(drafted));
      cell.toggleAttribute("data-center", index === center);
      cell.toggleAttribute("data-draft", drafted !== undefined);
      cell.setAttribute("aria-disabled", String(!canPlay || exchangeMode || !!current || (drafted === undefined && selected === undefined)));
      const coordinate = `Row ${Math.floor(index / columns) + 1}, column ${index % columns + 1}`;
      cell.setAttribute("aria-label", `${coordinate}, ${tile ?
        `${tile.letter}, ${tile.points} points${drafted !== undefined ? ", draft" : ""}` :
        index === center ? "center, empty" : "empty"}`);
      cell.replaceChildren(...(tile ? [tileArt(tile)] : index === center ? ["◆"] : []));
    }
    const used = new Set(draft.values());
    const rackButtons = position.rack.filter(tile => !used.has(tile.id)).map(tile => {
      const control = button("", "word-rack-tile", () => chooseTile(tile.id));
      control.dataset.tile = String(tile.id);
      control.setAttribute("aria-label", `${tile.letter}, ${tile.points} points · tile ${position!.rack.indexOf(tile) + 1}`);
      control.setAttribute("aria-pressed", String(exchangeMode ? exchange.has(tile.id) : selected === tile.id));
      control.setAttribute("aria-disabled", String(!canPlay));
      control.append(tileArt(tile));
      return control;
    });
    rack.replaceChildren(...rackButtons);
    if (activeTile != null) (rackButtons.find(control => control.dataset.tile === activeTile) ?? rackHeading).focus({ preventScroll: true });
    text(hint, !canPlay ? "You can review the board and rack." : exchangeMode ?
      `Select up to ${Math.min(exchangeLimit, position.bagCount)} to exchange. ${exchange.size} selected.` :
      selected !== undefined ? "Choose an empty square to place the selected letter." :
        draft.size ? `${draft.size} tiles in your draft. Select a drafted square to recall its tile.` :
          "Choose a rack tile to start a draft.");
    recall.disabled = !canPlay || !draft.size;
    exchangeToggle.disabled = !canPlay || !!draft.size || (!exchangeMode && !position.bagCount);
    text(exchangeToggle, exchangeMode ? "Cancel exchange" : "Exchange tiles");
    pass.disabled = !canPlay || !!draft.size || exchangeMode;
    text(submit, exchangeMode ? "Exchange selected" : "Submit word");
    submit.disabled = !canPlay || (exchangeMode ? !exchange.size : !draft.size);
  }

  root.addEventListener("keydown", event => {
    if (event.key === "Escape" && canPlay && (draft.size || exchangeMode || selected !== undefined)) {
      resetDraft();
      update();
    }
  });
  return {
    root,
    show(next, enabled) {
      const nextStamp = `${next.id}/${next.revision}`;
      if (stamp !== nextStamp) resetDraft();
      stamp = nextStamp;
      table = next;
      position = next.position!;
      canPlay = enabled && position.turn === next.seat;
      update();
    },
  };
}

export function mount(host: PortalHost): void {
  new Multiplayer(host, { game: "word-tiles", endpoint: "words", playerCounts: [2], view });
}
