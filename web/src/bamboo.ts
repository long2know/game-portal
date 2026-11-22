import type { BarPlayer } from "./portal/ambient";

// Bamboo by the Xiang: D pentatonic, with a plucked question and a breathed reply.
// Entries are beats and MIDI pitches; winds also give the length in beats.
const score = [
  [[0, 50, .74, 62, 1.5, 64, 2, 69, 3.25, 66], [1.35, 74, 1.15, 2.7, 76, 1], [0]],
  [[0, 57, .6, 64, 1.65, 66, 2.25, 71, 3.4, 69], [0, 78, 1.4, 1.65, 74, 1.65], [2.5]],
  [[0, 47, .85, 62, 1.35, 66, 1.8, 69, 3, 71], [1.65, 76, .8, 2.56, 78, 1.12], [1, 3.6]],
  [[0, 50, .85, 57, 1.65, 66, 2.45, 64, 3, 62], [.1, 81, .65, .84, 78, .65, 1.56, 74, 1.72], []],
  [[0, 45, .72, 69, 1.28, 71, 2, 74, 3.4, 76], [1.22, 81, 1.1, 2.53, 83, 1], [0]],
  [[0, 47, .8, 66, 1.5, 69, 2.6, 76, 3.2, 78], [.3, 86, .86, 1.25, 83, 1.49], [2]],
  [[0, 52, .7, 71, 1.43, 69, 2.1, 66, 2.77, 64, 3.4, 69], [.1, 81, 1.2, 1.55, 78, 1.7], [.08, 3.1]],
  [[0, 50, .83, 62, 1.55, 69, 2.3, 74], [.3, 76, .6, 1, 74, 2.25], [0]],
  [[0, 47, .64, 62, 1.48, 66, 2.11, 71, 3.12, 69], [1.82, 78, .74, 2.65, 81, .98], [1]],
  [[0, 45, .63, 64, 1.43, 69, 2.87, 66, 3.5, 64], [.1, 83, .53, .74, 81, 1.18, 2.15, 76, 1.35], [2.61]],
  [[0, 52, .86, 59, 1.5, 69, 2, 66, 3, 64], [.22, 78, .79, 1.17, 76, .76, 2.08, 74, 1.15], [0, 2.88]],
  [[0, 50, .9, 57, 1.8, 62, 2.28, 66, 2.8, 62], [.36, 74, 1.9], []],
];
const beat = .86;
const floor = .0001;
const hertz = (pitch: number) => 440 * 2 ** ((pitch - 69) / 12);

export function bamboo(context: BaseAudioContext): BarPlayer {
  const wave = (...partials: number[]) =>
    context.createPeriodicWave(new Float32Array(partials.length), new Float32Array(partials));
  const strings = wave(0, .83, .56, .18, .34, .12, .19, .064, .09);
  const flute = wave(0, 1, .23, .11, .12, .04, .063, .025);
  const noise = context.createBuffer(1, Math.ceil(context.sampleRate * .37), context.sampleRate);
  let seed = 1909;
  const samples = noise.getChannelData(0);
  for (let i = 0; i < samples.length; i++) {
    seed = (Math.imul(seed, 1664525) + 1013904223) | 0;
    samples[i] = seed / 2147483648;
  }

  function voice(room: AudioNode, pitch: number, timbre: PeriodicWave) {
    const oscillator = context.createOscillator();
    oscillator.setPeriodicWave(timbre);
    oscillator.frequency.value = hertz(pitch);
    const envelope = context.createGain();
    envelope.connect(room);
    return { oscillator, envelope };
  }

  function texture(room: AudioNode, start: number, seconds: number, frequency: number, level: number, breath = false) {
    const source = context.createBufferSource();
    source.buffer = noise;
    source.loop = breath;
    const filter = context.createBiquadFilter();
    filter.type = "bandpass";
    filter.frequency.value = frequency;
    filter.Q.value = breath ? .82 : 2.4;
    const envelope = context.createGain();
    source.connect(filter).connect(envelope).connect(room);
    envelope.gain.setValueAtTime(floor, start);
    envelope.gain.exponentialRampToValueAtTime(level, start + (breath ? .105 : .005));
    if (breath) envelope.gain.setValueAtTime(level, start + seconds * .65);
    envelope.gain.exponentialRampToValueAtTime(floor, start + seconds);
    source.onended = () => { source.disconnect(); filter.disconnect(); envelope.disconnect(); };
    source.start(start);
    source.stop(start + seconds);
  }

  return (room, start, bar) => {
    const [plucks, winds, wood] = score[bar % score.length];
    for (let i = 0; i < plucks.length; i += 2) {
      const at = start + plucks[i] * beat;
      const pitch = plucks[i + 1], length = pitch < 60 ? 2.65 : 1.92;
      const { oscillator, envelope } = voice(room, pitch, strings);
      const filter = context.createBiquadFilter();
      filter.frequency.setValueAtTime(pitch < 60 ? 2400 : 4900, at);
      filter.frequency.exponentialRampToValueAtTime(pitch < 60 ? 500 : 1150, at + .53);
      oscillator.connect(filter).connect(envelope);
      oscillator.detune.setValueAtTime(i % 4 ? -14 : 6, at);
      oscillator.detune.linearRampToValueAtTime(0, at + .12);
      const level = i === 0 ? .21 : .145;
      envelope.gain.setValueAtTime(floor, at);
      envelope.gain.exponentialRampToValueAtTime(level, at + .012);
      envelope.gain.exponentialRampToValueAtTime(level * .34, at + .24);
      envelope.gain.exponentialRampToValueAtTime(floor, at + length);
      oscillator.onended = () => { oscillator.disconnect(); filter.disconnect(); envelope.disconnect(); };
      oscillator.start(at);
      oscillator.stop(at + length);
    }
    for (let i = 0; i < winds.length; i += 3) {
      const at = start + winds[i] * beat, length = winds[i + 2] * beat;
      const { oscillator, envelope } = voice(room, winds[i + 1], flute);
      const vibrato = context.createOscillator(), depth = context.createGain();
      vibrato.frequency.value = 4.8;
      depth.gain.setValueAtTime(0, at);
      depth.gain.linearRampToValueAtTime(length > 1 ? 18 : 8, at + .29);
      vibrato.connect(depth).connect(oscillator.detune);
      oscillator.detune.setValueAtTime(-48, at);
      oscillator.detune.linearRampToValueAtTime(0, at + .11);
      oscillator.connect(envelope);
      envelope.gain.setValueAtTime(floor, at);
      envelope.gain.exponentialRampToValueAtTime(.108, at + .11);
      envelope.gain.linearRampToValueAtTime(.079, at + length * .68);
      envelope.gain.exponentialRampToValueAtTime(floor, at + length);
      texture(room, at, length, 2440, .021, true);
      oscillator.onended = () => { oscillator.disconnect(); vibrato.disconnect(); depth.disconnect(); envelope.disconnect(); };
      for (const source of [oscillator, vibrato]) {
        source.start(at);
        source.stop(at + length);
      }
    }
    for (const tick of wood) texture(room, start + tick * beat, .095, tick ? 1060 : 740, .078);
    return beat * 4;
  };
}
