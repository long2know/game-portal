import { createHash } from "node:crypto";
import { expect, test } from "@playwright/test";
import type { Page, WebSocketRoute } from "@playwright/test";
import type { ClientMessage, Guest, Meld, Seat, Snapshot } from "../src/protocol";

const online = '.connection[data-status="online"]';
const clipPattern = /\/(cha-english|(?:chi|peng|hu)-(?:lower|higher))-[\w-]+\.mp3$/;
const seats: Seat[] = [0, 1, 2, 3];
const words = ["chi", "peng", "hu"] as const;
const voiceClips = (voice: string) => words.map(word => `${word}-${voice}`);

interface Playback {
  name: string;
  fingerprint: number;
  simultaneous: number;
  running: boolean;
}
interface AudioProbe {
  decoded: string[];
  pendingLocal: number;
  playback: Playback[];
  active: number;
  rms: number;
  duration: number;
  cueStrikes: number;
  auditions: number[];
  deviceCalls: string[];
}

async function observeCalls(page: Page) {
  await page.addInitScript(pattern => {
    const mp3 = new RegExp(pattern);
    const inputs = new WeakMap<ArrayBuffer, string>(), buffers = new WeakMap<AudioBuffer, string>();
    const decoded: string[] = [], playback: Playback[] = [];
    const active = new Set<AudioBufferSourceNode>(), analysers: AnalyserNode[] = [];
    const auditions: number[] = [], deviceCalls: string[] = [];
    let duration = 0, cueStrikes = 0, pendingLocal = 0;
    let held: Promise<void> | undefined, release: (() => void) | undefined;
    Object.assign(window, { delayLocalCalls: (delay: boolean) => {
      if (delay) held = new Promise<void>(resolve => { release = resolve; });
      else { release?.(); held = release = undefined; }
    } });
    const play = HTMLMediaElement.prototype.play;
    HTMLMediaElement.prototype.play = function () {
      return play.call(this).then(() => { if (this.src.startsWith("blob:")) auditions.push(this.duration); });
    };
    if (typeof SpeechSynthesis !== "undefined") {
      const speak = SpeechSynthesis.prototype.speak;
      SpeechSynthesis.prototype.speak = function (utterance) { deviceCalls.push("speech"); speak.call(this, utterance); };
    }
    if (typeof MediaDevices !== "undefined") {
      const capture = MediaDevices.prototype.getUserMedia;
      MediaDevices.prototype.getUserMedia = function (constraints) { deviceCalls.push("capture"); return capture.call(this, constraints); };
    }
    const fetchAudio = window.fetch;
    window.fetch = async (...args) => {
      const response = await fetchAudio(...args);
      const name = mp3.exec(new URL(response.url).pathname)?.[1];
      if (name) {
        const bytes = response.arrayBuffer.bind(response);
        response.arrayBuffer = async () => {
          const input = await bytes();
          inputs.set(input, name);
          return input;
        };
      }
      return response;
    };
    const decode = BaseAudioContext.prototype.decodeAudioData;
    BaseAudioContext.prototype.decodeAudioData = function (input, success, failure) {
      const view = new DataView(input);
      const marker = view.byteLength > 46 && view.getUint32(0) === 0x52494646 ? view.getInt16(44, true) : 0;
      const local = !("startRendering" in this) && ["local-chi", "local-peng", "local-hu"][marker - 3101];
      if (local) pendingLocal++;
      const decodeInput = () => decode.call(this, input, success, failure);
      return (local && held ? held.then(decodeInput) : decodeInput()).then(buffer => {
        const name = inputs.get(input) ?? local;
        if (name) {
          buffers.set(buffer, name);
          decoded.push(name);
        }
        return buffer;
      });
    };
    const connect = AudioNode.prototype.connect;
    AudioNode.prototype.connect = function (this: AudioNode, destination: AudioNode | AudioParam, ...args: number[]) {
      if (destination instanceof AudioDestinationNode) {
        const analyser = this.context.createAnalyser();
        analysers.push(analyser);
        Reflect.apply(connect, this, [analyser, ...args]);
        Reflect.apply(connect, analyser, [destination]);
        return destination;
      }
      return Reflect.apply(connect, this, [destination, ...args]);
    } as typeof AudioNode.prototype.connect;
    const { start, stop } = AudioBufferSourceNode.prototype;
    AudioBufferSourceNode.prototype.start = function (...args: Parameters<typeof start>) {
      start.apply(this, args);
      const name = this.buffer && buffers.get(this.buffer);
      if (name) {
        active.add(this);
        this.addEventListener("ended", () => active.delete(this), { once: true });
        duration = this.buffer!.duration;
        const pcm = this.buffer!.getChannelData(0);
        let fingerprint = pcm.length;
        for (let i = 0; i < pcm.length; i += 117) fingerprint = Math.imul(fingerprint, 31) + Math.round(pcm[i] * 16384) | 0;
        playback.push({ name, fingerprint, simultaneous: active.size, running: this.context.state === "running" });
      } else if (this.buffer && this.buffer.duration < .1) cueStrikes++;
    };
    AudioBufferSourceNode.prototype.stop = function (...args: Parameters<typeof stop>) {
      stop.apply(this, args);
      active.delete(this);
    };
    Object.assign(window, { mahjongAudio: () => {
      const rms = Math.max(0, ...analysers.map(analyser => {
        const values = new Float32Array(analyser.fftSize);
        analyser.getFloatTimeDomainData(values);
        return Math.sqrt(values.reduce((sum, value) => sum + value * value, 0) / values.length);
      }));
      return { decoded, pendingLocal, playback, active: active.size, rms, duration, cueStrikes, auditions, deviceCalls };
    } });
  }, clipPattern.source);
  return () => page.evaluate(() => (window as unknown as { mahjongAudio: () => AudioProbe }).mahjongAudio());
}

