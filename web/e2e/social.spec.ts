import { expect, test } from "@playwright/test";
import type { Browser, BrowserContext, Page } from "@playwright/test";
import type { Table } from "../src/portal/api";
import type { InviteGame } from "../src/portal/invite-handoff";
import type { ClientMessage, Snapshot } from "../src/protocol";
import { fitsPhone, receive, tap } from "./portal-support";

type Address = (path?: string) => string;
const urls = (base: string): Address => {
  const root = new URL(base.endsWith("/") ? base : `${base}/`);
  return (path = "") => new URL(path, root).href;
};
const dialog = (page: Page) => page.getByRole("dialog", { name: "Online lobby" });
const button = (page: Page, name: string) => page.getByRole("button", { name, exact: true });
const opener = (page: Page) => page.getByRole("button", { name: /^Online lobby/ });
const fixtureMode = process.env.SOCIAL_FIXTURES === "1";

async function phone(browser: Browser, viewport = { width: 320, height: 730 }) {
  const context = await browser.newContext({ viewport, isMobile: true, hasTouch: true, reducedMotion: "reduce" });
  return context.newPage();
}

async function openLobby(page: Page) {
  await opener(page).tap();
  await expect(dialog(page)).toBeVisible();
  return dialog(page);
}

async function optIn(page: Page, name: string) {
  const lobby = await openLobby(page);
  const available = lobby.getByRole("switch", { name: "Available to play" });
  await expect(available).toBeEnabled();
  await expect(available).not.toBeChecked();
  await lobby.getByRole("textbox", { name: "Display name" }).fill(name);
  await lobby.getByRole("button", { name: "Save name" }).tap();
  await expect(lobby.getByText("Display name saved.")).toBeVisible();
  await available.focus();
  await page.keyboard.press("Space");
  await expect(available).toBeChecked();
  await page.keyboard.press("Escape");
  await expect(lobby).not.toBeVisible();
  await expect(opener(page)).toBeFocused();
}

async function visibility(page: Page, hidden: boolean) {
  await page.evaluate(hidden => {
    Object.defineProperty(document, "hidden", { configurable: true, get: () => hidden });
    document.dispatchEvent(new Event("visibilitychange"));
  }, hidden);
}

// Optional HTTP social fixture: actual guest, member, game-action and WebSocket routes still hit the app.
// It deliberately does not replace the real server's privacy/authorization tests.
function fixture(attach: (game: InviteGame, target: Page) => Promise<string>) {
  type Peer = { page: Page; name: string; available: boolean; tabs: Map<string, boolean> };
  const peers = new Map<string, Peer>();
  const invitations = new Map<string, { from: Peer; to: Peer; game: InviteGame; tableId: string }>();
  return async (page: Page, name: string) => {
    if (!fixtureMode) return;
    const id = `fixture-player-${peers.size}`;
    const self: Peer = { page, name, available: false, tabs: new Map() };
    peers.set(id, self);
    await page.context().route("**/api/social/**", async route => {
      const path = new URL(route.request().url()).pathname.split("/api/social/")[1];
      expect(route.request().method()).toBe("POST");
      expect(await route.request().headerValue("origin")).toBe(new URL(route.request().url()).origin);
      expect(route.request().postDataBuffer()!.length).toBeLessThanOrEqual(4096);
      const body = route.request().postDataJSON();
      if (path === "presence") {
        if (body.available !== undefined) self.available = body.available;
        if (body.active) self.tabs.set(body.tabId, body.busy);
        else self.tabs.delete(body.tabId);
        const players = [...peers].flatMap(([id, peer]) =>
          self.available && peer !== self && peer.available && peer.tabs.size && ![...peer.tabs.values()].some(Boolean)
            ? [{ id, displayName: peer.name }] : []);
        const invites = (incoming: boolean) => [...invitations].flatMap(([id, invite]) =>
          (incoming ? invite.to : invite.from) === self ?
            [{ id, game: invite.game, displayName: (incoming ? invite.from : invite.to).name,
              expiresAtUtc: new Date(Date.now() + 120_000).toISOString() }] : []);
        return route.fulfill({ json: { available: self.available, players, incoming: invites(true), outgoing: invites(false) } });
      }
      if (path === "invites") {
        const id = `fixture-invite-${invitations.size}`;
        invitations.set(id, { from: self, to: peers.get(body.playerId)!, game: body.game, tableId: body.tableId });
        return route.fulfill({ json: { id } });
      }
      const [, id, action] = path.split("/");
      const invite = invitations.get(id)!;
      if (action === "accept") {
        const id = await attach(invite.game, self.page);
        expect(id).toBe(invite.tableId);
        invitations.delete(path.split("/")[1]);
        return route.fulfill({ json: { game: invite.game, tableId: id } });
      }
      invitations.delete(id);
      return route.fulfill({ json: { ok: true } });
    });
  };
}

