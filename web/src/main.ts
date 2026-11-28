import "./styles.css";
import { LanternAudio } from "./audio";
import type { AudioChoices } from "./audio";
import { Modal } from "./dialog";
import { button, el, text } from "./dom";
import type { ChatPanel } from "./mahjong-forms";
import { appPath } from "./paths";
import { rememberTable, tableEntry } from "./portal/invite-handoff";
import { onlineLobby } from "./portal/social-entry";
import { api, HttpError } from "./protocol";
import type { Action, Command, Guest, Preferences, Snapshot } from "./protocol";
import { HomeScreen, WaitingRoom, iconButton } from "./screens";
import { errorMessage, TableSession } from "./session";
import type { Connection } from "./session";
import type { TableCue, TableView } from "./table";

const connectionText: Record<Connection, string> = {
  connecting: "Connecting", syncing: "Syncing", online: "Online", offline: "Offline",
  "taken-over": "Other tab", unsupported: "Update needed",
};

const nonDiscard = /[\u0000-\u001f\u007f-\u009f]/u;
const mahjongPath = `${appPath}mahjong/`;
const fromPortal = document.querySelector("#app")!.hasAttribute("data-portal-mahjong") && location.pathname.startsWith(mahjongPath);
const newTableRules = fromPortal ? "changsha-two-bird-v1" : undefined;
const handoffError = "Couldn’t verify the accepted Mahjong seat. Try again, or ask your friend for their full invite link.";

class LanternTable {
  private readonly root = el("div", "shell");
  private readonly main = el("main");
  private readonly connectionLabel = el("span");
  private readonly connection = el("span", "connection", el("i"), this.connectionLabel);
  private readonly noticeText = el("p");
  private readonly retryButton = button("Reconnect now", "btn secondary", () => this.retry());
  private readonly dismissButton = iconButton("close", "Dismiss message", () => this.showNotice(""));
  private readonly notice = el("div", "notice", this.noticeText, el("div", "notice-actions", this.retryButton, this.dismissButton));
  private readonly audio = new LanternAudio(fromPortal ? `${mahjongPath}audio` : undefined);
  private readonly modal = new Modal(() => this.modalClosed());
  private readonly chatButton = iconButton("chat", "Table chat", () => this.openChat());
  private readonly settingsButton = iconButton("settings", "Settings", () => this.openSettings());
  private chat?: ChatPanel;
  private readonly home = new HomeScreen({
    quick: () => void this.enter({ op: "createRoom",
      payload: { visibility: "invite", matchHands: 1, fillBots: true, rulesVersion: newTableRules } }),
    create: () => this.openCreate(),
    resume: () => this.session.resume(),
    join: inviteCode => void this.enter({ op: "joinRoom", payload: { inviteCode } }),
    publicJoin: (roomId, takeBot) => void this.enter({ op: "joinRoom", payload: takeBot ? { roomId, takeBot: true } : { roomId } }),
    refresh: () => void this.refresh(),
  }, fromPortal);
  private readonly waiting = new WaitingRoom({
    ready: ready => { const room = this.bound(); if (room) this.command({ op: "setReady", payload: { ...room, ready } }); },
    bot: (seat, enabled) => { const room = this.bound(); if (room) this.command({ op: "setBot", payload: { ...room, seat, enabled } }); },
    start: () => { const room = this.bound(); if (room) this.command({ op: "start", payload: room }); },
    invite: () => this.openInvite(),
    invitePlayers: fromPortal ? opener => this.social?.open(opener, true) : undefined,
    leave: () => this.leave(),
  });
  private readonly session = new TableSession({
    change: event => this.render(event?.effects ?? false),
    notice: (message, command) => {
      this.showNotice(message);
      if (fromPortal && command?.op === "joinRoom" && command.payload.roomId) void this.refresh(false);
    },
    committed: command => this.committed(command),
    guestRefresh: guest => this.setGuest(guest),
  });

