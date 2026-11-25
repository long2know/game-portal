import { expect } from "@playwright/test";
import type { Locator, Page, TestInfo } from "@playwright/test";
import type { Table } from "../src/portal/api";
import type { Ack, ClientMessage, ServerMessage, Snapshot } from "../src/protocol";
import { mahjongSolitaireProgressKey } from "../src/portal/games/mahjong-solitaire-save";
import { MahjongStack } from "../src/portal/games/mahjong-stack";
import { fitsPhone, receive, tap } from "./portal-support";

const row = (page: Page, game: string) => page.getByRole("region", { name: "Continue playing" }).getByRole("listitem")
  .filter({ has: page.getByRole("heading", { name: game, exact: true }) });
const button = (from: Locator, name: string) => from.getByRole("button", { name, exact: true });

async function post<T>(page: Page, url: string, data = {}): Promise<T> {
  const response = await page.request.post(url, { data, headers: { Origin: new URL(url).origin } });
  expect(response.ok(), new URL(url).pathname).toBe(true);
  return response.json();
}

export async function savedSeats(page: Page, base: string, info: TestInfo) {
  const url = (path = "") => new URL(path, base).href;
  await post(page, url("api/guest"));
  const create = (botDifficulty: "hard" | "easy") => post<Table>(page, url("api/board/tables"),
    { game: "chess", variant: "standard", players: 2, bots: true, botDifficulty });
  const shared = await post<{ id: string; revision: number }>(page, url("api/big-two/tables"), { players: 4, bots: false });
  const farkleEndpoint = url("api/farkle/tables");
  const farkle = await post<{ id: string; revision: number }>(page, farkleEndpoint, { players: 3, bots: true });
  const old = await create("hard");
  await page.setViewportSize({ width: 320, height: 730 });
  await page.goto(base);
  const chess = row(page, "Chess");
  await expect(chess).toBeVisible();
  await expect(row(page, "Big Two").getByRole("button", { name: "Continue Big Two" })).toBeVisible();
  await expect(row(page, "Big Two").getByRole("button", { name: /Start fresh/ })).toHaveCount(0);
  await fitsPhone(page);
  await page.locator("#continue-playing").screenshot({ path: info.outputPath("continue-playing-320.png") });
  const later = await create("easy");
  const at = (id: string) => url(`api/board/tables/${id}`);
  const exact = receive(page, at(old.id), "GET");
  await button(chess, "Continue Chess").tap();
  expect(await (await exact).json()).toMatchObject({ id: old.id, botDifficulty: "hard" });
  await expect(page).toHaveURL(url("chess/"));

  await page.goto(base);
  const fresh = button(chess, "Start fresh: Chess");
  await fresh.tap();
  const dialog = page.getByRole("dialog", { name: "Start fresh?" });
  await expect(dialog).toHaveAccessibleDescription(/won’t be closed or changed/);
  await button(dialog, "Cancel").tap();
  await expect(fresh).toBeFocused();
  expect((await (await page.request.get(url("api/board/resume?game=chess"))).json()).tableId).toBe(later.id);
  await fresh.tap();
  const newTable = receive(page, url("api/board/tables"), "POST");
  const opened = page.waitForResponse(response => response.request().method() === "GET" &&
    response.url().startsWith(url("api/board/tables/")) && response.url() !== at(later.id));
  await button(dialog, "Start fresh").tap();
  const created = await newTable;
  expect(created.request().postDataJSON()).toEqual({ game: "chess", variant: "standard", players: 2, bots: true, botDifficulty: "easy" });
  expect(created.ok()).toBe(true);
  const restarted: Table = await (await opened).json();
  expect(restarted.id).not.toBe(later.id);
  expect(restarted).toMatchObject({ status: "playing", botDifficulty: "easy" });
  await expect(page).toHaveURL(url("chess/"));
  await expect(page.getByText("Standard · 2 players · Easy bot", { exact: true })).toBeVisible();
  expect(await (await page.request.get(at(later.id))).json()).toMatchObject({ id: later.id, status: "playing", revision: later.revision });
  const sharedGet = await page.request.get(url(`api/big-two/tables/${shared.id}`));
  expect(await sharedGet.json()).toMatchObject({ status: "waiting", revision: shared.revision });
  await page.goto(base);
  await button(row(page, "Farkle"), "Start fresh: Farkle").tap();
  const seated = receive(page, farkleEndpoint, "POST");
  const reopened = page.waitForResponse(response => response.request().method() === "GET" &&
    response.url().startsWith(`${farkleEndpoint}/`) && response.url() !== `${farkleEndpoint}/${farkle.id}`);
  await button(dialog, "Start fresh").tap();
  const seatedReply = await seated;
  expect(seatedReply.request().postDataJSON()).toEqual({ players: 3, bots: true });
  expect(seatedReply.ok()).toBe(true);
  expect((await (await reopened).json()).id).not.toBe(farkle.id);
  await expect(page).toHaveURL(url("farkle/"));
  expect(await (await page.request.get(`${farkleEndpoint}/${farkle.id}`)).json()).toMatchObject({ revision: farkle.revision, status: "playing" });
  await page.goto(base);
  const exactShared = receive(page, url(`api/big-two/tables/${shared.id}`), "GET");
  await button(row(page, "Big Two"), "Continue Big Two").tap();
  expect((await exactShared).ok()).toBe(true);
  await expect(page).toHaveURL(url("big-two/"));
  await expect(page.getByText("Waiting for players", { exact: true })).toBeVisible();
}