async function joinByLink(page: Page, url: Address, endpoint: string, invitation: string) {
  const response = await page.request.post(url(`api/${endpoint}/join`), {
    headers: { Origin: new URL(url()).origin }, data: { invite: new URL(invitation).hash.slice("#invite=".length) },
  });
  expect(response.ok()).toBe(true);
  return (await response.json()).id as string;
}

async function send(from: Page, to: string, expected: { game: InviteGame; tableId: string }) {
  await tap(from, "Invite players");
  const panel = dialog(from);
  await expect(panel.getByRole("switch")).toBeChecked();
  const request = from.waitForRequest(request => request.url().endsWith("/api/social/invites"));
  await panel.getByRole("list", { name: "Available guests" }).getByRole("listitem").filter({ hasText: to })
    .getByRole("button", { name: "Invite", exact: true }).tap();
  const data = (await request).postDataJSON();
  expect(data).toEqual({ ...expected, playerId: expect.any(String) });
  await expect(panel.getByText("Invite sent. You can keep waiting at your table.")).toBeVisible();
  const sent = panel.getByRole("list", { name: "Sent invites" }).getByRole("listitem").filter({ hasText: to });
  await expect(sent.getByRole("button", { name: "Revoke", exact: true })).toBeEnabled();
  await panel.getByRole("button", { name: "Close" }).tap();
}

async function accept(page: Page, url: string, sender: string) {
  const heading = page.getByRole("heading", { level: 1 });
  await heading.evaluate(node => { node.setAttribute("tabindex", "-1"); (node as HTMLElement).focus(); });
  await expect(opener(page)).toHaveAccessibleName("Online lobby, 1 incoming invite", { timeout: 21_000 });
  await expect(heading).toBeFocused();
  await expect(dialog(page)).not.toBeVisible();
  const panel = await openLobby(page);
  const incoming = panel.getByRole("list", { name: "Incoming invites" }).getByRole("listitem").filter({ hasText: sender });
  await expect(incoming.getByRole("button", { name: "Decline", exact: true })).toBeEnabled();
  await expect(incoming.getByRole("button", { name: "Block invites", exact: true })).toBeEnabled();
  await incoming.getByRole("button", { name: "Accept", exact: true }).tap();
  await expect(page).toHaveURL(url);
}

function observeWire(page: Page) {
  const sent: ClientMessage[] = [], snapshots: Snapshot[] = [];
  page.on("websocket", socket => {
    socket.on("framesent", ({ payload }) => { sent.push(JSON.parse(String(payload)) as ClientMessage); });
    socket.on("framereceived", ({ payload }) => {
      const frame = JSON.parse(String(payload));
      if (frame.type === "snapshot") snapshots.push(frame);
    });
  });
  return { sent, snapshots };
}

test.beforeEach(({ baseURL }) => {
  test.skip(!baseURL || new URL(baseURL).pathname === "/", "Requires a published portal mount.");
});

