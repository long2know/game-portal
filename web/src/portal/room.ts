import type { Guest } from "../protocol";
import { ApiError, apiCode as code, isLive } from "./api";
import type { BotDifficulty, GameDefinition, Move, Table, TurnMotion } from "./api";
import { catalog, isBoardId } from "./catalog";
import type { BoardId } from "./catalog";
import { el, text } from "./dom";
import { readInvite } from "./host";
import type { PortalHost } from "./host";
import { reduceMotion, turnSteps } from "./motion";
import { SetupView, TableView } from "./screens";

const invalidName = /[\u0000-\u001f\u007f-\u009f]/u;
const pollDelay = 2500;

export class GameRoom {
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
  private motionEpoch = 0;
  private needsBaseline = false;

  constructor(private game: BoardId, private readonly host: PortalHost) {
    const { main } = host;
    this.setup = this.makeSetup();
    this.match = this.makeMatch();
    this.notice.setAttribute("role", "status");
    this.notice.hidden = true;
    main.append(this.notice, this.setup.root, this.match.root);
    this.show();
    void this.open();
    const stopMotion = () => this.stopMotion();
    matchMedia(reduceMotion).addEventListener("change", stopMotion);
    document.addEventListener("court-motion-preference", stopMotion);
    window.addEventListener("resize", stopMotion, { passive: true });
    window.visualViewport?.addEventListener("resize", stopMotion, { passive: true });
    for (const event of ["pointerdown", "keydown", "click"]) main.addEventListener(event, stopMotion, true);
    document.addEventListener("visibilitychange", () => {
      if (document.hidden) this.suspend();
      else this.reconnect();
    });
    window.addEventListener("pagehide", () => this.suspend());
    window.addEventListener("pageshow", event => { if (event.persisted) this.reconnect(); });
    window.addEventListener("online", () => this.reconnect());
    window.addEventListener("offline", () => {
      this.suspend();
      if (this.current && !isLive(this.current)) return;
      this.online = false;
      this.show();
    });
    window.addEventListener("hashchange", () => { if (readInvite() !== this.invite) location.reload(); });
  }

  private get definition(): GameDefinition | undefined {
    return this.definitions.find(definition => definition.id === this.game);
  }

  private gamePath(game: BoardId): string { return `${this.host.mount}${game}/`; }

  private makeSetup(): SetupView {
    document.title = `${catalog[this.game].name} · Jade Court`;
    return new SetupView(this.game, this.invite !== null, bots => void this.enter(bots), () => void this.open());
  }

