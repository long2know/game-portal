import { expect, test } from "@playwright/test";
import type { Locator, Page, WebSocketRoute } from "@playwright/test";
import type { ClientMessage, Command, Face, Meld, OptionKind, Seat, Snapshot, Tile } from "../src/protocol";

const origin = `http://127.0.0.1:${process.env.MAHJONG_GEOMETRY_PORT ?? 4179}`;
const players = ["River", "Willow", "Ink", "Jade"];
const seats = [0, 1, 2, 3] as Seat[];
const hand: Tile[] = (["b1", "b3", "b3", "b6", "b7", "c2", "c2", "c4", "c6", "c9", "d1", "d5", "d6"] as Face[])
  .map((face, i) => ({ tileId: `held-${i}`, face }));
const drawn: Tile = { tileId: "private-draw", face: "d9" };
const layer = ".table-effects";
const finale = ".hall-finale";

function frame(rev: number, number = 1, draw = true): Snapshot {
  const tiles = draw ? [...hand, drawn] : hand;
  return {
    v: 1, type: "snapshot", roomId: "motion-room", roomRevision: rev + 5, gameRevision: rev,
    serverTimeUtc: new Date().toISOString(),
    room: { visibility: "invite", status: "playing", hostSeat: 0, matchHands: 4, paused: false,
      autoResumeAtUtc: null, rulesVersion: "changsha-v1", chat: [], seats: seats.map(seat => ({
        seat, kind: seat === 0 ? "human" : "bot", connected: seat === 0, ready: true, displayName: players[seat],
      })) },
    game: {
      handNumber: number, dealerSeat: 0, phase: "turn", turnSeat: draw ? 0 : 3, wallCount: 55,
      seats: seats.map(seat => ({ seat, concealedCount: seat === 0 ? tiles.length : 13,
        discards: [], melds: [], revealedTiles: [], total: 0 })),
      window: { windowId: `turn-${rev}`, anchorGameRevision: rev, reason: "turn", trigger: null,
        deadlineUtc: new Date(Date.now() + 30000).toISOString() },
      settlement: null,
    },
    self: { seat: 0, inviteCode: null, hand: tiles, drawnTileId: draw ? drawn.tileId : null,
      legalOptions: draw ? tiles.map(tile => ({
        optionId: `drop-${tile.tileId}`, kind: "discard", tileIds: [tile.tileId], faces: [tile.face],
      })) : [] },
  };
}

function waiting(rev: number): Snapshot {
  const state = frame(rev, 1, false);
  state.game = null;
  state.room.status = "waiting";
  state.self.hand = [];
  return state;
}

function advance(prior: Snapshot): Snapshot {
  const next = structuredClone(prior);
  next.gameRevision++;
  return next;
}

function settled(prior: Snapshot, winners: Seat[] = [1, 2]): Snapshot {
  const state = advance(prior);
  state.roomRevision++;
  state.room.status = state.game!.handNumber === 4 ? "complete" : "betweenHands";
  state.self.legalOptions = [];
  const game = state.game!;
  game.phase = "settled";
  game.turnSeat = game.window = null;
  game.settlement = { reason: "win", sourceSeat: 0, nextDealerSeat: winners.length > 1 ? 0 : winners[0], birds: [],
    winners: winners.map(seat => ({ seat, winBy: "discard", winningTiles: ["b8"], patterns: [] })),
    seatResults: seats.map(seat => {
      const delta = winners.includes(seat) ? 8 : seat === 0 ? -8 * winners.length : 0;
      return { seat, delta, total: game.seats[seat].total + delta };
    }),
  };
  game.seats.forEach(seat => { seat.total = game.settlement!.seatResults[seat.seat].total; });
  return state;
}

function revealed(prior: Snapshot): Snapshot {
  const state = settled(prior);
  const [cha, closed] = state.game!.settlement!.winners;
  cha.winningHand = {
    concealedTiles: ["b8", "b8", "c4", "c4", "d1", "d1", "d1"],
    melds: [
      { kind: "gang", method: "concealed", fromSeat: null, tiles: ["c9", "c9", "c9", "c9"] },
      { kind: "chi", method: "open", fromSeat: 0, tiles: ["d3", "d4", "d5"] },
    ],
  };
  closed.winningHand = {
    concealedTiles: ["b1", "b1", "b1", "b6", "b7", "c1", "c2", "c3", "d2", "d2", "d7", "d8", "d9"],
    melds: [],
  };
  return state;
}

// Only the transport is stubbed. These frames run through the production session, router, renderer and controls.
async function open(page: Page, initial: Snapshot, savedMotion = false, freezeActors = false) {
  let state = initial;
  const links: WebSocketRoute[] = [];
  const commands: Command[] = [];
  const ack = (socket: WebSocketRoute, requestId: string, code?: string) => socket.send(JSON.stringify({
    v: 1, type: "ack", requestId, status: code ? "error" : "ok", error: code ? { code } : null, roomId: state.roomId,
    roomRevision: state.roomRevision, gameRevision: state.gameRevision,
  }));
  const send = (socket: WebSocketRoute) =>
    socket.send(JSON.stringify({ ...state, serverTimeUtc: new Date().toISOString() }));
  await page.addInitScript(selector => {
    const animate = Element.prototype.animate;
    Element.prototype.animate = function (frames, options) {
      const animation = animate.call(this, frames, options);
      if (this.closest(selector)) {
        animation.pause();
        animation.currentTime = 0;
      }
      return animation;
    };
  }, freezeActors ? `${layer}, .seat-cast` : layer);
  await page.route("**/api/guest", route => route.fulfill({ json: {
    displayName: players[initial.self.seat], resumeRoomId: initial.roomId,
    preferences: { sound: false, music: false, volume: .7, reducedMotion: savedMotion },
  } }));
  await page.route("**/api/rooms", route => route.fulfill({ json: { rooms: [] } }));
  await page.routeWebSocket("**/ws", socket => {
    links.push(socket);
    socket.onMessage(raw => {
      const message = JSON.parse(String(raw)) as ClientMessage;
      if (message.type === "sync") {
        ack(socket, message.requestId);
        send(socket);
      } else if (message.type === "command") commands.push(message);
    });
    socket.send(JSON.stringify({ v: 1, type: "lease", lease: "effect-test", serverTimeUtc: new Date().toISOString(),
      expiresAtUtc: new Date(Date.now() + 180000).toISOString() }));
  });
  await page.goto(origin);
  await expect(page.locator('.connection[data-status="online"]')).toBeVisible();
  return {
    commands,
    publish(snapshot: Snapshot, command?: Command) {
      state = snapshot;
      const socket = links.at(-1)!;
      if (command) ack(socket, command.requestId);
      send(socket);
    },
    reject(snapshot: Snapshot, command: Command, code: string) {
      state = snapshot;
      ack(links.at(-1)!, command.requestId, code);
    },
    reconnect(snapshot: Snapshot) {
      state = snapshot;
      links.at(-1)!.close({ code: 1012, reason: "fixture reconnect" });
    },
  };
}

async function seek(page: Page, milliseconds: number) {
  await page.locator(layer).evaluate((root, time) => {
    for (const animation of root.getAnimations({ subtree: true })) animation.currentTime = time;
  }, milliseconds);
}

async function midpoint(locator: Locator) {
  const box = (await locator.boundingBox())!;
  return { x: box.x + box.width / 2, y: box.y + box.height / 2 };
}

const actor = (page: Page, seat: Seat) => page.locator(`[data-actor-seat="${seat}"]`);
const gesture = (page: Page) => page.locator(".seat-actor[data-gesture]");
const joint = (node: Locator, name: string) => node.locator(`[data-joint="${name}"]`);
const eyes = (node: Locator, mood: string) => node.locator(`[data-eyes="${mood}"]`);
const pawPose = (node: Locator, pose: string) => node.locator(`[data-main-paw] [data-hand="${pose}"]`);
const distance = (a: { x: number; y: number }, b: { x: number; y: number }) => Math.hypot(a.x - b.x, a.y - b.y);

async function seekCast(page: Page, milliseconds: number) {
  await page.locator(".seat-cast").evaluate((root, time) => {
    for (const animation of root.getAnimations({ subtree: true })) animation.currentTime = time;
  }, milliseconds);
}

async function finishCast(page: Page) {
  await page.locator(".seat-cast").evaluate(root => {
    for (const animation of root.getAnimations({ subtree: true })) animation.finish();
  });
  await expect(gesture(page)).toHaveCount(0);
}

function discardFrom(prior: Snapshot, seat: Seat, face: Face): Snapshot {
  const state = advance(prior);
  const game = state.game!;
  game.seats[seat].discards.push(face);
  game.seats[seat].concealedCount--;
  game.phase = "claim";
  game.turnSeat = null;
  game.window = { ...game.window!, windowId: `discard-${state.gameRevision}`, anchorGameRevision: state.gameRevision,
    reason: "discard", trigger: { seat, faces: [face] } };
  if (seat === state.self.seat) {
    state.self.hand = state.self.hand.filter(tile => tile.tileId !== state.self.drawnTileId);
    state.self.drawnTileId = null;
  }
  state.self.legalOptions = [];
  return state;
}

function phoneClaimTurn(rev: number): Snapshot {
  const state = frame(rev, 1, false);
  state.game!.seats[3].concealedCount = 14;
  state.self.hand = (["b3", "b4", "b4", "b5", "b5", "b6", "b6", "b7", "c2", "c2", "d5", "d5", "d5"] as Face[])
    .map((face, i) => ({ tileId: `claim-held-${i}`, face }));
  return state;
}

function phoneClaim(prior: Snapshot): Snapshot {
  const state = discardFrom(prior, 3, "b5");
  state.game!.window!.deadlineUtc = new Date(Date.now() + 8000).toISOString();
  const options: { kind: OptionKind; faces: Face[]; tiles: number[] }[] = [
    { kind: "hu", faces: ["b5"], tiles: [] },
    { kind: "peng", faces: ["b5", "b5", "b5"], tiles: [3, 4] },
    { kind: "chi", faces: ["b3", "b4", "b5"], tiles: [0, 1] },
    { kind: "chi", faces: ["b4", "b5", "b6"], tiles: [1, 5] },
    { kind: "chi", faces: ["b5", "b6", "b7"], tiles: [5, 7] },
    { kind: "pass", faces: [], tiles: [] },
  ];
  state.self.legalOptions = options.map(({ kind, faces, tiles }, i) => ({
    kind, faces, optionId: `claim-${state.gameRevision}-${i}`, tileIds: tiles.map(at => state.self.hand[at].tileId),
  }));
  return state;
}

function phoneHuResponse(prior: Snapshot, reason: "kong" | "lastTile"): Snapshot {
  const state = discardFrom(prior, 3, "c3");
  const game = state.game!;
  game.window!.reason = reason;
  game.window!.deadlineUtc = new Date(Date.now() + 8000).toISOString();
  if (reason === "kong") {
    game.seats[3].discards.pop();
    game.seats[3].concealedCount = 11;
    game.seats[3].melds = [{ kind: "peng", fromSeat: 2, tiles: ["c3", "c3", "c3"] }];
  } else game.wallCount = 0;
  state.self.hand = (["b1", "b2", "b3", "b4", "b5", "b6", "c1", "c2", "d2", "d2", "d3", "d4", "d5"] as Face[])
    .map((face, i) => ({ tileId: `win-held-${i}`, face }));
  state.self.legalOptions = [
    { kind: "hu", faces: ["c3"], tileIds: [], optionId: `${reason}-win` },
    { kind: "pass", faces: [], tileIds: [], optionId: `${reason}-pass` },
  ];
  return state;
}

const kongFaces: Tile[] = [{ tileId: "private-rear", face: "c3" }, { tileId: "private-front", face: "c6" }];

function kongCandidate(revision: number, method: "sum" | "lower" | "tail", seat: Seat = 0): Snapshot {
  const state = frame(revision, 1, false);
  state.room.rulesVersion = "changsha-two-tile-kong-v1";
  const game = state.game!;
  game.turnSeat = seat;
  game.wallCount = { sum: 40, lower: 4, tail: 0 }[method];
  game.kongRoll = { seat, dice: [2, 5], lowerDie: 2, stackMethod: method, stackCount: { sum: 7, lower: 2, tail: 1 }[method] };
  game.seats[seat].melds = [{ kind: "gang", method: "open", fromSeat: 2, tiles: ["d3", "d3", "d3", "d3"] }];
  state.self.hand = (seat === 0
    ? ["b1", "b2", "b3", "b4", "b5", "b6", "c1", "c2", "d2", "d2"]
    : ["b1", "b2", "b3", "b4", "b5", "b6", "c4", "c5", "d2", "d2", "d7", "d8", "d9"]
  ).map((face, i) => ({ tileId: `kong-held-${i}`, face: face as Face }));
  game.seats[seat].concealedCount = 10;
  game.seats[0].concealedCount = state.self.hand.length;
  if (seat === 0) {
    const replacements = state.self.kongReplacements = method === "tail" ? kongFaces.slice(1) : kongFaces;
    state.self.legalOptions = [
      ...(method === "tail" ? [] : [{ kind: "hu" as const, optionId: "own-hu", tileIds: ["private-rear"], faces: ["c3" as const] }]),
      { kind: "discardKong", optionId: "own-discard", tileIds: replacements.map(tile => tile.tileId), faces: replacements.map(tile => tile.face) },
    ];
  }
  return state;
}

