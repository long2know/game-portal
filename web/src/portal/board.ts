import "./east-court-boards.css";
import type { Cell, MotionStep, Move, Position } from "./api";
import { sideName, token } from "./art";
import type { BoardId } from "./catalog";
import { catalog } from "./catalog";
import { button, el, svg, text } from "./dom";
import { BoardMotion } from "./motion";

interface Square {
  cell: Cell;
  button: HTMLButtonElement;
  shape: HTMLElement;
}

function file(game: BoardId, column: number): string {
  return (game === "go" ? "ABCDEFGHJKLMNOPQRSTUVWXYZ" :
    game === "chess" ? "abcdefghijklmnopqrstuvwxyz" : "ABCDEFGHIJKLMNOPQRSTUVWXYZ")[column];
}

function rank(game: BoardId, rows: number, row: number): number {
  return game === "chess" || game === "go" ? rows - row : row + 1;
}

function coordinates(game: BoardId, rows: number, { id, row, column }: Cell): string {
  if (game === "xiangqi" || game === "gomoku") return id;
  return `${file(game, column)}${rank(game, rows, row)}`;
}

function lattice(game: BoardId, { rows, columns, cells }: Position): SVGSVGElement {
  const art = svg("svg", { viewBox: `0 0 ${columns} ${rows}`, preserveAspectRatio: "none", class: "lattice", "aria-hidden": "true" });
  if (game === "xiangqi") {
    const right = columns - .5;
    const bottom = rows - .5;
    const upperBank = rows / 2 - .5;
    const lowerBank = rows / 2 + .5;
    let ranks = "", files = `M.5 .5V${bottom}M${right} .5V${bottom}`;
    for (let y = .5; y < rows; y++) ranks += `M.5 ${y}H${right}`;
    for (let x = 1.5; x < right; x++) files += `M${x} .5V${upperBank}M${x} ${lowerBank}V${bottom}`;
    art.append(svg("path", { class: "xiangqi-ranks", d: ranks }),
      svg("path", { class: "xiangqi-files", d: files }),
      svg("path", { class: "xiangqi-palaces", d: `M3.5 .5l2 2m0-2l-2 2M3.5 ${bottom - 2}l2 2m0-2l-2 2` }));
    let cuts = "";
    for (const [y, points] of [[2.5, [1.5, 7.5]], [3.5, [.5, 2.5, 4.5, 6.5, 8.5]],
      [6.5, [.5, 2.5, 4.5, 6.5, 8.5]], [7.5, [1.5, 7.5]]] as const) {
      for (const x of points) {
        for (const dx of [-1, 1]) {
          if (x + dx < .5 || x + dx > right) continue;
          for (const dy of [-1, 1]) cuts += `M${x + dx * .23} ${y + dy * .11}h${-dx * .12}v${dy * .12}`;
        }
      }
    }
    art.append(svg("path", { class: "xiangqi-cuts", d: cuts }));
    for (const [x, label] of [[2.5, "CHU RIVER"], [6.5, "HAN BANK"]] as const) {
      const notice = svg("text", { x, y: rows / 2, class: "xiangqi-river", "text-anchor": "middle", "dominant-baseline": "central" });
      notice.textContent = label;
      art.append(notice);
    }
  } else if (game === "go" || game === "gomoku") {
    let lines = "";
    for (let x = .5; x < columns; x++) lines += `M${x} .5V${rows - .5}`;
    for (let y = .5; y < rows; y++) lines += `M.5 ${y}H${columns - .5}`;
    art.append(svg("path", { d: lines }));
    const inset = game === "gomoku" ? 3 : rows <= 9 ? 2 : 3;
    const star = [inset, (rows - 1) / 2, rows - 1 - inset];
    for (const [ix, x] of star.entries()) {
      for (const [iy, y] of star.entries()) {
        if (rows < 19 && (ix === 1) !== (iy === 1)) continue;
        art.append(svg("circle", { cx: x + .5, cy: y + .5, r: rows < 13 ? .075 : .093 }));
      }
    }
  } else {
    const points = new Set(cells.map(({ row, column }) => `${row}/${column}`));
    let lines = "";
    for (const { row, column } of cells) {
      for (const [dy, dx] of [[0, 2], [1, -1], [1, 1]]) {
        if (points.has(`${row + dy}/${column + dx}`)) lines += `M${column + .5} ${row + .5}l${dx} ${dy}`;
      }
    }
    if (rows === 17 && columns === 25) {
      art.append(svg("polygon", {
        points: "12.5,.5 16.5,4.5 24.5,4.5 20.5,8.5 24.5,12.5 16.5,12.5 12.5,16.5 8.5,12.5 .5,12.5 4.5,8.5 .5,4.5 8.5,4.5",
      }), svg("polygon", {
        class: "lattice-hub",
        points: "8.5,4.5 16.5,4.5 20.5,8.5 16.5,12.5 8.5,12.5 4.5,8.5",
      }));
    }
    art.append(svg("path", { d: lines }));
  }
  return art;
}

