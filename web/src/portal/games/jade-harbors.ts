import "./jade-harbors.css";
import { button, el, svg, text } from "../dom";
import type { PortalHost } from "../host";
import { Multiplayer, nameAt } from "./multiplayer";
import type { MatchView, MultiplayerTable } from "./multiplayer";

type Phase = "placePier" | "placeRoute" | "roll" | "build" | "finished";
type Point = { x: number; y: number };
type Tile = Point & { id: number; resource: number | null; number: number | null; nodeIds: number[] };
type Site = Point & { id: number; seat: number | null; level: number };
type Route = { id: number; a: number; b: number; seat: number | null };
type Trade = { give: number; take: number; cost: number };
export type HarborsPosition = {
  phase: Phase;
  turnSeat: number | null;
  turn: number;
  winnerSeat: number | null;
  lastRoll: number[] | null;
  tiles: Tile[];
  nodes: Site[];
  routes: Route[];
  seats: { seat: number; points: number; resourceCount: number }[];
  ownResources: number[];
  legalActions: { canRoll: boolean; canEndTurn: boolean; piers: number[]; routes: number[]; upgrades: number[]; trades: Trade[] };
};
type BuildKind = "buildPier" | "buildRoute" | "upgrade";
type Build = { kind: BuildKind; target: number };
type Exchange = { kind: "trade"; give: number; take: number };
export type HarborsAction = Build | Exchange | { kind: "roll" } | { kind: "endTurn" };
type Mode = BuildKind | "trade";
type Choice = { action: Build | Exchange; label: string; detail: string };

const goods = [
  { name: "Timber", mark: "M3 15 13 5l7 3v5L10 23l-7-3ZM3 15l7 3 10-10M10 18v5m4-13 3-3M5 18l2 1" },
  { name: "Clay", mark: "M2 12h10v7H2Zm10 0h10v7H12ZM7 5h10v7H7Zm2 3h4m2 7h3m-13 0h3" },
  { name: "Grain", mark: "M12 22V4m0 7Q5 9 5 5q5 0 7 6Zm0 5q7-2 7-6-5 0-7 6Zm0 4q-7-1-8-6 6 1 8 6Z" },
  { name: "Stone", mark: "M2 17l5-8 7 2 3 9H5Zm11-7 3-5 5 3 1 9-5 3M7 9l2 7 7 3" },
];
const basin = { name: "Basin", mark: "M3 14h18l-3 5H7Zm6 0V5l7 6H9M2 22q4-3 9 0t10 0" };
const terrain = (index: number | null) => index === null ? basin : goods[index];
const tileName = ({ resource, number }: Tile) => `${terrain(resource).name}${number === null ? "" : ` ${number}`}`;
const modes: { kind: Mode; label: string; confirm: string; prompt: string }[] = [
  { kind: "buildPier", label: "Pier", confirm: "Build pier", prompt: "Choose a site" },
  { kind: "buildRoute", label: "Route", confirm: "Build route", prompt: "Choose a route" },
  { kind: "upgrade", label: "Warehouse", confirm: "Raise warehouse", prompt: "Choose a pier to upgrade" },
  { kind: "trade", label: "Market", confirm: "Make trade", prompt: "Choose an exchange" },
];
const costs: Record<BuildKind, string> = {
  buildPier: "Timber + Clay + Grain", buildRoute: "Timber + Clay", upgrade: "2 Stone + Grain",
};
const phases: Record<Exclude<Phase, "finished">, string> = {
  placePier: "place a free pier", placeRoute: "connect a free route", roll: "roll the dice", build: "build, trade, or end turn",
};

function ink(mark: string) {
  return svg("path", { class: "jh-ink", d: mark });
}

function seal(mark: string) {
  const icon = svg("svg", { viewBox: "0 0 24 24", "aria-hidden": "true" });
  icon.append(ink(mark));
  return icon;
}

const scale = 44;
const point = ({ x, y }: Point): Point => ({ x: x * scale * Math.sqrt(3) / 2, y: y * scale / 2 });
const mid = (a: Point, b: Point) => ({ x: (a.x + b.x) / 2, y: (a.y + b.y) / 2 });
const shift = ({ x, y }: Point) => `translate(${x} ${y})`;
const edge = (a: Point, b: Point, kind: string) => svg("line", { class: kind, x1: a.x, y1: a.y, x2: b.x, y2: b.y });
const choiceId = ({ action }: Choice) => action.kind === "trade" ? `${action.give}:${action.take}` : String(action.target);
const ownerAttrs = (seat: number | null): Record<string, string> => seat === null ? {} : { "data-owner": String(seat) };
const hasChoices = (legal: HarborsPosition["legalActions"], kind: Mode) => ({
  buildPier: legal.piers, buildRoute: legal.routes, upgrade: legal.upgrades, trade: legal.trades,
})[kind].length > 0;

