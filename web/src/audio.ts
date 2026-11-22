import { defaultPreferences } from "./protocol";
import type { Preferences } from "./protocol";
import type { Ambient } from "./portal/ambient";
import type { TableCue } from "./table";

type Effect = "select" | TableCue;

export class LanternAudio {
  private context: AudioContext | null = null;
  private effects: GainNode | null = null;
  private music?: Ambient;
  private musicLoading = false;
  private draft?: [boolean, number];
  private noise: AudioBuffer | null = null;
  private readonly voices = new Map<AudioScheduledSourceNode, GainNode>();
  private prefs = defaultPreferences;
  private unavailable = false;
  changed?: () => void;

  constructor() {
    const activate = (event: Event) => { if (event.isTrusted) void this.unlock(); };
    // Touch and pen grant activation on release, not pointerdown.
    window.addEventListener("pointerdown", event => { if (event.pointerType === "mouse") activate(event); }, { passive: true });
    window.addEventListener("pointerup", event => { if (event.pointerType !== "mouse") activate(event); }, { passive: true });
    window.addEventListener("touchend", activate, { passive: true });
    window.addEventListener("keydown", activate);
    document.addEventListener("visibilitychange", () => this.updateBuses());
  }

  get note(): string {
    const [music, volume] = this.musicChoice;
    if (this.unavailable || this.music?.unavailable) return "Audio unavailable.";
    if (music && !volume) return "Music is muted at 0% volume.";
    if (music && !this.music?.playing)
      return this.context?.state === "running" ? "Music is paused." : "Tap or press a key to allow sound.";
    return "Music preview · Save settings to keep changes.";
  }

  private get musicChoice(): [boolean, number] {
    return this.draft ?? [this.prefs.music, this.prefs.volume];
  }

  preview(draft?: [boolean, number]): void {
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

  play(effect: Effect): void {
    const bus = this.effects;
    if (!bus || this.context?.state !== "running" || !this.prefs.sound || !this.prefs.volume || document.hidden) return;
    this.clearEffects();
    switch (effect) {
      case "select":
        this.strike(0, 2450, .024, .085);
        this.tone(bus, 740, 0, .045, .045, "triangle");
        break;
      case "draw":
        this.strike(0, 1060, .072, .065);
        this.tone(bus, 340, .034, .09, .09, "triangle");
        this.tone(bus, 680, .039, .1, .07);
        break;
      case "discard":
        this.strike(0, 1850, .033, .16);
        this.tone(bus, 490, 0, .085, .12, "triangle");
        this.tone(bus, 1330, .006, .045, .06);
        break;
      case "call":
        this.strike(0, 1610, .045, .13);
        this.strike(.06, 2340, .035, .12);
        this.tone(bus, 554.37, .04, .18, .07, "triangle");
        this.tone(bus, 830.61, .1, .25, .09);
        break;
      case "turn":
        this.tone(bus, 392, 0, .17, .08, "triangle");
        this.tone(bus, 587.33, .074, .2, .08);
        break;
      case "win":
        [392, 493.88, 587.33, 783.99].forEach((frequency, index) => {
          this.tone(bus, frequency, index * .085, .39, .105);
          this.tone(bus, frequency * 2.01, index * .085, .21, .027, "triangle");
        });
        break;
      case "exhausted":
        this.tone(bus, 293.66, 0, .32, .09, "triangle");
        this.tone(bus, 196, .12, .43, .11);
        break;
    }
  }

  clearEffects(): void {
    if (!this.context) return;
    const now = this.context.currentTime;
    for (const [source, envelope] of this.voices) {
      envelope.gain.cancelScheduledValues(now);
      envelope.gain.setTargetAtTime(0, now, .005);
      source.stop(now + .02);
    }
    this.voices.clear();
  }

  private updateBuses(): void {
    this.updateMusic();
    if (!this.context || !this.effects) return;
    const visible = !document.hidden;
    if (!this.prefs.sound || !this.prefs.volume || !visible) this.clearEffects();
    this.effects.gain.setTargetAtTime(this.prefs.sound && visible ? this.prefs.volume : 0, this.context.currentTime, .02);
  }

  private updateMusic(): void {
    const [music, volume] = this.musicChoice;
    const context = this.context;
    this.music?.setVolume(music ? volume : 0);
    if (music && volume && context?.state === "running" && !this.musicLoading) {
      this.musicLoading = true;
      const url = new URL("./portal/ambient.js?no-inline", import.meta.url).href;
      void (import(/* @vite-ignore */ url) as Promise<typeof import("./portal/ambient")>).then(({ Ambient }) => {
        this.music = new Ambient(context, () => this.changed?.());
        this.updateMusic();
      }).catch(() => { this.unavailable = true; this.changed?.(); });
    }
    this.changed?.();
  }

  private tone(bus: AudioNode, frequency: number, wait: number, duration: number, level: number, wave: OscillatorType = "sine"): void {
    const context = this.context!;
    const oscillator = context.createOscillator();
    oscillator.type = wave;
    oscillator.frequency.value = frequency;
    this.voice(oscillator, bus, wait, duration, level, .012);
  }

  private strike(wait: number, frequency: number, duration: number, level: number): void {
    const context = this.context!;
    if (!this.noise) {
      this.noise = context.createBuffer(1, Math.ceil(context.sampleRate * .09), context.sampleRate);
      const samples = this.noise.getChannelData(0);
      for (let i = 0; i < samples.length; i++) samples[i] = Math.random() * 2 - 1;
    }
    const source = context.createBufferSource();
    source.buffer = this.noise;
    const band = context.createBiquadFilter();
    band.type = "bandpass";
    band.frequency.value = frequency;
    band.Q.value = .95;
    this.voice(source, this.effects!, wait, duration, level, .002, band);
  }

  private voice(source: AudioScheduledSourceNode, bus: AudioNode, wait: number, duration: number, level: number,
    attack: number, filter?: BiquadFilterNode): void {
    const context = this.context!;
    const gain = context.createGain();
    const time = context.currentTime + wait;
    gain.gain.setValueAtTime(.0001, time);
    gain.gain.exponentialRampToValueAtTime(level, time + attack);
    gain.gain.exponentialRampToValueAtTime(.0001, time + duration);
    source.connect(filter ?? gain);
    filter?.connect(gain);
    gain.connect(bus);
    if (bus === this.effects) this.voices.set(source, gain);
    source.addEventListener("ended", () => {
      this.voices.delete(source);
      source.disconnect();
      filter?.disconnect();
      gain.disconnect();
    }, { once: true });
    source.start(time);
    source.stop(time + duration);
  }
}