  private guest: Guest | null = null;
  private opening?: Promise<Guest>;
  private readonly social?: ReturnType<typeof onlineLobby>;
  private accepted?: string;
  private handoffFailed = false;
  private booting = false;
  private bootFailed = false;
  private profileBusy = false;
  private refreshBusy = false;
  private scene: "home" | "waiting" | "loading" | "play" | null = null;
  private table?: TableView;
  private tableLoad?: Promise<void>;
  private previous: Snapshot | null = null;
  private connectionBefore: Connection = "connecting";
  private resultKey = "";
  private resultReveal = 0;
  private restartPending = false;
  private message = "";
  private overlayActions?: (available: boolean) => void;

  constructor(mount: HTMLElement) {
    const brand = el(fromPortal ? "a" : "div", "brand", el("span", "brand-symbol"),
      el("div", "", el("p", "brand-name", "Jade Court"), el("p", "brand-kicker", "Changsha Mahjong")));
    if (brand instanceof HTMLAnchorElement) {
      brand.href = appPath;
      brand.setAttribute("aria-label", "Back to all games in Jade Court");
    }
    const rulesButton = iconButton("book", "Changsha rules", () => void this.openForm(({ rulesContent }) =>
      this.modal.show("rules", "Changsha rules", rulesContent(this.session.snapshot?.room.rulesVersion), rulesButton)));
    const actions = el("div", "header-actions", this.connection, rulesButton, this.chatButton, this.settingsButton);
    if (fromPortal) {
      const control = iconButton("globe", "Online lobby", () => {});
      control.classList.add("court-lobby-trigger");
      actions.insertBefore(control, rulesButton);
      this.social = onlineLobby(control, { mount: appPath, openGuest: refresh => this.openGuest(refresh),
        saveName: async displayName => this.setGuest(await api.profile({ displayName })) });
      window.addEventListener("hashchange", () => {
        if (this.accepted && new URLSearchParams(location.hash.slice(1)).has("invite")) location.reload();
      });
    }
    const header = el("header", "site-header", brand, actions);
    this.connection.setAttribute("role", "status");
    this.noticeText.setAttribute("role", "status");
    this.root.append(header, this.notice, this.main, this.modal.root);
    this.main.tabIndex = -1;
    mount.append(this.root);
    window.setInterval(() => {
      if (this.session.snapshot?.game) this.table?.tick(this.session.now);
    }, 140);
    window.setInterval(() => {
      if (!document.hidden && this.scene === "home" && this.session.status === "online") void this.refresh(false);
    }, 15000);
    void this.boot();
  }

  private async boot(): Promise<void> {
    if (this.booting) return;
    this.booting = true;
    this.bootFailed = false;
    this.render();
    try {
      const entry = fromPortal ? tableEntry(appPath, "mahjong") : undefined;
      this.accepted = entry?.accepted ? entry.tableId : undefined;
      const guest = await this.openGuest(true);
      if (this.accepted && guest.resumeRoomId !== this.accepted) {
        this.handoffFailed = true;
        this.showNotice(handoffError);
        return;
      }
      if (entry && entry.tableId !== guest.resumeRoomId) rememberTable(appPath, "mahjong", null);
      this.session.start(guest, !fromPortal || entry?.tableId === guest.resumeRoomId);
      void this.refresh();
    } catch (error) {
      this.bootFailed = true;
      this.showNotice(error instanceof HttpError ? errorMessage(error.code) : "Couldn’t reach the table server. Check your connection and try again.");
    } finally {
      this.booting = false;
      this.render();
    }
  }

  private retry(): void {
    if (this.handoffFailed) { location.reload(); return; }
    this.showNotice("");
    if (this.bootFailed) void this.boot();
    else this.session.retry();
  }

  private openGuest(refresh = false): Promise<Guest> {
    return this.guest && !refresh ? Promise.resolve(this.guest) : this.opening ??=
      api.guest().then(guest => this.setGuest(guest)).finally(() => { this.opening = undefined; });
  }

  private setGuest(guest: Guest): Guest {
    this.guest = guest;
    this.home.identity(guest);
    this.audio.configure(guest.preferences);
    return guest;
  }

  private async refresh(showLoading = true): Promise<void> {
    if (!this.guest || this.refreshBusy) return;
    this.refreshBusy = true;
    if (showLoading) this.home.rooms({ kind: "loading" });
    try {
      const result = await api.rooms();
      this.home.rooms({ kind: "ready", items: result.rooms });
    } catch {
      this.home.rooms({ kind: "error" });
    } finally {
      this.refreshBusy = false;
    }
  }

