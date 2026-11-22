import type { GameId } from "./catalog";
import { el, svg } from "./dom";

const chessPieces = new URL("./assets/cburnett.svg?no-inline", import.meta.url).href;
const chessProfiles: Readonly<Record<string, string>> = {
  pawn: "P", knight: "N", bishop: "B", rook: "R", queen: "Q", king: "K",
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
    const shape = svg("svg", { viewBox: "0 0 45 45", class: "chess-form", "aria-hidden": "true" });
    shape.append(svg("use", { href: `${chessPieces}#${owner === 0 ? "w" : "b"}${chessProfiles[piece] ?? "P"}` }));
    node.append(shape);
  } else if (game === "checkers" && piece === "king") {
    const shape = svg("svg", { viewBox: "0 0 24 24", class: "checker-crown", "aria-hidden": "true" });
    shape.append(svg("path", { d: "m4 7 4.5 3.5L12 5l3.5 5.5L20 7l-2 10H6zM7 20h10" }));
    node.append(shape);
  }
  return node;
}

function tiles(): HTMLElement[] {
  const bamboo = svg("svg", { viewBox: "0 0 24 32", fill: "none" });
  bamboo.append(svg("path", {
    d: "M7 5v22m10-22v22M4 10h6m4 0h6M4 16h6m4 0h6M4 22h6m4 0h6",
    stroke: "currentColor", "stroke-width": 2, "stroke-linecap": "round",
  }));
  const dots = svg("svg", { viewBox: "0 0 24 32", fill: "none" });
  for (const [cx, cy] of [[7, 10], [17, 10], [7, 22], [17, 22]]) {
    dots.append(svg("circle", { cx, cy, r: 3.6, stroke: cx === 7 ? "#49654a" : "#984338", "stroke-width": 1.8 }));
  }
  return [el("span", "art-tile", bamboo), el("span", "art-tile", dots), el("span", "art-tile art-tile-back")];
}

function studyDie(pips: readonly number[]): HTMLElement {
  const face = svg("svg", { viewBox: "0 0 24 24", fill: "currentColor" });
  for (const index of pips) face.append(svg("circle", {
    cx: 6 + (index % 3) * 6, cy: 6 + Math.floor(index / 3) * 6, r: 1.8,
  }));
  return el("span", "study-die", face);
}

function backgammonStudy(): SVGSVGElement {
  const board = svg("svg", { viewBox: "0 0 96 64", class: "mini-backgammon" });
  board.append(svg("rect", { x: 1, y: 1, width: 94, height: 62, rx: 2, fill: "#5a4335" }),
    svg("path", { d: "M4 4h42v56H4zM50 4h42v56H50z", fill: "#dec299" }));
  for (let i = 0; i < 12; i++) {
    const x = 4 + i * 7 + (i >= 6 ? 4 : 0);
    board.append(svg("path", { d: `M${x} 4h7l-3.5 23zM${x} 60h7l-3.5-23z`, fill: i % 2 ? "#974e3d" : "#59664e" }));
  }
  board.append(svg("circle", { cx: 14.5, cy: 8, r: 3.5, fill: "#f7edd6", stroke: "#795740" }),
    svg("circle", { cx: 14.5, cy: 15.5, r: 3.5, fill: "#f7edd6", stroke: "#795740" }),
    svg("circle", { cx: 81.5, cy: 55, r: 3.5, fill: "#382d26", stroke: "#dec299" }));
  return board;
}

export function tableau(game: GameId | "farkle" | "backgammon", scale: "index" | "table" = "table"): HTMLElement {
  const scene = el("div", "tableau");
  scene.dataset.game = game;
  scene.dataset.scale = scale;
  scene.setAttribute("aria-hidden", "true");
  const field = el("div", "mini-field");
  const pieces = el("div", "mini-pieces");
  switch (game) {
    case "mahjong": pieces.append(...tiles()); break;
    case "chess":
      if (scale === "table") pieces.append(token(game, 1, "rook"), token(game, 0, "knight"), token(game, 0, "pawn"));
      break;
    case "go": pieces.append(token(game, 0, "stone"), token(game, 1, "stone"), token(game, 0, "stone")); break;
    case "reversi": pieces.append(token(game, 1, "disc"), token(game, 0, "disc")); break;
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
    case "farkle": pieces.append(studyDie([0, 4, 8]), studyDie([0, 2, 4, 6, 8])); break;
    case "backgammon": pieces.append(backgammonStudy()); break;
  }
  scene.append(field, pieces);
  return scene;
}