  private makeMatch(): TableView {
    return new TableView(this.game, this.gamePath(this.game), move => void this.play(move), () => this.reconnect(),
      () => this.anotherGame(), (table, action) => void this.end(table, action));
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
    const { api, openGuest } = this.host;
    try {
      const [{ games }, guest] = await Promise.all([api.games(), openGuest()]);
      this.definitions = games;
      this.guest = guest;
      this.setup.setDefinition(this.definition);
      this.setup.remember(guest);
      if (this.invite === null) {
        const { tableId } = await api.resume(this.game);
        if (tableId) {
          const table = await api.read(tableId);
          if (table.status !== "closed") this.adopt(table);
        }
      }
    } catch (error) {
      if (code(error) === "room_not_found") {
        this.message("Your previous table is no longer available. You can start a new one below.");
      } else {
        if (code(error) === "not_authenticated") this.host.forgetGuest();
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
    const { api, changeProfile } = this.host;
    try {
      if (name !== this.guest.displayName) this.guest = await changeProfile(() => ({ displayName: name }));
      this.setup.remember(this.guest);
      const difficulty = bots && this.game === "chess" ? this.setup.difficulty.value as BotDifficulty : undefined;
      const table = this.invite ? await api.join(this.invite) : await api.create(this.game, variant, players, bots, difficulty);
      if (!isBoardId(table.game)) throw new ApiError("unavailable");
      if (this.invite) {
        this.invite = null;
        history.replaceState(null, "", this.gamePath(table.game) + location.search);
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
      if (!ready) this.host.forgetGuest();
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

  private adopt(next: Table, epoch?: number, motion?: TurnMotion): void {
    const prior = this.current;
    if (prior?.id === next.id && next.revision < prior.revision) return;
    if (!isLive(next)) this.pause();
    if (prior?.status !== next.status) this.match.confirmation.close();
    const changed = prior?.id !== next.id || prior.revision !== next.revision;
    const steps = prior && epoch === this.motionEpoch && !document.hidden &&
      document.documentElement.dataset.reducedMotion !== "true" && !matchMedia(reduceMotion).matches
      ? turnSteps(prior, next, motion) : [];
    if (changed) {
      this.match.board.stopMotion();
      if (prior?.id !== next.id) this.needsBaseline = document.hidden;
    }
    this.current = next;
    this.online = true;
    this.show(changed);
    if (prior?.position && steps.length) this.match.board.replay(prior.position, steps);
  }

  private anotherGame(): void {
    this.suspend();
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

  private stopMotion(): void {
    this.motionEpoch++;
    this.match.board.stopMotion();
  }

  private suspend(): void {
    this.needsBaseline = true;
    this.stopMotion();
    this.pause();
  }

  private schedule(): void {
    window.clearTimeout(this.timer);
    if (this.current && isLive(this.current) && !document.hidden && !this.busy && !this.request) {
      this.timer = window.setTimeout(() => void this.sync(), pollDelay);
    }
  }

  private reconnect(): void {
    this.needsBaseline = true;
    this.stopMotion();
    if (this.current && isLive(this.current)) void this.sync();
  }

  private async sync(): Promise<boolean> {
    const id = this.current?.id;
    if (!id || document.hidden || this.busy || this.request) return false;
    this.pause();
    const request = new AbortController();
    const epoch = this.motionEpoch;
    this.request = request;
    try {
      const fresh = await this.host.api.read(id, request.signal);
      if (request.signal.aborted) return false;
      this.adopt(fresh, this.online && !this.needsBaseline ? epoch : undefined);
      this.needsBaseline = false;
      return true;
    } catch (error) {
      if (request.signal.aborted) return false;
      const problem = code(error);
      if (problem === "room_not_found" || problem === "not_authenticated") {
        if (problem === "not_authenticated") this.host.forgetGuest();
        this.anotherGame();
        this.message(problem === "room_not_found" ? "This table is no longer available. Start another game below." :
          "Your guest session ended. Try again to reconnect.");
        this.setup.state(problem === "room_not_found", false, problem === "not_authenticated");
        return false;
      }
      this.stopMotion();
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
    if (table?.status !== "playing" || table.position?.turn !== table.seat || !this.online || this.busy) return;
    this.pause();
    this.stopMotion();
    const epoch = this.motionEpoch;
    this.busy = true;
    this.message("");
    this.show();
    let refresh = false;
    let stale = false;
    try {
      const { turnMotion, ...next } = await this.host.api.move(table.id, table.revision, move.id);
      this.adopt(next, epoch, turnMotion);
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

  private async end(table: Table, action: "leave" | "resign"): Promise<void> {
    if (this.current?.id !== table.id || !isLive(this.current) || !this.online || this.busy) return;
    this.suspend();
    this.busy = true;
    this.message("");
    this.show();
    let failed = false;
    let stale = false;
    try {
      this.adopt(await this.host.api.end(table.id, table.revision, action));
      this.match.heading.focus();
    } catch (error) {
      failed = true;
      stale = code(error) === "stale_room";
      this.online = false;
      this.message(stale ? "The table changed. Refreshing before you try again…" :
        "Couldn’t confirm the table ended. Checking for updates…");
    } finally {
      this.busy = false;
      this.show();
      if (failed && await this.sync()) {
        this.message(this.current && isLive(this.current) ? "The table is up to date. Check it before ending the game again." : "");
      }
      this.schedule();
    }
  }
}
