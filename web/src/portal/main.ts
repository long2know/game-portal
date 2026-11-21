import "./styles.css";
import { ApiError, portalApi } from "./api";
import type { GameDefinition, Guest, Move, Table } from "./api";
import { catalog, isBoardId } from "./catalog";
import type { BoardId } from "./catalog";
import { el, link, text } from "./dom";
import { lobby, SetupView, TableView } from "./screens";

const app = document.querySelector<HTMLElement>("#app")!;
const mount = app.dataset.base ?? "/";
const route = location.pathname.startsWith(mount) ? location.pathname.slice(mount.length).replace(/\/$/, "") : "";
const gamePath = (game: BoardId) => `${mount}${game}/`;
const api = portalApi(mount);
const readInvite = () => new URLSearchParams(location.hash.slice(1)).get("invite");
const invalidName = /[\u0000-\u001f\u007f-\u009f]/u;
const pollDelay = 2500;

const brand = link("", mount, "brand");
brand.setAttribute("aria-label", "Jade Court · All games");
brand.append(el("span", "brand-tile"), el("span", "brand-name", "Jade Court"));
const nav = el("nav", "site-nav", isBoardId(route)
  ? link("‹ All games", mount, "button")
  : link("Mahjong table ↗", `${mount}mahjong/`, "button"));
nav.setAttribute("aria-label", "Court navigation");
const main = el("main");
main.id = "main";
main.tabIndex = -1;
const skip = link("Skip to content", "#main", "skip-link");
skip.addEventListener("click", event => {
  event.preventDefault();
  main.focus();
});
const footer = el("footer", "court-footer",
  el("span", "footer-mark", "◆"), el("span", "", "Jade Court"), el("span", "", "A place for a good game."));
app.append(skip, el("div", "shell", el("header", "header", brand, nav), main, footer));

function code(error: unknown): string {
  return error instanceof ApiError ? error.code : "";
}

class GameRoom {
  private readonly notice = el("p", "room-notice");
  private guest: Guest | null = null;
  private definitions: GameDefinition[] = [];
  private current: Table | null = null;
  private invite = readInvite();
  private setup: SetupView;
  private match: TableView;
  private busy = false;
  private online = true;
  private timer = 0;
  private request: AbortController | null = null;

  constructor(private game: BoardId) {
    this.setup = this.makeSetup();
    this.match = this.makeMatch();
    this.notice.setAttribute("role", "status");
    this.notice.hidden = true;
    main.append(this.notice, this.setup.root, this.match.root);
    this.show();
    void this.open();
    document.addEventListener("visibilitychange", () => {
      if (document.hidden) this.pause();
      else this.reconnect();
    });
    window.addEventListener("pagehide", () => this.pause());
    window.addEventListener("pageshow", event => { if (event.persisted) this.reconnect(); });
    window.addEventListener("online", () => this.reconnect());
    window.addEventListener("offline", () => {
      if (this.current?.status === "finished") return;
      this.pause();
      this.online = false;
      this.show();
    });
    window.addEventListener("hashchange", () => { if (readInvite() !== this.invite) location.reload(); });
  }

  private get definition(): GameDefinition | undefined {
    return this.definitions.find(definition => definition.id === this.game);
  }

  private makeSetup(): SetupView {
    document.title = `${catalog[this.game].name} · Jade Court`;
    return new SetupView(this.game, this.invite !== null, bots => void this.enter(bots), () => void this.open());
  }

  private makeMatch(): TableView {
    return new TableView(this.game, gamePath(this.game), move => void this.play(move), () => this.reconnect(), () => this.anotherGame());
  }

  private message(value: string): void {
    this.notice.hidden = !value;
    text(this.notice, value);
  }

  private show(reset = false): void {
    this.setup.root.hidden = !!this.current;
    this.match.root.hidden = !this.current;
    if (this.current) {
      const variant = this.definition?.variants.find(item => item.id === this.current!.variant)?.label ?? "Standard";
      this.match.show(this.current, variant, this.online, this.busy, reset);
    }
  }

  private async open(): Promise<void> {
    if (this.busy) return;
    this.busy = true;
    this.message("");
    this.setup.state(false, true, false, "Opening your guest session…");
    let failed = false;
    try {
      const [{ games }, guest] = await Promise.all([api.games(), api.guest()]);
      this.definitions = games;
      this.guest = guest;
      this.setup.setDefinition(this.definition);
      this.setup.remember(guest);
      if (this.invite === null) {
        const { tableId } = await api.resume(this.game);
        if (tableId) this.adopt(await api.read(tableId));
      }
    } catch (error) {
      if (code(error) === "room_not_found") {
        this.message("Your previous table is no longer available. You can start a new one below.");
      } else {
        failed = true;
        this.message("Couldn’t open the game. Check your connection and try again.");
      }
    } finally {
      this.busy = false;
      const ready = !failed && !!this.guest && (this.invite !== null || !!this.definition);
      this.setup.state(ready, false, !ready, ready ? "" : "This table isn’t available yet.");
      this.show();
      this.schedule();
    }
  }

