import type { GameId } from "./catalog";
import { el, svg } from "./dom";

const profiles: Record<string, [string, string]> = {
  pawn: [
    "M24 5a6.1 6.1 0 0 1 3.8 10.9c.1 3.6 1.5 6.9 5 10.8l-1.5 3.5 3.4 3.6 1.3 6.2H12l1.3-6.2 3.4-3.6-1.5-3.5c3.5-3.9 4.9-7.2 5-10.8A6.1 6.1 0 0 1 24 5Z",
    "M19 27h10m-12 7h14m-8-25a2.8 2.8 0 0 0-2.8 2.8",
  ],
  knight: [
    "M18 8l3.5 3L24 4l4.5 5.7C36 13 39 22.5 34.2 32l3.9 3.5 1.4 4.5h-31l1.7-5.9 5-2.9c.1-4 1.5-7.1 6.2-10l-6.2 2-3.7 3-5-5.6 3-5 8.5-4.2Z",
    "M25.5 12.5c6.7 3 9.2 8.7 7 15.5M11.5 33.8h24m-16-17.1 2.4-1m-10.7 5.8 1.3-.7",
  ],
  bishop: [
    "M24 4c6.5 5 10 9.3 10 14.3 0 3.8-2.3 6.4-6 7.6l3.7 4.5 3.4 3.6 1.7 6H11.2l1.7-6 3.4-3.6 3.7-4.5c-3.7-1.2-6-3.8-6-7.6C14 13.3 17.5 9 24 4Z",
    "M23 17.5l6-8m-11.5 21h13m-16 4.2h19",
  ],
  rook: [
    "M9 8h7v5h4.5V8h7v5H32V8h7v13l-5 5v5.8l3.2 3.1 1.6 5.1H9.2l1.6-5.1 3.2-3.1V26l-5-5Z",
    "M10.3 20.5h27.4m-21.2 5h15m-16.5 7h18m-21.1 3.2h24.2",
  ],
  queen: [
    "M24 3a2.9 2.9 0 0 1 .8 5.7L29 21l5.2-10.2c-1.3-2.9 2.4-5.1 4.7-3.3 1.9 1.5 1.1 4.3-1 4.9l-2.5 13.9-3.5 4.8 4.1 3.5 1.3 5.4H10.7l1.3-5.4 4.1-3.5-3.5-4.8-2.5-13.9c-2.1-.6-2.9-3.4-1-4.9 2.3-1.8 6 .4 4.7 3.3L19 21l4.2-12.3A2.9 2.9 0 0 1 24 3Z",
    "M13.5 26.4h21m-17.8 5.1h14.6m-17.6 4h20.6",
  ],
  king: [
    "M21.7 3h4.6v4.8h4.8v4.6h-4.8v5.2c6.2-3.2 12.5-.8 12.5 4.6 0 4-2.7 7-6.7 8.8l4.1 3.7 1.7 5.3H10.1l1.7-5.3 4.1-3.7c-4-1.8-6.7-4.8-6.7-8.8 0-5.4 6.3-7.8 12.5-4.6v-5.2h-4.8V7.8h4.8Z",
    "M14.8 24c1.9 2.6 5.2 3.1 9.2 3.1s7.3-.5 9.2-3.1M17 31h14m-16.8 4.6h19.6",
  ],
};

export function side(game: GameId, owner: number): string {
  const set = game === "go" || game === "reversi" ? ["onyx", "ivory"] :
    game === "checkers" ? ["cinnabar", "ivory"] :
    game === "chinese-checkers" ? ["jade", "cinnabar", "ivory", "amber", "plum", "teal"] : ["ivory", "onyx"];
  return set[owner] ?? "onyx";
}

export function sideName(game: GameId, owner: number): string {
  if (game === "chinese-checkers") return side(game, owner);
  if (game === "checkers") return owner === 0 ? "red" : "white";
  return (game === "chess" ? owner === 0 : owner === 1) ? "white" : "black";
}

export function token(game: GameId, owner: number, piece: string): HTMLElement {
  const node = el("span", "token");
  node.dataset.side = side(game, owner);
  node.dataset.piece = piece;
  node.setAttribute("aria-hidden", "true");
  if (game === "chess") {
    const shape = svg("svg", { viewBox: "0 0 48 48", class: "chess-form", "aria-hidden": "true" });
    const [silhouette, engraving] = profiles[piece] ?? profiles.pawn;
    shape.append(svg("path", { d: silhouette }), svg("path", { d: engraving, class: "engrave" }));
    node.append(shape);
  } else if (game === "checkers" && piece === "king") {
    const shape = svg("svg", { viewBox: "0 0 24 24", class: "checker-crown", "aria-hidden": "true" });
    shape.append(svg("path", { d: "m4 7 4.5 3.5L12 5l3.5 5.5L20 7l-2 10H6zM7 20h10" }));
    node.append(shape);
  }
  return node;
}

export function tableau(game: GameId): HTMLElement {
  const scene = el("div", "tableau");
  scene.dataset.game = game;
  scene.setAttribute("aria-hidden", "true");
  const field = el("div", "mini-field");
  const pieces = el("div", "mini-pieces");
  switch (game) {
    case "mahjong": {
      const bamboo = svg("svg", { viewBox: "0 0 22 32", fill: "none" });
      bamboo.append(svg("path", { d: "M6 7v18m10-18v18M3 10l3 2 3-2m4 4 3 2 3-2M3 19l3 2 3-2m4 4 3 2 3-2", stroke: "currentColor", "stroke-width": 2 }));
      pieces.append(el("span", "art-tile", bamboo), el("span", "art-tile", el("i", "tile-dot"), el("i", "tile-dot"), el("i", "tile-dot")));
      break;
    }
    case "chess": pieces.append(token(game, 1, "rook"), token(game, 0, "knight"), token(game, 0, "pawn")); break;
    case "go": pieces.append(token(game, 0, "stone"), token(game, 1, "stone"), token(game, 0, "stone")); break;
    case "reversi": pieces.append(token(game, 1, "disc"), token(game, 0, "disc"), token(game, 1, "disc")); break;
    case "checkers": pieces.append(token(game, 0, "man"), token(game, 1, "king")); break;
    case "chinese-checkers": pieces.append(token(game, 3, "peg"), token(game, 0, "peg"), token(game, 1, "peg")); break;
    case "holdem":
    case "solitaire":
      pieces.append(el("span", "art-card", "A♠"), el("span", "art-card red", game === "holdem" ? "A♥" : "2♥"));
      break;
    case "sudoku":
    case "word-tiles":
      pieces.append(...(game === "sudoku" ? ["2", "7", "4"] : ["J", "A", "D", "E"])
        .map(glyph => el("span", "art-block", glyph)));
      break;
  }
  scene.append(field, pieces);
  return scene;
}
