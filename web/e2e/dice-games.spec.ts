import { expect, test } from "@playwright/test";
import type { Locator, Page, Response } from "@playwright/test";
import type { MultiplayerTable } from "../src/portal/games/multiplayer";
import { fitsPhone, receive } from "./portal-support";

type DiceGame = "farkle" | "backgammon";
type Table<P> = MultiplayerTable<P> & { position: P };
type Farkle = {
  turn: number; phase: "roll" | "keep" | "finished"; dice: number[]; kept: number[];
  turnPoints: number; canRoll: boolean; canBank: boolean; choices: { mask: number; points: number }[];
  recentTurns: { seat: number; kind: "banked" | "farkle"; points: number; dice: number[] }[];
};
type Move = { from: number; to: number; die: number };
type Backgammon = {
  turn: number; points: number[]; bar: number[]; off: number[]; dice: number[]; remaining: number[];
  lastTurn: { seat: number; dice: number[] } | null;
  legalActions: { canRoll: boolean; canPass: boolean; moves: Move[] } | null;
};

const button = (page: Page, name: string | RegExp) => page.getByRole("button", { name, exact: true });
const point = (page: Page, index: number) => button(page, new RegExp(`^Point ${index}\\b`, "i"));
const die = (page: Page, index: number, face: number) => button(page, `Die ${index + 1}: ${face}`);
const api = (base: string, game: DiceGame) => new URL(`api/${game}`, base).href;
const tableUrl = (base: string, game: DiceGame, table: { id: string }) => `${api(base, game)}/tables/${table.id}`;
const status = (page: Page, text: string | RegExp) => page.getByRole("status").filter({ hasText: text });

async function payload<T>(response: Response): Promise<T> {
  const raw = await response.text();
  expect(response.status(), `${new URL(response.url()).pathname}: ${raw}`).toBe(200);
  return JSON.parse(raw) as T;
}

async function create<P>(page: Page, base: string, game: DiceGame, bots: boolean) {
  await page.goto(new URL(`${game}/`, base).href);
  await page.getByRole("textbox", { name: "Display name" }).fill("Cedar");
  const response = receive(page, `${api(base, game)}/tables`, "POST");
  await button(page, bots ? "Play a bot" : "Invite a friend").tap();
  const table = await payload<Table<P>>(await response);
  expect(table).toMatchObject({ status: bots ? "playing" : "waiting", seat: 0 });
  expect(table.seats[1]).toMatchObject({ occupied: bots, bot: bots });
  return table;
}

async function invite<P>(pages: readonly [Page, Page], base: string, game: DiceGame) {
  const [host, friend] = pages;
  const waiting = await create<P>(host, base, game, false);
  await expect(status(host, "Waiting for players")).toBeVisible();
  const link = host.getByLabel("Invite link", { exact: true });
  await expect(link).not.toHaveValue("");
  const invitation = new URL(await link.inputValue());
  expect(invitation.href).toBe(new URL(`${game}/#invite=${waiting.invite}`, base).href);
  const bare = new URL(invitation.href);
  bare.pathname = bare.pathname.slice(0, -1);
  bare.search = "?from=invite";
  invitation.search = bare.search;
  await friend.goto(bare.href);
  await expect(friend).toHaveURL(invitation.href);
  await friend.getByRole("textbox", { name: "Display name" }).fill("Pine");
  const response = receive(friend, `${api(base, game)}/join`, "POST");
  await button(friend, "Join table").tap();
  const joined = await payload<Table<P>>(await response);
  expect(joined).toMatchObject({ id: waiting.id, seat: 1, status: "playing", invite: null });
  expect(joined.seats.map(seat => seat.name)).toEqual(["Cedar", "Pine"]);
  invitation.hash = "";
  await expect(friend).toHaveURL(invitation.href);
  await expect(status(host, "Waiting for players")).toBeHidden();
  return joined;
}

async function act<P>(table: Table<P>, url: string, page: Page, control: Locator, action: object) {
  const pending = receive(page, `${url}/actions`, "POST");
  await control.tap();
  const response = await pending;
  expect(response.request().postDataJSON()).toEqual({ revision: table.revision, action });
  const after = await payload<Table<P>>(response);
  expect(after).toMatchObject({ id: table.id, revision: table.revision + 1, seat: table.seat });
  return after;
}

async function reload<P>(page: Page, url: string, table: Table<P>) {
  const pending = receive(page, url, "GET");
  await page.reload();
  expect(await payload(await pending)).toEqual(table);
}

