export type BarPlayer = (room: AudioNode, start: number, bar: number) => number;

export class Ambient {
  constructor(context: AudioContext, changed: () => void);
  readonly playing: boolean;
  readonly unavailable: boolean;
  setVolume(volume: number): void;
  setTrack(track?: BarPlayer): void;
}
