import "./windward-ledger.css";
import { button, el, svg, text } from "../dom";
import type { PortalHost } from "../host";
import { solo, type SoloView } from "./solo";

type Action = { kind: "sail"; index: number } | { kind: "assign" | "load"; value: number }
  | { kind: "found" | "deliver" | "end-season" };
type Tile = { kind: "water" | "land" | "home" | "coast"; name: string | null; specialty: number | null };
type Port = { index: number; production: number | null; stock: number[] };
type Good = { name: string; points: number; cargo: number; delivered: number };
type Position = {
  finished: boolean; moves: number; status: "active" | "won" | "lost";
  width: number; height: number; season: number; seasonLimit: number; shipIndex: number;
  sailRemaining: number; ordersRemaining: number; cargoCapacity: number; portLimit: number; stockLimit: number;
  charterPoints: number; targetPoints: number;
  chart: (Tile | null)[]; goods: Good[]; ports: Port[]; legalActions: Action[];
};

const ensigns = {
  water: "M3 9q3-3 6 0t6 0 6 0M3 15q3-3 6 0t6 0 6 0",
  land: "M2 19l7-13 5 7 3-4 5 10zM7 10l2 2 2-2",
  coast: "M4 20V4m1 1h14l-4 4 4 4H5m2 7h15",
  home: "M2 11l10-7 10 7M5 10v10h14V10m-9 10v-6h4v6",
  port: "M12 3v17M8 7h8M5 13c-2 4 3 7 7 7s9-3 7-7m0 0-3 2m3-2 2 2M5 13l3 2m-3-2-2 2",
  fog: "M9 8a3.5 3.5 0 1 1 5 3.2c-2 1-2 1.8-2 3.3m0 3.5v.1",
  cutter: "M4 17h17l-4 4H8zM13 14V3L5 14zm3-7 4 7h-4",
};
type Glyph = keyof typeof ensigns;

function mark(kind: Glyph): SVGSVGElement {
  const icon = svg("svg", { viewBox: "0 0 24 24", "aria-hidden": "true", class: "windward-mark" });
  icon.append(svg("path", { d: ensigns[kind] }));
  return icon;
}

function coordinate(index: number, width: number): string {
  return `${String.fromCharCode(65 + index % width)}${Math.floor(index / width) + 1}`;
}

function describe(position: Position, index: number): string {
  const tile = position.chart[index];
  if (!tile) return "Uncharted";
  if (!tile.name) return tile.kind === "land" ? "Land" : "Open water";
  if (tile.kind === "home") return `${tile.name} · home dock`;
  return `${tile.name} · ${position.ports.some(port => port.index === index) ? "port" : "coast"}`;
}

function drawChart(chart: HTMLTableElement, position: Position): void {
  const { width, height, shipIndex } = position;
  chart.replaceChildren(el("caption", "", "Soundings chart"),
    el("thead", "", el("tr", "", el("td"), ...Array.from({ length: width }, (_, index) => {
      const column = el("th", "", String.fromCharCode(65 + index));
      column.scope = "col";
      return column;
    }))),
    el("tbody", "", ...Array.from({ length: height }, (_, row) => {
      const heading = el("th", "", String(row + 1));
      heading.scope = "row";
      return el("tr", "", heading, ...Array.from({ length: width }, (_, column) => {
        const index = row * width + column;
        const kind = position.ports.some(port => port.index === index) ? "port" : position.chart[index]?.kind ?? "fog";
        const label = `${coordinate(index, width)} · ${describe(position, index)}${index === shipIndex ? " · Cutter here" : ""}`;
        const cell = el("td", "", el("span", "windward-sr", label), mark(kind));
        cell.dataset.kind = kind;
        if (index === shipIndex) {
          cell.dataset.cutter = "";
          const icon = mark("cutter");
          icon.classList.add("windward-cutter");
          cell.append(icon);
        }
        return cell;
      }));
    })));
}

const headings = [
  ["North", 0, -1], ["East", 1, 0], ["South", 0, 1], ["West", -1, 0],
] as const;