function initial(): Snapshot {
  const now = Date.now();
  return {
    v: 1, type: "snapshot", roomId: "audioFixtureRoom000001", roomRevision: 1, gameRevision: 1,
    serverTimeUtc: new Date(now).toISOString(),
    room: {
      status: "playing", visibility: "invite", hostSeat: 0, matchHands: 4, paused: false, autoResumeAtUtc: null,
      rulesVersion: "changsha-v1", chat: [],
      seats: seats.map(seat => ({ seat, kind: seat ? "bot" : "human", displayName: `Fixture ${seat}`, ready: true, connected: !seat })),
    },
    game: {
      handNumber: 1, dealerSeat: 0, phase: "claim", turnSeat: null, wallCount: 60, settlement: null,
      window: { windowId: "offer", anchorGameRevision: 1, reason: "discard", trigger: { seat: 3, faces: ["b2"] },
        deadlineUtc: new Date(now + 30000).toISOString() },
      seats: seats.map(seat => ({ seat, concealedCount: 13, discards: [], melds: [], revealedTiles: [], total: 0 })),
    },
    self: { seat: 0, inviteCode: null, hand: [{ face: "b1", tileId: "b1" }, { face: "b3", tileId: "b3" }],
      drawnTileId: null, legalOptions: [] },
  };
}

function meld(state: Snapshot, seat: Seat, kind: "chi" | "peng") {
  const committed: Meld = { kind, method: "open", fromSeat: (seat + 3) % 4 as Seat,
    tiles: kind === "chi" ? ["b1", "b2", "b3"] : ["d6", "d6", "d6"] };
  state.game!.seats[seat].melds.push(committed);
}

function hu(state: Snapshot) {
  const game = state.game!;
  game.phase = "settled";
  game.turnSeat = game.window = null;
  state.room.status = "betweenHands";
  game.settlement = {
    reason: "win", sourceSeat: 0, nextDealerSeat: 0, birds: [],
    winners: ([1, 2] as const).map(seat => ({ seat, winBy: "discard", winningTiles: ["d8"], patterns: [] })),
    seatResults: seats.map(seat => ({ seat, delta: seat === 0 ? -12 : seat < 3 ? 6 : 0, total: seat === 0 ? -12 : seat < 3 ? 6 : 0 })),
  };
}

// Only guest/transport data is stubbed; the installed Web Audio engine decodes and plays the shipped MP3s.
async function fixture(page: Page, child: string) {
  let state = initial(), wire: WebSocketRoute;
  let guest: Guest = { displayName: "Audio fixture", resumeRoomId: state.roomId,
    preferences: { sound: true, music: false, volume: .72, reducedMotion: true } };
  const choices: string[] = [];
  await page.route("**/api/guest", route => route.fulfill({ json: guest }));
  await page.route("**/api/rooms", route => route.fulfill({ json: { rooms: [] } }));
  await page.route("**/api/profile", route => {
    const update = route.request().postDataJSON() as Pick<Guest, "displayName" | "preferences">;
    guest = { ...guest, ...update };
    return route.fulfill({ json: guest });
  });
  const send = () => wire.send(JSON.stringify({ ...state, serverTimeUtc: new Date().toISOString() }));
  await page.routeWebSocket("**/ws", socket => {
    wire = socket;
    const future = new Date(Date.now() + 180000).toISOString();
    socket.onMessage(raw => {
      const message = JSON.parse(String(raw)) as ClientMessage;
      if (!("requestId" in message)) return;
      if (message.type === "command" && message.op === "choose") choices.push(message.payload.optionId);
      socket.send(JSON.stringify({ v: 1, type: "ack", requestId: message.requestId, status: "ok", error: null,
        roomId: state.roomId, roomRevision: state.roomRevision, gameRevision: state.gameRevision }));
      if (message.type === "sync") send();
    });
    socket.send(JSON.stringify({ v: 1, type: "lease", lease: "fixture", expiresAtUtc: future, serverTimeUtc: new Date().toISOString() }));
  });
  await page.goto(child);
  const resume = page.getByRole("button", { name: "Resume your table" });
  await expect(resume).toBeEnabled();
  await resume.evaluate(control => (control as HTMLButtonElement).click());
  await expect(page.locator(online)).toBeVisible();
  return {
    choices,
    duplicate: send,
    async commit(change: (next: Snapshot) => void = () => {}, reconnect = false) {
      state = structuredClone(state);
      state.roomRevision++;
      state.gameRevision++;
      state.game!.wallCount--;
      change(state);
      if (reconnect) wire.close({ code: 1012, reason: "fixture resync" });
      else send();
      await expect(page.locator(".wall-count")).toHaveText(String(state.game!.wallCount));
      if (reconnect) await expect(page.locator(online)).toBeVisible();
    },
  };
}

