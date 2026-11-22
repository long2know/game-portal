import "./holdem.css";
import { button, el, text } from "../dom";
import type { PortalHost } from "../host";
import { cardFace, suits } from "./cards";
import { Multiplayer, nameAt } from "./multiplayer";
import type { MatchView, MultiplayerTable } from "./multiplayer";
import { field } from "./shared";

type Card = { rank: number; suit: number };
type Seat = {
  seat: number; stack: number; streetCommitted: number; handCommitted: number; dealtIn: boolean; folded: boolean; isAllIn: boolean;
};
type Wager = { minTo: number; maxTo: number };
type HandResult = {
  reason: number;
  pots: { chips: number; awards: { seat: number; chips: number }[] }[];
  showdownHands: { seat: number; cards: Card[]; rank: { category: number } }[];
};
type Position = {
  handNumber: number; buttonSeat: number; smallBlindSeat: number; bigBlindSeat: number;
  smallBlind: number; bigBlind: number; street: number; actingSeat: number | null; currentBet: number;
  potChips: number; seats: Seat[]; board: Card[]; ownCards: Card[]; result: HandResult | null; matchWinnerSeat: number | null;
  legalActions: { canFold: boolean; canCheck: boolean; callAmount: number | null; bet: Wager | null; raise: Wager | null } | null;
};
type Action = { kind: "fold" | "check" | "call" | "bet" | "raise"; to?: number };
const streets = ["Preflop", "Flop", "Turn", "River"];
const categories = ["High card", "One pair", "Two pair", "Three of a kind", "Straight", "Flush",
  "Full house", "Four of a kind", "Straight flush"];

function cards(projected: Card[]): HTMLElement[] {
  return projected.map(card => cardFace({ rank: card.rank, suit: suits[card.suit] }));
}