  private async enter(bots: boolean): Promise<void> {
    if (this.busy || this.current || !this.guest) return;
    const name = this.setup.name.value.trim() || this.guest.displayName;
    if (invalidName.test(name)) {
      this.message("Use a display name without control characters.");
      this.setup.name.focus();
      return;
    }
    if (this.invite === "") {
      this.message("This invitation is missing its code. Ask your friend for the full link.");
      return;
    }
    this.busy = true;
    this.message("");
    const variant = this.setup.variant.value;
    const players = Number(this.setup.players.value);
    this.setup.state(true, true, false, this.invite ? "Taking your seat…" : "Setting the table…");
    let ready = true;
    try {
      if (name !== this.guest.displayName) this.guest = await api.profile(name);
      this.setup.remember(this.guest);
      const table = this.invite ? await api.join(this.invite) : await api.create(this.game, variant, players, bots);
      if (!isBoardId(table.game)) throw new ApiError("unavailable");
      if (this.invite) {
        this.invite = null;
        history.replaceState(null, "", gamePath(table.game) + location.search);
        this.game = table.game;
        const setup = this.makeSetup();
        const match = this.makeMatch();
        this.setup.root.replaceWith(setup.root);
        this.match.root.replaceWith(match.root);
        this.setup = setup;
        this.match = match;
        this.setup.setDefinition(this.definition);
        this.setup.remember(this.guest);
      }
      this.adopt(table);
      this.match.heading.focus();
    } catch (error) {
      const problem = code(error);
      ready = problem !== "not_authenticated";
      this.message(!ready ? "Your guest session ended. Try again to reconnect." :
        problem === "invalid_request" ? "Check your display name and table settings, then try again." :
        problem === "busy" ? "The court is busy. Give it a moment, then try again." :
        this.invite && problem && problem !== "unavailable" ? "This invitation couldn’t be accepted. Ask your friend for a fresh link." :
        "Couldn’t open the table. Check your connection and try again.");
    } finally {
      this.busy = false;
      this.setup.state(ready, false, !ready);
      this.show();
      this.schedule();
    }
  }

  private adopt(next: Table): void {
    if (this.current?.id === next.id && next.revision < this.current.revision) return;
    const changed = this.current?.id !== next.id || this.current.revision !== next.revision;
    this.current = next;
    this.online = true;
    this.show(changed);
  }

  private anotherGame(): void {
    this.pause();
    this.current = null;
    this.message("");
    this.show();
    this.setup.heading.focus();
  }

  private pause(): void {
    window.clearTimeout(this.timer);
    this.request?.abort();
    this.request = null;
  }

  private schedule(): void {
    window.clearTimeout(this.timer);
    if (this.current && this.current.status !== "finished" && !document.hidden && !this.busy && !this.request) {
      this.timer = window.setTimeout(() => void this.sync(), pollDelay);
    }
  }

  private reconnect(): void {
    if (this.current?.status !== "finished") void this.sync();
  }

  private async sync(): Promise<boolean> {
    const id = this.current?.id;
    if (!id || document.hidden || this.busy || this.request) return false;
    this.pause();
    const request = new AbortController();
    this.request = request;
    try {
      const fresh = await api.read(id, request.signal);
      if (request.signal.aborted) return false;
      this.adopt(fresh);
      return true;
    } catch (error) {
      if (request.signal.aborted) return false;
      const problem = code(error);
      if (problem === "room_not_found" || problem === "not_authenticated") {
        this.anotherGame();
        this.message(problem === "room_not_found" ? "This table is no longer available. Start another game below." :
          "Your guest session ended. Try again to reconnect.");
        this.setup.state(problem === "room_not_found", false, problem === "not_authenticated");
        return false;
      }
      this.online = false;
      this.show();
      return false;
    } finally {
      if (this.request === request) {
        this.request = null;
        this.schedule();
      }
    }
  }

  private async play(move: Move): Promise<void> {
    const table = this.current;
    if (!table || table.position?.turn !== table.seat || !this.online || this.busy) return;
    this.pause();
    this.busy = true;
    this.message("");
    this.show();
    let refresh = false;
    let stale = false;
    try {
      this.adopt(await api.move(table.id, table.revision, move.id));
    } catch (error) {
      stale = code(error) === "stale_room" || code(error) === "not_your_turn" || code(error) === "illegal_move";
      refresh = true;
      this.online = false;
      this.show(true);
      this.message(stale ? "The table changed before your move could be played. Refreshing the board…" :
        "Couldn’t confirm your move. Checking the board before you try again…");
    } finally {
      this.busy = false;
      this.show();
      if (refresh && await this.sync()) {
        this.message(stale ? "The table changed. The board is up to date; choose your move again if it’s your turn." :
          "The board is up to date. Check the last move before playing again.");
      }
      this.schedule();
    }
  }
}

if (isBoardId(route)) new GameRoom(route);
else main.append(lobby(mount));