test("a private Chess invite crosses from a solo page and opens only the exact member table", async ({ page, browser, baseURL }, info) => {
  const url = urls(baseURL!);
  const hostName = `Crane ${info.project.name}`, guestName = `<Bamboo> ${info.project.name}`, offName = `Away ${info.project.name}`;
  let share = "";
  const guest = await phone(browser);
  const off = await phone(browser);
  const mock = fixture((_, visitor) => joinByLink(visitor, url, "board", share));
  try {
    await page.setViewportSize({ width: 320, height: 730 });
    await Promise.all([mock(page, hostName), mock(guest, guestName), mock(off, offName)]);
    // Observe clipboard fallback without requiring browser permission or touching a real clipboard.
    await page.addInitScript(() => Object.defineProperty(navigator, "clipboard", { value: { writeText: async () => { throw new Error(); } } }));
    await Promise.all([page.goto(url()), guest.goto(url("sudoku/")), off.goto(url("go/"))]);
    await optIn(guest, guestName);
    await optIn(page, hostName);
    const offLobby = await openLobby(off);
    await expect(offLobby.getByRole("switch")).toBeEnabled();
    await expect(offLobby.getByRole("switch")).not.toBeChecked();
    await offLobby.getByRole("textbox", { name: "Display name" }).fill(offName);
    await offLobby.getByRole("button", { name: "Save name" }).tap();
    await expect(offLobby.getByText("Display name saved.")).toBeVisible();
    await offLobby.getByRole("button", { name: "Close" }).tap();

    await page.goto(url("chess/"));
    const created = receive(page, url("api/board/tables"), "POST");
    const busy = page.waitForResponse(response => response.url() === url("api/social/presence") &&
      response.request().postDataJSON().active && response.request().postDataJSON().busy);
    await tap(page, "Invite a friend");
    const target = await (await created).json() as Table;
    expect((await busy).ok()).toBe(true);
    const recipientUpdate = receive(guest, url("api/social/presence"), "POST");
    const recipientPanel = await openLobby(guest);
    const recipientView = await (await recipientUpdate).json() as { players: { displayName: string }[] };
    expect(recipientView.players.map(player => player.displayName)).not.toContain(hostName);
    await expect(recipientPanel.getByRole("list", { name: "Available guests" }).getByText(hostName)).toHaveCount(0);
    await recipientPanel.getByRole("button", { name: "Close" }).tap();
    await tap(page, "Copy invite link");
    await expect(page.getByText("Link selected. Copy it to send your invitation.")).toBeVisible();
    share = await page.getByRole("textbox", { name: "Invite link" }).inputValue();
    expect(share).toBe(`${url("chess/")}#invite=${target.invite}`);

    await tap(page, "Invite players");
    const panel = dialog(page);
    await expect(panel.getByText(guestName, { exact: true })).toBeVisible();
    await expect(panel.getByText(offName, { exact: true })).toHaveCount(0);
    await expect(panel.getByText(hostName, { exact: true })).toHaveCount(0);
    await expect(panel.locator("bamboo")).toHaveCount(0);
    await fitsPhone(page);
    await panel.screenshot({ path: info.outputPath("online-lobby-phone.png") });
    await panel.getByRole("button", { name: "Close" }).tap();
    await send(page, guestName, { game: "chess", tableId: target.id });
    await expect(page).toHaveURL(url("chess/"));
    await expect(page.getByText("Waiting for players", { exact: true })).toBeVisible();

    const paths: string[] = [];
    guest.on("request", request => paths.push(new URL(request.url()).pathname));
    const memberUrl = url(`api/board/tables/${target.id}`);
    const member = receive(guest, memberUrl, "GET");
    await accept(guest, url("chess/"), hostName);
    const joined = await (await member).json() as Table;
    expect(joined).toMatchObject({ id: target.id, seat: 1, status: "playing" });
    const move = receive(page, url(`api/board/tables/${target.id}/moves`), "POST");
    await page.locator('[data-cell="e2"]').tap();
    await page.locator('[data-cell="e4"]').tap();
    expect((await (await move).json() as Table).position?.lastMove).toMatchObject({ from: "e2", to: "e4" });
    // The old document polls the same URL. Only count a request after the reload commits.
    let committed = false;
    guest.on("framenavigated", frame => { if (frame === guest.mainFrame()) committed = true; });
    const reloaded = guest.waitForRequest(request => committed && request.url() === memberUrl &&
      request.method() === "GET" && request.frame() === guest.mainFrame());
    await Promise.all([reloaded, guest.reload()]);
    const from = guest.locator('[data-cell="e2"]');
    const to = guest.locator('[data-cell="e4"]');
    await expect(to).toHaveAccessibleName("e4, white pawn");
    await expect(to).toHaveAttribute("data-occupied", "");
    await expect(from).toHaveAccessibleName("e2, empty");
    await expect(from).not.toHaveAttribute("data-occupied");
    expect(paths).not.toContain(new URL(url("api/board/resume")).pathname);
  } finally { await Promise.all([guest.context().close(), off.context().close()]); }
});

