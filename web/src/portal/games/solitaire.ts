import "./solitaire.css";
import { button, el, text } from "../dom";
import type { PortalHost } from "../host";
import { cardFace, cardName, suitMark, suits } from "./cards";
import type { Suit } from "./cards";
import { solo } from "./solo";
import type { SoloView } from "./solo";

type Card = { rank: number | null; suit: Suit | null; faceUp: boolean };
type Move = { from: string; to: string; count: number };
type Position = {
  finished: boolean; moves: number; stockCount: number; wasteCount: number;
  waste: Card | null; foundations: (Card | null)[]; tableau: Card[][]; legalMoves: Move[];
};
type Action = { kind: "draw" } | ({ kind: "move" } & Move);

function view(play: (action: Action) => void): SoloView<Position> {
  let position: Position | undefined;
  let enabled = false;
  let selected: Pick<Move, "from" | "count"> | undefined;
  let controls = new Map<string, HTMLButtonElement>();
  const board = el("div", "solitaire-board");
  board.setAttribute("role", "group");
  board.setAttribute("aria-label", "Solitaire board");
  const instruction = el("p", "feature-hint",
    "Choose a face-up card to move it and the cards below it. Then choose a marked pile. Swipe or scroll the board for all seven columns.");
  const hint = el("p", "solitaire-hint");
  hint.setAttribute("role", "status");
  const cancel = button("Clear selection", "button secondary", () => { selected = undefined; render(); });
  cancel.hidden = true;
  const root = el("section", "feature-panel",
    instruction, el("div", "feature-controls", hint, cancel), el("div", "feature-scroll", board));

  function choose(id: string, count: number, top: boolean): void {
    if (!enabled) return;
    const target = top && position?.legalMoves.find(move => move.from === selected?.from && move.count === selected.count && move.to === id);
    if (target) { play({ kind: "move", ...target }); return; }
    if (!position?.legalMoves.some(move => move.from === id && move.count === count)) return;
    selected = selected?.from === id && selected.count === count ? undefined : { from: id, count };
    render();
  }

  function figure(card: Card | null, id: string, count: number, top: boolean, empty: string, emptyMark = "◇") {
    if (card && !card.faceUp) return el("div", "solitaire-under", cardFace());
    const control = button("", "solitaire-card", () => choose(id, count, top));
    const key = `${id}/${count}`;
    control.dataset.control = key;
    controls.set(key, control);
    const face = card?.rank != null && card.suit ? cardFace({ rank: card.rank, suit: card.suit }) :
      el("span", "solitaire-slot", emptyMark);
    face.setAttribute("aria-hidden", "true");
    const description = card?.rank != null && card.suit ? `${cardName(card.rank, card.suit)}${count > 1 ? `, stack of ${count}` : ""}` : "empty";
    control.setAttribute("aria-label", `${empty}: ${description}`);
    const moves = position!.legalMoves;
    const source = moves.some(move => move.from === id && move.count === count);
    const target = top && moves.some(move => move.to === id && move.from === selected?.from && move.count === selected.count);
    if (source) control.setAttribute("aria-pressed", String(selected?.from === id && selected.count === count));
    control.toggleAttribute("data-target", target && enabled);
    control.setAttribute("aria-disabled", String(!enabled || (!source && !target)));
    control.append(face);
    return control;
  }

  function render(): void {
    if (!position) return;
    const { stockCount, wasteCount, waste, foundations, tableau } = position;
    const focus = document.activeElement instanceof HTMLElement ? document.activeElement.dataset.control : undefined;
    controls = new Map();
    const drawLabel = stockCount ? `Draw card, ${stockCount} in stock` : wasteCount ? `Recycle waste, ${wasteCount} cards` : "Stock empty";
    const stock = button("", "solitaire-card stock", () => {
      if (enabled && (stockCount || wasteCount)) play({ kind: "draw" });
    });
    stock.dataset.control = "stock";
    controls.set("stock", stock);
    stock.setAttribute("aria-label", drawLabel);
    stock.setAttribute("aria-disabled", String(!enabled || !(stockCount || wasteCount)));
    const stockFace = stockCount ? cardFace() : el("span", "solitaire-slot", wasteCount ? "↻" : "◇");
    stockFace.setAttribute("aria-hidden", "true");
    stock.append(stockFace);
    const pile = (caption: string, ...cards: HTMLElement[]) => el("div", "solitaire-pile", el("span", "pile-title", caption), ...cards);
    const top = el("div", "solitaire-row",
      pile(stockCount ? `Stock · ${stockCount}` : wasteCount ? "Recycle" : "Stock", stock),
      pile("Waste", figure(waste, "waste", 1, true, "Waste")),
      el("div"),
      ...foundations.map((card, index) => {
        const suit = suits[index];
        return pile(suitMark(suit), figure(card, `f${index}`, 1, true, `${suit} foundation`, suitMark(suit)));
      }));
    const columns = el("div", "solitaire-row solitaire-columns", ...tableau.map((stack, index) => {
      const name = `Column ${index + 1}`;
      const cards = stack.length ? stack.map((card, depth) =>
        figure(card, `t${index}`, stack.length - depth, depth === stack.length - 1, name)) :
        [figure(null, `t${index}`, 1, true, name)];
      return pile(String(index + 1), ...cards);
    }));
    board.replaceChildren(top, columns);
    const current = focus && (controls.get(focus) ?? controls.get(`${focus.split("/")[0]}/1`) ?? stock);
    if (current) current.focus({ preventScroll: true });
    if (!selected && document.activeElement === cancel) stock.focus();
    cancel.hidden = !selected;
    cancel.disabled = !enabled;
    text(hint, selected ? `Selected ${selected.count === 1 ? "one card" : `${selected.count} cards`}. Choose a marked destination.` :
      "Draw from stock, or move a face-up card.");
  }

  board.addEventListener("keydown", event => {
    if (event.key === "Escape" && selected) { selected = undefined; render(); }
  });
  return {
    root,
    show(next, permitted, newGame) {
      if (next !== position || newGame) selected = undefined;
      position = next;
      enabled = permitted;
      render();
    },
  };
}

export function mount(host: PortalHost): void {
  solo(host, { game: "solitaire", variants: { "draw-one": "Draw one" }, defaultVariant: "draw-one", view });
}