const button = (page: Page, name: string) => page.getByRole("button", { name, exact: true });
const dialog = (page: Page) => page.getByRole("dialog", { name: "Settings" });
const voice = (page: Page) => dialog(page).getByRole("combobox", { name: "Spoken table calls", exact: true });
const changshaOption = (page: Page) => voice(page).locator('option[value="changsha"]');
const savePack = (page: Page) => button(page, "Save recordings on this device");
const played = async (read: () => Promise<AudioProbe>) => (await read()).playback.map(event => event.name);

function wave(marker: number, seconds = .5, amplitude = .28) {
  const rate = 24000, samples = Math.floor(seconds * rate);
  const buffer = Buffer.alloc(44 + samples * 2);
  buffer.write("RIFF", 0); buffer.writeUInt32LE(buffer.length - 8, 4); buffer.write("WAVEfmt ", 8);
  buffer.writeUInt32LE(16, 16); buffer.writeUInt16LE(1, 20); buffer.writeUInt16LE(1, 22);
  buffer.writeUInt32LE(rate, 24); buffer.writeUInt32LE(rate * 2, 28);
  buffer.writeUInt16LE(2, 32); buffer.writeUInt16LE(16, 34); buffer.write("data", 36);
  buffer.writeUInt32LE(samples * 2, 40);
  for (let i = 0; i < samples; i++) buffer.writeInt16LE(Math.round(amplitude * 32767 * Math.sin(i * marker / rate)), 44 + i * 2);
  if (marker) buffer.writeInt16LE(marker, 44);
  return buffer;
}

const recordings = {
  Cha: { name: "fixture-Cha.wav", mimeType: "audio/wav", buffer: wave(3101) },
  Peng: { name: "fixture-Peng.wav", mimeType: "audio/wav", buffer: wave(3102) },
  Hu: { name: "fixture-Hu.wav", mimeType: "audio/wav", buffer: wave(3103) },
};
const input = (page: Page, name: string) => dialog(page).getByLabel(`${name} recording`, { exact: true });

async function openPack(page: Page) {
  await button(page, "Settings").tap();
  await dialog(page).getByText("Configure Changsha recordings", { exact: true }).tap();
  await expect(input(page, "Cha")).toBeEnabled();
}

async function fillPack(page: Page) {
  for (const [label, file] of Object.entries(recordings)) await input(page, label).setInputFiles(file);
}

type Stored = { keys: string[]; sizes?: number[]; hashes?: string[]; types?: string[]; fields?: string[][] };
async function inspectPack(page: Page, key: string, seed?: "foreign" | "corrupt"): Promise<Stored> {
  const waves = seed && Object.values(recordings).map(file => file.buffer.toString("base64"));
  return page.evaluate(async ({ key, waves, seed }) => {
    const db = await new Promise<IDBDatabase>((resolve, reject) => {
      const request = indexedDB.open("jade-court-changsha-calls");
      request.onsuccess = () => resolve(request.result);
      request.onerror = () => reject(request.error);
    });
    const clip = (bytes: string) => ({ bytes: Uint8Array.from(atob(bytes), char => char.charCodeAt(0)).buffer, type: "audio/wav" });
    const saved = await new Promise<{ keys: string[]; clips?: ReturnType<typeof clip>[] }>((resolve, reject) => {
      const tx = db.transaction("recordings", waves ? "readwrite" : "readonly"), store = tx.objectStore("recordings");
      if (waves) store.put({ chi: clip(waves[0]), peng: clip(seed === "corrupt" ? "AwUH" : waves[1]), hu: clip(waves[2]) }, key);
      const keys = store.getAllKeys();
      const value = store.get(key);
      tx.oncomplete = () => resolve({ keys: keys.result as string[],
        clips: value.result && [value.result.chi, value.result.peng, value.result.hu] });
      tx.onabort = () => reject(tx.error);
    }).finally(() => db.close());
    return {
      keys: saved.keys,
      sizes: saved.clips?.map(clip => clip.bytes.byteLength),
      hashes: saved.clips && await Promise.all(saved.clips.map(async clip => {
        const digest = await crypto.subtle.digest("SHA-256", clip.bytes);
        return [...new Uint8Array(digest)].map(byte => byte.toString(16).padStart(2, "0")).join("");
      })),
      types: saved.clips?.map(clip => clip.type),
      fields: saved.clips?.map(clip => Object.keys(clip).sort()),
    };
  }, { key, waves, seed });
}

