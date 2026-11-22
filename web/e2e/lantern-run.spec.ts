import { expect, test } from "@playwright/test";
import type { Page } from "@playwright/test";
import { LanternCourse, pace } from "../src/portal/games/lantern-course";

const epoch = Date.UTC(2026, 10, 22, 20);
const runner = (page: Page) => page.locator(".lantern-run");
const stat = (page: Page, name: string) => runner(page).locator(`[data-stat="${name}"]`);
const action = (page: Page, name: string) => runner(page).getByRole("button", { name, exact: true });
const hint = (page: Page) => runner(page).getByRole("status");
const distance = async (page: Page) => parseInt(await stat(page, "distance").innerText(), 10);

async function freeze(page: Page): Promise<void> {
  await page.clock.install({ time: epoch });
  await page.clock.pauseAt(epoch + 60_000);
}

async function reach(page: Page, meters: number): Promise<void> {
  for (let attempt = 0; attempt < 40; attempt++) {
    const left = meters - await distance(page);
    if (left <= 0) return;
    await expect(runner(page)).toHaveAttribute("data-state", "running");
    await page.clock.runFor(left >= 5 ? 130 : 43);
  }
  throw new Error(`Courier never reached ${meters} m (fell short at ${await distance(page)} m).`);
}

async function geometry(page: Page): Promise<void> {
  expect(await page.evaluate(() => document.documentElement.scrollWidth)).toBeLessThanOrEqual(page.viewportSize()!.width);
  await expect(page.locator(".run-canvas")).toBeInViewport();
  for (const name of ["Move left", "Jump", "Slide", "Move right"]) {
    const control = action(page, name);
    await expect(control).toBeInViewport({ ratio: .99 });
    const { width, height } = (await control.boundingBox())!;
    expect(width).toBeGreaterThanOrEqual(44);
    expect(height).toBeGreaterThanOrEqual(44);
    expect(await control.evaluate(node => node.scrollWidth)).toBeLessThanOrEqual(Math.ceil(width));
  }
}

async function swipe(page: Page, dx: number, dy: number): Promise<void> {
  const canvas = (await page.locator(".run-canvas").boundingBox())!;
  const x = Math.round(canvas.x + canvas.width * .52);
  const y = Math.round(canvas.y + canvas.height * .58);
  const session = await page.context().newCDPSession(page);
  try {
    await session.send("Input.dispatchTouchEvent", { type: "touchStart", touchPoints: [{ x, y, id: 1 }] });
    await session.send("Input.dispatchTouchEvent", {
      type: "touchMove", touchPoints: [{ x: x + dx, y: y + dy, id: 1 }],
    });
    await session.send("Input.dispatchTouchEvent", { type: "touchEnd", touchPoints: [] });
  } finally {
    await session.detach();
  }
}

async function hidden(page: Page, hidden: boolean): Promise<void> {
  await page.evaluate(value => {
    Object.defineProperty(document, "hidden", { configurable: true, get: () => value });
    document.dispatchEvent(new Event("visibilitychange"));
  }, hidden);
}

