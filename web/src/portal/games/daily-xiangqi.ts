import type { Move, Position } from "../api";
import { BoardView } from "../board";
import { puzzleIndex } from "../daily-edition";
import type { DailyEdition } from "../daily-edition";
import { button, el, text } from "../dom";
import puzzles from "./daily-xiangqi.json";

const idAt = (row: number, column: number) => `${"abcdefghi"[column]}${10 - row}`;

export function mountDailyXiangqi(main: HTMLElement, edition: DailyEdition, savedMove: string | undefined,
  current: () => boolean, save: (move: string) => boolean, storageAvailable: boolean): void {
  const puzzle = puzzles[puzzleIndex(edition, puzzles.length)];
  const occupancy = new Map(puzzle.pieces.map(piece => [piece.cell, piece]));
  const start: Position = {
    rows: 10, columns: 9, turn: 0, status: "playing", winners: [], detail: null, lastMove: null,
    cells: Array.from({ length: 90 }, (_, at) => {
      const row = Math.floor(at / 9), column = at % 9, id = idAt(row, column);
      const occupant = occupancy.get(id);
      return { id, row, column, owner: occupant?.owner ?? null, piece: occupant?.piece ?? null };
    }),
    moves: puzzle.legal.map(id => {
      const [from, to] = id.split(">");
      return { id, from, to, label: `${from} → ${to}` };
    }),
  };
  let won = puzzle.mate.includes(savedMove ?? "") ? start.moves.find(move => move.id === savedMove) : undefined;
  const opening = puzzle.inCheck ? "Red is in check. The mating move must also get Red out of check." :
    "Stalemate does not count as checkmate.";
  const goal = el("p", "intro");
  const notice = el("p", "daily-attempt");
  notice.setAttribute("role", "status");
  const storage = el("p", "daily-storage muted", "Browser storage is unavailable. Progress lasts until you leave or reload.");
  storage.hidden = storageAvailable;
  const replay = button("Replay today’s tactic", "button secondary", () => {
    if (!current()) return;
    won = undefined;
    render(`Already completed today. ${opening}`);
    board.root.querySelector<HTMLElement>("[tabindex='0']")?.focus();
  });
  const board = new BoardView("xiangqi", move => {
    if (!current() || won) return;
    if (puzzle.mate.includes(move.id)) {
      won = move;
      storage.hidden = save(move.id);
    }
    render("Legal move, but not checkmate. Try again from the same position.");
  });
  const header = el("header", "daily-heading", el("h1", "", "Xiangqi"), goal);
  const table = el("div", "daily-table", board.root,
    el("aside", "daily-guide", el("h2", "", "Today’s tactic"), notice, replay, storage));
  table.dataset.puzzle = puzzle.id;
  main.append(header, table);
  render(opening);

  function render(message: string): void {
    table.dataset.state = won ? "won" : "playing";
    replay.hidden = won === undefined;
    text(goal, won ? "Black is in check and has no legal move." : "Red to move. Checkmate Black in one move.");
    text(notice, won ? "Checkmate! You completed today’s tactic." : message);
    if (!won) {
      board.update(start, true, true, true);
      return;
    }
    const { from, to } = won;
    const source = occupancy.get(from!);
    const cells = start.cells.map(cell => cell.id === from ? { ...cell, owner: null, piece: null } :
      cell.id === to ? { ...cell, owner: source!.owner, piece: source!.piece } : cell);
    board.update({ ...start, cells, status: "finished", turn: null, winners: [0], moves: [], lastMove: won }, false, false, true);
  }
}
