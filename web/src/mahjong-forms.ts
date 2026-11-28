import type { Modal } from "./dialog";
import { button, el, reconcile, wind } from "./dom";
import { canManageMatch, classicRules, rulesProfile, rulesProfiles, tableScore, twoTileKongRules } from "./mahjong-profile";
import { setMelds } from "./melds";
import type { Action, ChatMessage, Face, Payloads, RulesVersion, Seat, Snapshot } from "./protocol";
import { field, submit, toggle } from "./screens";
import { tileLabel } from "./tile";

export function showCreate(modal: Modal, initialRules: RulesVersion | undefined, portal: boolean,
  enter: (payload: Payloads["createRoom"]) => void): HTMLButtonElement {
  const visibility = el("select", "", new Option("Invite only", "invite"), new Option("Public", "public"));
  const hands = el("select", "", el("option", "", "One hand"), el("option", "", "Four hands"));
  const profiles = (Object.keys(rulesProfiles) as RulesVersion[]).filter(version => portal || version !== twoTileKongRules);
  const rules = el("select", "", ...profiles.map(version => new Option(rulesProfile(version).label, version)));
  rules.value = initialRules ?? classicRules;
  const explanation = el("p", "note", portal
    ? "Two-tile Kong shows up to two replacements after a rolled Kong. Classic and house rules use one replacement. Classic reveals up to one bird after a win; the other profiles reveal up to two. These rules stay with the table."
    : "Bird rules stay with this table. You can add or remove bots in the waiting room.");
  explanation.id = "create-rules-note";
  rules.setAttribute("aria-describedby", explanation.id);
  const bots = toggle("Fill the other three seats with bots", false);
  const create = submit("Create table");
  const form = el("form", "form", field("Who can join?", visibility), field("Match length", hands),
    field(portal ? "Table rules" : "Birds", rules), bots.field, explanation);
  if (portal) {
    const listing = el("p", "note", "Public listings show the host’s display name. Players can replace eligible bots, even during a hand.");
    listing.hidden = true;
    visibility.addEventListener("change", () => { listing.hidden = visibility.value !== "public"; });
    form.append(listing);
  }
  form.append(el("div", "row-actions", create));
  form.addEventListener("submit", event => {
    event.preventDefault();
    const version = profiles[rules.selectedIndex];
    enter({ visibility: visibility.selectedIndex === 0 ? "invite" : "public",
      matchHands: hands.selectedIndex === 0 ? 1 : 4, fillBots: bots.input.checked,
      ...(version && version !== classicRules ? { rulesVersion: version } : {}) });
  });
  modal.show("create", "Create a table", form);
  return create;
}

