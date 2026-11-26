import "./nonograms.css";
import { Confirmation } from "../confirm";
import { button, el, svg, text } from "../dom";
import type { PortalHost } from "../host";
import { EMPTY, findHint, INK, matchesClues, UNDECIDED, type Hint, type Mark } from "./nonogram-logic";
import { inkPlates, type InkPlate } from "./nonogram-puzzles";
import { applyMark, blankBoard, readProgress, undoMark, writeProgress } from "./nonogram-save";

const markName = ["undecided", "ink", "empty"] as const;
const markGlyph = ["·", "", "×"] as const;
const readClue = (clue: readonly number[]) => clue.join(", ") || "0";

interface Spot {
  row: number;
  column: number;
  control: HTMLButtonElement;
}

function printFrom(plate: InkPlate, marks: readonly Mark[]): SVGSVGElement {
  const width = plate.columns.length;
  const height = plate.rows.length;
  const drawing = svg("svg", {
    class: "ink-impression", viewBox: `-1.5 -1.5 ${width + 3} ${height + 3}`,
    role: "img", "aria-label": `Earned print: ${plate.title}`,
  });
  const silhouette = marks.map((mark, cell) =>
    mark === INK ? `M${cell % width} ${Math.floor(cell / width)}h1v1h-1z` : "").join("");
  drawing.append(
    svg("path", { d: silhouette, fill: plate.pigment, transform: "translate(.17 .15)", opacity: ".27" }),
    svg("path", { d: silhouette, fill: plate.pigment, class: "ink-silhouette" }),
    svg("path", {
      d: `M-1 0v-1h1 M${width} -1h1v1 M-1 ${height}v1h1 M${width} ${height + 1}h1v-1`,
      fill: "none", stroke: "currentColor", "stroke-width": ".11",
    }),
  );
  return drawing;
}

