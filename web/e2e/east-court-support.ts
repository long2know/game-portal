import { expect } from "@playwright/test";
import type { Locator, Page, TestInfo } from "@playwright/test";
import { mahjongSolitaireProgressKey } from "../src/portal/games/mahjong-solitaire-save";
import { MahjongStack } from "../src/portal/games/mahjong-stack";
import { fitsPhone } from "./portal-support";

export async function checkLattice(board: Locator, game: "xiangqi" | "gomoku") {
  const [rows, columns] = game === "xiangqi" ? [10, 9] : [15, 15];
  await expect(board.locator("[data-cell]")).toHaveCount(rows * columns);
  const geometry = await board.evaluate((node, { rows, columns, game }) => {
    const art = node.querySelector<SVGSVGElement>(".lattice")!;
    const matrix = art.getScreenCTM()!;
    const distances = [...node.querySelectorAll<HTMLElement>("[data-cell]")].map(cell => {
      const { x, y, width, height } = cell.getBoundingClientRect();
      const center = new DOMPoint(Number(cell.dataset.column) + .5, Number(cell.dataset.row) + .5).matrixTransform(matrix);
      return Math.hypot(center.x - x - width / 2, center.y - y - height / 2);
    });
    const drawn = (selector: string, x: number, y: number) => {
      const path = art.querySelector<SVGPathElement>(selector)!;
      const length = path.getTotalLength();
      for (let i = 0; i <= length; i += .035) {
        const point = path.getPointAtLength(i);
        if (Math.hypot(point.x - x, point.y - y) < .045) return true;
      }
      return false;
    };
    const paths = game === "xiangqi" ? [
      drawn(".xiangqi-files", 4.5, 3.5), drawn(".xiangqi-files", 4.5, 5),
      drawn(".xiangqi-files", .5, 5), drawn(".xiangqi-palaces", 4.5, 1.5), drawn(".xiangqi-palaces", 4.5, 8.5),
    ] : [
      drawn("path", .5, rows - .5), drawn("path", columns - .5, 7.5), drawn("path", 7.5, .5),
    ];
    return { maxOffset: Math.max(...distances), paths, surface: getComputedStyle(node).backgroundImage };
  }, { rows, columns, game });
  expect(geometry.maxOffset, "hit targets stay centered on the lattice").toBeLessThan(.8);
  expect(geometry.paths).toEqual(game === "xiangqi" ? [true, false, true, true, true] : [true, true, true]);
  expect(geometry.surface).toMatch(new RegExp(`${game}-board-[\\w-]+\\.svg`));
}

export async function paintedUses(parent: Locator, selector: string, count: number) {
  await expect.poll(() => parent.locator(selector).evaluateAll(uses => uses.filter(node => {
    const { width, height } = (node as SVGGraphicsElement).getBBox();
    return width > 0 && height > 0;
  }).length)).toBe(count);
}