test.describe("published Lantern Run", () => {
  test.beforeEach(({ baseURL }) => {
    test.skip(!baseURL || !/^\/(?:game-)?portal\/$/.test(new URL(baseURL).pathname), "Requires either published portal mount.");
  });

  test("catalog, canonical URLs, lazy assets and score fallback stay local", async ({ page, request, baseURL }) => {
    const mount = new URL(baseURL!).pathname;
    const bare = new URL("lantern-run?entry=night%2Fcanal&note=a+b", baseURL);
    for (const method of ["GET", "HEAD"]) {
      const response = await request.fetch(bare.href, { method, maxRedirects: 0 });
      expect(response.status()).toBe(308);
      expect(response.headers().location).toBe(`${mount}lantern-run/?entry=night%2Fcanal&note=a+b`);
    }
    expect((await request.get(new URL("lantern-run/private-table/", baseURL).href)).status()).toBe(404);
    await page.addInitScript(current => {
      const other = current === "/portal/" ? "/game-portal/" : "/portal/";
      localStorage.setItem(`jade-court:lantern-run:best:${other}`, "9000");
      const set = Storage.prototype.setItem;
      Storage.prototype.setItem = function (key: string, value: string) {
        if (key === `jade-court:lantern-run:best:${current}`) throw new DOMException("Storage full", "QuotaExceededError");
        return set.call(this, key, value);
      };
    }, mount);
    await freeze(page);
    const paths = new Set<string>();
    const errors: string[] = [];
    page.on("request", request => paths.add(new URL(request.url()).pathname));
    page.on("pageerror", error => errors.push(error.message));
    await page.goto(baseURL!);
    const card = page.getByRole("link", { name: "Play Lantern Run" });
    await expect(card).toHaveAttribute("href", `${mount}lantern-run/`);
    await expect(card.locator("svg")).toBeVisible();
    await expect(page.getByText("13 games · No account required")).toBeVisible();
    const assets = () => [...paths].filter(path => /\/assets\/lantern-run-/.test(path));
    expect(assets()).toEqual([]);
    await card.click();
    await expect(page).toHaveTitle("Lantern Run · Jade Court");
    await expect(page.locator("#app")).toHaveAttribute("data-base", mount);
    expect(assets()).toEqual(expect.arrayContaining([
      expect.stringMatching(/\/lantern-run-[\w-]+\.js$/), expect.stringMatching(/\/lantern-run-[\w-]+\.css$/),
    ]));
    expect(assets().every(path => path.startsWith(`${mount}assets/`))).toBe(true);
    await expect(page.getByRole("navigation").getByRole("link", { name: "All games" })).toHaveAttribute("href", mount);
    await expect(stat(page, "best")).toHaveText("0");
    await action(page, "Start run").click();
    await page.clock.runFor(2300);
    await expect(runner(page)).toHaveAttribute("data-state", "over");
    await expect(stat(page, "best")).toHaveText("56");
    await action(page, "Run again").click();
    await expect(stat(page, "best")).toHaveText("56");
    await page.reload();
    await expect(stat(page, "best")).toHaveText("0");
    expect([...paths].filter(path => path.includes("/api/"))).toEqual([]);
    expect(errors).toEqual([]);
  });

  test("remembered portal preferences apply without starting the run", async ({ page, baseURL }) => {
    const base = new URL(baseURL!);
    const headers = { Origin: base.origin };
    const guest = await page.request.post(new URL("api/guest", base).href, { headers, data: {} });
    expect(guest.ok()).toBe(true);
    const profile = await page.request.patch(new URL("api/profile", base).href, { headers,
      data: { preferences: { sound: false, music: true, volume: 0, reducedMotion: true } } });
    expect(profile.ok()).toBe(true);
    await page.addInitScript(mount => localStorage.setItem(`${mount}court-music`, "on"), base.pathname);
    await page.goto(new URL("lantern-run/", base).href);
    await expect(page.getByText("Music is muted at 0% volume.")).toBeVisible();
    await expect(page.getByLabel("Reduce effects")).toBeChecked();
    await expect(action(page, "Start run")).toBeVisible();
    await expect(stat(page, "distance")).toHaveText("0 m");
  });

  test.describe("desktop", () => {
    test.use({ isMobile: false, hasTouch: false });

    test("keyboard: steer, jump, slide, pause on hide, crash and persist a fresh restart", async ({ page, baseURL }, info) => {
      await page.setViewportSize({ width: 1280, height: 820 });
      await freeze(page);
      await page.goto(new URL("lantern-run/", baseURL).href);
      await expect(action(page, "Start run")).toBeVisible();
      await page.clock.runFor(1800);
      await expect(stat(page, "distance")).toHaveText("0 m");
      await page.screenshot({ path: info.outputPath("desktop-start.png") });
      await action(page, "Start run").click();
      await geometry(page);
      await reach(page, 26);
      await expect(stat(page, "lanterns")).toHaveText("1");
      await expect(hint(page)).toContainText("Move left");
      await page.keyboard.press("ArrowLeft");
      await reach(page, 45);
      await expect(stat(page, "lanterns")).toHaveText("2");
      await expect(hint(page)).toContainText("Jump the gold rail");
      await expect(runner(page).getByText("Now", { exact: true })).toBeVisible();
      await page.keyboard.press("w");
      await page.clock.runFor(145);
      await page.screenshot({ path: info.outputPath("desktop-live.png") });
      await reach(page, 63);
      await expect(stat(page, "lanterns")).toHaveText("3");
      await expect(hint(page)).toContainText("Slide under the jade gate");
      await page.keyboard.press("ArrowDown");
      await reach(page, 71);
      await expect(stat(page, "lanterns")).toHaveText("4");

      const before = await distance(page);
      await page.keyboard.press("p");
      await expect(page.getByRole("heading", { name: "Run paused" })).toBeVisible();
      await page.clock.runFor(1600);
      expect(await distance(page)).toBe(before);
      await action(page, "Resume run").click();
      await hidden(page, true);
      await page.clock.runFor(1600);
      await hidden(page, false);
      await page.clock.runFor(1600);
      await expect(action(page, "Resume run")).toBeFocused();
      expect(await distance(page)).toBe(before);
      await action(page, "Resume run").click();
      await page.clock.runFor(940);
      await expect(page.getByRole("heading", { name: "End of the run" })).toBeVisible();
      await expect(action(page, "Run again")).toBeFocused();
      await expect(stat(page, "distance")).toHaveText("84 m");
      await expect(stat(page, "score")).toHaveText("184");
      await expect(stat(page, "best")).toHaveText("184");
      await page.screenshot({ path: info.outputPath("desktop-over.png") });
      await page.keyboard.press("Enter");
      await expect(runner(page)).toHaveAttribute("data-state", "running");
      await expect(stat(page, "distance")).toHaveText("0 m");
      await expect(stat(page, "lanterns")).toHaveText("0");
      await page.reload();
      await expect(stat(page, "best")).toHaveText("184");
      await expect(runner(page)).toHaveAttribute("data-state", "ready");
    });
  });

  test("phone: swipes or touch controls work at 320px and after landscape rotation", async ({ page, baseURL, browserName }, info) => {
    await page.setViewportSize({ width: 320, height: 640 });
    await page.emulateMedia({ reducedMotion: "reduce" });
    await freeze(page);
    await page.goto(new URL("lantern-run/", baseURL).href);
    await expect(page.getByLabel("Reduce effects")).toBeDisabled();
    await expect(page.getByLabel("Reduce effects")).toBeChecked();
    await page.screenshot({ path: info.outputPath("phone-start.png") });
    await action(page, "Start run").tap();
    await geometry(page);
    await reach(page, 26);
    if (browserName === "chromium") await swipe(page, -55, 0);
    else await action(page, "Move left").tap();
    await reach(page, 45);
    await expect(stat(page, "lanterns")).toHaveText("2");
    if (browserName === "chromium") await swipe(page, 0, -55);
    else await action(page, "Jump").tap();
    await reach(page, 63);
    await expect(stat(page, "lanterns")).toHaveText("3");
    await action(page, "Slide").tap();
    await page.clock.runFor(155);
    await page.screenshot({ path: info.outputPath("phone-live.png") });
    await reach(page, 71);
    await expect(stat(page, "lanterns")).toHaveText("4");
    await action(page, "Pause").tap();
    await page.setViewportSize({ width: 640, height: 320 });
    await expect(action(page, "Resume run")).toBeInViewport({ ratio: .99 });
    await action(page, "Resume run").tap();
    await geometry(page);
    await action(page, "Move right").tap();
    await reach(page, 88);
    await expect(stat(page, "lanterns")).toHaveText("5");
    await page.screenshot({ path: info.outputPath("phone-landscape.png") });
    await page.setViewportSize({ width: 320, height: 640 });
    await page.reload();
    await action(page, "Start run").tap();
    await page.clock.runFor(2300);
    await expect(page.getByRole("heading", { name: "End of the run" })).toBeVisible();
    await expect(stat(page, "distance")).toHaveText("31 m");
    await page.screenshot({ path: info.outputPath("phone-over.png") });
    await action(page, "Run again").tap();
    await expect(stat(page, "score")).toHaveText("0");
    await expect(stat(page, "best")).toHaveText("56");
  });
});