test("a cold local-only route does not open a guest or send presence until the lobby is opened", async ({ page, baseURL, request }) => {
  const url = urls(baseURL!);
  // Exercise the portal entry's local-only path even when the optional game is not in this publish.
  const local = url("lantern-run/");
  if ((await request.get(local)).status() === 404) {
    await page.route(local, async route => route.fulfill({ response: await route.fetch({ url: url() }) }));
  }
  const paths: string[] = [];
  page.on("request", request => {
    const path = new URL(request.url()).pathname;
    if (/\/api\/(?:guest|social\/)/.test(path)) paths.push(path);
  });
  await page.clock.install();
  await page.goto(url());
  await expect(page.getByRole("heading", { name: "Find your next move." })).toBeVisible();
  await page.goto(local);
  await expect(opener(page)).toBeVisible();
  await page.clock.runFor(46_000);
  expect(paths).toEqual([]);
  await page.route(url("api/social/presence"), route => route.fulfill({ json: {
    available: false, players: [], incoming: [], outgoing: [],
  } }));
  await openLobby(page);
  await expect(dialog(page).getByRole("switch")).toBeEnabled();
  expect(paths).toEqual([new URL(url("api/guest")).pathname, new URL(url("api/social/presence")).pathname]);
});

test("presence serializes explicit choices, stops while hidden, and backs off on busy", async ({ page, context, baseURL }) => {
  const url = urls(baseURL!);
  const calls: { tabId: string; active: boolean; busy: boolean; available?: boolean }[] = [];
  let available = false, busy = false;
  let finishToggle: (() => void) | undefined;
  await context.route(url("api/social/presence"), async route => {
    expect(route.request().method()).toBe("POST");
    expect(await route.request().headerValue("origin")).toBe(new URL(url()).origin);
    const body = route.request().postDataJSON();
    calls.push(body);
    if (body.available === true) await new Promise<void>(resolve => { finishToggle = resolve; });
    if (body.available !== undefined) available = body.available;
    if (busy) return route.fulfill({ status: 429, json: { error: { code: "busy" } } });
    return route.fulfill({ json: { available, players: [], incoming: [], outgoing: [] } });
  });
  await page.clock.install();
  await page.goto(url());
  const panel = await openLobby(page);
  const toggle = panel.getByRole("switch");
  await expect(toggle).not.toBeChecked();
  await expect(toggle).toBeEnabled();
  await toggle.focus();
  await page.keyboard.press("Space");
  await expect.poll(() => !!finishToggle).toBe(true);
  await page.keyboard.press("Space");
  expect(calls.filter(call => call.available !== undefined).map(call => call.available)).toEqual([true]);
  finishToggle!();
  await expect(toggle).toBeChecked();
  await expect(toggle).toBeFocused();
  const firstId = calls.at(-1)!.tabId;
  await visibility(page, true);
  await expect.poll(() => calls.at(-1)).toEqual({ tabId: firstId, active: false, busy: false });
  const hiddenCalls = calls.length;
  await page.clock.runFor(46_000);
  expect(calls).toHaveLength(hiddenCalls);
  await visibility(page, false);
  await expect.poll(() => calls.at(-1)).toEqual({ tabId: firstId, active: true, busy: false });
  await expect(toggle).toBeEnabled();

  // A user-visible busy response must not start a second normal 15-second polling loop.
  busy = true;
  await page.clock.runFor(15_200);
  await expect(panel.getByText("The lobby is busy. We’ll retry shortly.")).toBeVisible();
  const rateLimitedCalls = calls.length;
  await page.clock.runFor(15_500);
  expect(calls).toHaveLength(rateLimitedCalls);
  busy = false;
  await page.clock.runFor(15_000);
  await expect(toggle).toBeEnabled();
  await toggle.focus();
  await page.keyboard.press("Space");
  await expect(toggle).not.toBeChecked();
  await expect.poll(() => calls.flatMap(call => call.available === undefined ? [] : [call.available])).toEqual([true, false]);

  const sibling = await context.newPage();
  await sibling.goto(url("sudoku/"));
  await expect.poll(() => new Set(calls.map(call => call.tabId)).size).toBe(2);
  await sibling.close();
});

