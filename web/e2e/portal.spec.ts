import { gzipSync } from "node:zlib";
import { expect, test } from "@playwright/test";
import type { Locator, Page, Response } from "@playwright/test";
import type { Table } from "../src/portal/api";

const boardGames = [
  ["chess", "Chess"], ["go", "Go"], ["reversi", "Reversi"],
  ["checkers", "Checkers"], ["chinese-checkers", "Chinese Checkers"],
] as const;

const portal = (baseURL: string) => new URL("/portal/", baseURL);
const urls = (baseURL: string) => (path = "") => new URL(path, portal(baseURL)).href;
const projected = (response: Response) => response.json() as Promise<Table>;
const squareAt = (board: Locator, row: number, column: number) => board.locator(`[data-row="${row}"][data-column="${column}"]`);
const cellById = (board: Locator, id: string) => board.locator(`[data-cell=${JSON.stringify(id)}]`);
const state = (page: Page) => page.locator(".turn-text");
const boardOn = (page: Page, name: string) => page.getByRole("group", { name: `${name} board`, exact: true });
const tap = (page: Page, name: string | RegExp) => page.getByRole("button", { name, exact: true }).tap();

function receive(page: Page, url: string, method: string) {
  return page.waitForResponse(response => response.url() === url && response.request().method() === method);
}

type ClipboardPage = typeof window & { clipboardWrites: string[] };

async function observeClipboard(page: Page) {
  await page.addInitScript(() => {
    const writes: string[] = [];
    (window as ClipboardPage).clipboardWrites = writes;
    Object.defineProperty(navigator, "clipboard", { value: { writeText: async (value: string) => { writes.push(value); } } });
  });
  return () => page.evaluate(() => (window as ClipboardPage).clipboardWrites);
}

async function fitsPhone(page: Page) {
  expect(await page.evaluate(() => document.documentElement.scrollWidth)).toBeLessThanOrEqual(page.viewportSize()!.width);
}

test.describe("mounted Jade Court portal", () => {
  test.beforeEach(({ baseURL }) => {
    test.skip(!baseURL || !/^\/portal\/?$/.test(new URL(baseURL).pathname), "Requires the published runtime with the portal mounted at /portal/.");
  });

  test("lobby presents six real destinations and keeps assets and links at the mount", async ({ page, baseURL, request }, info) => {
    const url = urls(baseURL!);
    await page.setViewportSize({ width: 1440, height: 900 });
    const heroStudy = page.waitForResponse(response => /\/portal\/assets\/court-study-[\w-]+\.svg$/.test(new URL(response.url()).pathname));
    await page.goto(url());
    await expect(page).toHaveTitle("Jade Court · Board Games");
    await expect(page.getByRole("heading", { name: "Find your next move." })).toBeVisible();
    await expect(page.locator("#app")).toHaveAttribute("data-base", "/portal/");
    expect((await heroStudy).ok()).toBe(true);
    await expect(page.locator('link[rel="icon"]')).toHaveAttribute("href", /^\/portal\/assets\/jade-court-[\w-]+\.svg$/);
    await expect(page.getByRole("link", { name: "Take a seat", exact: true })).toHaveAttribute("href", "/portal/mahjong/");
    await page.screenshot({ path: info.outputPath("lobby-desktop.png"), fullPage: true });
    await page.setViewportSize({ width: 320, height: 730 });
    await fitsPhone(page);
    await page.screenshot({ path: info.outputPath("lobby-phone.png"), fullPage: true });

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
      expect(initialResponse.request().postDataJSON()).toEqual({ game, variant: initial.variant, players: initial.seats.length, bots: true });
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
        : position.moves.find(move => move.to !== null && position.moves.filter(candidate => candidate.from === move.from && candidate.to === move.to).length === 1))!;
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
      const after = await projected(moveResponse);
      expect(after.revision).toBeGreaterThan(initial.revision);
      expect(after.position!.cells).not.toEqual(position.cells);
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
      await expect(page.getByText("Court Guest", { exact: true })).toBeVisible();
      if (game === "chess") {
        await page.setViewportSize({ width: 1440, height: 900 });
        await page.screenshot({ path: info.outputPath("chess-desktop.png"), fullPage: true });
      }
    });
  }

  test("a stale move refreshes the real board without replaying the choice", async ({ page, context, baseURL }) => {
    const url = urls(baseURL!);
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
  });

  test("private Go invite joins a second guest, captures, reconnects, and completes by passing", async ({ page, context, browser, baseURL }, info) => {
    const url = urls(baseURL!);
    const copied = await observeClipboard(page);
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
      await take(friend, () => squareAt(friendBoard, 0, 0).tap());
      await expect(squareAt(hostBoard, 0, 0)).toHaveAccessibleName(/white stone$/);
      await context.setOffline(true);
      await expect(state(page)).toHaveText("Reconnecting…");
      await context.setOffline(false);
      await expect(state(page)).toHaveText("Your turn");
      const captured = await take(page, () => squareAt(hostBoard, 1, 0).tap());
      expect(captured.position!.cells.find(cell => cell.row === 0 && cell.column === 0)?.owner).toBeNull();
      await expect(squareAt(friendBoard, 0, 0)).toHaveAccessibleName(/empty$/);
      await take(friend, () => tap(friend, "Pass"));
      const finished = await take(page, () => tap(page, "Pass"));
      expect(finished).toMatchObject({ status: "finished", position: { turn: null, scores: expect.any(Array) } });
      await expect(page.getByText("Game over", { exact: true })).toBeVisible();
      await expect(friend.getByText("Game over", { exact: true })).toBeVisible();
      await expect(state(page)).toHaveText(/won|wins|Draw/);
      await expect(page.getByText(/ pts$/)).toHaveCount(2);
      await page.screenshot({ path: info.outputPath("finished-go-phone.png"), fullPage: true });
      await tap(page, "Set up another game");
      await expect(page.getByRole("textbox", { name: "Display name" })).toHaveValue("Opal");
    } finally {
      await friendContext.close();
    }
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
    expect([...assets].map(asset => new URL(asset).pathname)).toEqual(expect.arrayContaining([
      expect.stringMatching(/\/index-[\w-]+\.js$/), expect.stringMatching(/\/index-[\w-]+\.css$/),
      expect.stringMatching(/\/jade-court-[\w-]+\.svg$/),
    ]));
    expect([...assets].every(asset => !/\/(?:portal-|actors-|court-study-|hall-dragon-)/.test(new URL(asset).pathname))).toBe(true);
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
});
