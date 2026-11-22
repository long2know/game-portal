import type { Modal } from "./dialog";
import { button, el, reconcile, winds } from "./dom";
import type { ChatMessage, Face, Payloads, Seat, Snapshot } from "./protocol";
import { field, submit, toggle } from "./screens";
import { setMelds } from "./table";
import { tileLabel } from "./tile";

export function showCreate(modal: Modal, enter: (payload: Payloads["createRoom"]) => void): HTMLButtonElement {
  const visibility = el("select", "", new Option("Invite only", "invite"), new Option("Public", "public"));
  const hands = el("select", "", el("option", "", "One hand"), el("option", "", "Four hands"));
  const bots = toggle("Fill the other three seats with bots", false);
  const create = submit("Create table");
  const form = el("form", "form", field("Who can join?", visibility), field("Match length", hands), bots.field,
    el("p", "note", "You can also add or remove bots from the waiting room."), el("div", "row-actions", create));
  form.addEventListener("submit", event => {
    event.preventDefault();
    enter({ visibility: visibility.selectedIndex === 0 ? "invite" : "public",
      matchHands: hands.selectedIndex === 0 ? 1 : 4, fillBots: bots.input.checked });
  });
  modal.show("create", "Create a table", form);
  return create;
}

export function showShareLink(modal: Modal, path: string, inviteCode: string): void {
  const link = new URL(path, location.origin);
  link.hash = new URLSearchParams({ invite: inviteCode }).toString();
  const input = el("input");
  input.readOnly = true;
  input.value = link.href;
  const copy = button("Copy invite link", "btn primary", async () => {
    try {
      await navigator.clipboard.writeText(link.href);
      if (modal.kind === "invite") modal.message("Link copied.");
    } catch {
      if (modal.kind !== "invite") return;
      input.focus();
      input.select();
      modal.message("Link selected. Copy it to send your invitation.");
    }
  });
  modal.show("invite", "Invite friends", el("div", "form",
    el("p", "detail", "Send this link to your friends. They can open it and choose Join."), field("Invite link", input), copy));
}

export function rulesContent(): HTMLElement {
  return el("div", "form rules-content",
    el("p", "", "This table uses the provisional changsha-v1 profile. Local variations exist."),
    el("h3", "", "The table"),
    el("p", "", "Four seats share 108 tiles: bamboo, characters and dots, with four of each numbered tile. East marks the dealer. Your drawn tile is marked separately."),
    el("h3", "", "Your turn"),
    el("p", "", "Choose from the actions shown. To discard, select one tile in your hand, then confirm with the separate discard button. The table clock continues while a dialog is open."),
    el("h3", "", "Claims and wins"),
    el("p", "", "Chow makes a sequence, Peng makes three matching tiles, and a Kong uses four. Changsha also has opening reveals, special wins, and bird scoring. The table offers only the choices your seat can make; use Pass to decline an optional claim."),
    el("h3", "", "After a hand"),
    el("p", "", "See hand scores and any winning patterns, then continue if there are more hands. At the end, the host can rematch with the same seats."));
}

const clockFormat = new Intl.DateTimeFormat(undefined, { hour: "numeric", minute: "2-digit" });

export class ChatPanel {
  readonly root = el("div", "form");
  readonly input = el("input", "chat-input");
  private readonly log = el("div", "chat-log");
  private readonly empty = el("p", "note", "No messages yet. Say hello.");
  private readonly send = submit("Send");

  constructor(post: (value: string) => void) {
    this.log.setAttribute("role", "log");
    this.log.setAttribute("aria-label", "Table messages");
    this.log.setAttribute("aria-relevant", "additions");
    this.log.tabIndex = 0;
    this.input.setAttribute("aria-label", "Message");
    this.input.placeholder = "Write a message…";
    this.input.maxLength = 300;
    this.input.autocomplete = "off";
    const form = el("form", "chat-composer", this.input, this.send);
    form.addEventListener("submit", event => {
      event.preventDefault();
      const value = this.input.value.trim();
      if (!this.send.disabled && value) post(value);
    });
    this.root.append(this.empty, this.log, form);
  }