async function setVoice(page: Page, value: string, sound = true, volumeKey?: "Home" | "End") {
  await button(page, "Settings").tap();
  if (value === "changsha") await expect(changshaOption(page)).toBeEnabled();
  await voice(page).selectOption(value);
  await dialog(page).getByLabel("Tile and turn sounds").setChecked(sound);
  if (volumeKey) {
    await dialog(page).getByRole("slider").focus();
    await page.keyboard.press(volumeKey);
    await expect(dialog(page).getByRole("slider")).toHaveValue(volumeKey === "Home" ? "0" : "100");
  }
  await button(page, "Save settings").tap();
  await expect(dialog(page).getByText("Settings saved.", { exact: true })).toBeVisible();
  await button(page, "Close dialog").tap();
}

async function visible(page: Page, visible: boolean) {
  await page.evaluate(visible => {
    Object.defineProperty(document, "hidden", { configurable: true, get: () => !visible });
    document.dispatchEvent(new Event("visibilitychange"));
  }, visible);
}

test.beforeEach(({ baseURL }) => {
  test.skip(!baseURL || !/^\/(?:game-)?portal\/?$/.test(new URL(baseURL).pathname), "Requires the published portal mount.");
});

test("committed calls play the selected Mandarin PCM exactly once, independently of motion", async ({ page, baseURL }, info) => {
  const read = await observeCalls(page);
  const requests: string[] = [];
  page.on("request", request => {
    const path = new URL(request.url()).pathname;
    if (/\/(?:callouts|table-sounds)-|\.mp3$/.test(path)) requests.push(path);
  });
  const wire = await fixture(page, new URL("mahjong/", baseURL).href);
  expect(requests).toEqual([]);
  await button(page, "Settings").tap();
  await expect(voice(page)).toHaveValue("off");
  await expect(voice(page).locator('option[value="mixed"]')).toHaveText("English Cha · Mandarin Peng/Hu");
  await voice(page).selectOption("lower");
  await button(page, "Close dialog").tap();
  expect(requests.filter(path => clipPattern.test(path))).toEqual([]);
  await button(page, "Settings").tap();
  await expect(voice(page)).toHaveValue("off");
  await button(page, "Close dialog").tap();
  await setVoice(page, "lower");
  await expect.poll(async () => new Set((await read()).decoded)).toEqual(new Set(voiceClips("lower")));

  await wire.commit(state => {
    state.self.legalOptions = [{ kind: "chi", optionId: "choose-cha", tileIds: ["b1", "b3"], faces: ["b1", "b2", "b3"] }];
  });
  const option = page.getByRole("button", { name: /^Cha:/ });
  await expect(option).toBeEnabled();
  expect(await played(read)).toEqual([]);
  await option.tap();
  await expect.poll(() => wire.choices).toEqual(["choose-cha"]);
  await wire.commit(state => { state.self.legalOptions = []; });
  expect(await played(read)).toEqual([]);

  await wire.commit(state => meld(state, 1, "chi"));
  await expect.poll(() => played(read)).toEqual(["chi-lower"]);
  await expect.poll(async () => (await read()).rms).toBeGreaterThan(.009);
  await expect(page.locator('[data-seat="1"] .seat-feedback')).toHaveText("Cha");
  await expect(page.locator("html")).toHaveAttribute("data-reduced-motion", "true");
  await expect(page.locator(".table-effects > *")).toHaveCount(0);
  wire.duplicate();
  await wire.commit();
  expect(await played(read)).toEqual(["chi-lower"]);
  await wire.commit(state => meld(state, 2, "peng"));
  await expect.poll(() => played(read)).toEqual(["chi-lower", "peng-lower"]);
  await wire.commit(hu);
  await expect.poll(() => played(read)).toEqual(voiceClips("lower"));
  await expect(page.getByRole("dialog", { name: "Hand results" })).toBeVisible();
  const lower = (await read()).playback;
  expect(lower.every(event => event.running && event.simultaneous === 1)).toBe(true);

  await button(page, "Close dialog").tap();
  await setVoice(page, "higher");
  requests.length = 0;
  await page.reload();
  await expect(page.locator(online)).toBeVisible();
  expect(requests).toEqual([]);
  expect(await played(read)).toEqual([]);
  await button(page, "Close dialog").tap(); // The cached two-winner settlement is not an announcement.
  await button(page, "Settings").tap();
  await expect(voice(page)).toHaveValue("higher");
  await button(page, "Close dialog").tap();
  await expect.poll(async () => new Set((await read()).decoded)).toEqual(new Set(voiceClips("higher")));
  await wire.commit(state => {
    const { game, room, self } = initial();
    state.game = { ...game!, handNumber: 2, wallCount: state.game!.wallCount };
    state.room = room;
    state.self = self;
  });

  await visible(page, false);
  await wire.commit(state => meld(state, 1, "chi"));
  expect(await played(read)).toEqual([]);
  await visible(page, true);
  await wire.commit(); // The first changed visible snapshot becomes the fresh effects baseline.
  await wire.commit(state => {
    state.room.paused = true;
    state.room.autoResumeAtUtc = new Date(Date.now() + 60000).toISOString();
    meld(state, 2, "peng");
  });
  await expect(button(page, "Resume")).toBeVisible();
  await wire.commit(state => {
    state.room.paused = false;
    state.room.autoResumeAtUtc = null;
  });
  await wire.commit(state => meld(state, 3, "peng"), true);
  expect(await played(read)).toEqual([]);

  await wire.commit(state => meld(state, 1, "chi"));
  await expect.poll(() => played(read)).toEqual(["chi-higher"]);
  await expect.poll(async () => (await read()).rms).toBeGreaterThan(.009);
  await wire.commit(state => meld(state, 2, "peng"));
  await expect.poll(() => played(read)).toEqual(["chi-higher", "peng-higher"]);
  await wire.commit(hu);
  await expect.poll(() => played(read)).toEqual(voiceClips("higher"));
  const higher = (await read()).playback;
  expect(higher.every((event, i) => event.running && event.simultaneous === 1 && event.fingerprint !== lower[i].fingerprint)).toBe(true);
  expect((await read()).duration).toBeGreaterThan(.25);
  await info.attach("decoded-voice-playback", { body: JSON.stringify({ lower, higher }, null, 2), contentType: "application/json" });

  await button(page, "Close dialog").tap();
  await wire.commit(state => {
    const { game } = initial();
    state.game = { ...game!, handNumber: 3, wallCount: state.game!.wallCount };
    state.room.status = "playing";
  });
  await setVoice(page, "off");
  const cueCount = (await read()).cueStrikes;
  await wire.commit(state => meld(state, 2, "chi"));
  await expect.poll(async () => (await read()).cueStrikes).toBeGreaterThan(cueCount);
  await setVoice(page, "higher", false);
  await wire.commit(state => meld(state, 2, "peng"));
  await setVoice(page, "higher", true, "Home");
  await wire.commit(state => meld(state, 3, "peng"));
  expect(await played(read)).toEqual(voiceClips("higher"));
  await expect.poll(async () => (await read()).rms).toBeLessThan(.001);
  await setVoice(page, "mixed", true, "End");
  await expect.poll(async () => (await read()).decoded).toContain("cha-english");
  await wire.commit(state => meld(state, 3, "chi"));
  await expect.poll(() => played(read)).toEqual([...voiceClips("higher"), "cha-english"]);
  const spoken = (await read()).playback.at(-1)!;
  expect(spoken).toMatchObject({ running: true, simultaneous: 1 });
  expect(spoken.fingerprint).not.toBe(lower[0].fingerprint);
  expect(spoken.fingerprint).not.toBe(higher[0].fingerprint);
  const englishClip = requests.find(path => /\/cha-english-[\w-]+\.mp3$/.test(path));
  expect(englishClip).toBeDefined();
  const shipped = await page.request.get(new URL(englishClip!, baseURL).href);
  expect(shipped.ok()).toBe(true);
  expect(createHash("sha256").update(await shipped.body()).digest("hex"))
    .toBe("4c5d494a23a16758367082775bb5888d5a52a80d2afaef4c140aefc024535c65");
});