function voyage(play: (action: Action) => void, cues: HTMLButtonElement): SoloView<Position> {
  let position: Position | undefined;
  let enabled = false;
  const allowed = (action: Action) => enabled && position?.legalActions.some(candidate =>
    candidate.kind === action.kind &&
    (!("index" in action) || ("index" in candidate && candidate.index === action.index)) &&
    (!("value" in action) || ("value" in candidate && candidate.value === action.value)));
  const send = (action: Action) => { if (allowed(action)) play(action); };
  const availability = (control: HTMLButtonElement, action?: Action) =>
    control.setAttribute("aria-disabled", String(!action || !allowed(action)));

  const chart = el("table", "windward-chart");
  const legend = el("div", "windward-legend", ...([
    ["cutter", "Cutter"], ["home", "Home"], ["coast", "Coast"], ["port", "Port"],
    ["water", "Water"], ["land", "Land"], ["fog", "Uncharted"],
  ] as const).map(([kind, label]) => el("span", "", mark(kind), label)));
  const season = el("strong", "windward-season");
  const score = el("strong", "windward-score");
  const tide = el("p", "windward-tide");
  const soundings = el("p", "windward-note");
  const location = el("p", "windward-location");
  location.setAttribute("role", "status");
  const compass = headings.map(([direction, dx, dy]) => {
    let target: number | undefined;
    const control = button(`Sail ${direction.toLowerCase()}`, "button secondary", () => {
      if (target !== undefined) send({ kind: "sail", index: target });
    });
    return {
      control,
      show(next: Position) {
        const x = next.shipIndex % next.width + dx;
        const y = Math.floor(next.shipIndex / next.width) + dy;
        target = x >= 0 && x < next.width && y >= 0 && y < next.height ? y * next.width + x : undefined;
        const destination = target === undefined ? "" : ` · ${coordinate(target, next.width)}`;
        text(control, `Sail ${direction.toLowerCase()}${destination}`);
        availability(control, target === undefined ? undefined : { kind: "sail", index: target });
      },
    };
  });
  const end = button("End season", "button primary", () => send({ kind: "end-season" }));
  const found = button("Establish port", "button secondary", () => send({ kind: "found" }));
  const deliver = button("Deliver cargo", "button primary", () => send({ kind: "deliver" }));
  const berth = el("p", "windward-note");
  const charter = el("p", "windward-note");
  const hold = el("h2", "", "Hold & charter");
  const manifests = el("dl", "windward-manifest");
  const orderButtons = el("div", "windward-orders");
  const warehouses = el("ul", "windward-ports");
  const portsHeading = el("h2", "", "Ports");
  const orders = el("section", "windward-sheet", hold, charter, manifests,
    el("div", "windward-berth", berth, found, deliver, orderButtons), portsHeading, warehouses);
  const instructions = el("details", "windward-rules", el("summary", "", "Read the charter"),
    el("p", "", "Sail to adjacent charted water or coast. The cutter surveys each neighboring square as it arrives."),
    el("p", "", "Moor at a flag to establish a port and assign a good. Ports make stock when you end a season: two crates of the site’s specialty or one other crate. Load at the port, then deliver at Lantern Dock."),
    el("p", "", "Sailing uses sail steps. Founding, assigning, each loaded crate, and delivery use one order each. End the season to renew both; unspent steps and orders do not carry over."));
  const root = el("div", "windward-run",
    el("div", "windward-banner", el("div", "", el("p", "windward-kicker", "The Soundings charter"), season), score),
    el("div", "windward-panels",
      el("section", "windward-sheet windward-navigation", chart, legend, soundings, location, tide,
        el("div", "windward-compass", ...compass.map(item => item.control)), end, instructions, cues),
      orders));

  let goodControls: { assign: HTMLButtonElement; load: HTMLButtonElement }[] = [];
  function drawLedger(next: Position, port: Port | undefined): void {
    const { cargoCapacity, goods, ports, chart } = next;
    const sites = chart.flatMap((tile, index) => tile?.kind === "coast" && tile.specialty !== null
      ? [`${coordinate(index, next.width)} ${tile.name} (${goods[tile.specialty].name})`] : []);
    text(soundings, sites.length ? `Charted coasts: ${sites.join(" · ")}` : "Survey the coast to find promising berths.");
    const cargo = goods.reduce((total, good) => total + good.cargo, 0);
    text(hold, `Hold · ${cargo}/${cargoCapacity} crates`);
    const missing = goods.filter(good => !good.delivered).map(good => good.name);
    text(charter, `Goal: ${next.targetPoints} charter points and every good. ${missing.length ? `Still needed: ${missing.join(", ")}.` : "All three goods delivered."}`);
    manifests.replaceChildren(...goods.map(good => el("div", "", el("dt", "", good.name),
      el("dd", "", `Hold ${good.cargo} · Delivered ${good.delivered} · ${good.points} pt each`))));
    text(portsHeading, `Ports · ${ports.length}/${next.portLimit}`);
    warehouses.replaceChildren(...(ports.length ? ports.map(site => {
      const label = site.production === null ? "Idle" : `Making ${goods[site.production].name}`;
      const stock = goods.map((good, value) => `${good.name} ${site.stock[value]}`).join(" · ");
      const specialty = chart[site.index]?.specialty ?? 0;
      return el("li", "", el("strong", "", `${chart[site.index]?.name} · ${coordinate(site.index, next.width)}`),
        el("span", "", `${label} · ${goods[specialty].name} specialty`),
        el("span", "", `${stock} (max ${next.stockLimit} each)`));
    }) : [el("li", "", "No ports yet. Look for coastal flags.")]));
    const tile = chart[next.shipIndex];
    if (tile?.kind === "home") text(berth, "Lantern Dock accepts your whole hold in one order.");
    else if (tile?.kind === "coast" && tile.specialty !== null) {
      const good = goods[tile.specialty].name;
      text(berth, port ? `${good} is the local specialty. You are moored here.`
        : ports.length < next.portLimit ? `This site specializes in ${good}. Establish a port to make and load goods.`
        : `This site specializes in ${good}, but your port charter is full.`);
    } else text(berth, "Sail to a port to load or to the home dock to deliver.");
  }

  return {
    root,
    show(next, permitted) {
      enabled = permitted;
      const here = next.chart[next.shipIndex];
      const port = next.ports.find(item => item.index === next.shipIndex);
      if (position !== next) {
        position = next;
        drawChart(chart, next);
        drawLedger(next, port);
        text(season, `Season ${next.season} of ${next.seasonLimit}`);
        text(score, `${next.charterPoints} / ${next.targetPoints} charter points`);
        text(location, `Cutter at ${coordinate(next.shipIndex, next.width)} · ${describe(next, next.shipIndex)}`);
        text(tide, `${next.sailRemaining} sail steps · ${next.ordersRemaining} orders left`);
      }
      if (!goodControls.length) {
        goodControls = next.goods.map((good, value) => {
          const assign = button(`Make ${good.name}`, "button secondary", () => send({ kind: "assign", value }));
          const load = button(`Load ${good.name}`, "button secondary", () => send({ kind: "load", value }));
          orderButtons.append(assign, load);
          return { assign, load };
        });
      }
      const focusOrders = document.activeElement === found && !!port;
      found.hidden = here?.kind !== "coast" || !!port;
      deliver.hidden = here?.kind !== "home";
      orderButtons.hidden = !port;
      availability(found, { kind: "found" });
      availability(deliver, { kind: "deliver" });
      availability(end, { kind: "end-season" });
      for (const [value, { assign, load }] of goodControls.entries()) {
        availability(assign, { kind: "assign", value });
        availability(load, { kind: "load", value });
        assign.setAttribute("aria-pressed", String(port?.production === value));
      }
      if (focusOrders) goodControls.find(control => control.assign.getAttribute("aria-disabled") === "false")?.assign.focus();
      for (const item of compass) item.show(next);
    },
  };
}

