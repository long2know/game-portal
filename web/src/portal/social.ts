import "./social.css";
import { mountedJson } from "./api";
import { catalog } from "./catalog";
import { button, el, text } from "./dom";
import { isInviteGame, isInviteTable, openAcceptedTable } from "./invite-handoff";
import type { InviteGame, InviteTable } from "./invite-handoff";
import { preferenceKey } from "./social-entry";
import type { LobbyHooks, LobbyState } from "./social-entry";

type Player = { id: string; displayName: string };
type Invite = Player & { game: InviteGame; expiresAtUtc: string };
type Presence = { available: boolean; players: Player[]; incoming: Invite[]; outgoing: Invite[] };
type LinkState = "checking" | "online" | "disconnected" | "busy" | "auth";

const presencePath = "api/social/presence";
const invitesPath = "api/social/invites";
const interval = 15_000;
const code = (error: unknown) => error instanceof Error && "code" in error && typeof error.code === "string" ? error.code : "";
const empty: Presence = { available: false, players: [], incoming: [], outgoing: [] };
const player = (value: Player | null): value is Player =>
  !!value && typeof value.displayName === "string" && typeof value.id === "string" && /^[A-Za-z0-9_-]{1,128}$/.test(value.id);
const invite = (value: Invite | null): value is Invite =>
  player(value) && isInviteGame(value.game) && typeof value.expiresAtUtc === "string" && Number.isFinite(Date.parse(value.expiresAtUtc));

function roster<T extends Player>(heading: string, label: string, row: (item: T) => HTMLElement) {
  const title = el("h3", "", heading);
  title.tabIndex = -1;
  const list = el("ul", "court-lobby-list");
  list.setAttribute("aria-label", heading);
  const hint = el("p", "court-lobby-muted", label);
  const rows = new Map<string, HTMLElement>();
  return {
    root: el("section", "court-lobby-section", title, hint, list),
    update(items: T[], message: string, enabled: boolean, visibleActions = true) {
      const focused = document.activeElement;
      const hadFocus = list.contains(focused);
      const next = new Set<string>();
      let at = list.firstElementChild;
      for (const item of items) {
        if (next.has(item.id)) continue;
        next.add(item.id);
        const node = rows.get(item.id) ?? row(item);
        rows.set(item.id, node);
        text(node.querySelector("strong")!, item.displayName);
        for (const control of node.querySelectorAll("button")) {
          control.hidden = !visibleActions;
          control.setAttribute("aria-disabled", String(!enabled));
        }
        if (node !== at) list.insertBefore(node, at);
        at = node.nextElementSibling;
      }
      for (const [id, node] of rows) if (!next.has(id)) { node.remove(); rows.delete(id); }
      if (hadFocus && (!list.contains(focused) || (focused as HTMLElement).hidden)) title.focus({ preventScroll: true });
      hint.hidden = !!items.length;
      list.hidden = !items.length;
      text(hint, message);
    },
  };
}

export class OnlineLobby {
  private readonly json;
  private readonly dialog = el("dialog", "court-lobby");
  private readonly heading = el("h2", "", "Online lobby");
  private readonly close = button("Close", "court-lobby-button", () => this.dialog.close());
  private readonly live = el("span", "court-lobby-live");
  private readonly linkStatus = el("p", "court-lobby-muted");
  private readonly actionStatus = el("p", "court-lobby-message");
  private readonly localStatus = el("p", "court-lobby-muted");
  private readonly retry = button("Retry lobby", "court-lobby-button", () => this.refresh());
  private readonly retryAccept = button("Retry accepting invite", "court-lobby-button primary", () => {
    if (this.unconfirmed) this.respond(this.unconfirmed, "accept", true);
  });
  private readonly proceed = button("Open accepted table", "court-lobby-button primary", () => this.navigate());
  private readonly toggle = el("input");
  private readonly name = el("input");
  private readonly save = button("Save name", "court-lobby-button");
  private readonly directory = roster<Player>("Available guests", "", item => el("li", "court-lobby-row",
    el("strong"), button("Invite", "court-lobby-button primary", () => this.send(item.id))));
  private readonly incoming = roster<Invite>("Incoming invites", "", item => this.inviteRow(item, true));
  private readonly outgoing = roster<Invite>("Sent invites", "", item => this.inviteRow(item, false));
  private readonly profile: HTMLFormElement;
  private opener?: HTMLButtonElement;
  private snapshot?: Presence;
  private connection: LinkState = "checking";
  private savedName?: string;
  private refreshName = true;
  private accepted?: InviteTable;
  private unconfirmed?: Invite;
  private table?: InviteTable;
  private generation = 0;
  private suspended = document.hidden;
  private announced = false;
  private timer = 0;
  private retryAt = 0;
  private failures = 0;
  private requested = false;
  private acting = false;
  private poll?: Promise<void>;
  private presenceRequest?: Promise<Presence>;
  private off?: Promise<void>;
  private sendOff?: () => Promise<void>;