export async function savedSolo(page: Page, base: string) {
  const url = (path = "") => new URL(path, base).href;
  const key = mahjongSolitaireProgressKey(new URL(base).pathname);
  const stack = new MahjongStack(328603);
  stack.match(...stack.hint(null)!);
  const saved = stack.save();
  await page.addInitScript(({ key, saved }) => {
    if (!localStorage.getItem(key)) localStorage.setItem(key, saved);
  }, { key, saved });
  await post(page, url("api/guest"));
  const sudoku = url("api/solo/sudoku");
  const initial = await post<{ revision: number; position: { given: number[] } }>(page, sudoku, { variant: "hard" });
  const index = initial.position.given.indexOf(0);
  const played = await post<{ revision: number; position: { moves: number } }>(page, `${sudoku}/moves`,
    { revision: initial.revision, action: { kind: "set", index, value: 1 } });
  expect(played.position.moves).toBe(1);
  await page.goto(base);
  const local = row(page, "Mahjong Solitaire");
  await expect(local).toContainText("On this device");
  const localFresh = local.getByRole("link", { name: "Start fresh: Mahjong Solitaire" });
  await expect(localFresh).toHaveAttribute("href", `${new URL(base).pathname}mahjong-solitaire/?fresh=1`);
  await localFresh.tap();
  await expect(page.getByRole("dialog")).toHaveCount(1);
  await button(page.getByRole("dialog", { name: "New game?" }), "Cancel").tap();
  await expect(page.locator(".stack-count")).toHaveText("76 / 78 tiles left");
  expect(await page.evaluate(key => localStorage.getItem(key), key)).toBe(saved);
  await page.goto(base);

  const fresh = button(row(page, "Sudoku"), "Start fresh: Sudoku");
  await fresh.tap();
  const dialog = page.getByRole("dialog", { name: "Start fresh?" });
  await expect(dialog).toHaveAccessibleDescription(/replaces your saved Sudoku game/);
  await page.keyboard.press("Escape");
  expect(await (await page.request.get(sudoku)).json()).toMatchObject({ table: { revision: played.revision, position: { moves: 1 } } });
  await fresh.tap();
  const replacement = receive(page, sudoku, "POST");
  await button(dialog, "Start fresh").tap();
  const result = await replacement;
  expect(result.request().postDataJSON()).toEqual({ variant: "hard" });
  expect(result.ok()).toBe(true);
  await expect(page).toHaveURL(url("sudoku/"));
  await expect(page.getByText("Hard · 0 moves", { exact: true })).toBeVisible();
  expect(await (await page.request.get(sudoku)).json()).toMatchObject({
    table: { variant: "hard", revision: played.revision + 1, position: { moves: 0 } },
  });
}

