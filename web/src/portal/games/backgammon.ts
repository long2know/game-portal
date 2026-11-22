import "./backgammon.css";
import { button, el, text } from "../dom";
import type { PortalHost } from "../host";
import { Multiplayer, nameAt } from "./multiplayer";
import type { MatchView, MultiplayerTable } from "./multiplayer";

type Move = { from: number; to: number; die: number };
type Action = { kind: "roll" | "pass" } | ({ kind: "move" } & Move);
type Position = {
  points: number[];
  bar: number[];
  off: number[];
  turn: number | null;
  winner: number | null;
  dice: number[];
  remaining: number[];
  openingRoll: number[];
  lastTurn: { seat: number; dice: number[]; unused: number[] } | null;
  legalActions: { canRoll: boolean; canPass: boolean; moves: Move[] } | null;
};
type Cell = { control: HTMLButtonElement; stack: HTMLElement; location: number; seat?: number };

const bar = 0;
const off = 25;
const colors = ["Ivory", "Umber"];
const top = [13, 14, 15, 16, 17, 18, bar, 19, 20, 21, 22, 23, 24, off];
const bottom = [12, 11, 10, 9, 8, 7, bar, 6, 5, 4, 3, 2, 1, off];
const pipLayouts = [[], [5], [1, 9], [1, 5, 9], [1, 3, 7, 9], [1, 3, 5, 7, 9], [1, 3, 4, 6, 7, 9]];

function checker(seat: number, count = 0): HTMLElement {
  const disk = el("span", `bg-checker bg-color-${seat}`, count ? String(count) : "");
  disk.setAttribute("aria-hidden", "true");
  return disk;
}

function dieFace(value: number): HTMLElement {
  const face = el("span", "bg-die");
  face.setAttribute("aria-hidden", "true");
  for (const place of pipLayouts[value]) {
    const pip = el("span", "bg-pip");
    pip.style.gridArea = `${Math.ceil(place / 3)} / ${(place - 1) % 3 + 1}`;
    face.append(pip);
  }
  return face;
}

