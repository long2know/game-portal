import { el, svg } from "../dom";

export type FlowerCard = { id: number; month: number; kind: "bright" | "animal" | "ribbon" | "plain"; name: string };

const sheet = new URL("./koi-deck.svg?no-inline", import.meta.url).href;
const months = ["January", "February", "March", "April", "May", "June", "July", "August",
  "September", "October", "November", "December"];
const kinds = { bright: "Bright", animal: "Animal", ribbon: "Ribbon", plain: "Plain" } as const;
const seals = { bright: "✦", animal: "◆", ribbon: "⚑", plain: "•" } as const;
const variants = ["", "translate(64 0) scale(-1 1)", "translate(-9 -6) scale(1.23)", "translate(55 -2) scale(-.87 1.05)"];
const features: Partial<Record<number, string>> = {
  0: "crane", 4: "warbler", 8: "curtain", 12: "cuckoo", 16: "bridge", 20: "butterflies",
  24: "boar", 28: "moon", 29: "geese", 32: "cup", 36: "deer", 40: "rain", 41: "swallow",
  43: "lightning", 44: "phoenix",
};

const category = ({ id, month, kind }: FlowerCard) =>
  kind === "plain" && month !== 11 ? `Plain ${id % 4 - Number(month !== 12)}` : kinds[kind];

export function flowerName(card: FlowerCard): string {
  const kind = category(card);
  return `${months[card.month - 1]} · ${card.name}${card.name.endsWith(kind) ? "" : ` · ${kind}`}`;
}

export function flowerFace(card: FlowerCard): HTMLElement {
  const { id, month, kind } = card;
  const mark = svg("svg", { class: "koi-card-art", viewBox: "0 0 64 64", "aria-hidden": "true" });
  const paint = (symbol: string, transform = "") =>
    mark.append(svg("use", { href: `${sheet}#${symbol}`, width: 64, height: 64, transform }));
  const feature = features[id];
  paint(`month-${month}`, kind === "plain" && !feature ? variants[id % 4] : "");
  if (feature) paint(feature);
  if (kind === "ribbon") paint(month < 4 ? "poem" : [6, 9, 10].includes(month) ? "blue-ribbon" : "red-ribbon");
  const face = el("span", "koi-face",
    el("span", "koi-face-top", months[month - 1].slice(0, 3), el("span", "", seals[kind])),
    mark, el("span", "koi-face-kind", category(card)));
  face.dataset.kind = kind;
  face.setAttribute("aria-hidden", "true");
  return face;
}

export function publicFlower(card: FlowerCard): HTMLElement {
  const wrap = el("span", "koi-public-card", flowerFace(card));
  wrap.setAttribute("role", "img");
  wrap.setAttribute("aria-label", flowerName(card));
  wrap.dataset.cardId = String(card.id);
  return wrap;
}
