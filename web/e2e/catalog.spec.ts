import { readFileSync } from "node:fs";
import { gzipSync } from "node:zlib";
import { expect, test } from "@playwright/test";
import { exerciseStack } from "./east-court-support";
import { fitsPhone } from "./portal-support";

test.beforeEach(({ baseURL }) => {
  test.skip(!baseURL || !/^\/(?:game-)?portal\/$/.test(new URL(baseURL).pathname), "Requires the published portal.");
});

test("lobby presents seventeen real destinations with local, cold catalog art", async ({ page, baseURL, request }) => {
  const mount = new URL(baseURL!);
  const href = (path: string) => new URL(path, mount).href;
  const assets = new Set<string>();
  await page.route("**/*", route => new URL(route.request().url()).origin === mount.origin ? route.continue() : route.abort());
  page.on("request", event => {
    if (!event.isNavigationRequest()) assets.add(event.url());
  });
  const heroStudy = page.waitForResponse(response => /\/assets\/court-study-[\w-]+\.svg$/.test(response.url()));
  const catalogArt = page.waitForResponse(response => /\/assets\/catalog-marks-[\w-]+\.svg$/.test(response.url()));
  const html = (await page.goto(mount.href))!;
  await expect(page).toHaveTitle("Jade Court · Board Games");
  await expect(page.locator("#app")).toHaveAttribute("data-base", mount.pathname);
  await expect(page.getByRole("heading", { name: "Find your next move." })).toBeVisible();
  await expect(page.locator(".hero-copy .intro")).toHaveText("Play board games, cards, and puzzles solo, against bots, or with friends.");
  await expect(page.locator(".lobby")).not.toContainText(/quiet|room to breathe|whole crew|Table study 01/i);
  await expect(page.locator(".court-footer")).not.toContainText("A place for a good game.");
  await expect(page.getByRole("link", { name: "Mahjong table", exact: true })).not.toContainText("↗");
  const art = await catalogArt;
  expect(art.ok()).toBe(true);
  expect((await heroStudy).ok()).toBe(true);
  const games = ["mahjong", "chess", "go", "reversi", "checkers", "chinese-checkers", "holdem", "solitaire",
    "sudoku", "word-tiles", "farkle", "backgammon", "lantern-run", "xiangqi", "gomoku", "mahjong-solitaire", "big-two"];
  const cards = page.getByRole("region", { name: "Choose a game" }).getByRole("link");
  await expect(cards).toHaveCount(games.length);
  await expect(cards.filter({ hasText: "↗" })).toHaveCount(0);
  const marks = await cards.locator(".catalog-mark use").evaluateAll(uses => uses.map(use => {
    const { width, height } = (use as SVGGraphicsElement).getBBox();
    return {
      motif: new URL(use.getAttribute("href")!, use.baseURI).href,
      link: use.closest("a")!.href,
      painted: width > 0 && height > 0,
    };
  }));
  expect(marks).toEqual(games.map(game => ({ motif: `${art.url()}#${game}`, link: href(`${game}/`), painted: true })));
  const exactFaces = await page.evaluate(({ lobby, game }) => {
    const parse = (source: string) => new DOMParser().parseFromString(source, "image/svg+xml");
    const published = parse(lobby);
    const authoritative = parse(game);
    const artwork = [...published.querySelectorAll('[id^="lt-"]')];
    const faces = [...published.querySelectorAll("#mahjong use")].map(use => use.getAttribute("href"));
    return {
      faces,
      matched: artwork.filter(node => node.outerHTML === authoritative.getElementById(node.id)?.outerHTML).map(node => node.id),
    };
  }, { lobby: await art.text(), game: readFileSync(new URL("../src/tile-suits.svg", import.meta.url), "utf8") });
  expect(exactFaces).toEqual({
    faces: ["#lt-b2", "#lt-d4"],
    matched: ["lt-ink0", "lt-ink1", "lt-ink2", "lt-ink3", "lt-ink4", "lt-ink10", "lt-b2", "lt-d4"],
  });
  assets.add(href((await page.locator('link[rel="icon"]').getAttribute("href"))!));
  const lobbyAssets = [...assets].map(asset => new URL(asset));
  expect(lobbyAssets.every(asset => asset.origin === mount.origin && asset.pathname.startsWith(`${mount.pathname}assets/`))).toBe(true);
  const coldAssets = [
    expect.stringMatching(/^catalog-marks-[\w-]+\.svg$/), expect.stringMatching(/^court-study-[\w-]+\.svg$/),
    expect.stringMatching(/^jade-court-[\w-]+\.svg$/), expect.stringMatching(/^portal-[\w-]+\.css$/),
    expect.stringMatching(/^portal-[\w-]+\.js$/),
  ];
  expect(lobbyAssets).toHaveLength(coldAssets.length);
  expect(lobbyAssets.map(asset => asset.pathname.split("/").pop())).toEqual(expect.arrayContaining(coldAssets));
  const compressed = await Promise.all([...assets].map(async asset => {
    const response = await request.get(asset);
    expect(response.ok(), asset).toBe(true);
    return gzipSync(await response.body()).length;
  }));
  expect(gzipSync(await html.body()).length + compressed.reduce((a, b) => a + b, 0)).toBeLessThanOrEqual(24 * 1024);
});