function maritimeCues() {
  let audio: AudioContext | undefined;
  const control = button("Maritime cues", "button secondary windward-cues", () => {
    if (audio) {
      void audio.close().catch(() => {});
      audio = undefined;
      control.setAttribute("aria-pressed", "false");
      return;
    }
    if (!window.AudioContext) return;
    const context = new AudioContext();
    audio = context;
    control.setAttribute("aria-pressed", "true");
    void context.resume().catch(() => {
      if (audio === context) {
        audio = undefined;
        control.setAttribute("aria-pressed", "false");
        void context.close().catch(() => {});
      }
    });
  });
  control.setAttribute("aria-pressed", "false");
  control.hidden = !window.AudioContext;
  return {
    control,
    accepted(action: Action) {
      const context = audio;
      if (!context || context.state !== "running" || document.hidden) return;
      const notes = action.kind === "sail" ? [174, 146] : action.kind === "deliver" ? [392, 523, 659] : [];
      notes.forEach((pitch, index) => {
        const start = context.currentTime + index * .075;
        const tone = context.createOscillator();
        const gain = context.createGain();
        tone.type = action.kind === "sail" ? "sine" : "triangle";
        tone.frequency.setValueAtTime(pitch, start);
        gain.gain.setValueAtTime(.001, start);
        gain.gain.exponentialRampToValueAtTime(.035, start + .018);
        gain.gain.exponentialRampToValueAtTime(.001, start + .2);
        tone.connect(gain).connect(context.destination);
        tone.start(start);
        tone.stop(start + .21);
      });
    },
  };
}

export function mount(host: PortalHost): void {
  const cues = maritimeCues();
  solo(host, {
    game: "windward-ledger", variants: { soundings: "Soundings" }, defaultVariant: "soundings",
    startLabel: "Start voyage", restartLabel: "Restart voyage",
    finishLabel: (position: Position) => position.status === "won"
      ? "Charter complete. Welcome home, captain!" : "Voyage ended. The charter expired before the goal was met.",
    view: play => voyage(play, cues.control),
    onAccepted: cues.accepted,
  });
}