function kongBatch(prior: Snapshot, faces: Face[], eligible: boolean): Snapshot {
  const state = advance(prior);
  const game = state.game!;
  const seat = game.turnSeat!;
  game.seats[seat].discards.push(...faces);
  game.phase = "claim";
  game.turnSeat = game.kongRoll = null;
  game.window = { ...game.window!, windowId: `kong-${state.gameRevision}`, anchorGameRevision: state.gameRevision, reason: "kong",
    trigger: { seat, faces } };
  delete state.self.kongReplacements;
  state.self.legalOptions = eligible ? [
    ...faces.map((face, i) => ({ kind: "hu" as const, optionId: `cannon-${i + 1}`, tileIds: [], faces: [face] })),
    { kind: "pass", optionId: "pass-cannon", tileIds: [], faces: [] },
  ] : [];
  return state;
}

function kongChoice(snapshot: Snapshot, optionId: string) {
  const { windowId, anchorGameRevision } = snapshot.game!.window!;
  return { op: "choose", payload: { roomId: snapshot.roomId, windowId, anchorGameRevision, optionId } };
}

async function assertKongArt(tiles: Locator, faces: Face[], width: number, height: number) {
  await expect(tiles).toHaveCount(faces.length);
  for (const [i, face] of faces.entries()) {
    const tile = tiles.nth(i);
    const use = tile.locator("use");
    await expect(use).toHaveAttribute("href", new RegExp(`#lt-${face}$`));
    await expect.poll(() => use.evaluate(node => (node as SVGGraphicsElement).getBBox().width)).toBeGreaterThan(0);
    await expect(tile).toBeInViewport({ ratio: 1 });
    const box = (await tile.boundingBox())!;
    expect(box.width).toBeGreaterThanOrEqual(width);
    expect(box.height).toBeGreaterThanOrEqual(height);
  }
}

for (const [width, height, method, label, roll] of [
  [320, 730, "sum", "Hu · Win: 3 characters", "Dice sum selects stack 7"],
  [740, 320, "lower", "Discard both", "Lower die selects stack 2"],
  [1280, 720, "sum", "Hu · Win: 3 characters, replacement 2", "Dice sum selects stack 7"],
] as const) {
  test(`Kong replacement controls commit one current choice at ${width}×${height} (controlled snapshots)`, async ({ page }, info) => {
    await page.setViewportSize({ width, height });
    const offer = kongCandidate(31, method);
    const sameFace = width === 1280;
    if (sameFace) {
      offer.self.kongReplacements = [kongFaces[0], { ...kongFaces[1], face: "c3" }];
      offer.self.legalOptions.push({ kind: "hu", optionId: "own-hu-front", tileIds: ["private-front"], faces: ["c3"] });
      offer.self.legalOptions.find(option => option.kind === "discardKong")!.faces = ["c3", "c3"];
    }
    const before = structuredClone(offer);
    before.gameRevision--;
    before.game!.window!.windowId = "before-replacements";
    before.self.kongReplacements = undefined;
    before.self.legalOptions = [];
    before.game!.kongRoll = null;
    const wire = await open(page, before);
    if (width === 740) await page.addStyleTag({ content: ":root { --safe-t: 10px; --safe-r: 22px; --safe-b: 13px; --safe-l: 25px; }" });
    await expect(page.locator(".seat-cast .seat-actor")).toHaveCount(4);
    const rack = page.locator(".rack");
    const dock = await rack.boundingBox();
    const tiles = await rack.locator(".hand-tile").evaluateAll(nodes => nodes.map(node => (node as HTMLElement).dataset.tileId));
    wire.publish(offer);
    const replacements = page.getByRole("group", { name: "Kong replacements" });
    await expect(replacements).toBeVisible();
    await expect(page.locator(".hall-dial-roll")).toHaveText(method === "sum" ? "Sum #7" : "Low #2");
    await expect(page.locator(".board-center [role=status]")).toHaveText(`Kong roll: 2 and 5. ${roll} from the tail.`);
    await assertKongArt(replacements.locator(".tile-art"), ["c3", sameFace ? "c3" : "c6"], 30, 36);
    await expect(replacements.getByRole("img", { name: "6 characters" })).toHaveCount(sameFace ? 0 : 1);
    await expect(replacements.getByRole("button")).toHaveCount(sameFace ? 3 : 2);
    await expect(replacements.getByRole("button", {
      name: sameFace ? "Hu · Win: 3 characters, replacement 1" : "Hu · Win: 3 characters", exact: true,
    })).toBeEnabled();
    await expect(replacements.getByRole("button", { name: "Discard both", exact: true })).toBeEnabled();
    await expect(page.locator(".center-trigger .tile-target, .river .last-discard, .hand-tile.is-drawn, .discard-confirm:visible")).toHaveCount(0);
    expect(await rack.locator(".hand-tile").evaluateAll(nodes => nodes.map(node => (node as HTMLElement).dataset.tileId))).toEqual(tiles);
    expect(await rack.boundingBox()).toEqual(dock);
    for (const control of [...await replacements.getByRole("button").all(),
      page.getByRole("button", { name: "Pause", exact: true }), page.getByRole("button", { name: "Table options" })]) {
      await expect(control).toBeInViewport({ ratio: 1 });
      const box = (await control.boundingBox())!;
      expect(Math.min(box.width, box.height)).toBeGreaterThanOrEqual(44);
      expect(await control.evaluate(node => {
        const { x, y, width, height } = node.getBoundingClientRect();
        return node.contains(document.elementFromPoint(x + width / 2, y + height / 2));
      })).toBe(true);
    }
    expect(await page.evaluate(() => [document.documentElement.scrollWidth, scrollY])).toEqual([width, 0]);
    await page.screenshot({ path: info.outputPath(`kong-replacements-${width}x${height}.png`), animations: "disabled" });

    const settings = page.getByRole("button", { name: "Settings" });
    await settings.tap();
    await expect(page.getByRole("dialog", { name: "Settings" })).toBeVisible();
    await page.keyboard.press("Escape");
    await expect(settings).toBeFocused();
    expect(wire.commands).toHaveLength(0);
    const choice = replacements.getByRole("button", { name: label, exact: true });
    if (method === "sum") await choice.press("Enter");
    else await choice.tap();
    await expect(choice).toBeDisabled();
    await choice.dispatchEvent("click");
    await expect(replacements.locator(".tile-art")).toHaveCount(2);
    await expect(page.locator(".last-discard")).toHaveCount(0);
    expect(await rack.boundingBox()).toEqual(dock);
    await expect.poll(() => wire.commands.map(({ op, payload }) => ({ op, payload })))
      .toEqual([kongChoice(offer, sameFace ? "own-hu-front" : method === "sum" ? "own-hu" : "own-discard")]);
    if (method === "lower") {
      wire.publish(kongBatch(offer, ["c3", "c6"], false), wire.commands[0]);
      await expect(replacements).toBeHidden();
      await expect(page.locator(".hall-dial-roll")).toBeHidden();
      await assertKongArt(page.getByRole("group", { name: "Kong discards" }).locator(".tile-art"), ["c3", "c6"], 24, 28);
      await expect(page.locator(".river--near .last-discard")).toHaveCount(2);
      expect(wire.commands).toHaveLength(1);
    }
  });
}

test("short-wall Kong shows one real replacement and never submits after expiry or on reconnect (controlled snapshots)", async ({ page }) => {
  const offer = kongCandidate(25, "tail");
  const wire = await open(page, offer, true);
  await page.clock.install();
  const replacements = page.getByRole("group", { name: "Kong replacements" });
  const discard = replacements.getByRole("button", { name: "Discard replacement", exact: true });
  await expect(discard).toBeEnabled();
  await expect(replacements.getByRole("button")).toHaveCount(1);
  await assertKongArt(replacements.locator(".tile-art"), ["c6"], 30, 36);
  await expect(page.locator(".hall-dial-roll")).toHaveText("Rear #1");
  await expect(page.locator(".board-center [role=status]")).toHaveText("Kong roll: 2 and 5. Rear fallback selects stack 1 from the tail.");
  await discard.focus();
  await page.keyboard.press("Escape");
  await page.clock.fastForward(31_000);
  await expect(page.getByRole("timer")).toHaveText("0s");
  await expect(discard).toBeDisabled();
  await discard.dispatchEvent("click");
  expect(wire.commands).toHaveLength(0);

  const committed = kongBatch(offer, ["c6"], false);
  wire.reconnect(committed);
  await expect(replacements).toBeHidden();
  await assertKongArt(page.getByRole("group", { name: "Kong discards" }).locator(".tile-art"), ["c6"], 24, 28);
  await expect(page.locator(".hall-dial-roll")).toBeHidden();
  await expect(page.locator(".effect-flight")).toHaveCount(0);
  expect(wire.commands).toHaveLength(0);
});

for (const [width, height, faces, label] of [
  [320, 730, ["c3", "c6"], "Hu · Win: 6 characters"],
  [1280, 720, ["c6", "c6"], "Hu · Win · Tile 2: 6 characters"],
] as const) {
  test(`Kong Cannon uses one offered tile at ${width}×${height} (controlled snapshots)`, async ({ page }) => {
    await page.setViewportSize({ width, height });
    const turn = kongCandidate(40, "sum", 3);
    const wire = await open(page, turn, true);
    await expect(page.locator(".hall-dial-roll")).toHaveText("Sum #7");
    await expect(page.getByRole("group", { name: "Kong replacements" })).toHaveCount(0);
    await expect(page.locator(".center-trigger .tile-target, .kong-discards .tile-target")).toHaveCount(0);
    await expect(page.locator(".choice-list button")).toHaveCount(0);
    const rack = page.locator(".rack");
    const dock = await rack.boundingBox();
    const offer = kongBatch(turn, [...faces], true);
    wire.publish(offer);
    const choices = page.getByRole("group", { name: "Available table actions" });
    const discards = page.getByRole("group", { name: "Kong discards" });
    await expect(choices).toHaveClass(width === 320 ? /phone-claim/ : "choice-list kong-claim");
    await expect(page.locator(".hall-dial-roll")).toBeHidden();
    await assertKongArt(discards.locator(".tile-art"), [...faces], 24, 28);
    const river = page.locator(".river--left .last-discard");
    await expect(river).toHaveCount(2);
    for (const [i, face] of faces.entries()) await expect(river.nth(i).locator("use")).toHaveAttribute("href", new RegExp(`#lt-${face}$`));
    const controls = choices.getByRole("button");
    await expect(controls).toHaveCount(3);
    await assertKongArt(choices.locator("button .tile-art"), [...faces], 24, 28);
    await expect(choices.getByRole("button", { name: "Pass", exact: true })).toBeEnabled();
    expect(await rack.boundingBox()).toEqual(dock);
    const selected = choices.getByRole("button", { name: label, exact: true });
    const dimensions = (await selected.boundingBox())!;
    expect(Math.min(dimensions.height, dimensions.width)).toBeGreaterThanOrEqual(44);
    if (width === 320) await selected.tap();
    else await selected.press("Enter");
    await expect.poll(() => wire.commands.map(({ op, payload }) => ({ op, payload })))
      .toEqual([kongChoice(offer, "cannon-2")]);

    const result = settled(offer, [0]);
    result.game!.wallCount = 0;
    result.game!.settlement!.sourceSeat = 3;
    result.game!.settlement!.winners[0].winningTiles = ["c6"];
    result.game!.settlement!.winners[0].patterns = [{ code: "kongCannon", label: "Kong Cannon", points: 6 }];
    result.game!.settlement!.birds = ["d5"];
    wire.publish(result, wire.commands[0]);
    const results = page.getByRole("dialog", { name: "Hand results" });
    await expect(results.getByText("Two-tile Kong · Two birds", { exact: true })).toBeVisible();
    const winning = results.getByRole("group", { name: "Winning tile" }).getByRole("img");
    await expect(winning).toHaveCount(1);
    await expect(winning).toHaveAccessibleName("6 characters");
    await expect(results.getByText("Only one wall tile remained after the win.")).toBeVisible();
  });
}

const claimLabels = [
  "Hu · Win: 5 bamboo", "Peng: 5 bamboo, 5 bamboo, 5 bamboo",
  "Cha: 3 bamboo, 4 bamboo, 5 bamboo", "Cha: 4 bamboo, 5 bamboo, 6 bamboo", "Cha: 5 bamboo, 6 bamboo, 7 bamboo", "Pass",
];