test("accept errors preserve the current page and a hidden response needs a second visible click", async ({ page, baseURL }) => {
  const url = urls(baseURL!);
  const id = "0123456789abcdef0123456789abcdef";
  const pending = { id: "fixture-accept", displayName: "Pine", game: "chess", expiresAtUtc: "2090-01-01T00:00:00Z" };
  const routes: string[] = [];
  let attempted = 0, finishAccept: (() => void) | undefined;
  let incoming = [pending, { ...pending, id: "fixture-solo", game: "solitaire", displayName: "Not a table" }];
  await page.route(url("api/social/presence"), route => route.fulfill({ json: { available: true, players: [], incoming, outgoing: [] } }));
  await page.route(url("api/social/invites/fixture-accept/accept"), async route => {
    expect(route.request().postDataJSON()).toEqual({});
    attempted++;
    if (attempted === 1) return route.fulfill({ status: 409, json: { error: { code: "already_seated" } } });
    if (attempted === 2) return route.fulfill({ json: { game: "chess", tableId: "../wrong" } });
    if (attempted === 3) {
      incoming = [];
      return route.abort("failed");
    }
    await new Promise<void>(resolve => { finishAccept = resolve; });
    return route.fulfill({ json: { game: "chess", tableId: id } });
  });
  await page.route(url(`api/board/tables/${id}`), route => route.fulfill({ status: 404, json: { error: { code: "room_not_found" } } }));
  page.on("request", request => { if (request.method() === "GET") routes.push(new URL(request.url()).pathname); });
  await page.goto(url());
  const panel = await openLobby(page);
  await expect(panel.getByText("Not a table")).toHaveCount(0);
  const accept = panel.getByRole("button", { name: "Accept", exact: true });
  await accept.tap();
  await expect(panel.getByText(/That game was left untouched/)).toBeVisible();
  await expect(page).toHaveURL(url());
  await accept.tap();
  await expect(panel.getByText(/Couldn’t verify the accepted table/)).toBeVisible();
  await expect(page).toHaveURL(url());
  await accept.tap();
  await expect(panel.getByText(/Couldn’t confirm your seat/)).toBeVisible();
  await panel.getByRole("button", { name: "Retry lobby" }).tap();
  const retry = panel.getByRole("button", { name: "Retry accepting invite" });
  await retry.tap();
  await expect.poll(() => !!finishAccept).toBe(true);
  const response = page.waitForResponse(url("api/social/invites/fixture-accept/accept"));
  await visibility(page, true);
  finishAccept!();
  await response;
  await visibility(page, false);
  await expect(page).toHaveURL(url());
  const open = panel.getByRole("button", { name: "Open accepted table" });
  await expect(open).toBeEnabled();
  await open.tap();
  await expect(page).toHaveURL(url("chess/"));
  const retryNotice = page.getByText(/Couldn’t open the accepted table/);
  await expect(retryNotice).toBeVisible();
  await page.reload();
  await expect(retryNotice).toBeVisible();
  expect(routes.filter(path => path === new URL(url(`api/board/tables/${id}`)).pathname)).toHaveLength(2);
  expect(routes).not.toContain(new URL(url("api/board/resume")).pathname);
  expect(attempted).toBe(4);
});

test("a Hold’em handoff reads the Poker member view and keeps the other cards covered", async ({ page, browser, baseURL }, info) => {
  const url = urls(baseURL!);
  const guest = await phone(browser);
  let share = "";
  const name = `Willow ${info.project.name}`, visitorName = `Reed ${info.project.name}`;
  const mock = fixture((_, visitor) => joinByLink(visitor, url, "poker", share));
  try {
    await page.setViewportSize({ width: 1440, height: 900 });
    await Promise.all([mock(page, name), mock(guest, visitorName)]);
    await Promise.all([page.goto(url("holdem/")), guest.goto(url("go/"))]);
    await optIn(page, name);
    await optIn(guest, visitorName);
    await guest.goto(url());
    const created = receive(page, url("api/poker/tables"), "POST");
    await tap(page, "Invite a friend");
    const { id } = await (await created).json() as { id: string };
    share = await page.getByRole("textbox", { name: "Invite link" }).inputValue();
    const panel = await openLobby(page);
    await expect(panel.getByText(visitorName, { exact: true })).toBeVisible();
    await panel.screenshot({ path: info.outputPath("online-lobby-desktop.png") });
    await panel.getByRole("button", { name: "Close" }).tap();
    await send(page, visitorName, { game: "holdem", tableId: id });
    const member = receive(guest, url(`api/poker/tables/${id}`), "GET");
    await accept(guest, url("holdem/"), name);
    expect(await (await member).json()).toMatchObject({ id, seat: 1, status: "playing" });
    await expect(guest.getByRole("group", { name: "Your cards" }).locator(".playing-card")).toHaveCount(2);
    await expect(guest.getByRole("group", { name: `${name}’s cards` }).getByRole("img", { name: "Face-down card" })).toHaveCount(2);
    const action = receive(page, url(`api/poker/tables/${id}/actions`), "POST");
    await page.getByRole("button", { name: /^Call \d+$/ }).tap();
    const played = await action;
    expect(played.ok()).toBe(true);
    expect(played.request().postDataJSON()).toMatchObject({ action: { kind: "call" } });
  } finally { await guest.context().close(); }
});

