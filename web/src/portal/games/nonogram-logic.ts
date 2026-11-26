export const UNDECIDED = 0;
export const INK = 1;
export const EMPTY = 2;
export type Mark = typeof UNDECIDED | typeof INK | typeof EMPTY;

export interface Clues {
  readonly rows: readonly (readonly number[])[];
  readonly columns: readonly (readonly number[])[];
}

interface Line {
  axis: "row" | "column";
  index: number;
}

export type Hint =
  | { kind: "forced"; cell: number; mark: typeof INK | typeof EMPTY; line: Line }
  | { kind: "contradiction"; line: Line }
  | { kind: "stalled" };

export function runs(marks: Iterable<Mark>): number[] {
  const result: number[] = [];
  let length = 0;
  for (const mark of marks) {
    if (mark === INK) length++;
    else if (length) {
      result.push(length);
      length = 0;
    }
  }
  if (length) result.push(length);
  return result;
}

function lines({ rows, columns }: Clues) {
  return [
    ...rows.map((clues, index) => ({
      line: { axis: "row" as const, index }, cells: columns.map((_, i) => index * columns.length + i), clues,
    })),
    ...columns.map((clues, index) => ({
      line: { axis: "column" as const, index }, cells: rows.map((_, i) => i * columns.length + index), clues,
    })),
  ];
}

export function matchesClues(clues: Clues, marks: readonly Mark[]): boolean {
  return marks.length === clues.rows.length * clues.columns.length && lines(clues).every(line => {
    const actual = runs(line.cells.map(cell => marks[cell]));
    return actual.length === line.clues.length && actual.every((value, i) => value === line.clues[i]);
  });
}

// Each line is at most ten cells; enumerate whole runs with a separating space.
function placements(length: number, clues: readonly number[]): number[] {
  const patterns: number[] = [];
  const tail: number[] = [];
  let remaining = -1;
  for (let i = clues.length - 1; i >= 0; i--) {
    remaining += 1 + clues[i];
    tail[i] = remaining;
  }
  function place(run: number, start: number, pattern: number): void {
    if (run === clues.length) {
      patterns.push(pattern);
      return;
    }
    const width = clues[run];
    for (let offset = start; offset + tail[run] <= length; offset++) {
      place(run + 1, offset + width + 1, pattern | (((1 << width) - 1) << offset));
    }
  }
  place(0, 0, 0);
  return patterns;
}

export function findHint(clues: Clues, marks: readonly Mark[]): Hint {
  const working = [...marks];
  const domains = lines(clues).map(line => ({ ...line, patterns: placements(line.cells.length, line.clues) }));
  let first: Hint | undefined;
  let changed: boolean;
  // Finish propagation before offering the first deduction: a later line may refute the marks.
  do {
    changed = false;
    for (const { line, cells, patterns } of domains) {
      let painted = 0, crossed = 0;
      cells.forEach((cell, bit) => {
        if (working[cell] === INK) painted |= 1 << bit;
        if (working[cell] === EMPTY) crossed |= 1 << bit;
      });
      let possible = 0, required = (1 << cells.length) - 1;
      let compatible = false;
      for (const pattern of patterns) {
        if ((pattern & painted) !== painted || (pattern & crossed) !== 0) continue;
        compatible = true;
        possible |= pattern;
        required &= pattern;
      }
      if (!compatible) return { kind: "contradiction", line };
      cells.forEach((cell, bit) => {
        if (working[cell] !== UNDECIDED) return;
        const mask = 1 << bit;
        const mark = required & mask ? INK : possible & mask ? UNDECIDED : EMPTY;
        if (mark === UNDECIDED) return;
        working[cell] = mark;
        first ??= { kind: "forced", cell, mark, line };
        changed = true;
      });
    }
  } while (changed);
  return first ?? { kind: "stalled" };
}