for (const [width, height, picked] of [[320, 700, 3], [740, 320, 4], [667, 320, 1], [430, 844, 2]] as const) {
  test(`compact phone claim options are legible and submit the exact tap at ${width}×${height}`, async ({ page }, info) => {
    await page.setViewportSize({ width, height });
    const turn = phoneClaimTurn(30);
    const wire = await open(page, turn);
    const rack = page.locator(".rack");
    const board = page.locator(".board-stage");
    const choices = page.locator(".choice-list");
    const settings = page.getByRole("button", { name: "Settings" });
    await expect(rack).toBeVisible();
    await expect(choices).toBeHidden();
    if (width === 740) {
      await page.evaluate(() => {
        ["t", "r", "b", "l"].forEach((edge, i) => document.documentElement.style
          .setProperty(`--safe-${edge}`, `${[10, 22, 13, 25][i]}px`));
      });
    }
    const before = await rack.boundingBox();
    await settings.focus();
    const offer = phoneClaim(turn);
    wire.publish(offer);
    await expect(choices.getByRole("button")).toHaveCount(6);
    await expect(page.locator(".seat-cast .seat-actor")).toHaveCount(4);
    if (width === 320 || width === 740) {
      await page.screenshot({ path: info.outputPath(`phone-claim-${width}.png`), fullPage: true });
    }
    await expect(settings).toBeFocused();
    await expect(page.locator(".table-actions .choice-list")).toHaveCount(0);
    const controls = choices.getByRole("button");
    for (const [i, label] of claimLabels.entries()) {
      await expect(controls.nth(i)).toHaveAccessibleName(label);
      const { width, height } = (await controls.nth(i).boundingBox())!;
      expect(Math.min(width, height), label).toBeGreaterThanOrEqual(44);
      const previews = controls.nth(i).locator(".tile-art");
      const faces = offer.self.legalOptions[i].faces;
      await expect(previews).toHaveCount(faces.length);
      for (const [j, face] of faces.entries()) {
        const artUse = previews.nth(j).locator("use");
        await expect(artUse).toHaveAttribute("href", new RegExp(`#lt-${face}$`));
        await expect.poll(() => artUse.evaluate(use => (use as SVGGraphicsElement).getBBox().width)).toBeGreaterThan(0);
        const art = (await previews.nth(j).boundingBox())!;
        expect(art.width).toBeGreaterThanOrEqual(23);
        expect(art.height).toBeGreaterThanOrEqual(29);
      }
    }
    const box = (await choices.boundingBox())!;
    const stage = (await board.boundingBox())!;
    expect(await rack.boundingBox()).toEqual(before);
    expect(box.x + box.width / 2).toBeCloseTo(stage.x + stage.width / 2, 0);
    expect(box.y + box.height / 2).toBeCloseTo(stage.y + stage.height / 2, 0);
    expect(box.y + box.height).toBeLessThan(before!.y);
    expect(await page.evaluate(() => [document.documentElement.scrollWidth, scrollY])).toEqual([width, 0]);
    await expect(choices.locator(".claim-time")).toHaveText(/^[1-8]s$/);
    await expect(choices.getByRole("button", { name: "Pass" })).toBeInViewport({ ratio: 1 });
    const tile = rack.locator(".hand-tile").first();
    const tilePoint = await midpoint(tile);
    expect(await tile.evaluate((node, { x, y }) => node.contains(document.elementFromPoint(x, y)), tilePoint)).toBe(true);

    if (width === 320) {
      const onBoard = { x: box.x + box.width / 2, y: box.y + box.height / 2 };
      await settings.tap();
      const dialog = page.getByRole("dialog", { name: "Settings" });
      await expect(dialog).toBeVisible();
      expect(await page.evaluate(({ x, y }) => !!document.elementFromPoint(x, y)?.closest("dialog[open]"), onBoard)).toBe(true);
      await page.keyboard.press("Escape");
      await expect(dialog).toBeHidden();
      await expect(choices).toBeVisible();
      expect(wire.commands).toHaveLength(0);
    }

    const pass = controls.last();
    const passBeforeScroll = await pass.boundingBox();
    const option = controls.nth(picked);
    await option.scrollIntoViewIfNeeded();
    expect(await pass.boundingBox()).toEqual(passBeforeScroll);
    expect(await page.evaluate(() => scrollY)).toBe(0);
    expect(await option.evaluate(node => {
      const { x, y, width, height } = node.getBoundingClientRect();
      return node.contains(document.elementFromPoint(x + width / 2, y + height / 2));
    })).toBe(true);
    const point = await midpoint(option);
    const original = choices.locator("button").nth(picked);
    await page.touchscreen.tap(point.x, point.y);
    await expect(choices).toBeHidden();
    await expect.poll(() => wire.commands.map(command => command.payload)).toEqual([{
      roomId: offer.roomId, windowId: offer.game!.window!.windowId,
      anchorGameRevision: offer.game!.window!.anchorGameRevision, optionId: offer.self.legalOptions[picked].optionId,
    }]);
    await original.dispatchEvent("click");
    const committed = advance(offer);
    committed.self.legalOptions = [];
    wire.publish(committed, wire.commands[0]);
    await expect(choices.locator("button")).toHaveCount(0);
    expect(wire.commands).toHaveLength(1);
    expect(await rack.boundingBox()).toEqual(before);
  });
}

test("short landscape phone claim choices scroll without moving Pass or the rack", async ({ page }) => {
  await page.setViewportSize({ width: 667, height: 320 });
  const turn = phoneClaimTurn(30);
  turn.self.hand = turn.self.hand.filter(tile => ["b3", "b4", "b6", "b7"].includes(tile.face))
    .filter((tile, i, tiles) => tiles.findIndex(other => other.face === tile.face) === i);
  turn.game!.seats[0].concealedCount = turn.self.hand.length;
  turn.game!.seats[0].melds = [
    { kind: "chi", fromSeat: 3, tiles: ["c1", "c2", "c3"] },
    { kind: "peng", fromSeat: 1, tiles: ["d1", "d1", "d1"] },
    { kind: "peng", fromSeat: 2, tiles: ["d5", "d5", "d5"] },
  ];
  const wire = await open(page, turn);
  const state = discardFrom(turn, 3, "b5");
  const patterns: Face[][] = [["b3", "b4", "b5"], ["b4", "b5", "b6"], ["b5", "b6", "b7"]];
  state.self.legalOptions = patterns.map((faces, i) => ({
    kind: "chi", optionId: `scroll-chi-${i}`, faces,
    tileIds: turn.self.hand.filter(tile => faces.includes(tile.face)).map(tile => tile.tileId),
  }));
  state.self.legalOptions.push({ kind: "pass", faces: [], tileIds: [], optionId: "scroll-pass" });
  const rack = page.locator(".rack");
  await expect(rack).toBeVisible();
  const before = await rack.boundingBox();
  wire.publish(state);
  const choices = page.locator(".choice-list");
  const scrolling = choices.locator(".choice-options");
  const pass = choices.getByRole("button", { name: "Pass" });
  await expect(pass).toBeInViewport({ ratio: 1 });
  const pinned = await pass.boundingBox();
  expect(await scrolling.evaluate(node => node.scrollHeight > node.clientHeight)).toBe(true);
  const last = choices.getByRole("button", { name: claimLabels[4] });
  await last.scrollIntoViewIfNeeded();
  expect(await scrolling.evaluate(node => node.scrollTop)).toBeGreaterThan(0);
  expect(await pass.boundingBox()).toEqual(pinned);
  expect(await rack.boundingBox()).toEqual(before);
  expect(await page.evaluate(() => scrollY)).toBe(0);
  const point = await midpoint(pass);
  await page.touchscreen.tap(point.x, point.y);
  await expect.poll(() => wire.commands.map(command => command.payload)).toEqual([{
    roomId: state.roomId, windowId: state.game!.window!.windowId,
    anchorGameRevision: state.game!.window!.anchorGameRevision, optionId: "scroll-pass",
  }]);
});

test("compact phone claim options track the current deadline, pause, re-sync and disconnect", async ({ page }) => {
  const turn = phoneClaimTurn(30);
  const wire = await open(page, turn);
  await page.clock.install();
  const first = phoneClaim(turn);
  wire.publish(first);
  const pane = page.locator(".choice-list");
  const pass = pane.getByRole("button", { name: "Pass" });
  await expect(pass).toBeVisible();
  await pass.focus();
  await page.keyboard.press("Escape");
  await expect(pass).toBeFocused();
  const peerResponse = advance(first);
  wire.publish(peerResponse);
  await expect(pass).toBeFocused();
  const oldPass = (await pass.elementHandle())!;

  const paused = structuredClone(peerResponse);
  paused.roomRevision++;
  paused.room.paused = true;
  paused.room.autoResumeAtUtc = new Date(Date.now() + 60000).toISOString();
  paused.game!.window!.deadlineUtc = null;
  wire.publish(paused);
  await expect(pane).toBeHidden();
  await oldPass.dispatchEvent("click");
  expect(wire.commands).toHaveLength(0);
  const resumed = structuredClone(peerResponse);
  resumed.roomRevision = paused.roomRevision + 1;
  resumed.game!.window!.deadlineUtc = new Date(Date.now() + 8000).toISOString();
  wire.publish(resumed);
  await expect(pass).toBeVisible();
  await page.clock.fastForward(9000);
  await expect(page.getByRole("timer")).toHaveText("0s");
  await expect(pane).toBeHidden();
  await oldPass.dispatchEvent("click");

  const kong = phoneHuResponse(resumed, "kong");
  wire.publish(kong);
  const hu = pane.getByRole("button", { name: "Hu · Win: 3 characters" });
  await expect(hu).toBeVisible();
  await expect(pane).toContainText("Kong response");
  await oldPass.dispatchEvent("click");
  await hu.press("Enter");
  await expect(pane).toBeHidden();
  await expect.poll(() => wire.commands.map(command => command.payload)).toEqual([{
    roomId: kong.roomId, windowId: kong.game!.window!.windowId,
    anchorGameRevision: kong.game!.window!.anchorGameRevision, optionId: "kong-win",
  }]);
  const drawOffer = phoneClaimTurn(kong.gameRevision + 1);
  drawOffer.roomRevision = kong.roomRevision;
  drawOffer.game!.turnSeat = 0;
  drawOffer.game!.wallCount = 1;
  drawOffer.game!.window!.reason = "lastTile";
  drawOffer.self.legalOptions = [
    { kind: "draw", faces: [], tileIds: [], optionId: "last-tile-draw" },
    { kind: "pass", faces: [], tileIds: [], optionId: "last-tile-skip" },
  ];
  wire.reject(drawOffer, wire.commands[0], "stale_window");
  await expect(page.getByText("That decision window has closed.", { exact: false })).toBeVisible();
  await expect(page.locator(".table-actions").getByRole("button", { name: "Draw" })).toBeEnabled();

  const last = phoneHuResponse(drawOffer, "lastTile");
  wire.publish(last);
  await expect(pane).toContainText("Last tile");
  await expect(hu).toBeVisible();
  const later = phoneClaimTurn(last.gameRevision + 1);
  wire.reconnect(later);
  await expect(pane).toBeHidden();
  await expect(page.locator('.connection[data-status="online"]')).toBeVisible();
  await page.reload();
  await expect(page.locator(".rack")).toBeVisible();
  await expect(pane).toBeHidden();
  expect(wire.commands).toHaveLength(1);
});

test("claim choices stay docked on desktop, narrow mouse windows and full-screen or split-view tablets", async ({ browser }) => {
  const layouts = [
    { viewport: { width: 1440, height: 900 }, screen: { width: 1440, height: 900 }, touch: false, preview: 19 },
    { viewport: { width: 400, height: 780 }, screen: { width: 430, height: 844 }, touch: false, preview: 14 },
    { viewport: { width: 1024, height: 800 }, screen: { width: 1024, height: 1366 }, touch: true, preview: 19 },
    { viewport: { width: 410, height: 800 }, screen: { width: 1024, height: 1366 }, touch: true, preview: 14 },
  ];
  for (const { viewport, screen, touch, preview } of layouts) {
    const context = await browser.newContext({ viewport, screen, deviceScaleFactor: 1, isMobile: touch, hasTouch: touch });
    try {
      const page = await context.newPage();
      const turn = phoneClaimTurn(30);
      await open(page, phoneClaim(turn));
      expect(await page.evaluate(() => ({
        screen: { width: window.screen.width, height: window.screen.height },
        touch: matchMedia("(pointer: coarse) and (hover: none)").matches,
      }))).toEqual({ screen, touch });
      const choices = page.locator(".table-actions .choice-list");
      const first = choices.getByRole("button", { name: claimLabels[0] });
      await expect(first).toBeEnabled();
      expect((await first.locator(".tile-target").boundingBox())!.width).toBe(preview);
      await first.focus();
      await page.keyboard.press("Tab");
      await expect(choices.getByRole("button", { name: claimLabels[1] })).toBeFocused();
      await expect(choices.locator(".claim-context")).toBeHidden();
    } finally { await context.close(); }
  }
});