test("catalog links announce the game and player context without changing the invitations", async ({ page }) => {
  await page.goto("./");
  await expect(page.getByRole("link", { name: "Take a seat", exact: true })).toHaveAccessibleDescription(
    "Changsha Mahjong Flagship · 4 players Draw and discard, claim melds, and complete a winning hand.");
  await expect(page.getByRole("link", { name: "Set up Chess", exact: true })).toHaveAccessibleDescription(
    "2 players Control the center, develop your pieces, and trap the king.");
  await expect(page.getByRole("link", { name: "Play Solitaire", exact: true })).toHaveAccessibleDescription(
    "Solo · Draw one Build four suited piles from ace to king; alternate colors in the tableau.");
  await expect(page.getByRole("link", { name: "Play Mahjong Solitaire", exact: true })).toHaveAccessibleDescription(
    "Solo · Tile matching Match free pairs from a four-tier lantern stack. Play and save in this browser.");
});

test("the mounted Mahjong Solitaire route paints and plays the local stack without creating a guest", async ({ page, baseURL, request }, info) => {
  const mount = new URL(baseURL!);
  const canonical = await request.get(new URL("mahjong-solitaire?fresh=1", mount).href, { maxRedirects: 0 });
  expect(canonical.status()).toBe(308);
  expect(canonical.headers().location).toBe(`${mount.pathname}mahjong-solitaire/?fresh=1`);
  expect((await request.get(new URL("mahjong-solitaire/private-table/", mount).href)).status()).toBe(404);
  await exerciseStack(page, new URL("mahjong-solitaire/", mount), info);
});

test("the 320px and 390px landing header fits all controls in one row with room for wider fonts", async ({ page }) => {
  await page.goto("./");
  const header = page.getByRole("banner");
  const controls = [
    header.getByRole("link", { name: "Jade Court · All games", exact: true }),
    header.getByRole("button", { name: "Music off", exact: true }),
    header.getByRole("button", { name: "Online lobby", exact: true }),
    header.getByRole("link", { name: "Mahjong table", exact: true }),
  ];
  for (const font of ["default", "wider"]) {
    if (font === "wider") {
      await page.addStyleTag({ content: `
        .header .brand-name { font-family: "DejaVu Serif", Georgia, serif; }
        .header .site-nav .button { font-family: "DejaVu Sans", system-ui, sans-serif; }
      ` });
      await page.evaluate(() => document.fonts.ready);
    }
    for (const width of [390, 320]) {
      await test.step(`${width}px, ${font} font metrics`, async () => {
        await page.setViewportSize({ width, height: 780 });
        const [brand, music, online, headerTable] = await Promise.all(controls.map(async control => (await control.boundingBox())!));
        expect(headerTable.y).toBeLessThan(brand.y + brand.height);
        let lastRight = 0;
        for (const box of [brand, music, online, headerTable]) {
          expect(box.y).toBeLessThan(brand.y + brand.height);
          expect(box.x).toBeGreaterThanOrEqual(lastRight);
          expect(Math.min(box.width, box.height)).toBeGreaterThanOrEqual(44);
          lastRight = box.x + box.width;
        }
        expect(lastRight).toBeLessThanOrEqual(width);
        await fitsPhone(page);
        await controls[2].focus();
        await page.keyboard.press("Tab");
        await expect(controls[3]).toBeFocused();
        await expect(controls[3]).toHaveCSS("outline-style", "solid");
      });
    }
  }
});