function view(play: (action: Action) => void): MatchView<Position> {
  const status = el("p", "bg-status");
  status.setAttribute("role", "status");
  status.tabIndex = -1;
  const dice = el("div", "bg-dice");
  dice.setAttribute("role", "group");
  const remaining = el("p", "bg-remaining");
  const history = el("p", "bg-history");
  const roll = button("Roll dice", "button primary", () => play({ kind: "roll" }));
  const pass = button("Pass turn", "button secondary", () => play({ kind: "pass" }));
  const choiceButtons = el("div", "bg-choice-buttons");
  const choices = el("div", "bg-choices", el("span", "", "Choose a die:"), choiceButtons);
  choices.hidden = true;
  const players = el("ul", "bg-players");
  players.setAttribute("aria-label", "Players and remaining pips");
  const seats = colors.map((color, seat) => {
    const name = el("strong");
    const pips = el("span", "bg-seat-pips");
    const item = el("li", "bg-seat", checker(seat), name, el("span", "bg-seat-color", color), pips);
    return { name, pips, item };
  });
  players.append(...seats.map(seat => seat.item));
  const board = el("div", "bg-board");
  board.setAttribute("role", "group");
  board.setAttribute("aria-label", "Backgammon board");
  board.setAttribute("aria-describedby", "backgammon-board-help");
  const boardHelp = el("p", "bg-board-help",
    "Ivory moves toward 1; Umber moves toward 24. Use the arrow keys, then Enter or Space.",
    el("span", "bg-scroll-hint", " Swipe or scroll sideways for the full board."));
  boardHelp.id = "backgammon-board-help";
  const viewport = el("div", "feature-scroll bg-board-window", board);
  const guide = el("details", "bg-guide", el("summary", "", "How to play"),
    el("p", "", "The higher opening roll goes first and uses both dice. Select a highlighted checker, then a highlighted destination. Enter from the bar first. Two or more opposing checkers block a point; one alone can be hit."),
    el("p", "", "Use as many dice as possible, the higher die if either one alone would work, and up to four moves for doubles. Once all your checkers are home, move them to the off tray. First to bear off fifteen wins."));
  const root = el("section", "feature-stack bg-table", players,
    el("div", "bg-turn", status, el("div", "bg-roll-line", dice, remaining, roll, pass), history, choices),
    viewport, boardHelp, guide);
  root.setAttribute("aria-label", "Backgammon match");

  let current: MultiplayerTable<Position> | undefined;
  let enabled = false;
  let selected: number | null = null;
  let previous = "";
  let active = 0;
  const cells = [...top.map(location => makeCell(location, 1)), ...bottom.map(location => makeCell(location, 0))];
  board.append(...cells.map(cell => cell.control));
  setActive(0);
  board.addEventListener("keydown", event => {
    const column = active % top.length;
    let next = active;
    switch (event.key) {
      case "ArrowLeft": if (column > 0) next--; break;
      case "ArrowRight": if (column < top.length - 1) next++; break;
      case "ArrowUp": if (active >= top.length) next -= top.length; break;
      case "ArrowDown": if (active < top.length) next += top.length; break;
      case "Home": next -= column; break;
      case "End": next += top.length - 1 - column; break;
      default: return;
    }
    event.preventDefault();
    cells[next].control.focus();
  });
  root.addEventListener("keydown", event => {
    if (event.key !== "Escape" || selected === null || !enabled) return;
    const wasChoosing = choiceButtons.contains(document.activeElement);
    setSelection(null);
    if (wasChoosing) cells[active].control.focus();
    event.preventDefault();
  });
  cells.forEach((cell, index) => cell.control.addEventListener("focus", () => setActive(index)));

  function makeCell(location: number, rowSeat: number): Cell {
    const isPoint = location !== bar && location !== off;
    const kind = location === bar ? "bar" : location === off ? "off" : "point";
    const seat = isPoint ? undefined : rowSeat;
    const control = button("", `bg-cell bg-${kind} ${rowSeat === 0 ? "bg-bottom" : "bg-top"}`,
      () => select(location, seat));
    control.tabIndex = -1;
    if (isPoint) control.dataset.point = String(location);
    else control.dataset.seat = String(seat);
    const label = el("span", "bg-point-label", isPoint ? String(location) : kind === "bar" ? "Bar" : "Off");
    const stack = el("span", "bg-stack");
    control.append(label, stack);
    return { control, stack, location, seat };
  }

  function setActive(index: number): void {
    cells[active].control.tabIndex = -1;
    cells[index].control.tabIndex = 0;
    active = index;
  }

  function select(location: number, seat: number | undefined): void {
    if (!enabled || !current || (seat !== undefined && seat !== current.seat)) return;
    const moves = current.position?.legalActions?.moves ?? [];
    const destinations = moves.filter(move => move.from === selected && move.to === location);
    if (destinations.length === 1) {
      play({ kind: "move", ...destinations[0] });
      return;
    }
    if (destinations.length > 1) {
      choiceButtons.replaceChildren(...destinations.map(move => button(`Use ${move.die}`, "button secondary",
        () => { if (enabled) play({ kind: "move", ...move }); })));
      choices.hidden = false;
      text(status, "More than one die can make this move. Choose the die to use.");
      choiceButtons.querySelector("button")?.focus();
      return;
    }
    if (moves.some(move => move.from === location))
      setSelection(location === selected ? null : location);
  }

  function setSelection(location: number | null): void {
    selected = location;
    choices.hidden = true;
    choiceButtons.replaceChildren();
    showControls();
  }

  function cellInfo(cell: Cell, position: Position): [string, number, number] {
    if (cell.location === bar) return [`${colors[cell.seat!]} bar`, cell.seat!, position.bar[cell.seat!]];
    if (cell.location === off) return [`${colors[cell.seat!]} off tray`, cell.seat!, position.off[cell.seat!]];
    const count = position.points[cell.location - 1];
    return [`Point ${cell.location}`, count < 0 ? 1 : 0, Math.abs(count)];
  }

  function showControls(): void {
    const table = current!;
    const position = table.position!;
    const legal = position.legalActions;
    const moves = legal?.moves ?? [];
    const finished = position.winner !== null;
    text(status, finished ? position.winner === table.seat ? "You won the game!" : `${nameAt(table, position.winner!)} wins the game.`
      : position.turn !== table.seat && position.turn !== null ? `${nameAt(table, position.turn)}’s turn.`
      : legal?.canRoll ? "Your turn. Roll the dice."
      : legal?.canPass ? "You have no legal moves. Pass the turn."
      : !choices.hidden ? "More than one die can make this move. Choose the die to use."
      : selected !== null ? `${selected === bar ? "Bar" : `Point ${selected}`} selected. Choose a highlighted destination.`
      : moves[0]?.from === bar ? "You must enter from the bar. Select your bar."
      : "Select one of your highlighted checkers.");
    roll.hidden = !legal?.canRoll;
    pass.hidden = !legal?.canPass;
    roll.disabled = pass.disabled = !enabled;
    for (const control of choiceButtons.querySelectorAll("button")) control.disabled = !enabled;
    for (const cell of cells) {
      const { control, location, seat } = cell;
      const ownLocation = seat === undefined || seat === table.seat;
      const source = enabled && ownLocation && moves.some(move => move.from === location);
      const destination = enabled && ownLocation && moves.some(move => move.from === selected && move.to === location);
      control.toggleAttribute("data-source", source);
      control.toggleAttribute("data-destination", destination);
      control.setAttribute("aria-disabled", String(!source && !destination));
      if (source) control.setAttribute("aria-pressed", String(selected === location));
      else control.removeAttribute("aria-pressed");
      const [name, color, count] = cellInfo(cell, position);
      control.setAttribute("aria-label", `${name}: ${count ? `${count} ${colors[color]} ${count === 1 ? "checker" : "checkers"}` : "empty"}`
        + (destination ? ". Legal destination" : source ? ". Movable" : ""));
    }
  }

  return {
    root,
    show(table, canPlay) {
      current = table;
      enabled = canPlay;
      const position = table.position!;
      const stamp = `${table.id}/${table.revision}`;
      const focused = document.activeElement;
      const choiceFocused = choiceButtons.contains(focused);
      if (stamp !== previous) {
        previous = stamp;
        setSelection(null);
        const first = position.legalActions?.moves[0]?.from;
        const ready = cells.findIndex(cell => cell.location === first && (cell.seat === undefined || cell.seat === table.seat));
        if (ready >= 0 && !board.contains(focused)) setActive(ready);
        for (const cell of cells) {
          const [, color, count] = cellInfo(cell, position);
          const shown = Math.min(cell.location === off ? 3 : 5, count);
          cell.stack.replaceChildren(...Array.from({ length: shown }, (_, index) =>
            checker(color, index === shown - 1 && count > shown ? count : 0)));
        }
        for (const [seat, display] of seats.entries()) {
          text(display.name, nameAt(table, seat));
          const pips = position.points.reduce((total, count, index) =>
            total + (seat === 0 ? Math.max(0, count) * (index + 1) : Math.max(0, -count) * (24 - index)), position.bar[seat] * off);
          text(display.pips, `${pips} pips`);
          display.item.toggleAttribute("data-acting", position.turn === seat);
        }
        dice.replaceChildren(...(position.dice.length ? position.dice.map(dieFace) : [el("span", "", "Not rolled")]));
        dice.setAttribute("aria-label", position.dice.length ? `Rolled dice: ${position.dice.join(" and ")}` : "Dice not rolled");
        remaining.hidden = position.dice.length === 0;
        text(remaining, `To play: ${position.remaining.join(" · ")}`);
        const last = position.lastTurn;
        text(history, last ?
          `${last.seat === table.seat ? "You" : nameAt(table, last.seat)} rolled ${last.dice.join("–")}.`
          + (last.unused.length && position.winner === null ? ` Could not play ${last.unused.join(" · ")}.` : "") :
          `Opening roll: Ivory ${position.openingRoll[0]} · Umber ${position.openingRoll[1]}. The higher roll goes first.`);
      } else {
        showControls();
      }
      if ((focused === roll && roll.hidden) || (focused === pass && pass.hidden) || (choiceFocused && choices.hidden))
        status.focus();
    },
  };
}

export function mount(host: PortalHost): void {
  new Multiplayer(host, { game: "backgammon", endpoint: "backgammon", playerCounts: [2], view });
}