  update(messages: ChatMessage[], self: Seat, enabled: boolean): void {
    const atBottom = this.log.scrollHeight - this.log.scrollTop - this.log.clientHeight < 42;
    this.empty.hidden = messages.length !== 0;
    this.log.hidden = messages.length === 0;
    reconcile(this.log, messages, item => item.messageId, item => {
      const date = new Date(item.sentAtUtc);
      const stamp = Number.isFinite(date.getTime()) ? clockFormat.format(date) : "";
      const byline = el("p", "small", `${item.displayName}${stamp ? ` · ${stamp}` : ""}`);
      return el("article", `chat-msg${item.seat === self ? " mine" : ""}`, byline, el("p", "", item.text));
    });
    if (atBottom) this.log.scrollTop = this.log.scrollHeight;
    this.send.disabled = !enabled;
  }

  committed(value: string): void {
    if (this.input.value.trim() === value) this.input.value = "";
  }

  reset(): void {
    this.input.value = "";
    this.log.replaceChildren();
  }
}

const winNames = { selfDraw: "Self-draw", discard: "Claimed discard", robKong: "Robbed kong" };
const sign = (value: number) => value > 0 ? `+${value}` : String(value);

function resultRack(name: string, tiles: Face[], style = ""): HTMLElement {
  const rack = el("div", `meld-group ${style}`, el("div", "result-tiles", ...tiles.map(face => tileLabel(face))),
    el("span", "meld-caption", name));
  rack.setAttribute("role", "group");
  rack.setAttribute("aria-label", name);
  return rack;
}

export function resultsContent(state: Snapshot, actions: HTMLElement[]): HTMLElement {
  const game = state.game!;
  const settlement = game.settlement!;
  const seatName = (seat: Seat) => state.room.seats.find(item => item.seat === seat)?.displayName ?? winds[seat];
  const summary = settlement.reason === "draw" ? "The hand ended in a draw." : `${settlement.winners.map(item => seatName(item.seat)).join(" and ")} won this hand.`;
  const root = el("div", "form", el("p", "eyebrow", `Hand ${game.handNumber} of ${state.room.matchHands}`),
    el("p", "detail", summary));
  const head = el("thead", "", el("tr", "", el("th", "", "Seat"), el("th", "", "Hand"), el("th", "", "Total")));
  const deltas = el("tbody");
  settlement.seatResults.forEach(row => {
    const name = el("th", "", `${seatName(row.seat)}${row.seat === state.self.seat ? " (you)" : ""}`);
    name.scope = "row";
    const points = el("td", row.delta > 0 ? "positive" : row.delta < 0 ? "negative" : "", sign(row.delta));
    deltas.append(el("tr", "", name, points, el("td", "", sign(row.total))));
  });
  const scoreboard = el("table", "scoreboard", el("caption", "sr-only", "Per-seat score changes and match totals"), head, deltas);
  root.append(scoreboard);
  if (settlement.reason === "win") {
    const hands = el("div", "form winning-hands");
    for (const winner of settlement.winners) {
      const winnerNode = el("section", "winner", el("h3", "", `${seatName(winner.seat)} · ${winNames[winner.winBy]}`));
      const tiles = el("div", "result-tiles");
      if (winner.winningHand) {
        setMelds(tiles, winner.winningHand.melds, game.dealerSeat);
        if (winner.winningHand.concealedTiles.length) tiles.prepend(resultRack("Concealed tiles", winner.winningHand.concealedTiles));
      }
      if (winner.winningTiles.length) tiles.append(resultRack("Winning tile", winner.winningTiles, "winning-event"));
      if (tiles.childElementCount) winnerNode.append(tiles);
      if (winner.patterns.length) winnerNode.append(el("ul", "inline-list",
        ...winner.patterns.map(pattern => el("li", "", `${pattern.label} · ${pattern.points}`))));
      hands.append(winnerNode);
    }
    root.append(hands);
  }
  if (settlement.birds.length) root.append(el("section", "result-section", el("h3", "", "Birds"),
    el("div", "result-tiles", ...settlement.birds.map(face => tileLabel(face)))));
  if (settlement.transfers?.length) root.append(el("section", "result-section", el("h3", "", "Score transfers"),
    el("ul", "inline-list", ...settlement.transfers.map(transfer =>
      el("li", "", `${seatName(transfer.fromSeat)} → ${seatName(transfer.toSeat)}: ${transfer.points} (${transfer.reason})`)))));
  if (state.room.status === "betweenHands") root.append(el("p", "note", `Next dealer: ${seatName(settlement.nextDealerSeat)}.`));
  if (actions.length) root.append(el("div", "row-actions", ...actions));
  return root;
}
