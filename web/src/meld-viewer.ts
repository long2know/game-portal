import "./meld-viewer.css";
import { button, el, reconcile, text, wind } from "./dom";
import { isExposedMeld, meldKey, meldLabel, publicMelds, setMelds } from "./melds";
import type { Meld, Seat, Snapshot } from "./protocol";
import { faceName } from "./tile";

function nameOf(state: Snapshot, seat: Seat): string {
  const direction = wind(seat, state.game!.dealerSeat);
  const name = state.room.seats.find(player => player.seat === seat)?.displayName;
  return name ? `${name} (${direction})` : direction;
}

function openKeys(state: Snapshot | null) {
  const keys = new Map<string, { seat: Seat; index: number; meld: Meld }>();
  if (!state?.game) return keys;
  for (const { seat, melds } of state.game.seats) {
    if (seat === state.self.seat) continue;
    melds.forEach((meld, index) => {
      if (isExposedMeld(meld)) {
        keys.set(`${seat}:${meldKey(meld, index)}`, { seat, index, meld });
      }
    });
  }
  return keys;
}

export class MeldViewer {
  private readonly badge = el("span", "exposed-new", "New");
  readonly trigger = button("Exposed tiles", "btn secondary exposed-trigger", () => this.toggle());
  readonly announce = el("p", "sr-only");
  private readonly title = el("h2", "", "Exposed tiles");
  private readonly note = el("p", "note");
  private readonly rows = el("div", "exposed-rows");
  readonly root = el("section", "exposed-panel",
    el("header", "exposed-head", this.title, button("Back to table", "btn subtle", () => this.close())),
    this.note, this.rows);
  private latest?: string;

  constructor(private readonly table: HTMLElement) {
    this.trigger.append(this.badge);
    this.trigger.hidden = this.badge.hidden = this.root.hidden = true;
    this.root.id = "exposed-tiles";
    this.title.id = "exposed-title";
    this.title.tabIndex = -1;
    this.trigger.setAttribute("aria-expanded", "false");
    this.trigger.setAttribute("aria-controls", this.root.id);
    this.root.setAttribute("aria-labelledby", this.title.id);
    this.announce.setAttribute("role", "status");
    this.root.addEventListener("keydown", event => {
      if (event.key === "Escape") {
        event.preventDefault();
        this.close();
      }
    });
  }

  update(state: Snapshot, prior: Snapshot | null): void {
    const current = openKeys(state);
    if (!current.size) {
      this.trigger.hidden = this.badge.hidden = true;
      this.latest = undefined;
      if (!this.root.hidden) this.close(this.root.contains(document.activeElement));
      this.rows.replaceChildren();
      text(this.announce, "");
      return;
    }
    this.trigger.hidden = false;
    if (!prior && !this.root.hidden) this.close(this.root.contains(document.activeElement));
    const before = openKeys(prior);
    const added = [...current.keys()].filter(key => !before.has(key));
    if (added.length) {
      // A resume can reveal several commits with no recoverable ordering between seats.
      this.latest = added.length === 1 ? added[0] : undefined;
      this.badge.hidden = !this.root.hidden;
    }
    const latest = this.latest ? current.get(this.latest) : undefined;
    const game = state.game!;
    const description = latest
      ? `Latest seen: ${nameOf(state, latest.seat)} · ${meldLabel(latest.meld, game.dealerSeat)}`
      : `${current.size} exposed ${current.size === 1 ? "meld" : "melds"} this hand.`;
    text(this.note, description);
    if (added.length) text(this.announce, latest
      ? `${description}: ${latest.meld.tiles.map(faceName).join(", ")}.${this.root.hidden ? " Open Exposed tiles to review." : ""}`
      : `${description} Open Exposed tiles to review.`);
    const opponents = game.seats.filter(row => row.seat !== state.self.seat && row.melds.length)
      .sort((a, b) => Number(b.seat === latest?.seat) - Number(a.seat === latest?.seat));
    reconcile(this.rows, opponents, row => String(row.seat), () =>
      el("section", "exposed-seat", el("h3"), el("div", "exposed-groups")), (node, row) => {
      text(node.firstElementChild!, nameOf(state, row.seat));
      const groups = node.lastElementChild as HTMLElement;
      const melds = publicMelds(row.melds);
      const first = latest?.seat === row.seat ? latest.index : -1;
      if (first > 0) melds.unshift(...melds.splice(first, 1));
      setMelds(groups, melds, game.dealerSeat);
      [...groups.children].forEach((group, index) =>
        group.classList.toggle("meld-latest", first >= 0 && index === 0));
    });
  }

  private toggle(): void {
    if (!this.root.hidden) { this.close(); return; }
    this.root.hidden = false;
    this.badge.hidden = true;
    this.trigger.setAttribute("aria-expanded", "true");
    this.title.focus({ preventScroll: true });
    this.root.scrollIntoView({ block: "start", behavior: "instant" });
  }

  private close(restore = true): void {
    this.root.hidden = true;
    this.trigger.setAttribute("aria-expanded", "false");
    if (!restore) return;
    const destination = this.trigger.hidden ? this.table : this.trigger;
    destination.focus({ preventScroll: true });
    destination.scrollIntoView({ block: "nearest", behavior: "instant" });
  }
}
