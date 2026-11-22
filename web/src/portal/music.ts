import type { Preferences } from "../protocol";
import type { Ambient } from "./ambient";
import { button, el, text } from "./dom";

export class MusicControl {
  readonly button = button("", "button music-toggle");
  readonly note = el("p", "music-note");
  private readonly key: string;
  private enabled: boolean;
  private pending?: boolean;
  private edits = 0;
  private volume?: number;
  private context?: AudioContext;
  private ambient?: Ambient;
  private loading = false;
  private attempt = 0;
  private blocked = false;
  private unavailable = false;
  private missingPreferences = false;

  constructor(mount: string, private readonly persist: (enabled: boolean) => Promise<unknown>) {
    this.key = `${mount}court-music`;
    let saved: string | null = null;
    try { saved = localStorage.getItem(this.key); } catch { /* Storage is optional. */ }
    this.enabled = saved === "on" || saved === "pending-on";
    if (saved?.startsWith("pending-")) this.pending = this.enabled;
    this.note.id = "court-music-note";
    this.note.setAttribute("role", "status");
    this.button.setAttribute("aria-describedby", this.note.id);
    this.button.addEventListener("click", event => {
      if (!this.canResume) this.save(!this.enabled);
      if (event.isTrusted) this.unlock();
      this.update();
    });
    const activate = (event: Event) => {
      if (event.isTrusted && !event.composedPath().includes(this.button)) this.unlock();
    };
    window.addEventListener("pointerdown", event => { if (event.pointerType === "mouse") activate(event); }, { passive: true });
    window.addEventListener("pointerup", event => { if (event.pointerType !== "mouse") activate(event); }, { passive: true });
    window.addEventListener("touchend", activate, { passive: true });
    window.addEventListener("keydown", activate);
    this.paint();
  }

  get remembered(): boolean { return this.enabled; }

  private get canResume(): boolean {
    return this.enabled && this.volume !== 0 && !this.missingPreferences && !this.unavailable && !this.blocked && !this.attempt &&
      this.context?.state !== "running";
  }

  restorePending(): void {
    if (this.pending !== undefined) this.save(this.pending);
  }

  configure({ volume, music }: Preferences): void {
    this.volume = volume;
    this.missingPreferences = false;
    if (this.pending === undefined && this.enabled !== music) {
      this.enabled = music;
      this.cache(music ? "on" : "off");
    }
    this.update();
  }

  preferencesUnavailable(): void {
    if (this.volume === undefined) {
      this.missingPreferences = true;
      this.paint();
    }
  }

  private cache(value: string): void {
    try { localStorage.setItem(this.key, value); } catch { /* Guest settings still persist. */ }
  }

  private save(enabled: boolean): void {
    this.enabled = this.pending = enabled;
    this.missingPreferences = false;
    const edit = ++this.edits;
    this.cache(enabled ? "pending-on" : "pending-off");
    void this.persist(enabled).then(() => {
      if (edit === this.edits) {
        this.pending = undefined;
        this.cache(enabled ? "on" : "off");
      }
    }, () => this.preferencesUnavailable());
  }

  private unlock(): void {
    if (!this.enabled || this.volume === 0 || document.hidden || this.unavailable ||
      this.context?.state === "running" || this.attempt || navigator.userActivation?.isActive === false) return;
    try {
      const context = this.context ??= new AudioContext();
      this.blocked = false;
      const resume = context.resume();
      this.attempt = window.setTimeout(() => {
        this.attempt = 0;
        this.blocked = context.state !== "running";
        this.update();
      }, 1200);
      void resume.then(() => {
        clearTimeout(this.attempt);
        this.attempt = 0;
        this.blocked = context.state !== "running";
        this.update();
      }, () => {
        clearTimeout(this.attempt);
        this.attempt = 0;
        this.blocked = true;
        this.paint();
      });
    } catch { this.unavailable = true; }
    this.update();
  }

  private update(): void {
    const volume = this.enabled ? this.volume ?? 0 : 0;
    this.ambient?.setVolume(volume);
    const context = this.context;
    if (volume && context?.state === "running" && !this.ambient && !this.loading && !this.unavailable) {
      this.loading = true;
      const url = new URL("./ambient.js?no-inline", import.meta.url).href;
      void (import(/* @vite-ignore */ url) as Promise<typeof import("./ambient")>).then(({ Ambient }) => {
        this.ambient = new Ambient(context, () => this.paint());
        this.update();
      }).catch(() => {
        this.unavailable = true;
        this.paint();
      });
    }
    this.paint();
  }

  private paint(): void {
    const playing = this.ambient?.playing ?? false;
    text(this.button, playing ? "Music on" : this.canResume ? "Play music" : this.enabled ? "Music paused" : "Music off");
    this.button.setAttribute("aria-pressed", String(playing));
    const message = !this.enabled || playing ? "" :
      this.unavailable || this.ambient?.unavailable ? "Music is unavailable in this browser." :
      this.volume === 0 ? "Music is muted at 0% volume." :
      this.missingPreferences ? "Music settings couldn’t load. Turn music off and back on to retry." :
      this.blocked ? "The browser kept music muted. Tap or press a key to try again." :
      this.canResume ? "Music is saved on. Play it here or continue to the game." :
      this.attempt || !this.ambient ? "Starting music…" : "Music will play when this tab has the audio output.";
    text(this.note, message);
    this.note.hidden = !message;
  }
}