test("a Koi-Koi player invite opens the exact flower table without a link credential", async ({ page, browser, baseURL }, info) => {
  const url = urls(baseURL!);
  const guest = await phone(browser);
  let share = "";
  const hostName = `Peony ${info.project.name}`, guestName = `Clover ${info.project.name}`;
  const mock = fixture((_, visitor) => joinByLink(visitor, url, "koi-koi", share));
  try {
    await Promise.all([mock(page, hostName), mock(guest, guestName)]);
    await Promise.all([page.goto(url("koi-koi/")), guest.goto(url())]);
    await optIn(page, hostName);
    await optIn(guest, guestName);
    const created = receive(page, url("api/koi-koi/tables"), "POST");
    await tap(page, "Invite a friend");
    const response = await created;
    expect(response.ok()).toBe(true);
    const { id } = await response.json() as { id: string };
    share = await page.getByRole("textbox", { name: "Invite link" }).inputValue();
    await send(page, guestName, { game: "koi-koi", tableId: id });
    const [opened] = await Promise.all([
      guest.waitForResponse(response => response.url() === url(`api/koi-koi/tables/${id}`) &&
        response.request().method() === "GET", { timeout: 26_000 }),
      accept(guest, url("koi-koi/"), hostName),
    ]);
    expect(opened.ok()).toBe(true);
    const view = await opened.json() as { id: string; seat: number; position: { ownCards: { id: number }[] } };
    expect(view).toMatchObject({ id, seat: 1 });
    const hand = guest.getByRole("group", { name: "Your cards", exact: true });
    await expect(hand.getByRole("button")).toHaveCount(view.position.ownCards.length);
    expect(await hand.locator("[data-card-id]").evaluateAll(cards => cards.map(card => Number(card.getAttribute("data-card-id")))))
      .toEqual(view.position.ownCards.map(card => card.id));
    expect(new URL(guest.url()).search + new URL(guest.url()).hash).toBe("");
  } finally { await guest.context().close(); }
});