test("short landscape actors wind up, articulate toward their rivers and settle without moving a tap target", async ({ page }, testInfo) => {
  await page.setViewportSize({ width: 740, height: 320 });
  const initial = frame(17);
  const wire = await open(page, initial, false, true);
  await page.evaluate(() => {
    for (const [edge, inset] of [["t", 10], ["r", 22], ["b", 13], ["l", 25]]) {
      document.documentElement.style.setProperty(`--safe-${edge}`, `${inset}px`);
    }
  });
  const cast = page.locator(".seat-cast .seat-actor");
  await expect(cast).toHaveCount(4);
  await expect(page.locator(".seat-cast")).toHaveCSS("pointer-events", "none");
  const stage = (await page.locator(".board-stage").boundingBox())!;
  expect(stage.x).toBeGreaterThanOrEqual(25);
  expect(stage.x + stage.width).toBeLessThanOrEqual(740 - 22);
  for (const seat of seats) {
    const head = (await joint(actor(page, seat), "head").boundingBox())!;
    expect(head.width).toBeGreaterThan(35);
    expect(head.y).toBeGreaterThanOrEqual(stage.y);
    expect(head.y + head.height).toBeLessThanOrEqual(stage.y + stage.height);
  }
  expect(new Set(await cast.evaluateAll(nodes => nodes.map(node => node.getAttribute("data-character")))).size).toBe(4);
  await expect(page.locator(".seat-name")).toHaveText(["You", "Willow", "Ink", "Jade"]);
  await expect(gesture(page)).toHaveCount(0);

  const drawnTile = page.getByRole("button", { name: "9 dots, drawn tile" });
  await drawnTile.tap();
  const confirm = page.getByRole("button", { name: "Discard 9 dots" });
  await expect(confirm).toBeEnabled();
  const near = actor(page, 0), other = actor(page, 1);
  const otherArm = await joint(other, "shoulder").evaluate(node => getComputedStyle(node).transform);
  await page.screenshot({ path: testInfo.outputPath("01-short-seated-before.png") });
  await confirm.tap();
  await expect.poll(() => wire.commands[0]?.op).toBe("choose");
  const tap = await midpoint(confirm);
  await page.touchscreen.tap(tap.x, tap.y);
  expect(wire.commands).toHaveLength(1);
  const first = discardFrom(initial, 0, "d9");
  wire.publish(first, wire.commands[0]);
  await expect(near).toHaveAttribute("data-gesture", "discard");
  await expect(gesture(page)).toHaveCount(1);
  await expect(drawnTile).toHaveCount(0);
  const landing = page.locator(".river--near .last-discard");
  await expect(landing.getByRole("img", { name: "9 dots" })).toBeVisible();
  const target = await midpoint(landing);
  const arm = joint(near, "shoulder"), elbow = joint(near, "elbow"), wrist = joint(near, "wrist");
  const matrix = (node: Locator) => node.evaluate(part => getComputedStyle(part).transform);
  const rest = await midpoint(wrist);
  await seekCast(page, 105);
  const windup = await midpoint(wrist);
  expect(windup.x).toBeLessThan(rest.x - 4);
  await expect(pawPose(near, "open")).toHaveCSS("opacity", "0");
  const prep = await Promise.all([matrix(arm), matrix(elbow), matrix(wrist)]);
  await seekCast(page, 228);
  await seek(page, 228);
  const peak = await midpoint(wrist);
  expect(distance(windup, peak)).toBeGreaterThan(27);
  expect(distance(peak, target)).toBeLessThan(distance(rest, target) - 16);
  const extended = await Promise.all([matrix(arm), matrix(elbow), matrix(wrist)]);
  extended.forEach((value, i) => expect(value).not.toBe(prep[i]));
  await expect(pawPose(near, "open")).toHaveCSS("opacity", "1");
  const hand = (await pawPose(near, "open").boundingBox())!;
  expect(hand.x + hand.width).toBeLessThan(stage.x + stage.width);
  expect(hand.y).toBeGreaterThan(stage.y);
  expect(hand.y + hand.height).toBeLessThan(stage.y + stage.height);
  expect(await matrix(joint(other, "shoulder"))).toBe(otherArm);
  const held = page.getByRole("button", { name: "1 bamboo" });
  const heldBox = await held.boundingBox();
  await page.screenshot({ path: testInfo.outputPath("02-short-seated-reach-228ms.png") });
  await page.evaluate(() => window.visualViewport?.dispatchEvent(new Event("resize")));
  await expect(near).toHaveAttribute("data-gesture", "discard");
  await seekCast(page, 295);
  expect(distance(await midpoint(wrist), peak)).toBeGreaterThan(3);
  expect(await held.boundingBox()).toEqual(heldBox);
  await finishCast(page);
  await seek(page, 510);
  await page.screenshot({ path: testInfo.outputPath("03-short-seated-settled.png") });

  const next = discardFrom(first, 3, "c4");
  wire.publish(next);
  const left = actor(page, 3);
  await expect(left).toHaveAttribute("data-gesture", "discard");
  await expect(gesture(page)).toHaveCount(1);
  const leftWrist = joint(left, "wrist");
  const botRest = await midpoint(leftWrist);
  const leftTarget = await midpoint(page.locator(".river--left .last-discard"));
  await seekCast(page, 228);
  const botPeak = await midpoint(leftWrist);
  expect(botPeak.x).toBeGreaterThan(botRest.x + 12);
  expect(distance(botPeak, leftTarget)).toBeLessThan(distance(botRest, leftTarget) - 9);
});

test("the four reach silhouettes stay on stage beside dense compact rivers", async ({ page }, testInfo) => {
  await page.setViewportSize({ width: 740, height: 320 });
  let state = frame(24);
  state.self.hand = [...hand.slice(0, 4), drawn];
  state.self.legalOptions = state.self.legalOptions.filter(option => state.self.hand.some(tile => tile.tileId === option.tileIds[0]));
  state.game!.seats[0].concealedCount = 5;
  state.game!.seats[0].melds = [
    { kind: "chi", fromSeat: 3, tiles: ["b1", "b2", "b3"] },
    { kind: "peng", fromSeat: 1, tiles: ["c2", "c2", "c2"] },
    { kind: "peng", fromSeat: 2, tiles: ["d3", "d3", "d3"] },
  ];
  for (const seat of state.game!.seats) {
    seat.discards = ["c3", "d2", "b9", "b6", "d5", "c8", "d7", "c5", "b8", "c7", "d4"];
  }
  const wire = await open(page, state, false, true);
  await page.evaluate(() => {
    for (const [edge, inset] of [["t", 10], ["r", 22], ["b", 13], ["l", 25]]) {
      document.documentElement.style.setProperty(`--safe-${edge}`, `${inset}px`);
    }
  });
  await expect(page.locator(".local-melds > *")).toHaveCount(3);
  await expect(page.locator(".seat-actor")).toHaveCount(4);
  await page.mouse.click(10, 302);
  const board = (await page.locator(".board-stage").boundingBox())!;
  expect(board.height).toBeLessThan(140);
  for (const seat of seats) {
    state = discardFrom(state, seat, seat === 0 ? drawn.face : "d8");
    wire.publish(state);
    const active = actor(page, seat);
    await expect(active).toHaveAttribute("data-gesture", "discard");
    const wrist = joint(active, "wrist");
    await seekCast(page, 105);
    const wound = await midpoint(wrist);
    await seekCast(page, 228);
    const palm = (await pawPose(active, "open").boundingBox())!;
    expect(distance(wound, await midpoint(wrist)), `seat ${seat} opens away from its folded arm`).toBeGreaterThan(16);
    expect(palm.x).toBeGreaterThanOrEqual(board.x);
    expect(palm.y).toBeGreaterThanOrEqual(board.y);
    expect(palm.x + palm.width).toBeLessThanOrEqual(board.x + board.width);
    expect(palm.y + palm.height).toBeLessThanOrEqual(board.y + board.height);
    if (seat === 3) {
      await seek(page, 228);
      await page.screenshot({ path: testInfo.outputPath("07-dense-seated-reach-228ms.png") });
    }
    await finishCast(page);
  }
});

test("desktop call points and brightens; winning and losing react with different arms and faces", async ({ page }, testInfo) => {
  await page.setViewportSize({ width: 1280, height: 800 });
  const initial = frame(26);
  const wire = await open(page, initial, false, true);
  const near = actor(page, 0), far = actor(page, 2), right = actor(page, 1);
  await expect(eyes(far, "open")).toBeVisible();
  const still = await midpoint(joint(far, "wrist"));
  const called = advance(initial);
  called.game!.seats[2].melds.push({ kind: "peng", fromSeat: 3, tiles: ["d3", "d3", "d3"] });
  wire.publish(called);
  await expect(far).toHaveAttribute("data-gesture", "call");
  await expect(gesture(page)).toHaveCount(1);
  await expect(pawPose(far, "point")).toBeVisible();
  await expect(far.locator('[data-mouth="call"]')).toBeVisible();
  await expect(page.locator('[data-seat="2"] .seat-feedback')).toHaveText("Peng");
  await expect(page.locator('[data-seat="2"] .meld-group')).toBeVisible();
  await page.getByRole("button", { name: "9 dots, drawn tile" }).tap();
  const target = page.getByRole("button", { name: "Discard 9 dots" });
  const before = await target.boundingBox();
  await seekCast(page, 261);
  await seek(page, 260);
  expect((await midpoint(joint(far, "wrist"))).y).toBeLessThan(still.y - 24);
  expect(await target.boundingBox()).toEqual(before);
  await page.screenshot({ path: testInfo.outputPath("04-desktop-seat-call-261ms.png") });
  await target.tap();
  await expect.poll(() => wire.commands.length).toBe(1);

  await finishCast(page);
  await expect(far.locator('[data-mouth="rest"]')).toBeVisible();
  wire.publish(settled(called, [1, 2]), wire.commands[0]);
  await expect(right).toHaveAttribute("data-gesture", "win");
  await expect(far).toHaveAttribute("data-gesture", "win");
  await expect(near).toHaveAttribute("data-gesture", "loss");
  await expect(gesture(page)).toHaveCount(3);
  await seekCast(page, 217);
  await seek(page, 217);
  await expect(pawPose(far, "fist")).toBeVisible();
  await expect(pawPose(near, "open")).toBeVisible();
  await expect(eyes(right, "win")).toBeVisible();
  await expect(eyes(near, "loss")).toBeVisible();
  expect((await midpoint(joint(far, "rear-wrist"))).y).toBeLessThan((await midpoint(joint(far, "head"))).y + 3);
  expect((await midpoint(joint(near, "wrist"))).y).toBeGreaterThan((await midpoint(joint(near, "head"))).y + 25);
  const results = page.getByRole("dialog", { name: "Hand results" });
  await expect(results).toBeVisible();
  await results.getByRole("button", { name: "Close dialog" }).tap();
  await page.screenshot({ path: testInfo.outputPath("05-desktop-celebration-and-loss.png") });
  await finishCast(page);
  await expect(eyes(far, "win")).toBeVisible();
  await expect(eyes(near, "loss")).toBeVisible();
  await expect(pawPose(far, "rest")).toBeVisible();
});

test("a cold table chunk catches the first public motion, not a resync", async ({ page }, testInfo) => {
  let release!: () => void, fetched!: () => void;
  const wait = new Promise<void>(resolve => { release = resolve; });
  const ready = new Promise<void>(resolve => { fetched = resolve; });
  await page.route("**/src/actors.css", async route => {
    const response = await route.fetch();
    fetched();
    await wait;
    await route.fulfill({ response });
  });
  const initial = frame(11, 1, false);
  const wire = await open(page, initial, false, true);
  await ready;
  await expect(page.locator(".seat-actor")).toHaveCount(0);
  const discarded = discardFrom(initial, 3, "c4");
  const committed = page.waitForFunction(() => !!document.querySelector(".river--left .last-discard"));
  wire.publish(discarded);
  await committed;
  release();
  await expect(actor(page, 3)).toHaveAttribute("data-gesture", "discard");
  await seekCast(page, 228);
  await seek(page, 228);
  await page.screenshot({ path: testInfo.outputPath("06-portrait-seat-reach-228ms.png") });
  const recovered = settled({ ...discarded, gameRevision: discarded.gameRevision + 2 }, [2]);
  wire.reconnect(recovered);
  await expect(page.getByRole("dialog", { name: "Hand results" })).toBeVisible();
  await expect(gesture(page)).toHaveCount(0);
  await expect(actor(page, 2)).toHaveAttribute("data-mood", "win");
  await expect(eyes(actor(page, 0), "loss")).toBeVisible();
});

