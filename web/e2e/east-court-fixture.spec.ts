import { readFileSync } from "node:fs";
import { fileURLToPath } from "node:url";
import { expect, test } from "@playwright/test";
import type { Page, Route } from "@playwright/test";
import type { Cell, Move, Table } from "../src/portal/api";
import { MahjongStack } from "../src/portal/games/mahjong-stack";
import { checkLattice, exerciseStack, paintedUses } from "./east-court-support";
import { fitsPhone } from "./portal-support";

const port = process.env.MAHJONG_GEOMETRY_PORT ?? 4179;
const fixture = new URL(`http://127.0.0.1:${port}/east-court-fixture/`);

async function serveBuild(page: Page, api?: (route: Route, path: string) => Promise<void>) {
  const dist = new URL("../dist/", import.meta.url);
  const html = readFileSync(new URL("portal.html", dist), "utf8")
    .replace('data-base="/"', `data-base="${fixture.pathname}"`).replaceAll("./assets/", `${fixture.pathname}assets/`);
  await page.route(`${fixture.href}**`, async route => {
    const path = new URL(route.request().url()).pathname.slice(fixture.pathname.length);
    if (/^assets\/[\w-]+\.(?:css|js|svg)$/.test(path)) {
      await route.fulfill({ path: fileURLToPath(new URL(path, dist)) });
    } else if (route.request().isNavigationRequest()) {
      await route.fulfill({ contentType: "text/html", body: html });
    } else if (api && path.startsWith("api/")) {
      await api(route, path);
    } else await route.abort();
  });
}

type Tile = MahjongStack["tiles"][number];
const available = (tile: Tile, board: readonly Tile[]) => {
  const parallel = board.filter(other => other.y < tile.y + 2 && other.y + 2 > tile.y);
  return !parallel.some(other => other.level > tile.level && other.x < tile.x + 2 && other.x + 2 > tile.x) &&
    [-2, 2].some(side => !parallel.some(other => other.level === tile.level && other.x === tile.x + side));
};

test("the seeded Lantern Stack witness removes only geometrically free matching pairs", () => {
  for (const seed of [0, 1, 42, 631, 328603, 0x7fffffff, 0xffffffff]) {
    const stack = new MahjongStack(seed);
    expect(stack.tiles).toEqual(new MahjongStack(seed).tiles);
    expect(stack.tiles.length).toBe(78);
    expect(new Set(stack.tiles.map(tile => tile.level))).toEqual(new Set([0, 1, 2, 3]));
    let remaining = [...stack.tiles];
    for (const [a, b] of stack.witness) {
      expect(stack.tiles[a].face).toBe(stack.tiles[b].face);
      expect(a).not.toBe(b);
      expect(remaining.filter(tile => available(tile, remaining)).map(({ id }) => id))
        .toEqual(remaining.filter(tile => stack.state(tile.id) === "free").map(({ id }) => id));
      expect(available(stack.tiles[a], remaining)).toBe(true);
      expect(available(stack.tiles[b], remaining)).toBe(true);
      expect(stack.match(a, b)).toBe(true);
      remaining = remaining.filter(tile => tile.id !== a && tile.id !== b);
    }
    expect(stack.remaining).toBe(0);
    expect(stack.hint(null)).toBeUndefined();
    expect(MahjongStack.restore(stack.save())?.remaining).toBe(0);
  }
});

test("a save must replay actual legal matches, not just carry plausible tile IDs", () => {
  const stack = new MahjongStack(328603);
  const [a, b] = stack.hint(null)!;
  expect(stack.hint(a)).toEqual([a, b]);
  const blocked = stack.witness.find(([x, y]) => !available(stack.tiles[x], stack.tiles) || !available(stack.tiles[y], stack.tiles))!;
  const wrong = stack.tiles.find(tile => tile.face !== stack.tiles[a].face && available(tile, stack.tiles))!.id;
  expect(stack.match(...blocked)).toBe(false);
  expect(stack.match(a, a)).toBe(false);
  expect(stack.match(a, wrong)).toBe(false);
  expect(stack.match(a, b)).toBe(true);
  const saved = stack.save();
  const parsed = JSON.parse(saved);
  for (const moves of [[blocked], [[a, wrong]], [[a, a]], [[a, b], [a, b]], [[a, 100]], [[String(a), b]]]) {
    expect(MahjongStack.restore(JSON.stringify({ ...parsed, moves }))).toBeNull();
  }
  for (const invalid of ["{", "null", JSON.stringify({ ...parsed, seed: -1 }), JSON.stringify({ ...parsed, version: 2 })]) {
    expect(MahjongStack.restore(invalid)).toBeNull();
  }
  const recovered = MahjongStack.restore(saved)!;
  expect(recovered.remaining).toBe(76);
  expect(recovered.state(a)).toBe("removed");
  expect(recovered.undo()).toEqual([a, b]);
  expect(recovered.hint(null)).toEqual([a, b]);
});

