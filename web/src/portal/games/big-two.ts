import "./big-two.css";
import { button, el, text } from "../dom";
import type { PortalHost } from "../host";
import { cardFace, cardName, suits } from "./cards";
import { Multiplayer, nameAt } from "./multiplayer";
import type { MatchView, MultiplayerTable } from "./multiplayer";

type Card = { id: number; rank: number; suit: number };
type Seat = { seat: number; cardsRemaining: number };
type Position = {
  turnSeat: number | null;
  winnerSeat: number | null;
  seats: Seat[];
  ownCards: Card[];
  lastPlay: { seat: number; kind: string; cards: Card[] } | null;
  legalMasks: number[];
  canPass: boolean;
};
type Action = { kind: "play"; cards: number[] } | { kind: "pass" };

function face({ rank, suit }: Card): HTMLElement {
  return cardFace({ rank, suit: suits[suit] });
}

function view(send: (action: Action) => void): MatchView<Position> {
  const seats = el("ol", "big-two-seats");
  seats.setAttribute("aria-label", "Seats in playing order and cards remaining");
  const trickTitle = el("h2", "", "Centre of the table");
  const trickNote = el("p", "big-two-trick-note");
  const trick = el("div", "big-two-trick");
  trick.setAttribute("role", "group");
  trick.setAttribute("aria-label", "Last played cards");
  const hand = el("div", "big-two-hand");
  hand.setAttribute("role", "group");
  hand.setAttribute("aria-label", "Your cards");
  const turn = el("p", "big-two-turn");
  const selection = el("p", "big-two-selection");
  turn.setAttribute("role", "status");
  selection.setAttribute("role", "status");
  const play = button("Play selected", "button primary", () => {
    if (active && position?.legalMasks.includes(selected)) {
      send({ kind: "play", cards: position.ownCards.filter((_, index) => selected & (1 << index)).map(card => card.id) });
    }
  });
  const pass = button("Pass", "button secondary", () => { if (active && position?.canPass) send({ kind: "pass" }); });
  const root = el("section", "big-two-table board-frame",
    seats,
    el("section", "big-two-centre", trickTitle, trickNote, trick),
    el("section", "big-two-rail",
      el("div", "big-two-rail-title", el("h2", "", "Your hand"), turn), hand,
      el("div", "big-two-actions", play, pass, selection)));
  root.setAttribute("aria-label", "Big Two playing area");
  const rules = el("details", "big-two-rules", el("summary", "", "How to play Big Two"),
    el("p", "", "The first play must include the 3 of diamonds. The first player out wins."),
    el("p", "", "Beat the last play with the same number of cards, or Pass. Three consecutive passes clear the trick, and the last player to put down cards gets a free lead. You cannot pass on a lead."),
    el("p", "", "Play a single, pair, triple, or a five-card combination. Five-card hands rise from straight to flush, full house, four of a kind, then straight flush. Rank runs 3 to 2; suit runs diamonds, clubs, hearts, spades. Straights never contain 2s."));
  let position: Position | undefined;
  let stamp = "";
  let selected = 0;
  let active = false;
  let buttons: HTMLButtonElement[] = [];

  function paintSelection() {
    play.disabled = !active || !position?.legalMasks.includes(selected);
    pass.disabled = !active || !position?.canPass;
    for (const [index, control] of buttons.entries()) {
      control.setAttribute("aria-pressed", String(!!(selected & (1 << index))));
      control.setAttribute("aria-disabled", String(!active));
    }
    const masks = position?.legalMasks ?? [];
    text(selection, !active ? "" : selected ? masks.includes(selected) ? "Ready to play." :
      masks.some(mask => (mask & selected) === selected) ? "Add cards to finish the play." : "That selection isn’t a legal play." :
      !masks.length ? "No play can beat this trick. Pass to continue." : "Select cards, then play.");
  }

  hand.addEventListener("keydown", event => {
    if (!(event.target instanceof HTMLButtonElement)) return;
    const index = buttons.indexOf(event.target);
    if (index < 0) return;
    const next = event.key === "ArrowLeft" ? index - 1 : event.key === "ArrowRight" ? index + 1 :
      event.key === "Home" ? 0 : event.key === "End" ? buttons.length - 1 : -1;
    if (event.key === "Escape" && selected) { event.preventDefault(); selected = 0; paintSelection(); }
    if (buttons[next]) { event.preventDefault(); buttons[next].focus(); }
  });

  return {
    root: el("div", "big-two-layout", root, rules),
    show(table: MultiplayerTable<Position>, enabled) {
      position = table.position!;
      const { turnSeat, winnerSeat, lastPlay, ownCards } = position;
      active = enabled && turnSeat === table.seat;
      const who = (seat: number) => nameAt(table, seat);
      text(turn, winnerSeat !== null ? winnerSeat === table.seat ? "You won!" : `${who(winnerSeat)} wins.` :
        turnSeat === null ? "Hand complete." : turnSeat === table.seat ? "Your turn" : `${who(turnSeat)}’s turn`);
      const next = `${table.id}/${table.revision}`;
      if (stamp !== next) {
        const focusCard = document.activeElement instanceof HTMLElement ? document.activeElement.dataset.cardId : undefined;
        const focusIndex = focusCard ? buttons.findIndex(control => control.dataset.cardId === focusCard) : -1;
        stamp = next;
        selected = 0;
        const opening = !lastPlay && position.seats.every(seat => seat.cardsRemaining === 13);
        const lead = winnerSeat === null ? lastPlay?.seat ?? turnSeat : null;
        root.dataset.trick = lastPlay ? "in-play" : opening ? "opening" : "clear";
        text(trickNote, lastPlay ? `${who(lastPlay.seat)} · ${lastPlay.kind.replaceAll("-", " ")}` :
          winnerSeat !== null ? "The hand is over." : opening ? "Opening lead · include the 3 ♦" : "Trick cleared · free lead");
        trick.replaceChildren(...(lastPlay ? lastPlay.cards.map(face) : [el("span", "big-two-seal", opening ? "3 ♦" : "◆")]));
        seats.replaceChildren(...[...position.seats].sort((a, b) => ((a.seat - table.seat + 4) % 4) - ((b.seat - table.seat + 4) % 4))
          .map(({ seat, cardsRemaining }) => {
            const label = [winnerSeat === seat ? "Winner" : turnSeat === seat ? "Turn" : "", lead === seat ? "Lead" : "",
              table.seats.find(item => item.seat === seat)?.bot ? "Bot" : ""].filter(Boolean).join(" · ");
            const node = el("li", "big-two-seat", el("strong", "", who(seat)),
              el("span", "big-two-count", `${cardsRemaining} cards`), el("small", "", label));
            node.dataset.seat = String(seat);
            node.toggleAttribute("data-leading", lead === seat);
            node.toggleAttribute("data-acting", turnSeat === seat);
            node.toggleAttribute("data-winner", winnerSeat === seat);
            return node;
          }));
        buttons = ownCards.map((card, index) => {
          const control = button("", "big-two-card", () => {
            if (!active) return;
            selected ^= 1 << index;
            paintSelection();
          });
          control.dataset.cardId = String(card.id);
          control.setAttribute("aria-label", cardName(card.rank, suits[card.suit]));
          control.append(face(card));
          control.firstElementChild!.setAttribute("aria-hidden", "true");
          return control;
        });
        hand.replaceChildren(...(buttons.length ? buttons : [el("p", "big-two-empty", "No cards left.")]));
        if (focusIndex >= 0) (buttons.find(control => control.dataset.cardId === focusCard)
          ?? buttons[Math.min(focusIndex, buttons.length - 1)])?.focus();
      }
      paintSelection();
    },
  };
}

export function mount(host: PortalHost): void {
  new Multiplayer(host, { game: "big-two", endpoint: "big-two", playerCounts: [4], view });
}