test("backed deal packets, turquoise draw and public discard travel without stealing a tap", async ({ page }, testInfo) => {
  const wire = await open(page, waiting(0));
  await expect(page.getByRole("heading", { name: "Take a seat." })).toBeVisible();
  await page.getByRole("button", { name: "Start match" }).tap();
  await expect.poll(() => wire.commands[0]?.op).toBe("start");
  const started = frame(1);
  wire.publish(started, wire.commands[0]);
  const packets = page.locator(".effect-flight--deal.effect-flight");
  await expect(packets).toHaveCount(4);
  await expect(packets.locator(".tile-back")).toHaveCount(4);
  await expect(packets.locator(".tile-art")).toHaveCount(0);
  const rackTile = page.locator("[data-tile-id]").first();
  await expect(rackTile).toHaveAttribute("aria-disabled", "false");
  await expect(page.locator(layer)).toHaveCSS("pointer-events", "none");
  const start = await midpoint(packets.first());
  await seek(page, 246);
  const mid = await midpoint(packets.first());
  expect(mid.y - start.y).toBeGreaterThan(55);
  await page.screenshot({ path: testInfo.outputPath("01-portrait-deal-246ms.png") });

  const baseline = frame(3, 1, false);
  baseline.game!.wallCount = 49;
  baseline.game!.seats[3].discards = ["b2"];
  wire.publish(baseline);
  await expect(page.locator(".wall-count")).toHaveText("49");
  await expect(page.locator(`${layer} > *`)).toHaveCount(0);
  const draw = frame(4);
  draw.game!.wallCount = 48;
  draw.game!.seats[3].discards = ["b2"];
  wire.publish(draw);
  const flight = page.locator(".effect-flight--draw.effect-flight");
  await expect(flight).toHaveCount(1);
  const tile = page.getByRole("button", { name: "9 dots, drawn tile" });
  const before = (await tile.boundingBox())!;
  expect(await flight.locator(".tile-art").evaluate(svg => svg.outerHTML))
    .toBe(await tile.locator(".tile-art").evaluate(svg => svg.outerHTML));
  const takeoff = await midpoint(flight);
  await seek(page, 182);
  const moving = await midpoint(flight);
  expect(Math.hypot(moving.x - takeoff.x, moving.y - takeoff.y)).toBeGreaterThan(70);
  await page.screenshot({ path: testInfo.outputPath("02-portrait-draw-182ms.png") });
  await page.touchscreen.tap(before.x + before.width / 2, before.y + before.height / 2);
  await expect(tile).toHaveAttribute("aria-pressed", "true");
  expect(await tile.boundingBox()).toEqual(before);
  expect(wire.commands).toHaveLength(1);
  const confirm = page.getByRole("button", { name: "Discard 9 dots" });
  const confirmPoint = await midpoint(confirm);
  await seek(page, 302);
  expect(await midpoint(confirm)).toEqual(confirmPoint);
  await page.touchscreen.tap(confirmPoint.x, confirmPoint.y);
  await expect.poll(() => wire.commands.length).toBe(2);
  await page.touchscreen.tap(confirmPoint.x, confirmPoint.y);
  await expect(confirm).toBeDisabled();
  expect(wire.commands).toHaveLength(2);
  const choice = wire.commands[1];
  expect(choice).toMatchObject({ op: "choose", payload: {
    roomId: draw.roomId, windowId: draw.game!.window!.windowId, anchorGameRevision: draw.gameRevision,
    optionId: draw.self.legalOptions.at(-1)!.optionId,
  } });

  const discard = advance(draw);
  discard.self = { ...baseline.self };
  discard.game!.seats[0].concealedCount--;
  discard.game!.seats[0].discards.push("d9");
  discard.game!.phase = "claim";
  discard.game!.turnSeat = null;
  discard.game!.window = { ...discard.game!.window!, windowId: "claim-5", reason: "discard",
    trigger: { seat: 0, faces: ["d9"] }, anchorGameRevision: discard.gameRevision };
  wire.publish(discard, choice);
  const toss = page.locator(".effect-flight--discard.effect-flight");
  await expect(toss).toHaveCount(1);
  await expect(flight).toHaveCount(0);
  await expect(tile).toHaveCount(0);
  await expect(toss.locator("[data-tile-id]")).toHaveCount(0);
  const from = await midpoint(toss);
  expect(from.x).toBeCloseTo(before.x + before.width / 2, 0);
  expect(from.y).toBeCloseTo(before.y + before.height / 2, 0);
  const river = page.locator(".river--near .last-discard");
  await expect(river.getByRole("img", { name: "9 dots" })).toBeVisible();
  await seek(page, 206);
  await page.screenshot({ path: testInfo.outputPath("03-portrait-discard-206ms.png") });
  await seek(page, 337);
  const landing = await midpoint(toss), target = await midpoint(river);
  expect(Math.hypot(landing.x - target.x, landing.y - target.y)).toBeLessThan(4);
});

test("distinct stamp bursts and two seat-local wins fit the short landscape lead-in", async ({ page }, testInfo) => {
  await page.setViewportSize({ width: 740, height: 320 });
  let state = frame(10, 1, false);
  const wire = await open(page, state);
  await page.evaluate(() => {
    for (const [edge, inset] of [["t", 10], ["r", 22], ["b", 13], ["l", 25]]) {
      document.documentElement.style.setProperty(`--safe-${edge}`, `${inset}px`);
    }
  });
  expect((await page.locator(".board-stage").boundingBox())!.x).toBeGreaterThanOrEqual(25);
  const cases: { seat: Seat; label: string; meld: Meld }[] = [
    { seat: 1, label: "Cha", meld: { kind: "chi", fromSeat: 0, tiles: ["b4", "b5", "b6"] } },
    { seat: 2, label: "Peng", meld: { kind: "peng", fromSeat: 3, tiles: ["d3", "d3", "d3"] } },
    { seat: 2, label: "Kong", meld: { kind: "gang", method: "added", fromSeat: 3, tiles: ["d3", "d3", "d3", "d3"] } },
    { seat: 3, label: "Supplement", meld: { kind: "bu", method: "concealed", fromSeat: null, tiles: [null, null, null, null] } },
  ];
  for (const { seat, label, meld } of cases) {
    state = advance(state);
    state.game!.seats[seat].melds = [meld];
    state.game!.seats[seat].concealedCount = meld.kind === "chi" || meld.kind === "peng" ? 11 : 10;
    state.game!.turnSeat = seat;
    wire.publish(state);
    const stamp = page.locator(`.effect-stamp--${meld.kind}`);
    await expect(stamp.locator(".effect-caption")).toHaveText(label);
    await expect(stamp.locator(".effect-ring")).toHaveCount(2);
    await expect(stamp.locator(".effect-spark")).toHaveCount(7);
    await expect(page.locator(`[data-seat="${seat}"] .seat-feedback`)).toHaveText(label);
    if (meld.kind === "chi") {
      await expect(page.locator('.seat-label[data-seat="1"]').getByRole("group", { name: "Cha · from East" }))
        .toBeVisible();
    }
    const seal = stamp.locator(".effect-seal");
    const small = (await seal.boundingBox())!;
    await seek(page, 108);
    const big = (await seal.boundingBox())!;
    expect(big.width / small.width).toBeGreaterThan(1.7);
    expect(big.y).toBeLessThan(small.y - 20);
    const viewport = page.viewportSize()!;
    expect(big.x).toBeGreaterThan(0);
    expect(big.x + big.width).toBeLessThan(viewport.width);
    if (meld.kind === "peng") {
      expect(big.y).toBeGreaterThanOrEqual((await page.locator(layer).boundingBox())!.y);
      await page.screenshot({ path: testInfo.outputPath("04-compact-peng-108ms.png") });
    }
    if (meld.method === "concealed") {
      await expect(page.locator(`[data-seat="${seat}"] .meld-group .tile-back`)).toHaveCount(4);
      await expect(stamp.locator(".tile-art")).toHaveCount(0);
    }
  }

  wire.publish(settled(state));
  const wins = page.locator(".effect-stamp--win");
  await expect(wins).toHaveCount(2);
  const results = page.getByRole("dialog", { name: "Hand results" });
  await expect(results).toBeHidden();
  await seek(page, 170);
  await page.screenshot({ path: testInfo.outputPath("05-compact-win-score-170ms.png") });
  await expect(page.locator(".effect-score--gain")).toHaveText(["+8", "+8"]);
  await expect(page.locator(".effect-score--loss")).toHaveText("−16");
  await expect(page.locator('[data-seat="1"] .seat-feedback')).toHaveText("Hu · +8");
  await expect(page.locator('[data-seat="2"] .seat-feedback')).toHaveText("Hu · +8");
  const right = await midpoint(wins.nth(0)), far = await midpoint(wins.nth(1));
  expect(right.x - far.x).toBeGreaterThan(100);
  expect(right.y - far.y).toBeGreaterThan(15);
  await expect(page.locator(".table-actions").getByText("Hand results", { exact: true })).toBeEnabled();
  await expect(results.getByRole("button", { name: "Next hand" })).toBeEnabled();
});

for (const viewer of seats) {
  test(`seat ${viewer} sees its Cha source to the left and submits the original chi option`, async ({ page }) => {
    const predecessor = ((viewer + 3) % 4) as Seat;
    const successor = ((viewer + 1) % 4) as Seat;
    const baseline = frame(20, 1, false);
    baseline.self.seat = viewer;
    const first = discardFrom(baseline, predecessor, "b8");
    first.self.legalOptions = [{
      optionId: "claim-sequence", kind: "chi", tileIds: [hand[3].tileId, hand[4].tileId], faces: ["b6", "b7", "b8"],
    }, { optionId: "decline", kind: "pass", tileIds: [], faces: [] }];
    const wire = await open(page, first);
    const source = page.locator(`.seat-label--left[data-seat="${predecessor}"]`);
    const nextSeat = page.locator(`.seat-label--right[data-seat="${successor}"]`);
    const own = page.locator(`.seat-label--near[data-seat="${viewer}"]`);
    await expect(source.locator(".seat-name")).toHaveText(players[predecessor]);
    await expect(nextSeat.locator(".seat-name")).toHaveText(players[successor]);
    await expect(own.locator(".seat-name")).toHaveText("You");
    await expect(page.locator(".river--left .last-discard").getByRole("img", { name: "8 bamboo" })).toBeVisible();
    expect((await midpoint(source)).x).toBeLessThan((await midpoint(own)).x - 30);
    const sourceActor = actor(page, predecessor);
    await expect(sourceActor).toHaveAttribute("data-place", "left");
    await expect(actor(page, successor)).toHaveAttribute("data-place", "right");
    expect((await midpoint(sourceActor)).x).toBeLessThan((await midpoint(actor(page, viewer))).x - 20);
    await page.getByRole("button", { name: "Cha: 6 bamboo, 7 bamboo, 8 bamboo" }).tap();
    await expect.poll(() => wire.commands[0]).toMatchObject({ op: "choose", payload: {
      windowId: first.game!.window!.windowId, anchorGameRevision: first.gameRevision, optionId: "claim-sequence",
    } });
    const next = advance(first);
    next.game!.seats[predecessor].discards.pop();
    next.game!.seats[viewer].melds = [{ kind: "chi", fromSeat: predecessor, tiles: ["b6", "b7", "b8"] }];
    next.self.hand = next.self.hand.filter(tile => !first.self.legalOptions[0].tileIds.includes(tile.tileId));
    next.self.legalOptions = [];
    wire.publish(next, wire.commands[0]);
    const cha = page.getByRole("group", { name: `Cha · from ${["East", "South", "West", "North"][predecessor]}` });
    await expect(cha.getByRole("img", { name: "8 bamboo" })).toBeVisible();
    await expect(page.locator(".effect-stamp--chi .effect-caption")).toHaveText("Cha");
    await expect(own.locator(".seat-feedback")).toHaveText("Cha");
  });
}

test("Roll for Kong is offered only for the server's legal Gang", async ({ page }) => {
  await page.setViewportSize({ width: 740, height: 320 });
  const first = frame(33);
  first.self.hand = first.self.hand.map((tile, i) => i < 4 ? { ...tile, face: "c8" } : tile);
  const quad = first.self.hand.slice(0, 4);
  first.self.legalOptions = [{ optionId: "supplement-c8", kind: "bu", tileIds: quad.map(tile => tile.tileId), faces: quad.map(tile => tile.face) }];
  const wire = await open(page, first);
  const roll = page.getByRole("button", { name: /^Roll for Kong/ });
  await expect(page.getByRole("button", { name: /^Supplement/ })).toBeVisible();
  await expect(roll).toHaveCount(0);
  const offered = advance(first);
  offered.game!.window = { ...offered.game!.window!, windowId: "legal-kong", anchorGameRevision: offered.gameRevision };
  offered.self.legalOptions = [{ ...offered.self.legalOptions[0], kind: "gang", optionId: "roll-c8" }];
  wire.publish(offered);
  await expect(roll).toBeInViewport({ ratio: 1 });
  await roll.tap();
  await expect.poll(() => wire.commands[0]).toMatchObject({ op: "choose", payload: {
    windowId: "legal-kong", anchorGameRevision: offered.gameRevision, optionId: "roll-c8",
  } });
});

