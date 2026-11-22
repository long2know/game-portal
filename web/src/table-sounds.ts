import type { TableCue } from "./table";

export type Effect = "select" | TableCue;

export class TileSounds {
  private readonly context: BaseAudioContext;
  private noise: AudioBuffer | null = null;
  private readonly voices = new Map<AudioScheduledSourceNode, GainNode>();

  constructor(private readonly bus: GainNode) {
    this.context = bus.context;
  }

  play(effect: Effect): void {
    switch (effect) {
      case "select":
        this.strike(0, 2450, .024, .085);
        this.tone(740, 0, .045, .045, "triangle");
        break;
      case "draw":
        this.strike(0, 1060, .072, .065);
        this.tone(340, .034, .09, .09, "triangle");
        this.tone(680, .039, .1, .07);
        break;
      case "discard":
        this.strike(0, 1850, .033, .16);
        this.tone(490, 0, .085, .12, "triangle");
        this.tone(1330, .006, .045, .06);
        break;
      case "chi":
      case "peng":
      case "call":
        this.strike(0, 1610, .045, .13);
        this.strike(.06, 2340, .035, .12);
        this.tone(554.37, .04, .18, .07, "triangle");
        this.tone(830.61, .1, .25, .09);
        break;
      case "turn":
        this.tone(392, 0, .17, .08, "triangle");
        this.tone(587.33, .074, .2, .08);
        break;
      case "win":
        [392, 493.88, 587.33, 783.99].forEach((frequency, index) => {
          this.tone(frequency, index * .085, .39, .105);
          this.tone(frequency * 2.01, index * .085, .21, .027, "triangle");
        });
        break;
      case "exhausted":
        this.tone(293.66, 0, .32, .09, "triangle");
        this.tone(196, .12, .43, .11);
        break;
    }
  }

  clear(): void {
    const now = this.context.currentTime;
    for (const [source, envelope] of this.voices) {
      envelope.gain.cancelScheduledValues(now);
      envelope.gain.setTargetAtTime(0, now, .005);
      source.stop(now + .02);
    }
    this.voices.clear();
  }

  private tone(frequency: number, wait: number, duration: number, level: number, wave: OscillatorType = "sine"): void {
    const oscillator = this.context.createOscillator();
    oscillator.type = wave;
    oscillator.frequency.value = frequency;
    this.voice(oscillator, wait, duration, level, .012);
  }

  private strike(wait: number, frequency: number, duration: number, level: number): void {
    const context = this.context;
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
    this.voice(source, wait, duration, level, .002, band);
  }

  private voice(source: AudioScheduledSourceNode, wait: number, duration: number, level: number,
    attack: number, filter?: BiquadFilterNode): void {
    const gain = this.context.createGain();
    const time = this.context.currentTime + wait;
    gain.gain.setValueAtTime(.0001, time);
    gain.gain.exponentialRampToValueAtTime(level, time + attack);
    gain.gain.exponentialRampToValueAtTime(.0001, time + duration);
    source.connect(filter ?? gain);
    filter?.connect(gain);
    gain.connect(this.bus);
    this.voices.set(source, gain);
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