test("320px catalog keeps previews, copy and both hero actions usable", async ({ page, baseURL }) => {
  const href = (path: string) => new URL(path, baseURL).href;
  await page.setViewportSize({ width: 320, height: 740 });
  await page.goto(href(""));

  const choose = page.getByRole("link", { name: "Choose a game ↓", exact: true });
  const play = page.getByRole("link", { name: "Play Mahjong", exact: true });
  const featured = page.getByRole("link", { name: "Take a seat", exact: true });
  const chess = page.getByRole("link", { name: "Set up Chess", exact: true });
  for (const link of [choose, play, featured, chess]) {
    const { width, height } = (await link.boundingBox())!;
    expect(Math.min(width, height)).toBeGreaterThanOrEqual(44);
  }
  await expect(choose).toBeInViewport({ ratio: 1 });
  await expect(play).toBeInViewport({ ratio: 1 });
  const introInkBottom = await page.locator(".hero-copy .intro").evaluate(intro => {
    const copy = document.createRange();
    copy.selectNodeContents(intro);
    return copy.getBoundingClientRect().bottom;
  });
  const primary = (await choose.boundingBox())!;
  expect(primary.y - introInkBottom).toBeLessThan(primary.height / 2);

  const [chessCard, chessPreview, chessCopy, flagshipPreview, flagshipAction] = await Promise.all([
    chess.boundingBox(), chess.locator(".tableau").boundingBox(), chess.locator(".game-card-body").boundingBox(),
    featured.locator(".tableau").boundingBox(), featured.locator(".card-action").boundingBox(),
  ]);
  expect(chessPreview!.width).toBeGreaterThan(chessCard!.width / 5);
  expect(chessCopy!.width).toBeGreaterThan(chessPreview!.width * 2);
  const endGutter = chessCard!.x + chessCard!.width - chessCopy!.x - chessCopy!.width;
  const between = chessCopy!.x - chessPreview!.x - chessPreview!.width;
  expect(endGutter).toBeLessThanOrEqual(between + 1);
  expect(flagshipPreview!.width).toBeGreaterThan(chessPreview!.width);
  expect(flagshipPreview!.y + flagshipPreview!.height + 1).toBeGreaterThanOrEqual(flagshipAction!.y + flagshipAction!.height);

  for (const width of [320, 390, 480, 481, 639, 640, 719, 720, 999, 1000, 1440]) {
    await page.setViewportSize({ width, height: 740 });
    await fitsPhone(page);
  }
  await page.setViewportSize({ width: 320, height: 740 });
  await choose.focus();
  for (const link of [play, featured, chess]) {
    await page.keyboard.press("Tab");
    await expect(link).toBeFocused();
    await expect(link).toHaveCSS("outline-style", "solid");
  }
  await choose.tap();
  await expect(page).toHaveURL(href("#games"));
  await expect(page.getByRole("heading", { name: "Choose a game", exact: true })).toBeInViewport();
  await chess.tap();
  await expect(page).toHaveURL(href("chess/"));
  await expect(page.getByRole("heading", { name: "Chess", exact: true })).toBeVisible();
  await expect(page.getByRole("button", { name: "Play a bot", exact: true })).toBeEnabled();
  await page.getByRole("link", { name: "‹ All games", exact: true }).tap();
  await play.tap();
  await expect(page).toHaveURL(href("mahjong/"));
  await expect(page.locator(".connection")).toHaveAttribute("data-status", "online");
});

test.describe("desktop catalog artwork", () => {
  test.use({ viewport: { width: 1440, height: 900 }, isMobile: false, hasTouch: false });

  test("the feature rails and index edges paint without reframing the artwork or taking input", async ({ page }) => {
    await page.goto("./");
    const feature = page.getByRole("link", { name: "Take a seat", exact: true });
    const chess = page.getByRole("link", { name: "Set up Chess", exact: true });
    const studies = [page.locator(".hero-study"), feature.locator(".tableau"), chess.locator(".tableau")];
    await page.addStyleTag({ content: `
      [data-unframed]::before, [data-unframed]::after { display: none !important; }
    ` });
    for (const [index, study] of studies.entries()) {
      await study.scrollIntoViewIfNeeded();
      const inspect = () => study.evaluate(node => ({
        bounds: node.getBoundingClientRect().toJSON(),
        svgBounds: node.querySelector("use")?.getBoundingClientRect().toJSON(),
        crop: getComputedStyle(node).backgroundSize,
        registration: getComputedStyle(node, "::before").clipPath,
      }));
      const { registration, ...framed } = await inspect();
      expect(registration !== "none", "only the Go study and featured preview get a registration mark").toBe(index < 2);
      const painted = await study.screenshot();
      await study.evaluate(node => node.setAttribute("data-unframed", ""));
      const { registration: _, ...bare } = await inspect();
      expect(bare).toEqual(framed);
      expect((await study.screenshot()).equals(painted), "the rail must visibly paint").toBe(false);
      await study.evaluate(node => node.removeAttribute("data-unframed"));
    }

    await expect.poll(() => page.evaluate(() => matchMedia("(hover: hover) and (pointer: fine)").matches)).toBe(true);
    await feature.locator(".tableau").hover({ position: { x: 2, y: 2 } });
    await expect(feature.and(page.locator(":hover"))).toHaveCount(1);
    await feature.focus();
    await page.keyboard.press("Tab");
    await expect(chess).toBeFocused();
    await expect(chess).toHaveCSS("outline-style", "solid");
  });
});
