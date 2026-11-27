import { defaultPreferences } from "./protocol";
import type { Preferences } from "./protocol";
import type { Callouts } from "./callouts";
import type { Ambient, BarPlayer } from "./portal/ambient";
import type { Effect, TileSounds } from "./table-sounds";

type Soundtrack = "still" | "bamboo";
export type CallVoice = "off" | "mixed" | "lower" | "higher" | "changsha";
export interface AudioChoices { track: Soundtrack; voice: CallVoice }
type MusicChoice = [enabled: boolean, volume: number, track: Soundtrack];
type AudioDraft = { sound: boolean; music: MusicChoice };

export class LanternAudio {
  private context: AudioContext | null = null;
  private effects: GainNode | null = null;
  private music?: Ambient;
  private musicLoading = false;
  private musicFailed = false;
  private bamboo?: BarPlayer;
  private bambooLoading = false;
  private bambooFailed = false;
  private draft?: AudioDraft;
  private callouts?: Callouts;
  private callsLoading = false;
  private callsFailed = false;
  private atTable = false;
  private away = false;
  private Sounds?: typeof TileSounds;
  private cues?: TileSounds;
  private cuesLoading = false;
  private cuesFailed = false;
  private pendingCue?: () => void;
  private test?: { bus: GainNode; cues: TileSounds };
  private pendingTest?: { expires: number; inSettings: () => boolean };
  private ready = false;
  private resumeTimer = 0;
  private blocked = false;
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
    const activate = (event: Event) => {
      if (event.isTrusted && !event.defaultPrevented) this.unlock(event.type === "click");
    };
    // Touch and pen grant activation on release, not pointerdown.
    window.addEventListener("pointerdown", event => { if (event.pointerType === "mouse") activate(event); }, { passive: true });
    window.addEventListener("pointerup", event => { if (event.pointerType !== "mouse") activate(event); }, { passive: true });
    window.addEventListener("touchend", activate, { passive: true });
    window.addEventListener("keydown", event => { if (!event.repeat && event.key !== "Escape") activate(event); }, true);
    window.addEventListener("click", activate);
    document.addEventListener("visibilitychange", () => this.updateBuses());
    window.addEventListener("pagehide", () => { this.away = true; this.updateBuses(); });
    window.addEventListener("pageshow", () => { this.away = false; this.updateBuses(); });
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
    const sound = this.draft?.sound ?? this.prefs.sound;
    if (!sound && !music) return "Tile sounds and music are off in Settings.";
    if (!volume) return sound ? "Audio is muted at 0% volume." : "Music is muted at 0% volume.";
    if (this.unavailable) return "Audio unavailable.";
    if (!this.running) {
      if (this.resumeTimer) return "Starting audio…";
      if (this.blocked) return "The browser kept audio paused. Tap Test sound to retry.";
      return "Audio is paused. Tap Test sound to start or resume it.";
    }
    if (sound && this.cuesFailed) return "Tile sounds couldn’t load. Reload this page to try again.";
    if (music && (this.musicFailed || this.music?.unavailable)) return "Music is unavailable.";
    if (music && track === "bamboo" && this.bambooFailed) return "Bamboo by the Xiang is unavailable.";
    const voice = this.choices.voice;
    if (voice !== "off" && (this.callsFailed || this.callouts?.unavailable(voice)))
      return "Spoken calls are unavailable. Table cues still play.";
    if (this.pendingTest) return "Preparing the tile sound…";
    if (music) return this.music?.playing ? "Music preview · Save settings to keep changes." : "Music is paused.";
    return "Tap Test sound to hear a tile cue. If it’s silent, check device volume and audio output.";
  }

  private get musicChoice(): MusicChoice {
    return this.draft?.music ?? [this.prefs.music, this.prefs.volume, this.choices.track];
  }

  private get visible(): boolean { return !this.away && !document.hidden; }
  private get running(): boolean { return this.ready && this.context?.state === "running"; }
  private get wantsAudio(): boolean {
    const [music, volume] = this.musicChoice;
    return volume > 0 && (music || (this.draft?.sound ?? this.prefs.sound));
  }

  preview(draft?: AudioDraft): void {
    this.draft = draft;
    this.clearTest();
    this.updateBuses();
  }

  testSound(inSettings: () => boolean): void {
    this.clearTest();
    this.unlock(true);
    if (this.visible && this.draft?.sound && this.musicChoice[1] > 0 && (this.running || this.resumeTimer)) {
      this.pendingTest = { expires: performance.now() + 1200, inSettings };
      this.updateTest();
    }
    this.changed?.();
  }

  unlock(retry = false): void {
    if (!this.visible || !this.wantsAudio || this.unavailable || this.running || this.resumeTimer && !retry) return;
    try {
      const current = this.context;
      const context = current && current.state !== "closed" ? current : this.openContext();
      if (context) {
        this.cancelResume();
        this.blocked = false;
        const settle = () => {
          if (this.resumeTimer === timer) this.finishResume(context);
        };
        // A browser can leave resume() pending rather than reject an activation it can't honor.
        const timer = this.resumeTimer = window.setTimeout(settle, 1200);
        void context.resume().then(settle, settle);
      }
    } catch {
      this.cancelResume();
      this.blocked = true;
      this.clearTest();
    }
    this.changed?.();
  }

  private openContext(): AudioContext | undefined {
    const Safari = (window as Window & { webkitAudioContext?: typeof AudioContext }).webkitAudioContext;
    const Context = window.AudioContext ?? Safari;
    if (!Context) { this.unavailable = true; return; }
    const context = new Context();
    const effects = context.createGain();
    effects.gain.value = 0;
    effects.connect(context.destination);
    this.clearCues(true);
    this.callouts?.dispose();
    this.clearTest();
    this.ready = false;
    this.cues = this.test = this.music = this.bamboo = this.callouts = undefined;
    this.musicLoading = this.bambooLoading = this.callsLoading = false;
    this.musicFailed = this.bambooFailed = this.callsFailed = false;
    this.context = context;
    this.effects = effects;
    context.addEventListener("statechange", () => {
      if (this.context !== context) return;
      if (this.visible && this.resumeTimer && context.state === "running") this.finishResume(context);
      else this.updateBuses();
    });
    return context;
  }

  private cancelResume(): void {
    clearTimeout(this.resumeTimer);
    this.resumeTimer = 0;
  }

  private finishResume(context: AudioContext): void {
    this.cancelResume();
    if (this.visible && context.state === "running") this.ready = true;
    this.blocked = !this.running;
    if (this.blocked) this.clearTest();
    this.updateBuses();
  }

  configure(prefs: Preferences): void {
    this.prefs = prefs;
    document.documentElement.dataset.reducedMotion = String(prefs.reducedMotion);
    this.updateBuses();
  }

  setTableActive(active: boolean): void {
    if (active === this.atTable) return;
    this.atTable = active;
    if (!active) this.clearTest();
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
    this.clearCues(true);
  }

  private clearCues(immediate = false): void {
    this.pendingCue = undefined;
    this.cues?.clear(immediate);
  }

  private clearTest(): void {
    this.pendingTest = undefined;
    this.test?.cues.clear(true);
  }

  private updateTest(): void {
    const pending = this.pendingTest;
    if (!this.running) {
      if (this.resumeTimer) this.test?.cues.clear(true);
      else this.clearTest();
    } else if (pending) {
      if (!pending.inSettings() || performance.now() >= pending.expires || this.cuesFailed) this.clearTest();
      else if (!this.Sounds) this.loadCues();
      else {
        const context = this.context!;
        if (!this.test) {
          // A draft sound test must not change the live table's saved cue volume.
          const bus = context.createGain();
          bus.connect(context.destination);
          this.test = { bus, cues: new this.Sounds(bus) };
        }
        this.test.bus.gain.value = this.musicChoice[1];
        this.test.cues.play("turn");
        this.pendingTest = undefined;
      }
    }
  }

  private updateBuses(): void {
    if (!this.visible || !this.wantsAudio) {
      this.cancelResume();
      this.clearTest();
    }
    this.updateMusic();
    this.updateTableSound();
    this.updateTest();
    this.changed?.();
  }

  private updateMusic(): void {
    const [music, volume, track] = this.musicChoice;
    const context = this.context;
    if (music && volume && context && this.running && this.visible && !this.musicLoading) {
      this.musicLoading = true;
      const url = new URL("./portal/ambient.js?no-inline", import.meta.url).href;
      void (import(/* @vite-ignore */ url) as Promise<typeof import("./portal/ambient")>).then(({ Ambient }) => {
        if (context !== this.context) return;
        this.music = new Ambient(context, () => this.changed?.());
        this.updateMusic();
      }).catch(() => {
        if (context === this.context) { this.musicFailed = true; this.changed?.(); }
      });
    }
    if (music && volume && track === "bamboo" && context && this.running && this.visible && !this.bambooLoading) {
      this.bambooLoading = true;
      void import("./bamboo").then(({ bamboo }) => {
        if (context !== this.context) return;
        this.bamboo = bamboo(context);
        this.updateMusic();
      }).catch(() => {
        if (context === this.context) { this.bambooFailed = true; this.updateMusic(); }
      });
    }
    if (!music || !volume || this.bambooFailed && track === "bamboo") this.music?.setVolume(0);
    else if (track === "still" || this.bamboo) {
      this.music?.setTrack(track === "bamboo" ? this.bamboo : undefined);
      this.music?.setVolume(volume);
    }
    this.changed?.();
  }

  private get tableAudible(): boolean {
    return this.atTable && this.prefs.sound && this.prefs.volume > 0 && this.visible && this.running;
  }

  private loadCues(): void {
    if (this.cuesLoading) return;
    this.cuesLoading = true;
    void import("./table-sounds").then(({ TileSounds }) => {
      this.Sounds = TileSounds;
      this.updateBuses();
    }).catch(() => { this.cuesFailed = true; this.updateBuses(); });
  }

  private updateTableSound(): void {
    const bus = this.effects;
    if (!bus) return;
    if (!this.tableAudible) {
      this.clearEffects();
      bus.gain.cancelScheduledValues(bus.context.currentTime);
      bus.gain.setValueAtTime(0, bus.context.currentTime);
      return;
    }
    const voice = this.choices.voice;
    bus.gain.setTargetAtTime(this.prefs.volume, bus.context.currentTime, .02);
    if (this.Sounds) {
      this.cues ??= new this.Sounds(bus);
      this.pendingCue?.();
      this.pendingCue = undefined;
    } else if (this.cuesFailed) {
      this.pendingCue = undefined;
    } else {
      this.loadCues();
    }
    if (voice === "off") return;
    if (this.callouts) this.callouts.prepare(voice);
    else if (!this.callsLoading) {
      this.callsLoading = true;
      void import("./callouts").then(({ Callouts }) => {
        if (bus !== this.effects) return;
        this.callouts = new Callouts(bus, () => this.changed?.(), this.key);
        this.updateTableSound();
      }).catch(() => {
        if (bus === this.effects) { this.callsFailed = true; this.changed?.(); }
      });
    }
  }
}