for (const visibility of ["invite", "public"] as const) {
  const kind = visibility === "invite" ? "private" : "public";
  test(`a ${kind} Mahjong invite opens the exact saved seat without browsing taking it over`, async ({ page, browser, baseURL }, info) => {
    const url = urls(baseURL!);
    const hostName = `Heron ${kind} ${info.project.name}`, guestName = `Lotus ${kind} ${info.project.name}`;
    const guest = await phone(browser, { width: 740, height: 320 });
    const legacy = visibility === "invite" ? await phone(browser) : undefined;
    let share = "";
    const wire = observeWire(page);
    const visitorWire = observeWire(guest);
    const mock = fixture(async (_, visitor) => {
      // Commit through the existing WS join only in fixture mode.
      const payload = visibility === "public" ? { roomId: wire.snapshots.at(-1)!.roomId } :
        { inviteCode: new URL(share).hash.slice("#invite=".length) };
      return visitor.evaluate(({ wsPath, payload }) => new Promise<string>((resolve, reject) => {
        const socket = new WebSocket(wsPath);
        socket.onerror = () => reject(new Error("Fixture Mahjong connection failed."));
        socket.onmessage = ({ data }) => {
          const packet = JSON.parse(data);
          if (packet.type === "lease") {
            socket.send(JSON.stringify({ v: 1, type: "command", op: "joinRoom", requestId: crypto.randomUUID(),
              lease: packet.lease, payload }));
          } else if (packet.type === "ack") {
            socket.close();
            if (packet.status === "ok") resolve(packet.roomId);
            else reject(new Error(packet.error.code));
          }
        };
      }), { wsPath: url("ws").replace(/^http/, "ws"), payload });
    });
    try {
      await page.setViewportSize({ width: 740, height: 320 });
      await Promise.all([mock(page, hostName), mock(guest, guestName)]);
      if (legacy) await page.addInitScript(() => Object.defineProperty(navigator, "clipboard", {
        value: { writeText: async () => { throw new Error(); } },
      }));
      const tableUrl = url("mahjong/");
      await Promise.all([page.goto(tableUrl), guest.goto(url("solitaire/"))]);
      await optIn(page, hostName);
      await optIn(guest, guestName);
      await tap(page, "Play with friends");
      const create = page.getByRole("dialog", { name: "Create a table" });
      const mode = create.getByLabel("Who can join?");
      await expect(mode).toHaveValue("invite");
      if (visibility === "public") await mode.selectOption("public");
      await create.getByRole("button", { name: "Create table" }).tap();
      await expect(page.getByRole("heading", { name: "Take a seat." })).toBeVisible();
      const { roomId: id, self } = wire.snapshots.at(-1)!;
      expect(wire.sent.find(frame => frame.type === "command" && frame.op === "createRoom")).toMatchObject({
        payload: { visibility, matchHands: 1, fillBots: false },
      });
      const shareHint = "Copy one link to share with your friends before starting.";
      if (legacy) {
        await expect(page.getByText(shareHint)).toBeVisible();
        await tap(page, "Invite friends");
        const sharePanel = page.getByRole("dialog", { name: "Invite friends" });
        await sharePanel.getByRole("button", { name: "Copy invite link" }).tap();
        await expect(sharePanel.getByText(/Link selected/)).toBeVisible();
        share = await sharePanel.getByRole("textbox", { name: "Invite link" }).inputValue();
        expect(share).toBe(`${tableUrl}#invite=${self.inviteCode}`);
        expect(self.inviteCode).toMatch(/^[A-Za-z0-9_-]{16}$/);
        await sharePanel.getByRole("button", { name: "Close dialog" }).tap();
      } else {
        expect(self.inviteCode).toBeNull();
        await expect(button(page, "Invite friends")).toBeHidden();
        await expect(page.getByText(shareHint)).toBeHidden();
      }

      await send(page, guestName, { game: "mahjong", tableId: id });
      await accept(guest, tableUrl, hostName);
      await expect(guest.getByRole("heading", { name: "Take a seat." })).toBeVisible();
      await expect(page).toHaveURL(tableUrl);
      await expect(page.getByRole("heading", { name: guestName, exact: true })).toBeVisible();
      expect(visitorWire.sent.filter(frame => frame.type === "sync").map(frame => frame.roomId)).toEqual([id]);
      expect(visitorWire.snapshots.at(-1)).toMatchObject({ roomId: id, room: { visibility }, self: { inviteCode: null } });
      await expect(button(guest, "Invite players")).toBeHidden();
      await expect(button(guest, "Invite friends")).toBeHidden();
      await expect(guest.getByText(shareHint)).toBeHidden();
      await guest.screenshot({ path: info.outputPath("mahjong-handoff-landscape.png") });

      const otherTab = await guest.context().newPage();
      const otherWire = observeWire(otherTab);
      await otherTab.goto(tableUrl);
      await expect(button(otherTab, "Resume your table")).toBeEnabled();
      await openLobby(otherTab);
      expect(otherWire.sent.filter(frame => frame.type === "sync")).toEqual([]);
      await expect(guest.locator(".connection")).toHaveAttribute("data-status", "online");
      await otherTab.close();

      if (!legacy) return;
      await legacy.goto(share);
      await tap(legacy, "Join");
      await expect(legacy.getByRole("heading", { name: "Take a seat." })).toBeVisible();
      await page.getByRole("button", { name: /^Add bot to / }).first().tap();
      for (const player of [page, guest, legacy]) await tap(player, "Mark ready");
      await tap(page, "Start match");
      await expect(guest.locator(".hand-tile")).not.toHaveCount(0);
      expect(visitorWire.snapshots.at(-1)).toMatchObject({ roomId: id, room: { status: "playing" } });
    } finally { await Promise.all([guest.context().close(), legacy?.context().close()]); }
  });
}