async function read<P>(page: Page, url: string): Promise<Table<P>> {
  const response = await page.request.get(url);
  expect(response.ok(), new URL(url).pathname).toBe(true);
  return response.json();
}

function validDice(dice: number[], count: number) {
  expect(dice).toHaveLength(count);
  for (const face of dice) expect([1, 2, 3, 4, 5, 6]).toContain(face);
}

async function keep(page: Page, url: string, table: Table<Farkle>) {
  const { choices, dice, turnPoints } = table.position;
  const choice = [...choices].sort((a, b) => b.points - a.points)[0];
  expect(choice.points).toBeGreaterThan(0);
  const indices = [...dice.keys()].filter(index => choice.mask & (1 << index));
  for (const index of indices) await die(page, index, dice[index]).tap();
  const kept = await act(table, url, page, button(page, "Keep"), { kind: "keep", dice: indices });
  expect(kept.position.turnPoints).toBe(turnPoints + choice.points);
  expect(kept.position.kept).toEqual(indices);
  await expect(page.getByLabel("Unbanked points", { exact: true })).toHaveText(String(kept.position.turnPoints));
  await expect(button(page, "Roll")).toBeEnabled();
  return kept;
}

async function moveChecker(page: Page, url: string, table: Table<Backgammon>) {
  const move = table.position.legalActions?.moves[0];
  expect(move, "The seated player should have a legal checker move").toBeDefined();
  const side = table.seat === 0 ? "Ivory" : "Umber";
  await (move!.from === 0 ? button(page, new RegExp(`^${side} bar:`)) : point(page, move!.from)).tap();
  await expect(status(page, /Choose a highlighted destination/)).toBeVisible();
  return act(table, url, page, point(page, move!.to), { kind: "move", ...move });
}

