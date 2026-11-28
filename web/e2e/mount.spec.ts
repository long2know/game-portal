import { readFileSync } from "node:fs";
import { expect, test } from "@playwright/test";
import type { ClientMessage, Guest, Payloads, Snapshot } from "../src/protocol";

test("the published Mahjong hero paints exact playable bamboo faces without covering the controls", async ({ page, baseURL }) => {
  const mount = new URL(baseURL!);
  const portal = /^\/(?:game-)?portal\/$/.test(mount.pathname);
  await page.goto(new URL(portal ? "mahjong/" : "./", mount).href);
  await expect(page.locator(".connection")).toHaveAttribute("data-status", "online");
  await expect(page.getByRole("heading", { name: "Find your fourth.", exact: true })).toBeVisible();
  const decoration = page.locator(".hero-art");
  await expect(decoration).toHaveAttribute("aria-hidden", "true");
  await expect(decoration).toHaveCSS("pointer-events", "none");
  const art = decoration.locator("svg");
  await expect(art).toBeVisible();
  const subset = await art.evaluate((svg, source) => {
    const parse = (markup: string) => new DOMParser().parseFromString(markup, "image/svg+xml");
    const played = parse(source);
    const definitions = [...parse(svg.outerHTML).querySelectorAll('[id^="lt-"]')];
    return {
      copied: definitions.map(node => node.id),
      altered: definitions.filter(node => node.outerHTML !== played.getElementById(node.id)?.outerHTML).map(node => node.id),
      faces: [...svg.querySelectorAll<SVGUseElement>('use[href^="#lt-b"]')].map(use => {
        const { width, height } = use.getBBox();
        return { href: use.getAttribute("href"), painted: width > 0 && height > 0 };
      }),
    };
  }, readFileSync(new URL("../src/tile-suits.svg", import.meta.url), "utf8"));
  expect(subset).toEqual({
    copied: ["lt-ink0", "lt-ink1", "lt-ink2", "lt-ink3", "lt-ink4", "lt-b2", "lt-b3", "lt-b4"],
    altered: [],
    faces: ["#lt-b2", "#lt-b3", "#lt-b4"].map(href => ({ href, painted: true })),
  });

  const name = page.getByRole("textbox", { name: "Display name", exact: true });
  const friends = page.getByRole("button", { name: "Play with friends", exact: true });
  const bots = page.getByRole("button", { name: "Play with bots", exact: true });
  for (const [width, height] of [[320, 640], [390, 780], [740, 320], [1920, 1080]]) {
    await page.setViewportSize({ width, height });
    await expect(art).toBeVisible();
    const layout = await art.evaluate(svg => {
      const bounds = svg.getBoundingClientRect();
      const hero = svg.closest(".hero")!;
      const panel = hero.getBoundingClientRect();
      const text = [...hero.querySelectorAll(".hero-copy :is(p:not([hidden]), h1)")].flatMap(node => {
        const range = document.createRange();
        range.selectNodeContents(node);
        return [...range.getClientRects()];
      });
      const controls = [...hero.querySelectorAll(".hero-copy :is(label, button:not([hidden]))")]
        .map(node => node.getBoundingClientRect());
      const encloses = (outer: DOMRect, inner: DOMRect) => inner.left >= outer.left && inner.right <= outer.right &&
        inner.top >= outer.top && inner.bottom <= outer.bottom;
      const marks = [...svg.querySelectorAll('use[href^="#lt-b"]')].map(use => use.getBoundingClientRect());
      return {
        clipped: !encloses(panel, bounds) || marks.some(mark => !encloses(bounds, mark)),
        overlap: [...text, ...controls].some(rect => bounds.left < rect.right && bounds.right > rect.left &&
          bounds.top < rect.bottom && bounds.bottom > rect.top),
      };
    });
    expect(layout, `Hero artwork at ${width}×${height}`).toEqual({ clipped: false, overlap: false });
    await name.focus();
    await page.keyboard.press("Tab");
    await expect(friends).toBeFocused();
    await page.keyboard.press("Tab");
    await expect(bots).toBeFocused();
    for (const control of [name, friends, bots]) await control.click({ trial: true });
  }
});

