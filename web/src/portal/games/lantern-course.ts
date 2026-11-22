export type Lane = 0 | 1 | 2;
export type Hazard = "kiln" | "rail" | "gate";
export type Move = "left" | "right" | "jump" | "slide";
type Lanes = [Hazard | null, Hazard | null, Hazard | null];

export interface Row {
  at: number;
  hazards: Lanes;
  safe: Lane;
  lantern?: Lane;
}

const lanes = [0, 1, 2] as const;
const stepTime = 1 / 120;
const jumpTime = .74;
const slideTime = .76;
const timeEpsilon = 1e-9;
export const viewDistance = 112;
export const pace = (meters: number) => 16 + Math.min(17, meters * .044);

const opening = (): Row[] => [
  { at: 13, hazards: [null, null, null], safe: 1, lantern: 1 },
  { at: 31, hazards: [null, "kiln", "kiln"], safe: 0, lantern: 0 },
  { at: 49, hazards: ["rail", null, "kiln"], safe: 1, lantern: 0 },
  { at: 67, hazards: ["gate", null, "kiln"], safe: 1, lantern: 0 },
  { at: 84, hazards: ["kiln", null, null], safe: 1, lantern: 1 },
];

const patterns: readonly (readonly [Hazard | null, Hazard | null])[] = [
  ["kiln", null], ["rail", null], ["gate", null],
  ["kiln", "rail"], ["gate", "kiln"], ["rail", "gate"], ["kiln", "kiln"],
];

function after(row: Row, random: () => number): Row {
  // At every pace an adjacent lane stays open, with time to finish a move and cross both lanes.
  const density = Math.min(1, row.at / 380);
  const at = row.at + pace(row.at) * (1.44 - .49 * density + random() * .34);
  const options = lanes.filter(lane => Math.abs(lane - row.safe) <= 1);
  const safe = options[Math.floor(random() * options.length)];
  const pattern = patterns[Math.floor(random() * (density > .45 ? patterns.length : patterns.length - 1))];
  const other = lanes.filter(lane => lane !== safe);
  if (random() < .5) other.reverse();
  const hazards: Lanes = [null, null, null];
  hazards[other[0]] = pattern[0];
  hazards[other[1]] = pattern[1];
  const skillLane = other.find(lane => hazards[lane] === "rail" || hazards[lane] === "gate");
  return { at, hazards, safe, lantern: skillLane !== undefined && random() < .58 ? skillLane : safe };
}

export class LanternCourse {
  readonly rows = opening();
  meters = 0;
  seconds = 0;
  lane: Lane = 1;
  x = 1;
  lanterns = 0;
  pickupAt = -Infinity;
  crash?: Hazard;
  private action?: "jump" | "slide";
  private actionAt = -Infinity;
  private remaining = 0;

  constructor(private readonly random: () => number = Math.random) {
    this.lookAhead();
  }

  get score(): number { return Math.floor(this.meters) + this.lanterns * 25; }
  get next(): Row { return this.rows.find(row => row.at > this.meters)!; }
  get lift(): number { return this.heightAt(this.seconds); }
  get sliding(): boolean { return this.slidingAt(this.seconds); }

  move(direction: Move): void {
    if (this.crash) return;
    if (direction === "left" || direction === "right") {
      this.lane = Math.max(0, Math.min(2, this.lane + (direction === "left" ? -1 : 1))) as Lane;
    } else if (!this.sliding && (this.action !== "jump" || this.seconds - this.actionAt >= jumpTime)) {
      this.actionAt = this.seconds;
      this.action = direction;
    }
  }

  advance(seconds: number): void {
    // A delayed frame slows play rather than teleporting the courier past an unseen obstacle.
    this.remaining += Math.max(0, Math.min(.1, seconds));
    while (this.remaining + timeEpsilon >= stepTime && !this.crash) {
      this.remaining = Math.max(0, this.remaining - stepTime);
      this.step();
    }
  }

  private lookAhead(): void {
    while (this.rows.at(-1)!.at < this.meters + viewDistance) {
      this.rows.push(after(this.rows.at(-1)!, this.random));
    }
  }

  private heightAt(seconds: number): number {
    if (this.action !== "jump") return 0;
    const p = (seconds - this.actionAt) / jumpTime;
    return p < 1 ? 6.2 * p * (1 - p) : 0;
  }

  private slidingAt(seconds: number): boolean {
    return this.action === "slide" && seconds - this.actionAt < slideTime;
  }

  private step(): void {
    const travel = pace(this.meters) * stepTime;
    const delta = this.lane - this.x;
    const lateral = Math.sign(delta) * Math.min(Math.abs(delta), stepTime * 8.4);
    const row = this.next;
    if (row.at <= this.meters + travel) {
      // Sample the exact crossing rather than checking a whole frame's final pose.
      const fraction = (row.at - this.meters) / travel;
      const crossingX = this.x + Math.sign(delta) * Math.min(Math.abs(delta), stepTime * fraction * 8.4);
      const crossingTime = this.seconds + stepTime * fraction;
      const lane = Math.round(crossingX) as Lane;
      const hazard = row.hazards[lane];
      if (hazard && (hazard === "kiln" || (hazard === "rail" && this.heightAt(crossingTime) < .64) ||
            (hazard === "gate" && !this.slidingAt(crossingTime)))) {
        this.meters = row.at;
        this.seconds = crossingTime;
        this.x = crossingX;
        this.crash = hazard;
        return;
      }
      if (row.lantern !== undefined && Math.abs(crossingX - row.lantern) < .36) {
        row.lantern = undefined;
        this.pickupAt = crossingTime;
        this.lanterns++;
      }
    }
    this.x += lateral;
    this.meters += travel;
    this.seconds += stepTime;
    if (this.rows[0].at < this.meters - 6) this.rows.shift();
    this.lookAhead();
  }
}