  constructor(private readonly trigger: HTMLButtonElement, private readonly hooks: LobbyHooks,
    private readonly tabId: string, private readonly state: () => LobbyState, retryAt: number) {
    this.json = mountedJson(hooks.mount);
    if (retryAt > Date.now()) {
      this.retryAt = retryAt;
      this.connection = "busy";
    }
    this.heading.id = "court-lobby-title";
    this.dialog.setAttribute("aria-labelledby", this.heading.id);
    this.toggle.type = "checkbox";
    this.toggle.setAttribute("role", "switch");
    this.toggle.addEventListener("change", () => {
      const available = this.toggle.checked;
      this.toggle.checked = !!this.snapshot?.available;
      void this.act(async generation => this.presence(generation, available), "Couldn’t update availability. Try again.");
    });
    this.linkStatus.setAttribute("role", "status");
    this.actionStatus.setAttribute("role", "status");
    this.live.setAttribute("role", "status");
    this.name.maxLength = 24;
    this.name.placeholder = "Guest";
    this.name.setAttribute("autocomplete", "nickname");
    this.save.type = "submit";
    this.close.autofocus = true;
    this.profile = el("form", "court-lobby-profile", el("label", "", el("span", "", "Display name"), this.name), this.save);
    this.profile.addEventListener("submit", event => { event.preventDefault(); this.saveName(); });
    this.dialog.append(el("header", "court-lobby-head", this.heading, this.close),
      el("div", "court-lobby-body",
        el("label", "court-lobby-toggle", this.toggle, el("strong", "", "Available to play")),
        el("p", "court-lobby-muted", "Only available guests can see you. You’re hidden while at an active table or away."),
        this.profile, el("p", "court-lobby-muted", "Use a name friends will recognize, not private details. Names aren’t unique."),
        this.linkStatus, this.localStatus, this.actionStatus, this.retry, this.retryAccept, this.proceed,
        this.incoming.root, this.directory.root, this.outgoing.root));
    this.dialog.addEventListener("close", () => {
      if (this.opener?.isConnected && !this.opener.closest("[hidden]")) this.opener.focus({ preventScroll: true });
      else this.trigger.focus({ preventScroll: true });
      this.opener = undefined;
      this.table = undefined;
      this.schedule(interval);
    });
    document.body.append(this.dialog, this.live);
    document.addEventListener("visibilitychange", () => document.hidden ? this.suspend() : this.resume());
    window.addEventListener("pagehide", () => { this.suspend(); void this.sendOff?.(); });
    window.addEventListener("pageshow", event => { if (event.persisted && !document.hidden) this.resume(); });
    window.addEventListener("offline", () => {
      this.suspend();
      this.connection = "disconnected";
      this.paint();
    });
    window.addEventListener("online", () => { if (!document.hidden) this.resume(); });
    this.paint();
    this.refresh();
  }

  open(opener: HTMLButtonElement, choosingPlayers: boolean): void {
    if (this.dialog.open) return;
    this.opener = opener;
    this.table = this.state().table;
    this.refreshName = true;
    text(this.actionStatus, choosingPlayers && this.table ? `Invite guests to your ${catalog[this.table.game].name} table.` : "");
    this.dialog.showModal();
    this.close.focus({ preventScroll: true });
    this.paint();
    this.refresh();
  }