export class BoardView {
  readonly root = el("section", "board-deck");
  private readonly grid = el("div", "board-grid");
  private readonly scroll = el("div", "board-scroll", this.grid);
  private readonly frame = el("div", "board-frame", this.scroll);
  private readonly hint = el("p", "board-hint");
  private readonly keys = el("p", "board-keys", "Keyboard: arrow keys to move, Enter or Space to choose.");
  private readonly actions = el("div", "board-actions");
  private readonly passNote = el("p", "board-keys", "Two consecutive passes end the game. Play out unsettled groups before passing.");
  private readonly chooser = el("section", "move-chooser");
  private readonly zoom = button("Enlarge board", "button small", () => this.toggleZoom());
  private readonly zoomNote = el("p", "board-keys", "Swipe or scroll to see the rest of the board.");
  private readonly cells = new Map<string, Square>();
  private readonly passButtons = new Map<string, HTMLButtonElement>();
  private readonly motion: BoardMotion;
  private position: Position | null = null;
  private focused: string | null = null;
  private selected: string | null = null;
  private choices: Move[] = [];
  private destinations = new Map<string, Move[]>();
  private moves: Move[] = [];
  private enabled = false;
  private mine = false;

  constructor(private readonly game: BoardId, private readonly send: (move: Move) => void) {
    this.motion = new BoardMotion(game, this.grid);
    this.root.dataset.game = game;
    this.root.setAttribute("aria-label", `${catalog[game].name} playing area`);
    this.grid.setAttribute("role", "group");
    this.grid.setAttribute("aria-label", `${catalog[game].name} board`);
    this.grid.setAttribute("aria-describedby", "board-keys");
    this.keys.id = "board-keys";
    this.hint.setAttribute("aria-live", "polite");
    this.hint.id = "board-hint";
    this.zoom.setAttribute("aria-pressed", "false");
    this.chooser.setAttribute("aria-label", "Choose a move");
    this.chooser.hidden = true;
    this.zoom.hidden = true;
    this.zoomNote.hidden = true;
    this.passNote.hidden = true;
    this.grid.addEventListener("keydown", event => this.navigate(event));
    this.root.append(this.frame, this.chooser, el("div", "board-instructions", this.hint, this.keys),
      el("div", "board-tools", this.actions, this.zoom), this.passNote, this.zoomNote);
    if (game === "xiangqi") this.keys.append(" Black at the top; Red at the bottom.");
  }

  update(position: Position, mine: boolean, enabled: boolean, reset: boolean): void {
    const rebuild = !this.position || this.position.rows !== position.rows || this.position.columns !== position.columns
      || this.cells.size !== position.cells.length || position.cells.some(cell => !this.cells.has(cell.id));
    this.position = position;
    this.mine = mine;
    this.enabled = enabled;
    this.moves = mine ? position.moves : [];
    if (rebuild) {
      this.stopMotion();
      this.build(position);
    }
    if (reset || !mine) this.clearSelection();

    for (const cell of position.cells) {
      const square = this.cells.get(cell.id)!;
      const { owner, piece } = cell;
      if (rebuild || square.cell.owner !== owner || square.cell.piece !== piece) {
        const occupant = owner !== null && piece ? `${sideName(this.game, owner)} ${piece}` : "empty";
        square.button.setAttribute("aria-label", `${coordinates(this.game, position.rows, cell)}, ${occupant}`);
        square.shape.replaceChildren(...(owner !== null && piece ? [token(this.game, owner, piece)] : []));
        square.button.toggleAttribute("data-occupied", owner !== null);
      }
      square.cell = cell;
      square.button.toggleAttribute("data-last", position.lastMove?.from === cell.id || position.lastMove?.to === cell.id);
      square.button.toggleAttribute("data-trail", position.lastMove?.via?.includes(cell.id) ?? false);
    }
    if (!this.focused) {
      const firstMove = mine ? this.moves.find(move => move.from || move.to) : undefined;
      this.setRoving(firstMove?.from ?? firstMove?.to ?? position.cells[0]?.id ?? null);
    }
    this.showTargets();
    this.showActions();
  }

