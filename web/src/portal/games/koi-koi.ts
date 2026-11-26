import "./koi-koi.css";
import { button, el, text } from "../dom";
import type { PortalHost } from "../host";
import { flowerFace, flowerName, publicFlower } from "./koi-deck";
import type { FlowerCard } from "./koi-deck";
import { Multiplayer, nameAt } from "./multiplayer";
import type { MatchView, MultiplayerTable } from "./multiplayer";

type Yaku = { name: string; points: number };
type Seat = { seat: number; handCount: number; captured: FlowerCard[]; score: number; yaku: Yaku[]; continued: boolean };
type Play = { cardId: number; targetIds: number[] };
type Result = { winnerSeat: number | null; points: number; basePoints: number; sevenPointBonus: boolean;
  opponentContinued: boolean; nextDealerSeat: number };
type Position = {
  phase: "hand" | "draw" | "decision" | "roundEnd" | "finished";
  turnSeat: number | null;
  dealerSeat: number;
  round: number;
  totalRounds: number;
  field: FlowerCard[];
  stockCount: number;
  drawnCard: FlowerCard | null;
  ownCards: FlowerCard[];
  seats: Seat[];
  result: Result | null;
  matchWinnerSeat: number | null;
  legalActions: { plays: Play[]; drawTargets: number[]; canScore: boolean; canContinue: boolean; canNextRound: boolean };
};
type Action = { kind: "play"; cardId: number; targetId?: number }
  | { kind: "takeDraw"; targetId: number } | { kind: "score" } | { kind: "continue" };

function guide(): HTMLElement {
  return el("details", "koi-guide",
    el("summary", "", "Learn the table"),
    el("ol", "",
      el("li", "", el("strong", "", "Match the month. "), "Pick a card from your hand. With no match it stays on the field; with one match or three, capture them all. With two matches, pick one."),
      el("li", "", el("strong", "", "Then flip. "), "The stock flips one card on your turn. The same rule applies: if it has two matches, choose one from the field."),
      el("li", "", el("strong", "", "Bank or call Koi-Koi. "), "Gather scoring sets (yaku) in your captures. Bank to end the round and add points to your total. You can call Koi-Koi once per round instead. After your call, you must improve your total to bank. Your opponent can bank if they make a set, and their award doubles."),
      el("li", "", el("strong", "", "Read a card. "), "The month is at the top and the category at the bottom. Artwork and numerals distinguish the plain cards. Use Tab to move between groups, arrow keys to browse cards, Enter to select, and Escape to clear a selection.")));
}

function study(): HTMLElement {
  const samples: FlowerCard[] = [
    { id: 8, month: 3, kind: "bright", name: "Curtain" },
    { id: 21, month: 6, kind: "ribbon", name: "Peony ribbon" },
    { id: 36, month: 10, kind: "animal", name: "Deer" },
  ];
  const spread = el("div", "koi-study-cards", ...samples.map(flowerFace));
  spread.setAttribute("aria-hidden", "true");
  return el("figure", "koi-study", spread,
    el("figcaption", "", el("strong", "", "Four seasons in ink"), el("span", "", "48 flower cards · Match by month")));
}

function cardControl(card: FlowerCard, zone: "hand" | "field"): HTMLButtonElement {
  const control = button("", "koi-card");
  control.dataset.cardId = String(card.id);
  control.dataset.zone = zone;
  control.setAttribute("aria-label", flowerName(card));
  control.append(flowerFace(card));
  return control;
}

function group(name: string, className: string): HTMLElement {
  const root = el("div", className);
  root.setAttribute("role", "group");
  root.setAttribute("aria-label", name);
  return root;
}

function sets(yaku: Yaku[]): HTMLElement {
  return yaku.length
    ? el("ul", "koi-sets", ...yaku.map(set => el("li", "", `${set.name} · ${set.points} ${set.points === 1 ? "point" : "points"}`)))
    : el("p", "koi-empty", "No scoring sets yet.");
}