function deadEnd() {
  for (let seed = 0; seed < 32; seed++) {
    const stack = new MahjongStack(seed);
    const sorted = [...stack.tiles];
    let legal = true;
    while (legal && stack.remaining > 0) {
      const open = sorted.filter(tile => stack.state(tile.id) === "free");
      legal = open.some((tile, index) => open.slice(index + 1).some(other => stack.match(tile.id, other.id)));
    }
    if (stack.remaining > 0) return stack;
  }
  throw new Error("Fixture should find a legal but unsolved branch.");
}

test("emitted Solitaire handles actual taps, matching, restart confirmation and optional storage", async ({ page }, info) => {
  await serveBuild(page);
  await exerciseStack(page, new URL("mahjong-solitaire/", fixture), info);
});

test("emitted Solitaire recovers a dead deal, and can clear every tile with no storage", async ({ page, context }) => {
  await serveBuild(page);
  const route = new URL("mahjong-solitaire/", fixture);
  const stuck = deadEnd().save();
  await context.addInitScript(({ stuck, mount }) => {
    if (location.search === "?without-storage") {
      Object.defineProperty(window, "localStorage", { get() { throw new DOMException("Disabled", "SecurityError"); } });
    } else if (!sessionStorage.getItem("tested-dead-end")) {
      sessionStorage.setItem("tested-dead-end", "1");
      localStorage.setItem(`${mount}court-mahjong-solitaire-v1`, stuck);
    }
  }, { stuck, mount: fixture.pathname });
  await page.goto(route.href);
  const game = page.locator(".mahjong-solitaire");
  const hint = game.getByRole("button", { name: "Hint", exact: true });
  await expect(game.getByRole("status")).toHaveText(/No free pair remains/);
  await expect(hint).toBeDisabled();
  await game.getByRole("button", { name: "Undo", exact: true }).click();
  await expect(hint).toBeEnabled();
  await game.getByRole("button", { name: "Restart deal", exact: true }).click();
  await page.getByRole("dialog", { name: "Restart deal?" }).getByRole("button", { name: "Restart deal" }).click();
  expect(await page.evaluate(key => JSON.parse(localStorage.getItem(key)!), `${fixture.pathname}court-mahjong-solitaire-v1`))
    .toEqual({ version: 1, seed: JSON.parse(stuck).seed, moves: [] });

  await page.goto(`${route.href}?without-storage`);
  await expect(game.getByText("Browser storage is unavailable.", { exact: false })).toBeVisible();
  for (let i = 39; i > 0; i--) {
    await hint.click();
    const pair = await game.locator("[data-hinted]").evaluateAll(tiles => tiles.map(tile => tile.getAttribute("data-tile")));
    expect(pair).toHaveLength(2);
    for (const id of pair) await game.locator(`[data-tile="${id}"]`).click();
  }
  await expect(game.getByRole("status")).toHaveText("Stack cleared. You matched every tile!");
  await expect(game.locator(".stack-tile:visible")).toHaveCount(0);
  await expect(hint).toBeDisabled();
});

const rank = ["chariot", "horse", "elephant", "advisor", "general", "advisor", "elephant", "horse", "chariot"];