  replay(before: Position, steps: MotionStep[]): void {
    if (this.position) void this.motion.replay(before, this.position, steps, id => this.cells.get(id)?.shape);
  }

  stopMotion(): void { this.motion.stop(); }

  private build(position: Position): void {
    this.cells.clear();
    this.focused = null;
    this.grid.replaceChildren();
    this.grid.style.setProperty("--cols", String(position.columns));
    this.grid.style.setProperty("--rows", String(position.rows));
    if (this.game === "go" || this.game === "chinese-checkers" || this.game === "xiangqi" || this.game === "gomoku") {
      this.grid.append(lattice(this.game, position));
    }
    for (const cell of position.cells) {
      const { id, column, row } = cell;
      const square = button("", "square", () => this.choose(id));
      const shape = el("span", "square-shape");
      square.append(shape);
      square.dataset.cell = id;
      square.dataset.row = String(row);
      square.dataset.column = String(column);
      square.dataset.tone = (row + column) % 2 ? "dark" : "light";
      square.style.gridArea = `${row + 1} / ${column + 1}`;
      square.tabIndex = -1;
      square.addEventListener("focus", () => this.setRoving(id));
      if (this.game === "chess" || this.game === "reversi") {
        if (column === 0) square.append(this.mark(String(rank(this.game, position.rows, row)), "rank"));
        if (row === position.rows - 1) square.append(this.mark(file(this.game, column), "file"));
      }
      this.cells.set(id, { cell, button: square, shape });
      this.grid.append(square);
    }
    this.zoom.hidden = position.columns < 13;
    if (this.zoom.hidden && this.frame.hasAttribute("data-enlarged")) this.toggleZoom();
  }

  private mark(value: string, kind: string): HTMLElement {
    const node = el("span", `coordinate ${kind}`, value);
    node.setAttribute("aria-hidden", "true");
    return node;
  }

  private toggleZoom(): void {
    this.stopMotion();
    const enlarged = this.frame.toggleAttribute("data-enlarged");
    this.zoom.textContent = enlarged ? "Fit board" : "Enlarge board";
    this.zoom.setAttribute("aria-pressed", String(enlarged));
    this.zoomNote.hidden = !enlarged;
    if (enlarged && this.focused) this.cells.get(this.focused)?.button.scrollIntoView({ block: "nearest", inline: "nearest" });
  }

  private setRoving(id: string | null): void {
    const current = this.focused && this.cells.get(this.focused);
    const next = id && this.cells.get(id);
    if (current) current.button.tabIndex = -1;
    if (next) next.button.tabIndex = 0;
    this.focused = id;
  }

  private navigate(event: KeyboardEvent): void {
    if (!(event.target instanceof HTMLButtonElement) || !event.target.dataset.cell) return;
    const current = this.cells.get(event.target.dataset.cell)?.cell;
    if (!current) return;
    if (event.key === "Escape" && this.selected) {
      event.preventDefault();
      this.clearSelection();
      this.showTargets();
      return;
    }
    if (!["ArrowLeft", "ArrowRight", "ArrowUp", "ArrowDown", "Home", "End"].includes(event.key)) return;
    event.preventDefault();
    const squares = [...this.cells.values()];
    let next: Square | undefined;
    if (event.key === "Home" || event.key === "End") {
      const row = event.ctrlKey ? squares : squares.filter(square => square.cell.row === current.row);
      next = event.key === "Home" ? row[0] : row.at(-1);
    } else if (event.key === "ArrowLeft" || event.key === "ArrowRight") {
      const direction = event.key === "ArrowLeft" ? -1 : 1;
      next = squares
        .filter(({ cell }) => cell.row === current.row && (cell.column - current.column) * direction > 0)
        .sort((a, b) => (a.cell.column - b.cell.column) * direction)[0];
    } else {
      const direction = event.key === "ArrowUp" ? -1 : 1;
      next = squares.filter(({ cell }) => (cell.row - current.row) * direction > 0).sort((a, b) =>
        (a.cell.row - b.cell.row) * direction
          || Math.abs(a.cell.column - current.column) - Math.abs(b.cell.column - current.column))[0];
    }
    next?.button.focus();
  }

