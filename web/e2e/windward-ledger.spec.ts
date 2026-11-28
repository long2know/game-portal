import { createHash } from "node:crypto";
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
  const entry = page.getByRole("link", { name: "Play Windward Ledger", exact: true });
  await expect(entry).toBeVisible();
  expect([...traffic].filter(path => /\/assets\/windward-/.test(path))).toEqual([]);
  await entry.tap();
  await expect(page).toHaveURL(new URL("windward-ledger/", mount).href);
  await expect(page.getByRole("heading", { name: "Windward Ledger", level: 1 })).toBeVisible();
  const { api, cell, chart, control, start, order } = voyage(page, mount.href);
  const sail = (direction: string, destination: string, index: number) => order(`Sail ${direction} · ${destination}`, { kind: "sail", index });
  const end = () => order("End season", { kind: "end-season" });
  const load = (name: string, value: number) => order(`Load ${name}`, { kind: "load", value });
  const make = (name: string, value: number) => order(`Make ${name}`, { kind: "assign", value });
  const found = () => order("Establish port", { kind: "found" });
  const deliver = () => order("Deliver cargo", { kind: "deliver" });
  const sea = page.waitForResponse(response => /\/assets\/windward-sea-[\w-]+\.png$/.test(response.url()));
  await start(true);
  const texture = await sea;
  expect(new URL(texture.url()).pathname).toMatch(new RegExp(`^${mount.pathname}assets/windward-sea-[\\w-]+\\.png$`));
  expect(texture.headers()["content-type"]).toBe("image/png");
  expect(createHash("sha256").update(await texture.body()).digest("hex"))
    .toBe("c1e4b9dddf2acb2f11153749212aada489c69c60b062ace69f4170a13137591f");
  const fill = (label: string) => cell(label).evaluate(node => getComputedStyle(node).backgroundImage);
  expect(await fill("B4 · Open water")).toContain(texture.url());
  expect(await fill("B3 · Uncharted")).not.toContain(texture.url());
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

