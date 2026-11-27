import { readFileSync } from "node:fs";
import { expect, test } from "@playwright/test";
import type { Page } from "@playwright/test";
import { puzzleIndex, utcEdition } from "../src/portal/daily-edition";
import { mahjongSolitaireProgressKey } from "../src/portal/games/mahjong-solitaire-save";
import { MahjongStack } from "../src/portal/games/mahjong-stack";
import { checkLattice, paintedUses } from "./east-court-support";
import { fitsPhone } from "./portal-support";

const firstDate = new Date("2026-11-25T14:00:00Z");
const currentDay = utcEdition(firstDate);
const corpus = JSON.parse(readFileSync(new URL("../src/portal/games/daily-xiangqi.json", import.meta.url), "utf8")) as { id: string }[];
const dailyUrl = (base: string, hash = "") => new URL(`daily-court/${hash}`, base).href;
const tactic = (page: Page) => page.locator(".daily-table");
const xiangqiBoard = (page: Page) => tactic(page).getByRole("group", { name: /Xiangqi.*board/ });
const point = (page: Page, id: string) => xiangqiBoard(page).locator(`[data-cell="${id}"]`);
const stackBoard = (page: Page) => page.getByRole("group", { name: "Lantern stack tiles" });
const tile = (page: Page, id: number) => stackBoard(page).locator(`[data-tile="${id}"]`);
const tally = (page: Page) => page.locator(".stack-count");
const stackFaces = (page: Page) => stackBoard(page).locator(".tile-art use").evaluateAll(uses =>
  uses.map(use => use.getAttribute("href")!.split("#")[1]));
const dailySave = (mount: string, slug: string) => `${mount}court-daily-${slug}-v1`;
const playRecord = (page: Page) => page.getByRole("region", { name: "Your play record" });
const recordDay = (page: Page, date: string) => playRecord(page).getByRole("listitem")
  .filter({ has: page.locator(`time[datetime="${date}"]`) });
const savedRecord = (page: Page, key: string) => page.evaluate(key => JSON.parse(localStorage.getItem(key) ?? "null"), key);

async function streaks(page: Page, current: number, best: number) {
  const record = playRecord(page);
  for (const [label, count] of [["Current streak", current], ["Best in 30 UTC days", best]] as const) {
    await expect(record.locator(".daily-streaks div").filter({ hasText: label }).locator("dd"))
      .toHaveText(`${count} ${count === 1 ? "day" : "days"}`);
  }
}

async function tryMove(page: Page, from: string, to: string) {
  await point(page, from).tap();
  await expect(point(page, to)).toHaveAttribute("data-target", "");
  await point(page, to).tap();
}

function noRemotePlay(page: Page, base: string) {
  const root = new URL(base);
  const requests: string[] = [];
  const sockets: string[] = [];
  const errors: string[] = [];
  page.on("request", request => {
    const url = new URL(request.url());
    if (request.method() !== "GET" || url.origin !== root.origin || !url.pathname.startsWith(root.pathname) ||
        !(url.pathname.startsWith(`${root.pathname}assets/`) || url.pathname === `${root.pathname}daily-court/`)) {
      requests.push(`${request.method()} ${url.pathname}`);
    }
  });
  page.on("websocket", socket => sockets.push(socket.url()));
  page.on("pageerror", error => errors.push(error.message));
  return async () => {
    expect(requests, "Daily fetches only its local document and static assets").toEqual([]);
    expect(sockets).toEqual([]);
    expect(errors).toEqual([]);
    expect(await page.context().cookies()).toEqual([]);
    expect(new URL(page.url()).search).toBe("");
  };
}

