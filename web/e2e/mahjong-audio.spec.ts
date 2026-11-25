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
  playback: Playback[];
  active: number;
  rms: number;
  duration: number;
  cueStrikes: number;
}

async function observeCalls(page: Page) {
  await page.addInitScript(pattern => {
    const mp3 = new RegExp(pattern);
    const inputs = new WeakMap<ArrayBuffer, string>(), buffers = new WeakMap<AudioBuffer, string>();
    const decoded: string[] = [], playback: Playback[] = [];
    const active = new Set<AudioBufferSourceNode>(), analysers: AnalyserNode[] = [];
    let duration = 0, cueStrikes = 0;
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
      return decode.call(this, input, success, failure).then(buffer => {
        const name = inputs.get(input);
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
      return { decoded, playback, active: active.size, rms, duration, cueStrikes };
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
const played = async (read: () => Promise<AudioProbe>) => (await read()).playback.map(event => event.name);

async function setVoice(page: Page, value: string, sound = true, volumeKey?: "Home" | "End") {
  await button(page, "Settings").tap();
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
});

test("a real published Mahjong game speaks a committed call", async ({ page, baseURL, browserName }, info) => {
  test.setTimeout(80000);
  await page.setViewportSize({ width: 320, height: 730 });
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
  await button(page, "Play with bots").tap();
  await expect(page.getByRole("heading", { name: "Take a seat." })).toBeVisible();
  await expect(page.getByText("One bird (classic)", { exact: true })).toBeVisible();
  const ready = button(page, "Mark ready");
  if (await ready.isVisible()) await ready.tap();
  await button(page, "Start match").tap();
  await expect.poll(async () => new Set((await read()).decoded)).toEqual(new Set(voiceClips(selected)));
  const handled = new Set<string>();
  const deadline = Date.now() + 40000;
  let result: Playback | undefined;
  const exposed = () => current?.game?.seats.find(row => row.seat !== current?.self.seat &&
    row.melds.some(meld => meld.method !== "concealed" && meld.tiles.some(face => face !== null)));
  const priority = ["hu", "reveal", "peng", "chi", "draw", "discard", "pass"];
  while (Date.now() < deadline) {
    result ??= (await read()).playback[0];
    const decision = current?.game?.window;
    if ((result && exposed()) || current?.room.status === "complete") break;
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
    await expect.poll(() => current?.room.paused || current?.room.status === "complete").toBe(true);
  }
  if (current?.room.status === "complete") {
    await expect(page.getByRole("dialog", { name: "Match results" })).toBeVisible();
    await button(page, "Close dialog").tap();
  }
  const row = exposed();
  expect(row, "an actual committed opponent meld").toBeDefined();
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
  await page.screenshot({ path: info.outputPath("live-exposed-320.png") });
  await button(page, "Back to table").tap();
  await expect(switcher).toBeFocused();
  expect(await geometry()).toEqual(before);
});
