import type { Guest } from "../../protocol";
import { ApiError, apiCode, isLive, mountedJson } from "../api";
import type { Table } from "../api";
import { Confirmation } from "../confirm";
import { button, el, text } from "../dom";
import { readInvite } from "../host";
import type { PortalHost } from "../host";
import { rememberTable, tableEntry } from "../invite-handoff";
import { expired, field, gameFrame, invitationField, messageFor } from "./shared";

type Game = "holdem" | "word-tiles" | "farkle" | "backgammon" | "big-two" | "koi-koi";
export type MultiplayerTable<P> = Pick<Table, "id" | "revision" | "status" | "seat" | "seats" | "invite" | "endReason" | "endedBySeat">
  & { position: P | null };
export interface MatchView<P> {
  root: HTMLElement;
  show: (table: MultiplayerTable<P>, enabled: boolean) => void;
}
type Options<P, A> = {
  game: Game;
  endpoint: "poker" | "words" | "farkle" | "backgammon" | "big-two" | "koi-koi";
  playerCounts: number[];
  view: (play: (action: A) => void, next: () => void) => MatchView<P>;
  study?: HTMLElement;
  guide?: HTMLElement;
};

export const nameAt = <P>({ seats, seat }: MultiplayerTable<P>, index: number) =>
  index === seat ? "You" : seats.find(item => item.seat === index)?.name || `Seat ${index + 1}`;

export class Multiplayer<P, A extends object> {
  private readonly frame;
  private readonly json;
  private readonly path;
  private readonly view;
  private readonly share = invitationField(opener => this.host.social.open(opener, true));
  private readonly confirm = new Confirmation();
  private readonly name = el("input");
  private readonly players = el("select");
  private readonly setup: HTMLFormElement;
  private readonly setupPanel: HTMLElement;
  private readonly setupActions: HTMLButtonElement[];
  private readonly status = el("p", "feature-status");
  private readonly roster = el("ul", "waiting-roster");
  private readonly waiting = el("section", "feature-panel", el("h2", "", "At this table"), this.roster);
  private readonly leave = button("Leave table", "button secondary", () => this.askLeave());
  private readonly restart = button("Set up another game", "button primary", () => this.restartSetup());
  private readonly retry = button("Reconnect now", "button secondary", () => {
    void (this.current && this.guest ? this.sync() : this.open());
  });
  private readonly match: HTMLElement;
  private invite = readInvite();
  private readonly inviteSetup = this.invite !== null;
  private acceptedId?: string;
  private guest?: Guest;
  private current?: MultiplayerTable<P>;
  private online = true;
  private busy = false;
  private leftLocally = false;
  private timer = 0;
  private read?: AbortController;