function chart(select: (id: string) => void) {
  const root = svg("svg", { class: "jh-chart", "aria-hidden": "true" });
  const current = svg("pattern", { id: "jh-canal", width: 83, height: 39, patternUnits: "userSpaceOnUse" });
  current.append(svg("path", { class: "jh-waves", d: "M-9 13Q3 7 15 12t27 0m20 19 14-4 9 1M9 35q11-3 24 0m12-30 12 1" }));
  const defs = svg("defs", {});
  defs.append(current);
  const board = svg("g", {});
  const marks = svg("g", {});
  root.append(defs, board, marks);
  let sites = new Map<number, Point>();
  let routes = new Map<number, Point>();

  function draw(position: HarborsPosition) {
    sites = new Map(position.nodes.map(node => [node.id, point(node)]));
    routes = new Map();
    const xs = [...sites.values()].map(({ x }) => x);
    const ys = [...sites.values()].map(({ y }) => y);
    const rim = 23;
    const x = Math.min(...xs) - rim;
    const y = Math.min(...ys) - rim;
    const width = Math.max(...xs) + rim - x;
    const height = Math.max(...ys) + rim - y;
    root.setAttribute("viewBox", `${x} ${y} ${width} ${height}`);
    root.setAttribute("width", String(Math.ceil(width)));
    root.setAttribute("height", String(Math.ceil(height)));
    const bounds = { x, y, width, height };
    const water = svg("rect", { ...bounds, class: "jh-water" });
    const currents = svg("rect", { ...bounds, fill: "url(#jh-canal)" });
    const tiles = position.tiles.map(tile => {
      const { x, y } = point(tile);
      const surface = svg("g", { transform: shift({ x, y }), "data-tile": tile.id, "data-resource": tile.resource ?? "basin" });
      const corners = tile.nodeIds.map(id => sites.get(id)).filter(node => node !== undefined)
        .map(node => `${(node.x - x) * .9},${(node.y - y) * .9}`).join(" ");
      const glyph = svg("g", { transform: "translate(-12 -28)" });
      glyph.append(ink(terrain(tile.resource).mark));
      const label = svg("text", { class: "jh-terrain-name", x: 0, y: 8 });
      label.textContent = terrain(tile.resource).name;
      surface.append(
        svg("polygon", { class: "jh-terrain", points: corners }),
        svg("path", { class: "jh-wash", d: "M-29 12q14 6 24 0t32 0m-53 4 9 2m25 11 11-2m-42-42 9-6m29 4 8 3" }),
        glyph, label);
      if (tile.number !== null) {
        const numeral = svg("text", { class: "jh-terrain-number", x: 0, y: 24 });
        numeral.textContent = String(tile.number);
        surface.append(svg("rect", { class: "jh-number-slip", x: -11, y: 11, width: 22, height: 17, rx: 5 }), numeral);
      }
      return surface;
    });
    const connections = position.routes.flatMap(route => {
      const a = sites.get(route.a);
      const b = sites.get(route.b);
      if (!a || !b) return [];
      routes.set(route.id, mid(a, b));
      const channel = svg("g", { class: "jh-link", "data-route": route.id, ...ownerAttrs(route.seat) });
      channel.append(edge(a, b, "jh-link-bed"), edge(a, b, "jh-link-ink"));
      return [channel];
    });
    const pieces = position.nodes.map(site => {
      const node = svg("g", {
        class: "jh-site", transform: shift(point(site)), "data-site": site.id, "data-level": site.level, ...ownerAttrs(site.seat),
      });
      if (site.seat === null) node.append(svg("circle", { r: 3 }));
      else {
        const silhouette = site.level === 2 ? "M-11 0 0-11 11 0V9H-11Z" : "M-12 8v-9h5v-6H7v6h5v9Z";
        const flag = svg("text", { class: "jh-piece-flag", y: 6 });
        flag.textContent = String(site.seat + 1);
        node.append(svg("path", { d: silhouette }), flag);
      }
      return node;
    });
    board.replaceChildren(water, currents, ...tiles, ...connections, ...pieces);
  }

  function showTargets(choices: Choice[], picked: string, active: boolean) {
    marks.replaceChildren(...choices.flatMap(choice => {
      const { action } = choice;
      if (action.kind === "trade") return [];
      const at = (action.kind === "buildRoute" ? routes : sites).get(action.target);
      if (!at) return [];
      const id = choiceId(choice);
      const mark = svg("g", { class: "jh-target", transform: shift(at), "data-target": id, "data-kind": action.kind });
      mark.toggleAttribute("data-picked", id === picked);
      mark.toggleAttribute("data-active", active);
      const label = svg("text", { y: 4 });
      label.textContent = id;
      mark.append(svg("circle", { class: "jh-tap", r: 15 }), svg("ellipse", { rx: Math.max(10, id.length * 4.4), ry: 9 }), label);
      mark.addEventListener("click", () => select(id));
      return [mark];
    }));
  }
  return { root, draw, showTargets };
}

