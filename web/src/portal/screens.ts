import type { GameDefinition, Guest, Move, Table } from "./api";
import { side, sideName, tableau } from "./art";
import { BoardView } from "./board";
import { boardIds, catalog } from "./catalog";
import type { BoardId, GameId } from "./catalog";
import { button, el, link, text } from "./dom";

export function lobby(mount: string): HTMLElement {
  const h1 = el("h1", "", "Find your ", el("em", "", "next move."));
  const hero = el("section", "lobby-hero", el("div", "hero-copy",
    el("p", "eyebrow", "The board game parlor"),
    h1,
    el("p", "intro", "Six classic games, one quiet court. Play a bot at your own pace, or invite a friend to a private table."),
    el("div", "hero-actions", link("Choose a game ↓", "#games", "button primary"),
      link("Play Mahjong", `${mount}mahjong/`, "button secondary")),
  ), el("div", "hero-study"));
  hero.setAttribute("aria-label", "Welcome to Jade Court");
  const section = el("section", "catalog");
  section.id = "games";
  const heading = el("h2", "", "Choose a game");
  heading.id = "catalog-heading";
  section.setAttribute("aria-labelledby", heading.id);
  const grid = el("div", "catalog-grid");
  const games: GameId[] = ["mahjong", ...boardIds];
  for (const game of games) {
    const info = catalog[game];
    const card = el("a", "game-card", tableau(game), el("div", "game-card-body",
      el("p", "card-meta", game === "mahjong" ? "Flagship · 4 players" : info.seats),
      el("h3", "", info.name),
      el("p", "card-description", info.description),
      el("span", "card-action", game === "mahjong" ? "Take a seat" : "Open game", el("span", "arrow", "↗")),
    ));
    card.href = `${mount}${game}/`;
    card.setAttribute("aria-label", info.invitation);
    grid.append(card);
  }
  section.append(el("div", "section-title", el("div", "", el("p", "eyebrow", "The collection"), heading),
    el("p", "section-caption", "Six games, no account required.")), grid);
  return el("div", "lobby", hero, section);
}

function field(label: string, input: HTMLInputElement | HTMLSelectElement): HTMLLabelElement {
  return el("label", "field", el("span", "", label), input);
}

export class SetupView {
  readonly root = el("div", "setup");
  readonly heading: HTMLHeadingElement;
  readonly name = el("input");
  readonly variant = el("select");
  readonly players = el("select");
  private readonly variantField = field("Variant", this.variant);
  private readonly playerField = field("Players", this.players);
  private readonly options = el("div", "setup-options", this.variantField, this.playerField);
  private readonly bots = button("Play a bot", "button primary");
  private readonly friends = button("Invite a friend", "button secondary");
  private readonly join = button("Join table", "button primary");
  private readonly retry = button("Try again", "button secondary");
  private readonly progress = el("p", "setup-progress");
  private readonly nameHint = el("p", "field-hint", "Optional. Remembered in this browser.");

  constructor(game: BoardId, invitation: boolean, submit: (bots: boolean) => void, retry: () => void) {
    const info = catalog[game];
    this.heading = el("h1", "", info.name);
    this.heading.tabIndex = -1;
    const kicker = el("p", "eyebrow", "The board game parlor");
    this.name.placeholder = "Guest";
    this.name.setAttribute("autocomplete", "nickname");
    this.name.maxLength = 24;
    this.nameHint.id = "profile-hint";
    this.name.setAttribute("aria-describedby", this.nameHint.id);
    this.progress.setAttribute("role", "status");
    this.bots.type = this.friends.type = this.join.type = "submit";
    this.bots.value = "bots";
    this.retry.addEventListener("click", retry);
    this.retry.hidden = true;
    this.players.addEventListener("change", () => this.setModeLabels());
    const controls = invitation ? [this.join] : [this.bots, this.friends];
    const form = el("form", "setup-form", el("p", "eyebrow", invitation ? "Private invitation" : "New table"),
      el("h2", "", invitation ? "A friend saved you a seat." : "Set the table."),
      el("p", "muted", invitation ? "Join with the name your friends will see." :
        "Start right away against a bot, or get a private link to share."),
      el("div", "profile-field", field("Display name", this.name), this.nameHint),
      ...(invitation ? [] : [this.options]),
      el("div", "setup-actions", ...controls, this.retry), this.progress);
    form.addEventListener("submit", event => {
      event.preventDefault();
      if (!controls.some(control => !control.disabled)) return;
      submit(!invitation && (!(event.submitter instanceof HTMLButtonElement) || event.submitter.value === "bots"));
    });
    const study = el("aside", "setup-study", tableau(game), el("div", "study-caption",
      el("span", "eyebrow", "On the board"), el("span", "", info.seats)));
    this.root.append(el("div", "setup-copy", kicker, this.heading, el("p", "intro", info.description), form), study);
    this.state(false, true, false);
  }

