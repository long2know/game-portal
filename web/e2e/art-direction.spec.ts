import { expect, test } from "@playwright/test";
import { fitsPhone, tap } from "./portal-support";

test.beforeEach(async ({ baseURL }) => {
  test.skip(!baseURL || !/^\/(?:game-)?portal\/$/.test(new URL(baseURL).pathname), "Requires the mounted portal.");
});

test("Go marks the latest stone without covering the unplayed intersections", async ({ page, baseURL }) => {
  await page.setViewportSize({ width: 320, height: 740 });
  await page.emulateMedia({ reducedMotion: "reduce" });
  await page.goto(new URL("go/", baseURL).href);
  await tap(page, "Play a bot");
  const board = page.locator(".board-grid");
  await board.locator(".square[data-target]").first().tap();
  const latest = board.locator(".square[data-last] .token");
  const previous = board.locator(".square:not([data-last]) .token");
  const legal = board.locator(".square[data-target]").first();
  await expect(latest).toHaveCount(1);
  await expect(previous).toHaveCount(1); // The bot has replied, so the original move is no longer marked.
  expect(await latest.evaluate(stone => {
    const ring = getComputedStyle(stone, "::before");
    return ring.content !== "none" && ring.borderStyle === "solid" && parseFloat(ring.borderWidth) > 0;
  })).toBe(true);
  expect(await previous.evaluate(stone => getComputedStyle(stone, "::before").content)).toBe("none");
  const aimOpacity = () => legal.evaluate(square => Number(getComputedStyle(square, "::after").opacity));
  expect(await aimOpacity()).toBe(0);
  await page.keyboard.press("Tab");
  await legal.focus();
  await expect(legal).toBeFocused();
  expect(await aimOpacity()).toBeGreaterThan(0);
  await page.addStyleTag({ content: `
    .board-frame[data-unframed]::before { display: none; }
    .board-frame[data-unframed], .board-frame[data-unframed] .board-scroll { box-shadow: none; }
  ` });
  const rail = await page.locator(".board-frame").evaluate(frame => {
    const target = frame.querySelector(".square[data-target]:focus")!;
    const inspect = () => {
      const square = target.getBoundingClientRect();
      const field = frame.querySelector(".board-grid")!.getBoundingClientRect();
      const mark = getComputedStyle(frame, "::before");
      return {
        finish: { rail: getComputedStyle(frame).boxShadow, mark: mark.clipPath, display: mark.display },
        markGap: field.top - frame.getBoundingClientRect().top - parseFloat(getComputedStyle(frame).borderTopWidth)
          - parseFloat(mark.top) - parseFloat(mark.height),
        field: field.toJSON(),
        square: square.toJSON(),
        hit: document.elementFromPoint(square.x + square.width / 2, square.y + square.height / 2)?.closest(".square") === target,
        focus: getComputedStyle(target).outlineStyle,
        aim: getComputedStyle(target, "::after").opacity,
      };
    };
    const { finish, markGap, ...play } = inspect();
    frame.setAttribute("data-unframed", "");
    const { finish: bare, markGap: _, ...barePlay } = inspect();
    frame.removeAttribute("data-unframed");
    return { finish, bare, markGap, play, barePlay };
  });
  expect(rail.finish.rail).not.toBe(rail.bare.rail);
  expect(rail.finish.mark).not.toBe("none");
  expect(rail.finish.display).not.toBe(rail.bare.display);
  expect(rail.markGap).toBeGreaterThan(0);
  expect(rail.play).toEqual(rail.barePlay);
  expect(rail.play).toMatchObject({ hit: true, focus: "dashed", aim: "1" });
  await fitsPhone(page);
});

test("Sudoku's printed guide follows tap and keyboard selection without moving the cells", async ({ page, baseURL }) => {
  await page.setViewportSize({ width: 320, height: 740 });
  await page.goto(new URL("sudoku/", baseURL).href);
  await tap(page, "Start puzzle");
  const cell = (index: number) => page.locator(`.sudoku-cell[data-index="${index}"]`);
  const center = cell(40);
  await cell(0).tap();
  await expect(cell(0)).toHaveAttribute("aria-pressed", "true");
  const bounds = await center.boundingBox();
  const paint = (index: number) => cell(index).evaluate(node => getComputedStyle(node).backgroundColor);
  const [above, left, diagonal] = await Promise.all([paint(31), paint(39), paint(30)]);
  await center.tap();
  await expect(center).toHaveAttribute("aria-pressed", "true");
  expect(await paint(31)).not.toBe(above);
  expect(await paint(39)).not.toBe(left);
  expect(await paint(30)).toBe(diagonal);
  expect(await center.boundingBox()).toEqual(bounds);
  await center.focus();
  await page.keyboard.press("ArrowRight");
  await expect(cell(41)).toBeFocused();
  await expect(cell(41)).toHaveAttribute("aria-pressed", "true");
  expect(await paint(31)).toBe(above);
  expect(await paint(39)).not.toBe(left);
  await fitsPhone(page);
});

