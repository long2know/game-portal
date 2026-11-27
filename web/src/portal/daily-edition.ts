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
const completionBit = { xiangqi: 1, "mahjong-solitaire": 2 } as const;
const fullClear = completionBit.xiangqi | completionBit["mahjong-solitaire"];
const retainedDays = 30;

export class DailyProgress {
  private readonly attempts = new Map<Challenge, Attempt>();
  private readonly earned = new Map<string, number>();
  private storage?: Storage;
  private recordStorage?: Storage;

  constructor(private readonly mount: string) {
    try { this.storage = this.recordStorage = localStorage; } catch { /* The current tab can still play. */ }
  }

  get available(): boolean { return this.storage !== undefined; }
  get recordAvailable(): boolean { return this.recordStorage !== undefined; }

  read(challenge: Challenge, date: string): Attempt | undefined {
    const stored = this.readStored(challenge);
    if (stored?.date === date) return stored;
    const attempt = this.attempts.get(challenge);
    return attempt?.date === date ? attempt : undefined;
  }

  save(challenge: Challenge, edition: DailyEdition, value: string, completed: boolean): void {
    const { date } = edition;
    const attempt = { date, value, completed: completed || this.read(challenge, date)?.completed === true };
    if (completed) this.reconcileRecord(edition, challenge);
    this.attempts.set(challenge, attempt);
    try { this.storage?.setItem(this.key(challenge), JSON.stringify(attempt)); }
    catch { this.storage = undefined; }
  }

  playRecord(edition: DailyEdition) {
    const days = this.reconcileRecord(edition);
    let best = 0, run = 0;
    for (const { bits } of days) {
      run = bits === fullClear ? run + 1 : 0;
      best = Math.max(best, run);
    }
    const start = days[0].bits === fullClear ? 0 : 1;
    let current = 0;
    while (days[start + current]?.bits === fullClear) current++;
    return {
      current, best, days: days.slice(0, 14).map(({ date, bits }) => ({
        date, xiangqi: !!(bits & completionBit.xiangqi), solitaire: !!(bits & completionBit["mahjong-solitaire"]),
      })),
    };
  }

  private readStored(challenge: Challenge): Attempt | undefined {
    let raw: string | null | undefined;
    try { raw = this.storage?.getItem(this.key(challenge)); }
    catch { this.storage = undefined; }
    if (raw && raw.length <= 2300) {
      try {
        const { date, value, completed } = JSON.parse(raw);
        if (typeof date === "string" && typeof value === "string" && typeof completed === "boolean") {
          return { date, value, completed };
        }
      } catch { /* Ignore damaged local saves. */ }
    }
  }

  private reconcileRecord({ day, date }: DailyEdition, completed?: Challenge) {
    const key = `${this.mount}court-daily-record-v1`;
    let raw: string | null | undefined;
    try { raw = this.recordStorage?.getItem(key); }
    catch { this.recordStorage = undefined; }
    let stored: Record<string, unknown> = {};
    if (raw && raw.length <= 2048) {
      try {
        const value = JSON.parse(raw);
        if (value && typeof value === "object" && !Array.isArray(value)) stored = value;
      } catch { /* Ignore a damaged play record. */ }
    }

    const mark = (date: string, challenge: Challenge) =>
      this.earned.set(date, (this.earned.get(date) ?? 0) | completionBit[challenge]);
    for (const challenge of Object.keys(completionBit) as Challenge[]) {
      for (const attempt of [this.readStored(challenge), this.attempts.get(challenge)]) {
        if (attempt?.completed) mark(attempt.date, challenge);
      }
    }
    if (completed) mark(date, completed);

    const retained = new Map<string, number>();
    let changed = false;
    const days = Array.from({ length: retainedDays }, (_, offset) => {
      const date = utcEdition(new Date((day - offset) * dayMs)).date;
      const value = stored[date];
      const prior = value === 1 || value === 2 || value === fullClear ? value : 0;
      const bits = prior | (this.earned.get(date) ?? 0);
      if (bits) retained.set(date, bits);
      changed ||= bits !== prior;
      return { date, bits };
    });
    this.earned.clear();
    for (const [date, bits] of retained) this.earned.set(date, bits);
    if (changed) {
      try { this.recordStorage?.setItem(key, JSON.stringify(Object.fromEntries(retained))); }
      catch { this.recordStorage = undefined; }
    }
    return days;
  }

  private key(challenge: Challenge): string {
    return `${this.mount}court-daily-${challenge}-v1`;
  }
}
