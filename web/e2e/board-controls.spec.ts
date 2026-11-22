import { expect, test } from "@playwright/test";
import type { Table } from "../src/portal/api";
import { fitsPhone, observeMotion, projected, receive, state, tap, urls } from "./portal-support";

test.describe("mounted Jade Court table controls", () => {
  test.beforeEach(({ baseURL }) => {
    test.skip(!baseURL || !/^\/portal\/?$/.test(new URL(baseURL).pathname), "Requires the mounted portal runtime.");
  });

  test("a waiting table closes on Leave and cannot be rejoined or resumed", async ({ page, browser, baseURL }) => {
    const url = urls(baseURL!);
    await page.setViewportSize({ width: 320, height: 730 });
    await page.goto(url("chess/"));
    const created = receive(page, url("api/board/tables"), "POST");
    await tap(page, "Invite a friend");
    const response = await created;
    expect(response.request().postDataJSON()).not.toHaveProperty("botDifficulty");
    const waiting = await projected(response);
    const invitation = await page.getByRole("textbox", { name: "Invite link" }).inputValue();
    const leave = page.getByRole("button", { name: "Leave table", exact: true });
    expect((await leave.boundingBox())!.height).toBeGreaterThanOrEqual(44);
    await leave.tap();
    const dialog = page.getByRole("dialog", { name: "Leave table?" });
    await expect(dialog).toHaveAccessibleDescription(/can’t be resumed/);
    await expect(dialog.getByRole("button", { name: "Cancel" })).toBeFocused();
    await page.keyboard.press("Escape");
    await expect(leave).toBeFocused();
    await expect(state(page)).toHaveText("Waiting for players");
    await leave.tap();
    const closed = receive(page, url(`api/board/tables/${waiting.id}/leave`), "POST");
    await dialog.getByRole("button", { name: "Leave table" }).tap();
    const result = await closed;
    expect(result.request().postDataJSON()).toEqual({ revision: waiting.revision });
    expect(await projected(result)).toMatchObject({ status: "closed", endedBySeat: waiting.seat, endReason: "left" });
    await expect(state(page)).toHaveText("Table closed");
    await expect(page.getByText("You left the table.", { exact: true })).toBeVisible();
    await expect(page.getByRole("heading", { name: "Chess", exact: true })).toBeFocused();
    const peer = await browser.newContext();
    try {
      const visitor = await peer.newPage();
      await visitor.goto(invitation);
      await visitor.getByRole("button", { name: "Join table" }).click();
      await expect(visitor.getByText(/invitation couldn’t be accepted/)).toBeVisible();
      await expect(visitor).toHaveURL(invitation);
    } finally { await peer.close(); }
    const resumed = receive(page, url("api/board/resume?game=chess"), "GET");
    await page.reload();
    expect(await (await resumed).json()).toEqual({ tableId: null });
    const difficulty = page.getByRole("combobox", { name: "Bot difficulty", exact: true });
    await expect(difficulty).toBeEnabled();
    await expect(difficulty).toHaveValue("standard");
    for (const [id, label] of [["easy", "Easy"], ["hard", "Hard"]]) {
      await difficulty.selectOption(id);
      const restarted = receive(page, url("api/board/tables"), "POST");
      await tap(page, "Play a bot");
      const restartedResponse = await restarted;
      const botTable = await projected(restartedResponse);
      expect(botTable).toMatchObject({ status: "playing", botDifficulty: id });
      expect(botTable.id).not.toBe(waiting.id);
      expect(restartedResponse.request().postDataJSON()).toHaveProperty("botDifficulty", id);
      const variant = page.getByText(`Standard · 2 players · ${label} bot`, { exact: true });
      await expect(variant).toBeVisible();
      await page.reload();
      await expect(variant).toBeVisible();
      await tap(page, "Resign");
      await page.getByRole("dialog", { name: "Resign?" }).getByRole("button", { name: "Resign" }).tap();
      await expect(page.getByText("You resigned.", { exact: true })).toBeVisible();
      await tap(page, "Set up another game");
    }
    await fitsPhone(page);
  });

  test("a visiting player’s stale Leave refreshes before closing a live private game", async ({ page, context, browser, baseURL }, info) => {
    const url = urls(baseURL!);
    await page.goto(url("reversi/"));
    await page.getByLabel("Display name").fill("Host");
    const created = receive(page, url("api/board/tables"), "POST");
    await tap(page, "Invite a friend");
    const waiting = await projected(await created);
    const path = url(`api/board/tables/${waiting.id}`);
    const peer = await browser.newContext({ viewport: { width: 320, height: 730 }, hasTouch: true });
    try {
      const visitor = await peer.newPage();
      const motion = await observeMotion(visitor);
      await visitor.goto(await page.getByRole("textbox", { name: "Invite link" }).inputValue());
      await visitor.getByLabel("Display name").fill("Visitor");
      const joined = receive(visitor, url("api/board/join"), "POST");
      await tap(visitor, "Join table");
      const table = await projected(await joined);
      await expect(state(page)).toHaveText("Your turn");
      const host = await (await context.request.get(path)).json() as Table;
      const move = host.position!.moves[0];
      await visitor.route(`${path}/leave`, async route => {
        const changed = await context.request.post(`${path}/moves`, {
          headers: { Origin: new URL(path).origin },
          data: { revision: host.revision, moveId: move.id },
        });
        expect(changed.ok()).toBe(true);
        await route.continue();
      }, { times: 1 });
      const close = async () => {
        await tap(visitor, "Leave table");
        const dialog = visitor.getByRole("dialog", { name: "Leave table?" });
        await expect(dialog.getByRole("button", { name: "Cancel" })).toBeFocused();
        await dialog.getByRole("button", { name: "Leave table" }).tap();
      };
      const stale = receive(visitor, `${path}/leave`, "POST");
      await close();
      const rejected = await stale;
      expect(rejected.status()).toBe(409);
      expect(rejected.request().postDataJSON()).toEqual({ revision: table.revision });
      await expect(visitor.getByText("The table is up to date. Check it before ending the game again.")).toBeVisible();
      await expect(visitor.getByText(`Last move: ${move.label}`, { exact: true })).toBeVisible();
      expect(await motion()).toEqual([]);
      const ended = receive(visitor, `${path}/leave`, "POST");
      await close();
      const result = await ended;
      expect(result.request().postDataJSON()).toEqual({ revision: table.revision + 1 });
      const terminal = await projected(result);
      expect(terminal).toMatchObject({ status: "closed", endedBySeat: table.seat, endReason: "left" });
      await expect(state(visitor)).toHaveText("Table closed");
      await expect(visitor.getByText("You left the table.", { exact: true })).toBeVisible();
      await expect(page.getByText("Visitor left the table.", { exact: true })).toBeVisible();
      await expect(visitor.getByRole("button", { name: "Leave table", exact: true })).toHaveCount(0);
      await fitsPhone(visitor);
      await visitor.screenshot({ path: info.outputPath("leave-visitor-320.png"), fullPage: true });
      await visitor.reload();
      await expect(visitor.getByRole("button", { name: "Invite a friend" })).toBeEnabled();
      await page.reload();
      await expect(page.getByRole("button", { name: "Invite a friend" })).toBeEnabled();
    } finally { await peer.close(); }
  });

  test("browsing away preserves private Chess, while a non-turn resignation records a loss", async ({ page, browser, baseURL }, info) => {
    const url = urls(baseURL!);
    await page.goto(url("chess/"));
    await page.getByLabel("Display name").fill("Ivory");
    await tap(page, "Invite a friend");
    const peer = await browser.newContext({ viewport: { width: 320, height: 730 }, hasTouch: true });
    try {
      const visitor = await peer.newPage();
      await visitor.goto(await page.getByRole("textbox", { name: "Invite link" }).inputValue());
      await visitor.getByLabel("Display name").fill("Onyx");
      const joined = receive(visitor, url("api/board/join"), "POST");
      await tap(visitor, "Join table");
      const table = await projected(await joined);
      expect(table.position?.turn).not.toBe(table.seat);
      await expect(state(visitor)).toHaveText("Ivory’s turn");
      await visitor.getByRole("link", { name: "‹ All games", exact: true }).tap();
      await expect(visitor).toHaveURL(url());
      const resumed = receive(visitor, url("api/board/resume?game=chess"), "GET");
      await visitor.getByRole("link", { name: "Set up Chess" }).tap();
      expect(await (await resumed).json()).toEqual({ tableId: table.id });
      await expect(state(visitor)).toHaveText("Ivory’s turn");
      await expect(visitor.getByRole("button", { name: "Leave table", exact: true })).toHaveCount(0);
      await tap(visitor, "Resign");
      const dialog = visitor.getByRole("dialog", { name: "Resign?" });
      await expect(dialog).toHaveAccessibleDescription(/records a loss/);
      await visitor.screenshot({ path: info.outputPath("resign-dialog-320.png") });
      const sent = receive(visitor, url(`api/board/tables/${table.id}/resign`), "POST");
      await dialog.getByRole("button", { name: "Resign" }).tap();
      const response = await sent;
      expect(response.request().postDataJSON()).toEqual({ revision: table.revision });
      expect(await projected(response)).toMatchObject({
        status: "finished", result: { cause: "resignation", loserSeat: table.seat, winnerSeat: table.position!.turn },
      });
      await expect(state(visitor)).toHaveText("Ivory wins.");
      await expect(visitor.getByText("You resigned.", { exact: true })).toBeVisible();
      await expect(state(page)).toHaveText("You won.");
      await expect(page.getByText("Onyx resigned.", { exact: true })).toBeVisible();
      await visitor.reload();
      await expect(visitor.getByRole("button", { name: "Invite a friend" })).toBeEnabled();
    } finally { await peer.close(); }
  });
});