for (const [width, height] of [[1440, 900], [740, 320]] as const) {
  test(`two committed winners light the hall and reveal their actual hands at ${width}×${height}`, async ({ page }, testInfo) => {
    await page.setViewportSize({ width, height });
    await page.clock.install();
    await page.clock.pauseAt(new Date(Date.now() + 1500));
    const first = discardFrom(frame(40, 1, false), 0, "b8");
    const won = revealed(first);
    first.game!.seats[1].melds = [
      { kind: "gang", method: "concealed", fromSeat: null, tiles: [null, null, null, null] },
      { kind: "chi", fromSeat: 0, tiles: ["d3", "d4", "d5"] },
    ];
    won.game!.seats[1].melds = first.game!.seats[1].melds;
    for (const [i, seat] of won.game!.seats.entries()) {
      seat.discards.unshift(...(["c3", "d2", "b9", "b6", "d5", "c8", "d7"] as Face[]));
      first.game!.seats[i].discards = seat.discards;
    }
    const wire = await open(page, first, false, true);
    if (width === 740) {
      await page.evaluate(() => {
        for (const [edge, inset] of [["t", 10], ["r", 22], ["b", 13], ["l", 25]]) {
          document.documentElement.style.setProperty(`--safe-${edge}`, `${inset}px`);
        }
      });
      // Stress both columns with wider text and larger font metrics than the compact defaults.
      await page.addStyleTag({ content: `
        .modal .winner h3 { font-size: 1.075rem; }
        .modal .winner .meld-caption { font-size: .74rem; letter-spacing: .09em; }
        .modal .scoreboard { font-size: .9rem; letter-spacing: .065em; }
        .modal .result-overview :is(.detail, .note) { font-size: .95rem; letter-spacing: .04em; }
      ` });
    }
    await expect(page.locator(".seat-actor")).toHaveCount(4);
    await expect(page.locator(".seat-label[data-seat='1'] .tile-back")).toHaveCount(4);
    await expect(page.locator(finale)).toHaveCount(0);
    wire.publish(won);

    const art = page.locator(finale), crest = art.locator(".finale-crest"), relays = art.locator("[data-finale-seat]");
    await expect(relays).toHaveCount(2);
    await expect(art).toHaveAttribute("aria-hidden", "true");
    await expect(art).toHaveCSS("pointer-events", "none");
    await expect(page.locator(".seat-label[data-seat='1'] .seat-feedback")).toHaveText("Hu · +8");
    await expect(page.locator(".seat-label[data-seat='2'] .seat-feedback")).toHaveText("Hu · +8");
    await expect(page.locator(".table-effects .effect-score--loss")).toHaveText("−16");
    const participants = await relays.evaluateAll(nodes => nodes.map(node => node.getAttribute("data-finale-seat")));
    expect(participants).toEqual(["1", "2"]);
    const resultsButton = page.locator(".table-actions").getByRole("button", { name: "Hand results" });
    await expect(resultsButton).toBeEnabled();
    const geometry = () => Promise.all([".board-stage", ".rack", ".table-actions"].map(sel => page.locator(sel).boundingBox()));
    const layout = await geometry();
    const starting = await Promise.all([midpoint(relays.nth(0)), midpoint(relays.nth(1))]);
    for (const [i, seat] of ([1, 2] as const).entries()) {
      const position = await midpoint(actor(page, seat));
      expect(distance(position, starting[i]), `seat ${seat} originates at its actor`).toBeLessThan(width === 740 ? 34 : 62);
    }
    await seekCast(page, 162);
    await seek(page, 162);
    const flying = await Promise.all([midpoint(relays.nth(0)), midpoint(relays.nth(1))]);
    expect(distance(flying[0], starting[0])).toBeGreaterThan(38);
    expect(distance(flying[1], starting[1])).toBeGreaterThan(16);
    await page.screenshot({ path: testInfo.outputPath("01-votives-in-flight-162ms.png") });
    await seekCast(page, 374);
    await seek(page, 374);
    await expect(crest).toHaveCSS("opacity", "1");
    const lantern = await midpoint(crest), bounds = (await crest.boundingBox())!;
    const docked = await Promise.all([midpoint(relays.nth(0)), midpoint(relays.nth(1))]);
    expect(distance(docked[0], lantern)).toBeLessThan(bounds.width * .45);
    expect(distance(docked[1], lantern)).toBeLessThan(bounds.width * .45);
    expect(distance(docked[0], docked[1])).toBeGreaterThan(bounds.width * .30);
    expect(lantern.x).toBeGreaterThan(layout[0]!.x + layout[0]!.width * .5);
    expect(lantern.x).toBeLessThan(layout[0]!.x + layout[0]!.width * .72);
    expect(bounds.width).toBeGreaterThan(width === 740 ? 100 : 200);
    expect(await geometry()).toEqual(layout);
    const buttonPoint = await midpoint(resultsButton);
    expect(await resultsButton.evaluate((node, p) => node.contains(document.elementFromPoint(p.x, p.y)), buttonPoint))
      .toBe(true);
    await page.screenshot({ path: testInfo.outputPath("02-lantern-tableau-374ms.png") });

    const updatedName = width === 740 ? "Jade of the Lantern Hall" : "Jade II";
    const dealerName = width === 740 ? "River of the Jade Court" : players[0];
    wire.publish({ ...won, roomRevision: won.roomRevision + 1,
      room: { ...won.room, seats: won.room.seats.map(seat => seat.seat === 3 ? { ...seat, displayName: updatedName } :
        seat.seat === 0 ? { ...seat, displayName: dealerName } : seat) } });
    await expect(page.locator('.seat-label[data-seat="3"] .seat-name')).toHaveText(updatedName);
    expect(await midpoint(relays.nth(0))).toEqual(docked[0]);
    const results = page.getByRole("dialog", { name: "Hand results" });
    if (width === 740) {
      await resultsButton.focus();
      await page.keyboard.press("Enter");
    } else {
      await page.clock.runFor(539);
      await expect(results).toBeHidden();
      await page.clock.runFor(1);
    }
    await expect(results).toBeVisible();
    await expect(results.getByText("Willow and Ink won this hand.")).toBeVisible();
    await expect(results.getByRole("row", { name: `${dealerName} (you) -16 -16 -16` })).toHaveCount(1);
    await expect(results.getByText(`Next dealer: ${dealerName}`, { exact: true })).toBeVisible();
    await expect(results.getByRole("rowheader", { name: updatedName, exact: true })).toBeVisible();
    const firstWinner = results.locator(".winner").filter({ hasText: "Willow" });
    const secondWinner = results.locator(".winner").filter({ hasText: "Ink" });
    await expect(firstWinner.getByRole("group", { name: "Concealed tiles" }).getByRole("img")).toHaveCount(7);
    await expect(firstWinner.getByRole("group", { name: "Concealed Kong" }).getByRole("img", { name: "9 characters" }))
      .toHaveCount(4);
    await expect(page.locator(".exposed-panel .meld-concealed .tile-back")).toHaveCount(4);
    await expect(page.locator(".exposed-panel .meld-concealed use")).toHaveCount(0);
    await expect(firstWinner.getByRole("group", { name: "Cha · from East" }).getByRole("img")).toHaveCount(3);
    await expect(firstWinner.getByRole("img", { name: "8 bamboo" })).toHaveCount(3);
    await expect(secondWinner.getByRole("group", { name: "Concealed tiles" }).getByRole("img")).toHaveCount(13);
    for (const winner of [firstWinner, secondWinner]) {
      const event = winner.getByRole("group", { name: "Winning tile" });
      await expect(event.getByRole("img", { name: "8 bamboo" })).toHaveCount(1);
      await expect(event).toHaveCSS("border-top-color", "rgb(218, 172, 101)");
    }
    await expect(results.getByRole("img", { name: "6 dots" })).toHaveCount(0);
    await expect(results.getByRole("img", { name: "Concealed tile" })).toHaveCount(0);
    if (width === 740) await expect(results.locator(".winning-hands")).toBeInViewport({ ratio: 1 });
    expect(await results.evaluate(node => node.scrollWidth - node.clientWidth)).toBeLessThanOrEqual(1);
    await finishCast(page);
    await expect(art).toHaveCount(0);
    await page.locator(layer).evaluate(root => root.getAnimations({ subtree: true }).forEach(animation => animation.finish()));
    await page.screenshot({ path: testInfo.outputPath("03-official-winning-hands.png") });
    const next = results.getByRole("button", { name: "Next hand" });
    if (width === 740) {
      await expect(next).toBeInViewport({ ratio: 1 });
      for (const button of [next, results.getByRole("button", { name: "Close dialog" })]) {
        const box = (await button.boundingBox())!;
        expect(Math.min(box.width, box.height)).toBeGreaterThanOrEqual(44);
      }
      const nextBounds = (await next.boundingBox())!;
      const handBounds = (await results.locator(".winning-hands").boundingBox())!;
      expect(nextBounds.x + nextBounds.width).toBeLessThan(handBounds.x);
      await testInfo.attach("compact-review-bounds", {
        body: JSON.stringify({ nextHand: nextBounds, winningHands: handBounds }), contentType: "application/json",
      });
      expect(await results.getByRole("cell").evaluateAll(cells =>
        cells.every(cell => cell.scrollWidth <= cell.clientWidth))).toBe(true);
      const point = await midpoint(next);
      expect(await next.evaluate((node, p) => node.contains(document.elementFromPoint(p.x, p.y)), point)).toBe(true);
      await page.touchscreen.tap(point.x, point.y);
    } else {
      await next.focus();
      await expect(next).toBeFocused();
      await page.keyboard.press("Enter");
    }
    await expect.poll(() => wire.commands[0]?.op).toBe("nextHand");
  });
}

test("settled hands render before deferred art on first sync; that art never catches up", async ({ page }) => {
  let release!: () => void;
  const blocked = new Promise<void>(resolve => { release = resolve; });
  await page.route("**/src/finale.css", async route => {
    await blocked;
    await route.continue();
  });
  const recovered = revealed(frame(61, 4, false));
  recovered.room.seats[3].kind = "human";
  await open(page, recovered, false, true);
  const results = page.getByRole("dialog", { name: "Match results" });
  try {
    await expect(results).toBeVisible();
    await expect(results.getByRole("group", { name: "Concealed Kong" }).getByRole("img")).toHaveCount(4);
    await expect(results.getByRole("group", { name: "Winning tile" })).toHaveCount(2);
    await expect(results.getByRole("button", { name: "Rematch" })).toBeEnabled();
    await expect(results.getByRole("button", { name: "Leave table" })).toBeEnabled();
    await expect(results.getByRole("button", { name: "New game" })).toHaveCount(0);
    expect(await results.evaluate(node => node.scrollWidth - node.clientWidth)).toBeLessThanOrEqual(1);
    await expect(page.locator(".seat-actor")).toHaveCount(0);
  } finally { release(); }
  await expect(page.locator(".seat-actor")).toHaveCount(4);
  await expect(page.locator(finale)).toHaveCount(0);
  await expect(results.getByRole("group", { name: "Winning tile" })).toHaveCount(2);
});

