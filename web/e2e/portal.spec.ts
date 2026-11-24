import { gzipSync } from "node:zlib";
import { expect, test } from "@playwright/test";
import type { APIRequestContext, Locator, Page } from "@playwright/test";
import type { Ack, ClientMessage, Command, Guest, ServerMessage, Snapshot } from "../src/protocol";
import type { MoveReply, Table } from "../src/portal/api";
import { fitsPhone, observeMotion, projected, receive, state, tap, urls } from "./portal-support";
import type { MotionPage, Point } from "./portal-support";

const boardGames = [
  ["chess", "Chess"], ["go", "Go"], ["reversi", "Reversi"],
  ["checkers", "Checkers"], ["chinese-checkers", "Chinese Checkers"],
] as const;

const squareAt = (board: Locator, row: number, column: number) => board.locator(`[data-row="${row}"][data-column="${column}"]`);
const cellById = (board: Locator, id: string) => board.locator(`[data-cell=${JSON.stringify(id)}]`);
const boardOn = (page: Page, name: string) => page.getByRole("group", { name: `${name} board`, exact: true });

type ClipboardPage = typeof window & { clipboardWrites: string[] };

async function observeClipboard(page: Page) {
  await page.addInitScript(() => {
    const writes: string[] = [];
    (window as ClipboardPage).clipboardWrites = writes;
    Object.defineProperty(navigator, "clipboard", { value: { writeText: async (value: string) => { writes.push(value); } } });
  });
  return () => page.evaluate(() => (window as ClipboardPage).clipboardWrites);
}

async function boardCenters(board: Locator) {
  return board.evaluate(node => {
    const origin = node.getBoundingClientRect();
    return Object.fromEntries([...node.querySelectorAll<HTMLElement>("[data-cell]")].map(cell => {
      const { x, y, width, height } = cell.getBoundingClientRect();
      return [cell.dataset.cell!, [x - origin.x + width / 2, y - origin.y + height / 2] as Point];
    }));
  });
}

function expectPoint(actual: Point, expected: Point) {
  expect(Math.abs(actual[0] - expected[0])).toBeLessThan(.7);
  expect(Math.abs(actual[1] - expected[1])).toBeLessThan(.7);
}

type AudioProbe = Window & {
  audioSignal: () => { running: boolean; rms: number; peak: number; taps: number; breaths: number; rooms: number };
};

async function observeAudio(page: Page) {
  await page.addInitScript(() => {
    const taps: AnalyserNode[] = [];
    const echoes = new Set<DelayNode>();
    let breaths = 0;
    const start = AudioBufferSourceNode.prototype.start;
    AudioBufferSourceNode.prototype.start = function (...args: Parameters<typeof start>) {
      if (this.loop) breaths++;
      start.apply(this, args);
    };
    const connect = AudioNode.prototype.connect;
    AudioNode.prototype.connect = function (this: AudioNode, destination: AudioNode | AudioParam, ...args: number[]) {
      if (this instanceof DelayNode) echoes.add(this);
      if (destination instanceof AudioDestinationNode) {
        const tap = this.context.createAnalyser();
        taps.push(tap);
        Reflect.apply(connect, this, [tap, ...args]);
        Reflect.apply(connect, tap, [destination]);
        return destination;
      }
      return Reflect.apply(connect, this, [destination, ...args]);
    } as typeof AudioNode.prototype.connect;
    const disconnect = AudioNode.prototype.disconnect;
    AudioNode.prototype.disconnect = function (this: AudioNode, ...args: unknown[]) {
      Reflect.apply(disconnect, this, args);
      if (this instanceof DelayNode) echoes.delete(this);
    } as typeof AudioNode.prototype.disconnect;
    (window as unknown as AudioProbe).audioSignal = () => {
      let rms = 0, peak = 0;
      for (const tap of taps) {
        const samples = new Float32Array(tap.fftSize);
        tap.getFloatTimeDomainData(samples);
        rms = Math.max(rms, Math.sqrt(samples.reduce((sum, i) => sum + i * i, 0) / samples.length));
        peak = Math.max(peak, ...samples.map(Math.abs));
      }
      return { running: taps.some(tap => tap.context.state === "running"), rms, peak, taps: taps.length, breaths, rooms: echoes.size };
    };
  });
  return () => page.evaluate(() => (window as unknown as AudioProbe).audioSignal());
}

async function audible(signal: Awaited<ReturnType<typeof observeAudio>>, floor = .028) {
  await expect.poll(async () => {
    const { running, rms, peak } = await signal();
    return running && rms > floor && peak < .65;
  }).toBe(true);
}

async function simulateVisibility(page: Page, hidden: boolean) {
  await page.evaluate(hidden => {
    Object.defineProperty(document, "hidden", { configurable: true, get: () => hidden });
    document.dispatchEvent(new Event("visibilitychange"));
  }, hidden);
}

async function seedPreferences(request: APIRequestContext, base: string, volume = .7): Promise<Guest> {
  const headers = { Origin: new URL(base).origin };
  const guest = await request.post(new URL("api/guest", base).href, { headers, data: { displayName: "Bamboo" } });
  expect(guest.ok()).toBe(true);
  const profile = await request.patch(new URL("api/profile", base).href,
    { headers, data: { preferences: { sound: false, music: false, volume, reducedMotion: true } } });
  expect(profile.ok()).toBe(true);
  return profile.json();
}

