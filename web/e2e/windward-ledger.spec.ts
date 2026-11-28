import { expect, test, type Page } from "@playwright/test";

test.beforeEach(({ baseURL }) => {
  test.skip(!baseURL || !/^\/(?:game-)?portal\/$/.test(new URL(baseURL).pathname), "Requires the real portal server.");
});

function voyage(page: Page, baseURL: string) {
  const api = new URL("api/solo/windward-ledger", baseURL).href;
  const control = (label: string) => page.getByRole("button", { name: label, exact: true });
  const chart = page.getByRole("table", { name: "Soundings chart" });
  const cell = (label: string) => chart.getByRole("cell", { name: label, exact: true });
  let revision = 0;
  return {
    api, cell, chart, control,
    async start(touch: boolean) {
      const posted = page.waitForResponse(r => r.url() === api && r.request().method() === "POST");
      if (touch) await control("Start voyage").tap();
      else await control("Start voyage").click();
      const response = await posted;
      expect(response.status()).toBe(200);
      expect(response.request().postDataJSON()).toEqual({ variant: "soundings" });
      expect(await response.json()).toMatchObject({ game: "windward-ledger", variant: "soundings", revision: 0 });
    },
    async order(label: string, action: { kind: string; index?: number; value?: number }, input: "tap" | "click" | "keyboard" = "tap") {
      const posted = page.waitForResponse(r => r.url() === `${api}/moves` && r.request().method() === "POST");
      await expect(control(label)).toBeEnabled();
      if (input === "keyboard") await page.keyboard.press("Space");
      else await control(label)[input]();
      const response = await posted;
      expect(response.status(), label).toBe(200);
      expect(response.request().postDataJSON()).toEqual({ revision, action });
      expect(await response.json()).toMatchObject({ revision: ++revision });
    },
  };
}

test("a 320px voyage charts fog, works two ports, resumes and earns its original charter", async ({ page, baseURL }) => {
  const mount = new URL(baseURL!);
  const traffic = new Set<string>();
  const offsite: string[] = [];
  await page.route("**/*", route => {
    const url = new URL(route.request().url());
    if (url.origin === mount.origin) return route.continue();
    offsite.push(url.href);
    return route.abort();
  });
  page.on("request", request => traffic.add(new URL(request.url()).pathname));
  await page.setViewportSize({ width: 320, height: 730 });
  await page.goto(mount.href);
  await page.getByRole("link", { name: "Play Windward Ledger", exact: true }).tap();
  await expect(page).toHaveURL(new URL("windward-ledger/", mount).href);
  await expect(page.getByRole("heading", { name: "Windward Ledger", level: 1 })).toBeVisible();
  const { api, cell, chart, control, start, order } = voyage(page, mount.href);
  const sail = (direction: string, destination: string, index: number) => order(`Sail ${direction} · ${destination}`, { kind: "sail", index });
  const end = () => order("End season", { kind: "end-season" });
  const load = (name: string, value: number) => order(`Load ${name}`, { kind: "load", value });
  const make = (name: string, value: number) => order(`Make ${name}`, { kind: "assign", value });
  const found = () => order("Establish port", { kind: "found" });
  const deliver = () => order("Deliver cargo", { kind: "deliver" });
  await start(true);
  await expect(chart.locator("td[data-kind]")).toHaveCount(42);
  await expect(chart.locator("td[data-kind=fog]")).toHaveCount(38);
  await expect(cell("A4 · Lantern Dock · home dock · Cutter here").locator(".windward-cutter path"))
    .toHaveAttribute("d", "M4 17h17l-4 4H8zM13 14V3L5 14zm3-7 4 7h-4");
  await expect(cell("B3 · Uncharted")).toBeVisible();
  await expect(control("Maritime cues")).toHaveAttribute("aria-pressed", "false");
  await expect(control("Deliver cargo")).toBeDisabled();
  await expect(control("Sail west")).toBeDisabled();
  expect(await page.evaluate(() => document.documentElement.scrollWidth)).toBe(320);
  const paper = (await chart.boundingBox())!;
  expect(paper.x).toBeGreaterThanOrEqual(0);
  expect(paper.x + paper.width).toBeLessThanOrEqual(320);
  expect(paper.width).toBeGreaterThan(240);
  await sail("east", "B4", 22);
  await expect(cell("B3 · Reed Haven · coast")).toBeVisible();
  await expect(page.getByText("Charted coasts: B3 Reed Haven (Kelp)", { exact: true })).toBeVisible();
  await expect(cell("D4 · Uncharted")).toBeVisible();
  await expect(cell("B5 · Land")).toBeVisible();
  await expect(control("Sail south · B5")).toBeDisabled();
  await sail("north", "B3", 15);
  const bounds = await Promise.all(["Sail north · B2", "End season", "Establish port"].map(label => control(label).boundingBox()));
  for (const bound of bounds) {
    expect(bound!.height).toBeGreaterThanOrEqual(44);
    expect(bound!.width).toBeGreaterThanOrEqual(44);
    expect(bound!.x + bound!.width).toBeLessThanOrEqual(320);
  }
  await found();
  await expect(cell("B3 · Reed Haven · port · Cutter here")).toBeVisible();
  await expect(control("Load Kelp")).toBeDisabled();
  await make("Kelp", 0);
  await expect(control("Make Kelp")).toHaveAttribute("aria-pressed", "true");
  await expect(page.getByText("Kelp 0 · Dye 0 · Cedar 0 (max 4 each)", { exact: true })).toBeVisible();
  await end();
  await expect(page.getByText("Kelp 2 · Dye 0 · Cedar 0 (max 4 each)", { exact: true })).toBeVisible();
  await load("Kelp", 0);
  await expect(page.getByRole("heading", { name: "Hold · 1/4 crates" })).toBeVisible();

  await page.reload();
  await expect(cell("B3 · Reed Haven · port · Cutter here")).toBeVisible();
  await expect(page.getByText("Kelp 1 · Dye 0 · Cedar 0 (max 4 each)", { exact: true })).toBeVisible();
  await expect(page.getByText("Hold 1 · Delivered 0 · 2 pt each", { exact: true })).toBeVisible();
  await page.getByRole("link", { name: "‹ All games", exact: true }).tap();
  await page.getByRole("link", { name: "Continue Windward Ledger", exact: true }).tap();
  await expect(page.getByText("Season 2 of 22", { exact: true })).toBeVisible();
  await expect(cell("B3 · Reed Haven · port · Cutter here")).toBeVisible();

  await load("Kelp", 0);
  await sail("south", "B4", 22);
  await sail("west", "A4", 21);
  await expect(control("Deliver cargo")).toBeDisabled();
  await end();
  await deliver();
  await expect(page.getByText("4 / 18 charter points", { exact: true })).toBeVisible();
  await sail("east", "B4", 22);
  await sail("east", "C4", 23);
  await end();
  await sail("east", "D4", 24);
  await found();
  await make("Dye", 1);
  await end();
  await expect(page.getByRole("heading", { name: "Ports · 2/2" })).toBeVisible();
  await expect(page.getByText("Kelp 0 · Dye 2 · Cedar 0 (max 4 each)", { exact: true })).toBeVisible();
  await load("Dye", 1);
  await load("Dye", 1);
  await sail("west", "C4", 23);
  await sail("west", "B4", 22);
  await end();
  await sail("west", "A4", 21);
  await deliver();
  await expect(page.getByText("10 / 18 charter points", { exact: true })).toBeVisible();
  await sail("east", "B4", 22);
  await end();
  await sail("north", "B3", 15);
  await make("Cedar", 2);
  await end();
  await load("Cedar", 2);
  await end();
  await load("Cedar", 2);
  await sail("south", "B4", 22);
  await sail("west", "A4", 21);
  await deliver();
  await expect(page.getByText("18 / 18 charter points", { exact: true })).toBeVisible();
  await expect(page.getByText("Charter complete. Welcome home, captain!", { exact: true })).toBeVisible();
  await expect(control("End season")).toBeDisabled();
  expect(await (await page.request.get(api)).json()).toMatchObject({
    table: { position: { status: "won", season: 9, shipIndex: 21, legalActions: [],
      goods: [{ delivered: 2, cargo: 0 }, { delivered: 2, cargo: 0 }, { delivered: 2, cargo: 0 }] } },
  });

  await control("Restart voyage").tap();
  const confirmation = page.getByRole("dialog", { name: "Restart voyage?" });
  await confirmation.getByRole("button", { name: "Cancel", exact: true }).tap();
  await expect(page.getByText("18 / 18 charter points", { exact: true })).toBeVisible();
  await control("Restart voyage").tap();
  await confirmation.getByRole("button", { name: "Restart voyage", exact: true }).tap();
  await expect(page.getByText("Season 1 of 22", { exact: true })).toBeVisible();
  await expect(page.getByRole("heading", { name: "Ports · 0/2" })).toBeVisible();
  await expect(chart.locator("td[data-kind=fog]")).toHaveCount(38);
  expect(offsite).toEqual([]);
  expect([...traffic]).toEqual(expect.arrayContaining([
    expect.stringMatching(/\/assets\/windward-ledger-[\w-]+\.js$/),
    expect.stringMatching(/\/assets\/windward-ledger-[\w-]+\.css$/),
  ]));
  expect([...traffic].filter(path => /\.(wav|mp3|ogg)$/.test(path))).toEqual([]);
});