test("the course samples the crossing pose, not rendered frames, and caps stalled frames", () => {
  const fast = new LanternCourse(() => .5);
  const uneven = new LanternCourse(() => .5);
  for (let i = 0; i < 100; i++) {
    fast.advance(.01);
    uneven.advance(i % 2 ? .0165 : .0035);
  }
  expect(fast.meters).toBe(uneven.meters);
  expect(fast.meters).toBeGreaterThan(16);
  expect(fast.lanterns).toBe(1);
  fast.advance(20);
  uneven.advance(.1);
  expect(fast.meters).toBe(uneven.meters);
  expect(fast.meters).toBeLessThan(19);

  for (const move of ["jump", "slide"] as const) {
    const run = new LanternCourse(() => .5);
    run.move("left");
    for (let i = 0; run.meters < 45 && i < 280; i++) run.advance(.016);
    run.move(move);
    for (let i = 0; run.meters <= 49 && !run.crash && i < 33; i++) run.advance(.016);
    expect(run.crash, `${move} at the gold rail`).toBe(move === "slide" ? "rail" : undefined);
    if (move === "jump") {
      expect(run.lanterns).toBe(2);
      for (let i = 0; run.meters < 63 && i < 60; i++) run.advance(.016);
      run.move("jump");
      for (let i = 0; !run.crash && i < 35; i++) run.advance(.016);
      expect(run.crash, "jumping alone must not clear the low gate").toBe("gate");
    }
  }
});

test("generated rows leave a reachable open path all the way to maximum speed", () => {
  for (let seed = 1; seed <= 9; seed++) {
    let state = seed;
    const run = new LanternCourse(() => ((state = (Math.imul(state, 1664525) + 1013904223) >>> 0) / 2 ** 32));
    let previous = run.lane;
    let previousDistance = 0;
    for (let index = 0; index < 125; index++) {
      const row = run.next;
      expect(row.hazards[row.safe], `seed ${seed} at ${row.at} m`).toBeNull();
      expect(Math.abs(previous - row.safe)).toBeLessThanOrEqual(1);
      if (index > 4) expect((row.at - previousDistance) / pace(row.at)).toBeGreaterThan(.9);
      previous = row.safe;
      previousDistance = row.at;
      for (let elapsed = 0; run.meters < row.at && !run.crash && elapsed < 3; elapsed += .033) {
        if (elapsed > .47 && run.lane !== row.safe) run.move(run.lane > row.safe ? "left" : "right");
        run.advance(.033);
      }
      expect(run.crash, `open route must pass row ${index} in seed ${seed}`).toBeUndefined();
      expect(run.meters).toBeGreaterThanOrEqual(row.at);
    }
    expect(pace(run.meters)).toBeGreaterThan(pace(0) * 2);
    expect(run.lanterns).toBeGreaterThan(30);
  }
});