test("Changsha recording drafts are validated and saved privately for this mount without enabling the voice", async ({ page, baseURL }, info) => {
  const child = new URL("mahjong/", baseURL), key = `${child.pathname}audio`;
  const otherKey = child.pathname.startsWith("/portal/") ? "/game-portal/mahjong/audio" : "/portal/mahjong/audio";
  const read = await observeCalls(page);
  const requests: { url: string; method: string; body: string | null }[] = [];
  page.on("request", request => requests.push({ url: request.url(), method: request.method(), body: request.postData() }));
  await fixture(page, child.href);
  expect(requests.some(item => /\/(?:changsha-(?:calls|settings)|callouts)-|\.mp3$/.test(item.url))).toBe(false);
  await openPack(page);
  await expect(changshaOption(page)).toHaveText("Changsha dialect (your recordings)");
  await expect(changshaOption(page)).toBeDisabled();
  await expect(savePack(page)).toBeDisabled();
  await expect(input(page, "Cha")).toHaveAttribute("accept", /\.m4a,.mp3,.wav/);
  expect((await inspectPack(page, key)).keys).toEqual([]);
  await button(page, "Close dialog").tap();

  await inspectPack(page, otherKey, "foreign");
  await openPack(page);
  await expect(dialog(page).getByText(/^No Changsha recordings saved\./)).toBeVisible();
  await expect(changshaOption(page)).toBeDisabled();
  await input(page, "Cha").setInputFiles(recordings.Cha);
  await expect(button(page, "Audition Cha")).toBeEnabled();
  await button(page, "Audition Cha").tap();
  await expect.poll(async () => (await read()).auditions).toEqual([.5]);
  await input(page, "Peng").setInputFiles({ name: "large.m4a", mimeType: "audio/mp4", buffer: Buffer.alloc(192 * 1024 + 1) });
  await expect(dialog(page).getByText(/no larger than 192 KiB/)).toBeVisible();
  await input(page, "Peng").setInputFiles({ ...recordings.Peng, buffer: wave(3102, 2.25) });
  await expect(dialog(page).getByText(/between 0.1 and 2 seconds/)).toBeVisible();
  await input(page, "Peng").setInputFiles({ ...recordings.Peng, buffer: wave(0, .5, 0) });
  await expect(dialog(page).getByText(/silent or too quiet/)).toBeVisible();
  await input(page, "Peng").setInputFiles({ ...recordings.Peng, buffer: Buffer.from("not audio") });
  await expect(dialog(page).getByText(/couldn’t decode the clip/)).toBeVisible();
  await input(page, "Hu").setInputFiles(recordings.Hu);
  await expect(button(page, "Audition Hu")).toBeEnabled();
  await expect(savePack(page)).toBeDisabled();
  await expect(changshaOption(page)).toBeDisabled();
  await button(page, "Close dialog").tap();
  expect(await inspectPack(page, key)).toEqual({ keys: [otherKey] });

  await openPack(page);
  await expect(button(page, "Audition Cha")).toBeDisabled();
  await fillPack(page);
  await savePack(page).tap();
  await expect(dialog(page).getByText("Recordings saved on this device. Your voice choice is unchanged.")).toBeVisible();
  await expect(changshaOption(page)).toBeEnabled();
  await expect(voice(page)).toHaveValue("off");
  expect(await page.evaluate(key => localStorage.getItem(key), key)).toBeNull();
  await voice(page).selectOption("changsha");
  await button(page, "Close dialog").tap();
  await page.reload();
  await expect(page.locator(online)).toBeVisible();
  await openPack(page);
  await expect(voice(page)).toHaveValue("off");
  await expect(changshaOption(page)).toBeEnabled();
  await expect(button(page, "Audition Peng")).toBeEnabled();
  await button(page, "Audition Peng").tap();
  await expect.poll(async () => (await read()).auditions).toEqual([.5]);
  const stored = await inspectPack(page, key);
  expect(stored).toEqual({ keys: [key, otherKey].sort(),
    sizes: [24044, 24044, 24044], types: ["audio/wav", "audio/wav", "audio/wav"],
    fields: [["bytes", "type"], ["bytes", "type"], ["bytes", "type"]],
    hashes: Object.values(recordings).map(file => createHash("sha256").update(file.buffer).digest("hex")),
  });
  expect(requests.filter(item => {
    const url = new URL(item.url);
    if (url.origin !== child.origin || /\.mp3$/.test(url.pathname)) return true;
    if (item.method === "GET") return false;
    if (item.method !== "POST") return true;
    if (url.href === new URL("api/guest", baseURL).href) return item.body !== "{}";
    if (url.href === new URL("api/social/presence", baseURL).href) {
      const { tabId, ...body } = JSON.parse(item.body ?? "{}");
      return typeof tabId !== "string" || body.active !== false || body.busy !== false || Object.keys(body).length !== 2;
    }
    return true;
  })).toEqual([]);
  expect((await read()).deviceCalls).toEqual([]);
  await info.attach("private-recording-readback", { body: JSON.stringify(stored), contentType: "application/json" });
  await button(page, "Delete saved recordings").tap();
  await expect(changshaOption(page)).toBeDisabled();
  expect(await inspectPack(page, key)).toEqual({ keys: [otherKey] });
});