test("UTC edition rotation visits each tactic and each date’s stack has a legal removal witness", () => {
  const lastSecond = utcEdition(new Date("2026-11-26T07:59:59.999+08:00"));
  const midnight = utcEdition(new Date("2026-11-26T08:00:00.000+08:00"));
  expect(lastSecond).toEqual(currentDay);
  expect(lastSecond.nextAt).toBe(Date.parse("2026-11-26T00:00:00Z"));
  expect(midnight.date).toBe("2026-11-26");
  expect(midnight.seed).not.toBe(lastSecond.seed);

  const selected = new Set<string>();
  const deals = new Set<string>();
  for (let offset = 0; offset < corpus.length; offset++) {
    const edition = utcEdition(new Date(Date.UTC(2026, 10, 25 + offset, 7)));
    const puzzle = corpus[puzzleIndex(edition, corpus.length)];
    selected.add(puzzle.id);
    const stack = new MahjongStack(edition.seed);
    deals.add(stack.tiles.map(tile => tile.face).join(","));
    for (const [a, b] of stack.witness) {
      expect(stack.state(a), `${edition.date} tile ${a}`).toBe("free");
      expect(stack.state(b), `${edition.date} tile ${b}`).toBe("free");
      expect(stack.match(a, b), `${edition.date} pair ${a}/${b}`).toBe(true);
    }
    expect(stack.remaining, edition.date).toBe(0);
  }
  expect(selected).toEqual(new Set(corpus.map(puzzle => puzzle.id)));
  expect(deals.size).toBe(corpus.length);
  expect(corpus[puzzleIndex(utcEdition(new Date("2026-11-24T23:59:00Z")), corpus.length)].id)
    .toBe("counterstroke-reflected");
  expect(corpus[puzzleIndex(utcEdition(new Date("2026-12-13T00:00:00Z")), corpus.length)].id).toBe("rank-and-file");
});