  private choose(id: string): void {
    this.setRoving(id);
    if (!this.enabled) return;
    const options = this.destinations.get(id);
    if (options?.length === 1) {
      this.send(options[0]);
    } else if (options?.length) {
      this.openChoices(options);
    } else if (id === this.selected || this.moves.some(move => move.from === id)) {
      const alreadySelected = id === this.selected;
      this.clearSelection();
      this.selected = alreadySelected ? null : id;
      this.showTargets();
    }
  }

  private showTargets(): void {
    this.destinations.clear();
    const sources = new Set<string>();
    for (const move of this.moves) {
      if (move.from !== null) sources.add(move.from);
      if (move.from === this.selected && move.to !== null) {
        const group = this.destinations.get(move.to) ?? [];
        group.push(move);
        this.destinations.set(move.to, group);
      }
    }
    for (const [id, { button: square }] of this.cells) {
      const selected = this.selected === id;
      const target = this.destinations.has(id);
      const source = sources.has(id);
      square.toggleAttribute("data-selected", selected);
      square.toggleAttribute("data-target", target && this.enabled);
      square.toggleAttribute("data-source", source && this.enabled);
      square.setAttribute("aria-disabled", String(!this.enabled || !(target || source)));
      if (source) square.setAttribute("aria-pressed", String(selected));
      else square.removeAttribute("aria-pressed");
    }
    for (const choice of this.chooser.querySelectorAll("button[data-move]")) {
      (choice as HTMLButtonElement).disabled = !this.enabled;
    }
    const instructions = this.position?.status === "finished" ? "Review the final board." :
      !this.mine ? "Explore the board while you wait." :
      !this.moves.some(move => move.to !== null) && this.moves.length > 0 ? "No moves on the board. Use the action below." :
      this.selected ? "Choose a marked destination, or choose another piece." :
      this.game === "go" ? "Choose an empty intersection to place a stone." :
      this.game === "gomoku" ? "Place a stone on a marked intersection." :
      this.game === "reversi" ? "Place a disc on a marked square." : "Choose a piece to see where it can move.";
    text(this.hint, instructions);
  }

  private showActions(): void {
    const keep = new Set<string>();
    for (const move of this.moves) {
      if (move.to !== null) continue;
      keep.add(move.id);
      let action = this.passButtons.get(move.id);
      if (!action) {
        action = button(move.label, "button primary", () => this.send(move));
        this.passButtons.set(move.id, action);
        this.actions.append(action);
      }
      action.disabled = !this.enabled;
    }
    for (const [id, action] of this.passButtons) {
      if (keep.has(id)) continue;
      if (document.activeElement === action && this.focused) this.cells.get(this.focused)?.button.focus();
      action.remove();
      this.passButtons.delete(id);
    }
    this.passNote.hidden = this.game !== "go" || keep.size === 0;
  }

  private openChoices(moves: Move[]): void {
    this.choices = moves;
    const title = moves.some(move => move.promotion)
      ? "Choose a promotion" : "Choose a route";
    const controls = el("div", "choice-actions");
    const rows = this.position!.rows;
    moves.forEach((move, index) => {
      const hops = move.via?.map(id => {
        const cell = this.cells.get(id)?.cell;
        return cell ? coordinates(this.game, rows, cell) : id;
      });
      const label = move.promotion ? `Promote to ${move.promotion}` : hops?.length
        ? `Route ${index + 1}: via ${hops.join(" → ")}` : `Route ${index + 1}: ${move.label}`;
      const choice = button(label, "button secondary", () => this.send(move));
      choice.dataset.move = move.id;
      controls.append(choice);
    });
    const cancel = button("Back to board", "button", () => this.closeChoices(true));
    controls.append(cancel);
    this.chooser.replaceChildren(el("h3", "", title), controls);
    this.chooser.hidden = false;
    this.chooser.onkeydown = event => {
      if (event.key === "Escape") this.closeChoices(true);
    };
    controls.querySelector("button")?.focus();
  }

  private closeChoices(restoreFocus = false): void {
    if (this.chooser.hidden) return;
    if (restoreFocus || this.chooser.contains(document.activeElement)) {
      const target = this.choices[0]?.to ?? this.selected;
      if (target) this.cells.get(target)?.button.focus();
    }
    this.choices = [];
    this.chooser.hidden = true;
    this.chooser.replaceChildren();
  }

  private clearSelection(): void {
    this.closeChoices();
    this.selected = null;
  }
}
