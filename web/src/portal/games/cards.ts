import "./cards.css";
import { el } from "../dom";

export const suits = ["clubs", "diamonds", "hearts", "spades"] as const;
export type Suit = typeof suits[number];
const glyphs = ["♣", "♦", "♥", "♠"];
const high = { 1: "Ace", 11: "Jack", 12: "Queen", 13: "King", 14: "Ace" } as Record<number, string>;

export const suitMark = (suit: Suit) => glyphs[suits.indexOf(suit)];
export const cardName = (rank: number, suit: Suit) => `${high[rank] ?? rank} of ${suit}`;

export function cardFace(card?: { rank: number; suit: Suit }): HTMLElement {
  const face = el("span", card ? "playing-card" : "playing-card card-back");
  face.setAttribute("role", "img");
  const art = el("span", "playing-card-art");
  art.setAttribute("aria-hidden", "true");
  if (card) {
    const { rank, suit } = card;
    const index = el("span", "card-rank", high[rank]?.[0] ?? String(rank));
    index.dataset.pip = suitMark(suit);
    face.dataset.suit = suit;
    face.setAttribute("aria-label", cardName(rank, suit));
    art.append(index, el("span", "card-suit", suitMark(suit)));
  } else {
    face.setAttribute("aria-label", "Face-down card");
    art.append(el("span", "back-mark", "◆"));
  }
  face.append(art);
  return face;
}