for (const [game, name] of [["xiangqi", "Xiangqi"], ["gomoku", "Gomoku"]] as const) {
  test(`emitted ${name} geometry, artwork and tap submission`, async ({ page }, info) => {
    const xiangqi = game === "xiangqi";
    const [rows, columns] = xiangqi ? [10, 9] : [15, 15];
    const cellAt = (row: number, column: number) => `${String.fromCharCode(97 + column)}${rows - row}`;
    const cells: Cell[] = [];
    for (let row = 0; row < rows; row++) {
      for (let column = 0; column < columns; column++) {
        const piece = xiangqi ? (row === 0 || row === 9 ? rank[column] :
          (row === 2 || row === 7) && (column === 1 || column === 7) ? "cannon" :
          (row === 3 || row === 6) && column % 2 === 0 ? "soldier" : null) :
          row === 7 && (column === 6 || column === 8) ? "stone" : null;
        const owner = !piece ? null : xiangqi ? Number(row < 5) : Number(column === 8);
        cells.push({ id: cellAt(row, column), row, column, owner, piece });
      }
    }
    const from = xiangqi ? "a4" : null;
    const to = xiangqi ? "a5" : "h8";
    const move: Move = { id: "fixture-opaque-id", label: `To ${to}`, from, to };
    const id = "b".repeat(32);
    const initial: Table = {
      id, game, variant: "standard", revision: 1, status: "playing", seat: 0, invite: null,
      seats: [{ seat: 0, name: "Guest", occupied: true, bot: false }, { seat: 1, name: "Bot", occupied: true, bot: true }],
      position: { rows, columns, cells, turn: 0, status: "playing", winners: [], detail: null, moves: [move], lastMove: null },
    };
    let sent: unknown;
    await serveBuild(page, async (route, path) => {
      if (path === "api/guest") await route.fulfill({ json: { displayName: "Guest", resumeRoomId: null,
        preferences: { sound: false, music: false, volume: 0, reducedMotion: true } } });
      else if (path === "api/board/games") await route.fulfill({ json: {
        games: [{ id: game, name, playerCounts: [2], variants: [{ id: "standard", label: "Standard" }] }],
      } });
      else if (path === "api/board/resume") await route.fulfill({ json: { tableId: null } });
      else if (path === "api/board/tables") await route.fulfill({ json: initial });
      else if (path === `api/board/tables/${id}/moves`) {
        sent = route.request().postDataJSON();
        await route.fulfill({ json: { ...initial, revision: 2, position: { ...initial.position, lastMove: move, turn: 1,
          moves: [], cells: cells.map(cell => cell.id === from ? { ...cell, owner: null, piece: null } :
            cell.id === to ? { ...cell, owner: 0, piece: xiangqi ? "soldier" : "stone" } : cell) } } });
      } else await route.abort();
    });
    await page.setViewportSize({ width: 320, height: 730 });
    await page.goto(new URL(`${game}/`, fixture).href);
    await page.getByRole("button", { name: "Play a bot", exact: true }).tap();
    const board = page.getByRole("group", { name: `${name} board`, exact: true });
    const square = (id: string) => board.locator(`[data-cell="${id}"]`);
    await checkLattice(board, game);
    await fitsPhone(page);
    if (xiangqi) {
      await expect(square("a10")).toHaveAccessibleName("a10, black chariot");
      await expect(square("a1")).toHaveAccessibleName("a1, red chariot");
      await paintedUses(board, ".xiangqi-seal use:not(.seal-cut)", 32);
      await square(from!).tap();
      await expect(square(from!)).toHaveAttribute("aria-pressed", "true");
    } else {
      await expect(square("g8")).toHaveAccessibleName("g8, black stone");
      await expect(square("i8")).toHaveAccessibleName("i8, white stone");
      await paintedUses(board, ".gomoku-stone use", 2);
    }
    const destination = square(to);
    await expect(destination).toHaveAttribute("data-target", "");
    await page.screenshot({ path: info.outputPath(`${game}-320.png`) });
    await destination.tap();
    await expect(destination).toHaveAccessibleName(`${to}, ${xiangqi ? "red soldier" : "black stone"}`);
    expect(sent).toEqual({ revision: 1, moveId: "fixture-opaque-id" });
    await page.setViewportSize({ width: 740, height: 320 });
    await checkLattice(board, game);
    await fitsPhone(page);
    await board.screenshot({ path: info.outputPath(`${game}-740x320.png`) });
    if (info.project.name === "chromium") {
      const path = board.locator(".lattice path").first();
      const paint = await path.evaluate(node => getComputedStyle(node).stroke);
      const width = (await destination.boundingBox())!.width;
      await page.emulateMedia({ forcedColors: "active" });
      await expect(path).not.toHaveCSS("stroke", paint);
      await expect(board).toHaveCSS("background-image", "none");
      expect((await destination.boundingBox())!.width).toBe(width);
    }
  });
}