  setDefinition(definition?: GameDefinition): void {
    this.variant.replaceChildren(...(definition?.variants ?? []).map(({ id, label }) => new Option(label, id)));
    this.players.replaceChildren(...(definition?.playerCounts ?? []).map(count => new Option(`${count} players`, String(count))));
    this.variantField.hidden = this.variant.options.length < 2;
    this.playerField.hidden = this.players.options.length < 2;
    this.options.hidden = this.variantField.hidden && this.playerField.hidden;
    this.setModeLabels();
  }

  remember(guest: Guest): void {
    this.name.value = guest.displayName === "Guest" ? "" : guest.displayName;
  }

  state(ready: boolean, busy: boolean, canRetry: boolean, message = ""): void {
    const unavailable = !ready || busy;
    this.name.disabled = this.variant.disabled = this.players.disabled = unavailable;
    this.bots.disabled = this.friends.disabled = this.join.disabled = unavailable;
    this.retry.hidden = !canRetry;
    this.retry.disabled = busy;
    text(this.progress, message);
  }

  private setModeLabels(): void {
    const many = Number(this.players.value) > 2;
    text(this.bots, many ? "Play with bots" : "Play a bot");
    text(this.friends, many ? "Invite friends" : "Invite a friend");
  }
}

export class TableView {
  readonly root = el("div", "match");
  readonly heading: HTMLHeadingElement;
  readonly board: BoardView;
  readonly retry = button("Reconnect now", "button secondary");
  private readonly newGame = button("Set up another game", "button primary");
  private readonly turn = el("strong", "turn-text");
  private readonly badge = el("p", "turn-badge", el("span", "turn-light"), this.turn);
  private readonly note = el("p", "match-note");
  private readonly last = el("p", "last-move");
  private readonly detail = el("p", "position-detail");
  private readonly variant = el("p", "match-variant");
  private readonly seats = el("ol", "seats");
  private readonly count = el("p", "seat-count");
  private readonly seatsSummary = el("section", "match-card seats-card", el("h2", "", "At this table"), this.count, this.seats);
  private readonly inviteInput = el("input");
  private readonly copy = button("Copy invite link", "button primary", () => void this.copyInvite());
  private readonly copied = el("p", "copy-status");
  private readonly share = el("section", "match-card share-card", el("p", "eyebrow", "Private table"),
    el("h2", "", "Send the invitation"), el("p", "muted", "Anyone with this link can take an open seat."),
    field("Invite link", this.inviteInput), this.copy, this.copied);
  private readonly art: HTMLElement;
  private seatKey = "";

  constructor(private readonly game: BoardId, private readonly href: string, send: (move: Move) => void, retry: () => void, newGame: () => void) {
    this.heading = el("h1", "", catalog[game].name);
    this.heading.tabIndex = -1;
    this.board = new BoardView(game, send);
    this.art = el("div", "waiting-study", tableau(game));
    this.inviteInput.readOnly = true;
    this.inviteInput.spellcheck = false;
    this.inviteInput.dir = "ltr";
    this.copied.setAttribute("role", "status");
    this.turn.setAttribute("role", "status");
    this.retry.addEventListener("click", retry);
    this.newGame.addEventListener("click", newGame);
    const head = el("header", "match-head",
      el("div", "match-title", el("p", "eyebrow", "Jade Court table"), this.heading, this.variant),
      el("div", "match-state", this.badge, this.note, this.retry));
    const rail = el("aside", "match-rail", this.share, this.seatsSummary,
      el("section", "match-card game-notes", el("h2", "", "Game notes"), this.last, this.detail, this.newGame));
    this.root.append(el("div", "match-layout", head, this.board.root, this.art, rail));
  }

