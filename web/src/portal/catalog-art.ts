import type { GameId } from "./catalog";
import { el, svg } from "./dom";

const sheet = new URL("./catalog-marks.svg?no-inline", import.meta.url).href;

export function dailySeal(): SVGSVGElement {
  const stamp = svg("svg", { class: "daily-stamp", viewBox: "0 0 72 60", "aria-hidden": "true" });
  stamp.append(svg("use", { href: `${sheet}#daily-court` }));
  return stamp;
}

export function catalogPreview(game: GameId, scale: "index" | "table" = "index"): HTMLElement {
  const mark = svg("svg", {
    viewBox: "0 0 72 60", class: "catalog-mark",
    preserveAspectRatio: game === "mahjong" || scale === "table" ? "xMidYMid meet" : "xMidYMid slice",
  });
  mark.append(svg("use", { href: `${sheet}#${game}`, width: 72, height: 60 }));
  const scene = el("div", "tableau", mark);
  scene.dataset.game = game;
  scene.dataset.scale = scale;
  scene.setAttribute("aria-hidden", "true");
  return scene;
}