function view(send: (action: Action) => void, next: () => void): MatchView<Position> {
  const round = el("p", "koi-round");
  const status = el("p", "koi-status");
  status.setAttribute("role", "status");
  status.tabIndex = -1;
  const activity = el("p", "koi-activity");
  activity.setAttribute("role", "status");
  const scores = el("ol", "koi-scores");
  scores.setAttribute("aria-label", "Seats and banked points");
  const field = group("Field cards", "koi-cards");
  const stock = el("span", "koi-stock");
  const draw = el("div", "koi-draw");
  const hand = group("Your cards", "koi-cards");
  const hint = el("p", "koi-hint");
  hint.setAttribute("role", "status");
  const apply = button("", "button primary", () => {
    const play = selectedPlay();
    if (active && play && play.targetIds.length !== 2) commit({ kind: "play", cardId: play.cardId });
  });
  const bank = button("Bank points", "button primary", () => {
    if (active && position.legalActions.canScore) commit({ kind: "score" });
  });
  const continuePlay = button("Call Koi-Koi", "button secondary", () => {
    if (active && position.legalActions.canContinue) commit({ kind: "continue" });
  });
  const decision = el("div", "koi-decision");
  const decisionNote = el("p");
  const decisionBar = el("div", "koi-choice", decisionNote, el("div", "koi-actions", bank, continuePlay));
  const advance = button("Next round", "button primary", () => {
    if (active && position.legalActions.canNextRound) {
      committing = true;
      next();
    }
  });
  const settlement = el("div", "koi-settlement");
  const result = el("section", "koi-result", settlement, advance);
  result.setAttribute("aria-label", "Round result");
  const captures = el("section", "koi-captures", el("h2", "", "Scoring sets & captures"));
  const trays = el("div", "koi-trays");
  captures.append(trays);
  const arena = el("section", "koi-table",
    el("div", "koi-table-head", round, status, activity), scores,
    el("div", "koi-mats",
      el("section", "koi-mat koi-field", el("header", "", el("h2", "", "Field"), stock), draw, field),
      el("section", "koi-mat koi-hand", el("h2", "", "Your hand"), hand, hint, apply, decision, decisionBar)),
    result, captures);
  arena.setAttribute("aria-label", "Koi-Koi playing area");
  let position: Position;
  let tableId = "";
  let renderedRevision: number | undefined;
  let active = false;
  let committing = false;
  let picked: number | undefined;
  let handCards: HTMLButtonElement[] = [];
  let fieldCards: HTMLButtonElement[] = [];
  let handStop = -1;
  const openTrays = new Map<number, HTMLDetailsElement>();

  const selectedPlay = () => position?.legalActions.plays.find(play => play.cardId === picked);
  const selectedTargets = () => position.phase === "draw" ? position.legalActions.drawTargets : selectedPlay()?.targetIds ?? [];
  const targets = () => {
    const ids = selectedTargets();
    return ids.length === 2 ? fieldCards.filter(control => ids.includes(Number(control.dataset.cardId))) : [];
  };
  const setAvailable = (control: HTMLElement, enabled: boolean) => control.setAttribute("aria-disabled", String(!enabled));

  function commit(action: Action) {
    committing = true;
    send(action);
  }

  function paintSelection() {
    const legal = position.legalActions;
    const play = selectedPlay();
    const choices = targets();
    for (const control of fieldCards) {
      const match = selectedTargets().includes(Number(control.dataset.cardId));
      control.toggleAttribute("data-match", match);
      setAvailable(control, active && choices.includes(control));
      control.tabIndex = active && choices.includes(control) ? 0 : -1;
    }
    for (const control of handCards) {
      const id = Number(control.dataset.cardId);
      const legalCard = legal.plays.some(play => play.cardId === id);
      control.setAttribute("aria-pressed", String(id === picked));
      setAvailable(control, active && legalCard);
      control.tabIndex = id === handStop ? 0 : -1;
    }
    apply.hidden = !play || play.targetIds.length === 2;
    setAvailable(apply, active && !apply.hidden);
    if (play) text(apply, play.targetIds.length === 0 ? "Leave on field" :
      play.targetIds.length === 1 ? "Capture match" : "Capture all three");
    bank.hidden = !legal.canScore;
    continuePlay.hidden = !legal.canContinue;
    decisionBar.hidden = !legal.canScore && !legal.canContinue;
    setAvailable(bank, active && legal.canScore);
    setAvailable(continuePlay, active && legal.canContinue);
    advance.hidden = !legal.canNextRound;
    setAvailable(advance, active && legal.canNextRound);
    text(hint, play ? play.targetIds.length === 2 ? "Two same-month matches. Choose one marked card on the field." :
      play.targetIds.length === 0 ? "No match this month. Leave the selected card on the field." :
      play.targetIds.length === 1 ? "One same-month match. Capture both cards." :
      "Three same-month matches. Capture all four cards." : legal.plays.length ? "Pick a card from your hand." : "");
  }

  for (const strip of [hand, field]) {
    strip.addEventListener("keydown", event => {
      const card = event.target;
      if (!(card instanceof HTMLButtonElement)) return;
      if (event.key === "Escape" && picked !== undefined) {
        event.preventDefault();
        const previous = handCards.find(control => Number(control.dataset.cardId) === picked);
        picked = undefined;
        paintSelection();
        previous?.focus();
        return;
      }
      const list = strip === hand ? handCards : targets();
      const index = list.indexOf(card);
      const offset = event.key === "ArrowLeft" || event.key === "ArrowUp" ? -1 :
        event.key === "ArrowRight" || event.key === "ArrowDown" ? 1 : 0;
      const destination = event.key === "Home" ? list[0] : event.key === "End" ? list.at(-1) :
        offset ? list[index + offset] : undefined;
      if (index >= 0 && destination) {
        event.preventDefault();
        destination.focus();
      }
    });
  }

  function paintRevision(table: MultiplayerTable<Position>) {
    const who = (seat: number) => nameAt(table, seat);
    const ordered = [...position.seats].sort((a, b) => Number(b.seat === table.seat) - Number(a.seat === table.seat));
    text(round, `Round ${position.round} / ${position.totalRounds}`);
    let cue: string;
    if (position.phase === "finished") cue = position.matchWinnerSeat === null ? "Match complete · tied" :
      position.matchWinnerSeat === table.seat ? "Match complete · you win" : `Match complete · ${who(position.matchWinnerSeat)} wins`;
    else if (position.phase === "roundEnd") cue = "Round complete";
    else if (position.turnSeat !== table.seat) cue = position.turnSeat === null ? "Waiting for play" :
      `${who(position.turnSeat)} · ${position.phase === "decision" ? "choosing whether to bank" :
        position.phase === "draw" ? "matching the draw" : "to play"}`;
    else cue = position.phase === "draw" ? "Your draw · pick a marked same-month card" :
      position.phase === "decision" ? "You made a scoring set" : "Your turn · choose a card";
    text(status, cue);
    text(stock, `${position.stockCount} in stock`);
    scores.replaceChildren(...ordered.map(({ seat, handCount, score, continued }) => {
      const notes = [seat === position.dealerSeat ? "Dealer" : "", table.seats.find(item => item.seat === seat)?.bot ? "Bot" : "",
        continued ? "Called Koi-Koi" : ""].filter(Boolean);
      const row = el("li", "koi-seat",
        el("div", "koi-seat-name", el("strong", "", who(seat)), el("span", "", `${handCount} in hand`)),
        el("div", "koi-seat-score", el("strong", "", String(score)), el("span", "", "banked")),
        ...(notes.length ? [el("small", "koi-seat-note", notes.join(" · "))] : []));
      row.dataset.seat = String(seat);
      row.toggleAttribute("data-turn", seat === position.turnSeat);
      return row;
    }));
    for (const { seat, captured, yaku } of ordered) {
      let tray = openTrays.get(seat);
      if (!tray) {
        tray = el("details", "", el("summary"));
        openTrays.set(seat, tray);
        trays.append(tray);
      }
      text(tray.querySelector("summary")!, `${seat === table.seat ? "Your captures" : `${who(seat)} · Captures`} · ${captured.length}`);
      tray.querySelector(".koi-tray-body")?.remove();
      const body = el("div", "koi-tray-body", sets(yaku));
      if (captured.length) {
        const visible = group(`${who(seat)} · Captured cards`, "koi-cards");
        visible.append(...captured.map(publicFlower));
        body.append(visible);
      } else body.append(el("p", "koi-empty", "No cards captured yet."));
      tray.append(body);
    }
    draw.hidden = !position.drawnCard;
    draw.replaceChildren(...(position.drawnCard ? [publicFlower(position.drawnCard),
      el("p", "", "Stock draw", el("small", "", position.turnSeat === table.seat ?
        "Choose a match on the field." : "A public card awaiting a match."))] : []));
    fieldCards = position.field.map(card => {
      const control = cardControl(card, "field");
      control.addEventListener("click", () => {
        if (!active || !targets().includes(control)) return;
        if (position.phase === "draw") commit({ kind: "takeDraw", targetId: card.id });
        else if (picked !== undefined) commit({ kind: "play", cardId: picked, targetId: card.id });
      });
      return control;
    });
    field.replaceChildren(...(fieldCards.length ? fieldCards : [el("p", "koi-empty", "The field is empty.")]));
    handCards = position.ownCards.map(card => {
      const control = cardControl(card, "hand");
      control.addEventListener("focus", () => {
        handStop = card.id;
        for (const candidate of handCards) candidate.tabIndex = candidate === control ? 0 : -1;
      });
      control.addEventListener("click", event => {
        if (!active || !position.legalActions.plays.some(play => play.cardId === card.id)) return;
        picked = picked === card.id ? undefined : card.id;
        paintSelection();
        if (!event.detail) targets()[0]?.focus();
      });
      return control;
    });
    if (!position.ownCards.some(card => card.id === handStop)) handStop = position.ownCards[0]?.id ?? -1;
    hand.replaceChildren(...(handCards.length ? handCards : [el("p", "koi-empty", "No cards in your hand.")]));
    decision.hidden = position.phase !== "decision" || position.turnSeat !== table.seat;
    decision.replaceChildren(...(decision.hidden ? [] : [sets(position.seats.find(seat => seat.seat === table.seat)?.yaku ?? [])]));
    text(decisionNote, position.legalActions.canContinue ?
      "Bank to end the round, or call Koi-Koi and give up these points for a chance to score more." :
      "Bank these points to close the round.");
    const outcome = position.result;
    result.hidden = !outcome;
    settlement.replaceChildren();
    if (outcome) {
      const { winnerSeat, points, basePoints, sevenPointBonus, opponentContinued, nextDealerSeat } = outcome;
      const award = winnerSeat === null ? "No points awarded" :
        `${winnerSeat === table.seat ? "You banked" : `${who(winnerSeat)} banked`} ${points} ${points === 1 ? "point" : "points"}`;
      const breakdown = [`${basePoints} from sets`, ...(sevenPointBonus ? ["7+ points ×2"] : []),
        ...(opponentContinued ? ["Opponent called Koi-Koi ×2"] : [])];
      settlement.append(el("h2", "", award));
      if (winnerSeat !== null) settlement.append(el("p", "", breakdown.join(" · ")));
      if (position.phase === "roundEnd") settlement.append(el("p", "", `Next dealer: ${who(nextDealerSeat)}`));
    }
  }

  return {
    root: arena,
    show(table, enabled) {
      const before = position;
      position = table.position!;
      active = enabled;
      const sameTable = table.id === tableId;
      if (sameTable && renderedRevision === table.revision) {
        paintSelection();
        if (enabled) committing = false;
        return;
      }
      const previous = document.activeElement;
      const focusCard = previous instanceof HTMLButtonElement && arena.contains(previous) ? previous.dataset : undefined;
      const restore = !!focusCard?.zone || previous === apply || previous === bank || previous === continuePlay || previous === advance;
      if (!sameTable) {
        tableId = table.id;
        handStop = -1;
        openTrays.clear();
        trays.replaceChildren();
      }
      const captured = sameTable && renderedRevision === table.revision - 1 && before.round === position.round
        ? position.seats.flatMap(seat => {
          const gained = seat.captured.length - (before.seats.find(prior => prior.seat === seat.seat)?.captured.length ?? 0);
          return gained > 0 ? [`${nameAt(table, seat.seat)} captured ${gained} cards.`] : [];
        }) : [];
      text(activity, captured.join(" "));
      renderedRevision = table.revision;
      picked = undefined;
      paintRevision(table);
      paintSelection();
      if (restore || committing && (previous === document.body || previous === status)) {
        const old = (focusCard?.zone === "field" ? targets() : handCards).find(card => card.dataset.cardId === focusCard?.cardId);
        const updated = active && (old ?? targets()[0] ?? (!bank.hidden ? bank :
          !advance.hidden ? advance : position.legalActions.plays.length ? handCards.find(card => card.tabIndex === 0) : undefined));
        (updated || status).focus({ preventScroll: !committing });
      }
      committing = false;
    },
  };
}

export function mount(host: PortalHost): void {
  new Multiplayer(host, { game: "koi-koi", endpoint: "koi-koi", playerCounts: [2], view, study: study(), guide: guide() });
}
