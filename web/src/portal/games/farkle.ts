import "./farkle.css";
import { button, el, text } from "../dom";
import type { PortalHost } from "../host";
import { Multiplayer, nameAt } from "./multiplayer";
import type { MatchView, MultiplayerTable } from "./multiplayer";

type Action = { kind: "roll" | "bank" } | { kind: "keep"; dice: number[] };
type TurnResult = { seat: number; kind: "banked" | "farkle"; points: number; dice: number[] };
type Position = {
  turn: number | null; phase: "roll" | "keep" | "finished"; scores: number[];
  dice: number[]; kept: number[]; diceToRoll: number; turnPoints: number;
  canRoll: boolean; canBank: boolean; choices: { mask: number; points: number }[];
  finalStarter: number | null; winners: number[]; recentTurns: TurnResult[];
};

const number = new Intl.NumberFormat("en-US");
const format = (value: number) => number.format(value);
const pips: Record<number, number[]> = {
  1: [5], 2: [1, 9], 3: [1, 5, 9], 4: [1, 3, 7, 9], 5: [1, 3, 5, 7, 9], 6: [1, 3, 4, 6, 7, 9],
};

function face(faceValue: number): HTMLElement {
  const art = el("span", "farkle-pips");
  art.setAttribute("aria-hidden", "true");
  for (const cell of pips[faceValue]) {
    const pip = el("span", "farkle-pip");
    pip.style.gridArea = `${Math.ceil(cell / 3)} / ${(cell - 1) % 3 + 1}`;
    art.append(pip);
  }
  return art;
}

function miniRoll(dice: number[]): HTMLElement {
  const roll = el("span", "farkle-mini-roll", ...dice.map(value => {
    const die = el("span", "farkle-mini-die", face(value));
    die.dataset.face = String(value);
    return die;
  }));
  roll.setAttribute("role", "img");
  roll.setAttribute("aria-label", `Rolled ${dice.join(", ")}`);
  return roll;
}

function guide(): HTMLElement {
  const entries = [
    ["Single 1 / single 5", "100 / 50"],
    ["Three 1s", "300"],
    ["Three 2s through 6s", "200–600"],
    ["Four / five / six of a kind", "1,000 / 2,000 / 3,000"],
    ["Straight, three pairs, or four of a kind and a pair", "1,500"],
    ["Two triples", "2,500"],
  ];
  return el("details", "farkle-guide",
    el("summary", "", "Scoring guide"),
    el("p", "", "Keep only dice that score together on this roll. Keep every available die for hot dice and roll all six again."),
    el("dl", "farkle-values", ...entries.map(([pattern, points]) =>
      el("div", "", el("dt", "", pattern), el("dd", "", points)))),
    el("p", "", "Your first bank needs 500 points. Later banks can be any positive score. A Farkle loses only the points from this turn."),
    el("p", "", "At 10,000, everyone else gets one last turn. Highest total wins; tied leaders share the win."));
}