function view(play: (action: Action) => void, next: () => void): MatchView<Position> {
  const status = el("p", "feature-status");
  status.tabIndex = -1;
  status.setAttribute("role", "status");
  const header = el("p", "eyebrow");
  const pot = el("strong", "poker-pot");
  const board = el("div", "poker-community");
  board.setAttribute("role", "group");
  board.setAttribute("aria-label", "Community cards");
  const own = el("div", "poker-own");
  own.setAttribute("role", "group");
  own.setAttribute("aria-label", "Your cards");
  const seats = el("ul", "poker-seats");
  seats.setAttribute("aria-label", "Seats and chip counts");
  const awards = el("p", "poker-awards");
  const nextHand = button("Next hand", "button primary", next);
  const fold = button("Fold", "button secondary", () => play({ kind: "fold" }));
  const check = button("Check", "button primary", () => play({ kind: "check" }));
  const call = button("Call", "button primary", () => play({ kind: "call" }));
  const chips = el("input");
  chips.type = "number";
  chips.inputMode = "numeric";
  chips.required = true;
  chips.step = "1";
  const range = el("p", "feature-hint");
  range.id = "holdem-range";
  chips.setAttribute("aria-describedby", range.id);
  const submit = button("", "button primary");
  submit.type = "submit";
  const wager = el("form", "poker-wager", field("Street total (chips)", chips), submit, range);
  const controls = el("section", "feature-panel", el("h2", "", "At the rail"),
    status, awards, el("div", "feature-controls", check, call, fold, nextHand), wager);
  let action: "bet" | "raise" = "bet";
  let previous = "";
  let enabled = false;
  wager.addEventListener("submit", event => {
    event.preventDefault();
    if (enabled && !wager.hidden && chips.checkValidity()) play({ kind: action, to: chips.valueAsNumber });
  });
  const root = el("div", "feature-stack holdem-layout",
    el("section", "poker-felt", header, pot, board,
      el("div", "poker-own-lane", el("p", "eyebrow", "Your hand"), own), seats), controls);
  return {
    root,
    show(table, canPlay) {
      const position = table.position!;
      const { actingSeat, result, matchWinnerSeat, legalActions: legal } = position;
      const describe = (seat: number) => nameAt(table, seat);
      enabled = canPlay;
      text(status, matchWinnerSeat != null ? matchWinnerSeat === table.seat ? "You won the table." : `${describe(matchWinnerSeat)} wins the table.` :
        result ? result.reason === 1 ? "Showdown · hand complete" : "Hand complete · all other players folded" :
          actingSeat === table.seat ? "Your turn" :
            actingSeat != null ? `${describe(actingSeat)}’s turn` : "Hand in progress");
      const awardTotals = new Map<number, number>();
      for (const { awards } of result?.pots ?? []) {
        for (const { seat, chips } of awards) awardTotals.set(seat, (awardTotals.get(seat) ?? 0) + chips);
      }
      text(awards, [...awardTotals].map(([seat, amount]) => seat === table.seat
        ? `You take ${amount} chips.` : `${describe(seat)} takes ${amount} chips.`).join(" "));
      awards.hidden = !awardTotals.size;

      const active = document.activeElement;
      for (const [control, available] of [[fold, legal?.canFold], [check, legal?.canCheck], [call, legal?.callAmount != null],
        [nextHand, !!result && matchWinnerSeat == null && table.status === "playing"]] as const) {
        control.hidden = !available;
        control.disabled = !canPlay;
      }
      if (legal?.callAmount != null) text(call, `Call ${legal.callAmount}`);
      const allowed = legal?.bet ?? legal?.raise;
      wager.hidden = !allowed;
      if (allowed) {
        action = legal?.bet ? "bet" : "raise";
        chips.min = String(allowed.minTo);
        chips.max = String(allowed.maxTo);
        if (previous !== `${table.id}/${table.revision}`) chips.value = chips.min;
        text(range, `Choose a total from ${allowed.minTo} to ${allowed.maxTo} chips, including any chips you’ve put in this street.`);
        text(submit, action === "raise" ? "Raise" : "Bet");
      }
      chips.disabled = submit.disabled = !canPlay;
      if (active instanceof HTMLElement && active.closest("[hidden]")) status.focus();

      const stamp = `${table.id}/${table.revision}`;
      if (stamp === previous) return;
      previous = stamp;
      text(header, `Hand ${position.handNumber} · ${streets[position.street]} · Blinds ${position.smallBlind}/${position.bigBlind}`);
      text(pot, result ? `${[...awardTotals.values()].reduce((total, chips) => total + chips, 0)} chips awarded` :
        `${position.potChips} chips in the pot`);
      board.replaceChildren(...cards(position.board), ...Array.from({ length: 5 - position.board.length }, () => {
        const slot = el("span", "poker-card-slot");
        slot.setAttribute("aria-hidden", "true");
        return slot;
      }));
      own.replaceChildren(...(position.ownCards.length ? cards(position.ownCards) : [el("span", "feature-hint", "No cards this hand.")]));
      seats.replaceChildren(...position.seats.map(item => renderSeat(table, position, item)));
    },
  };
}

function renderSeat(table: MultiplayerTable<Position>, position: Position, seat: Seat): HTMLElement {
  const { seat: index } = seat;
  const shown = position.result?.showdownHands.find(item => item.seat === index);
  const marks = [
    index === position.buttonSeat ? "Dealer" : "",
    index === position.smallBlindSeat ? "Small blind" : "",
    index === position.bigBlindSeat ? "Big blind" : "",
    seat.folded ? "Folded" : seat.isAllIn ? "All-in" : !seat.dealtIn ? "Sitting out" : "",
  ].filter(Boolean);
  const row = el("li", "poker-seat",
    el("strong", "", nameAt(table, index)),
    el("p", "poker-stack", `${seat.stack} chips`, seat.streetCommitted ? ` · ${seat.streetCommitted} in this street` : ""),
    el("p", "feature-hint", marks.join(" · ")));
  row.toggleAttribute("data-acting", position.actingSeat === index);
  if (index !== table.seat && seat.dealtIn) {
    const privateCards = el("div", "poker-seat-cards", ...(shown ? cards(shown.cards) : [cardFace(), cardFace()]));
    privateCards.setAttribute("role", "group");
    privateCards.setAttribute("aria-label", `${nameAt(table, index)}’s cards`);
    row.append(privateCards);
  }
  if (shown) row.append(el("p", "feature-hint", categories[shown.rank.category] ?? "Shown hand"));
  return row;
}

export function mount(host: PortalHost): void {
  new Multiplayer(host, { game: "holdem", endpoint: "poker", playerCounts: [2, 3, 4, 5, 6], view });
}
