import "./sudoku.css";
import { button, el, text } from "../dom";
import type { PortalHost } from "../host";
import { makeGrid } from "./shared";
import { solo } from "./solo";
import type { SoloView } from "./solo";

type Position = { finished: boolean; moves: number; given: number[]; values: number[]; conflicts: number[] };
type Action = { kind: "set"; index: number; value: number } | { kind: "hint" };

function view(play: (action: Action) => void): SoloView<Position> {
  let position: Position | undefined;
  let selected = -1;
  let enabled = false;
  const cellName = (index: number) => `Row ${Math.floor(index / 9) + 1}, column ${index % 9 + 1}`;
  const board = makeGrid(9, 9, index => {
    const control = button("", "sudoku-cell", () => select(index));
    control.dataset.index = String(index);
    control.addEventListener("focus", () => select(index));
    return control;
  }, "Sudoku board");
  board.root.classList.add("sudoku-grid");
  const instruction = el("p", "feature-hint", "Choose a square and a number. Use arrow keys and 1–9 on a keyboard; Delete clears a square.");
  instruction.id = "sudoku-keys";
  board.root.setAttribute("aria-describedby", instruction.id);
  const conflicts = el("p", "sudoku-conflicts");
  conflicts.setAttribute("role", "status");
  const chosen = el("p", "feature-hint");
  const numbers = Array.from({ length: 9 }, (_, i) => button(String(i + 1), "button", () => set(i + 1)));
  const keypad = el("div", "sudoku-keypad", ...numbers);
  keypad.setAttribute("role", "group");
  keypad.setAttribute("aria-label", "Enter a number");
  const clear = button("Clear square", "button secondary", () => set(0));
  const hint = button("Get a hint", "button secondary", () => { if (enabled) play({ kind: "hint" }); });
  const root = el("section", "feature-panel sudoku-panel", board.root,
    el("div", "feature-stack", instruction, conflicts, chosen, keypad, el("div", "feature-controls", clear, hint)));

  function editable(): boolean { return !!position && selected >= 0 && !position.given[selected]; }

  function select(index: number): void {
    selected = index;
    updateSelection();
  }

  function updateSelection(): void {
    const row = Math.floor(selected / 9), column = selected % 9;
    for (const [index, cell] of board.cells.entries()) {
      cell.setAttribute("aria-pressed", String(selected === index));
      cell.toggleAttribute("data-related", selected >= 0 && selected !== index &&
        (Math.floor(index / 9) === row || index % 9 === column));
    }
    for (const control of numbers) control.setAttribute("aria-disabled", String(!enabled || !editable()));
    clear.setAttribute("aria-disabled", String(!enabled || !editable() || !position?.values[selected]));
    hint.setAttribute("aria-disabled", String(!enabled));
    text(chosen, selected < 0 ? "No square selected." :
      `${cellName(selected)} selected${editable() ? "." : " · given number."}`);
  }

  function set(value: number): void {
    if (enabled && editable() && position?.values[selected] !== value) play({ kind: "set", index: selected, value });
  }

  board.root.addEventListener("keydown", event => {
    if (event.altKey || event.ctrlKey || event.metaKey) return;
    if (/^[1-9]$/.test(event.key) || ["Backspace", "Delete", "0"].includes(event.key)) {
      event.preventDefault();
      set(Number(event.key) || 0);
    }
  });
  return {
    root,
    show(next, permitted, newGame) {
      position = next;
      enabled = permitted;
      if (newGame || selected < 0) {
        selected = next.given.findIndex(value => !value);
        for (const [index, cell] of board.cells.entries()) cell.tabIndex = index === selected ? 0 : -1;
      }
      const duplicates = new Set(next.conflicts);
      for (const [index, cell] of board.cells.entries()) {
        const given = !!next.given[index];
        const value = next.values[index];
        const conflict = duplicates.has(index);
        cell.toggleAttribute("data-given", given);
        cell.toggleAttribute("data-conflict", conflict);
        cell.setAttribute("aria-label", `${cellName(index)}, ${value ? `${value}${given ? ", given" : ""}` : "empty"}${conflict ? ", conflict" : ""}`);
        text(cell, value ? String(value) : "");
      }
      text(conflicts, duplicates.size > 0 ? "Some numbers repeat in a row, column, or box. Conflicts are marked." : "");
      conflicts.hidden = !duplicates.size;
      updateSelection();
    },
  };
}

export function mount(host: PortalHost): void {
  solo(host, { game: "sudoku", variants: { easy: "Easy", standard: "Standard", hard: "Hard" }, defaultVariant: "standard", view });
}
