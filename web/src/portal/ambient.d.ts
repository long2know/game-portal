export class Ambient {
  constructor(context: AudioContext, changed: () => void);
  readonly playing: boolean;
  readonly unavailable: boolean;
  setVolume(volume: number): void;
}
