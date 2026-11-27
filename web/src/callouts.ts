import type { CallVoice } from "./audio";

type Voice = Exclude<CallVoice, "off">;
export type TableCall = "chi" | "peng" | "hu";

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
  private source?: { node: AudioBufferSourceNode; voice: Voice };
  private local?: Record<TableCall, AudioBuffer> | null;
  private localLoading = false;

  constructor(private readonly bus: GainNode, private readonly changed: () => void, private readonly key?: string) {}

  prepare(voice: Voice): void {
    if (voice === "changsha") {
      this.prepareLocal();
      return;
    }
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

  private prepareLocal(): void {
    const key = this.key;
    if (!key || this.localLoading) return;
    this.localLoading = true;
    void import("./changsha-calls").then(({ decodeRecordings, readRecordings, watchRecordings }) => {
      let generation = 0;
      const reload = async () => {
        const current = ++generation;
        this.local = undefined;
        if (this.source?.voice === "changsha") this.stop();
        let next: Record<TableCall, AudioBuffer> | null = null;
        try {
          const recordings = await readRecordings(key);
          if (recordings) next = await decodeRecordings(recordings, this.bus.context);
        } catch { /* An incomplete or unreadable pack never supplies individual calls. */ }
        if (generation !== current) return;
        this.local = next;
        this.changed();
      };
      watchRecordings(key, () => void reload());
      return reload();
    }).catch(() => { this.local = null; this.changed(); });
  }

  unavailable(voice: Voice): boolean {
    if (voice === "changsha") return !this.key || this.local === null;
    return Object.values(clips[voice]).some(url => this.failed.has(url));
  }

  play(voice: Voice, call: TableCall): boolean {
    const buffer = voice === "changsha" ? this.local?.[call] : this.buffers.get(clips[voice][call]);
    if (!buffer) return false;
    this.stop();
    const source = this.bus.context.createBufferSource();
    this.source = { node: source, voice };
    source.buffer = buffer;
    source.connect(this.bus);
    source.onended = () => {
      source.disconnect();
      if (this.source?.node === source) this.source = undefined;
    };
    source.start();
    return true;
  }

  stop(): void {
    const source = this.source?.node;
    if (!source) return;
    this.source = undefined;
    source.stop();
    source.disconnect();
  }
}