test("Changsha PCM follows fresh commits only and falls back to table cues while delayed, absent or invalid", async ({ page, baseURL }, info) => {
  const child = new URL("mahjong/", baseURL), key = `${child.pathname}audio`;
  const read = await observeCalls(page);
  const clips: string[] = [];
  page.on("request", request => { if (clipPattern.test(new URL(request.url()).pathname)) clips.push(request.url()); });
  const wire = await fixture(page, child.href);
  await openPack(page);
  await fillPack(page);
  await savePack(page).tap();
  await expect(changshaOption(page)).toBeEnabled();
  await button(page, "Close dialog").tap();
  const delay = (set: boolean) => page.evaluate(set =>
    (window as unknown as { delayLocalCalls: (set: boolean) => void }).delayLocalCalls(set), set);
  const cueInstead = async (change: (state: Snapshot) => void) => {
    const before = await read();
    await wire.commit(change);
    await expect.poll(async () => (await read()).cueStrikes).toBeGreaterThan(before.cueStrikes);
    expect(await played(read)).toEqual(before.playback.map(event => event.name));
  };
  await delay(true);
  await setVoice(page, "changsha");
  await expect.poll(async () => (await read()).pendingLocal).toBe(3);
  await cueInstead(state => meld(state, 1, "chi"));
  await delay(false);
  await expect.poll(async () => new Set((await read()).decoded)).toEqual(new Set(["local-chi", "local-peng", "local-hu"]));
  wire.duplicate();
  await wire.commit();
  expect(await played(read)).toEqual([]);

  await wire.commit(state => meld(state, 2, "chi"));
  await expect.poll(() => played(read)).toEqual(["local-chi"]);
  await expect.poll(async () => (await read()).rms).toBeGreaterThan(.009);
  await wire.commit(state => meld(state, 3, "peng"));
  await expect.poll(() => played(read)).toEqual(["local-chi", "local-peng"]);
  await wire.commit(hu);
  await expect.poll(() => played(read)).toEqual(["local-chi", "local-peng", "local-hu"]);
  const local = (await read()).playback;
  expect(new Set(local.map(event => event.fingerprint)).size).toBe(3);
  expect(local.every(event => event.running && event.simultaneous === 1)).toBe(true);
  await info.attach("local-decoded-voice-playback", { body: JSON.stringify(local), contentType: "application/json" });
  await expect(page.getByRole("dialog", { name: "Hand results" })).toBeVisible();
  await button(page, "Close dialog").tap();
  await wire.commit(state => {
    const { game, room, self } = initial();
    state.game = { ...game!, handNumber: 2, wallCount: state.game!.wallCount };
    state.room = room;
    state.self = self;
  });
  await setVoice(page, "changsha", false);
  const muted = await read();
  await wire.commit(state => meld(state, 1, "peng"));
  expect((await read()).cueStrikes).toBe(muted.cueStrikes);
  expect(await played(read)).toEqual(local.map(event => event.name));
  await setVoice(page, "changsha");
  await visible(page, false);
  await wire.commit(state => meld(state, 2, "peng"));
  expect(await played(read)).toEqual(local.map(event => event.name));
  await visible(page, true);
  await wire.commit();

  await openPack(page);
  await button(page, "Delete saved recordings").tap();
  await expect(dialog(page).getByText(/^No Changsha recordings saved\./)).toBeVisible();
  await expect(voice(page)).toHaveValue("changsha");
  await expect(changshaOption(page)).toBeDisabled();
  await button(page, "Close dialog").tap();
  await cueInstead(state => meld(state, 3, "chi"));
  expect(await page.evaluate(key => localStorage.getItem(key), key)).toBe("still:changsha");

  await inspectPack(page, key, "corrupt");
  await page.reload();
  await expect(page.locator(online)).toBeVisible();
  await openPack(page);
  await expect(dialog(page).getByText(/^Saved Changsha recordings couldn’t be read or played\./)).toBeVisible();
  await expect(voice(page)).toHaveValue("changsha");
  await expect(changshaOption(page)).toBeDisabled();
  await expect(button(page, "Audition Cha")).toBeDisabled();
  await expect.poll(async () => (await read()).pendingLocal).toBe(2);
  await button(page, "Close dialog").tap();
  await cueInstead(state => meld(state, 1, "chi"));
  expect(await played(read)).toEqual([]);
  expect(clips).toEqual([]);
  expect((await read()).deviceCalls).toEqual([]);
});