function match(play: (action: Action) => void): MatchView<Position> {
  let table: MultiplayerTable<Position> | undefined;
  let position: Position | undefined;
  let enabled = false;
  let selected = 0;

  const round = el("p", "farkle-round", "Race to 10,000");
  const turn = el("h2", "farkle-turn");
  turn.tabIndex = -1;
  turn.setAttribute("aria-live", "polite");
  const unbanked = el("output", "", "0");
  unbanked.setAttribute("aria-label", "Unbanked points");
  const purse = el("dl", "farkle-purse", el("dt", "", "Unbanked points"), el("dd", "", unbanked));
  const caption = el("p", "farkle-caption");
  const hint = el("p", "farkle-hint");
  hint.setAttribute("role", "status");
  hint.id = "farkle-dice-hint";
  const resultNote = el("div", "farkle-result-note");
  resultNote.setAttribute("role", "status");
  const dice = Array.from({ length: 6 }, (_, index) => button("", "farkle-die", () => {
    if (!enabled || !position || position.turn !== table?.seat || position.phase !== "keep") return;
    selected ^= 1 << index;
    renderDecision();
  }));
  const wells = dice.map(die => el("div", "farkle-well", die));
  const tray = el("div", "farkle-tray", ...wells);
  tray.setAttribute("role", "group");
  tray.setAttribute("aria-label", "Current dice");
  tray.setAttribute("aria-describedby", hint.id);
  const roll = button("Roll", "button", () => {
    if (enabled && position?.canRoll) play({ kind: "roll" });
  });
  const keep = button("Keep", "button", () => {
    if (enabled && position?.choices.some(choice => choice.mask === selected)) {
      play({ kind: "keep", dice: position.dice.flatMap((_, index) => selected & (1 << index) ? [index] : []) });
    }
  });
  const bank = button("Bank", "button", () => {
    if (enabled && position?.canBank) play({ kind: "bank" });
  });
  const controls = el("div", "farkle-actions", roll, keep, bank);
  const ledgerTitle = el("h2", "", "Banked scores");
  ledgerTitle.id = "farkle-ledger-title";
  const scores = el("ol", "farkle-scores");
  scores.setAttribute("role", "list");
  scores.setAttribute("aria-labelledby", ledgerTitle.id);
  const lastRound = el("p", "farkle-ledger-note");
  const history = el("ul", "farkle-history");
  history.setAttribute("role", "list");
  history.setAttribute("aria-label", "Recent turns");
  const noHistory = el("p", "farkle-empty-history", "Completed turns will appear here.");
  const ledger = el("section", "farkle-ledger", ledgerTitle, scores, lastRound);
  const live = el("section", "farkle-play",
    el("header", "farkle-play-head", el("div", "", round, turn), purse),
    caption, tray, controls, hint, resultNote);
  const root = el("div", "farkle-game",
    el("div", "farkle-table", live, ledger),
    el("div", "farkle-notes", el("section", "farkle-recent", el("h2", "", "Recent turns"), history, noHistory), guide()));

  function setAction(control: HTMLButtonElement, available: boolean, accent: boolean): void {
    control.setAttribute("aria-disabled", String(!available));
    control.toggleAttribute("data-accent", available && accent);
    control.tabIndex = available ? 0 : -1;
  }

  function renderDecision(): void {
    if (!table || !position) return;
    const { turn, phase, canRoll, canBank, choices, dice: faces, kept, diceToRoll } = position;
    const mine = turn === table.seat;
    const pick = phase === "keep" && mine;
    const choice = choices.find(option => option.mask === selected);
    for (const [index, die] of dice.entries()) {
      const value = faces[index];
      const keptHere = kept.includes(index);
      const pressed = pick && !!(selected & (1 << index));
      die.hidden = value === undefined;
      wells[index].toggleAttribute("data-filled", !die.hidden);
      if (die.hidden) continue;
      if (die.dataset.face !== String(value)) {
        die.dataset.face = String(value);
        die.replaceChildren(face(value));
      }
      die.setAttribute("aria-label", `Die ${index + 1}: ${value}${keptHere ? ", kept" : ""}`);
      die.setAttribute("aria-disabled", String(!enabled || !pick));
      die.tabIndex = enabled && pick ? 0 : -1;
      die.toggleAttribute("data-kept", keptHere);
      if (pick) die.setAttribute("aria-pressed", String(pressed));
      else die.removeAttribute("aria-pressed");
    }
    setAction(roll, enabled && canRoll, !canBank);
    setAction(keep, enabled && pick && !!choice, true);
    setAction(bank, enabled && canBank, true);

    let message: string;
    if (phase === "finished") message = "The highest banked total wins.";
    else if (pick) message = !selected ? "Select dice that score. Then choose Keep." :
      choice ? `${format(choice.points)} selected points. Keep to add them to this turn.` :
        choices.some(option => (option.mask & selected) === selected)
          ? "Complete the scoring set, or unselect a die."
          : "That selection doesn’t score. Unselect a die to try again.";
    else if (phase === "keep") message = "Waiting for scoring dice to be kept.";
    else if (mine && !canRoll) message = "Roll limit reached. Bank to end your turn.";
    else if (kept.length > 0 && kept.length === faces.length)
      message = `Hot dice! Roll all six again${canBank ? " or bank these points" : ""}.`;
    else if (!faces.length) message = mine ? "Roll six dice to start your turn." : "The next roll starts with all six dice.";
    else if (!mine) message = "The active player can roll again or bank if eligible.";
    else if (canBank) message = `Bank these points or risk them by rolling ${diceToRoll === 1 ? "one die" : `${diceToRoll} dice`}.`;
    else message = `Your first bank needs 500 points. Roll ${diceToRoll === 1 ? "one die" : `${diceToRoll} dice`} to continue.`;
    text(hint, message);
    text(caption, phase === "finished" ? "Table complete" :
      phase === "keep" ? `${faces.length} ${faces.length === 1 ? "die" : "dice"} rolled` :
        mine && !canRoll ? "Ready to bank" : `${diceToRoll} ${diceToRoll === 1 ? "die" : "dice"} ready to roll`);
  }

  function renderScoreboard(): void {
    if (!table || !position) return;
    const current = table;
    const { scores: totals, turn: active, finalStarter, phase, winners, recentTurns } = position;
    const tied = winners.length > 1;
    text(round, phase === "finished" ? "Game complete" : finalStarter === null ? "Race to 10,000" : "Final round");
    text(turn, phase === "finished" ?
      tied ? "Tied for the win" :
        winners[0] === current.seat ? "You win" : `${nameAt(current, winners[0])} wins` :
      active === current.seat ? "Your turn" : `${nameAt(current, active!)}’s turn`);
    text(unbanked, format(position.turnPoints));
    text(lastRound, finalStarter === null || phase === "finished" ? "Target: 10,000 points" :
      `${finalStarter === current.seat ? "You started" : `${nameAt(current, finalStarter)} started`} the final round. Everyone else gets one last turn.`);
    scores.replaceChildren(...current.seats.map(item => {
      const isMine = item.seat === current.seat;
      const winner = winners.includes(item.seat);
      const bar = el("span", "farkle-score-line");
      bar.setAttribute("aria-hidden", "true");
      bar.style.setProperty("--banked", `${Math.min(100, totals[item.seat] / 100)}%`);
      const row = el("li", "",
        el("div", "farkle-score-row", el("span", "farkle-player",
          el("strong", "", isMine ? "You" : item.name || `Seat ${item.seat + 1}`),
          ...(winner ? [el("small", "", "Winner")] : [])),
        el("span", "farkle-total", format(totals[item.seat]))), bar);
      if (item.seat === active) row.setAttribute("aria-current", "true");
      row.toggleAttribute("data-winner", winner);
      return row;
    }));

    const last = current.seats.some(item => item.bot) ?
      recentTurns.find(result => result.seat === current.seat) ?? recentTurns[0] : recentTurns[0];
    resultNote.hidden = !last || position.dice.length > 0;
    resultNote.toggleAttribute("data-farkle", last?.kind === "farkle");
    if (last && !resultNote.hidden) {
      const name = nameAt(current, last.seat);
      const summary = el("span", "", last.kind === "banked" ? `${name} banked ${format(last.points)} points.` :
        `Farkle! ${name} ${last.points ? `lost ${format(last.points)} unbanked points.` : "rolled no scoring dice."}`);
      resultNote.replaceChildren(summary, ...(last.dice.length ? [miniRoll(last.dice)] : []));
    } else resultNote.replaceChildren();
    noHistory.hidden = recentTurns.length > 0;
    history.replaceChildren(...recentTurns.map(result => {
      const summary = el("div", "farkle-history-row",
        el("strong", "", nameAt(current, result.seat)),
        el("span", "", result.kind === "banked" ? `+${format(result.points)} banked` :
          result.points ? `Farkle · ${format(result.points)} lost` : "Farkle · no score"));
      const item = el("li", "", summary);
      if (result.dice.length) item.append(miniRoll(result.dice));
      return item;
    }));
  }

  return {
    root,
    show(next, available) {
      if (!next.position) return;
      const changed = next.id !== table?.id || next.revision !== table?.revision;
      if (changed) selected = 0;
      table = next;
      position = next.position;
      enabled = available;
      if (changed) renderScoreboard();
      renderDecision();

      const focused = document.activeElement;
      if (changed && available && focused instanceof HTMLButtonElement && live.contains(focused) &&
          (focused.hidden || focused.getAttribute("aria-disabled") === "true")) {
        const toFocus = position.turn === table.seat && position.phase === "keep" ? dice[0] :
          position.canBank ? bank : position.canRoll ? roll : turn;
        toFocus.focus({ preventScroll: true });
      }
    },
  };
}

export function mount(host: PortalHost): void {
  new Multiplayer<Position, Action>(host, {
    game: "farkle", endpoint: "farkle", playerCounts: [2, 3, 4, 5, 6], view: match,
  });
}