  constructor(private readonly host: PortalHost, private readonly options: Options<P, A>) {
    this.acceptedId = tableEntry(host.mount, options.game)?.tableId;
    this.frame = gameFrame(options.game, "Jade Court table");
    this.path = `api/${options.endpoint}`;
    this.json = mountedJson(host.mount);
    this.view = options.view(action => void this.command("actions", { action }), () => void this.command("next"));
    this.name.placeholder = "Guest";
    this.name.setAttribute("autocomplete", "nickname");
    this.name.maxLength = 24;
    this.players.append(...options.playerCounts.map(count => new Option(`${count} players`, String(count))));
    const inviteOnly = this.inviteSetup;
    const bots = button("Play a bot", "button primary");
    const friends = button("Invite a friend", "button secondary");
    const join = button("Join table", "button primary");
    this.setupActions = inviteOnly ? [join] : [bots, friends];
    const labels = () => {
      const many = Number(this.players.value) > 2;
      text(bots, many ? "Play with bots" : "Play a bot");
      text(friends, many ? "Invite friends" : "Invite a friend");
    };
    labels();
    this.players.addEventListener("change", labels);
    for (const control of this.setupActions) control.type = "submit";
    this.setup = el("form", "setup-form",
      el("h2", "", inviteOnly ? "A friend saved you a seat." : "Set the table."),
      el("p", "muted", inviteOnly ? "Join with the name your friends will see." : options.game === "big-two" ?
        "Play with three bots, or share a private invitation for four human seats." : "Start with a bot or share a private invitation."),
      field("Display name", this.name),
      ...(inviteOnly || options.playerCounts.length < 2 ? [] : [field("Players", this.players)]),
      el("div", "setup-actions", ...this.setupActions));
    this.setup.addEventListener("submit", event => {
      event.preventDefault();
      void this.enter(event.submitter !== friends);
    });
    this.setupPanel = options.study ? el("div", "setup", this.setup, options.study) : this.setup;
    this.status.setAttribute("role", "status");
    this.match = el("div", "feature-stack", this.status, this.share.root, this.waiting, this.view.root,
      el("div", "feature-controls", this.leave, this.restart));
    host.main.append(this.frame.root);
    this.frame.root.append(this.setupPanel, this.match, this.retry);
    if (options.guide) this.frame.root.append(options.guide);
    this.frame.root.append(this.confirm.root);
    void this.open();
    document.addEventListener("court-display-name", () => {
      void host.openGuest().then(guest => this.remember(guest), () => {});
    });
    document.addEventListener("visibilitychange", () => document.hidden ? this.pause() : void this.sync());
    window.addEventListener("pagehide", () => this.pause());
    window.addEventListener("pageshow", event => { if (event.persisted) void this.sync(); });
    window.addEventListener("online", () => void this.sync());
    window.addEventListener("offline", () => {
      this.pause();
      if (this.current && !isLive(this.current)) return;
      this.online = false;
      this.show();
    });
    window.addEventListener("hashchange", () => { if (readInvite() !== this.invite) location.reload(); });
  }

  private href(): string { return `${this.host.mount}${this.options.game}/`; }
  private tablePath(id: string): string { return `${this.path}/tables/${encodeURIComponent(id)}`; }

  private show(): void {
    const table = this.current;
    const openSeat = table?.seats.some(seat => !seat.occupied);
    this.host.social.update({ ready: !!table || !!this.guest && this.online && !this.busy && !this.acceptedId,
      busy: !!table && isLive(table),
      table: table?.status === "waiting" && this.online && !this.busy && table.invite && openSeat
        ? { game: this.options.game, tableId: table.id } : undefined });
    this.setupPanel.hidden = !!table;
    this.match.hidden = !table;
    this.match.setAttribute("aria-busy", String(this.busy));
    const unavailable = !this.guest || this.busy || !this.online;
    this.name.disabled = this.players.disabled = unavailable;
    for (const control of this.setupActions) control.disabled = unavailable;
    this.retry.hidden = this.online && !!this.guest || this.busy;
    if (!table) return;
    const { status, seats, seat, position, endedBySeat: closedBy } = table;
    const finished = !isLive(table);
    const waiting = status === "waiting";
    const previouslyFocused = document.activeElement;
    text(this.status, status === "closed" ? closedBy === seat || this.leftLocally ? "Table closed. You left the table." :
      closedBy != null ? `Table closed. ${nameAt(table, closedBy)} left the table.` :
        table.endReason === "left" ? "Table closed. A player left the table." : "Table closed." :
      !this.online ? "Reconnecting. Your seat is saved." : waiting ? "Waiting for players" : "");
    this.status.hidden = !this.status.textContent;
    this.leave.hidden = finished;
    this.leave.disabled = unavailable;
    this.restart.hidden = !finished;
    this.waiting.hidden = !waiting;
    if (waiting) this.roster.replaceChildren(...seats.map(item => el("li", "", item.occupied
      ? `${item.name || `Seat ${item.seat + 1}`}${item.seat === seat ? " · You" : item.bot ? " · Bot" : ""}`
      : `Open seat ${item.seat + 1}`)));
    this.share.root.hidden = !waiting || !table.invite;
    this.share.invite.hidden = !openSeat;
    this.share.invite.disabled = unavailable;
    if (!this.share.root.hidden && table.invite) {
      const invitation = new URL(this.href(), location.origin);
      invitation.hash = new URLSearchParams({ invite: table.invite }).toString();
      this.share.setValue(invitation.href);
    }
    this.view.root.hidden = !position || status === "closed";
    if (!this.view.root.hidden) this.view.show(table, !unavailable && !finished);
    const focused = document.activeElement;
    if (previouslyFocused instanceof HTMLElement && previouslyFocused.closest("[hidden]") &&
      (focused === previouslyFocused || focused === document.body)) this.frame.heading.focus();
  }