test.describe("desktop voyage", () => {
  test.use({ viewport: { width: 1280, height: 900 }, isMobile: false, hasTouch: false, reducedMotion: "reduce" });

  test("the chart and hold sit side by side and keyboard orders complete a real elapsed charter", async ({ page, baseURL }) => {
    await page.goto(new URL("windward-ledger/", baseURL!).href);
    const { api, cell, chart, control, start, order } = voyage(page, baseURL!);
    await start(false);
    const paper = (await chart.boundingBox())!;
    const hold = (await page.getByRole("heading", { name: "Hold · 0/4 crates" }).boundingBox())!;
    expect(hold.x).toBeGreaterThan(paper.x + paper.width);
    expect(await page.evaluate(() => document.documentElement.scrollWidth)).toBe(1280);
    const east = control("Sail east · B4");
    await control("Sail north · A3").focus();
    await page.keyboard.press("Tab");
    await expect(east).toBeFocused();
    await order("Sail east · B4", { kind: "sail", index: 22 }, "keyboard");
    await expect(cell("B4 · Open water · Cutter here")).toBeVisible();
    await expect(control("Sail east · C4")).toBeFocused();
    const south = control("Sail south · B5");
    await expect(south).toBeDisabled();
    await south.press("Enter");
    expect(await (await page.request.get(api)).json()).toMatchObject({ table: { revision: 1, position: { shipIndex: 22 } } });

    for (let season = 1; season <= 22; season++) {
      await expect(page.getByText(`Season ${season} of 22`, { exact: true })).toBeVisible();
      await order("End season", { kind: "end-season" }, "click");
    }
    await expect(page.getByText("Voyage ended. The charter expired before the goal was met.", { exact: true })).toBeVisible();
    await expect(control("End season")).toBeDisabled();
    expect(await (await page.request.get(api)).json()).toMatchObject({
      table: { revision: 23, position: { status: "lost", season: 22, charterPoints: 0, shipIndex: 22, legalActions: [] } },
    });
  });
});