test.describe("mounted Jade Court portal", () => {
  test.beforeEach(({ baseURL }) => {
    test.skip(!baseURL || !/^\/portal\/?$/.test(new URL(baseURL).pathname), "Requires the published runtime with the portal mounted at /portal/.");
  });

  test("the phone catalog opens each board setup and keeps its canonical portal route", async ({ page, baseURL, request }, info) => {
    const url = urls(baseURL!);
    await page.setViewportSize({ width: 320, height: 730 });
    await page.goto(url());
    await fitsPhone(page);
    const music = page.getByRole("button", { name: "Music off", exact: true });
    await expect(music).toBeInViewport();
    expect((await music.boundingBox())!.height).toBeGreaterThanOrEqual(44);
    await page.keyboard.press("Tab");
    await expect(page.getByRole("link", { name: "Skip to content" })).toBeFocused();
    await page.keyboard.press("Enter");
    await expect(page.getByRole("main")).toBeFocused();

    for (const [game, name] of boardGames) {
      const card = page.getByRole("link", { name: `Set up ${name}`, exact: true });
      await expect(card).toHaveAttribute("href", `/portal/${game}/`);
      await card.tap();
      await expect(page).toHaveURL(url(`${game}/`));
      await expect(page).toHaveTitle(`${name} · Jade Court`);
      await expect(page.getByRole("heading", { name, exact: true })).toBeVisible();
      const play = page.getByRole("button", { name: "Play a bot", exact: true });
      await expect(play).toBeEnabled();
      await expect(page.getByRole("button", { name: "Invite a friend", exact: true })).toBeEnabled();
      expect((await play.boundingBox())!.height).toBeGreaterThanOrEqual(44);
      await fitsPhone(page);
      if (game === "chess") await page.screenshot({ path: info.outputPath("chess-setup-phone.png"), fullPage: true });
      await page.getByRole("link", { name: "‹ All games", exact: true }).tap();
      await expect(page).toHaveURL(url());
    }
    const canonical = await request.get(url("chess?from=bookmark"), { maxRedirects: 0 });
    expect(canonical.status()).toBe(308);
    expect(canonical.headers().location).toBe("/portal/chess/?from=bookmark");
    expect((await request.get(url("not-a-game/"))).status()).toBe(404);
  });

  for (const [game, name] of boardGames) {
    test(`${name}: places a real bot move, aligns keyboard navigation, and resumes`, async ({ page, baseURL }, info) => {
      const url = urls(baseURL!);
      const motion = await observeMotion(page, game === "go" ? "landing" : "in-flight");
      await page.goto(url(`${game}/`));
      if (game === "go") {
        const size = page.getByRole("combobox", { name: "Variant" });
        await expect(size).toBeEnabled();
        await size.selectOption("19x19");
      }
      if (game === "chinese-checkers") {
        const count = page.getByRole("combobox", { name: "Players" });
        await expect(count).toBeEnabled();
        await count.selectOption("3");
      }
      await page.getByRole("textbox", { name: "Display name" }).fill("Court Guest");
      const created = receive(page, url("api/board/tables"), "POST");
      await tap(page, game === "chinese-checkers" ? "Play with bots" : "Play a bot");
      const initialResponse = await created;
      expect(initialResponse.ok()).toBe(true);
      const initial = await projected(initialResponse);
      expect(initial).toMatchObject({ game, status: "playing", seat: 0 });
      expect(initial.seats).toHaveLength(game === "chinese-checkers" ? 3 : 2);
      expect(initial.seats[0].name).toBe("Court Guest");
      expect(initial.seats.slice(1).every(seat => seat.bot && seat.occupied)).toBe(true);
      expect(initialResponse.request().postDataJSON()).toEqual({
        game, variant: initial.variant, players: initial.seats.length, bots: true,
        ...(game === "chess" ? { botDifficulty: "standard" } : {}),
      });
      if (game === "chess") {
        expect(initial.botDifficulty).toBe("standard");
        await expect(page.getByText("Standard · 2 players · Standard bot", { exact: true })).toBeVisible();
      }
      await expect(state(page)).toHaveText("Your turn");
      const board = boardOn(page, name);
      const position = initial.position!;
      if (game === "go") expect(position.rows).toBe(19);
      if (game === "chinese-checkers") expect([position.rows, position.columns, position.cells.length]).toEqual([17, 25, 121]);
      await expect(board.locator("[data-cell]")).toHaveCount(position.cells.length);
      const firstRow = position.cells.filter(cell => cell.row === (game === "chinese-checkers" ? 4 : 0)).sort((a, b) => a.column - b.column);
      const first = cellById(board, firstRow[0].id);
      const right = cellById(board, firstRow[1].id);
      await first.focus();
      await page.keyboard.press("ArrowRight");
      await expect(right).toBeFocused();
      const [a, b] = [(await first.boundingBox())!, (await right.boundingBox())!];
      expect(b.x).toBeGreaterThan(a.x);
      expect(Math.abs(b.y - a.y)).toBeLessThan(1);
      await page.keyboard.press("ArrowDown");
      await expect(board.locator(`[data-row="${firstRow[0].row + 1}"]:focus`)).toHaveCount(1);

      if (game === "go") {
        await tap(page, "Enlarge board");
        const scroll = page.locator(".board-scroll");
        await expect.poll(() => scroll.evaluate(node => node.scrollWidth > node.clientWidth)).toBe(true);
        await expect(page.getByRole("button", { name: "Fit board" })).toHaveAttribute("aria-pressed", "true");
        await fitsPhone(page);
        await tap(page, "Fit board");
      }
      const cellIdAt = (row: number, column: number) => position.cells.find(cell => cell.row === row && cell.column === column)?.id;
      const move = (game === "chess"
        ? position.moves.find(move => move.from === cellIdAt(6, 4) && move.to === cellIdAt(5, 4))
        : game === "reversi" ? position.moves.find(move => move.id === "r2c3")
        : position.moves.find(move => {
          if (!move.to || position.moves.filter(candidate => candidate.from === move.from && candidate.to === move.to).length !== 1) return false;
          if (game !== "chinese-checkers") return true;
          const a = position.cells.find(cell => cell.id === move.from)!, b = position.cells.find(cell => cell.id === move.to)!;
          return Math.abs(a.row - b.row) > 1 || Math.abs(a.column - b.column) > 2;
        }))!;
      expect(move, `${game} should offer a playable opening`).toBeDefined();
      const played = receive(page, url(`api/board/tables/${initial.id}/moves`), "POST");
      if (game === "chess") {
        await cellById(board, move.from!).focus();
        await page.keyboard.press("Enter");
        await expect(cellById(board, move.to!)).toHaveAttribute("data-target", "");
        await page.keyboard.press("ArrowUp");
        await expect(cellById(board, move.to!)).toBeFocused();
        await page.keyboard.press("Space");
      } else {
        if (move.from) await cellById(board, move.from).tap();
        await expect(cellById(board, move.to!)).toHaveAttribute("data-target", "");
        await cellById(board, move.to!).tap();
      }
      const moveResponse = await played;
      expect(moveResponse.ok()).toBe(true);
      expect(moveResponse.request().postDataJSON()).toEqual({ revision: initial.revision, moveId: move.id });
      const after: MoveReply = await moveResponse.json();
      expect(after.revision).toBeGreaterThan(initial.revision);
      expect(after.position!.cells).not.toEqual(position.cells);
      const steps = after.turnMotion!.steps;
      expect(after.turnMotion!.fromRevision).toBe(initial.revision);
      expect(steps[0].move.id).toBe(move.id);
      expect(steps.length).toBe(initial.seats.length);
      const playedSteps = steps.filter(({ move }) => move.to !== null);
      const effect = move.from === null ? "place" : "travel";
      // WebKit can skip the midpoint callback of a real drop. Verify both native animations and their landings instead.
      await expect.poll(async () => (await motion()).filter(entry =>
        entry.effect === effect && (game === "go" ? entry.finished : entry.sample)).length).toBe(playedSteps.length);
      const visual = await motion();
      const centers = await boardCenters(board);
      const travel = visual.filter(entry => entry.effect === effect);
      if (game === "go") expect(travel.map(({ side, piece }) => [side, piece])).toEqual([["onyx", "stone"], ["ivory", "stone"]]);
      for (const [index, entry] of travel.entries()) {
        const leg = playedSteps[index].move;
        const anchor = centers[leg.from ?? leg.to!];
        expectPoint(entry.start, anchor);
        if (game === "go") {
          expect(entry).toMatchObject({ started: true, finished: true, landing: { opacity: 1 } });
          expect(entry.cancelled).toBeUndefined();
          const [x, y] = entry.path[0];
          expect(Math.abs(x - anchor[0])).toBeLessThan(.7);
          expect(y).toBeLessThan(anchor[1] - .7);
          expectPoint(entry.path.at(-1)!, anchor);
          expectPoint(entry.landing!.center, anchor);
        } else {
          expect(entry.sample!.center).not.toEqual(entry.start);
          if (leg.from)
            [leg.from, ...leg.via ?? [], leg.to!].forEach((id, i) => expectPoint(entry.path[2 * i], centers[id]));
        }
      }
      if (game === "reversi") {
        const flips = visual.filter(entry => entry.effect === "flip");
        expect(flips.map(entry => entry.side)).toEqual(["ivory", "onyx", "onyx", "ivory"]);
        for (const [index, flip] of flips.entries()) {
          expectPoint(flip.start, centers.r3c3);
          const [start, end] = flip.flipTilt!;
          expect(start).toBeCloseTo(index % 2 === 0 ? 1 : 0);
          expect(end).toBeCloseTo(index % 2 === 0 ? 0 : 1);
        }
        // A busy runner can skip sampling a short half-flip even when it finishes.
        await expect.poll(async () => (await motion()).filter(entry => entry.effect === "flip" && entry.finished).length).toBe(4);
      }
      await expect(board.locator(".motion-layer")).toHaveCount(0);
      const last = after.position!.lastMove!;
      await expect(page.getByText(`Last move: ${last.label}`, { exact: true })).toBeVisible();
      if (last.to) await expect(cellById(board, last.to)).toHaveAttribute("data-last", "");
      const finalTarget = after.position!.cells.find(cell => cell.id === move.to)!;
      await expect(cellById(board, move.to!)).toHaveAccessibleName(finalTarget.piece ? new RegExp(`, .* ${finalTarget.piece}$`) : /, empty$/);
      await fitsPhone(page);
      await page.screenshot({ path: info.outputPath(`${game}-phone.png`), fullPage: true });

      const resumed = receive(page, url("api/board/resume?game=" + game), "GET");
      const resumedTable = receive(page, url(`api/board/tables/${initial.id}`), "GET");
      await page.reload();
      expect(await (await resumed).json()).toEqual({ tableId: initial.id });
      expect(await projected(await resumedTable)).toMatchObject({ id: initial.id, seat: initial.seat, seats: after.seats });
      await expect(board).toBeVisible();
      expect(await motion()).toEqual([]);
      await expect(page.getByText("Court Guest", { exact: true })).toBeVisible();
      if (game === "chess") {
        await page.setViewportSize({ width: 1440, height: 900 });
        await page.screenshot({ path: info.outputPath("chess-desktop.png"), fullPage: true });
      }
    });
  }

  for (const [game, title, leadIn, chainId] of [
    ["checkers", "Checkers", ["r5c2>r4c3", "r5c0>r4c1", "r6c1>r5c2", "r5c6>r4c5"], "r6c7>r4c5>r2c7"],
    ["chinese-checkers", "Chinese Checkers", ["r2c10>r4c8"], "r0c12>r2c10>r4c12"],
  ] as const) {
    test(`${title}: follows a real chain in landing order`, async ({ page, context, baseURL }, info) => {
      const url = urls(baseURL!);
      const motion = await observeMotion(page);
      await page.goto(url(`${game}/`));
      if (game === "chinese-checkers") await page.getByRole("combobox", { name: "Players" }).selectOption("3");
      const created = receive(page, url("api/board/tables"), "POST");
      await tap(page, game === "checkers" ? "Play a bot" : "Play with bots");
      let table = await projected(await created);
      const endpoint = url(`api/board/tables/${table.id}/moves`);
      for (const moveId of leadIn) {
        expect(table.position!.moves.map(move => move.id)).toContain(moveId);
        const reply = await context.request.post(endpoint, {
          headers: { Origin: new URL(endpoint).origin }, data: { revision: table.revision, moveId },
        });
        expect(reply.ok()).toBe(true);
        table = await reply.json();
      }
      await page.reload();
      const board = boardOn(page, title);
      await expect(page.getByText(`Last move: ${table.position!.lastMove!.label}`, { exact: true })).toBeVisible();
      const chain = table.position!.moves.find(move => move.id === chainId)!;
      expect(chain.via).toHaveLength(1);
      const centers = await boardCenters(board);
      await page.evaluate(() => { (window as unknown as MotionPage).holdMotion = true; });
      const played = receive(page, endpoint, "POST");
      await cellById(board, chain.from!).tap();
      await cellById(board, chain.to!).tap();
      const response = await played;
      expect(response.ok()).toBe(true);
      const reply: MoveReply = await response.json();
      expect(reply.turnMotion!.steps[0].move).toEqual(chain);
      await expect.poll(async () => (await motion()).some(entry => entry.effect === "travel" && entry.sample)).toBe(true);
      const travel = (await motion()).find(entry => entry.effect === "travel")!;
      [chain.from!, ...chain.via!, chain.to!].forEach((id, i) => expectPoint(travel.path[2 * i], centers[id]));
      expect(travel.path[1][1]).toBeLessThan((travel.path[0][1] + travel.path[2][1]) / 2);
      expect(travel.path[3][1]).toBeLessThan((travel.path[2][1] + travel.path[4][1]) / 2);
      if (game === "checkers") {
        const captured = (await motion()).filter(entry => entry.effect === "capture");
        expect(captured).toHaveLength(2);
        expect(captured[0].delay).not.toBe(captured[1].delay);
        const removed = reply.turnMotion!.steps[0].changes.filter(cell => cell.owner === null && cell.id !== chain.from);
        const points = removed.map(cell => centers[cell.id]).sort();
        captured.map(entry => entry.start).sort().forEach((center, i) => expectPoint(center, points[i]));
      }
      const layer = board.locator(".motion-layer");
      await expect(layer).toHaveAttribute("aria-hidden", "true");
      await expect(layer).toHaveCSS("pointer-events", "none");
      await expect(cellById(board, chain.to!)).toHaveAccessibleName(/, (jade peg|red man)$/);
      await page.screenshot({ path: info.outputPath(`${game}-active-hop.png`), fullPage: true });

      if (game === "chinese-checkers") await page.setViewportSize({ width: 660, height: 314 });
      // Real input cancels the paused visual copy without disabling final-board controls.
      const next = reply.position!.moves.find(move => move.from)!;
      await cellById(board, next.from!).tap();
      await expect(layer).toHaveCount(0);
      expect((await motion()).some(entry => entry.effect === "travel" && entry.cancelled)).toBe(true);
      await expect(cellById(board, next.to!)).toHaveAttribute("data-target", "");
    });
  }

  test("movement respects saved and newly toggled system preferences", async ({ page, context, baseURL }) => {
    const url = urls(baseURL!);
    await seedPreferences(context.request, url());
    const motion = await observeMotion(page);
    await page.goto(url("reversi/"));
    const created = receive(page, url("api/board/tables"), "POST");
    await tap(page, "Play a bot");
    let table = await projected(await created);
    const endpoint = url(`api/board/tables/${table.id}/moves`);
    const board = boardOn(page, "Reversi");
    async function place() {
      const pending = receive(page, endpoint, "POST");
      await cellById(board, table.position!.moves[0].to!).tap();
      const result = await pending;
      expect(result.ok()).toBe(true);
      table = await projected(result);
      await expect(page.getByText(`Last move: ${table.position!.lastMove!.label}`, { exact: true })).toBeVisible();
    }
    await place();
    expect(await motion()).toEqual([]);
    const profile = await context.request.patch(url("api/profile"), {
      headers: { Origin: new URL(url()).origin },
      data: { preferences: { sound: false, music: false, volume: .7, reducedMotion: false } },
    });
    expect(profile.ok()).toBe(true);
    await page.reload();
    await expect(state(page)).toHaveText("Your turn");
    await page.emulateMedia({ reducedMotion: "reduce" });
    await place();
    expect(await motion()).toEqual([]);

    await page.emulateMedia({ reducedMotion: "no-preference" });
    await expect.poll(() => page.evaluate(() => matchMedia("(prefers-reduced-motion: reduce)").matches)).toBe(false);
    await page.evaluate(() => { (window as unknown as MotionPage).holdMotion = true; });
    await place();
    await expect.poll(async () => (await motion()).some(entry => entry.sample)).toBe(true);
    await page.emulateMedia({ reducedMotion: "reduce" });
    await expect(board.locator(".motion-layer")).toHaveCount(0);
    expect((await motion()).some(entry => entry.cancelled)).toBe(true);
  });

  test("a stale move refreshes the real board without replaying the choice", async ({ page, context, baseURL }) => {
    const url = urls(baseURL!);
    const motion = await observeMotion(page);
    await page.goto(url("reversi/"));
    const created = receive(page, url("api/board/tables"), "POST");
    await tap(page, "Play a bot");
    const initial = await projected(await created);
    await expect(state(page)).toHaveText("Your turn");
    const [attempted, other] = initial.position!.moves;
    const endpoint = url(`api/board/tables/${initial.id}/moves`);
    const movesSent: string[] = [];
    let lastMove = "";
    await page.route(endpoint, async route => {
      movesSent.push(route.request().postDataJSON().moveId);
      if (movesSent.length === 1) {
        const changed = await context.request.post(endpoint, {
          headers: { Origin: new URL(endpoint).origin },
          data: { revision: initial.revision, moveId: other.id },
        });
        expect(changed.ok()).toBe(true);
        lastMove = ((await changed.json()) as Table).position!.lastMove!.label;
      }
      await route.continue();
    });
    const rejected = receive(page, endpoint, "POST");
    await cellById(boardOn(page, "Reversi"), attempted.to!).tap();
    expect((await rejected).status()).toBe(409);
    await expect(page.getByText(/The table changed\. The board is up to date/)).toBeVisible();
    await expect(page.getByText(`Last move: ${lastMove}`, { exact: true })).toBeVisible();
    expect(movesSent).toEqual([attempted.id]);
    expect(await motion()).toEqual([]);
  });

  test("Chess settles skipped revisions, then glides an accepted capture", async ({ page, context, browser, baseURL }) => {
    const url = urls(baseURL!);
    const motion = await observeMotion(page);
    await page.goto(url("chess/"));
    const created = receive(page, url("api/board/tables"), "POST");
    await tap(page, "Invite a friend");
    const waiting = await projected(await created);
    const tableUrl = url(`api/board/tables/${waiting.id}`);
    const headers = { Origin: new URL(url()).origin };
    const peer = await browser.newContext();
    let unblock: () => void = () => {};
    try {
      expect((await peer.request.post(url("api/guest"), { headers, data: {} })).ok()).toBe(true);
      const joined = await peer.request.post(url("api/board/join"), { headers, data: { invite: waiting.invite } });
      expect(joined.ok()).toBe(true);
      let table: Table = await joined.json();
      await expect(state(page)).toHaveText("Your turn");
      const gate = new Promise<void>(resolve => { unblock = resolve; });
      await page.route(tableUrl, async route => {
        await gate;
        await route.continue();
      });
      for (const [guest, moveId] of [[context, "e2e4"], [peer, "d7d5"]] as const) {
        const accepted = await guest.request.post(`${tableUrl}/moves`, {
          headers, data: { revision: table.revision, moveId },
        });
        expect(accepted.ok()).toBe(true);
        table = await accepted.json();
      }
      unblock();
      await expect(page.getByText(`Last move: ${table.position!.lastMove!.label}`, { exact: true })).toBeVisible();
      expect(await motion()).toEqual([]);
      const board = boardOn(page, "Chess");
      const centers = await boardCenters(board);
      const result = receive(page, `${tableUrl}/moves`, "POST");
      await cellById(board, "e4").tap();
      await cellById(board, "d5").tap();
      expect((await result).ok()).toBe(true);
      await expect.poll(async () => (await motion()).filter(entry => entry.sample).length).toBe(2);
      const [mover, captured] = await motion();
      expect(mover).toMatchObject({ effect: "travel", side: "ivory", piece: "pawn" });
      expectPoint(mover.start, centers.e4);
      expectPoint(mover.path.at(-1)!, centers.d5);
      expect(captured).toMatchObject({ effect: "capture", side: "onyx", piece: "pawn" });
      expectPoint(captured.start, centers.d5);
      expect(captured.sample!.opacity).toBeLessThan(1);
      await expect(board.locator(".motion-layer")).toHaveCount(0);
      await expect(cellById(board, "d5")).toHaveAccessibleName(/white pawn$/);
      await expect(cellById(board, "e4")).toHaveAccessibleName(/empty$/);
    } finally {
      unblock();
      await peer.close();
    }
  });

  test("private Go invite joins a second guest, captures, reconnects, and completes by passing", async ({ page, context, browser, baseURL }, info) => {
    const url = urls(baseURL!);
    const copied = await observeClipboard(page);
    const hostMotion = await observeMotion(page);
    await page.goto(url("go/"));
    await page.getByRole("textbox", { name: "Display name" }).fill("Opal");
    const created = receive(page, url("api/board/tables"), "POST");
    await tap(page, "Invite a friend");
    const waiting = await projected(await created);
    expect(waiting).toMatchObject({ status: "waiting", position: null });
    await expect(state(page)).toHaveText("Waiting for players");
    await expect(page.getByText("1 of 2 seats filled")).toBeVisible();
    const invite = new URL(await page.getByRole("textbox", { name: "Invite link" }).inputValue());
    expect(invite.origin + invite.pathname).toBe(url("go/"));
    expect(invite.search + invite.username + invite.password).toBe("");
    expect(invite.hash).toMatch(/^#invite=[a-f\d]{32}$/);
    expect(await copied()).toEqual([]);
    await tap(page, "Copy invite link");
    await expect(page.getByText("Link copied.", { exact: true })).toBeVisible();
    expect(await copied()).toEqual([invite.href]);
    await page.screenshot({ path: info.outputPath("invite-phone.png"), fullPage: true });

    const friendContext = await browser.newContext({ viewport: { width: 380, height: 780 }, isMobile: true, hasTouch: true });
    try {
      const friend = await friendContext.newPage();
      const friendMotion = await observeMotion(friend);
      const requests: string[] = [];
      friend.on("request", request => requests.push(request.url()));
      await friend.goto(invite.href);
      await expect(friend).toHaveURL(invite.href);
      await expect(friend.getByRole("heading", { name: "A friend saved you a seat." })).toBeVisible();
      const skip = friend.getByRole("link", { name: "Skip to content" });
      await skip.focus();
      await expect(skip).toBeFocused();
      await friend.keyboard.press("Enter");
      await expect(friend).toHaveURL(invite.href);
      await friend.getByRole("textbox", { name: "Display name" }).fill("Pearl");
      const joined = receive(friend, url("api/board/join"), "POST");
      await tap(friend, "Join table");
      const table = await projected(await joined);
      expect(table).toMatchObject({ id: waiting.id, status: "playing", seat: 1, invite: null });
      await expect(friend).toHaveURL(url("go/"));
      expect(requests.every(request => !request.includes(waiting.invite!))).toBe(true);
      await expect(state(page)).toHaveText("Your turn");
      await expect(page.getByText("Two consecutive passes end the game. Play out unsettled groups before passing.")).toBeVisible();
      await expect(state(friend)).toHaveText("Opal’s turn");
      await expect(page.getByRole("textbox", { name: "Invite link" })).toHaveCount(0);
      await expect(page.getByText("2 of 2 seats filled")).toBeVisible();
      expect(await hostMotion()).toEqual([]);
      expect(await friendMotion()).toEqual([]);

      const endpoint = url(`api/board/tables/${table.id}/moves`);
      const hostBoard = boardOn(page, "Go");
      const friendBoard = boardOn(friend, "Go");
      async function take(guest: Page, action: () => Promise<void>) {
        await expect(state(guest)).toHaveText("Your turn");
        const move = receive(guest, endpoint, "POST");
        await action();
        const response = await move;
        expect(response.ok()).toBe(true);
        return projected(response);
      }

      await take(page, () => squareAt(hostBoard, 0, 1).tap());
      await expect.poll(async () => (await friendMotion()).filter(entry => entry.effect === "place" && entry.sample).length).toBe(1);
      expectPoint((await friendMotion())[0].start, (await boardCenters(friendBoard)).r0c1);
      await simulateVisibility(page, true);
      await take(friend, () => squareAt(friendBoard, 0, 0).tap());
      await simulateVisibility(page, false);
      await expect(squareAt(hostBoard, 0, 0)).toHaveAccessibleName(/white stone$/);
      expect((await hostMotion()).map(entry => entry.side)).toEqual(["onyx"]);
      await context.setOffline(true);
      await expect(state(page)).toHaveText("Reconnecting…");
      await context.setOffline(false);
      await expect(state(page)).toHaveText("Your turn");
      const captured = await take(page, () => squareAt(hostBoard, 1, 0).tap());
      expect(captured.position!.cells.find(cell => cell.row === 0 && cell.column === 0)?.owner).toBeNull();
      await expect(squareAt(friendBoard, 0, 0)).toHaveAccessibleName(/empty$/);
      for (const [view, trace] of [[hostBoard, hostMotion], [friendBoard, friendMotion]] as const) {
        await expect.poll(async () => (await trace()).some(entry => entry.effect === "capture" && entry.sample)).toBe(true);
        const ghost = (await trace()).find(entry => entry.effect === "capture")!;
        expect(ghost.side).toBe("ivory");
        expectPoint(ghost.start, (await boardCenters(view)).r0c0);
        expect(ghost.sample!.opacity).toBeLessThan(1);
        await expect(view.locator(".motion-layer")).toHaveCount(0);
      }
      const beforePasses = [(await hostMotion()).length, (await friendMotion()).length];
      await take(friend, () => tap(friend, "Pass"));
      const finished = await take(page, () => tap(page, "Pass"));
      expect(finished).toMatchObject({ status: "finished", position: { turn: null, scores: expect.any(Array) } });
      await expect(page.getByText("Game over", { exact: true })).toBeVisible();
      await expect(friend.getByText("Game over", { exact: true })).toBeVisible();
      expect([(await hostMotion()).length, (await friendMotion()).length]).toEqual(beforePasses);
      await expect(state(page)).toHaveText(/won|wins|Draw/);
      await expect(page.getByText(/ pts$/)).toHaveCount(2);
      await page.screenshot({ path: info.outputPath("finished-go-phone.png"), fullPage: true });
      await tap(page, "Set up another game");
      await expect(page.getByRole("textbox", { name: "Display name" })).toHaveValue("Opal");
    } finally {
      await friendContext.close();
    }
  });

  test("portal music plays on gesture, survives reload, mutes while hidden, and saves without changing other preferences",
    async ({ page, context, baseURL }, info) => {
      const url = urls(baseURL!);
      const guest = await seedPreferences(context.request, url(), .64);
      const signal = await observeAudio(page);
      const calls: string[] = [];
      page.on("request", event => calls.push(new URL(event.url()).pathname));
      await page.goto(url());
      await expect(page.getByRole("button", { name: "Music off", exact: true })).toBeVisible();
      expect(calls.filter(path => path === "/portal/api/guest" || path.includes("/assets/ambient-"))).toEqual([]);
      expect(await signal()).toMatchObject({ running: false, rms: 0 });

      const saved = receive(page, url("api/profile"), "PATCH");
      await tap(page, "Music off");
      const save = await saved;
      expect(save.ok()).toBe(true);
      expect(await save.json()).toMatchObject({
        displayName: guest.displayName, preferences: { ...guest.preferences, music: true },
      });
      expect(calls.filter(path => path === "/portal/api/guest")).toHaveLength(1);
      await audible(signal);
      await expect(page.getByRole("button", { name: "Music on", exact: true })).toHaveAttribute("aria-pressed", "true");
      await page.setViewportSize({ width: 660, height: 314 });
      await page.screenshot({ path: info.outputPath("lobby-music-short-landscape.png") });
      await simulateVisibility(page, true);
      await expect.poll(async () => (await signal()).rms).toBeLessThan(.001);
      await simulateVisibility(page, false);
      await audible(signal);

      const game = receive(page, url("api/guest"), "POST");
      await page.goto(url("chess/"));
      expect(await (await game).json()).toMatchObject({ preferences: { music: true } });
      const opener = page.getByRole("button", { name: "Play music", exact: true });
      await expect(opener).toBeVisible();
      expect(await signal()).toMatchObject({ running: false, rms: 0 });
      await opener.focus();
      await page.keyboard.press("Enter");
      await audible(signal);

      const latest = { ...guest.preferences, music: true, sound: true, volume: .52, reducedMotion: false };
      expect((await context.request.patch(url("api/profile"), {
        headers: { Origin: new URL(url()).origin }, data: { displayName: "Willow", preferences: latest },
      })).ok()).toBe(true);
      const muted = receive(page, url("api/profile"), "PATCH");
      await tap(page, "Music on");
      expect(await (await muted).json()).toMatchObject({
        displayName: "Willow", preferences: { ...latest, music: false },
      });
      await expect.poll(async () => (await signal()).rms).toBeLessThan(.001);
      await page.reload();
      await expect(page.getByRole("button", { name: "Music off", exact: true })).toBeVisible();
      expect(await signal()).toMatchObject({ running: false, rms: 0 });

      await seedPreferences(context.request, url(), 0);
      await page.goto(url());
      await tap(page, "Music off");
      await expect(page.getByText("Music is muted at 0% volume.")).toBeVisible();
      expect(await signal()).toMatchObject({ rms: 0, taps: 0 });
    });

  test("Mahjong previews actual music before Save, cancels drafts, and plays saved music only on gesture",
    async ({ page, context, baseURL }, info) => {
      const url = urls(baseURL!);
      const guest = await seedPreferences(context.request, url());
      const signal = await observeAudio(page);
      const optional: string[] = [];
      page.on("request", request => {
        if (/\/(?:ambient|bamboo|callouts|table-sounds)-|\.(mp3|wav)$/.test(new URL(request.url()).pathname))
          optional.push(request.url());
      });
      const patches = () => page.evaluate(() => performance.getEntriesByName(location.origin + "/portal/api/profile").length);
      await page.goto(url("mahjong/"));
      await expect(page.locator(".connection")).toHaveAttribute("data-status", "online");
      const settings = page.getByRole("button", { name: "Settings", exact: true });
      const dialog = page.getByRole("dialog", { name: "Settings" });
      const music = dialog.getByLabel("Quiet table music", { exact: true });
      const track = dialog.getByRole("combobox", { name: "Soundtrack", exact: true });
      const bamboo = async (prior = 0) => {
        await expect.poll(async () => (await signal()).breaths).toBeGreaterThan(prior);
        await audible(signal, .016);
      };
      await settings.tap();
      await expect(music).not.toBeChecked();
      await expect(track).toHaveValue("still");
      expect(optional).toEqual([]);
      expect(await signal()).toMatchObject({ rms: 0, breaths: 0 });
      await music.check();
      await audible(signal);
      const still = await signal();
      expect(still.breaths).toBe(0);
      expect(optional.every(asset => !asset.includes("/bamboo-"))).toBe(true);
      await track.selectOption({ label: "Bamboo by the Xiang" });
      await bamboo();
      expect(await signal()).toMatchObject({ taps: still.taps, rooms: 1 });
      expect(await patches()).toBe(0);
      await page.screenshot({ path: info.outputPath("mahjong-music-preview.png") });
      await music.uncheck();
      await expect.poll(async () => (await signal()).rms).toBeLessThan(.001);
      const previousPreview = (await signal()).breaths;
      await music.check();
      await bamboo(previousPreview);
      await dialog.getByRole("button", { name: "Close dialog" }).tap();
      await expect(dialog).toHaveCount(0);
      await expect.poll(async () => (await signal()).rms).toBeLessThan(.001);

      await settings.tap();
      await expect(music).not.toBeChecked();
      await expect(track).toHaveValue("still");
      await track.selectOption("bamboo");
      await music.check();
      const changed = receive(page, url("api/profile"), "PATCH");
      await dialog.getByRole("button", { name: "Save settings" }).tap();
      expect(await (await changed).json()).toMatchObject({
        displayName: guest.displayName, preferences: { ...guest.preferences, music: true },
      });
      await expect(dialog.getByText("Settings saved.")).toBeVisible();
      await bamboo();
      optional.length = 0;
      await page.reload();
      await expect(page.locator(".connection")).toHaveAttribute("data-status", "online");
      expect(await signal()).toMatchObject({ running: false, rms: 0, breaths: 0 });
      expect(optional).toEqual([]);
      await settings.tap();
      await expect(music).toBeChecked();
      await expect(track).toHaveValue("bamboo");
      await bamboo();
      const savedTrack = await signal();
      await track.selectOption("still");
      await audible(signal);
      expect(await signal()).toMatchObject({ breaths: savedTrack.breaths, taps: savedTrack.taps, rooms: 1 });
      await dialog.getByRole("button", { name: "Close dialog" }).tap();
      await bamboo(savedTrack.breaths);
      expect(await signal()).toMatchObject({ taps: savedTrack.taps, rooms: 1 });
      await simulateVisibility(page, true);
      await expect.poll(async () => (await signal()).rms).toBeLessThan(.001);
      const hidden = await signal();
      expect(hidden.rooms).toBe(0);
      await simulateVisibility(page, false);
      await bamboo(hidden.breaths);
      await settings.tap();
      const volume = dialog.getByRole("slider");
      await volume.focus();
      await page.keyboard.press("Home");
      await expect(volume).toHaveValue("0");
      await expect(dialog.getByText("Music is muted at 0% volume.")).toBeVisible();
      await expect.poll(async () => (await signal()).rms).toBeLessThan(.001);
      await dialog.getByRole("button", { name: "Close dialog" }).tap();
      await audible(signal, .016);
    });

  test("the Mahjong child keeps its cold flagship assets, socket mount, return link and canonical invite", async ({ page, baseURL, request }) => {
    const url = urls(baseURL!);
    const copied = await observeClipboard(page);
    const assets = new Set<string>();
    const sockets = new Set<string>();
    page.on("request", request => {
      if (new URL(request.url()).pathname.startsWith("/portal/assets/")) assets.add(request.url());
    });
    page.on("websocket", socket => sockets.add(new URL(socket.url()).pathname));
    const html = (await page.goto(url("mahjong/")))!;
    await expect(page).toHaveTitle("Jade Court · Changsha Mahjong");
    await expect(page.locator("#app")).toHaveAttribute("data-base", "/portal/");
    await expect(page.locator(".connection")).toHaveAttribute("data-status", "online");
    await expect(page.getByRole("link", { name: "Back to all games in Jade Court" })).toHaveAttribute("href", "/portal/");
    expect([...sockets]).toEqual(["/portal/ws"]);
    const source = await html.body();
    expect(source.toString()).not.toContain("modulepreload");
    expect(assets.size).toBe(3);
    expect([...assets].map(asset => new URL(asset).pathname)).toEqual(expect.arrayContaining([
      expect.stringMatching(/\/index-[\w-]+\.js$/), expect.stringMatching(/\/index-[\w-]+\.css$/),
      expect.stringMatching(/\/jade-court-[\w-]+\.svg$/),
    ]));
    expect([...assets].filter(asset =>
      /\/(?:portal|actors|ambient|bamboo|callouts|table-sounds|settings|court-study|hall-dragon|holdem|solitaire|sudoku|words|multiplayer|solo|cards|shared|room)-|\.mp3$/.test(new URL(asset).pathname))).toEqual([]);
    const compressed = await Promise.all([...assets].map(async asset => {
      const response = await request.get(asset);
      expect(response.ok()).toBe(true);
      return gzipSync(await response.body()).length;
    }));
    expect(gzipSync(source).length + compressed.reduce((total, size) => total + size, 0)).toBeLessThanOrEqual(32 * 1024);

    await tap(page, "Play with friends");
    await page.getByRole("dialog", { name: "Create a table" }).getByRole("button", { name: "Create table" }).tap();
    await expect(page.getByRole("heading", { name: "Take a seat." })).toBeVisible();
    await tap(page, "Invite friends");
    expect(await copied()).toEqual([]);
    await tap(page, "Copy invite link");
    const [invitation] = await copied();
    const link = new URL(invitation);
    expect(link.origin + link.pathname + link.search).toBe(url("mahjong/"));
    expect(link.hash).toMatch(/^#invite=[\w-]{16}$/);
  });

  test("a resumed Mahjong bot host can cancel, start a new game in place, or quit without a saved seat", async ({ page, baseURL }) => {
    const url = urls(baseURL!);
    const commands: Command[] = [];
    const frames: (Ack | Snapshot)[] = [];
    let sendStale: (() => void) | undefined;
    let deliverDeal: (() => void) | undefined;
    let deliverQuit: (() => void) | undefined;
    let restartAck: Ack | undefined;
    let droppedQuit = false;
    await page.routeWebSocket("**/ws", route => {
      const server = route.connectToServer();
      route.onMessage(payload => {
        const message = JSON.parse(payload.toString()) as ClientMessage;
        if (message.type === "command") {
          commands.push(message);
          if (message.op === "restartMatch" && !sendStale) {
            sendStale = () => server.send(payload);
            return;
          }
        }
        server.send(payload);
      });
      server.onMessage(payload => {
        const message = JSON.parse(payload.toString()) as ServerMessage;
        if (message.type === "ack" || message.type === "snapshot") frames.push(message);
        if (message.type === "ack" && message.status === "ok") {
          const command = commands.find(command => command.requestId === message.requestId);
          if (command?.op === "restartMatch") restartAck = message;
          if (command?.op === "leaveRoom" && command.payload.abandon) {
            if (!droppedQuit) {
              droppedQuit = true;
              route.close({ code: 1012, reason: "test lost quit ack" });
            } else deliverQuit = () => route.send(payload);
            return;
          }
        }
        if (!deliverDeal && message.type === "snapshot" && restartAck?.gameRevision === message.gameRevision) {
          deliverDeal = () => route.send(payload);
          return;
        }
        route.send(payload);
      });
    });
    const snapshots = () => frames.filter((frame): frame is Snapshot => frame.type === "snapshot");
    const latest = () => snapshots().at(-1)!;
    const tools = page.locator(".turnbar").getByRole("button", { name: "Table options" });
    const table = page.getByRole("region", { name: "Mahjong table" });
    const dialog = (name: string) => page.getByRole("dialog", { name, exact: true });

    await page.setViewportSize({ width: 320, height: 730 });
    await page.goto(url("mahjong/"));
    await tap(page, "Play with bots");
    await tap(page, "Mark ready");
    await tap(page, "Start match");
    await tap(page, "Pause");
    await expect(page.getByText("Table paused", { exact: true })).toBeVisible();
    const paused = latest();
    expect(paused.room.seats.map(seat => seat.kind)).toEqual(["human", "bot", "bot", "bot"]);

    await tap(page, "Settings");
    await expect(dialog("Settings").getByText(/Your seat stays reserved/)).toBeVisible();
    await tap(page, "Back to lobby");
    await expect(page.getByRole("button", { name: "Resume your table" })).toBeEnabled();
    expect(commands.at(-1)?.op).toBe("leaveRoom");
    expect(commands.at(-1)?.payload).toEqual({ roomId: paused.roomId, expectedRoomRevision: paused.roomRevision });
    await page.reload();
    await expect(table).toHaveCount(0);
    await tap(page, "Resume your table");
    await expect(page.getByText("Table paused", { exact: true })).toBeVisible();
    expect(latest()).toMatchObject({ roomId: paused.roomId, gameRevision: paused.gameRevision,
      game: { window: { windowId: paused.game!.window!.windowId } } });

    await fitsPhone(page);
    await expect(tools).toBeInViewport({ ratio: 1 });
    const { width, height } = (await tools.boundingBox())!;
    expect(Math.min(width, height)).toBeGreaterThanOrEqual(44);
    await page.setViewportSize({ width: 667, height: 360 });
    await fitsPhone(page);
    await expect(tools).toBeInViewport({ ratio: 1 });
    await expect(page.getByRole("timer")).toHaveAttribute("aria-label", /^Auto-resume in \d+ seconds$/);
    expect(await page.locator(".turnbar").evaluate(bar =>
      [...bar.querySelectorAll(".turn-tools > :not([hidden])")].every(control =>
        control.getBoundingClientRect().right <= bar.getBoundingClientRect().right))).toBe(true);
    await page.setViewportSize({ width: 320, height: 730 });
    const count = commands.length;
    await tools.focus();
    await page.keyboard.press("Enter");
    const newGame = dialog("Table options").getByRole("button", { name: "New game", exact: true });
    await newGame.tap();
    const restart = dialog("New game?");
    const start = restart.getByRole("button", { name: "Start new game", exact: true });
    await expect(restart.getByText(/clears its scores/)).toBeVisible();
    await expect(restart.getByRole("button", { name: "Cancel" })).toBeFocused();
    await restart.getByRole("button", { name: "Cancel" }).tap();
    await expect(newGame).toBeFocused();
    await page.keyboard.press("Escape");
    await expect(tools).toBeFocused();
    expect(commands.length).toBe(count);
    expect(latest().game?.window?.windowId).toBe(paused.game!.window!.windowId);

    await tools.tap();
    await newGame.tap();
    await start.tap();
    await expect.poll(() => !!sendStale).toBe(true);
    const rename = await page.request.patch(url("api/profile"), {
      headers: { Origin: new URL(url()).origin }, data: { displayName: "Jade Host" },
    });
    expect(rename.ok()).toBe(true);
    await expect.poll(() => latest().roomRevision).toBe(paused.roomRevision + 1);
    sendStale!();
    await expect(restart.getByText(/The room changed while you were acting/)).toBeVisible();
    await expect(start).toBeEnabled();
    const before = latest();
    await start.tap();
    await expect.poll(() => !!deliverDeal).toBe(true);
    await expect(start).toBeDisabled();
    await expect(restart.getByRole("button", { name: "Cancel" })).toBeDisabled();
    await expect(page.getByText("Table paused", { exact: true })).toBeVisible();
    const firstDeal = latest();
    const restartCommand = commands.at(-1)!;
    expect(restartCommand.payload).toEqual({ roomId: before.roomId, expectedRoomRevision: before.roomRevision });
    expect(firstDeal).toMatchObject({ roomId: before.roomId, roomRevision: before.roomRevision + 1,
      gameRevision: before.gameRevision + 1, room: { status: "playing", paused: false, autoResumeAtUtc: null,
        matchHands: before.room.matchHands, seats: before.room.seats },
      game: { handNumber: 1, dealerSeat: 0, settlement: null } });
    expect(firstDeal.game!.window!.windowId).not.toBe(before.game!.window!.windowId);
    expect(firstDeal.game!.seats.map(seat => seat.total)).toEqual([0, 0, 0, 0]);
    expect(frames.indexOf(restartAck!)).toBeLessThan(frames.indexOf(firstDeal));
    deliverDeal!();
    await expect(restart).toHaveCount(0);
    await expect(tools).toBeFocused();
    await expect(table.locator(".table-caption")).toHaveText("Hand 1 / 1");
    await expect(table.locator(".hand-tile[aria-pressed=true]")).toHaveCount(0);
    await expect(page.getByRole("heading", { name: "Take a seat." })).toHaveCount(0);

    await tap(page, "Pause");
    await expect(page.getByText("Table paused", { exact: true })).toBeVisible();
    const quitting = latest();
    await tools.tap();
    await dialog("Table options").getByRole("button", { name: "Quit table" }).tap();
    const quit = dialog("Quit table?");
    await expect(quit.getByText(/won’t be able to resume/)).toBeVisible();
    await quit.getByRole("button", { name: "Quit table" }).tap();
    const abandon = { roomId: quitting.roomId, expectedRoomRevision: quitting.roomRevision, abandon: true };
    await expect.poll(() => commands.at(-1)?.payload).toEqual(abandon);
    await expect.poll(() => !!deliverQuit).toBe(true);
    await expect(quit.getByRole("button", { name: "Quit table" })).toBeDisabled();
    expect(commands.at(-2)!.payload).toEqual(abandon);
    // Compare the entire retry without dumping an ephemeral lease into the report.
    expect(JSON.stringify(commands.at(-1)) === JSON.stringify(commands.at(-2))).toBe(true);
    deliverQuit!();
    await expect(page.getByRole("button", { name: "Play with bots" })).toBeEnabled();
    await expect(page.getByRole("button", { name: "Resume your table" })).toHaveCount(0);
    await page.reload();
    await expect(page.getByRole("button", { name: "Resume your table" })).toHaveCount(0);
    await tap(page, "Play with bots");
    await expect(page.getByRole("heading", { name: "Take a seat." })).toBeVisible();
    expect(latest().roomId).not.toBe(paused.roomId);
    await tap(page, "Leave table");
  });
});