test.describe("mounted dice tables", () => {
  test.beforeEach(({ baseURL }) => {
    test.skip(!baseURL || !/^\/(?:game-)?portal\/$/.test(new URL(baseURL).pathname),
      "Requires the published portal at /portal/ or /game-portal/.");
  });

  test("Farkle rolls and scores real dice against a bot and reloads the pending keep", async ({ page, baseURL }) => {
    let table = await create<Farkle>(page, baseURL!, "farkle", true);
    const url = tableUrl(baseURL!, "farkle", table);
    let scored = false;
    for (let step = 0; step < 24 && !(scored && table.position.recentTurns.some(turn => turn.seat === 1)); step++) {
      const { canBank, canRoll, dice } = table.position;
      expect(table.position.turn).toBe(table.seat);
      if (canBank) {
        table = await act(table, url, page, button(page, "Bank"), { kind: "bank" });
      } else if (canRoll) {
        table = await act(table, url, page, button(page, "Roll"), { kind: "roll" });
      } else {
        if (!scored) {
          validDice(dice, 6);
          await expect(die(page, 0, dice[0])).toBeVisible();
          await reload(page, url, table);
          for (const [index, face] of dice.entries()) await expect(die(page, index, face)).toBeVisible();
          await fitsPhone(page);
        }
        table = await keep(page, url, table);
        scored = true;
      }
    }
    expect(scored).toBe(true);
    const bot = table.position.recentTurns.find(turn => turn.seat === 1);
    expect(bot, "The bot should have a committed turn result").toBeDefined();
    if (bot!.kind === "banked") expect(bot!.points).toBeGreaterThan(0);
    else expect(bot!.dice.length).toBeGreaterThan(0);
    await expect(page.getByRole("list", { name: "Recent turns" }).getByText(/Bot 1/)).toBeVisible();
    await expect(button(page, "Roll")).toBeEnabled();
  });

  test("Farkle invited guests see the same roll and commit a scoring keep", async ({ page, browser, baseURL }) => {
    const context = await browser.newContext({ viewport: page.viewportSize()!, isMobile: true, hasTouch: true });
    try {
      const pages = [page, await context.newPage()] as const;
      const joined = await invite<Farkle>(pages, baseURL!, "farkle");
      const url = tableUrl(baseURL!, "farkle", joined);
      let turn = joined.position.turn;
      for (let attempt = 0; attempt < 8; attempt++) {
        const actor = pages[turn];
        const rival = pages[1 - turn];
        await expect(actor.getByRole("heading", { name: "Your turn", exact: true })).toBeVisible();
        const before = await read<Farkle>(actor, url);
        const rolled = await act(before, url, actor, button(actor, "Roll"), { kind: "roll" });
        if (rolled.position.phase === "keep") {
          validDice(rolled.position.dice, 6);
          for (const [index, face] of rolled.position.dice.entries()) await expect(die(rival, index, face)).toBeVisible();
          await expect(button(rival, "Keep")).toBeDisabled();
          const kept = await keep(actor, url, rolled);
          expect((await read<Farkle>(rival, url)).position).toMatchObject({
            turnPoints: kept.position.turnPoints, kept: kept.position.kept, choices: [],
          });
          return;
        }
        expect(rolled.position.turn).toBe(1 - turn);
        turn = rolled.position.turn;
      }
      throw new Error("Eight server-rolled Farkle turns did not offer a scoring keep.");
    } finally {
      await context.close();
    }
  });

  test("Backgammon moves real checkers against a bot and restores the board on reload", async ({ page, baseURL }) => {
    let table = await create<Backgammon>(page, baseURL!, "backgammon", true);
    const url = tableUrl(baseURL!, "backgammon", table);
    const start = table.position.points;
    let moved = false;
    const botReplied = () => moved && table.position.lastTurn?.seat === 1 && table.position.legalActions?.canRoll;
    for (let step = 0; step < 12 && !botReplied(); step++) {
      expect(table.position.turn).toBe(table.seat);
      const legal = table.position.legalActions!;
      if (legal.canRoll) {
        table = await act(table, url, page, button(page, "Roll dice"), { kind: "roll" });
      } else if (legal.canPass) {
        table = await act(table, url, page, button(page, "Pass turn"), { kind: "pass" });
      } else {
        table = await moveChecker(page, url, table);
        moved = true;
      }
    }
    expect(moved).toBe(true);
    expect(table.position.points).not.toEqual(start);
    expect(table.position.lastTurn?.seat).toBe(1);
    expect(table.position.turn).toBe(table.seat);
    await expect(page.getByText(/^Bot 1 rolled /)).toBeVisible();
    for (const [seat, sign] of [1, -1].entries()) {
      const onBoard = table.position.points.reduce((sum, count) => sum + Math.max(0, count * sign), 0);
      expect(onBoard + table.position.bar[seat] + table.position.off[seat]).toBe(15);
    }
    await expect(button(page, "Roll dice")).toBeEnabled();
    const changed = start.findIndex((count, index) => count !== table.position.points[index]) + 1;
    await expect(point(page, changed)).toBeVisible();
    const label = await point(page, changed).getAttribute("aria-label");
    await reload(page, url, table);
    await expect(point(page, changed)).toHaveAccessibleName(label!);
    await expect(button(page, "Roll dice")).toBeEnabled();
    await fitsPhone(page);
  });

  test("Backgammon invited guests share a checker move and keep the unused die on reload", async ({ page, browser, baseURL }) => {
    const context = await browser.newContext({ viewport: page.viewportSize()!, isMobile: true, hasTouch: true });
    try {
      const pages = [page, await context.newPage()] as const;
      const joined = await invite<Backgammon>(pages, baseURL!, "backgammon");
      const url = tableUrl(baseURL!, "backgammon", joined);
      const actor = pages[joined.position.turn];
      const rival = pages[1 - joined.position.turn];
      const before = await read<Backgammon>(actor, url);
      validDice(before.position.dice, 2);
      const move = before.position.legalActions!.moves[0];
      const destination = point(rival, move.to);
      await expect(destination).toBeVisible();
      const beforeLabel = await destination.getAttribute("aria-label");
      const after = await moveChecker(actor, url, before);
      const sign = before.seat === 0 ? 1 : -1;
      const points = [...before.position.points];
      points[move.from - 1] -= sign;
      points[move.to - 1] += sign;
      expect(after.position.points).toEqual(points);
      expect(after.position.remaining).toEqual(before.position.remaining.filter(face => face !== move.die));
      await expect(destination).not.toHaveAccessibleName(beforeLabel!);
      expect((await read<Backgammon>(rival, url)).position).toMatchObject({
        points: after.position.points, dice: after.position.dice, remaining: after.position.remaining, legalActions: null,
      });
      await reload(actor, url, after);
      await expect(actor.getByRole("group", { name: `Rolled dice: ${after.position.dice.join(" and ")}` })).toBeVisible();
      await expect(actor.getByText(`To play: ${after.position.remaining[0]}`, { exact: true })).toBeVisible();
      await expect(point(actor, move.to)).toBeVisible();
      await expect(point(actor, after.position.legalActions!.moves[0].from)).toBeEnabled();
    } finally {
      await context.close();
    }
  });
});