  private adopt(table: MultiplayerTable<P>): void {
    if (this.current?.id === table.id && this.current.revision > table.revision) return;
    if (this.current?.status !== table.status) this.confirm.close();
    this.current = table;
    this.online = true;
  }

  private remember(guest: Guest): void {
    const label = (guest?: Guest) => guest?.displayName && guest.displayName !== "Guest" ? guest.displayName : "";
    if (this.name.value === label(this.guest)) this.name.value = label(guest);
    this.guest = guest;
  }

  private async open(): Promise<void> {
    if (this.busy) return;
    this.busy = true;
    this.frame.say("Opening your guest session…");
    this.show();
    try {
      this.remember(await this.host.openGuest());
      this.online = true;
      if (this.acceptedId) {
        const table = await this.json<MultiplayerTable<P>>(this.tablePath(this.acceptedId));
        if (table.id !== this.acceptedId || table.status === "closed") throw new ApiError("room_not_found");
        this.adopt(table);
        this.frame.heading.focus();
      } else if (this.invite === null) {
        const { tableId } = await this.json<{ tableId: string | null }>(`${this.path}/resume`);
        if (tableId) {
          const table = await this.json<MultiplayerTable<P>>(this.tablePath(tableId));
          if (table.status !== "closed") this.adopt(table);
        }
      }
      this.frame.say("");
    } catch (error) {
      const missing = apiCode(error) === "room_not_found";
      if (this.acceptedId || !missing) this.offline(error);
      this.frame.say(this.acceptedId ? "Couldn’t open the accepted table. Reconnect, or ask your friend for their full invite link." :
        missing ? "Your previous table is no longer available. You can start a new one." :
        messageFor(error, "Couldn’t open the game. Check your connection and try again."));
    } finally { this.busy = false; this.show(); this.schedule(); }
  }

  private async enter(bots: boolean): Promise<void> {
    if (!this.guest || this.busy || this.current || !this.online) return;
    const displayName = this.name.value.trim() || this.guest.displayName;
    if (/[\u0000-\u001f\u007f-\u009f]/u.test(displayName)) {
      this.frame.say("Use a display name without control characters.");
      this.name.focus();
      return;
    }
    if (this.invite === "") {
      this.frame.say("This invitation is missing its code. Ask your friend for the full link.");
      return;
    }
    this.busy = true;
    this.frame.say(this.invite ? "Taking your seat…" : "Setting the table…");
    this.show();
    try {
      if (displayName !== this.guest.displayName) this.guest = await this.host.changeProfile(() => ({ displayName }));
      const table = this.invite ? await this.json<MultiplayerTable<P>>(`${this.path}/join`, "POST", { invite: this.invite }) :
        await this.json<MultiplayerTable<P>>(`${this.path}/tables`, "POST", { players: Number(this.players.value), bots });
      if (this.invite) {
        this.invite = null;
        history.replaceState(null, "", this.href() + location.search);
      }
      this.adopt(table);
      this.frame.say("");
      this.frame.heading.focus();
    } catch (error) {
      if (expired(error)) this.offline(error);
      this.frame.say(messageFor(error, this.invite && apiCode(error) && apiCode(error) !== "unavailable"
        ? "This invitation couldn’t be accepted. Ask your friend for a fresh link."
        : "Couldn’t open the table. Check your connection and try again."));
    } finally { this.busy = false; this.show(); this.schedule(); }
  }