test("the finale skips draws and releases all effects on visibility loss, reconnect and departure", async ({ page }) => {
  const first = frame(70);
  const wire = await open(page, first, false, true);
  const art = page.locator(finale);
  const results = page.getByRole("dialog", { name: "Hand results" });
  await expect(page.locator(".seat-actor")).toHaveCount(4);
  const draw = settled(first);
  const game = draw.game!;
  game.settlement = { reason: "draw", sourceSeat: null, nextDealerSeat: 0, winners: [], birds: [],
    seatResults: seats.map(seat => ({ seat, delta: 0, total: 0 })) };
  game.seats.forEach(seat => { seat.total = 0; });
  wire.publish(draw);
  await expect(results.getByText("The hand ended in a draw.")).toBeVisible();
  await expect(results.locator(".winning-hands")).toHaveCount(0);
  await expect(art).toHaveCount(0);
  await expect(page.locator(".effect-stamp--win")).toHaveCount(0);

  const turn = async (after: Snapshot, number: number) => {
    const state = frame(after.gameRevision + 1, number);
    wire.publish(state);
    await expect(page.locator(".table-caption")).toHaveText(`Hand ${number} / 4`);
    return state;
  };
  const second = settled(await turn(draw, 2), [3]);
  wire.publish(second);
  await expect(art.locator("[data-finale-seat]")).toHaveAttribute("data-finale-seat", "3");
  await page.evaluate(() => {
    Object.defineProperty(document, "hidden", { configurable: true, value: true });
    document.dispatchEvent(new Event("visibilitychange"));
  });
  await expect(art).toHaveCount(0);
  await expect(results).toBeVisible();
  await page.evaluate(() => {
    Reflect.deleteProperty(document, "hidden");
    document.dispatchEvent(new Event("visibilitychange"));
  });
  const third = revealed(await turn(second, 3));
  wire.publish(third);
  await expect(art.locator("[data-finale-seat]")).toHaveCount(2);
  wire.reconnect(third);
  await expect(art).toHaveCount(0);
  await expect(page.locator('.connection[data-status="online"]')).toBeVisible();
  await expect(results.getByRole("group", { name: "Concealed Kong" }).getByRole("img")).toHaveCount(4);

  const last = settled(await turn(third, 4), [2]);
  wire.publish(last);
  const active = await art.elementHandle();
  const match = page.getByRole("dialog", { name: "Match results" });
  await expect(match.getByRole("button", { name: "New game" })).toBeVisible();
  await match.getByRole("button", { name: "Quit table" }).tap();
  await page.getByRole("dialog", { name: "Quit table?" })
    .getByRole("button", { name: "Quit table" }).tap();
  await expect.poll(() => wire.commands[0]?.op).toBe("leaveRoom");
  expect(wire.commands[0].payload).toEqual({ roomId: last.roomId, expectedRoomRevision: last.roomRevision, abandon: true });
  wire.publish(last, wire.commands[0]);
  await expect(page.getByRole("button", { name: "Play with bots" })).toBeVisible();
  expect(await active.evaluate(node => node.parentElement === null && node.getAnimations({ subtree: true }).length === 0)).toBe(true);
});

test("first sync, gaps, reconnect, duplicate snapshots and rematch obey revision continuity", async ({ page }) => {
  const first = frame(5);
  const wire = await open(page, first, false, true);
  const active = page.locator(`${layer} > *`);
  const packets = page.locator(".effect-flight--deal.effect-flight");
  await expect(page.locator(".hand-tile")).toHaveCount(14);
  await expect(page.locator(".seat-actor")).toHaveCount(4);
  await expect(active).toHaveCount(0);

  const scored = settled(first, [2]);
  wire.publish(scored);
  await expect(page.locator(".effect-stamp--win")).toHaveCount(1);
  await expect(page.locator(`${finale} [data-finale-seat]`)).toHaveAttribute("data-finale-seat", "2");
  await page.setViewportSize({ width: 740, height: 320 });
  await expect(page.locator(finale)).toHaveCount(0);
  await expect(page.locator(".seat-label[data-seat='2'] .seat-feedback")).toHaveText("Hu · +8");
  const next = frame(scored.gameRevision + 1, 2);
  wire.publish(next);
  await expect(packets).toHaveCount(4);
  await expect(page.locator(".effect-stamp--win")).toHaveCount(0);
  await seek(page, 203);
  const position = await midpoint(packets.first());
  wire.publish({ ...next, roomRevision: next.roomRevision + 1,
    room: { ...next.room, seats: next.room.seats.map(seat => seat.seat === 1 ? { ...seat, displayName: "Willow II" } : seat) } });
  await expect(page.locator('[data-seat="1"] .seat-name')).toHaveText("Willow II");
  expect(await midpoint(packets.first())).toEqual(position);

  const skipped = { ...settled(frame(next.gameRevision + 3, 4)), roomRevision: next.roomRevision + 4 };
  wire.publish(skipped);
  await expect(page.getByRole("dialog", { name: "Match results" })).toBeVisible();
  await expect(active).toHaveCount(0);
  await expect(page.locator(finale)).toHaveCount(0);
  const rematch = waiting(skipped.gameRevision + 1);
  wire.publish(rematch);
  await expect(page.getByRole("heading", { name: "Take a seat." })).toBeVisible();
  await expect(active).toHaveCount(0);
  const restart = frame(rematch.gameRevision + 1);
  wire.publish(restart);
  await expect(packets).toHaveCount(4);
  await expect(actor(page, 0)).toHaveAttribute("data-gesture", "gather");
  await page.setViewportSize({ width: 390, height: 780 });
  await expect(active).toHaveCount(0);
  await expect(gesture(page)).toHaveCount(0);

  const recovered = settled(restart);
  wire.reconnect(recovered);
  await expect(page.getByRole("dialog", { name: "Hand results" })).toBeVisible();
  await expect(page.locator('.connection[data-status="online"]')).toBeVisible();
  await expect(active).toHaveCount(0);
  await expect(page.locator(finale)).toHaveCount(0);
  wire.publish(frame(recovered.gameRevision + 1, 2));
  await expect(packets).toHaveCount(4);
});

test("bot-only hand results restart the match but keep table scores without replaying effects", async ({ page }) => {
  const first = frame(91, 2);
  const wire = await open(page, first, false, true);
  await expect(page.locator(".seat-actor")).toHaveCount(4);
  const scored = settled(first, [2]);
  scored.room.tableTotals = [-5, 0, 5, 0];
  wire.publish(scored);
  await expect(page.locator(`${finale} [data-finale-seat]`)).toHaveAttribute("data-finale-seat", "2");
  const results = page.getByRole("dialog", { name: "Hand results", exact: true });
  const newGame = results.getByRole("button", { name: "New game" });
  await expect(results.getByRole("button", { name: "Next hand" })).toBeVisible();
  await expect(results.getByRole("button", { name: "Quit table" })).toBeVisible();
  await newGame.tap();
  const confirm = page.getByRole("dialog", { name: "New game?" });
  await confirm.getByRole("button", { name: "Cancel" }).tap();
  await expect(newGame).toBeFocused();
  await expect(results.getByRole("row", { name: "River (you) -8 -8 -5" })).toBeVisible();
  expect(wire.commands).toHaveLength(0);

  await newGame.tap();
  await confirm.getByRole("button", { name: "Start new game" }).tap();
  await expect.poll(() => wire.commands[0]?.op).toBe("restartMatch");
  expect(wire.commands[0].payload).toEqual({ roomId: scored.roomId, expectedRoomRevision: scored.roomRevision });
  const fresh = frame(scored.gameRevision + 1);
  fresh.roomRevision = scored.roomRevision + 1;
  fresh.room.tableTotals = scored.room.tableTotals;
  wire.publish(fresh, wire.commands[0]);
  await expect(page.locator(".table-caption")).toHaveText("Hand 1 / 4");
  await expect(page.getByRole("dialog")).toHaveCount(0);
  await expect(page.getByRole("button", { name: "Table options" })).toBeFocused();
  await expect(page.locator(".seat-score")).toHaveText(["-5", "0", "5", "0"]);
  await expect(page.locator(`${layer} > *`)).toHaveCount(0);
  await expect(page.locator(finale)).toHaveCount(0);
});

