import type { Cell, MotionStep, Position, Table, TurnMotion } from "./api";
import { token } from "./art";
import type { BoardId } from "./catalog";
import { el } from "./dom";

export const reduceMotion = "(prefers-reduced-motion: reduce)";

const samePiece = (before: Cell | undefined, after: Cell) =>
  before?.owner === after.owner && before?.piece === after.piece;

export function turnSteps(before: Table, after: Table, motion?: TurnMotion): MotionStep[] {
  if (before.id !== after.id || before.revision + 1 !== after.revision ||
      before.status !== "playing" || after.status === "closed" || after.result?.cause === "resignation" ||
      !before.position || !after.position) return [];
  if (motion !== undefined) {
    return motion?.fromRevision === before.revision && Array.isArray(motion.steps) && motion.steps.length <= 6
      ? motion.steps : [];
  }
  const move = after.position.lastMove;
  if (!move || after.seats.some(seat => seat.bot)) return [];
  const cells = new Map(before.position.cells.map(cell => [cell.id, cell]));
  return [{ move, changes: after.position.cells.filter(cell => !samePiece(cells.get(cell.id), cell)) }];
}

type Part = { node: HTMLElement; box: DOMRect };
const pose = (x = 0, y = 0, scale = 1) => `translate3d(${x}px,${y}px,0) scale(${scale})`;

export class BoardMotion {
  private cleanup?: () => void;

  constructor(private readonly game: BoardId, private readonly grid: HTMLElement) {}

  stop(): void {
    this.cleanup?.();
    this.cleanup = undefined;
  }