test("the published Jade Court shell shares one local tile emblem with the browser tab", async ({
  page, baseURL, request,
}, testInfo) => {
  const home = new URL(baseURL!);
  const social: string[] = [];
  page.on("request", request => { if (request.url().includes("/api/social/")) social.push(request.url()); });
  await page.setViewportSize({ width: 320, height: 700 });
  await page.goto(new URL("play/reconnect?from=brand", home).href);
  const header = page.getByRole("banner");
  const name = header.getByText("Jade Court", { exact: true });
  const subtitle = header.getByText("Changsha Mahjong", { exact: true });
  const connection = header.getByText("Online", { exact: true });
  await expect(connection).toBeVisible();
  await expect(page).toHaveTitle("Jade Court · Changsha Mahjong");
  expect(await page.locator("#app").getAttribute("data-portal-mahjong")).toBeNull();
  const nestedMahjong = await request.get(new URL("mahjong/", home).href);
  await expect(nestedMahjong).toBeOK();
  expect(await nestedMahjong.text()).not.toContain("data-portal-mahjong");
  await expect(header.getByRole("button", { name: /Online lobby/ })).toHaveCount(0);
  expect(social).toEqual([]);
  await expect(name).toBeVisible();
  await expect(subtitle).toBeVisible();
  const toolsLeft = (await connection.boundingBox())!.x;
  for (const label of [name, subtitle]) {
    const { x, width } = (await label.boundingBox())!;
    expect(x + width).toBeLessThan(toolsLeft);
  }

  const favicon = new URL(await page.locator('link[rel="icon"]').evaluate(link => (link as HTMLLinkElement).href));
  expect(favicon.origin).toBe(home.origin);
  expect(favicon.pathname).toMatch(/\/assets\/jade-court-[\w-]{8,}\.svg$/);
  expect(favicon.pathname.startsWith(`${home.pathname}assets/`)).toBe(true);
  const response = await request.get(favicon.href);
  await expect(response).toBeOK();
  expect(response.headers()["content-type"]).toBe("image/svg+xml");
  await expect(header.locator(".brand-symbol")).toHaveCSS("background-image", `url("${favicon.href}")`);
  await header.screenshot({ path: testInfo.outputPath("jade-court-320.png") });
});

type InviteClipboard = typeof window & { inviteWrites: string[] };

