import { defaultPreferences } from "./protocol";
import type { Preferences } from "./protocol";
import type { Callouts } from "./callouts";
import type { Ambient, BarPlayer } from "./portal/ambient";
import type { Effect, TileSounds } from "./table-sounds";

type Soundtrack = "still" | "bamboo";
export type CallVoice = "off" | "mixed" | "lower" | "higher" | "changsha";
export interface AudioChoices { track: Soundtrack; voice: CallVoice }
type MusicChoice = [enabled: boolean, volume: number, track: Soundtrack];

export class LanternAudio {
  private context: AudioContext | null = null;
  private effects: GainNode | null = null;
  private music?: Ambient;
  private musicLoading = false;
  private musicFailed = false;
  private bamboo?: BarPlayer;
  private bambooLoading = false;
  private bambooFailed = false;
  private draft?: MusicChoice;
  private callouts?: Callouts;
  private callsLoading = false;
  private callsFailed = false;
  private atTable = false;
  private cues?: TileSounds;
  private cuesLoading = false;
  private pendingCue?: () => void;
  private prefs = defaultPreferences;
  private unavailable = false;
  choices: AudioChoices = { track: "still", voice: "off" };
  changed?: () => void;

  constructor(private readonly key?: string) {
    if (key) {
      try {
        const [track, voice] = localStorage.getItem(key)?.split(":") ?? [];
        this.choices = {
          track: track === "bamboo" ? "bamboo" : "still",
          voice: voice === "mixed" || voice === "lower" || voice === "higher" || voice === "changsha" ? voice : "off",
        };
      } catch { /* Storage is optional. */ }
    }
    const activate = (event: Event) => { if (event.isTrusted) void this.unlock(); };
    // Touch and pen grant activation on release, not pointerdown.
    window.addEventListener("pointerdown", event => { if (event.pointerType === "mouse") activate(event); }, { passive: true });
    window.addEventListener("pointerup", event => { if (event.pointerType !== "mouse") activate(event); }, { passive: true });
    window.addEventListener("touchend", activate, { passive: true });
    window.addEventListener("keydown", activate);
    document.addEventListener("visibilitychange", () => this.updateBuses());
  }

  get deviceChoices(): boolean { return !!this.key; }
  get callRecordingKey(): string | undefined { return this.key; }

  saveChoices(choices: AudioChoices): boolean {
    if (!this.key) return true;
    if (this.choices.voice !== choices.voice) this.callouts?.stop();
    this.choices = choices;
    try {
      localStorage.setItem(this.key, `${choices.track}:${choices.voice}`);
      return true;
    } catch { return false; }
  }

  get note(): string {
    const [music, volume, track] = this.musicChoice;
    if (this.unavailable) return "Audio unavailable.";
    if (music && (this.musicFailed || this.music?.unavailable)) return "Music is unavailable.";
    if (music && track === "bamboo" && this.bambooFailed) return "Bamboo by the Xiang is unavailable.";
    if (music && !volume) return "Music is muted at 0% volume.";
    const voice = this.choices.voice;
    if (voice !== "off" && (this.callsFailed || this.callouts?.unavailable(voice)))
      return "Spoken calls are unavailable. Table cues still play.";
    if (music && !this.music?.playing)
      return this.context?.state === "running" ? "Music is paused." : "Tap or press a key to allow sound.";
    return music ? "Music preview · Save settings to keep changes." : "Turn on music to preview. Save settings to keep changes.";
  }

  private get musicChoice(): MusicChoice {
    return this.draft ?? [this.prefs.music, this.prefs.volume, this.choices.track];
  }

  preview(draft?: MusicChoice): void {
    this.draft = draft;
    if (draft?.[0] && draft[1]) void this.unlock(true);
    this.updateMusic();
  }