  refresh(): void {
    this.requested = true;
    this.paint();
    this.schedule();
  }

  private get visible(): boolean { return !this.suspended && !document.hidden && navigator.onLine; }
  private get ready(): boolean { return this.visible && this.connection === "online" && !this.acting; }
  private get canAccept(): boolean { return this.state().ready && !this.state().busy; }

  private schedule(delay = 0): void {
    clearTimeout(this.timer);
    if (!this.visible || this.acting || this.poll || (!this.requested && this.connection === "auth") ||
      (!this.state().ready && !this.dialog.open) ||
      (!this.requested && !this.dialog.open && !this.snapshot?.available && !this.snapshot?.incoming.length)) return;
    this.timer = window.setTimeout(() => this.tick(), Math.max(delay, this.retryAt - Date.now()));
  }

  private tick(): void {
    if (!this.visible) return;
    this.requested = false;
    const generation = this.generation;
    this.poll = this.refreshPresence(generation).catch(error => {
      if (this.current(generation)) this.failed(error);
    }).finally(() => {
      this.poll = undefined;
      this.paint();
      this.schedule(this.requested ? 0 : interval);
    });
  }

  private current(generation: number): boolean { return generation === this.generation && this.visible; }

  private async refreshPresence(generation: number): Promise<void> {
    const reauthenticate = this.connection === "auth";
    if (this.dialog.open && (this.refreshName || reauthenticate)) {
      const { displayName } = await this.hooks.openGuest(reauthenticate);
      if (!this.current(generation)) return;
      this.rememberName(displayName);
      this.refreshName = false;
    }
    await this.presence(generation);
  }

  private async presence(generation: number, available?: boolean): Promise<void> {
    await this.off;
    if (!this.current(generation)) return;
    // While a game is opening, inspect preference without advertising a possibly occupied guest.
    const active = this.state().ready;
    this.announced ||= active;
    const request = this.presenceRequest = this.json<Presence>(presencePath, "POST",
      { tabId: this.tabId, active, busy: this.state().busy, ...(available === undefined ? {} : { available }) });
    let result: Presence;
    try { result = await request; }
    finally { this.presenceRequest = undefined; }
    if (typeof result?.available !== "boolean" || !Array.isArray(result.players) ||
      !Array.isArray(result.incoming) || !Array.isArray(result.outgoing)) throw new Error("Invalid presence response");
    if (available !== undefined || result.available && !this.snapshot && this.current(generation)) {
      try {
        const key = preferenceKey(this.hooks.mount);
        if (available !== undefined || localStorage.getItem(key) === null) {
          localStorage.setItem(key, `${result.available ? "+" : "-"}${Date.now() + Math.random()}`);
        }
      } catch { /* Optional wake signal for local-only games. */ }
    }
    if (!this.current(generation)) return;
    const incoming = result.incoming.filter(invite);
    if (incoming.some(item => !this.snapshot?.incoming.some(old => old.id === item.id))) {
      text(this.live, `${incoming.length} incoming ${incoming.length === 1 ? "invite" : "invites"} in the Online lobby.`);
    } else if (!incoming.length) text(this.live, "");
    this.snapshot = { available: result.available, players: result.players.filter(player), incoming, outgoing: result.outgoing.filter(invite) };
    this.connection = "online";
    this.failures = this.retryAt = 0;
  }