  private restartSetup(): void {
    if (this.inviteSetup && this.invite === null) {
      location.reload();
      return;
    }
    if (this.acceptedId) rememberTable(this.host.mount, this.options.game, null);
    this.acceptedId = undefined;
    this.current = undefined;
    this.leftLocally = false;
    this.frame.say("");
    this.show();
    this.frame.heading.focus();
  }

  private offline(error: unknown): void {
    this.online = false;
    if (expired(error)) {
      this.host.forgetGuest();
      this.guest = undefined;
    }
  }

  private pause(): void {
    window.clearTimeout(this.timer);
    this.read?.abort();
    this.read = undefined;
  }

  private schedule(): void {
    window.clearTimeout(this.timer);
    if (!this.busy && !this.read && this.guest && this.current && isLive(this.current) && !document.hidden) {
      this.timer = window.setTimeout(() => void this.sync(), 2500);
    }
  }

  private async sync(): Promise<boolean> {
    const table = this.current;
    if (!table || !isLive(table) || !this.guest || this.busy || this.read || document.hidden) return false;
    this.pause();
    const read = this.read = new AbortController();
    try {
      const fresh = await this.json<MultiplayerTable<P>>(this.tablePath(table.id), "GET", undefined, read.signal);
      if (read.signal.aborted) return false;
      if (fresh.revision !== table.revision || !this.online) {
        if (!this.online) this.frame.say("");
        this.adopt(fresh);
        this.show();
      }
      return true;
    } catch (error) {
      if (read.signal.aborted) return false;
      if (apiCode(error) === "room_not_found") {
        this.current = undefined;
        this.confirm.close();
        this.frame.say(this.acceptedId ? "The accepted table is no longer available. Reconnect, or ask for a full invite link." :
          "This table is no longer available.");
        this.online = !this.acceptedId;
      } else {
        this.offline(error);
        this.frame.say(messageFor(error, "Couldn’t refresh the table. We’ll try again."));
      }
      this.show();
      return false;
    } finally {
      if (this.read === read) this.read = undefined;
      this.schedule();
    }
  }

  private askLeave(): void {
    const table = this.current;
    if (!table || !isLive(table)) return;
    this.confirm.show("Leave table", "Leaving closes this table for everyone. It can’t be resumed.", this.leave,
      () => void this.command("leave", {}, table.revision));
  }

  private async command(action: "actions" | "next" | "leave", content = {}, revision?: number): Promise<void> {
    const table = this.current;
    if (!table || !isLive(table) || this.busy || !this.online) return;
    this.pause();
    this.busy = true;
    this.frame.say("");
    this.show();
    let check = false;
    try {
      const next = await this.json<MultiplayerTable<P>>(`${this.tablePath(table.id)}/${action}`, "POST",
        { revision: revision ?? table.revision, ...content });
      this.leftLocally = action === "leave" && next.status === "closed";
      this.adopt(next);
      if (!isLive(next)) this.frame.heading.focus();
    } catch (error) {
      const problem = apiCode(error);
      check = !problem || ["unavailable", "stale_room", "not_your_turn", "room_not_found"].includes(problem);
      if (expired(error) || check) this.offline(error);
      this.frame.say(messageFor(error, check ? "The table may have changed. Checking for updates…" :
        this.options.game === "word-tiles" ? "That play wasn’t accepted. Check the word, placement, or tiles and try again." :
          "That action wasn’t accepted. Check the available table actions."));
    } finally {
      this.busy = false;
      this.show();
      if (check && await this.sync()) this.frame.say(this.current && isLive(this.current)
        ? "The table is up to date. Check the latest play before trying again." : "");
      this.schedule();
    }
  }
}