  private bound(): { roomId: string; expectedRoomRevision: number } | null {
    const state = this.session.snapshot;
    return state ? { roomId: state.roomId, expectedRoomRevision: state.roomRevision } : null;
  }

  private async enter(action: Action): Promise<void> {
    if (!this.session.available || !this.guest || this.profileBusy || this.session.savedRoomId) return;
    const name = this.home.name.value.trim() || this.guest.displayName;
    if (nonDiscard.test(name)) {
      this.showNotice("Use a display name without control characters.");
      return;
    }
    if (name !== this.guest.displayName) {
      this.profileBusy = true;
      this.render();
      try {
        this.setGuest(await api.profile({ displayName: name }));
      } catch (error) {
        this.showNotice(error instanceof HttpError ? errorMessage(error.code) : "Couldn’t save your display name. Please try again.");
        return;
      } finally {
        this.profileBusy = false;
        this.render();
      }
    }
    this.command(action);
  }

  private command(action: Action): boolean {
    if (!this.session.available) return false;
    this.showNotice("");
    return this.session.send(action);
  }

  private committed(command: Command): void {
    if (command.op === "chat") this.chat?.committed(command.payload.text);
    if (command.op === "joinRoom" && command.payload.inviteCode) {
      this.home.consumedInvitation();
      if (new URLSearchParams(location.hash.slice(1)).has("invite")) {
        history.replaceState(history.state, "", location.pathname + location.search);
      }
    }
    if (command.op === "leaveRoom") {
      this.modal.close();
      this.chat?.reset();
      void this.refresh();
    }
    if (command.op === "restartMatch") this.restartPending = true;
  }

  private leave(): void {
    const room = this.bound();
    if (room) this.command({ op: "leaveRoom", payload: room });
  }

  private showNotice(message: string): void {
    this.message = message;
    if (this.modal.root.open) this.modal.message(message);
    this.paintConnection();
  }

  private paintConnection(): void {
    const status = this.bootFailed ? "offline" : this.session.status;
    this.connection.dataset.status = status;
    text(this.connectionLabel, connectionText[status]);
    const passive = status === "offline" ? this.session.releasingSeat ?
      "Connection lost. Checking whether your seat was handed over." : "Connection lost. Your seat is kept while we reconnect." :
      status === "taken-over" ? "This seat is open in another tab. This tab will not reconnect on its own." : "";
    const message = this.message || passive;
    this.retryButton.hidden = !this.bootFailed && !this.handoffFailed && status !== "offline" && status !== "taken-over";
    text(this.retryButton, status === "taken-over" ? "Use this tab" : this.bootFailed || this.handoffFailed ? "Try again" : "Reconnect now");
    this.notice.hidden = !message;
    this.dismissButton.hidden = !this.message || status === "taken-over" || this.bootFailed;
    text(this.noticeText, message);
  }