  private suspend(): void {
    if (this.suspended) return;
    this.suspended = true;
    this.generation++;
    clearTimeout(this.timer);
    if (!this.announced) return;
    this.announced = false;
    let sending: Promise<void> | undefined;
    const send = this.sendOff = () => sending ??= fetch(this.hooks.mount + presencePath, {
      method: "POST", mode: "same-origin", cache: "no-store", keepalive: true, signal: AbortSignal.timeout(8000),
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ tabId: this.tabId, active: false, busy: this.state().busy }),
    }).then(() => {}, () => {});
    this.off = this.presenceRequest ? this.presenceRequest.then(send, send) : send();
  }

  private resume(): void {
    this.suspended = false;
    this.sendOff = undefined;
    if (this.connection === "online") this.connection = "checking";
    this.refresh();
  }

  private failed(error: unknown): void {
    const problem = code(error);
    if (problem === "not_authenticated") {
      this.snapshot = undefined;
      this.connection = "auth";
      text(this.live, "");
    } else {
      this.connection = problem === "busy" ? "busy" : "disconnected";
      this.retryAt = Date.now() + Math.min(60_000, (problem === "busy" ? 30_000 : interval) * 2 ** this.failures++);
    }
  }

  private rememberName(name: string): void {
    if (this.savedName === undefined || this.name.value === this.savedName) this.name.value = name;
    this.savedName = name;
  }

  private async act(run: (generation: number) => Promise<void>, fallback: string): Promise<void> {
    if (!this.ready) return;
    const generation = this.generation;
    this.acting = true;
    clearTimeout(this.timer);
    text(this.actionStatus, "");
    this.paint();
    try {
      await this.poll;
      if (this.current(generation) && this.connection === "online") await run(generation);
    } catch (error) {
      if (this.current(generation)) {
        const problem = code(error);
        if (!problem || ["busy", "not_authenticated", "unavailable"].includes(problem)) this.failed(error);
        text(this.actionStatus, problem === "already_seated" ? "You already have an active table or seat. That game was left untouched." :
          problem === "invite_unavailable" || problem === "forbidden" ? "This invitation is no longer available. Ask your friend for their full invite link." :
          problem === "not_authenticated" || problem === "busy" ? "" : fallback);
        if (problem === "invite_unavailable") this.requested = true;
      }
    } finally {
      this.acting = false;
      this.paint();
      this.schedule(this.requested ? 0 : interval);
    }
  }

  private saveName(): void {
    const name = this.name.value.trim();
    if (!name || /[\u0000-\u001f\u007f-\u009f]/u.test(name)) {
      text(this.actionStatus, "Enter a display name without control characters.");
      this.name.focus();
      return;
    }
    void this.act(async generation => {
      const { displayName } = await this.hooks.saveName(name);
      if (!this.current(generation)) return;
      this.name.value = displayName;
      this.rememberName(displayName);
      text(this.actionStatus, "Display name saved.");
    }, "Couldn’t save the display name. Check it and try again.");
  }

  private get inviteTable(): InviteTable | undefined {
    const table = this.state().table;
    return this.table?.game === table?.game && this.table?.tableId === table?.tableId ? table : undefined;
  }

  private send(playerId: string): void {
    void this.act(async generation => {
      const table = this.inviteTable;
      if (!table || !this.snapshot?.available || !this.snapshot.players.some(player => player.id === playerId)) {
        text(this.actionStatus, "That guest or table is no longer available.");
        return;
      }
      await this.json(invitesPath, "POST", { playerId, game: table.game, tableId: table.tableId });
      if (!this.current(generation)) return;
      text(this.actionStatus, "Invite sent. You can keep waiting at your table.");
      this.requested = true;
    }, "Couldn’t confirm that the invite was sent. Reconnect and check Sent invites before trying again.");
  }

  private inviteRow(item: Invite, incoming: boolean): HTMLElement {
    const actions = (incoming ? ["accept", "decline", "block"] as const : ["revoke"] as const).map(action =>
      button({ accept: "Accept", decline: "Decline", block: "Block invites", revoke: "Revoke" }[action],
        `court-lobby-button${action === "accept" ? " primary" : ""}`, () => this.respond(item, action)));
    return el("li", "court-lobby-row", el("div", "", el("strong"), el("p", "court-lobby-muted", catalog[item.game].name)),
      el("div", "court-lobby-actions", ...actions));
  }

  private respond(item: Invite, action: "accept" | "decline" | "block" | "revoke", retry = false): void {
    void this.act(async generation => {
      if (!retry && !(action === "revoke" ? this.snapshot?.outgoing : this.snapshot?.incoming)?.some(invite => invite.id === item.id)) {
        text(this.actionStatus, "This invitation is no longer available. Ask your friend for their full invite link.");
        return;
      }
      if (action === "accept" && !this.canAccept) return;
      if (action === "accept") this.unconfirmed = item;
      let result: unknown;
      try { result = await this.json<unknown>(`${invitesPath}/${encodeURIComponent(item.id)}/${action}`, "POST", {}); }
      catch (error) {
        if (action === "accept" && ["already_seated", "forbidden", "invite_unavailable", "invalid_request"].includes(code(error))) {
          this.unconfirmed = undefined;
        }
        throw error;
      }
      if (action === "accept") {
        this.unconfirmed = undefined;
        if (!isInviteTable(result) || result.game !== item.game) {
          if (this.current(generation)) text(this.actionStatus, "Couldn’t verify the accepted table. Ask your friend for their full invite link.");
        } else {
          this.accepted = result;
          if (this.current(generation) && this.dialog.open) this.navigate();
        }
      } else if (this.current(generation)) {
        if (this.unconfirmed?.id === item.id) this.unconfirmed = undefined;
        text(this.actionStatus, { decline: "Invite declined.", block: "Invites from this guest are blocked.", revoke: "Invite revoked." }[action]);
      }
      this.requested = true;
    }, action === "accept" ?
      "Couldn’t confirm your seat. Reconnect to check the invite, or ask your friend for their full invite link." :
      "Couldn’t update the invite. Reconnect and check it before trying again.");
  }

  private navigate(): void {
    if (!this.accepted || !this.visible || !this.canAccept) return;
    try { openAcceptedTable(this.hooks.mount, this.accepted); }
    catch { text(this.actionStatus, "This browser couldn’t open the accepted table. Try again or ask your friend for their full invite link."); }
  }

  private paint(): void {
    const { available, players, incoming, outgoing } = this.snapshot ?? empty;
    this.trigger.dataset.incoming = incoming.length ? String(incoming.length) : "";
    this.trigger.setAttribute("aria-label", incoming.length ?
      `Online lobby, ${incoming.length} incoming ${incoming.length === 1 ? "invite" : "invites"}` : "Online lobby");
    if (!this.dialog.open) return;
    text(this.linkStatus, !navigator.onLine || this.connection === "disconnected" ? this.snapshot ?
      "Connection lost. Showing the last lobby update." : "Can’t reach the lobby. Check your connection and retry." :
      this.connection === "auth" ? "Your guest session couldn’t be verified. Retry to reconnect." :
      this.connection === "busy" ? "The lobby is busy. We’ll retry shortly." :
      this.connection === "checking" ? "Checking the lobby…" : "");
    this.retry.hidden = this.connection === "online" || this.connection === "checking";
    this.retry.disabled = !navigator.onLine || this.acting || this.connection === "busy" && Date.now() < this.retryAt;
    const ready = this.ready;
    this.toggle.checked = available;
    this.toggle.disabled = !this.snapshot;
    this.toggle.setAttribute("aria-disabled", String(!ready));
    this.name.disabled = this.save.disabled = !ready || this.savedName === undefined;
    this.proceed.hidden = !this.accepted;
    this.proceed.disabled = !this.visible || !this.canAccept;
    this.retryAccept.hidden = !this.unconfirmed;
    this.retryAccept.disabled = !ready || !this.canAccept;
    const inviteTable = this.inviteTable;
    const noRows = !this.snapshot ? "Waiting for a lobby update." : !available ?
      "Turn on Available to play to see other guests." : "No guests are available right now.";
    this.directory.update(available ? players : [], noRows, ready && !!inviteTable, !!inviteTable);
    this.incoming.update(incoming, this.snapshot ? "No incoming invites." : "Waiting for a lobby update.", ready && this.canAccept);
    for (const control of this.incoming.root.querySelectorAll("button:not(.primary)")) control.setAttribute("aria-disabled", String(!ready));
    this.outgoing.update(outgoing, this.snapshot ? "No sent invites." : "Waiting for a lobby update.", ready);
    text(this.localStatus, this.state().busy ? "Your current game stays open. You can’t accept another table while seated." :
      this.table && !inviteTable ? "This table is no longer ready for invites. The share link remains available while it’s open." : "");
  }
}
