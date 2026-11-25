import type { CallVoice } from "./audio";

type Voice = Exclude<CallVoice, "off">;
type Call = "chi" | "peng" | "hu";

const lower = {
  chi: new URL("./calls/chi-lower.mp3?no-inline", import.meta.url).href,
  peng: new URL("./calls/peng-lower.mp3?no-inline", import.meta.url).href,
  hu: new URL("./calls/hu-lower.mp3?no-inline", import.meta.url).href,
};
const clips = {
  lower,
  higher: {
    chi: new URL("./calls/chi-higher.mp3?no-inline", import.meta.url).href,
    peng: new URL("./calls/peng-higher.mp3?no-inline", import.meta.url).href,
    hu: new URL("./calls/hu-higher.mp3?no-inline", import.meta.url).href,
  },
  mixed: { ...lower, chi: new URL("./calls/cha-english.mp3?no-inline", import.meta.url).href },
};

export class Callouts {
  private readonly requested = new Set<string>();
  private readonly buffers = new Map<string, AudioBuffer>();
  private readonly failed = new Set<string>();
  private source?: AudioBufferSourceNode;

  constructor(private readonly bus: GainNode, private readonly changed: () => void) {}

  prepare(voice: Voice): void {
    for (const url of Object.values(clips[voice])) {
      if (this.requested.has(url)) continue;
      this.requested.add(url);
      void fetch(url).then(response => {
        if (!response.ok) throw new Error("Could not load call.");
        return response.arrayBuffer();
      }).then(bytes => this.bus.context.decodeAudioData(bytes))
        .then(buffer => { this.buffers.set(url, buffer); })
        .catch(() => { this.failed.add(url); this.changed(); });
    }
  }

  unavailable(voice: Voice): boolean {
    return Object.values(clips[voice]).some(url => this.failed.has(url));
  }

  play(voice: Voice, call: Call): boolean {
    const buffer = this.buffers.get(clips[voice][call]);
    if (!buffer) return false;
    this.stop();
    const source = this.source = this.bus.context.createBufferSource();
    source.buffer = buffer;
    source.connect(this.bus);
    source.onended = () => {
      source.disconnect();
      if (this.source === source) this.source = undefined;
    };
    source.start();
    return true;
  }

  stop(): void {
    const source = this.source;
    if (!source) return;
    this.source = undefined;
    source.stop();
    source.disconnect();
  }
}
