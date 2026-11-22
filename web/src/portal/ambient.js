// Still Water: MIDI bass, an open voicing, and time/pitch pairs for the felt keys.
const score = [
  [50, [57, 64, 66], [.10, 69, 1.34, 73, 2.90, 76]],
  [47, [54, 57, 62], [.43, 74, 2.14, 66]],
  [43, [55, 62, 69], [.18, 67, 1.77, 74, 3.17, 81]],
  [45, [55, 62, 64], [.86, 76, 2.63, 71]],
  [54, [57, 61, 64], [.23, 73, 1.82, 69]],
  [52, [55, 59, 66], [.67, 71, 2.49, 78]],
  [43, [55, 59, 62], [.22, 79, 1.49, 74, 3.11, 69]],
  [45, [55, 64, 66], [.79, 64, 2.53, 66]],
];
const barLength = 4;
const floor = .0001;

export class Ambient {
  #keys;
  #pad;
  #bus;
  #timer = 0;
  #nextBar = 0;
  #bar = 0;
  #volume = 0;
  #away = false;
  #pending;
  #release;
  #locks = navigator.locks;
  #context;
  #changed;
  unavailable = false;

  constructor(context, changed) {
    this.#context = context;
    this.#changed = changed;
    const wave = (...partials) =>
      context.createPeriodicWave(new Float32Array(partials.length), new Float32Array(partials));
    this.#keys = wave(0, 1, .34, .18, .07, .034, .012);
    this.#pad = wave(0, 1, .18, .055);
    const update = () => this.#update();
    document.addEventListener("visibilitychange", update);
    context.addEventListener("statechange", update);
    window.addEventListener("pagehide", () => { this.#away = true; update(); });
    window.addEventListener("pageshow", () => { this.#away = false; update(); });
    if (!this.#locks) {
      window.addEventListener("focus", update);
      window.addEventListener("blur", update);
    }
  }

  get playing() { return !!this.#bus; }

  setVolume(volume) {
    if (volume === this.#volume) return;
    this.#volume = volume;
    this.#update();
  }

  get #audible() {
    return !this.unavailable && this.#volume > 0 && !this.#away && !document.hidden &&
      this.#context.state === "running" && (!!this.#locks || document.hasFocus());
  }

  #update() {
    if (!this.#audible) this.#stop();
    else if (this.#bus) this.#bus.gain.setTargetAtTime(this.#volume, this.#context.currentTime, .02);
    else if (!this.#pending) {
      if (!this.#locks) this.#start();
      else {
        const request = this.#pending = new AbortController();
        void this.#locks.request("jade-court-ambient", { signal: request.signal }, async () => {
          if (this.#pending !== request || !this.#audible) return;
          this.#start();
          if (this.#bus) await new Promise(resolve => { this.#release = resolve; });
        }).catch(() => {
          if (!request.signal.aborted) {
            this.unavailable = true;
            this.#stop();
            this.#changed();
          }
        }).finally(() => { if (this.#pending === request) this.#pending = undefined; });
      }
    }
    this.#changed();
  }

  #start() {
    const context = this.#context;
    const bus = this.#bus = context.createGain();
    const room = context.createGain();
    const echo = context.createDelay();
    const wet = context.createGain();
    bus.gain.setValueAtTime(0, context.currentTime);
    bus.gain.setTargetAtTime(this.#volume, context.currentTime, .065);
    room.connect(bus);
    room.connect(echo);
    echo.delayTime.value = .32;
    echo.connect(wet);
    wet.gain.value = .20;
    wet.connect(bus);
    wet.connect(echo);
    bus.connect(context.destination);
    const schedule = () => {
      if (!this.#audible || context.currentTime + .45 < this.#nextBar) return;
      const start = Math.max(context.currentTime + .025, this.#nextBar);
      const [bass, harmony, melody] = score[this.#bar++ % score.length];
      this.#voice(room, this.#pad, bass, start, .060, .62, 4.65);
      harmony.forEach((pitch, i) => this.#voice(room, this.#pad, pitch, start + .20 + i * .29, .027, .87, 4.4));
      for (let i = 0; i < melody.length; i += 2) {
        this.#voice(room, this.#keys, melody[i + 1], start + melody[i], i === 0 ? .188 : .143, .024, 2.7);
      }
      this.#nextBar = start + barLength;
    };
    this.#nextBar = 0;
    schedule();
    this.#timer = window.setInterval(schedule, 190);
    this.#changed();
  }

  #stop() {
    this.#pending?.abort();
    this.#pending = undefined;
    const bus = this.#bus;
    if (!bus) return;
    this.#bus = undefined;
    clearInterval(this.#timer);
    const release = this.#release;
    this.#release = undefined;
    const disconnect = () => { bus.disconnect(); release?.(); };
    if (this.#away || document.hidden || this.#context.state !== "running") disconnect();
    else {
      bus.gain.setTargetAtTime(0, this.#context.currentTime, .014);
      window.setTimeout(disconnect, 92);
    }
  }

  #voice(target, wave, pitch, when, level, attack, length) {
    const context = this.#context;
    const tone = context.createOscillator(), envelope = context.createGain();
    tone.setPeriodicWave(wave);
    tone.frequency.value = 440 * 2 ** ((pitch - 69) / 12);
    envelope.gain.setValueAtTime(floor, when);
    envelope.gain.exponentialRampToValueAtTime(level, when + attack);
    envelope.gain.exponentialRampToValueAtTime(level * .25, when + length * .56);
    envelope.gain.exponentialRampToValueAtTime(floor, when + length);
    tone.connect(envelope);
    envelope.connect(target);
    tone.onended = () => { tone.disconnect(); envelope.disconnect(); };
    tone.start(when);
    tone.stop(when + length);
  }
}
