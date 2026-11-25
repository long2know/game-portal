const dayMs = 86_400_000;
const firstDay = Date.UTC(2026, 10, 25) / dayMs;

export function utcEdition(now: Date) {
  const day = Math.floor(now.getTime() / dayMs);
  return { date: now.toISOString().slice(0, 10), day, seed: day >>> 0, nextAt: (day + 1) * dayMs };
}

export type DailyEdition = ReturnType<typeof utcEdition>;

export function puzzleIndex({ day }: DailyEdition, count: number): number {
  const index = (day - firstDay) % count;
  return index < 0 ? index + count : index;
}

export type Challenge = "xiangqi" | "mahjong-solitaire";
type Attempt = { date: string; value: string; completed: boolean };

export class DailyProgress {
  private readonly attempts = new Map<Challenge, Attempt>();
  private storage?: Storage;

  constructor(private readonly mount: string) {
    try { this.storage = localStorage; } catch { /* The current tab can still play. */ }
  }

  get available(): boolean { return this.storage !== undefined; }

  read(challenge: Challenge, date: string): Attempt | undefined {
    let raw: string | null | undefined;
    try { raw = this.storage?.getItem(this.key(challenge)); }
    catch { this.storage = undefined; }
    if (raw && raw.length <= 2300) {
      try {
        const { date: savedDate, value, completed } = JSON.parse(raw);
        if (savedDate === date && typeof value === "string" && typeof completed === "boolean") {
          return { date, value, completed };
        }
      } catch { /* Ignore damaged local saves. */ }
    }
    const attempt = this.attempts.get(challenge);
    return attempt?.date === date ? attempt : undefined;
  }

  save(challenge: Challenge, date: string, value: string, completed: boolean): void {
    const attempt = { date, value, completed: completed || this.read(challenge, date)?.completed === true };
    this.attempts.set(challenge, attempt);
    try { this.storage?.setItem(this.key(challenge), JSON.stringify(attempt)); }
    catch { this.storage = undefined; }
  }

  private key(challenge: Challenge): string {
    return `${this.mount}court-daily-${challenge}-v1`;
  }
}