function choicesFor(position: HarborsPosition, mode: Mode): Choice[] {
  const legal = position.legalActions;
  if (mode === "trade") {
    return legal.trades.map(({ give, take, cost }) => ({
      action: { kind: "trade", give, take },
      label: `${cost} ${goods[give].name} → 1 ${goods[take].name}`,
      detail: `Give ${cost} ${goods[give].name} for 1 ${goods[take].name}.`,
    }));
  }
  if (mode === "buildRoute") {
    return position.routes.filter(route => legal.routes.includes(route.id)).map(({ id, a, b }) => ({
      action: { kind: mode, target: id }, label: `Route ${id} · Sites ${a}–${b}`, detail: `Connect sites ${a} and ${b}.`,
    }));
  }
  const ids = mode === "buildPier" ? legal.piers : legal.upgrades;
  return position.nodes.filter(node => ids.includes(node.id)).map(({ id }) => {
    const nearby = position.tiles.filter(tile => tile.nodeIds.includes(id)).map(tileName).join(", ");
    return { action: { kind: mode, target: id }, label: `Site ${id} · ${nearby}`, detail: `Site ${id} borders ${nearby}.` };
  });
}

function guide() {
  return el("details", "jh-guide", el("summary", "", "How to play Jade Harbors"),
    el("ol", "",
      el("li", "", el("strong", "", "Set up in snake order. "), "Seats 1 → 2 → 3 → 3 → 2 → 1 each place a free pier, then a free route from it. Your second starting pier collects supplies from its neighboring land once."),
      el("li", "", el("strong", "", "Roll for supplies. "), "Add the two dice. Land with that number pays each bordering pier 1 of its supply, or 2 for a warehouse. The basin pays nothing. The last roll stays on the table until someone rolls again."),
      el("li", "", el("strong", "", "Build your canal network. "), `Route: ${costs.buildRoute}. Pier: ${costs.buildPier}. Warehouse upgrade: ${costs.upgrade}. Connect to your own network. Leave at least one open site between buildings. A route can end at another seat’s building but cannot continue through it. Pick a build, choose a marked site or route on the map or in the list, then confirm.`),
      el("li", "", el("strong", "", "Use the market or pass the turn. "), "Make at most one market exchange per turn: 3 of one supply for 1 of another. A roll of 7 pays no supplies and lowers the price to 2 for 1 that turn. Your supplies are private; only the total is public."),
      el("li", "", el("strong", "", "Score with piers and warehouses. "), "A pier scores 1 point, a warehouse scores 2, and routes do not score. Reach 8 points to win. After 120 turns, the highest score wins; equal top scores tie. Points have no cash or redemption value.")));
}