test("a mounted guest can copy an invite, start a live hand, and reload into the same seat", async ({
  page, context, browser, baseURL, request,
}, testInfo) => {
  const home = new URL(baseURL!);
  test.skip(home.pathname === "/", "Run against the published app with SMOKE_PATH_BASE=/mahjong.");
  const mount = home.pathname.slice(0, -1);
  const online = '.connection[data-status="online"]';
  const responses = new Map<string, { status: number; mime: string }>();
  const engraving = () => [...responses].find(([path]) => /\/assets\/hall-dragon-[\w-]{8,}\.svg$/.test(path));
  const sockets = new Set<string>();
  const errors: string[] = [];
  const creates: Payloads["createRoom"][] = [];
  let snapshot: Snapshot | undefined;
  page.on("pageerror", error => errors.push(error.message));
  page.on("response", response => {
    const url = new URL(response.url());
    if (url.origin === home.origin) {
      responses.set(url.pathname, { status: response.status(), mime: response.headers()["content-type"] ?? "" });
    }
  });
  page.on("websocket", socket => {
    sockets.add(socket.url());
    socket.on("framesent", ({ payload }) => {
      const command = JSON.parse(payload.toString()) as ClientMessage;
      if (command.type === "command" && command.op === "createRoom") creates.push(command.payload);
    });
    socket.on("framereceived", ({ payload }) => {
      const message = JSON.parse(payload.toString());
      if (message.type === "snapshot") snapshot = message;
    });
  });
  const tap = (name: string) => page.getByRole("button", { name, exact: true }).tap();
  const fits = async (name: string) => {
    const target = page.getByRole("button", { name, exact: true });
    await expect(target).toBeInViewport({ ratio: 1 });
    const { width, height } = (await target.boundingBox())!;
    expect(Math.min(width, height)).toBeGreaterThanOrEqual(44);
  };
  const inviteDialog = page.getByRole("dialog", { name: "Invite friends" });
  // Observe writes without granting access to the system clipboard in CI.
  await page.addInitScript(() => {
    const inviteWrites: string[] = [];
    (window as InviteClipboard).inviteWrites = inviteWrites;
    Object.defineProperty(navigator, "clipboard", {
      value: { writeText: async (link: string) => { inviteWrites.push(link); } },
    });
  });
  const copied = () => page.evaluate(() => (window as InviteClipboard).inviteWrites);

  // A nested browser URL must still resolve the built entry assets, APIs and socket from the mount.
  const nested = new URL("play/reconnect?from=browser", home);
  await page.setViewportSize({ width: 320, height: 700 });
  await page.goto(nested.href);
  await expect(page.locator(online)).toBeVisible();
  expect(engraving()).toBeUndefined();
  await page.getByLabel("Display name").fill("Mounted Host");
  await fits("Play with friends");
  await tap("Play with friends");
  const create = page.getByRole("dialog", { name: "Create a table" });
  const birds = create.getByLabel("Birds");
  await expect(birds).toHaveValue("changsha-v1");
  await expect(birds.locator("option")).toHaveText(["One bird (classic)", "Two birds (house rules)"]);
  await create.getByRole("button", { name: "Create table" }).tap();
  await expect(page.getByRole("heading", { name: "Take a seat." })).toBeVisible();
  expect(snapshot?.room.rulesVersion).toBe("changsha-v1");
  expect(creates).toEqual([{ visibility: "invite", matchHands: 1, fillBots: false }]);
  await expect(inviteDialog).toHaveCount(0);
  await fits("Invite friends");
  await page.setViewportSize({ width: 740, height: 320 });
  await fits("Invite friends");
  await page.screenshot({ path: testInfo.outputPath("invite-waiting-740x320.png") });
  await tap("Invite friends");
  const link = inviteDialog.getByRole("textbox", { name: "Invite link" });
  await expect(link).toBeVisible();
  expect((await copied()).length).toBe(0);
  await fits("Copy invite link");
  await tap("Copy invite link");
  await expect(inviteDialog.getByRole("status")).toHaveText("Link copied.");
  const invitation = await link.inputValue();
  const writes = await copied();
  expect(writes).toEqual([invitation]);
  const invite = new URL(writes[0]);
  expect(invite.origin + invite.pathname + invite.search).toBe(home.href);
  expect(invite.username + invite.password).toBe("");
  expect([...new URLSearchParams(invite.hash.slice(1))]).toEqual([["invite", expect.stringMatching(/^[\w-]{16}$/)]]);
  await tap("Close dialog");

  const visitorContext = await browser.newContext({ hasTouch: true });
  try {
    const visitor = await visitorContext.newPage();
    const bare = new URL(invite);
    bare.pathname = mount;
    bare.search = "?from=invite%2Fbrowser";
    const redirect = await request.get(bare.href, { maxRedirects: 0 });
    expect(redirect.status()).toBe(308);
    expect(redirect.headers().location).toBe(home.pathname + bare.search);
    invite.search = bare.search;
    await visitor.goto(bare.href);
    await expect(visitor).toHaveURL(invite.href);
    await expect(visitor.getByText("Invitation detected.", { exact: false })).toBeVisible();
    await expect(visitor.locator(online)).toBeVisible();
    await visitor.getByRole("button", { name: "Join", exact: true }).tap();
    await expect(visitor.getByRole("heading", { name: "Take a seat." })).toBeVisible();
    await expect(visitor.getByRole("button", { name: "Invite friends" })).toHaveCount(0);
    invite.hash = "";
    await expect(visitor).toHaveURL(invite.href);
    await visitor.reload();
    await expect(visitor).toHaveURL(invite.href);
    await expect(visitor.getByRole("heading", { name: "Take a seat." })).toBeVisible();

    await tap("Add bot to West seat");
    await tap("Add bot to North seat");
    await tap("Mark ready");
    // The visitor must act on the room revision that includes the host's ready command.
    const hostSeat = visitor.getByRole("article").filter({ has: visitor.getByRole("heading", { name: "Mounted Host", exact: true }) });
    await expect(hostSeat.getByText(/ · Ready$/)).toBeVisible();
    await visitor.getByRole("button", { name: "Mark ready", exact: true }).tap();
    await expect(visitor.getByRole("button", { name: "Not ready", exact: true })).toBeEnabled();
    await expect(page.getByRole("button", { name: "Start match", exact: true })).toBeEnabled();
    await tap("Start match");
    await expect.poll(() => snapshot?.room.status).toBe("playing");
    expect(snapshot?.game?.handNumber).toBe(1);
    await expect(visitor.locator(".hand-tile").first()).toBeVisible();
    const tile = page.locator(".hand-tile .tile-face use").first();
    await expect(tile).toHaveAttribute("href", /#lt-[bcd][1-9]$/);
    const sheet = new URL((await tile.getAttribute("href"))!);
    expect(sheet.origin).toBe(home.origin);
    expect(sheet.pathname).toMatch(/\/assets\/tile-suits-[\w-]+\.svg$/);
    await expect.poll(() => responses.get(sheet.pathname)).toEqual({ status: 200, mime: "image/svg+xml" });
    await expect.poll(() => engraving()?.[1]).toEqual({ status: 200, mime: "image/svg+xml" });

    await tap("Settings");
    await expect(page.getByRole("dialog", { name: "Settings" })).toBeVisible();
    await expect(page.getByRole("combobox", { name: "Soundtrack", exact: true })).toHaveCount(0);
    await expect(page.getByRole("combobox", { name: "Spoken table calls", exact: true })).toHaveCount(0);
    await expect.poll(() => [...responses].some(([path, asset]) =>
      /\/assets\/fullscreen-[\w-]+\.js$/.test(path) && asset.status === 200)).toBe(true);
    await tap("Close dialog");
    for (const [name, ext] of [["index", "js"], ["index", "css"], ["actors", "js"], ["actors", "css"]]) {
      expect([...responses].some(([path, asset]) =>
        new RegExp(`/assets/${name}-[\\w-]+\\.${ext}$`).test(path) && asset.status === 200), `${name}.${ext} response`).toBe(true);
    }

    const cookie = (await context.cookies(home.href)).find(item => item.name === "mahjong_guest");
    expect(cookie).toMatchObject({ path: mount, httpOnly: true, sameSite: "Lax", secure: home.protocol === "https:" });
    expect((await context.cookies(`${home.origin}${mount}-other/`)).some(item => item.name === "mahjong_guest")).toBe(false);
    const roomId = snapshot!.roomId;
    const seat = snapshot!.self.seat;
    const resume = page.waitForResponse(response => response.url() === new URL("api/guest", home).href
      && response.request().method() === "POST");
    snapshot = undefined;
    await page.reload();
    await expect(page).toHaveURL(nested.href);
    const guest: Guest = await (await resume).json();
    expect(guest).toMatchObject({ displayName: "Mounted Host", resumeRoomId: roomId });
    await expect.poll(() => ({ roomId: snapshot?.roomId, seat: snapshot?.self.seat })).toEqual({ roomId, seat });
    await expect(page.locator(".hand-tile").first()).toBeVisible();
    await expect(page.locator(online)).toBeVisible();
    await expect(inviteDialog).toHaveCount(0);
    expect((await copied()).length).toBe(0);
    for (const route of ["api/guest", "api/profile", "api/rooms"]) {
      expect(responses.get(home.pathname + route)?.status, route).toBe(200);
    }
    expect([...responses.keys()].filter(path => !path.startsWith(home.pathname))).toEqual([]);
    const socket = new URL("ws", home);
    socket.protocol = home.protocol === "https:" ? "wss:" : "ws:";
    expect([...sockets]).toEqual([socket.href]);
    expect(errors).toEqual([]);
  } finally {
    await visitorContext.close();
  }
});