export async function savedMahjong(page: Page, base: string) {
  const url = (path = "") => new URL(path, base).href;
  await post(page, url("api/guest"));
  const settings = await page.request.patch(url("api/profile"), { headers: { Origin: new URL(base).origin }, data: {
    preferences: { sound: false, music: false, volume: .7, reducedMotion: true },
  } });
  expect(settings.ok()).toBe(true);
  const snapshots: Snapshot[] = [];
  const intents: { roomId: string; expectedRoomRevision: number }[] = [];
  let release: (() => void) | undefined;
  let latestRestart: Ack | undefined;
  let dropAck = false;
  await page.routeWebSocket("**/ws", route => {
    const server = route.connectToServer();
    let restart: string | undefined;
    let ack: Ack | undefined;
    let dealDelivered = false;
    const heldDeals: (string | Buffer)[] = [];
    route.onMessage(payload => {
      const request = JSON.parse(payload.toString()) as ClientMessage;
      if (request.type === "command" && request.op === "restartMatch") {
        intents.push(request.payload);
        restart = request.requestId;
      }
      server.send(payload);
    });
    server.onMessage(payload => {
      const frame = JSON.parse(payload.toString()) as ServerMessage;
      if (frame.type === "ack" && restart && frame.requestId === restart && frame.status === "ok") {
        ack = latestRestart = frame;
        if (dropAck) { route.close({ code: 1012, reason: "test lost confirmation" }); return; }
      }
      if (frame.type === "snapshot") {
        snapshots.push(frame);
        if (!dealDelivered && ack?.gameRevision != null && frame.gameRevision >= ack.gameRevision) {
          heldDeals.push(payload);
          release ??= () => {
            dealDelivered = true;
            heldDeals.splice(0).forEach(frame => route.send(frame));
          };
          return;
        }
      }
      route.send(payload);
    });
  });
  await page.goto(url("mahjong/"));
  await tap(page, "Play with bots");
  await tap(page, "Mark ready");
  await tap(page, "Start match");
  await tap(page, "Pause");
  await expect(page.getByText("Table paused", { exact: true })).toBeVisible();
  const before = snapshots.at(-1)!;
  await page.goto(base);
  const mahjong = row(page, "Changsha Mahjong");
  const fresh = button(mahjong, "Start fresh: Changsha Mahjong");
  const confirm = page.getByRole("dialog", { name: "Start fresh?" });
  await fresh.tap();
  await expect(confirm).toHaveAccessibleDescription(/keeps.*table total, but resets the match score/);
  await button(confirm, "Cancel").tap();
  expect(intents).toEqual([]);
  await fresh.tap();
  await button(confirm, "Start fresh").tap();
  await expect.poll(() => !!release).toBe(true);
  expect(intents).toEqual([{ roomId: before.roomId, expectedRoomRevision: before.roomRevision }]);
  await expect(page).toHaveURL(base);
  await expect(fresh).toBeDisabled();
  const firstDeal = snapshots.find(frame => frame.gameRevision === latestRestart!.gameRevision);
  expect(firstDeal).toMatchObject({ roomId: before.roomId, roomRevision: before.roomRevision + 1,
    room: { status: "playing", paused: false, tableTotals: before.room.tableTotals },
    game: { handNumber: 1, dealerSeat: before.game!.dealerSeat, settlement: null } });
  release!();
  await expect(page).toHaveURL(url("mahjong/"));
  await expect(page.getByRole("region", { name: "Mahjong table" })).toBeVisible();
  expect(snapshots.at(-1)).toMatchObject({ roomId: before.roomId });

  await page.goto(base);
  dropAck = true;
  await fresh.tap();
  await button(confirm, "Start fresh").tap();
  await expect(page.getByRole("region", { name: "Continue playing" }).getByRole("status")).toContainText("restart wasn’t confirmed");
  await expect(page).toHaveURL(base);
  await expect(fresh).toBeDisabled();
  expect(intents).toHaveLength(2);
  const verified = latestRestart!;
  await button(mahjong, "Continue Changsha Mahjong").tap();
  await expect(page).toHaveURL(url("mahjong/"));
  await expect(page.getByRole("region", { name: "Mahjong table" })).toBeVisible();
  expect(snapshots.at(-1)!.roomRevision).toBeGreaterThanOrEqual(verified.roomRevision!);
  expect(intents).toHaveLength(2);
}