export async function exerciseStack(page: Page, address: URL, info: TestInfo) {
  const mount = address.pathname.slice(0, -"mahjong-solitaire/".length);
  const key = mahjongSolitaireProgressKey(mount);
  const otherKey = mahjongSolitaireProgressKey(mount === "/portal/" ? "/game-portal/" : "/portal/");
  const untouched = new MahjongStack(328603).save();
  await page.setViewportSize({ width: 320, height: 730 });
  await page.emulateMedia({ reducedMotion: "reduce" });
  await page.addInitScript(({ mount, key, otherKey, untouched }) => {
    if (sessionStorage.getItem("tested-stack")) return;
    sessionStorage.setItem("tested-stack", "1");
    localStorage.setItem(otherKey, "unrelated");
    localStorage.setItem(`${mount}court-music`, "pending-on");
    localStorage.setItem(`${mount}court-availability-change`, "+1");
    localStorage.setItem(key, untouched);
  }, { mount, key, otherKey, untouched });
  const api: URL[] = [];
  const artwork = new Set<string>();
  const errors: string[] = [];
  page.on("request", event => {
    const url = new URL(event.url());
    if (url.pathname.includes("/api/")) api.push(url);
    if (url.pathname.endsWith(".svg")) artwork.add(url.href);
  });
  page.on("pageerror", error => errors.push(error.message));

  await page.goto(address.href);
  await expect(page.getByRole("navigation").getByRole("link", { name: "All games" })).toHaveAttribute("href", mount);
  await expect(page.getByRole("heading", { name: "Mahjong Solitaire", exact: true })).toBeVisible();
  const game = page.locator(".mahjong-solitaire");
  const board = game.getByRole("group", { name: "Lantern stack tiles" });
  const count = game.locator(".stack-count");
  const hint = game.getByRole("button", { name: "Hint", exact: true });
  const undo = game.getByRole("button", { name: "Undo", exact: true });
  const action = game.getByRole("status");
  const read = () => page.evaluate(key => localStorage.getItem(key), key);
  await expect(count).toHaveText("78 / 78 tiles left");
  await fitsPhone(page);
  await expect(board).toBeInViewport({ ratio: .99 });
  await expect.poll(() => board.evaluate(node => getComputedStyle(node).backgroundImage)).toMatch(/solitaire-well-[\w-]+\.svg/);
  expect(await board.locator(".stack-tile:visible").count()).toBe(78);
  for (const selector of ['[data-level="0"]', '[data-level="1"]', '[data-level="2"]', '[data-level="3"]']) {
    await expect(board.locator(selector).first()).toBeVisible();
  }
  await paintedUses(board, "svg.tile-art use", 78);
  const faceHrefs = await board.locator("svg.tile-art use").evaluateAll(uses => uses.map(use => use.getAttribute("href")!));
  expect(new Set(faceHrefs.map(href => href.split("#")[0])).size).toBe(1);
  const sheet = new URL(faceHrefs[0], address);
  expect(sheet.origin).toBe(address.origin);
  expect(sheet.pathname).toMatch(new RegExp(`^${mount}assets/tile-suits-[\\w-]+\\.svg$`));
  expect(artwork.has(`${sheet.origin}${sheet.pathname}`)).toBe(true);
  expect(new Set(faceHrefs.map(href => href.split("#")[1])).size).toBe(27);

  await hint.tap();
  const marked = board.locator("button[data-hinted]");
  await expect(marked).toHaveCount(2);
  await expect(marked.first()).toHaveCSS("cursor", "pointer");
  const first = board.locator("[data-hinted]:focus");
  const firstId = await first.getAttribute("data-tile");
  const pair = await marked.evaluateAll(nodes => nodes.map(node => (node as HTMLElement).dataset.tile!));
  expect(pair).toContain(firstId);
  const partner = board.locator(`[data-tile="${pair.find(id => id !== firstId)}"]`);
  const selected = board.locator(`[data-tile="${firstId}"]`);
  expect(await read()).toBe(untouched);
  await page.keyboard.press((await selected.boundingBox())!.x < (await partner.boundingBox())!.x ? "ArrowRight" : "ArrowLeft");
  await expect(partner).toBeFocused();
  await selected.focus();
  await page.keyboard.press("Enter");
  await expect(selected).toHaveAttribute("aria-pressed", "true");
  expect(await read()).toBe(untouched);

  const boxed = board.locator('[data-state="boxed"]').first();
  await expect(boxed).toHaveAccessibleName(/blocked on both sides$/);
  await expect(boxed).toHaveAttribute("aria-disabled", "true");
  const target = await boxed.evaluate(node => {
    const { x, y, width, height } = node.getBoundingClientRect();
    const px = x + width / 2, py = y + height / 2;
    return document.elementFromPoint(px, py)?.closest("button") === node ? { x: px, y: py } : null;
  });
  expect(target, "a visibly boxed bone tile remains a real hit target for feedback").not.toBeNull();
  await page.touchscreen.tap(target!.x, target!.y);
  await expect(action).toHaveText("That tile needs an open side.");
  await expect(selected).toHaveAttribute("aria-pressed", "true");
  await expect(count).toHaveText("78 / 78 tiles left");
  await page.screenshot({ path: info.outputPath("mahjong-solitaire-320-selected.png") });

  await partner.tap();
  await expect(count).toHaveText("76 / 78 tiles left");
  await expect(selected).toBeHidden();
  await expect(partner).toBeHidden();
  const matched = await read();
  expect(matched).not.toBe(untouched);
  await page.goto(`${address.href}?fresh=1`);
  const dialog = page.getByRole("dialog", { name: "New game?" });
  await expect(dialog.getByRole("button", { name: "Cancel" })).toBeFocused();
  expect(await read()).toBe(matched);
  await page.keyboard.press("Escape");
  await expect(count).toHaveText("76 / 78 tiles left");
  await expect(undo).toBeEnabled();
  await undo.tap();
  await expect(count).toHaveText("78 / 78 tiles left");
  await expect(selected).toBeVisible();
  await expect(partner).toBeVisible();
  expect(await read()).toBe(untouched);

  await page.setViewportSize({ width: 740, height: 320 });
  await page.reload();
  await expect(count).toHaveText("78 / 78 tiles left");
  await expect.poll(() => page.evaluate(() => document.body.getBoundingClientRect().width)).toBe(740);
  expect(await read()).toBe(untouched);
  await expect(board).toBeInViewport({ ratio: .99 });
  await fitsPhone(page);
  for (const control of await game.locator(".stack-actions button").all()) {
    expect((await control.boundingBox())!.height).toBeGreaterThanOrEqual(44);
    await expect(control).toBeInViewport();
  }
  const top = await board.locator('[data-level="3"]').first().boundingBox();
  expect(top!.width).toBeGreaterThanOrEqual(20);
  await page.screenshot({ path: info.outputPath("mahjong-solitaire-740x320.png") });

  await game.getByRole("button", { name: "New game", exact: true }).click();
  await expect(dialog.getByRole("button", { name: "New game" })).toBeInViewport();
  await dialog.getByRole("button", { name: "New game" }).click();
  await expect(action).toHaveText("A new solvable deal is ready.");
  const fresh = JSON.parse((await read())!);
  expect(fresh).toMatchObject({ version: 1, seed: expect.any(Number), moves: [] });
  expect(fresh.seed).not.toBe(328603);
  await page.reload();
  await expect(count).toHaveText("78 / 78 tiles left");
  expect(JSON.parse((await read())!)).toEqual(fresh);
  expect(await page.evaluate(key => localStorage.getItem(key), otherKey)).toBe("unrelated");
  if (info.project.name === "chromium") {
    const tile = board.locator('[data-state="free"]').first();
    const width = (await tile.boundingBox())!.width;
    await page.emulateMedia({ forcedColors: "active" });
    expect(await page.evaluate(() => matchMedia("(forced-colors: active)").matches)).toBe(true);
    await tile.focus();
    await page.keyboard.press("Enter");
    await expect(tile).toHaveAttribute("aria-pressed", "true");
    await expect(tile.locator(".tile-face")).toHaveCSS("border-top-width", "2px");
    expect((await tile.boundingBox())!.width).toBe(width);
  }
  expect(api.map(url => url.pathname)).toEqual([]);
  expect(errors).toEqual([]);
}