test("the portal Mahjong finish is fetched with the Hall and leaves the landscape hand playable", async ({ page, baseURL }) => {
  await page.setViewportSize({ width: 844, height: 390 });
  await page.emulateMedia({ reducedMotion: "reduce" });
  const hallStyles: number[] = [];
  page.on("response", response => {
    if (/\/assets\/actors-[\w-]+\.css$/.test(new URL(response.url()).pathname)) hallStyles.push(response.status());
  });
  await page.goto(new URL("mahjong/", baseURL).href);
  await expect(page.locator('.connection[data-status="online"]')).toBeVisible();
  expect(hallStyles).toEqual([]);
  await tap(page, "Play with bots");
  await tap(page, "Mark ready");
  await tap(page, "Start match");
  await expect(page.locator(".hall-dial-plate")).toBeVisible();
  expect(hallStyles).toEqual([200]);
  const paint = await page.locator(".board-plane").evaluate(node => {
    const app = node.closest("#app")!;
    const well = node.querySelector(".board-well")!;
    const inspect = () => ({
      baize: getComputedStyle(well).backgroundImage,
      rail: getComputedStyle(node, "::before").borderColor,
      register: getComputedStyle(node, "::after").content,
      wellLip: getComputedStyle(well).boxShadow,
      projection: [node, well, ...node.querySelectorAll(".plane-corner")].map(part => part.getBoundingClientRect().toJSON()),
      cast: [...app.querySelectorAll<SVGSVGElement>(".seat-actor")].map(actor =>
        ["[data-joint=head] > g > path", "[data-joint=body] > g > path"].map(selector => {
          const fill = getComputedStyle(actor.querySelector(selector)!).fill;
          const id = fill.match(/#([^"')]+)/)?.[1];
          return id ? document.getElementById(id)?.localName : undefined;
        })),
      roomHasHalo: getComputedStyle(document.body, "::before").backgroundImage.includes("radial-gradient("),
    });
    const portal = inspect();
    app.removeAttribute("data-portal-mahjong");
    try { return { portal, standalone: inspect() }; }
    finally { app.setAttribute("data-portal-mahjong", ""); }
  });
  expect(paint.portal.baize).toMatch(/^url\(/);
  expect(paint.portal.baize).not.toContain("gradient(");
  expect(paint.portal.roomHasHalo).toBe(false);
  expect(paint.portal.cast).toEqual(Array.from({ length: 4 }, () => ["pattern", "pattern"]));
  expect(paint.portal.register).not.toBe("none");
  expect(paint.portal.rail).not.toBe(paint.standalone.rail);
  expect(paint.portal.wellLip).not.toBe(paint.standalone.wellLip);
  expect(paint.portal.projection).toEqual(paint.standalone.projection);
  expect(paint.standalone.register).toBe("none");
  expect(paint.standalone.baize).toContain("radial-gradient(");
  expect(paint.standalone.cast).toEqual(Array.from({ length: 4 }, () => ["linearGradient", "linearGradient"]));
  const tile = page.locator('.hand-tile[aria-disabled="false"]').first();
  const hit = (await tile.boundingBox())!;
  expect(hit.width).toBeGreaterThanOrEqual(44);
  expect(hit.height).toBeGreaterThanOrEqual(44);
  const face = tile.locator(".tile-face");
  const grain = await face.evaluate(node => getComputedStyle(node).backgroundImage);
  expect(grain).toMatch(/^url\(/);
  await tile.tap();
  await expect(tile).toHaveAttribute("aria-pressed", "true");
  await expect(face).toHaveCSS("background-image", grain);
  await expect(face).not.toHaveCSS("transform", "none");
  const confirm = page.getByRole("button", { name: /^Discard (?!tile$)/ });
  await expect(confirm).toBeInViewport({ ratio: 1 });
  await confirm.tap();
  await expect(page.locator(".river--near .river-tile").first()).toBeVisible();
  await fitsPhone(page);

  await page.setViewportSize({ width: 320, height: 740 });
  const status = await page.locator(".connection").boundingBox();
  const subtitle = await page.locator(".brand-kicker").boundingBox();
  const rules = await page.getByRole("button", { name: "Changsha rules", exact: true }).boundingBox();
  expect(subtitle!.x + subtitle!.width).toBeLessThan(rules!.x);
  expect(rules!.y + rules!.height).toBeLessThanOrEqual(status!.y);
  await fitsPhone(page);
});