function view(send: (action: HarborsAction) => void): MatchView<HarborsPosition> {
  let position: HarborsPosition | undefined;
  let active = false;
  let stamp = "";
  let mode: Mode | undefined;
  let choices: Choice[] = [];

  const turn = el("p", "jh-kicker");
  const status = el("p", "jh-phase");
  status.tabIndex = -1;
  status.setAttribute("role", "status");
  const diceTitle = el("span", "jh-kicker");
  const first = el("span", "jh-die");
  const second = el("span", "jh-die");
  const total = el("strong", "jh-total");
  const faces = el("div", "jh-faces", first, second, total);
  const dice = el("div", "jh-roll", diceTitle, faces);
  dice.setAttribute("role", "group");
  diceTitle.setAttribute("aria-hidden", "true");
  faces.setAttribute("aria-hidden", "true");
  const scores = el("ul", "jh-seats");
  scores.setAttribute("role", "list");
  scores.setAttribute("aria-label", "Seats, points and supply totals");
  const hand = el("ul", "jh-hand");
  hand.setAttribute("role", "list");
  hand.setAttribute("aria-label", "Your supplies");
  const map = chart(id => {
    if (!active || !choices.some(choice => choiceId(choice) === id)) return;
    picker.value = id;
    paintSelection();
  });
  const lands = el("ul");
  const pieces = el("ul");
  const ledger = el("details", "jh-ledger", el("summary", "", "Read the map"),
    el("h3", "", "Terrain, roll numbers and bordering sites"), lands,
    el("h3", "", "Built piers, warehouses and routes"), pieces);
  const mapPanel = el("div", "jh-map-panel",
    el("figure", "jh-paper", el("figcaption", "", el("h2", "", "Canal chart"),
      el("p", "jh-map-caption", "Tile numbers are rolls. Outlined marks match the build list.")), map.root), ledger);

  const controls = modes.map(({ kind, label }) => ({ kind, control: button(label, "button secondary", () => {
    if (!active || !position || !hasChoices(position.legalActions, kind)) return;
    setMode(kind === mode ? undefined : kind);
    paintActions();
  }) }));
  const modeBar = el("div", "jh-modes", ...controls.map(({ control }) => control));
  modeBar.setAttribute("role", "group");
  modeBar.setAttribute("aria-label", "Build and market choices");
  const pickerLabel = el("span");
  const picker = el("select");
  const detail = el("p", "jh-detail");
  detail.setAttribute("role", "status");
  const confirm = button("", "button primary", () => {
    const choice = choices.find(item => choiceId(item) === picker.value);
    if (active && choice) commit(choice.action);
  });
  const choose = el("div", "jh-choose", el("label", "", pickerLabel, picker), detail, confirm);
  picker.addEventListener("change", paintSelection);
  const roll = button("Roll dice", "button primary", () => {
    if (active && position?.legalActions.canRoll) commit({ kind: "roll" });
  });
  const end = button("End turn", "button secondary", () => {
    if (active && position?.legalActions.canEndTurn) commit({ kind: "endTurn" });
  });
  const commands = el("div", "jh-commands", roll, end);
  const market = el("p", "jh-market", "Market rate this turn: 2 for 1.");
  const actions = el("div", "jh-actions", el("h2", "", "Table actions"), market, modeBar, choose, commands);
  const rail = el("div", "jh-console", actions, el("section", "jh-stores", el("h2", "", "Your supplies"), hand));
  const root = el("section", "jh-table",
    el("header", "jh-table-head", el("div", "", turn, status), dice),
    scores, el("div", "jh-board", mapPanel, rail));
  root.setAttribute("aria-label", "Jade Harbors playing area");

  function setupMode(): BuildKind | undefined {
    return position?.phase === "placePier" ? "buildPier" : position?.phase === "placeRoute" ? "buildRoute" : undefined;
  }

  function setMode(next?: Mode) {
    mode = next;
    choices = position && mode ? choicesFor(position, mode) : [];
    picker.replaceChildren(new Option("Select an option…", ""), ...choices.map(choice => new Option(choice.label, choiceId(choice))));
    const option = modes.find(item => item.kind === mode);
    if (option) {
      text(pickerLabel, option.prompt);
      text(confirm, option.confirm);
    }
  }

  function paintSelection() {
    const choice = choices.find(item => choiceId(item) === picker.value);
    const note = mode && mode !== "trade" ? setupMode() ? "Free during setup." : `Cost: ${costs[mode]}.` : "";
    text(detail, [choice?.detail, note].filter(Boolean).join(" "));
    confirm.setAttribute("aria-disabled", String(!active || !choice));
    map.showTargets(choices, picker.value, active);
  }

  function commit(action: HarborsAction) {
    active = false;
    picker.value = "";
    send(action);
  }

  actions.addEventListener("keydown", event => {
    if (event.key !== "Escape" || !active || !mode) return;
    if (!picker.value && setupMode()) return;
    event.preventDefault();
    if (picker.value) {
      picker.value = "";
      picker.focus();
    } else {
      const previous = controls.find(({ kind }) => kind === mode);
      setMode();
      choose.hidden = true;
      previous?.control.focus();
    }
    paintActions();
  });

  function paintActions() {
    if (!position) return;
    const { phase, legalActions: legal, lastRoll } = position;
    for (const { kind, control } of controls) {
      control.hidden = phase !== "build" || !hasChoices(legal, kind);
      control.setAttribute("aria-pressed", String(mode === kind));
      control.setAttribute("aria-disabled", String(!active));
    }
    modeBar.hidden = controls.every(({ control }) => control.hidden);
    choose.hidden = !choices.length;
    picker.disabled = !active;
    roll.hidden = !legal.canRoll;
    end.hidden = !legal.canEndTurn;
    roll.setAttribute("aria-disabled", String(!active));
    end.setAttribute("aria-disabled", String(!active));
    commands.hidden = roll.hidden && end.hidden;
    market.hidden = !(phase === "build" && position.turnSeat !== null && lastRoll?.length === 2 && lastRoll[0] + lastRoll[1] === 7);
    actions.hidden = modeBar.hidden && choose.hidden && commands.hidden && market.hidden;
    paintSelection();
  }

  return {
    root,
    show(table, enabled) {
      const current = table.position;
      if (!current) return;
      position = current;
      const { phase, turnSeat, winnerSeat, lastRoll, ownResources } = current;
      active = enabled && turnSeat === table.seat && phase !== "finished";
      const who = (seat: number) => nameAt(table, seat);
      const next = `${table.id}/${table.revision}`;
      const changed = stamp !== next;
      const focused = document.activeElement;
      if (changed) {
        stamp = next;
        root.dataset.phase = phase;
        text(turn, position.turn === 0 ? "Starting placements" : `Turn ${position.turn} / 120`);
        text(status, phase === "finished" ? winnerSeat === null ? "Match ends in a tie." :
          winnerSeat === table.seat ? "You won!" : `${who(winnerSeat)} wins.` :
          turnSeat === null ? "Waiting for the table." :
            `${turnSeat === table.seat ? "Your turn" : `${who(turnSeat)}’s turn`} · ${phases[phase]}`);
        const [a, b] = lastRoll ?? [];
        faces.hidden = a === undefined || b === undefined;
        text(diceTitle, faces.hidden ? "No roll yet" : "Last roll");
        if (a !== undefined && b !== undefined) {
          text(first, String(a));
          text(second, String(b));
          text(total, `= ${a + b}`);
        }
        dice.setAttribute("aria-label", a === undefined || b === undefined ? "No roll yet" : `Last roll: ${a} plus ${b} equals ${a + b}`);
        scores.replaceChildren(...position.seats.map(({ seat, points, resourceCount }) => {
          const label = table.seats.find(item => item.seat === seat)?.bot ? `${who(seat)} · Bot` : who(seat);
          const flag = el("span", "jh-flag", String(seat + 1));
          flag.setAttribute("role", "img");
          flag.setAttribute("aria-label", `Seat ${seat + 1}`);
          const card = el("li", "", el("span", "jh-seat-name", flag, el("strong", "", label)),
            el("b", "jh-points", `${points} ${points === 1 ? "point" : "points"}`),
            el("small", "", `${resourceCount} ${resourceCount === 1 ? "supply" : "supplies"}`),
            ...(seat === turnSeat ? [el("span", "jh-seat-turn", "Turn")] : []));
          card.dataset.owner = String(seat);
          card.toggleAttribute("data-acting", seat === turnSeat);
          return card;
        }));
        hand.replaceChildren(...goods.map(({ name, mark }, index) => {
          const swatch = el("li", "", seal(mark), el("span", "", name), el("strong", "", String(ownResources[index])));
          swatch.dataset.resource = String(index);
          return swatch;
        }));
        map.draw(position);
        lands.replaceChildren(...position.tiles.map(tile =>
          el("li", "", `${tileName(tile)}${tile.number === null ? " (no yield)" : ""} · Sites ${tile.nodeIds.join(", ")}`)));
        const built = [
          ...position.nodes.flatMap(({ id, seat, level }) => seat === null ? [] :
            [el("li", "", `Site ${id} · Seat ${seat + 1} (${who(seat)}) · ${level === 2 ? "Warehouse" : "Pier"}`)]),
          ...position.routes.flatMap(({ id, a, b, seat }) => seat === null ? [] :
            [el("li", "", `Route ${id}, sites ${a}–${b} · Seat ${seat + 1} (${who(seat)})`)]),
        ];
        pieces.replaceChildren(...(built.length ? built : [el("li", "", "Nothing built yet.")]));
        setMode(setupMode());
      }
      paintActions();
      if (changed && focused instanceof HTMLElement && actions.contains(focused) &&
          (focused.closest("[hidden]") || focused === confirm || focused === picker && !active)) {
        const nextControl = active ? [picker, roll, ...controls.map(({ control }) => control), end]
          .find(control => !control.closest("[hidden]")) : undefined;
        (nextControl ?? status).focus({ preventScroll: true });
      }
    },
  };
}

export function mount(host: PortalHost): void {
  new Multiplayer(host, { game: "jade-harbors", endpoint: "jade-harbors", playerCounts: [3], view, guide: guide() });
}
