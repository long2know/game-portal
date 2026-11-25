import { el, reconcile, text, wind } from "./dom";
import type { Meld, Seat } from "./protocol";
import { tileLabel } from "./tile";

export const meldNames = { chi: "Cha", peng: "Peng", gang: "Kong", bu: "Supplement" } as const;

export function meldKey(meld: Meld, index: number): string {
  return `${index}:${meld.kind}:${meld.method ?? ""}:${meld.fromSeat ?? "_"}:${meld.tiles.join("/")}`;
}

export function meldLabel(meld: Meld, dealer: Seat): string {
  const from = meld.fromSeat === null ? null : wind(meld.fromSeat, dealer);
  const method = meld.method === "added" ? "Added " : meldMethod(meld) === "concealed" ? "Concealed " : "";
  return `${method}${meldNames[meld.kind]}${from ? ` · from ${from}` : ""}`;
}

function meldMethod(meld: Meld): NonNullable<Meld["method"]> {
  return meld.method ?? (meld.fromSeat === null && (meld.kind === "gang" || meld.kind === "bu") ? "concealed" : "open");
}

export function isExposedMeld(meld: Meld): boolean {
  return meldMethod(meld) !== "concealed" && meld.tiles.some(face => face !== null);
}

export function publicMelds(melds: Meld[]): Meld[] {
  return melds.map(meld => meldMethod(meld) === "concealed" && meld.tiles.some(face => face !== null)
    ? { ...meld, tiles: meld.tiles.map(() => null) } : meld);
}

function meldGroup(meld: Meld, local: boolean, index: number): HTMLElement {
  const method = meldMethod(meld);
  const node = el("div", `meld-group meld-${method}`);
  const label = el("span", "meld-caption");
  const tiles = el("div", "meld-tiles");
  node.setAttribute("role", "group");
  node.append(tiles, label);
  meld.tiles.forEach((face, i) => {
    const tile = tileLabel(face, "meld-tile");
    if (local) tile.dataset.meldTile = `${index}-${i}`;
    if (i === 2 && meld.fromSeat !== null && meld.kind !== "chi") tile.classList.add("sourced");
    if (i === 3 && method === "added") tile.classList.add("stacked");
    tiles.append(tile);
  });
  return node;
}

export function setMelds(parent: HTMLElement, melds: Meld[], dealer: Seat, local = false): void {
  reconcile(parent, melds, meldKey, (meld, index) => meldGroup(meld, local, index), (node, meld) => {
    const label = meldLabel(meld, dealer);
    node.setAttribute("aria-label", label);
    text(node.lastElementChild!, label);
  });
}
