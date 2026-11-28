import { apiCode, mountedJson } from "../api";
import { Confirmation } from "../confirm";
import { button, el, text } from "../dom";
import type { PortalHost } from "../host";
import { expired, field, gameFrame, messageFor } from "./shared";

type Game = "sudoku" | "solitaire" | "windward-ledger";
type Position = { finished: boolean; moves: number };
export interface SoloView<P> {
  root: HTMLElement;
  show: (position: P, enabled: boolean, newGame: boolean) => void;
}
type Table<P> = { game: Game; variant: string; revision: number; position: P };
type Options<P, A> = {
  game: Game;
  variants: Record<string, string>;
  defaultVariant: string;
  view: (play: (action: A) => void) => SoloView<P>;
  startLabel?: string;
  restartLabel?: string;
  finishLabel?: (position: P) => string;
  onAccepted?: (action: A) => void;
};

export function solo<P extends Position, A extends object>(host: PortalHost, options: Options<P, A>): void {
  const { game, variants, defaultVariant } = options;
  const startLabel = options.startLabel ?? (game === "sudoku" ? "Start puzzle" : "Deal cards");
  const restartLabel = options.restartLabel ?? (game === "sudoku" ? "Restart puzzle" : "Restart game");
  const frame = gameFrame(game, "Jade Court · Solo");
  const json = mountedJson(host.mount);
  const path = `api/solo/${game}`;
  const confirmation = new Confirmation();
  const variant = el("select");
  variant.append(...Object.entries(variants).map(([id, name]) => new Option(name, id)));
  variant.value = defaultVariant;
  const mode = field(game === "sudoku" ? "Difficulty" : "Variant", variant);
  mode.hidden = variant.options.length < 2;
  const summary = el("p", "eyebrow");
  const status = el("p", "feature-status");
  status.setAttribute("role", "status");
  const retry = button("Reconnect now", "button secondary", () => void open());
  const start = button(startLabel, "button primary", () => {
    const choice = variant.value;
    if (busy || !ready) return;
    if (current) confirmation.show(restartLabel,
      "Starting over replaces your saved progress.", start, () => void deal(choice));
    else void deal(choice);
  });
  const settings = el("section", "feature-panel", el("h2", "", "Your game"), mode, el("div", "feature-controls", start, retry));
  const gameView = options.view(action => void play(action));
  host.main.append(frame.root);
  frame.root.append(el("div", "feature-stack", summary, status, gameView.root, settings), confirmation.root);
  let current: Table<P> | null = null;
  let ready = false;
  let busy = false;

  function show(newGame = false): void {
    const position = current?.position;
    gameView.root.hidden = summary.hidden = !position;
    status.hidden = !position?.finished;
    retry.hidden = ready || busy;
    variant.disabled = start.disabled = busy || !ready;
    text(start, current ? restartLabel : startLabel);
    if (!position || !current) return;
    text(summary, `${variants[current.variant]} · ${position.moves} ${position.moves === 1 ? "move" : "moves"}`);
    text(status, position.finished ? options.finishLabel?.(position)
      ?? (game === "sudoku" ? "Puzzle complete!" : "Solitaire complete!") : "");
    gameView.show(position, ready && !busy && !position.finished, newGame);
  }

  async function open(): Promise<void> {
    if (busy) return;
    busy = true;
    frame.say("Opening your saved game…");
    show();
    let replaced = false;
    try {
      await host.openGuest();
      const { table } = await json<{ table: Table<P> | null }>(path);
      replaced = current?.revision !== table?.revision;
      current = table;
      if (current) variant.value = current.variant;
      frame.say("");
      ready = true;
    } catch (error) {
      if (expired(error)) host.forgetGuest();
      ready = false;
      frame.say(messageFor(error, "Couldn’t open the game. Check your connection and try again."));
    } finally { busy = false; show(replaced); }
  }

  async function deal(choice: string): Promise<void> {
    if (busy || !ready) return;
    busy = true;
    frame.say("");
    show();
    let replaced = false;
    try {
      current = await json<Table<P>>(path, "POST", { variant: choice });
      variant.value = current.variant;
      replaced = true;
      frame.heading.focus();
    } catch (error) {
      failure(error);
    } finally { busy = false; show(replaced); }
  }

  async function play(action: A): Promise<void> {
    if (!current || current.position.finished || busy || !ready) return;
    busy = true;
    frame.say("");
    show();
    let accepted = false;
    try {
      current = await json<Table<P>>(`${path}/moves`, "POST", { revision: current.revision, action });
      accepted = true;
    } catch (error) {
      failure(error);
    } finally { busy = false; show(); }
    if (accepted) options.onAccepted?.(action);
  }

  function failure(error: unknown): void {
    const problem = apiCode(error);
    if (expired(error)) host.forgetGuest();
    if (expired(error) || !problem || ["stale_room", "stale_revision", "room_not_found", "unavailable"].includes(problem)) {
      ready = false;
      frame.say(messageFor(error, "The game may have changed. Reconnect before trying again."));
    } else {
      frame.say(messageFor(error, "That move wasn’t accepted. Check the board and choose another move."));
    }
  }

  void open();
  window.addEventListener("pageshow", event => { if (event.persisted) void open(); });
  window.addEventListener("online", () => { if (!ready) void open(); });
}