  private render(effect = false): void {
    const state = this.session.snapshot;
    if (fromPortal) {
      if (this.accepted && this.session.status === "online" && !this.session.savedRoomId) {
        this.handoffFailed = true;
        this.message = handoffError;
      }
      if (state && this.previous?.roomId !== state.roomId) {
        rememberTable(appPath, "mahjong", state.roomId, this.accepted === state.roomId);
        this.accepted = undefined;
      } else if (this.previous && (!state || this.connectionBefore !== "taken-over" && this.session.status === "taken-over")) {
        rememberTable(appPath, "mahjong", null);
      }
    }
    const available = this.session.available && !this.profileBusy && !this.handoffFailed;
    const restarted = this.restartPending && available;
    if (restarted) this.restartPending = false;
    this.social?.update({
      ready: !this.handoffFailed && (!!state && state.room.status !== "complete" || this.session.available),
      busy: !!state && state.room.status !== "complete",
      table: available && state?.room.status === "waiting" && state.room.hostSeat === state.self.seat &&
        state.room.seats.some(seat => seat.kind === "open")
        ? { game: "mahjong", tableId: state.roomId } : undefined,
    });
    const waiting = state?.room.status === "waiting";
    if (waiting || state?.game) this.tableLoad ??= this.loadTable();
    const scene = waiting ? "waiting" : state?.game ? this.table ? "play" : "loading" : "home";
    const interrupted = this.connectionBefore === "online" && this.session.status !== "online" ||
      this.scene === "play" && scene !== "play";
    if (interrupted || restarted) {
      this.table?.clearEffects();
      this.audio.clearEffects();
    }
    this.connectionBefore = this.session.status;
    this.paintConnection();
    this.chatButton.hidden = !state;
    this.settingsButton.disabled = !this.guest;
    this.home.controls(available, !!this.session.savedRoomId, !!this.guest,
      this.profileBusy ? "Saving your display name…" : this.booting ? "Opening a guest session…" :
        this.session.status === "syncing" && !state ? "Checking your saved seat…" : "");

    if (this.scene !== scene) {
      const initial = this.scene === null;
      this.scene = scene;
      this.main.replaceChildren(scene === "home" ? this.home.root : scene === "waiting" ? this.waiting.root :
        scene === "play" ? this.table!.root : el("p", "note", "Opening the table…"));
      if (this.modal.kind === "create" || this.modal.kind === "invite" || this.modal.kind === "results" || this.modal.kind === "match" ||
        (scene === "home" && this.modal.kind === "chat")) {
        this.modal.close();
      }
      if (!initial && !this.modal.root.open) {
        this.main.focus({ preventScroll: true });
        window.scrollTo({ top: 0, behavior: "instant" });
      }
      if (scene === "home" && !initial) void this.refresh();
    }

    let cue: TableCue | undefined;
    if (state) {
      if (!this.previous || this.previous.roomId !== state.roomId) this.chat?.reset();
      this.chat?.update(state.room.chat ?? [], state.self.seat, available);
      if (scene === "waiting") this.waiting.update(state, available);
      // Remember the same-room waiting projection before the first live hand.
      if (state.game || waiting) cue = this.table?.update(state, available, this.session.now, effect && !restarted);
    }
    if (restarted && this.modal.kind === "match") this.modal.close();
    this.audio.setTableActive(scene === "play" && this.session.status === "online" && !state?.room.paused);
    if (cue) this.audio.play(cue);
    this.overlayActions?.(available);

    const settlement = scene === "play" && state?.game?.settlement;
    const key = settlement ? `${state!.roomId}:${state!.game!.handNumber}:${state!.gameRevision}` : "";
    if (key !== this.resultKey) {
      this.cancelResultReveal();
      this.resultKey = key;
      if (key && !this.modal.root.open) {
        if ((cue === "win" || cue === "exhausted") && this.table?.motionEnabled) {
          this.resultReveal = window.setTimeout(() => {
            this.resultReveal = 0;
            if (!this.modal.root.open) this.openTableDialog(true);
          }, 540);
        } else this.openTableDialog(true);
      }
      if (!key && this.modal.kind === "results") this.modal.close();
    } else if (this.resultReveal && (interrupted || !this.table?.motionEnabled)) {
      this.finishResultReveal();
    }
    this.previous = state;
  }

  private cancelResultReveal(): void {
    clearTimeout(this.resultReveal);
    this.resultReveal = 0;
  }

  private finishResultReveal(): void {
    if (!this.resultReveal) return;
    this.cancelResultReveal();
    if (!this.modal.root.open) this.openTableDialog(true);
  }

  private modalClosed(): void {
    this.overlayActions = undefined;
    this.audio.changed = undefined;
    this.audio.preview();
  }

  private setOverlayActions(controls: HTMLButtonElement[]): void {
    this.overlayActions = available => controls.forEach(control => { control.disabled = !available; });
    this.overlayActions(this.session.available && !this.profileBusy);
  }