export function showShareLink(modal: Modal, path: string, state: Snapshot | null): void {
  if (state?.room.status !== "waiting" || state.room.visibility !== "invite" || !state.self.inviteCode) return;
  const link = new URL(path, location.origin);
  link.hash = new URLSearchParams({ invite: state.self.inviteCode }).toString();
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

export function rulesContent(profile?: RulesVersion, portal = false): HTMLElement {
  const showTwoTile = portal || profile === twoTileKongRules;
  const rules = showTwoTile ? "table rules" : "bird rules";
  return el("div", "form rules-content",
    el("p", "", profile ? `This table: ${rulesProfile(profile).label}. Local variations exist.` : `Local Changsha rules vary. Choose the ${rules} when you create a table.`),
    el("h3", "", "The table"),
    el("p", "", "Four seats share 108 tiles: bamboo, characters and dots, with four of each numbered tile. East marks the dealer. Your drawn tile is marked separately."),
    el("h3", "", "Your turn"),
    el("p", "", "Choose from the actions shown. To discard, select one tile in your hand, then confirm with the separate discard button. The table clock continues while a dialog is open."),
    el("h3", "", "Claims and wins"),
    el("p", "", "Cha makes a sequence, Peng makes three matching tiles, and a Kong uses four. The table offers only the choices your seat can make; use Pass to decline an optional claim. Open Exposed tiles to read opponents’ committed open melds."),
    ...(showTwoTile ? [
      el("h3", "", "Rolling for Kong"),
      el("p", "", "On the Two-tile Kong table, count stacks from the tail, starting at #1. Use the dice sum; if there aren’t enough stacks, use the lower die, then the rear stack if even that is too far. Take the tiles from that stack, up to two. The older profiles use a single rolled replacement."),
      el("p", "", "Each replacement is a separate choice for Hu, never part of a combined win. If you can’t win or decline, discard all the replacements together. Other players may pass or take one Kong Cannon win each, on one of those tiles."),
    ] : []),
    el("h3", "", "Birds"),
    el("p", "", `After a win, One bird (classic) flips up to one wall tile and Two birds (house rules)${showTwoTile ? " and Two-tile Kong" : ""} flip${showTwoTile ? "" : "s"} up to two, in wall order. A last-tile win uses its winning tile as one reference bird. A draw flips none.`),
    el("p", "", "Rank counts from this hand’s dealer: 1/5/9 dealer, 2/6 next seat, 3/7 opposite, 4/8 previous. In Jade Court, each win payment is multiplied by 1 + the number of birds that count for its payer or winner."),
    el("h3", "", "After a hand"),
    el("p", "", `Results show the points from this hand, the match total and the running table total. A new match with the same seats resets match scores but keeps table scores and ${rules}. Create a new table to change ${rules}.`));
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

function resultBirds(state: Snapshot, seatName: (seat: Seat) => string): HTMLElement {
  const game = state.game!;
  const settlement = game.settlement!;
  const { birds } = settlement;
  const section = el("section", "result-section", el("h3", "", `Birds (${birds.length})`));
  if (birds.length) section.append(el("div", "result-birds", ...birds.map(face => {
    const hit = ((game.dealerSeat + Number(face[1]) - 1) % 4) as Seat;
    return resultRack(`Counts for ${seatName(hit)} (${wind(hit, game.dealerSeat)})`, [face], "bird-hit");
  })));
  if (settlement.reason === "draw") section.append(el("p", "note", "A draw flips no birds."));
  else if (settlement.winners.some(winner =>
    winner.patterns.some(({ code }) => code === "lastTileDraw" || code === "lastTileDiscard"))) {
    section.append(el("p", "note", "The winning last tile counts as one reference bird. No extra tile is flipped."));
  } else {
    const availableBeforeReveal = game.wallCount + birds.length;
    const limit = rulesProfile(state.room.rulesVersion).birds;
    if (availableBeforeReveal < limit) section.append(el("p", "note", availableBeforeReveal === 0
      ? "No wall tiles remained after the win." : "Only one wall tile remained after the win."));
  }
  return section;
}

function resultsContent(state: Snapshot, nextHand: HTMLButtonElement | null, actions: HTMLElement[], managed: boolean): HTMLElement {
  const game = state.game!;
  const settlement = game.settlement!;
  const seatName = (seat: Seat) => state.room.seats.find(item => item.seat === seat)?.displayName ?? wind(seat, game.dealerSeat);
  const won = settlement.reason === "win";
  const summary = won ? `${settlement.winners.map(item => seatName(item.seat)).join(" and ")} won this hand.` : "The hand ended in a draw.";
  const nextDealer = settlement.nextDealerSeat;
  const birds = resultBirds(state, seatName);
  const outcome = el("p", "detail", summary);
  const profile = el("p", "note", rulesProfile(state.room.rulesVersion).label);
  const overview = el("div", "result-overview", el("p", "eyebrow", `Hand ${game.handNumber} of ${state.room.matchHands}`),
    ...(won ? [profile, birds, outcome] : [outcome, profile]),
    el("p", "note", `Current dealer: ${seatName(game.dealerSeat)}`),
    el("p", "note", `Next dealer: ${nextDealer == null ? "Not available" : seatName(nextDealer)}`));
  const root = el("div", "form result-review");
  const actionRow = el("div", "row-actions", ...actions);
  const head = el("thead", "", el("tr", "", ...["Seat", "This hand", "Match total", "Table total"].map(label => {
    const column = el("th", "", label);
    column.scope = "col";
    return column;
  })));
  const deltas = el("tbody");
  settlement.seatResults.forEach(row => {
    const name = el("th", "", `${seatName(row.seat)}${row.seat === state.self.seat ? " (you)" : ""}`);
    name.scope = "row";
    const points = el("td", row.delta > 0 ? "positive" : row.delta < 0 ? "negative" : "", sign(row.delta));
    deltas.append(el("tr", "", name, points, el("td", "", sign(row.total)), el("td", "", sign(tableScore(state, row.seat)))));
  });
  const scoreboard = el("table", "scoreboard", el("caption", "sr-only", "Scores by seat"), head, deltas);
  const summaryColumn = el("div", "form result-summary");
  if (nextHand) summaryColumn.append(el("div", "row-actions", nextHand));
  summaryColumn.append(overview, scoreboard);
  root.append(summaryColumn);
  if (won) {
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
  if (managed && actions.length) root.append(actionRow);
  if (!won) root.append(birds);
  if (settlement.transfers?.length) root.append(el("section", "result-section", el("h3", "", "Score transfers"),
    el("ul", "inline-list", ...settlement.transfers.map(transfer =>
      el("li", "", `${seatName(transfer.fromSeat)} → ${seatName(transfer.toSeat)}: ${transfer.points} (${transfer.reason})`)))));
  if (state.room.status === "complete") root.append(el("p", "note",
    `A new match keeps the ${state.room.rulesVersion === twoTileKongRules ? "table rules" : "table’s bird rules"} and table total but resets match scores.`));
  if (!managed && actions.length) root.append(actionRow);
  return root;
}

interface TableDialog {
  modal: Modal;
  opener: HTMLButtonElement;
  snapshot: () => Snapshot | null;
  actions: (controls: HTMLButtonElement[]) => void;
  command: (action: Action) => void;
}

const matchActions = {
  restartMatch: {
    label: "New game",
    warning: "Start over with the same bots and settings, including bird rules. This resets the match score but keeps the table total.",
  },
  quit: {
    label: "Quit table",
    warning: "End this match and return to the Mahjong lobby. You won’t be able to resume. To keep an ongoing match, use Back to lobby in Settings instead.",
  },
} as const;
type MatchAction = keyof typeof matchActions;

export function showTableDialog({ modal, opener, snapshot, actions, command }: TableDialog, results: boolean): void {
  const state = snapshot();
  const managed = canManageMatch(state);
  if (!state?.game || (results ? !state.game.settlement : !managed)) return;
  const complete = state.room.status === "complete";
  const send = (op: MatchAction | "nextHand" | "rematch" | "leaveRoom") => {
    const current = snapshot();
    if (current?.roomId !== state.roomId ||
      ((op === "restartMatch" || op === "quit") && !canManageMatch(current))) return;
    const payload = { roomId: current.roomId, expectedRoomRevision: current.roomRevision };
    command(op === "quit" ? { op: "leaveRoom", payload: { ...payload, abandon: true } } : { op, payload });
  };
  const controls: HTMLButtonElement[] = [];
  const nextHand = results && !complete ? button("Next hand", "btn primary", () => send("nextHand")) : null;
  const title = results ? complete ? "Match results" : "Hand results" : "Table options";
  const show = () => {
    modal.show(results ? "results" : "match", title, content, managed ? opener : undefined);
    actions(nextHand ? [nextHand, ...controls] : controls);
  };
  if (managed) {
    const add = (op: MatchAction) => {
      const { label, warning } = matchActions[op];
      const choice = button(label, "btn secondary", () => {
        const cancel = button("Cancel", "btn subtle", () => {
          show();
          choice.focus();
        });
        const confirm = button(op === "restartMatch" ? "Start new game" : "Quit table", "btn primary", () => {
          actions([confirm, cancel]);
          send(op);
        });
        const detail = el("p", "detail", op === "restartMatch" && state.room.rulesVersion === twoTileKongRules
          ? warning.replace("bird rules", "table rules") : warning);
        detail.id = "match-warning";
        confirm.setAttribute("aria-describedby", detail.id);
        modal.show("match", `${label}?`, el("div", "form", detail, el("div", "row-actions", cancel, confirm)));
        actions([confirm]);
        cancel.focus({ preventScroll: true });
      });
      controls.push(choice);
    };
    add("restartMatch");
    add("quit");
  } else if (complete) {
    if (state.room.hostSeat === state.self.seat) controls.push(button("Rematch", "btn primary", () => send("rematch")));
    controls.push(button("Leave table", "btn subtle", () => send("leaveRoom")));
  }
  const content = results ? resultsContent(state, nextHand, controls, managed) : el("div", "form",
    el("p", "detail", "Start over with these bots or leave this table permanently."), el("div", "row-actions", ...controls));
  show();
}