test("Tailwind plays the credited excerpt only on tap and pauses across page lifecycle changes", async ({
  page, baseURL, request,
}) => {
  const mount = new URL(baseURL!);
  const musicKey = `${mount.pathname}court-music`;
  await page.addInitScript(key => localStorage.setItem(key, "pending-on"), musicKey);
  const traffic = new Set<string>();
  page.on("request", r => traffic.add(new URL(r.url()).pathname));
  await page.setViewportSize({ width: 320, height: 730 });
  await page.goto(new URL("windward-ledger/", mount).href);
  const { control, start } = voyage(page, mount.href);
  const media = page.getByRole("region", { name: "Windward Ledger media" });
  const title = "Tailwind (excerpt)";
  const play = control(`Play ${title}`);
  const pause = control(`Pause ${title}`);
  const resume = control(`Resume ${title}`);
  const audio = media.locator("audio");
  const playback = () => audio.evaluate(node => {
    const { paused, muted, volume, currentTime, duration, currentSrc } = node as HTMLAudioElement;
    return { paused, muted, volume, currentTime, duration, currentSrc };
  });
  await expect(play).toBeVisible();
  await expect(page.getByRole("button", { name: "Online lobby", exact: true })).toBeVisible();
  await expect(page.getByRole("banner").getByRole("button", { name: /music/i })).toHaveCount(0);
  const credits = media.getByText("Media credits", { exact: true });
  await credits.tap();
  const pinned = "https://github.com/FreeCol/freecol/blob/7b59d42d54b31388194fba88cd31753aca2820b1/data/default/resources";
  await expect(media.getByRole("link", { name: title, exact: true })).toHaveAttribute("href", `${pinned}/music/default/tailwind.ogg`);
  await expect(media.getByRole("link", { name: "Sea texture (center0.png)", exact: true }))
    .toHaveAttribute("href", `${pinned}/images/terrain/ocean/center0.png`);
  await expect(media.getByText("by Alexander Zhelanov", { exact: false })).toBeVisible();
  await expect(media.getByText("by zookeeper and Zabin", { exact: false })).toBeVisible();
  await expect(media.getByRole("link", { name: "CC BY 4.0", exact: true }))
    .toHaveAttribute("href", "https://creativecommons.org/licenses/by/4.0/");
  await expect(media.getByRole("link", { name: "CC0 1.0", exact: true }))
    .toHaveAttribute("href", "https://creativecommons.org/publicdomain/zero/1.0/");
  await credits.tap();
  await start(true);
  await play.evaluate(node => (node as HTMLButtonElement).click());
  await expect(play).toBeVisible();
  await expect(audio).not.toHaveAttribute("src", /.+/);
  expect([...traffic].filter(path => /\.(mp3|ogg|wav)$/.test(path))).toEqual([]);
  const { width, height, x } = (await play.boundingBox())!;
  expect(Math.min(width, height)).toBeGreaterThanOrEqual(44);
  expect(x + width).toBeLessThanOrEqual(320);

  await play.tap();
  await expect(pause).toBeVisible();
  await expect.poll(async () => (await playback()).currentTime).toBeGreaterThan(.25);
  const playing = await playback();
  expect(playing).toMatchObject({ paused: false, muted: false, volume: 1 });
  expect(Math.abs(playing.duration - 25)).toBeLessThan(.15);
  const clipUrl = new URL(playing.currentSrc);
  expect(clipUrl.origin).toBe(mount.origin);
  expect(clipUrl.pathname).toMatch(new RegExp(`^${mount.pathname}assets/windward-tailwind-[\\w-]+\\.mp3$`));
  expect([...traffic].filter(path => /\.(mp3|ogg|wav)$/.test(path))).toEqual([clipUrl.pathname]);
  const excerpt = await request.get(playing.currentSrc);
  expect(excerpt.headers()["content-type"]).toBe("audio/mpeg");
  const bytes = await excerpt.body();
  expect(bytes.length).toBeLessThanOrEqual(192 * 1024);
  expect(createHash("sha256").update(bytes).digest("hex")).toBe("258705835dea28bf605006a76ba7b55c5b5731751f2d39b2924554b8c36c4456");

  const cues = control("Maritime cues");
  await expect(cues).toHaveAttribute("aria-pressed", "false");
  await cues.tap();
  await expect(cues).toHaveAttribute("aria-pressed", "true");
  await expect(pause).toBeVisible();
  await cues.tap();
  await expect(cues).toHaveAttribute("aria-pressed", "false");
  await pause.tap();
  await expect(resume).toBeVisible();
  const stopped = await playback();
  expect(stopped.paused).toBe(true);
  await resume.tap();
  await expect.poll(async () => (await playback()).currentTime).toBeGreaterThan(stopped.currentTime);

  const visibility = (hidden: boolean) => page.evaluate(hidden => {
    Object.defineProperty(document, "hidden", { configurable: true, value: hidden });
    document.dispatchEvent(new Event("visibilitychange"));
  }, hidden);
  await visibility(true);
  await expect(resume).toBeVisible();
  const hidden = await playback();
  expect(hidden.paused).toBe(true);
  await visibility(false);
  expect(await playback()).toEqual(hidden);

  await resume.tap();
  await audio.evaluate(node => {
    const clip = node as HTMLAudioElement;
    clip.currentTime = clip.duration - .2;
  });
  await control(`Replay ${title}`).tap();
  await expect.poll(async () => {
    const { paused, currentTime } = await playback();
    return !paused && currentTime > 0 && currentTime < 5;
  }).toBe(true);
  expect([...traffic].filter(path => /\/ambient-|\/api\/profile$/.test(path))).toEqual([]);
  expect(await page.evaluate(key => localStorage.getItem(key), musicKey)).toBe("pending-on");

  await page.reload();
  await expect(play).toBeVisible();
  await expect(audio).not.toHaveAttribute("src", /.+/);
  await play.tap();
  await expect.poll(async () => (await playback()).currentTime).toBeGreaterThan(.25);
  await audio.evaluate(node => {
    window.addEventListener("pagehide", () => {
      sessionStorage.setItem("windward-pagehide-paused", String((node as HTMLAudioElement).paused));
    }, { once: true });
  });
  await page.getByRole("link", { name: "‹ All games", exact: true }).tap();
  await expect(page).toHaveURL(mount.href);
  expect(await page.evaluate(() => sessionStorage.getItem("windward-pagehide-paused"))).toBe("true");
});

test("an unavailable Tailwind excerpt reports its failure and retries on tap", async ({ page, baseURL }) => {
  let blocked = true;
  await page.route(/\/assets\/windward-tailwind-[\w-]+\.mp3$/, route =>
    blocked ? route.fulfill({ status: 404, headers: { "cache-control": "no-store" } }) : route.continue());
  await page.goto(new URL("windward-ledger/", baseURL!).href);
  const { control } = voyage(page, baseURL!);
  const play = control("Play Tailwind (excerpt)");
  const media = page.getByRole("region", { name: "Windward Ledger media" });
  await play.tap();
  await expect(media.getByRole("status")).toHaveText("Tailwind couldn’t play. Try again.");
  await expect(control("Start voyage")).toBeEnabled();
  blocked = false;
  await play.tap();
  await expect(media.getByRole("status")).toBeEmpty();
  await expect.poll(() => media.locator("audio").evaluate(node => (node as HTMLAudioElement).currentTime)).toBeGreaterThan(.25);
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