  private async loadTable(): Promise<void> {
    try {
      const { TableView } = await import("./table");
      this.table = new TableView({
        choose: option => {
          const state = this.session.snapshot;
          const window = state?.game?.window;
          if (state && window) this.command({
            op: "choose", payload: { roomId: state.roomId, windowId: window.windowId,
              anchorGameRevision: window.anchorGameRevision, optionId: option.optionId },
          });
        },
        selected: () => this.audio.play("select"),
        results: () => this.openTableDialog(true, true),
        manage: () => this.openTableDialog(),
        motionStopped: () => this.finishResultReveal(),
        pause: () => {
          const room = this.bound();
          if (room) this.command({ op: "setPaused", payload: { ...room, paused: !this.session.snapshot?.room.paused } });
        },
      });
      this.render();
    } catch { this.showNotice("The table couldn’t load. Reload this page to try again."); }
  }

  private async openForm(show: (forms: typeof import("./mahjong-forms")) => void, replace = false): Promise<void> {
    try {
      const forms = await import("./mahjong-forms");
      if (replace || !this.modal.root.open) show(forms);
    } catch { this.showNotice("This dialog couldn’t load. Please try again."); }
  }

  private openCreate(): void {
    void this.openForm(({ showCreate }) => {
      if (!this.session.available || this.session.savedRoomId) return;
      this.setOverlayActions([showCreate(this.modal, newTableRules, fromPortal, payload => void this.enter({ op: "createRoom", payload }))]);
    });
  }

  private async openSettings(): Promise<void> {
    try {
      const { showSettings } = await import("./settings");
      if (!this.guest || this.modal.root.open) return;
      this.overlayActions = showSettings({
        guest: this.guest, snapshot: () => this.session.snapshot,
        available: this.session.available && !this.profileBusy, portal: fromPortal,
        audio: this.audio, modal: this.modal, opener: this.settingsButton,
        leave: payload => this.command({ op: "leaveRoom", payload }),
        save: (name, prefs, form, control, choices) => void this.saveSettings(name, prefs, form, control, choices),
      });
    } catch { this.showNotice("Settings couldn’t load. Please try again."); }
  }

  private async saveSettings(name: string, preferences: Preferences, form: HTMLElement, save: HTMLButtonElement,
    choices?: AudioChoices): Promise<void> {
    if (this.profileBusy) return;
    this.profileBusy = true;
    save.disabled = true;
    this.modal.message("");
    this.render();
    try {
      const guest = await api.profile({ displayName: name, preferences });
      const remembered = !choices || this.audio.saveChoices(choices);
      this.setGuest(guest);
      if (fromPortal) {
        try { localStorage.setItem(`${appPath}court-music`, preferences.music ? "on" : "off"); } catch { /* Optional. */ }
      }
      if (form.isConnected) this.modal.message(remembered ? "Settings saved." : "Settings saved. This browser can’t remember the audio choices after you leave.");
    } catch (error) {
      if (form.isConnected) this.modal.message(error instanceof HttpError ? errorMessage(error.code) : "Couldn’t save settings. Please try again.");
    } finally {
      this.profileBusy = false;
      save.disabled = false;
      this.render();
    }
  }

  private openChat(): void {
    void this.openForm(({ ChatPanel }) => {
      if (!this.session.snapshot) return;
      this.chat ??= new ChatPanel(message => {
        const state = this.session.snapshot;
        if (state) this.command({ op: "chat", payload: { roomId: state.roomId, text: message } });
      });
      this.modal.show("chat", "Table chat", this.chat.root, this.chatButton);
      this.render();
    });
  }

  private openInvite(): void {
    void this.openForm(({ showShareLink }) =>
      showShareLink(this.modal, fromPortal ? mahjongPath : appPath, this.session.snapshot));
  }

  private openTableDialog(results = false, explicit = false): void {
    if (!this.table) return;
    const opener = this.table.optionsButton;
    if (results) this.cancelResultReveal();
    const key = this.resultKey;
    void this.openForm(({ showTableDialog }) => {
      if (results ? key !== this.resultKey : !this.session.available) return;
      if (this.modal.kind === "settings") this.modalClosed();
      showTableDialog({
        modal: this.modal, opener, snapshot: () => this.session.snapshot,
        command: action => this.command(action),
        actions: controls => this.setOverlayActions(controls),
      }, results);
    }, explicit);
  }
}

new LanternTable(document.querySelector<HTMLElement>("#app")!);
