import { UNDECIDED, type Mark } from "./nonogram-logic";
import { inkPlates } from "./nonogram-puzzles";

const SAVE_LIMIT = 8192;
const UNDO_LIMIT = 32;

type Change = [cell: number, previous: Mark];
export interface InkBoard {
  marks: Mark[];
  undo: Change[];
}

export interface InkProgress {
  active: string;
  boards: Map<string, InkBoard>;
}

export const blankBoard = (size: number): InkBoard => ({ marks: Array<Mark>(size).fill(UNDECIDED), undo: [] });

export function applyMark(board: InkBoard, cell: number, mark: Mark): boolean {
  const previous = board.marks[cell];
  if (previous === undefined || previous === mark) return false;
  board.undo.push([cell, previous]);
  if (board.undo.length > UNDO_LIMIT) board.undo.shift();
  board.marks[cell] = mark;
  return true;
}

export function undoMark(board: InkBoard): number | undefined {
  const change = board.undo.pop();
  if (!change) return undefined;
  const [cell, previous] = change;
  board.marks[cell] = previous;
  return cell;
}

function record(value: unknown): value is Record<string, unknown> {
  return value !== null && typeof value === "object" && !Array.isArray(value);
}

function readUndo(value: unknown, marks: readonly Mark[]): Change[] {
  if (!Array.isArray(value) || value.length > UNDO_LIMIT) return [];
  const undo: Change[] = [];
  const working = [...marks];
  for (let i = value.length - 1; i >= 0; i--) {
    const change: unknown = value[i];
    if (!Array.isArray(change) || change.length !== 2) return [];
    const [cell, previous] = change;
    if (!Number.isInteger(cell) || cell < 0 || cell >= working.length ||
        (previous !== 0 && previous !== 1 && previous !== 2) || previous === working[cell]) return [];
    undo.push([cell, previous]);
    working[cell] = previous;
  }
  return undo.reverse();
}

export function readProgress(raw: string | null): InkProgress {
  const progress: InkProgress = { active: inkPlates[0].id, boards: new Map() };
  if (!raw || raw.length > SAVE_LIMIT) return progress;
  let parsed: unknown;
  try { parsed = JSON.parse(raw); } catch { return progress; }
  if (!record(parsed) || parsed.version !== 1 || !record(parsed.boards)) return progress;
  for (const plate of inkPlates) {
    if (plate.id === parsed.active) progress.active = plate.id;
    const board = parsed.boards[plate.id];
    if (!record(board) || typeof board.marks !== "string" ||
        board.marks.length !== plate.rows.length * plate.columns.length || /[^012]/.test(board.marks)) continue;
    const marks = Array.from(board.marks, mark => Number(mark) as Mark);
    progress.boards.set(plate.id, { marks, undo: readUndo(board.undo, marks) });
  }
  return progress;
}

export function writeProgress(progress: InkProgress): string {
  return JSON.stringify({
    version: 1,
    active: progress.active,
    boards: Object.fromEntries(
      Array.from(progress.boards, ([id, { marks, undo }]) => [id, { marks: marks.join(""), undo }]),
    ),
  });
}