test.describe("published Daily Court", () => {
  test.use({ timezoneId: "Pacific/Auckland" });

  test.beforeEach(({ baseURL }) => {
    test.skip(!baseURL || !/^\/(?:game-)?portal\/$/.test(new URL(baseURL).pathname), "Requires either real portal mount.");
  });

  test("the 320px shelf opens the daily tactic, rejects stalemate and accepts both mates", async ({ page, request, baseURL }, info) => {
    const base = baseURL!;
    const mount = new URL(base).pathname;
    const bare = await request.get(new URL("daily-court?from=smoke%2Fredirect", base).href, { maxRedirects: 0 });
    expect(bare.status()).toBe(308);
    expect(bare.headers().location).toBe(`${mount}daily-court/?from=smoke%2Fredirect`);
    for (const path of ["daily-court/private-table/", "api/daily-court"]) {
      expect((await request.get(new URL(path, base).href)).status(), path).toBe(404);
    }
    await page.clock.setFixedTime(firstDate);
    await page.setViewportSize({ width: 320, height: 730 });
    await page.emulateMedia({ reducedMotion: "reduce" });
    const coldRequests: string[] = [];
    page.on("request", request => coldRequests.push(new URL(request.url()).pathname));
    const saves = page.waitForResponse(response => new URL(response.url()).pathname === `${mount}api/saves`);
    await page.goto(base);
    expect(await (await saves).json()).toEqual({ saves: [] });
    const shelf = page.getByRole("region", { name: "Daily Court" });
    const start = shelf.getByRole("link", { name: "Open Daily Court" });
    await expect(start).toHaveAttribute("href", `${mount}daily-court/`);
    await paintedUses(shelf, ".daily-stamp use", 1);
    expect((await start.boundingBox())!.height).toBeGreaterThanOrEqual(44);
    await expect(page.getByText("19 games · No account required")).toBeVisible();
    await fitsPhone(page);
    expect(coldRequests.filter(path => /daily-court|daily-xiangqi|xiangqi-board|solitaire-well/.test(path))).toEqual([]);
    await page.screenshot({ path: info.outputPath("daily-shelf-320.png") });

    const verifyLocal = noRemotePlay(page, base);
    await start.tap();
    await expect(page.locator("time.daily-date")).toHaveText("2026-11-25 UTC");
    const play = page.getByRole("link", { name: "Play Xiangqi" });
    const record = dailySave(mount, "record");
    await expect(play).toHaveAttribute("href", "#xiangqi");
    await expect(page.getByRole("link", { name: "Play Mahjong Solitaire" })).toHaveAttribute("href", "#mahjong-solitaire");
    await expect(page.getByText("Ready to play")).toHaveCount(2);
    await expect(playRecord(page).getByText("This browser only. Not a leaderboard.")).toBeVisible();
    await expect(playRecord(page).getByRole("listitem")).toHaveCount(14);
    expect(await savedRecord(page, record)).toBeNull();
    await play.tap();
    await expect(page).toHaveURL(dailyUrl(base, "#xiangqi"));
    await expect(tactic(page)).toHaveAttribute("data-puzzle", "rank-and-file");
    await expect(page.getByText("Red to move. Checkmate Black in one move.")).toBeVisible();
    await fitsPhone(page);
    await expect(xiangqiBoard(page)).toBeInViewport({ ratio: .99 });
    await checkLattice(xiangqiBoard(page), "xiangqi");
    await paintedUses(xiangqiBoard(page), ".xiangqi-seal use:not(.seal-cut)", 4);
    await page.screenshot({ path: info.outputPath("daily-xiangqi-320.png") });

    await point(page, "b9").focus();
    await page.keyboard.press("Space");
    await expect(point(page, "b8")).toHaveAttribute("data-target", "");
    await page.keyboard.press("ArrowDown");
    await expect(point(page, "b8")).toBeFocused();
    await page.keyboard.press("Enter");
    await expect(tactic(page).getByRole("status")).toHaveText("Legal move, but not checkmate. Try again from the same position.");
    await expect(point(page, "b9")).toHaveAccessibleName("b9, red chariot");
    expect(await page.evaluate(key => localStorage.getItem(key), dailySave(mount, "xiangqi"))).toBeNull();
    expect(await savedRecord(page, record)).toBeNull();

    await tryMove(page, "b9", "b10");
    await expect(tactic(page).getByRole("status")).toHaveText("Checkmate! You completed today’s tactic.");
    expect(await savedRecord(page, record)).toEqual({ "2026-11-25": 1 });
    await expect(page.getByText("Black is in check and has no legal move.")).toBeVisible();
    await expect(point(page, "b10")).toHaveAccessibleName("b10, red chariot");
    await expect(point(page, "e10")).toHaveAccessibleName("e10, black general");
    await expect(xiangqiBoard(page).locator("[data-target], [data-source]")).toHaveCount(0);
    await page.reload();
    await expect(point(page, "b10")).toHaveAccessibleName("b10, red chariot");
    await expect(tactic(page)).toHaveAttribute("data-state", "won");
    await page.getByRole("button", { name: "Replay today’s tactic" }).tap();
    await tryMove(page, "b9", "e9");
    await expect(point(page, "e9")).toHaveAccessibleName("e9, red chariot");
    await expect(tactic(page)).toHaveAttribute("data-state", "won");
    await page.getByRole("link", { name: "‹ Daily Court" }).tap();
    await expect(page.getByText("Completed today")).toHaveCount(1);
    await expect(page.getByText("Ready to play")).toHaveCount(1);
    await expect(recordDay(page, "2026-11-25")).toContainText("Xiangqi cleared. Mahjong Solitaire not cleared.");
    await streaks(page, 0, 0);
    expect(await savedRecord(page, record)).toEqual({ "2026-11-25": 1 });
    await verifyLocal();
  });

  test("the daily stack keeps practice intact, rejects a wrong pair and clears all 78 tiles", async ({ page, baseURL }, info) => {
    const base = baseURL!;
    const mount = new URL(base).pathname;
    const practiceKey = mahjongSolitaireProgressKey(mount);
    const otherMount = mount === "/portal/" ? "/game-portal/" : "/portal/";
    const otherKey = dailySave(otherMount, "mahjong-solitaire");
    const otherRecord = dailySave(otherMount, "record");
    const record = dailySave(mount, "record");
    const practice = new MahjongStack(328603);
    practice.match(...practice.witness[0]);
    await page.addInitScript(({ practiceKey, practice, otherKey, otherRecord }) => {
      if (localStorage.getItem(practiceKey) === null) localStorage.setItem(practiceKey, practice);
      if (localStorage.getItem(otherKey) === null) localStorage.setItem(otherKey, "other mount");
      if (localStorage.getItem(otherRecord) === null) localStorage.setItem(otherRecord, '{"2026-11-25":3}');
    }, { practiceKey, practice: practice.save(), otherKey, otherRecord });
    await page.clock.setFixedTime(firstDate);
    await page.setViewportSize({ width: 320, height: 730 });
    await page.emulateMedia({ reducedMotion: "reduce" });
    const verifyLocal = noRemotePlay(page, base);
    await page.goto(dailyUrl(base, "#mahjong-solitaire"));
    const board = stackBoard(page);
    const feedback = page.locator(".stack-feedback");
    await expect(tally(page)).toHaveText("78 / 78 tiles left");
    await expect(page.getByRole("button", { name: "New game" })).toHaveCount(0);
    await expect(board).toBeInViewport({ ratio: .99 });
    await fitsPhone(page);
    await expect.poll(() => board.evaluate(node => getComputedStyle(node).backgroundImage)).toMatch(/solitaire-well-[\w-]+\.svg/);
    await paintedUses(board, "svg.tile-art use", 78);

    const expected = new MahjongStack(currentDay.seed);
    const faces = expected.tiles.map(tile => `lt-${tile.face}`);
    expect(await stackFaces(page)).toEqual(faces);
    const use = new URL((await board.locator("svg.tile-art use").first().getAttribute("href"))!, base);
    expect(use.origin).toBe(new URL(base).origin);
    expect(use.pathname).toMatch(new RegExp(`^${mount}assets/tile-suits-[\\w-]+\\.svg$`));
    const [first, partner] = expected.witness[0];
    const different = expected.tiles.find(item => expected.state(item.id) === "free" && item.face !== expected.tiles[first].face)!;
    await tile(page, first).tap();
    await tile(page, different.id).tap();
    await expect(feedback).toContainText("Not a match.");
    await expect(tally(page)).toHaveText("78 / 78 tiles left");
    expect(await page.evaluate(key => localStorage.getItem(key), dailySave(mount, "mahjong-solitaire"))).toBeNull();
    await page.screenshot({ path: info.outputPath("daily-mahjong-solitaire-320.png") });
    await tile(page, first).tap();
    await tile(page, partner).tap();
    await expect(tally(page)).toHaveText("76 / 78 tiles left");
    expect(await savedRecord(page, record)).toBeNull();
    await page.reload();
    await expect(tally(page)).toHaveText("76 / 78 tiles left");
    const restart = "Restart today’s deal";
    await page.getByRole("button", { name: restart, exact: true }).tap();
    await page.getByRole("dialog").getByRole("button", { name: restart, exact: true }).tap();
    await expect(tally(page)).toHaveText("78 / 78 tiles left");
    expect(await stackFaces(page)).toEqual(faces);

    await page.context().setOffline(true);
    for (const [a, b] of expected.witness) {
      await tile(page, a).tap();
      await tile(page, b).tap();
    }
    await expect(feedback).toHaveText("Stack cleared. You matched every tile!");
    await expect(tally(page)).toHaveText("0 / 78 tiles left");
    await expect(board.locator(".stack-tile:visible")).toHaveCount(0);
    await page.context().setOffline(false);
    await page.reload();
    await expect(tally(page)).toHaveText("0 / 78 tiles left");
    await page.getByRole("button", { name: restart, exact: true }).tap();
    await page.getByRole("dialog").getByRole("button", { name: restart, exact: true }).tap();
    await expect(tally(page)).toHaveText("78 / 78 tiles left");
    await page.getByRole("link", { name: "‹ Daily Court" }).tap();
    await expect(page.getByText("Completed today")).toHaveCount(1);
    await expect(page.getByText("Ready to play")).toHaveCount(1);
    await expect(recordDay(page, "2026-11-25")).toContainText("Xiangqi not cleared. Mahjong Solitaire cleared.");
    expect(await savedRecord(page, record)).toEqual({ "2026-11-25": 2 });
    await page.getByRole("link", { name: "Play Xiangqi" }).tap();
    await tryMove(page, "b9", "b10");
    await page.getByRole("link", { name: "‹ Daily Court" }).tap();
    await expect(page.getByText("Completed today")).toHaveCount(2);
    await expect(recordDay(page, "2026-11-25")).toContainText("Xiangqi cleared. Mahjong Solitaire cleared.");
    await streaks(page, 1, 1);
    expect(await savedRecord(page, record)).toEqual({ "2026-11-25": 3 });
    for (const [width, height] of [[320, 730], [740, 320], [1366, 900]]) {
      await page.setViewportSize({ width, height });
      await fitsPhone(page);
      expect(await playRecord(page).locator(".daily-record-day").evaluateAll(days =>
        days.every(day => day.clientWidth >= day.scrollWidth))).toBe(true);
      const box = (await playRecord(page).boundingBox())!;
      const deck = (await page.locator(".daily-deck").boundingBox())!;
      expect(box.y).toBeGreaterThanOrEqual(deck.y + deck.height);
      await playRecord(page).screenshot({ path: info.outputPath(`daily-play-record-${width}.png`) });
    }
    await page.setViewportSize({ width: 320, height: 730 });
    await page.reload();
    await expect(recordDay(page, "2026-11-25")).toContainText("Xiangqi cleared. Mahjong Solitaire cleared.");
    await streaks(page, 1, 1);
    expect(await savedRecord(page, otherRecord)).toEqual({ "2026-11-25": 3 });
    const stored = await page.evaluate(({ practiceKey, otherKey }) => [localStorage.getItem(practiceKey), localStorage.getItem(otherKey)],
      { practiceKey, otherKey });
    expect(stored).toEqual([practice.save(), "other mount"]);

    await page.clock.setFixedTime(new Date("2026-11-26T00:00:00Z"));
    await page.reload();
    await expect(page.getByText("Ready to play")).toHaveCount(2);
    await expect(recordDay(page, "2026-11-26")).toHaveAttribute("aria-current", "date");
    await streaks(page, 1, 1);
    await page.getByRole("link", { name: "Play Mahjong Solitaire" }).tap();
    await expect(tally(page)).toHaveText("78 / 78 tiles left");
    const tomorrow = new MahjongStack(utcEdition(new Date("2026-11-26T00:00:00Z")).seed);
    expect(await stackFaces(page)).toEqual(tomorrow.tiles.map(tile => `lt-${tile.face}`));
    for (const id of tomorrow.witness[0]) await tile(page, id).tap();
    await expect(tally(page)).toHaveText("76 / 78 tiles left");
    const saved = await page.evaluate(key => JSON.parse(localStorage.getItem(key)!), dailySave(mount, "mahjong-solitaire"));
    expect(saved).toMatchObject({ date: "2026-11-26", completed: false });
    await verifyLocal();
    const openPractice = page.getByRole("link", { name: "Open practice stack" });
    await expect(openPractice).toHaveAttribute("href", `${mount}mahjong-solitaire/`);
    await openPractice.click();
    await expect(tally(page)).toHaveText("76 / 78 tiles left");
  });

  test("the Daily record validates migrated completions and rolls its 30 UTC days without idle writes", async ({ page, baseURL }) => {
    const base = baseURL!;
    const mount = new URL(base).pathname;
    const record = dailySave(mount, "record");
    await page.addInitScript(({ record, xiangqi }) => {
      localStorage.setItem(record, JSON.stringify({
        "2026-11-24": 3, "2026-11-25": 3, "2026-11-26": 3, "2026-11-27": 3, "2026-11-28": 3,
        "2026-12-18": 4, "2026-12-21": 3, "2026-12-22": 3, "2026-12-23": 2, "2026-12-24": "3",
        "2026-12-25": 3, "2026-12-99": 3,
      }));
      localStorage.setItem(xiangqi, JSON.stringify({ date: "2026-12-23", completed: true, value: "b9-b10" }));
    }, { record, xiangqi: dailySave(mount, "xiangqi") });
    await page.clock.install({ time: new Date("2026-12-24T23:59:50Z") });
    const verifyLocal = noRemotePlay(page, base);
    await page.goto(dailyUrl(base));
    await expect(recordDay(page, "2026-12-23")).toContainText("Xiangqi cleared. Mahjong Solitaire cleared.");
    await expect(recordDay(page, "2026-12-18")).toContainText("Xiangqi not cleared. Mahjong Solitaire not cleared.");
    await expect(recordDay(page, "2026-12-24")).toContainText("Xiangqi not cleared. Mahjong Solitaire not cleared.");
    await streaks(page, 3, 4);
    const saved = await savedRecord(page, record);
    expect(saved).toEqual({ "2026-11-25": 3, "2026-11-26": 3, "2026-11-27": 3, "2026-11-28": 3,
      "2026-12-21": 3, "2026-12-22": 3, "2026-12-23": 3 });

    await page.clock.fastForward(10_010);
    await expect(page.locator("time.daily-date")).toHaveText("2026-12-25 UTC");
    await expect(recordDay(page, "2026-12-25")).toHaveAttribute("aria-current", "date");
    await streaks(page, 0, 3);
    expect(await savedRecord(page, record)).toEqual(saved);
    await verifyLocal();
  });

  test("desktop stays playable offline after load with storage blocked, then rolls over at UTC midnight", async ({ page, baseURL }, info) => {
    const base = baseURL!;
    await page.addInitScript(() => Object.defineProperty(window, "localStorage", {
      get: () => { throw new DOMException("Storage blocked", "SecurityError"); },
    }));
    await page.clock.install({ time: new Date("2026-11-25T23:50:00Z") });
    await page.setViewportSize({ width: 1280, height: 850 });
    await page.emulateMedia({ reducedMotion: "reduce" });
    const verifyLocal = noRemotePlay(page, base);
    await page.goto(dailyUrl(base, "#xiangqi"));
    await expect(page.locator("time.daily-date")).toHaveText("2026-11-25 UTC");
    await expect(page.getByText(/Browser storage is unavailable/)).toBeVisible();
    await expect(xiangqiBoard(page)).toBeInViewport({ ratio: .99 });
    await fitsPhone(page);
    await paintedUses(xiangqiBoard(page), ".xiangqi-seal use:not(.seal-cut)", 4);
    await page.screenshot({ path: info.outputPath("daily-xiangqi-desktop.png") });
    await page.context().setOffline(true);
    await tryMove(page, "b9", "e9");
    await expect(tactic(page)).toHaveAttribute("data-state", "won");
    await page.getByRole("link", { name: "‹ Daily Court" }).tap();
    await expect(page.getByText("Completed today")).toHaveCount(1);
    await expect(playRecord(page).getByText(/Browser storage is unavailable/)).toBeVisible();
    await expect(recordDay(page, "2026-11-25")).toContainText("Xiangqi cleared. Mahjong Solitaire not cleared.");
    await page.getByRole("link", { name: "Play Xiangqi" }).tap();
    await expect(tactic(page)).toHaveAttribute("data-state", "won");
    await page.getByRole("button", { name: "Replay today’s tactic" }).tap();
    await point(page, "b9").tap();
    await expect(point(page, "e9")).toHaveAttribute("data-target", "");
    await page.clock.fastForward(600_010);
    await expect(page.locator("time.daily-date")).toHaveText("2026-11-26 UTC");
    await expect(page.getByText("A new UTC edition is ready.")).toBeVisible();
    await expect(tactic(page)).toHaveAttribute("data-puzzle", "borrowed-screen");
    await expect(tactic(page).locator("[data-target]")).toHaveCount(0);
    await expect(point(page, "b7")).toHaveAccessibleName("b7, red cannon");
    await expect(point(page, "b9")).toHaveAccessibleName("b9, empty");
    await page.context().setOffline(false);
    await page.getByRole("link", { name: "‹ Daily Court" }).tap();
    await expect(page.getByText("Ready to play")).toHaveCount(2);
    await expect(recordDay(page, "2026-11-25")).toContainText("Xiangqi cleared. Mahjong Solitaire not cleared.");
    await page.getByRole("link", { name: "Play Mahjong Solitaire" }).tap();
    await expect(tally(page)).toHaveText("78 / 78 tiles left");
    await expect(page.getByText(/Browser storage is unavailable/)).toBeVisible();
    const stack = new MahjongStack(utcEdition(new Date("2026-11-26T00:00:00Z")).seed);
    expect(await stackFaces(page)).toEqual(stack.tiles.map(tile => `lt-${tile.face}`));
    const [a, b] = stack.witness[0];
    await tile(page, a).tap();
    await tile(page, b).tap();
    await expect(tally(page)).toHaveText("76 / 78 tiles left");
    await page.reload();
    await expect(tally(page)).toHaveText("78 / 78 tiles left");
    await page.getByRole("link", { name: "‹ Daily Court" }).tap();
    await expect(recordDay(page, "2026-11-25")).toContainText("Xiangqi not cleared. Mahjong Solitaire not cleared.");
    await verifyLocal();
  });
});