test("a real published Mahjong game speaks a committed call", async ({ page, baseURL, browserName }, info) => {
  test.setTimeout(105000);
  const viewport = browserName === "chromium" ? { width: 320, height: 730 } : { width: 740, height: 320 };
  await page.setViewportSize(viewport);
  if (viewport.width === 740) {
    await page.addInitScript(() => {
      document.addEventListener("DOMContentLoaded", () => {
        for (const [edge, inset] of [["t", 10], ["r", 22], ["b", 13], ["l", 25]]) {
          document.documentElement.style.setProperty(`--safe-${edge}`, `${inset}px`);
        }
      });
    });
  }
  const read = await observeCalls(page);
  let current: Snapshot | undefined;
  const commits: { kind: string; seat: Seat; bot: boolean; revision: number }[] = [];
  const record = (frame: Snapshot, kind: string, seat: Seat) =>
    commits.push({ kind, seat, bot: frame.room.seats.some(player => player.seat === seat && player.kind === "bot"), revision: frame.gameRevision });
  page.on("websocket", socket => {
    if (!new URL(socket.url()).pathname.endsWith("/ws")) return;
    socket.on("framereceived", ({ payload }) => {
      const frame = JSON.parse(payload.toString()) as Snapshot;
      if (frame.type !== "snapshot") return;
      const prior = current;
      current = frame;
      if (!prior?.game || !frame.game || prior.gameRevision + 1 !== frame.gameRevision ||
        prior.roomId !== frame.roomId || prior.game.handNumber !== frame.game.handNumber) return;
      if (frame.game.settlement?.reason === "win" && !prior.game.settlement)
        record(frame, "hu", frame.game.settlement.winners[0].seat);
      for (const seat of frame.game.seats) {
        const previous = prior.game.seats.find(item => item.seat === seat.seat)!;
        for (const call of seat.melds.slice(previous.melds.length)) {
          if (call.kind === "chi" || call.kind === "peng") record(frame, call.kind, seat.seat);
        }
      }
    });
  });
  await page.goto(new URL("mahjong/", baseURL).href);
  await expect(page.locator(online)).toBeVisible();
  const selected = browserName === "chromium" ? "lower" : "higher";
  await setVoice(page, selected);
  await button(page, "Play with friends").tap();
  const create = page.getByRole("dialog", { name: "Create a table" });
  await create.getByLabel("Match length").selectOption({ label: "Four hands" });
  await create.getByLabel("Fill the other three seats with bots").check();
  await button(page, "Create table").tap();
  await expect(page.getByRole("heading", { name: "Take a seat." })).toBeVisible();
  await expect(page.getByRole("main").getByText("Two birds (house rules)", { exact: true })).toBeVisible();
  expect(current?.room).toMatchObject({ rulesVersion: "changsha-two-bird-v1", matchHands: 4 });
  const ready = button(page, "Mark ready");
  if (await ready.isVisible()) await ready.tap();
  await button(page, "Start match").tap();
  await expect.poll(async () => new Set((await read()).decoded)).toEqual(new Set(voiceClips(selected)));
  const handled = new Set<string>();
  const deadline = Date.now() + 65000;
  let result: Playback | undefined;
  const exposed = () => current?.game?.seats.find(row => row.seat !== current?.self.seat &&
    row.melds.some(meld => meld.method !== "concealed" && meld.tiles.some(face => face !== null)));
  // Give the opponents the claim opportunities, even when the guest could claim or win.
  const priority = ["reveal", "draw", "discard", "pass"];
  while (Date.now() < deadline) {
    result ??= (await read()).playback[0];
    if ((result && exposed()) || current?.room.status === "complete") break;
    if (current?.room.status === "betweenHands") {
      const hand = current.game!.handNumber;
      await page.getByRole("dialog", { name: "Hand results" }).getByRole("button", { name: "Next hand" }).tap();
      await expect.poll(() => current?.game?.handNumber).toBe(hand + 1);
      continue;
    }
    const decision = current?.game?.window;
    if (decision && !handled.has(decision.windowId)) {
      const option = current!.self.legalOptions.filter(option => priority.includes(option.kind))
        .sort((a, b) => priority.indexOf(a.kind) - priority.indexOf(b.kind))[0];
      if (option) {
        try {
          if (option.kind === "discard") {
            const tile = page.locator(`[data-tile-id=${JSON.stringify(option.tileIds[0])}]`);
            if (await tile.getAttribute("aria-pressed", { timeout: 500 }) !== "true") await tile.tap({ timeout: 700 });
            await page.getByRole("button", { name: /^Discard [1-9] / }).tap({ timeout: 850 });
          } else await page.locator(`[data-option="${option.kind}"]`).first().tap({ timeout: 850 });
          handled.add(decision.windowId);
        } catch (error) {
          if (!(error instanceof Error) || error.name !== "TimeoutError") throw error;
        }
      }
    }
    await page.waitForTimeout(100);
  }
  result ??= (await read()).playback[0];
  expect(result, `Real claim/win commits: ${JSON.stringify(commits)}`).toBeDefined();
  expect(result).toMatchObject({ running: true, simultaneous: 1 });
  expect(voiceClips(selected)).toContain(result!.name);
  const kind = result!.name.split("-")[0];
  expect(commits).toEqual(expect.arrayContaining([expect.objectContaining({ kind })]));
  await info.attach("real-mahjong-call", { body: JSON.stringify({ result, commits }, null, 2), contentType: "application/json" });

  if (current?.room.status === "playing") {
    await button(page, "Pause").tap();
    await expect.poll(() => current?.room.paused || current?.room.status !== "playing").toBe(true);
  }
  if (current?.room.status === "complete" || current?.room.status === "betweenHands") {
    await expect(page.getByRole("dialog", { name: current.room.status === "complete" ? "Match results" : "Hand results" })).toBeVisible();
    await button(page, "Close dialog").tap();
  }
  const row = exposed();
  expect(row, `An actual committed opponent meld in hand ${current?.game?.handNumber ?? "unknown"}`).toBeDefined();
  const meld = row!.melds.find(candidate => candidate.method !== "concealed" && candidate.tiles.some(face => face !== null))!;
  const face = meld.tiles.find(Boolean);
  const switcher = page.getByRole("button", { name: /^Exposed tiles/ });
  const viewer = page.getByRole("region", { name: "Exposed tiles" });
  const geometry = () => Promise.all([".board-plane", ".hand-grid", ".table-actions"].map(selector => page.locator(selector).boundingBox()));
  const before = await geometry();
  await expect(switcher).toBeInViewport({ ratio: 1 });
  await switcher.tap();
  const publicMark = viewer.locator(`.exposed-seat[data-key="${row!.seat}"] use[href$="#lt-${face}"]`).first();
  await expect(publicMark).toBeVisible();
  await expect.poll(() => publicMark.evaluate(node => (node as SVGGraphicsElement).getBBox().width)).toBeGreaterThan(0);
  const href = new URL((await publicMark.getAttribute("href"))!, page.url());
  expect(href.pathname).toMatch(new RegExp(`^${new URL("assets/", baseURL).pathname}tile-suits-[\\w-]+\\.svg$`));
  expect((await publicMark.locator("xpath=ancestor::*[@role='img']").boundingBox())!.width).toBeGreaterThanOrEqual(44);
  await expect(viewer.locator(".meld-concealed use")).toHaveCount(0);
  await page.screenshot({ path: info.outputPath(`live-exposed-${viewport.width}x${viewport.height}.png`) });
  await button(page, "Back to table").tap();
  await expect(switcher).toBeFocused();
  expect(await geometry()).toEqual(before);
});
