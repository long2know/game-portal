import "./cards.css";
import { el, svg } from "../dom";

export const suits = ["clubs", "diamonds", "hearts", "spades"] as const;
export type Suit = typeof suits[number];
const glyphs = ["♣", "♦", "♥", "♠"];
const high = { 1: "Ace", 11: "Jack", 12: "Queen", 13: "King", 14: "Ace" } as Record<number, string>;
const stamps: Record<Suit, string> = {
  clubs: "M0-4a2.3 2.3 0 0 1 1.9 3.6A2.35 2.35 0 1 1 .8 2.1L1.65 4h-3.3l.85-1.9A2.35 2.35 0 1 1-1.9-.4 2.3 2.3 0 0 1 0-4Z",
  diamonds: "M0-4.5 3.3 0 0 4.5-3.3 0Z",
  hearts: "M0 4s-4-2.8-4-5.2C-4-3.8-1.1-4.7 0-2.4 1.1-4.7 4-3.8 4-1.2 4 1.2 0 4 0 4Z",
  spades: "M0-4.4C.8-3 4-.9 4 1.1a2.2 2.2 0 0 1-3.4 1.8L1.6 4H-1.6l1-1.1A2.2 2.2 0 0 1-4 1.1C-4-.9-.8-3 0-4.4Z",
};

type Pip = readonly [x: number, y: number];
const corners: Pip[] = [[8, 6], [28, 6], [8, 40], [28, 40]];
const center: Pip = [18, 23];
const six: Pip[] = [...corners, [8, 23], [28, 23]];
const offsetEight: Pip[] = [...corners, [8, 17], [28, 17], [8, 29], [28, 29]];
const layouts: Record<number, readonly Pip[]> = {
  1: [center],
  2: [[18, 6], [18, 40]],
  3: [[18, 6], center, [18, 40]],
  4: corners,
  5: [...corners, center],
  6: six,
  7: [...six, [18, 14]],
  8: [...six, [18, 14], [18, 32]],
  9: [...offsetEight, center],
  10: [...offsetEight, [18, 12], [18, 34]],
};

export const suitMark = (suit: Suit) => glyphs[suits.indexOf(suit)];
export const cardName = (rank: number, suit: Suit) => `${high[rank] ?? rank} of ${suit}`;

function pipField(rank: number, suit: Suit): SVGSVGElement | undefined {
  const ace = rank === 1 || rank === 14;
  const layout = layouts[ace ? 1 : rank];
  if (!layout) return;
  const field = svg("svg", { class: "card-pips", viewBox: "0 0 36 46", fill: "currentColor" });
  for (const [x, y] of layout) {
    field.append(svg("path", {
      d: stamps[suit],
      transform: `translate(${x} ${y})${y > 23 ? " rotate(180)" : ""}${ace ? " scale(2.6)" : ""}`,
    }));
  }
  return field;
}

function courtField(rank: number, suit: Suit, label: string): HTMLElement {
  const fallback = el("span", "", label);
  const field = el("span", "card-court", fallback);
  if (rank < 11 || rank > 13) return field;
  const kind = high[rank].toLowerCase();
  const portrait = el("img", "card-portrait");
  portrait.alt = "";
  portrait.loading = "lazy";
  portrait.decoding = "async";
  portrait.addEventListener("load", () => {
    field.dataset.illustrated = "";
    fallback.remove();
  }, { once: true });
  portrait.addEventListener("error", () => portrait.remove(), { once: true });
  portrait.src = new URL(`../assets/bellot/bellot-${kind}-${suit}.svg?no-inline`, import.meta.url).href;
  field.append(portrait);
  return field;
}

export function cardFace(card?: { rank: number; suit: Suit }): HTMLElement {
  const face = el("span", card ? "playing-card" : "playing-card card-back");
  face.setAttribute("role", "img");
  const art = el("span", "playing-card-art");
  art.setAttribute("aria-hidden", "true");
  if (card) {
    const { rank, suit } = card;
    const label = high[rank]?.[0] ?? String(rank);
    const index = el("span", "card-rank", label);
    const mirror = el("span", "card-rank card-rank-mirror", label);
    index.dataset.pip = mirror.dataset.pip = suitMark(suit);
    face.dataset.suit = suit;
    face.setAttribute("aria-label", cardName(rank, suit));
    art.append(index, pipField(rank, suit) ?? courtField(rank, suit, label), mirror);
  } else {
    face.setAttribute("aria-label", "Face-down card");
  }
  face.append(art);
  return face;
}