  async replay(before: Position, after: Position, steps: MotionStep[],
    shape: (id: string) => HTMLElement | undefined): Promise<void> {
    this.stop();
    if (!steps.length || document.hidden || !this.grid.clientWidth || !this.grid.animate ||
        before.rows !== after.rows || before.columns !== after.columns ||
        before.cells.length !== after.cells.length) return;

    const cells = new Map(before.cells.map(cell => [cell.id, cell]));
    const final = new Map(cells);
    const covered = new Set<string>();
    const points = new Set<string>();
    // Validate the sparse visual sequence against the committed board before covering any pieces.
    for (const { move, changes } of steps) {
      if (move.from !== null) covered.add(move.from);
      if (move.to !== null) covered.add(move.to);
      move.via?.forEach(id => points.add(id));
      for (const cell of changes) {
        const prior = cells.get(cell.id);
        if (!prior || prior.row !== cell.row || prior.column !== cell.column) return;
        covered.add(cell.id);
        final.set(cell.id, cell);
      }
    }
    if (!covered.size || after.cells.some(cell => !samePiece(final.get(cell.id), cell))) return;
    const origin = this.grid.getBoundingClientRect();
    const spaces = new Map<string, Part>();
    for (const id of new Set([...covered, ...points])) {
      const node = shape(id);
      if (!node) return;
      spaces.set(id, { node, box: node.getBoundingClientRect() });
    }

    const layer = el("div", "motion-layer");
    layer.setAttribute("aria-hidden", "true");
    const parts = new Map<string, HTMLElement>();
    const animations: Animation[] = [];
    const size = new ResizeObserver(() => {
      const bounds = this.grid.getBoundingClientRect();
      if (bounds.width !== origin.width || bounds.height !== origin.height) this.stop();
    });
    const cleanup = this.cleanup = () => {
      size.disconnect();
      animations.splice(0).forEach(animation => animation.cancel());
      for (const id of covered) spaces.get(id)!.node.removeAttribute("data-motion-covered");
      layer.remove();
    };
    const pieceAt = (cell: Cell): HTMLElement | undefined => {
      if (cell.owner === null || !cell.piece) return;
      const { box } = spaces.get(cell.id)!;
      const part = el("div", "motion-piece", token(this.game, cell.owner, cell.piece));
      Object.assign(part.style, {
        left: `${box.left - origin.left}px`, top: `${box.top - origin.top}px`,
        width: `${box.width}px`, height: `${box.height}px`,
      });
      layer.append(part);
      return part;
    };
    const paint = () => {
      layer.replaceChildren();
      parts.clear();
      for (const id of covered) {
        const part = pieceAt(cells.get(id)!);
        if (part) parts.set(id, part);
      }
    };
    const animate = (part: HTMLElement, effect: string, frames: Keyframe[], duration: number, delay = 0) => {
      part.classList.add(`motion-${effect}`);
      const animation = part.animate(frames, { duration, delay, fill: "both" });
      void animation.finished.catch(() => {});
      animations.push(animation);
    };
    const travel = (part: HTMLElement, route: string[]): number => {
      const start = spaces.get(route[0])!.box;
      const legs = route.length - 1;
      const frames: Keyframe[] = [{ transform: pose(), offset: 0, easing: "ease-out" }];
      let stepTime = this.game === "chess" ? 280 : 196;
      for (let i = 1; i <= legs; i++) {
        const a = spaces.get(route[i - 1])!.box;
        const b = spaces.get(route[i])!.box;
        const dx = (b.left + b.width / 2) - (start.left + start.width / 2);
        const dy = (b.top + b.height / 2) - (start.top + start.height / 2);
        const hop = Math.abs(b.top - a.top) > a.height * 1.2 || Math.abs(b.left - a.left) > a.width * 2;
        const lift = this.game === "chess" ? part.querySelector('[data-piece="knight"]') ? .24 : .105 : hop ? .32 : .10;
        if (hop && this.game !== "chess") stepTime = 238;
        frames.push({
          transform: pose((dx + a.left - start.left) / 2, (dy + a.top - start.top) / 2 - start.height * lift,
            hop && this.game !== "chess" ? 1.11 : 1.062),
          offset: (i - .5) / legs, easing: "ease-in",
        }, { transform: pose(dx, dy), offset: i / legs, easing: "ease-out" });
      }
      const duration = Math.min(640, stepTime * legs);
      animate(part, "travel", frames, duration);
      return duration;
    };

    try {
      this.grid.append(layer);
      size.observe(this.grid);
      for (const id of covered) spaces.get(id)!.node.setAttribute("data-motion-covered", "");
      for (const { move, changes } of steps) {
        if (document.hidden || matchMedia(reduceMotion).matches ||
            document.documentElement.dataset.reducedMotion === "true") break;
        paint();
        const { from, to } = move;
        const route = from !== null && to !== null ? [from, ...move.via ?? [], to] : [];
        const mover = from !== null ? cells.get(from) : undefined;
        const part = from !== null ? parts.get(from) : undefined;
        const duration = part && route.length ? travel(part, route) : 0;
        for (const cell of changes) {
          const prior = cells.get(cell.id)!;
          if (samePiece(prior, cell)) continue;
          const old = parts.get(cell.id);
          if (cell.id === to && from === null) {
            const placed = pieceAt(cell);
            if (placed) animate(placed, "place", [
              { transform: pose(0, -spaces.get(cell.id)!.box.height * .24, .81), opacity: .2, easing: "ease-in" },
              { transform: pose(0, 0, 1.06), opacity: 1, offset: .62, easing: "ease-out" },
              { transform: pose(), opacity: 1 },
            ], 232);
          } else if (this.game === "reversi" && old && cell.owner !== null) {
            const face = pieceAt(cell)!;
            const at = cells.get(to!)!;
            const distance = Math.max(Math.abs(at.row - cell.row), Math.abs(at.column - cell.column));
            const delay = 108 + Math.min(Math.max(0, distance - 1), 5) * 32;
            const edge = "perspective(220px) rotateX(90deg)";
            animate(old, "flip", [{ transform: "rotateX(0)" }, { transform: edge, opacity: 0 }], 104, delay);
            animate(face, "flip", [{ transform: edge, opacity: 0 }, { transform: "rotateX(0)", opacity: 1 }], 116, delay + 104);
          } else if (old && prior.owner !== mover?.owner && (cell.owner === null || cell.id === to)) {
            let delay = duration ? duration * .59 : 136;
            if (this.game === "checkers") {
              const hop = route.findIndex((id, i) => {
                if (!i) return false;
                const a = cells.get(route[i - 1])!, b = cells.get(id)!;
                return a.row + b.row === 2 * cell.row && a.column + b.column === 2 * cell.column;
              });
              if (hop > 0) delay = (hop - .45) * duration / (route.length - 1);
            }
            animate(old, "capture", [{ transform: pose(), opacity: 1, easing: "ease-in" },
              { transform: pose(0, 3, .64), opacity: 0 }], 138, delay);
          } else if (this.game === "chess" && old && cell.id !== from && cell.owner === null && prior.owner === mover?.owner) {
            const rook = changes.find(next => next.id !== to && next.piece === "rook" && next.owner === prior.owner);
            if (rook) travel(old, [cell.id, rook.id]);
          }
        }
        changes.forEach(cell => cells.set(cell.id, cell));
        if (!animations.length) continue;
        await Promise.all(animations.map(animation => animation.finished));
        if (this.cleanup !== cleanup) return;
        animations.splice(0).forEach(animation => animation.cancel());
      }
    } catch (error) {
      if (this.cleanup === cleanup) console.warn("Board turn animation unavailable.", error);
    }
    finally {
      if (this.cleanup === cleanup) this.stop();
    }
  }
}