  async unlock(explicit = false): Promise<void> {
    if (this.unavailable || this.context?.state === "running" || navigator.userActivation?.isActive === false ||
      (!explicit && !this.prefs.sound && !this.musicChoice[0])) return;
    try {
      if (!this.context) {
        const Safari = (window as Window & { webkitAudioContext?: typeof AudioContext }).webkitAudioContext;
        const Context = window.AudioContext ?? Safari;
        if (!Context) {
          this.unavailable = true;
          return;
        }
        this.context = new Context();
        this.effects = this.context.createGain();
        this.effects.connect(this.context.destination);
        this.context.addEventListener("statechange", () => this.updateBuses());
      }
      await this.context.resume();
      this.updateBuses();
    } catch { /* Autoplay restrictions do not block play. */ }
    finally { this.changed?.(); }
  }

  configure(prefs: Preferences): void {
    this.prefs = prefs;
    document.documentElement.dataset.reducedMotion = String(prefs.reducedMotion);
    this.updateBuses();
  }

  setTableActive(active: boolean): void {
    if (active === this.atTable) return;
    this.atTable = active;
    this.updateTableSound();
  }

  play(effect: Effect): void {
    if (!this.tableAudible) return;
    this.clearCues();
    const call = effect === "win" ? "hu" : effect === "chi" || effect === "peng" ? effect : undefined;
    const voice = this.choices.voice;
    if (call && voice !== "off") {
      if (this.callouts?.play(voice, call)) return;
      this.callouts?.stop();
    }
    if (this.cues) this.cues.play(effect);
    else {
      const expires = performance.now() + 250;
      this.pendingCue = () => {
        if (performance.now() < expires && this.tableAudible) this.cues?.play(effect);
      };
    }
  }

  clearEffects(): void {
    this.callouts?.stop();
    this.clearCues();
  }

  private clearCues(): void {
    this.pendingCue = undefined;
    this.cues?.clear();
  }

  private updateBuses(): void {
    this.updateMusic();
    this.updateTableSound();
    if (!this.context || !this.effects) return;
    this.effects.gain.setTargetAtTime(this.prefs.sound && !document.hidden ? this.prefs.volume : 0, this.context.currentTime, .02);
  }

  private updateMusic(): void {
    const [music, volume, track] = this.musicChoice;
    const context = this.context;
    if (music && volume && context?.state === "running" && !document.hidden && !this.musicLoading) {
      this.musicLoading = true;
      const url = new URL("./portal/ambient.js?no-inline", import.meta.url).href;
      void (import(/* @vite-ignore */ url) as Promise<typeof import("./portal/ambient")>).then(({ Ambient }) => {
        this.music = new Ambient(context, () => this.changed?.());
        this.updateMusic();
      }).catch(() => { this.musicFailed = true; this.changed?.(); });
    }
    if (music && volume && track === "bamboo" && context?.state === "running" && !document.hidden && !this.bambooLoading) {
      this.bambooLoading = true;
      void import("./bamboo").then(({ bamboo }) => {
        this.bamboo = bamboo(context);
        this.updateMusic();
      }).catch(() => { this.bambooFailed = true; this.updateMusic(); });
    }
    if (!music || !volume || this.bambooFailed && track === "bamboo") this.music?.setVolume(0);
    else if (track === "still" || this.bamboo) {
      this.music?.setTrack(track === "bamboo" ? this.bamboo : undefined);
      this.music?.setVolume(volume);
    }
    this.changed?.();
  }

  private get tableAudible(): boolean {
    return this.atTable && this.prefs.sound && this.prefs.volume > 0 && !document.hidden && this.context?.state === "running";
  }

  private updateTableSound(): void {
    if (!this.tableAudible) {
      this.clearEffects();
      return;
    }
    const bus = this.effects!, voice = this.choices.voice;
    if (!this.cuesLoading) {
      this.cuesLoading = true;
      void import("./table-sounds").then(({ TileSounds }) => {
        this.cues = new TileSounds(bus);
        this.pendingCue?.();
        this.pendingCue = undefined;
      }).catch(() => { this.pendingCue = undefined; });
    }
    if (voice === "off") return;
    if (this.callouts) this.callouts.prepare(voice);
    else if (!this.callsLoading) {
      this.callsLoading = true;
      void import("./callouts").then(({ Callouts }) => {
        this.callouts = new Callouts(bus, () => this.changed?.(), this.key);
        this.updateTableSound();
      }).catch(() => { this.callsFailed = true; this.changed?.(); });
    }
  }
}