export function mount({ main, mount }: Pick<PortalHost, "main" | "mount">): void {
  const key = `${mount}court-ink-grids-v1`;
  let raw: string | null = null;
  let readable = true;
  try { raw = localStorage.getItem(key); } catch { readable = false; }
  const progress = readProgress(raw);
  let plate = inkPlates.find(entry => entry.id === progress.active) ?? inkPlates[0];
  let state = blankBoard(0);
  let width = 0, height = 0;
  let tool: Mark = INK;
  let solved = false;
  let enlarged = false;
  let focused: Spot | undefined;
  let suggested: Extract<Hint, { kind: "forced" }> | undefined;
  let cells: Spot[] = [];
  let rowClues: Spot[] = [];
  let columnClues: Spot[] = [];

  const heading = el("h1", "", "Ink Grids");
  heading.id = "ink-title";
  const emblem = el("span", "ink-emblem");
  emblem.setAttribute("aria-hidden", "true");
  const header = el("header", "ink-mast", emblem,
    el("div", "", el("p", "ink-kicker", "The print room"), heading),
    el("p", "ink-intro", "Numbers are ink runs. Leave a gap."));
  const picker = el("select", "ink-picker");
  picker.id = "ink-plate";
  for (const [size, label] of [[5, "First impressions"], [8, "Workroom plates"], [10, "Full proofs"]] as const) {
    const group = el("optgroup");
    group.label = `${label} · ${size} × ${size}`;
    for (const entry of inkPlates.filter(entry => entry.rows.length === size)) {
      const option = el("option", "", plateLabel(entry));
      option.value = entry.id;
      group.append(option);
    }
    picker.append(group);
  }
  const pickerLabel = el("label", "ink-kicker", "Puzzle plate");
  pickerLabel.htmlFor = picker.id;
  picker.addEventListener("change", () => {
    const next = inkPlates.find(entry => entry.id === picker.value);
    if (!next || next === plate) return;
    plate = next;
    progress.active = plate.id;
    loadPlate();
    persist();
  });

  const tools = el("div", "ink-tools");
  tools.setAttribute("role", "group");
  tools.setAttribute("aria-label", "Marking tool");
  const toolButtons = ([[INK, "Ink", "■", "I"], [EMPTY, "Empty", "×", "X"], [UNDECIDED, "Erase", "·", "E"]] as const)
    .map(([mark, label, symbol, shortcut]) => {
      const control = button("", "ink-tool", () => selectTool(mark));
      const glyph = el("span", "ink-tool-glyph", symbol);
      glyph.setAttribute("aria-hidden", "true");
      control.append(glyph, label);
      if (mark === EMPTY) control.setAttribute("aria-label", "X Empty");
      control.setAttribute("aria-keyshortcuts", shortcut);
      return { control, mark };
    });
  tools.append(...toolButtons.map(({ control }) => control));
  const controls = el("div", "ink-controls", el("div", "ink-picker-field", pickerLabel, picker), tools);
  const plateNumber = el("span", "ink-kicker");
  const dimensions = el("span", "ink-dimensions");
  const zoom = button("Enlarge grid", "ink-zoom", () => {
    enlarged = !enlarged;
    scroll.toggleAttribute("data-enlarged", enlarged);
    text(zoom, enlarged ? "Fit grid" : "Enlarge grid");
    pan.hidden = !enlarged;
    if (focused) revealSpot(focused);
  });
  const reading = el("p", "ink-reading");
  const pan = el("p", "ink-pan", "Swipe or scroll inside the paper to reach the rest of the grid.");
  pan.hidden = true;
  const grid = el("div", "ink-board");
  grid.setAttribute("role", "grid");
  grid.setAttribute("aria-describedby", "ink-grid-keys");
  const scroll = el("div", "ink-scroll", grid);
  scroll.id = "ink-scroll";
  zoom.setAttribute("aria-controls", scroll.id);
  const paper = el("section", "ink-paper",
    el("header", "ink-plate-header", el("div", "ink-plate-index", plateNumber, dimensions), zoom), scroll, reading, pan);
  paper.setAttribute("aria-label", "Puzzle sheet");

  const confirmation = new Confirmation();
  const hint = button("Hint", "button secondary", suggest);
  const undo = button("Undo", "button secondary", () => {
    const cell = undoMark(state);
    if (cell === undefined) return;
    suggested = undefined;
    render("Last mark undone.");
    persist();
    moveTo(cells[cell], true);
  });
  const reset = button("Reset puzzle", "button secondary", () => confirmation.show("Reset puzzle",
    "Clear this plate and its undo history? Other plates will keep their marks.", reset, () => {
      state = blankBoard(width * height);
      suggested = undefined;
      render("This plate is blank again.");
      persist();
      moveTo(cells[0], true);
    }));
  const feedback = el("p", "ink-feedback");
  feedback.setAttribute("role", "status");
  const reward = el("figure", "ink-print");
  reward.hidden = true;
  const keys = el("p", "ink-grid-keys",
    "Arrows move between squares and clues. Enter or Space uses the selected tool. I inks, X crosses out, and E erases.");
  keys.id = "ink-grid-keys";
  const guide = el("details", "ink-guide", el("summary", "", "How to read a grid"),
    el("p", "", "Numbers give the lengths of ink runs in order. Leave at least one unpainted square between runs. A 0 is an empty line."),
    el("p", "", "An X marks a known empty square. Erase returns a square to its undecided dot. You can finish without crossing out every blank."),
    keys);
  const storage = el("p", "ink-storage", "Browser storage is unavailable. Marks last until you leave or reload.");
  storage.hidden = readable;
  const notes = el("section", "ink-notes",
    el("div", "ink-actions", hint, undo, reset), feedback, reward, guide, storage);
  notes.setAttribute("aria-label", "Puzzle actions");
  const root = el("section", "ink-grids", header, el("div", "ink-layout", controls, paper, notes), confirmation.root);
  root.setAttribute("aria-labelledby", heading.id);
  grid.addEventListener("keydown", navigate);
  main.append(root);
  selectTool(INK);
  loadPlate();

  function plateLabel(entry: InkPlate): string {
    return `Plate ${entry.id.slice(-2)}`;
  }

  function persist(): void {
    progress.boards.set(plate.id, state);
    try {
      localStorage.setItem(key, writeProgress(progress));
      storage.hidden = true;
    } catch { storage.hidden = false; }
  }

  function selectTool(next: Mark): void {
    tool = next;
    for (const { control, mark } of toolButtons) control.setAttribute("aria-pressed", String(mark === next));
  }

  function spot(row: number, column: number, classes: string, activate?: () => void): Spot {
    const control = button("", classes, () => {
      moveTo(target);
      activate?.();
    });
    const target = { row, column, control };
    control.tabIndex = -1;
    control.addEventListener("focus", () => selectSpot(target));
    return target;
  }

  function clueSpot(row: number, column: number, clue: readonly number[]): Spot {
    const columnHeader = row < 0;
    const target = spot(row, column, "ink-clue");
    const label = columnHeader ? `Column ${column + 1}` : `Row ${row + 1}`;
    target.control.setAttribute("aria-label", `${label} clues: ${readClue(clue)}`);
    for (const value of clue.length ? clue : [0]) target.control.append(el("span", "", String(value)));
    return target;
  }

  function loadPlate(): void {
    width = plate.columns.length;
    height = plate.rows.length;
    state = progress.boards.get(plate.id) ?? blankBoard(width * height);
    suggested = undefined;
    solved = false;
    focused = undefined;
    root.dataset.plate = plate.id;
    picker.value = plate.id;
    text(plateNumber, plateLabel(plate));
    text(dimensions, `${width} × ${height}`);
    grid.setAttribute("aria-label", `${plateLabel(plate)} · ${width} by ${height} nonogram`);
    grid.style.setProperty("--ink-size", String(width));
    grid.style.setProperty("--ink-depth", String(Math.max(...plate.columns.map(clue => clue.length))));
    grid.style.setProperty("--ink-rail", `${Math.max(44, 13 * Math.max(...plate.rows.map(clue => clue.length)) + 7)}px`);

    const corner = el("div", "ink-corner", el("span", "", "◆"));
    corner.setAttribute("role", "presentation");
    corner.setAttribute("aria-hidden", "true");
    columnClues = plate.columns.map((clue, column) => clueSpot(-1, column, clue));
    const top = el("div", "ink-row ink-columns", corner, ...columnClues.map(({ control }) => {
      const header = el("div", "", control);
      header.setAttribute("role", "columnheader");
      return header;
    }));
    top.setAttribute("role", "row");
    cells = [];
    rowClues = [];
    const rows = plate.rows.map((clue, row) => {
      const header = clueSpot(row, -1, clue);
      rowClues.push(header);
      const rowHeader = el("div", "ink-row-clue", header.control);
      rowHeader.setAttribute("role", "rowheader");
      const rowNode = el("div", "ink-row", rowHeader);
      rowNode.setAttribute("role", "row");
      for (let column = 0; column < width; column++) {
        const cell = row * width + column;
        const target = spot(row, column, "ink-cell", () => edit(cell, tool));
        const glyph = el("span", "ink-mark");
        glyph.setAttribute("aria-hidden", "true");
        target.control.append(glyph);
        target.control.dataset.cell = `${row + 1}-${column + 1}`;
        cells.push(target);
        const node = el("div", "ink-gridcell", target.control);
        node.setAttribute("role", "gridcell");
        rowNode.append(node);
      }
      return rowNode;
    });
    grid.replaceChildren(top, ...rows);
    scroll.scrollTo(0, 0);
    selectSpot(cells[0]);
    render(progress.boards.has(plate.id) && state.marks.some(mark => mark !== UNDECIDED) ? "Your saved plate is ready." : "");
  }

  function selectSpot(target: Spot): void {
    if (focused === target) return;
    if (focused) focused.control.tabIndex = -1;
    target.control.tabIndex = 0;
    focused = target;
    const { row, column } = target;
    rowClues.forEach((spot, i) => spot.control.setAttribute("aria-pressed", String(i === row)));
    columnClues.forEach((spot, i) => spot.control.setAttribute("aria-pressed", String(i === column)));
    cells.forEach(cell => cell.control.toggleAttribute("data-trace", cell.row === row || cell.column === column));
    const lines = [
      ...(row < 0 ? [] : [`Row ${row + 1}: ${readClue(plate.rows[row])}`]),
      ...(column < 0 ? [] : [`Column ${column + 1}: ${readClue(plate.columns[column])}`]),
    ];
    text(reading, lines.join("  /  "));
  }

  function revealSpot(target: Spot): void {
    const view = scroll.getBoundingClientRect();
    const square = target.control.getBoundingClientRect();
    if (target.column >= 0) {
      const rail = rowClues[0].control.offsetWidth;
      if (square.left < view.left + rail) scroll.scrollLeft += square.left - view.left - rail;
      else if (square.right > view.left + scroll.clientWidth) scroll.scrollLeft += square.right - view.left - scroll.clientWidth;
    }
    if (target.row >= 0) {
      const rail = columnClues[0].control.offsetHeight;
      if (square.top < view.top + rail) scroll.scrollTop += square.top - view.top - rail;
      else if (square.bottom > view.top + scroll.clientHeight) scroll.scrollTop += square.bottom - view.top - scroll.clientHeight;
    }
  }

  function moveTo(target: Spot, reveal = false): void {
    selectSpot(target);
    target.control.focus({ preventScroll: true });
    if (reveal) {
      scroll.scrollIntoView({ block: "nearest", inline: "nearest" });
      revealSpot(target);
    }
  }

  function render(notice: string): void {
    const won = matchesClues(plate, state.marks);
    root.dataset.state = won ? "solved" : "playing";
    for (const [cell, { row, column, control }] of cells.entries()) {
      const mark = state.marks[cell];
      const indicated = suggested?.cell === cell ? markName[suggested.mark] : undefined;
      control.dataset.mark = markName[mark];
      if (indicated) control.dataset.hint = indicated;
      else delete control.dataset.hint;
      text(control.firstChild!, markGlyph[mark]);
      control.setAttribute("aria-label", `Row ${row + 1}, column ${column + 1}: ${markName[mark]}` +
        (indicated ? `; hint: ${indicated}` : "") +
        `. Row clues: ${readClue(plate.rows[row])}; column clues: ${readClue(plate.columns[column])}.`);
      control.setAttribute("aria-disabled", String(won));
    }
    if (won && (!solved || reward.hidden)) {
      reward.replaceChildren(printFrom(plate, state.marks), el("figcaption", "",
        el("p", "ink-kicker", "Print pulled"), el("h2", "", plate.title), el("p", "", plate.caption)));
    } else if (!won) reward.replaceChildren();
    solved = won;
    reward.hidden = !won;
    undo.disabled = state.undo.length === 0;
    hint.disabled = won;
    reset.disabled = undo.disabled && state.marks.every(mark => mark === UNDECIDED);
    text(feedback, won ? "All runs match. Your print is ready." : notice || "Match each ink run to pull a print.");
  }

  function edit(cell: number, mark: Mark): void {
    if (solved || !applyMark(state, cell, mark)) return;
    suggested = undefined;
    render("");
    persist();
  }

  function suggest(): void {
    const result = findHint(plate, state.marks);
    suggested = result.kind === "forced" ? result : undefined;
    if (result.kind === "contradiction") {
      render(`No placement remains for ${result.line.axis} ${result.line.index + 1}. Undo or revise a mark.`);
    } else if (result.kind === "stalled") {
      render("No square is forced by the current line deductions.");
    } else {
      const target = cells[result.cell];
      render(`The clues force ${result.mark === INK ? "ink" : "an empty square"} at row ${target.row + 1}, column ${target.column + 1}.`);
      moveTo(target, true);
    }
  }

  function navigate(event: KeyboardEvent): void {
    if (!focused || event.target !== focused.control || event.altKey || event.ctrlKey || event.metaKey) return;
    let { row, column } = focused;
    if (event.key === "ArrowDown") row = Math.min(row + 1, height - 1);
    else if (event.key === "ArrowUp") row = Math.max(row - 1, -1);
    else if (event.key === "ArrowRight") column = Math.min(column + 1, width - 1);
    else if (event.key === "ArrowLeft") column = Math.max(column - 1, -1);
    else if (event.key === "Home") column = 0;
    else if (event.key === "End") column = width - 1;
    else {
      if (row < 0 || column < 0 || solved) return;
      const shortcut = event.key.toLowerCase();
      if (shortcut !== "i" && shortcut !== "x" && shortcut !== "e") return;
      event.preventDefault();
      selectTool(shortcut === "i" ? INK : shortcut === "x" ? EMPTY : UNDECIDED);
      edit(row * width + column, tool);
      return;
    }
    event.preventDefault();
    if (row < 0 && column < 0) return;
    moveTo(row < 0 ? columnClues[column] : column < 0 ? rowClues[row] : cells[row * width + column], true);
  }
}