  show(table: Table, variant: string, online: boolean, busy: boolean, reset: boolean): void {
    const { position, seat, status } = table;
    const mine = status === "playing" && position?.turn === seat;
    const seatName = (id: number) => table.seats.find(item => item.seat === id)?.name || `Seat ${id + 1}`;
    this.root.dataset.state = status;
    this.badge.dataset.signal = !online ? "offline" : mine ? "turn" : status;
    const winners = position?.winners ?? [];
    const outcome = winners.length === 0 ? "Draw." : winners.length > 1 ? `${winners.map(seatName).join(" and ")} tie.` :
      winners[0] === seat ? "You won." : `${seatName(winners[0])} wins.`;
    text(this.turn, !online ? "Reconnecting…" : status === "waiting" ? "Waiting for players" :
      status === "finished" ? outcome : mine ? "Your turn" :
      position?.turn != null ? `${seatName(position.turn)}’s turn` : "Game in progress");
    text(this.note, !online ? status === "waiting" ? "Your seat is saved. We’ll check whether your friends have joined." :
      "Your last board is still here. We’ll check for updates." :
      status === "waiting" ? "The game starts when every seat is filled." :
      status === "finished" ? "Game over" : busy ? "Sending your move…" : mine ? "Make your next move." : "Watch the board for the next move.");
    text(this.variant, `${variant} · ${table.seats.length} players`);
    text(this.count, `${table.seats.filter(item => item.occupied).length} of ${table.seats.length} seats filled`);
    text(this.last, position?.lastMove ? `Last move: ${position.lastMove.label}` : "No moves yet.");
    text(this.detail, position?.detail ?? "");
    this.detail.hidden = !position?.detail;
    this.retry.hidden = online;
    this.retry.disabled = busy;
    this.seatsSummary.hidden = false;
    this.newGame.hidden = status !== "finished";
    this.board.root.hidden = !position;
    this.art.hidden = !!position;
    this.last.parentElement!.hidden = !position;
    if (position) this.board.update(position, mine, online && !busy, reset);

    const key = JSON.stringify([table.seats, seat, position?.turn, position?.scores]);
    if (key !== this.seatKey) {
      this.seatKey = key;
      const seats = table.seats.map(item => {
        const chip = el("span", "seat-chip");
        if (item.occupied) chip.dataset.side = side(this.game, item.seat);
        const label = [
          item.seat === seat ? "You" : item.bot ? "Bot" : "",
          item.occupied ? sideName(this.game, item.seat) : `Seat ${item.seat + 1}`,
        ].filter(Boolean).join(" · ");
        const score = position?.scores?.[item.seat];
        const row = el("li", "seat", chip, el("div", "seat-label",
          el("strong", "", item.occupied ? seatName(item.seat) : "Open seat"), el("small", "", label)));
        row.toggleAttribute("data-current", position?.turn === item.seat);
        if (score != null) row.append(el("span", "seat-score", `${score} pts`));
        return row;
      });
      this.seats.replaceChildren(...seats);
    }

    this.share.hidden = status !== "waiting" || !table.invite;
    if (this.share.hidden) {
      if (this.share.contains(document.activeElement)) this.heading.focus();
      return;
    }
    const invitation = new URL(this.href, location.origin);
    invitation.hash = new URLSearchParams({ invite: table.invite! }).toString();
    if (this.inviteInput.value !== invitation.href) {
      this.inviteInput.value = invitation.href;
      text(this.copied, "");
    }
  }

  private async copyInvite(): Promise<void> {
    try {
      await navigator.clipboard.writeText(this.inviteInput.value);
      if (!this.share.hidden) text(this.copied, "Link copied.");
    } catch {
      if (this.share.hidden) return;
      this.inviteInput.focus();
      this.inviteInput.select();
      this.inviteInput.setSelectionRange(0, this.inviteInput.value.length);
      text(this.copied, "Link selected. Copy it to send your invitation.");
    }
  }
}
