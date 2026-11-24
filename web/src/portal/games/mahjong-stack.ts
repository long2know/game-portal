import type { Face } from "../../protocol";

type Slot = { x: number; y: number; level: number };
type Tile = Slot & { id: number; face: Face };
export type TilePair = readonly [number, number];
export type TileState = "free" | "covered" | "boxed" | "removed";

const tiers = [
  { y: 0, widths: [6, 8, 10, 10, 8, 6] },
  { y: 2, widths: [4, 6, 6, 4] },
  { y: 3, widths: [3, 2, 3] },
  { y: 5, widths: [2] },
];
const slots: readonly Slot[] = tiers.flatMap(({ y, widths }, level) =>
  widths.flatMap((width, row) => Array.from({ length: width }, (_, column) => ({
    x: 10 - width + column * 2, y: y + row * 2, level,
  }))));
const faces: Face[] = (["b", "c", "d"] as const).flatMap(suit =>
  Array.from({ length: 9 }, (_, rank) => `${suit}${rank + 1}` as Face));

// Each slot occupies two by two half-tile units. The painted relief is not part of its footprint.
const blocks = slots.map(slot => {
  const above: number[] = [], left: number[] = [], right: number[] = [];
  slots.forEach((other, id) => {
    if (Math.abs(slot.y - other.y) >= 2) return;
    const dx = other.x - slot.x;
    if (other.level > slot.level && Math.abs(dx) < 2) above.push(id);
    if (other.level === slot.level && dx === -2) left.push(id);
    if (other.level === slot.level && dx === 2) right.push(id);
  });
  return { above, left, right };
});

function stateAt(live: ReadonlySet<number>, id: number): TileState {
  if (!live.has(id)) return "removed";
  const { above, left, right } = blocks[id];
  const present = (other: number) => live.has(other);
  if (above.some(present)) return "covered";
  return left.some(present) && right.some(present) ? "boxed" : "free";
}

function seeded(seed: number) {
  return (size: number) => {
    seed = (seed + 0x6d2b79f5) >>> 0;
    let value = Math.imul(seed ^ (seed >>> 15), seed | 1);
    value ^= value + Math.imul(value ^ (value >>> 7), value | 61);
    return Math.floor(((value ^ (value >>> 14)) >>> 0) * size / 0x100000000);
  };
}

function shuffled<T>(items: readonly T[], draw: (size: number) => number): T[] {
  const result = [...items];
  for (let i = result.length - 1; i > 0; i--) {
    const j = draw(i + 1);
    [result[i], result[j]] = [result[j], result[i]];
  }
  return result;
}

export class MahjongStack {
  readonly tiles: readonly Tile[];
  readonly witness: readonly TilePair[];
  private readonly live = new Set(slots.keys());
  private readonly history: TilePair[] = [];

  constructor(readonly seed: number) {
    const draw = seeded(seed);
    const deck = shuffled([...faces, ...shuffled(faces, draw).slice(0, slots.length / 2 - faces.length)], draw);
    const uncoveredWalk = new Set(this.live);
    const assigned: Tile[] = [];
    const witness: TilePair[] = [];
    // The highest remaining tier is uncovered and always has even size. Its row ends
    // give at least two free tiles until it is empty, regardless of earlier choices.
    while (uncoveredWalk.size) {
      const top = Math.max(...[...uncoveredWalk].map(id => slots[id].level));
      const free = [...uncoveredWalk].filter(id => slots[id].level === top && stateAt(uncoveredWalk, id) === "free");
      const a = free.splice(draw(free.length), 1)[0];
      const b = free[draw(free.length)];
      const face = deck[witness.length];
      witness.push([a, b]);
      for (const id of [a, b]) {
        assigned[id] = { ...slots[id], id, face };
        uncoveredWalk.delete(id);
      }
    }
    this.tiles = assigned;
    this.witness = witness;
  }

  get remaining(): number { return this.live.size; }
  get canUndo(): boolean { return this.history.length > 0; }

  state(id: number): TileState { return stateAt(this.live, id); }

  private matches(a: number, b: number): boolean {
    return a !== b && this.state(a) === "free" && this.state(b) === "free" && this.tiles[a].face === this.tiles[b].face;
  }

  match(a: number, b: number): boolean {
    if (!this.matches(a, b)) return false;
    this.live.delete(a);
    this.live.delete(b);
    this.history.push([a, b]);
    return true;
  }

  undo(): TilePair | undefined {
    const pair = this.history.pop();
    pair?.forEach(id => this.live.add(id));
    return pair;
  }

  hint(selected: number | null): TilePair | undefined {
    if (selected !== null && this.state(selected) === "free") {
      for (const id of this.live) if (this.matches(selected, id)) return [selected, id];
    }
    const proven = this.witness.find(([a, b]) => this.matches(a, b));
    if (proven) return proven;
    const first = new Map<Face, number>();
    for (const id of this.live) {
      if (this.state(id) !== "free") continue;
      const { face } = this.tiles[id];
      const prior = first.get(face);
      if (prior !== undefined) return [prior, id];
      first.set(face, id);
    }
    return undefined;
  }

  save(): string { return JSON.stringify({ version: 1, seed: this.seed, moves: this.history }); }

  static restore(raw: string | null): MahjongStack | null {
    if (!raw || raw.length > 2048) return null;
    try {
      const { version, seed, moves } = JSON.parse(raw) as { version: unknown; seed: unknown; moves: unknown };
      if (version !== 1 || typeof seed !== "number" || !Number.isInteger(seed) || seed < 0 || seed > 0xffffffff ||
          !Array.isArray(moves) || moves.length > slots.length / 2) return null;
      const stack = new MahjongStack(seed);
      for (const pair of moves) {
        if (!Array.isArray(pair) || pair.length !== 2 || !stack.match(pair[0], pair[1])) return null;
      }
      return stack;
    } catch { return null; }
  }
}