for (const [width, height] of [[320, 730], [740, 320]]) {
  test(`committed opponent melds stay legible without moving the ${width}px table controls`, async ({ page }, info) => {
    await page.setViewportSize({ width, height });
    await page.emulateMedia({ reducedMotion: "reduce" });
    const first = frame(111);
    first.game!.dealerSeat = 1;
    first.game!.seats[1].concealedCount = 10;
    first.game!.seats[1].melds = [{ kind: "gang", method: "concealed", fromSeat: null, tiles: [null, null, null, null] }];
    const wire = await open(page, first);
    await page.addStyleTag({ content: width === 740
      ? ":root { --safe-t: 10px; --safe-r: 22px; --safe-b: 13px; --safe-l: 25px }"
      : ":root { --safe-t: 8px; --safe-r: 7px; --safe-b: 9px; --safe-l: 6px }" });
    await expect(page.locator(".seat-actor")).toHaveCount(4);
    const trigger = page.getByRole("button", { name: /^Exposed tiles/ });
    const viewer = page.getByRole("region", { name: "Exposed tiles" });
    await expect(trigger).toHaveCount(0);
    const bounds = () => page.locator(".board-plane, .hand-grid, .table-actions, .discard-confirm").evaluateAll(nodes =>
      nodes.map(node => {
        const { x, y, width, height } = node.getBoundingClientRect();
        return [x + scrollX, y + scrollY, width, height].map(value => Number(value.toFixed(2)));
      }));
    const before = await bounds();

    const called = advance(first);
    called.game!.seats[1].concealedCount = 7;
    called.game!.seats[1].melds.push({ kind: "chi", method: null, fromSeat: 0, tiles: ["b2", "b3", "b4"] });
    wire.publish(called);
    await expect(trigger.getByText("New")).toBeVisible();
    await expect(trigger).toBeInViewport({ ratio: 1 });
    const control = (await trigger.boundingBox())!;
    expect(Math.min(control.width, control.height)).toBeGreaterThanOrEqual(44);
    expect(await bounds()).toEqual(before);
    const edge = page.locator('.seat-label[data-seat="1"] .edge-melds').getByRole("img", { name: "2 bamboo" });
    expect((await edge.boundingBox())!.width).toBeGreaterThan(11);
    await expect(page.locator(`${layer} > *`)).toHaveCount(0);
    if (width === 320) {
      await trigger.focus();
      await page.keyboard.press("Enter");
    } else await trigger.tap();
    await expect(viewer.getByRole("heading", { name: "Exposed tiles" })).toBeFocused();
    expect(await bounds()).toEqual(before);
    await expect(viewer.getByText("Latest seen: Willow (East) · Cha · from North")).toBeVisible();
    const cha = viewer.getByRole("group", { name: "Cha · from North" });
    const face = cha.getByRole("img", { name: "2 bamboo" });
    await expect(face).toBeInViewport();
    const drawnFace = (await face.boundingBox())!;
    expect(drawnFace.width).toBeGreaterThanOrEqual(44);
    expect(drawnFace.height).toBeGreaterThanOrEqual(54);
    await expect(face.locator("use")).toHaveAttribute("href", /\/tile-suits\.svg#lt-b2$/);
    const backs = viewer.getByRole("group", { name: "Concealed Kong" });
    await expect(backs.getByRole("img", { name: "Concealed tile" })).toHaveCount(4);
    await expect(backs.locator("use")).toHaveCount(0);
    await expect(page.locator('.edge-melds .meld-concealed use')).toHaveCount(0);
    await page.screenshot({ path: info.outputPath("exposed-tiles.png") });
    const back = viewer.getByRole("button", { name: "Back to table" });
    expect(Math.min((await back.boundingBox())!.width, (await back.boundingBox())!.height)).toBeGreaterThanOrEqual(44);
    if (width === 320) await page.keyboard.press("Escape");
    else await back.tap();
    await expect(trigger).toBeFocused();
    await expect(viewer).toBeHidden();
    await expect(trigger.getByText("New")).toBeHidden();
    expect(await page.locator(".discard-confirm").evaluate(node => {
      const rect = node.getBoundingClientRect();
      return node.contains(document.elementFromPoint(rect.x + rect.width / 2, rect.y + rect.height / 2));
    })).toBe(true);

    const pending = advance(called);
    pending.game!.phase = "claim";
    pending.game!.turnSeat = null;
    pending.self.legalOptions = [];
    pending.game!.seats[2].concealedCount = 8;
    pending.game!.seats[2].melds = [
      { kind: "peng", method: null, fromSeat: 3, tiles: ["c5", "c5", "c5"] },
      { kind: "gang", method: "open", fromSeat: 1, tiles: ["d4", "d4", "d4", "d4"] },
    ];
    pending.game!.window = { ...pending.game!.window!, windowId: "rob-bu", anchorGameRevision: pending.gameRevision,
      reason: "kong", trigger: { seat: 2, faces: ["c5"] } };
    wire.publish(pending);
    await trigger.tap();
    await expect(viewer.getByRole("group", { name: "Peng · from West" }).getByRole("img")).toHaveCount(3);
    await expect(viewer.getByRole("group", { name: "Kong · from East", exact: true }).getByRole("img")).toHaveCount(4);
    await expect(viewer.getByRole("group", { name: /^Added Supplement/ })).toHaveCount(0);
    await back.tap();
    const added = advance(pending);
    added.game!.phase = "turn";
    added.game!.turnSeat = 2;
    added.game!.seats[2].melds[0] = { kind: "bu", method: "added", fromSeat: 3, tiles: ["c5", "c5", "c5", "c5"] };
    added.game!.window = { ...added.game!.window!, windowId: "after-bu", anchorGameRevision: added.gameRevision,
      reason: "turn", trigger: null };
    wire.reconnect(added);
    await expect(page.locator('.connection[data-status="online"]')).toBeVisible();
    await expect(trigger.getByText("New")).toBeVisible();
    await trigger.tap();
    await expect(viewer.getByText("Latest seen: Ink (South) · Added Supplement · from West")).toBeVisible();
    await expect(viewer.getByRole("group", { name: "Added Supplement · from West" }).getByRole("img", { name: "5 characters" })).toHaveCount(4);
    await expect(cha.getByRole("img")).toHaveCount(3);
    await expect(backs.locator("use")).toHaveCount(0);
    expect(wire.commands).toHaveLength(0);
    await info.attach("exposed-layout", { body: JSON.stringify({ width, height, before, control, drawnFace }), contentType: "application/json" });
  });
}

for (const [width, height] of [[320, 730], [740, 320]] as const) {
  test(`two-bird results show both faces before scrolling at ${width}×${height}`, async ({ page }, info) => {
    await page.setViewportSize({ width, height });
    const complete = width === 320;
    const state = frame(130, complete ? 4 : 2);
    state.room.rulesVersion = "changsha-two-bird-v1";
    state.room.status = complete ? "complete" : "betweenHands";
    state.room.tableTotals = [-11, 28, -31, 14];
    const winnerName = "W".repeat(24), dealerName = "M".repeat(24);
    state.room.seats[0].displayName = winnerName;
    state.room.seats[2].displayName = dealerName;
    state.self.legalOptions = [];
    const game = state.game!;
    game.dealerSeat = 2;
    game.phase = "settled";
    game.turnSeat = game.window = null;
    game.wallCount = 0;
    game.settlement = {
      reason: "win", sourceSeat: 2, nextDealerSeat: 0,
      winners: [{ seat: 0, winBy: "discard", winningTiles: ["d8"], patterns: [{ code: "plain", label: "Ordinary win (平胡)", points: 1 }] }],
      birds: ["d5", "c3"], transfers: [{ fromSeat: 2, toSeat: 0, points: 6, reason: "win" }],
      seatResults: [
        { seat: 0, delta: 6, total: 10 }, { seat: 1, delta: 0, total: -12 },
        { seat: 2, delta: -6, total: -8 }, { seat: 3, delta: 0, total: 10 },
      ],
    };
    game.seats.forEach(seat => { seat.total = game.settlement!.seatResults[seat.seat].total; });
    await open(page, state, true);
    if (!complete) await page.addStyleTag({ content: ":root { --safe-t: 10px; --safe-r: 22px; --safe-b: 13px; --safe-l: 25px; }" });
    const results = page.getByRole("dialog", { name: complete ? "Match results" : "Hand results" });
    const birds = results.getByRole("group", { name: /^Counts for / });
    await expect(birds).toHaveCount(2);
    const first = birds.nth(0).getByRole("img", { name: "5 dots", exact: true });
    const second = birds.nth(1).getByRole("img", { name: "3 characters", exact: true });
    await expect(first).toBeInViewport({ ratio: 1 });
    await expect(second).toBeInViewport({ ratio: 1 });
    if (!complete) await expect(results.getByRole("button", { name: "Next hand" })).toBeInViewport({ ratio: 1 });
    const [left, right] = await Promise.all([first.boundingBox(), second.boundingBox()]);
    expect(left!.y).toBe(right!.y);
    expect(left!.x + left!.width).toBeLessThan(right!.x);
    expect(await results.evaluate(node => node.scrollTop)).toBe(0);
    expect(await birds.evaluateAll(nodes => nodes.every(node => node.scrollWidth <= node.clientWidth))).toBe(true);
    expect(await results.evaluate(node => node.scrollWidth - node.clientWidth)).toBeLessThanOrEqual(1);
    await expect(birds.nth(0)).toHaveAccessibleName(`Counts for ${dealerName} (East)`);
    await expect(birds.nth(1)).toHaveAccessibleName(`Counts for ${winnerName} (West)`);
    await expect(first.locator("use")).toHaveAttribute("href", /#lt-d5$/);
    await expect(second.locator("use")).toHaveAttribute("href", /#lt-c3$/);

    await expect(results.getByRole("heading", { name: "Birds (2)", exact: true })).toBeInViewport({ ratio: 1 });
    await expect(results.getByRole("columnheader")).toHaveText(["Seat", "This hand", "Match total", "Table total"]);
    await expect(results.getByRole("row", { name: new RegExp(`^${winnerName} \\(you\\)`) }).getByRole("cell")).toHaveText(["+6", "+10", "-11"]);
    await expect(results.getByText("Two birds (house rules)", { exact: true })).toBeVisible();
    await expect(results.getByText(`Current dealer: ${dealerName}`)).toBeVisible();
    await expect(results.getByText(`Next dealer: ${winnerName}`)).toBeVisible();
    await expect(results.getByText(/wall tiles? remained after the win/)).toHaveCount(0);
    await expect(results.getByRole("group", { name: "Winning tile" }).getByRole("img", { name: "8 dots" })).toBeVisible();
    await expect(results.getByText(`${dealerName} → ${winnerName}: 6 (win)`)).toBeVisible();
    if (complete) await expect(results.getByText(/A new match keeps the table’s bird rules/)).toBeVisible();
    await page.screenshot({ path: info.outputPath("table-ledger.png") });
  });
}

for (const [name, wall, birds, code, note] of [
  ["one wall tile", 0, ["d5"], "plain", "Only one wall tile remained after the win."],
  ["no wall tiles", 0, [], "kongBloom", "No wall tiles remained after the win."],
  ["last-tile self-draw", 0, ["b8"], "lastTileDraw", "The winning last tile counts as one reference bird. No extra tile is flipped."],
  ["last-tile discard", 0, ["b8"], "lastTileDiscard", "The winning last tile counts as one reference bird. No extra tile is flipped."],
  ["a draw with one unclaimed tile", 1, [], "draw", "A draw flips no birds."],
] as const) {
  test(`two-bird results explain ${name}`, async ({ page }) => {
    await page.setViewportSize({ width: 740, height: 320 });
    const state = settled(frame(30), code === "draw" ? [] : [1]);
    state.room.rulesVersion = "changsha-two-bird-v1";
    state.game!.wallCount = wall;
    const settlement = state.game!.settlement!;
    settlement.birds = [...birds];
    if (code === "draw") {
      settlement.reason = "draw";
      settlement.sourceSeat = null;
      settlement.nextDealerSeat = 1;
    } else {
      const [winner] = settlement.winners;
      winner.patterns = [{ code, label: name, points: 1 }];
      if (code === "lastTileDraw") {
        winner.winBy = "selfDraw";
        settlement.sourceSeat = null;
      }
    }

    await open(page, state, true);
    const results = page.getByRole("dialog", { name: "Hand results" });
    await expect(results.getByRole("button", { name: "Next hand" })).toBeInViewport({ ratio: 1 });
    await expect(results.getByRole("heading", { name: `Birds (${birds.length})`, exact: true })).toBeVisible();
    await expect(results.getByText(note, { exact: true })).toBeVisible();
    const counted = results.getByRole("group", { name: /^Counts for / });
    await expect(counted).toHaveCount(birds.length);
    if (birds.length) await expect(counted.locator("use")).toHaveAttribute("href", new RegExp(`#lt-${birds[0]}$`));
  });
}

for (const preference of ["OS", "saved"] as const) {
  test(`${preference} reduced motion keeps immediate rack, call labels and results without ghosts`, async ({ page }) => {
    if (preference === "OS") await page.emulateMedia({ reducedMotion: "reduce" });
    const first = waiting(0);
    const wire = await open(page, first, preference === "saved");
    const started = frame(1);
    wire.publish(started);
    await expect(page.locator(".hand-tile")).toHaveCount(14);
    await expect(page.locator(".hand-tile.is-drawn .draw-mark")).toBeVisible();
    await expect(eyes(actor(page, 0), "open")).toBeVisible();
    await expect(gesture(page)).toHaveCount(0);
    const called = advance(started);
    called.game!.seats[2].melds.push({ kind: "peng", fromSeat: 3, tiles: ["d3", "d3", "d3"] });
    wire.publish(called);
    await expect(page.locator('[data-seat="2"] .seat-feedback')).toHaveText("Peng");
    await expect(pawPose(actor(page, 2), "rest")).toBeVisible();
    await expect(gesture(page)).toHaveCount(0);
    wire.publish(settled(called, [2]));
    const results = page.getByRole("dialog", { name: "Hand results" });
    await expect(results).toBeVisible();
    await expect(results.getByRole("group", { name: "Winning tile" }).getByRole("img", { name: "8 bamboo" })).toBeVisible();
    await expect(results.getByRole("group", { name: "Concealed tiles" })).toHaveCount(0);
    await expect(page.locator(finale)).toHaveCount(0);
    await expect(page.locator('[data-seat="2"] .seat-feedback')).toHaveText("Hu · +8");
    await expect(gesture(page)).toHaveCount(0);
    await expect(eyes(actor(page, 2), "win")).toBeVisible();
    await expect(eyes(actor(page, 0), "loss")).toBeVisible();
    await expect(page.locator(`${layer} > *`)).toHaveCount(0);
  });
}

test("saving reduced motion during a deal interrupts the visible packets", async ({ page }) => {
  const first = waiting(0);
  const wire = await open(page, first, false, true);
  await page.route("**/api/profile", route => route.fulfill({
    json: { ...route.request().postDataJSON(), resumeRoomId: first.roomId },
  }));
  await page.evaluate(path => import(path).then(() => {}), "/src/actors.ts");
  const started = frame(1);
  wire.publish(started);
  await expect(page.locator(".effect-flight--deal.effect-flight")).toHaveCount(4);
  await expect(actor(page, 0)).toHaveAttribute("data-gesture", "gather");
  await seek(page, 246);
  await page.getByRole("button", { name: "Settings" }).tap();
  const settings = page.getByRole("dialog", { name: "Settings" });
  await settings.getByRole("checkbox", { name: "Reduce visual motion" }).check();
  await settings.getByRole("button", { name: "Save settings" }).tap();
  await expect(settings.getByText("Settings saved.")).toBeVisible();
  await expect(page.locator("html")).toHaveAttribute("data-reduced-motion", "true");
  await expect(page.locator(`${layer} > *`)).toHaveCount(0);
  await expect(gesture(page)).toHaveCount(0);
  await settings.getByRole("button", { name: "Close dialog" }).tap();
  await expect(page.getByRole("button", { name: "9 dots, drawn tile" })).toHaveAttribute("aria-disabled", "false");
});

test("pause and visibility interrupt the active cue; no catch-up on return", async ({ page }) => {
  const wire = await open(page, waiting(0), false, true);
  await page.evaluate(path => import(path).then(() => {}), "/src/actors.ts");
  const start = frame(1);
  wire.publish(start);
  const active = page.locator(`${layer} > *`);
  await expect(page.locator(".effect-flight--deal.effect-flight")).toHaveCount(4);
  await expect(actor(page, 0)).toHaveAttribute("data-gesture", "gather");
  const paused = { ...start, roomRevision: start.roomRevision + 1,
    room: { ...start.room, paused: true, autoResumeAtUtc: new Date(Date.now() + 60000).toISOString() } };
  wire.publish(paused);
  await expect(page.locator(".turn-title")).toHaveText("Table paused");
  await expect(active).toHaveCount(0);
  await expect(gesture(page)).toHaveCount(0);
  const resumed = { ...start, roomRevision: paused.roomRevision + 1 };
  wire.publish(resumed);
  await expect(page.locator(".turn-title")).toHaveText("Your turn");
  await expect(active).toHaveCount(0);
  await expect(gesture(page)).toHaveCount(0);

  const firstCall = advance(resumed);
  firstCall.game!.seats[1].melds.push({ kind: "chi", fromSeat: 0, tiles: ["b4", "b5", "b6"] });
  wire.publish(firstCall);
  await expect(page.locator(".effect-stamp--chi")).toHaveCount(1);
  await expect(actor(page, 1)).toHaveAttribute("data-gesture", "call");
  await page.evaluate(() => {
    Object.defineProperty(document, "hidden", { configurable: true, value: true });
    document.dispatchEvent(new Event("visibilitychange"));
  });
  await expect(active).toHaveCount(0);
  await expect(gesture(page)).toHaveCount(0);
  await page.evaluate(() => {
    Reflect.deleteProperty(document, "hidden");
    document.dispatchEvent(new Event("visibilitychange"));
  });
  const missed = advance(firstCall);
  missed.game!.seats[2].melds.push({ kind: "peng", fromSeat: 1, tiles: ["d3", "d3", "d3"] });
  wire.publish(missed);
  await expect(page.locator('[data-seat="2"] .meld-group')).toHaveCount(1);
  await expect(active).toHaveCount(0);
  await expect(gesture(page)).toHaveCount(0);
  const fresh = advance(missed);
  fresh.game!.seats[3].melds.push({ kind: "gang", method: "concealed", fromSeat: null, tiles: [null, null, null, null] });
  wire.publish(fresh);
  await expect(page.locator(".effect-stamp--gang")).toHaveCount(1);
  await expect(actor(page, 3)).toHaveAttribute("data-gesture", "call");
  await page.locator(layer).evaluate(root =>
    root.getAnimations({ subtree: true }).forEach(animation => animation.play()));
  await expect(active).toHaveCount(0);
});
