import { expect, test } from "@playwright/test";
import type { Locator, Page, Response } from "@playwright/test";

type Table<P> = {
  id: string; seat: number; revision: number; status: string; position: P; invite: string | null;
  seats: { seat: number; name: string; bot: boolean; occupied: boolean }[];
};
type Solo<P> = { variant: string; revision: number; position: P };
type Card = { rank: number; suit: number };
type Poker = {
  handNumber: number; actingSeat: number | null; ownCards: Card[]; board: Card[];
  legalActions: { raise?: { minTo: number; maxTo: number }; canFold: boolean; canCheck: boolean; callAmount: number | null } | null;
  result: { reason: number; showdownHands: { seat: number; cards: Card[] }[] } | null; matchWinnerSeat: number | null;
};
type Klondike = {
  stockCount: number; moves: number; waste: { rank: number; suit: string } | null;
  tableau: { faceUp: boolean; rank?: number | null; suit?: string | null }[][];
  legalMoves: { from: string; to: string; count: number }[];
};
type Sudoku = { given: number[]; values: number[]; moves: number; conflicts: number[] };
type WordTile = { id: number; letter: string; points: number; blank: boolean };
type Words = {
  variant: "duel" | "classic" | "quick"; rows: number; columns: number; rackSize: number; exchangeLimit: number;
  rack: WordTile[]; turn: number; board: { row: number; column: number; tile: WordTile; seat: number; recent?: boolean }[];
  premiums: { row: number; column: number; kind: string }[];
  scores: number[]; rackSizes: number[]; bagCount: number; lastMove: string | null;
};

const routes = [
  ["holdem", "Texas Hold’em", "Set up Texas Hold’em", "poker/resume", "holdem", "Play a bot"],
  ["solitaire", "Solitaire", "Play Solitaire", "solo/solitaire", "solitaire", "Deal cards"],
  ["sudoku", "Sudoku", "Play Sudoku", "solo/sudoku", "sudoku", "Start puzzle"],
  ["word-tiles", "Jade Letters", "Set up Jade Letters", "words/resume", "words", "Play a bot"],
  ["farkle", "Farkle", "Set up Farkle", "farkle/resume", "farkle", "Play a bot"],
  ["backgammon", "Backgammon", "Set up Backgammon", "backgammon/resume", "backgammon", "Play a bot"],
] as const;
const button = (page: Page | Locator, name: string | RegExp) => page.getByRole("button", { name, exact: true });
const watch = (page: Page, url: string, method = "POST") =>
  page.waitForResponse(response => response.url() === url && response.request().method() === method);

async function body<T>(response: Response): Promise<T> {
  const raw = await response.text();
  expect(response.ok(), `${response.status()} on ${new URL(response.url()).pathname}: ${response.ok() ? "" : raw}`).toBe(true);
  return JSON.parse(raw) as T;
}

async function fits(page: Page) {
  const width = page.viewportSize()!.width;
  await expect.poll(() => page.evaluate(() => document.documentElement.scrollWidth)).toBeLessThanOrEqual(width);
}

async function targets(...controls: Locator[]) {
  for (const control of controls) {
    await expect(control).toBeVisible();
    const { width, height } = (await control.boundingBox())!;
    expect(Math.min(width, height)).toBeGreaterThanOrEqual(44);
  }
}

async function desktopCrop(page: Page, playArea: Locator, path: string) {
  const size = page.viewportSize()!;
  await page.setViewportSize({ width: 1366, height: 900 });
  await fits(page);
  await playArea.screenshot({ path });
  await page.setViewportSize(size);
}

const portrait = { width: 320, height: 730 };
const jadeLettersHeading = (page: Page) => page.getByRole("heading", { name: "Jade Letters", level: 1, exact: true });
const wordBoard = (page: Page) => page.getByRole("group", { name: "Jade Letters board", exact: true });
const wordRack = (page: Page) => page.getByRole("group", { name: "Your tile rack" });
const wordSquare = (board: Locator, row: number, column: number, columns: number) =>
  board.locator(`[data-word-index="${row * columns + column}"]`);
const wordTile = (rack: Locator, id: number) => rack.locator(`[data-tile="${id}"]`);
const shortWords = ["AM", "AN", "AS", "AT", "BE", "BY", "DO", "GO", "HE", "IF", "IN", "IS", "IT", "ME", "MY", "NO",
  "OF", "ON", "OR", "OX", "SO", "TO", "UP", "US", "WE"];

function openingWords(rack: WordTile[]) {
  return shortWords.flatMap(word => {
    const first = rack.find(tile => tile.letter === word[0]);
    const second = rack.find(tile => tile.letter === word[1]);
    return first && second ? [[first, second] as const] : [];
  });
}

function findWordLink(racks: [WordTile[], WordTile[]]) {
  for (const opening of openingWords(racks[0])) {
    for (const [offset, anchor] of opening.entries()) {
      for (const word of shortWords) {
        const above = word[1] === anchor.letter;
        if (!above && word[0] !== anchor.letter) continue;
        const tile = racks[1].find(tile => tile.letter === word[above ? 0 : 1]);
        if (tile) return { opening, reply: { row: above ? 6 : 8, column: 7 + offset, tile, word, anchor } };
      }
    }
  }
}

async function wordPan(board: Locator) {
  return board.evaluate(board => {
    const pan = board.closest(".word-board-pan")!;
    return {
      horizontal: pan.scrollWidth > pan.clientWidth, vertical: pan.scrollHeight > pan.clientHeight,
      centeredX: Math.abs(2 * pan.scrollLeft + pan.clientWidth - pan.scrollWidth) <= 2,
      centeredY: Math.abs(2 * pan.scrollTop + pan.clientHeight - pan.scrollHeight) <= 2,
    };
  });
}

async function tapWordSquare(page: Page, square: Locator) {
  await page.locator(".word-board-pan").scrollIntoViewIfNeeded();
  const { x, y, width, height } = (await square.boundingBox())!;
  const center = { x: x + width / 2, y: y + height / 2 };
  expect(await square.evaluate((square, { x, y }) => square.contains(document.elementFromPoint(x, y)), center)).toBe(true);
  await page.touchscreen.tap(center.x, center.y);
}

const wordFixture = (position: Words) => ({
  id: `test-${position.variant}`, seat: 0, revision: 3, status: "playing", invite: null,
  seats: [{ seat: 0, name: "Bamboo", occupied: true }, { seat: 1, name: "Pine", occupied: true }],
  position: { status: "playing", winners: [], detail: null, ...position },
});

const pokerCards = (page: Page) => page.getByRole("group", { name: "Your cards", exact: true }).getByRole("img");
const suitNames = ["clubs", "diamonds", "hearts", "spades"];
const cardName = (rank: number, suit: string) => `${({ 1: "Ace", 11: "Jack", 12: "Queen", 13: "King", 14: "Ace" } as Record<number, string>)[rank] ?? rank} of ${suit}`;

test.describe("mounted Jade Court expansion", () => {
  test.beforeEach(async ({ baseURL, page }) => {
    test.skip(!baseURL || !/^\/(?:game-)?portal\/$/.test(new URL(baseURL).pathname), "Requires the mounted portal runtime.");
    await page.setViewportSize(portrait);
  });

  test("catalog links, canonical paths and lazy assets stay isolated", async ({ page, baseURL, request }) => {
    const url = (path = "") => new URL(path, baseURL).href;
    const mount = new URL(url()).pathname;
    const seen = new Set<string>();
    const errors: string[] = [];
    page.on("request", event => seen.add(new URL(event.url()).pathname));
    page.on("pageerror", error => errors.push(error.message));
    await page.goto(url());
    await expect(page.getByRole("heading", { name: "Find your next move." })).toBeVisible();
    const featureAssets = () => [...seen].filter(path =>
      /\/assets\/(?:holdem|solitaire|sudoku|words|farkle|backgammon|multiplayer|solo|cards|shared|room)-/.test(path));
    expect(featureAssets()).toEqual([]);
    for (const [route, name, linkName, api, chunk, entry] of routes) {
      const bare = await request.get(url(`${route}?from=bookmark`), { maxRedirects: 0 });
      expect(bare.status()).toBe(308);
      expect(bare.headers().location).toBe(`${mount}${route}/?from=bookmark`);
      seen.clear();
      const opened = watch(page, url(`api/${api}`), "GET");
      const card = page.getByRole("link", { name: linkName, exact: true });
      await expect(card).toHaveAttribute("href", `${mount}${route}/`);
      await expect(card.getByRole("heading", { name, exact: true })).toBeVisible();
      await card.tap();
      await expect(page.getByRole("heading", { name, exact: true })).toBeVisible();
      await expect(page).toHaveTitle(`${name} · Jade Court`);
      expect((await opened).ok()).toBe(true);
      await expect(button(page, entry)).toBeEnabled();
      await targets(button(page, entry));
      await fits(page);
      expect(featureAssets()).toEqual(expect.arrayContaining([expect.stringMatching(new RegExp(`/assets/${chunk}-[\\w-]+\\.js$`))]));
      expect(featureAssets().filter(path => routes.some(([, , , , other]) =>
        other !== chunk && path.includes(`/assets/${other}-`)))).toEqual([]);
      expect([...seen].filter(path => !path.startsWith(mount))).toEqual([]);
      await page.getByRole("link", { name: "‹ All games", exact: true }).tap();
      await expect(page).toHaveURL(url());
    }
    expect(errors).toEqual([]);
  });

  test("Solitaire draws and moves real cards, then reloads and restarts", async ({ page, baseURL }, info) => {
    const url = (path: string) => new URL(path, baseURL).href;
    const game = url("api/solo/solitaire");
    const moves = `${game}/moves`;
    await page.goto(url("solitaire/"));
    const deal = watch(page, game);
    await button(page, "Deal cards").tap();
    const dealReply = await deal;
    expect(dealReply.request().postDataJSON()).toEqual({ variant: "draw-one" });
    let table = await body<Solo<Klondike>>(dealReply);
    const board = page.getByRole("group", { name: "Solitaire board", exact: true });
    const under = table.position.tableau.flat().filter(card => !card.faceUp);
    expect(under.length).toBeGreaterThan(0);
    expect(under.every(card => card.rank == null && card.suit == null)).toBe(true);
    await expect(board.getByRole("img", { name: "Face-down card" })).toHaveCount(under.length);
    const drawn = watch(page, moves);
    await button(board, /^Draw card,/).tap();
    const drawReply = await drawn;
    expect(drawReply.request().postDataJSON()).toEqual({ revision: table.revision, action: { kind: "draw" } });
    const first = await body<Solo<Klondike>>(drawReply);
    expect(first.position.stockCount).toBe(table.position.stockCount - 1);
    await expect(button(board, `Waste: ${cardName(first.position.waste!.rank, first.position.waste!.suit)}`)).toBeVisible();
    table = first;
    for (let attempt = 0; !table.position.legalMoves.length && table.position.stockCount > 0 && attempt < 23; attempt++) {
      const drawn = watch(page, moves);
      const stock = button(board, /^Draw card,/);
      await expect(stock).toBeEnabled();
      await stock.tap();
      table = await body(await drawn);
    }
    const action = table.position.legalMoves[0];
    expect(action, "A fresh deal should offer a legal move after drawing through stock").toBeDefined();
    const origin = board.locator(`[data-control="${action.from}/${action.count}"]`);
    const destination = board.locator(`[data-control="${action.to}/1"]`);
    await origin.focus();
    await page.keyboard.press("Enter");
    await expect(destination).toHaveAttribute("data-target", "");
    const moved = watch(page, moves);
    await destination.tap();
    const result = await moved;
    expect(result.request().postDataJSON()).toEqual({ revision: table.revision, action: { kind: "move", ...action } });
    const after = await body<Solo<Klondike>>(result);
    expect(after.position.moves).toBe(table.position.moves + 1);
    const resumed = watch(page, game, "GET");
    await page.reload();
    expect((await body<{ table: Solo<Klondike> }>(await resumed)).table).toEqual(after);
    await expect(page.getByText(`Draw one · ${after.position.moves} moves`, { exact: true })).toBeVisible();
    await fits(page);
    await expect.poll(() => board.evaluate(node => node.parentElement!.scrollWidth > node.parentElement!.clientWidth)).toBe(true);
    await page.screenshot({ path: info.outputPath("solitaire-320.png"), fullPage: true });
    await desktopCrop(page, board, info.outputPath("solitaire-desktop.png"));
    await button(page, "Restart game").tap();
    const dialog = page.getByRole("dialog", { name: "Restart game?" });
    await expect(button(dialog, "Cancel")).toBeFocused();
    const restarted = watch(page, game);
    await button(dialog, "Restart game").tap();
    expect((await body<Solo<Klondike>>(await restarted)).position.moves).toBe(0);
    await expect(page.getByText("Draw one · 0 moves", { exact: true })).toBeVisible();
  });

  test("Sudoku gives keyboard and touch input, readonly clues, hints and saved progress", async ({ page, baseURL }, info) => {
    const url = (path: string) => new URL(path, baseURL).href;
    const game = url("api/solo/sudoku");
    const moves = `${game}/moves`;
    await page.goto(url("sudoku/"));
    const difficulty = page.getByRole("combobox", { name: "Difficulty" });
    await expect(difficulty).toBeEnabled();
    await expect(difficulty).toHaveValue("standard");
    await difficulty.selectOption("easy");
    const started = watch(page, game);
    await button(page, "Start puzzle").tap();
    const start = await body<Solo<Sudoku>>(await started);
    expect(start.variant).toBe("easy");
    const board = page.getByRole("group", { name: "Sudoku board", exact: true });
    await expect(board.getByRole("button")).toHaveCount(81);
    const at = (index: number) => board.locator(`[data-index="${index}"]`);
    const open = start.position.given.findIndex((value, index, values) => !value && values.slice(index - index % 9, index - index % 9 + 9).some(Boolean));
    const rowStart = open - open % 9;
    const given = start.position.given.findIndex((value, index) => !!value && index >= rowStart && index < rowStart + 9);
    const value = start.position.given[given];
    await at(given).focus();
    await expect(at(given)).toHaveAccessibleName(/given/);
    await expect(button(page, String(value))).toBeDisabled();
    await at(open).focus();
    const neighbor = open % 9 < 8 ? open + 1 : open - 1;
    await page.keyboard.press(neighbor > open ? "ArrowRight" : "ArrowLeft");
    await expect(at(neighbor)).toBeFocused();
    await page.keyboard.press(neighbor > open ? "ArrowLeft" : "ArrowRight");
    const entered = watch(page, moves);
    await page.keyboard.press(String(value));
    const enter = await entered;
    expect(enter.request().postDataJSON()).toEqual({ revision: start.revision, action: { kind: "set", index: open, value } });
    const wrong = await body<Solo<Sudoku>>(enter);
    expect(wrong.position.conflicts).toContain(open);
    await expect(at(open)).toHaveAccessibleName(/conflict/);
    await expect(at(given)).toHaveAccessibleName(/conflict/);
    const cleared = watch(page, moves);
    await button(page, "Clear square").tap();
    const clear = await body<Solo<Sudoku>>(await cleared);
    expect(clear.position.values[open]).toBe(0);
    expect(clear.position.conflicts).toEqual([]);
    const hinted = watch(page, moves);
    await button(page, "Get a hint").tap();
    const hint = await hinted;
    expect(hint.request().postDataJSON()).toEqual({ revision: clear.revision, action: { kind: "hint" } });
    const saved = await body<Solo<Sudoku>>(hint);
    const changed = saved.position.values.findIndex((entry, index) => entry !== clear.position.values[index]);
    expect(changed).toBeGreaterThanOrEqual(0);
    expect(saved.position.values.filter((entry, index) => entry !== clear.position.values[index])).toHaveLength(1);
    await page.reload();
    await expect(at(changed)).toHaveText(String(saved.position.values[changed]));
    await expect(page.getByText(`Easy · ${saved.position.moves} moves`, { exact: true })).toBeVisible();
    await fits(page);
    await targets(button(page, "Get a hint"), button(page, "Restart puzzle"), button(page, "1"));
    await page.screenshot({ path: info.outputPath("sudoku-320.png"), fullPage: true });
    await desktopCrop(page, page.locator(".sudoku-panel"), info.outputPath("sudoku-desktop.png"));
    await difficulty.selectOption("hard");
    await button(page, "Restart puzzle").tap();
    const dialog = page.getByRole("dialog", { name: "Restart puzzle?" });
    await button(dialog, "Cancel").tap();
    await expect(at(changed)).toHaveText(String(saved.position.values[changed]));
    await button(page, "Restart puzzle").tap();
    const restarted = watch(page, game);
    await button(dialog, "Restart puzzle").tap();
    expect(await body(await restarted)).toMatchObject({ variant: "hard", position: { moves: 0 } });
    await expect(page.getByText("Hard · 0 moves", { exact: true })).toBeVisible();
  });

  test("Jade Letters keeps a private draft, scores a word and resumes after the bot turn", async ({ page, baseURL }, info) => {
    const url = (path: string) => new URL(path, baseURL).href;
    const game = url("api/words/tables");
    await page.goto(url("word-tiles/"));
    await expect(jadeLettersHeading(page)).toBeVisible();
    await expect(page).toHaveTitle("Jade Letters · Jade Court");
    await expect(button(page, "Play a bot")).toBeEnabled();
    await page.screenshot({ path: info.outputPath("jade-letters-setup-320.png"), fullPage: true });
    const create = async () => {
      const created = watch(page, game);
      await button(page, "Play a bot").tap();
      const response = await created;
      expect(response.request().postDataJSON()).toEqual({ players: 2, bots: true });
      return body<Table<Words>>(response);
    };
    const pick = (table: Table<Words>) => !table.position.board.length ? openingWords(table.position.rack)[0] : undefined;
    let table = await create();
    for (let attempt = 0; !pick(table) && attempt < 10; attempt++) {
      await button(page, "Leave table").tap();
      await button(page.getByRole("dialog", { name: "Leave table?" }), "Leave table").tap();
      await button(page, "Set up another game").tap();
      table = await create();
    }
    const tiles = pick(table)!;
    expect(tiles, "The rack has a common two-letter opener").toBeDefined();
    const endpoint = `${game}/${table.id}/actions`;
    const writes: unknown[] = [];
    page.on("request", request => { if (request.url() === endpoint) writes.push(request.postDataJSON()); });
    const rack = wordRack(page);
    const board = wordBoard(page);
    const { rows, columns } = table.position;
    const row = Math.floor(rows / 2), column = Math.floor(columns / 2);
    const center = wordSquare(board, row, column, columns);
    const neighbor = wordSquare(board, row, column + 1, columns);
    const tile = (id: number) => wordTile(rack, id);
    const bot = table.seats[1];
    expect(table).toMatchObject({ seat: 0, status: "playing", invite: null });
    expect(bot).toMatchObject({ seat: 1, bot: true, occupied: true });
    expect(table.position).toMatchObject({ variant: "duel", rows: 15, columns: 15, rackSize: 7, turn: 0, scores: [0, 0] });
    await expect(jadeLettersHeading(page)).toBeVisible();
    await expect(page.getByText("Duel · 7 tiles")).toBeVisible();
    await expect(board.getByRole("button")).toHaveCount(rows * columns);
    await expect(rack.getByRole("button")).toHaveCount(table.position.rackSize);
    await expect(center).toHaveAccessibleName(/center, empty$/);
    await expect(center).toHaveText("✦");
    await expect(wordSquare(board, 0, 0, columns)).toHaveText("2L");
    await expect(wordSquare(board, 0, 5, columns)).toHaveText("3W");
    const marks = new Map([["double-letter", "2L"], ["triple-letter", "3L"], ["double-word", "2W"], ["triple-word", "3W"]]);
    for (const [kind, mark] of marks) {
      const premium = table.position.premiums.find(premium => premium.kind === kind)!;
      await expect(wordSquare(board, premium.row, premium.column, columns)).toHaveText(mark);
      await expect(page.getByRole("list", { name: "Premium squares" })
        .getByText(new RegExp(`^${kind.replace("-", " ")}$`, "i"))).toBeVisible();
    }
    await expect.poll(() => wordPan(board)).toEqual({ horizontal: true, vertical: true, centeredX: true, centeredY: true });
    await expect(board.locator('[tabindex="0"]')).toHaveCount(1);
    await expect(center).toHaveAttribute("tabindex", "0");
    await targets(center, neighbor, tile(tiles[0]!.id), button(page, "Submit word"), button(page, "Center board"));
    await tile(tiles[0]!.id).tap();
    await tapWordSquare(page, center);
    await expect(center).toHaveAttribute("data-draft", "");
    await expect(center).not.toHaveAttribute("data-premium", /.+/);
    expect(writes).toEqual([]);
    await button(page, "Recall tiles").tap();
    await expect(center).toHaveText("✦");
    await expect(rack.getByRole("button")).toHaveCount(table.position.rack.length);
    await tile(tiles[0]!.id).tap();
    await center.focus();
    await page.keyboard.press("Enter");
    await page.keyboard.press("ArrowRight");
    await expect(neighbor).toBeFocused();
    await tile(tiles[1]!.id).tap();
    await neighbor.tap();
    await expect(neighbor).toHaveAccessibleName(/draft/);
    expect(writes).toEqual([]);
    await fits(page);
    await button(page, "Center board").tap();
    await page.screenshot({ path: info.outputPath("word-draft-320.png"), fullPage: true });
    await desktopCrop(page, page.locator(".word-layout"), info.outputPath("word-draft-desktop.png"));
    const played = watch(page, endpoint);
    await button(page, "Submit word").tap();
    const play = await body<Table<Words>>(await played);
    expect(writes).toEqual([{ revision: table.revision, action: { kind: "place", placements: [
      { row, column, tileId: tiles[0]!.id }, { row, column: column + 1, tileId: tiles[1]!.id },
    ] } }]);
    expect(play.revision).toBe(table.revision + 1);
    expect(play.position.turn).toBe(table.seat);
    expect(play.position.scores[table.seat]).toBe(tiles[0].points + tiles[1].points);
    expect(play.position.board.filter(cell => cell.seat === table.seat)).toEqual(tiles.map((tile, offset) =>
      ({ row, column: column + offset, tile, seat: table.seat })));
    const botCells = play.position.board.filter(cell => cell.seat === bot.seat);
    const lastMove = play.position.lastMove ?? "";
    const botScore = play.position.scores[bot.seat];
    if (lastMove === "Seat 2 passed.") {
      expect(botCells).toEqual([]);
      expect(botScore).toBe(0);
    } else {
      expect(botCells.length).toBeGreaterThan(0);
      expect(lastMove).toMatch(new RegExp(`^Seat 2 played [A-Z]+(?:, [A-Z]+)* \\(\\+${botScore}\\)\\.$`));
    }
    expect(play.position.board.filter(cell => cell.recent)).toEqual(botCells);
    expect(play.position.bagCount).toBe(table.position.bagCount - tiles.length - botCells.length);
    await expect(board.locator("[data-recent]")).toHaveCount(botCells.length);
    const scoreRow = page.getByRole("list", { name: "Scores and rack sizes" }).getByRole("listitem").nth(bot.seat);
    await expect(scoreRow).toContainText(bot.name);
    await expect(scoreRow).toContainText(`${botScore} pts`);
    await expect(center).not.toHaveAttribute("data-draft", "");
    await expect(center).not.toHaveAttribute("data-premium", /.+/);
    await expect(center).toHaveText(`${tiles[0]!.letter}${tiles[0]!.points}`);
    await expect(page.getByText(`Last move: ${lastMove}`, { exact: true })).toBeVisible();
    await info.attach("jade-letters-turn", {
      body: JSON.stringify({
        humanWord: tiles.map(tile => tile.letter).join(""),
        botMove: lastMove,
        scores: play.position.scores,
        revision: [table.revision, play.revision],
      }),
      contentType: "application/json",
    });
    await button(page, "Center board").tap();
    await page.screenshot({ path: info.outputPath("word-duel-320-committed.png"), fullPage: true });
    const resumed = watch(page, `${game}/${table.id}`, "GET");
    await page.reload();
    expect(await body<Table<Words>>(await resumed)).toEqual(play);
    await expect(jadeLettersHeading(page)).toBeVisible();
    await expect(center).toHaveAccessibleName(new RegExp(tiles[0]!.letter));
    await expect(rack.locator("[data-tile]")).toHaveCount(play.position.rack.length);
  });

  test("Jade Letters Duel scores a real mobile and desktop guest's linked words", async ({ page, browser, baseURL }, info) => {
    const url = (path: string) => new URL(path, baseURL).href;
    const game = url("api/words/tables");
    const desktop = await browser.newContext({ viewport: { width: 1366, height: 900 } });
    try {
      const peer = await desktop.newPage();
      await page.goto(url("word-tiles/"));
      await page.getByRole("textbox", { name: "Display name" }).fill("Linden");
      const players = [page, peer];
      let tables: Table<Words>[];
      let link: ReturnType<typeof findWordLink>;
      let endpoint: string;
      for (let attempt = 0; ; attempt++) {
        const made = watch(page, game);
        await button(page, "Invite a friend").tap();
        const created = await body<Table<Words>>(await made);
        endpoint = `${game}/${created.id}`;
        await expect(jadeLettersHeading(page)).toBeVisible();
        await expect(page.getByRole("heading", { name: "Send the invitation", exact: true })).toBeVisible();
        const invite = new URL(url("word-tiles/"));
        invite.hash = new URLSearchParams({ invite: created.invite! }).toString();
        await expect(page.getByRole("textbox", { name: "Invite link" })).toHaveValue(invite.href);
        invite.search = "?from=friend";
        const bare = new URL(invite);
        bare.pathname = bare.pathname.slice(0, -1);
        await peer.goto(bare.href);
        await expect(peer).toHaveURL(invite.href);
        await expect(jadeLettersHeading(peer)).toBeVisible();
        await expect(peer).toHaveTitle("Jade Letters · Jade Court");
        await expect(peer.getByRole("heading", { name: "A friend saved you a seat.", exact: true })).toBeVisible();
        if (attempt === 0) await peer.screenshot({ path: info.outputPath("jade-letters-invite-desktop.png") });
        await peer.getByRole("textbox", { name: "Display name" }).fill("Rowan");
        const joined = watch(peer, url("api/words/join"));
        await button(peer, "Join table").click();
        const visitor = await body<Table<Words>>(await joined);
        invite.hash = "";
        await expect(peer).toHaveURL(invite.href);
        const host = await page.request.get(endpoint);
        await expect(host).toBeOK();
        tables = [await host.json() as Table<Words>, visitor];
        link = findWordLink([tables[0].position.rack, tables[1].position.rack]);
        if (link || attempt === 5) break;
        await expect(wordBoard(page)).toBeVisible();
        await button(page, "Leave table").tap();
        await button(page.getByRole("dialog", { name: "Leave table?" }), "Leave table").tap();
        await button(page, "Set up another game").tap();
      }
      expect(link, "The seats can form two linked words from their real racks").toBeDefined();
      const { opening, reply } = link!;
      for (const table of tables) expect(table.position).toMatchObject({ variant: "duel", bagCount: 90, board: [], turn: 0 });
      expect(new Set(tables.flatMap(table => table.position.rack.map(tile => tile.id))).size).toBe(14);
      const draft = async (seat: number, row: number, column: number, tile: WordTile) => {
        const viewer = players[seat];
        const square = wordSquare(wordBoard(viewer), row, column, 15);
        const control = wordTile(wordRack(viewer), tile.id);
        if (viewer === page) {
          await control.tap();
          await tapWordSquare(viewer, square);
        } else {
          await control.click();
          await square.click();
        }
        await expect(square).toHaveAccessibleName(/draft/);
      };
      const submit = async (seat: number) => {
        const viewer = players[seat];
        const posted = watch(viewer, `${endpoint}/actions`);
        if (viewer === page) await button(viewer, "Submit word").tap();
        else await button(viewer, "Submit word").click();
        return body<Table<Words>>(await posted);
      };

      await draft(0, 7, 7, opening[0]);
      await draft(0, 7, 8, opening[1]);
      await expect(wordBoard(peer).locator("[data-draft], [data-recent]")).toHaveCount(0);
      await page.evaluate(() => window.scrollTo(0, 0));
      await expect(wordRack(page)).toBeInViewport({ ratio: 1 });
      await expect(button(page, "Submit word")).toBeInViewport({ ratio: 1 });
      await page.screenshot({ path: info.outputPath("word-duel-mobile-320-draft.png") });
      const first = await submit(0);
      const firstWord = opening.map(tile => tile.letter).join("");
      const firstScore = opening[0].points + opening[1].points;
      expect(first.position.lastMove).toBe(`Seat 1 played ${firstWord} (+${firstScore}).`);
      expect(first.position.scores[0]).toBe(firstScore);
      for (const viewer of players) {
        await expect(wordBoard(viewer).locator("[data-recent]")).toHaveCount(2);
        await expect(wordSquare(wordBoard(viewer), 7, 7, 15)).toHaveAccessibleName(/last play/);
      }

      await draft(1, reply.row, reply.column, reply.tile);
      await expect(wordBoard(peer).locator("[data-recent]")).toHaveCount(2);
      await expect(wordBoard(peer).locator("[data-draft]")).toHaveCount(1);
      await expect(wordBoard(page).locator("[data-draft]")).toHaveCount(0);
      await peer.locator(".word-layout").screenshot({ path: info.outputPath("word-duel-desktop-draft.png") });
      const second = await submit(1);
      const replyScore = reply.anchor.points + reply.tile.points;
      expect(second.position.lastMove).toBe(`Seat 2 played ${reply.word} (+${replyScore}).`);
      expect(second.position.scores[1]).toBe(replyScore);
      expect(second.position.board.filter(cell => cell.recent)).toEqual([
        { row: reply.row, column: reply.column, seat: 1, tile: reply.tile, recent: true },
      ]);
      for (const viewer of players) {
        await expect(wordBoard(viewer).locator("[data-recent]")).toHaveCount(1);
        await expect(wordBoard(viewer).locator("[data-draft]")).toHaveCount(0);
        await expect(viewer.getByRole("list", { name: "Scores and rack sizes" })).toContainText(`${replyScore} pts`);
      }
      await fits(peer);
      await peer.locator(".word-layout").screenshot({ path: info.outputPath("word-duel-desktop-committed.png") });
      await button(page, "Center board").tap();
      await fits(page);
      await page.screenshot({ path: info.outputPath("word-duel-multiplayer-320.png"), fullPage: true });
      const restored = watch(peer, endpoint, "GET");
      await peer.reload();
      expect((await body<Table<Words>>(await restored)).position).toEqual(second.position);
      await expect(wordSquare(wordBoard(peer), reply.row, reply.column, 15)).toHaveAccessibleName(/last play/);
    } finally { await desktop.close(); }
  });

  test("Jade Letters assigns and recalls distinct Classic blanks by touch and keyboard", async ({ page, baseURL, browserName }, info) => {
    await page.setViewportSize({ width: 390, height: 780 });
    const url = (path: string) => new URL(path, baseURL).href;
    const rackTiles = [
      { id: 99, letter: "?", points: 0, blank: true },
      ...[..."ECIND"].map((letter, index) => ({ id: index + 10, letter, points: letter === "C" ? 3 : 1, blank: false })),
      { id: 100, letter: "?", points: 0, blank: true },
    ];
    const table = wordFixture({
      variant: "classic", rows: 15, columns: 15, rackSize: 7, exchangeLimit: 0, turn: 0, rack: rackTiles, board: [],
      premiums: [{ row: 7, column: 7, kind: "double-word" }], scores: [0, 0], rackSizes: [7, 7], bagCount: 6, lastMove: null,
    });
    const game = url(`api/words/tables/${table.id}`);
    const writes: unknown[] = [];
    await page.route(url("api/words/resume"), route => route.fulfill({ json: { tableId: table.id } }));
    await page.route(game, route => route.fulfill({ json: table }));
    await page.route(`${game}/actions`, async route => {
      writes.push(route.request().postDataJSON());
      await route.fulfill({ status: 400, json: { error: { code: "illegal_action" } } });
    });
    await page.goto(url("word-tiles/"));
    await expect(jadeLettersHeading(page)).toBeVisible();
    await expect(page.getByText("Classic · 7 tiles")).toBeVisible();
    const board = wordBoard(page), rack = wordRack(page);
    const center = wordSquare(board, 7, 7, 15), left = wordSquare(board, 7, 6, 15), right = wordSquare(board, 7, 8, 15);
    const first = wordTile(rack, 99), second = wordTile(rack, 100);
    const picker = page.getByRole("dialog", { name: "Choose a blank letter" });
    const cancel = button(picker, "Cancel");
    await expect(button(page, "Exchange tiles")).toBeDisabled();
    await expect(first).toHaveAccessibleName("Blank, 0 points · tile 1");
    await expect(first).toHaveText("?0");
    await expect(first.locator(".word-tile")).toHaveAttribute("data-blank", "");
    await fits(page);
    await expect.poll(() => wordPan(board)).toEqual({ horizontal: true, vertical: true, centeredX: true, centeredY: true });
    if (browserName === "chromium") {
      const pan = page.locator(".word-board-pan");
      await pan.scrollIntoViewIfNeeded();
      const area = (await pan.boundingBox())!;
      const x = area.x + area.width * .75, y = area.y + area.height * .75;
      const input = await page.context().newCDPSession(page);
      const scrolled = pan.evaluate(node => new Promise<boolean>(resolve => {
        node.addEventListener("scrollend", () => resolve(true), { once: true });
        setTimeout(() => resolve(false), 3500);
      }));
      await input.send("Input.dispatchTouchEvent", { type: "touchStart", touchPoints: [{ x, y }] });
      for (let step = 1; step <= 6; step++) {
        await input.send("Input.dispatchTouchEvent", {
          type: "touchMove", touchPoints: [{ x: x - step * 17, y: y - step * 20 }],
        });
      }
      await input.send("Input.dispatchTouchEvent", { type: "touchEnd", touchPoints: [] });
      await input.detach();
      expect(await scrolled).toBe(true);
    } else {
      await center.focus();
      await page.keyboard.press("Control+End");
      await expect(wordSquare(board, 14, 14, 15)).toBeFocused();
    }
    await expect.poll(() => wordPan(board)).toMatchObject({ centeredX: false, centeredY: false });
    await button(page, "Center board").tap();
    await expect(center).toBeFocused();
    await expect.poll(() => wordPan(board)).toMatchObject({ centeredX: true, centeredY: true });

    await first.focus();
    await page.keyboard.press("Space");
    await expect(first).toBeFocused();
    await expect(first).toHaveAttribute("aria-pressed", "true");
    await expect(page.getByText(/Blank selected/)).toBeVisible();
    await tapWordSquare(page, center);
    await expect(cancel).toBeFocused();
    await targets(button(picker, "A"), button(picker, "Z"), cancel);
    await page.keyboard.press("Escape");
    await expect(picker).toBeHidden();
    await expect(center).toBeFocused();
    await expect(center).toHaveText("2W");
    await expect(first).toHaveAttribute("aria-pressed", "true");
    await page.keyboard.press("Enter");
    await page.keyboard.press("O");
    await expect(center).toBeFocused();
    await expect(center).toHaveAccessibleName(/O, blank, 0 points, draft/);
    await expect(center).toHaveText("O0");
    await page.keyboard.press("Enter");
    await expect(center).toHaveText("2W");
    await expect(first).toHaveAttribute("aria-pressed", "true");
    await page.keyboard.press("Enter");
    await page.keyboard.press("a");
    await expect(center).toHaveText("A0");
    await expect(center.locator(".word-tile")).toHaveAttribute("data-blank", "");

    await second.tap();
    await tapWordSquare(page, right);
    await cancel.tap();
    await expect(right).toBeFocused();
    await expect(center).toHaveText("A0");
    await expect(second).toHaveAttribute("aria-pressed", "true");
    await tapWordSquare(page, right);
    await page.screenshot({ path: info.outputPath("word-blank-390.png") });
    await button(picker, "T").tap();
    await expect(right).toHaveText("T0");
    await wordTile(rack, 11).tap();
    await tapWordSquare(page, left);
    await expect(left.locator(".word-tile")).not.toHaveAttribute("data-blank", "");
    expect(writes).toEqual([]);
    await expect(page).toHaveURL(url("word-tiles/"));

    const submit = button(page, "Submit word");
    await submit.focus();
    await page.keyboard.press("Enter");
    await expect(page.getByText(/That play wasn’t accepted/)).toBeVisible();
    expect(writes).toEqual([{ revision: table.revision, action: { kind: "place", placements: [
      { row: 7, column: 7, tileId: 99, letter: "A" },
      { row: 7, column: 8, tileId: 100, letter: "T" },
      { row: 7, column: 6, tileId: 11 },
    ] } }]);
    await expect(submit).toBeFocused();
    await expect(center).toHaveText("A0");
    await expect(right).toHaveText("T0");
  });

  test("Jade Letters resumes a Quick board without premiums or lost tiles", async ({ page, baseURL }) => {
    const url = (path: string) => new URL(path, baseURL).href;
    const normal = (letter: string, id: number) => ({ id, letter, points: 1, blank: false });
    const table = wordFixture({
      variant: "quick", rows: 11, columns: 11, rackSize: 6, exchangeLimit: 3, turn: 0,
      rack: [..."ECINDL"].map((letter, index) => normal(letter, index + 1)), board: [
        { row: 5, column: 5, seat: 0, tile: normal("A", 44) },
        { row: 5, column: 6, seat: 0, tile: normal("T", 55) },
      ],
      premiums: [], scores: [2, 0], rackSizes: [6, 6], bagCount: 32, lastMove: "Seat 1 played AT (+2).",
    });
    await page.route(url("api/words/resume"), route => route.fulfill({ json: { tableId: table.id } }));
    await page.route(url(`api/words/tables/${table.id}`), route => route.fulfill({ json: table }));
    await page.goto(url("word-tiles/"));
    await expect(jadeLettersHeading(page)).toBeVisible();
    const board = wordBoard(page);
    const center = wordSquare(board, 5, 5, 11);
    await expect(board.getByRole("button")).toHaveCount(121);
    await expect(wordRack(page).getByRole("button")).toHaveCount(6);
    await expect(page.getByText("Quick · 6 tiles")).toBeVisible();
    await expect(page.getByRole("list", { name: "Premium squares" })).toBeHidden();
    await expect(board.locator("[data-premium]")).toHaveCount(0);
    await expect(center).toHaveText("A1");
    await button(page, "Exchange tiles").tap();
    for (const tile of table.position.rack.slice(0, 4)) await wordTile(wordRack(page), tile.id).tap();
    await expect(page.getByText("Select up to 3 to exchange. 3 selected.")).toBeVisible();
    await expect(wordTile(wordRack(page), table.position.rack[3].id)).toHaveAttribute("aria-pressed", "false");
    await page.reload();
    await expect(center).toHaveText("A1");
    await expect(wordSquare(board, 5, 6, 11)).toHaveText("T1");
    await expect(page.getByText("Last move: Seat 1 played AT (+2).")).toBeVisible();
  });

  test("Hold’em uses street totals and a seated next-hand control against a bot", async ({ page, baseURL }, info) => {
    const url = (path: string) => new URL(path, baseURL).href;
    const game = url("api/poker/tables");
    await page.goto(url("holdem/"));
    const created = watch(page, game);
    await button(page, "Play a bot").tap();
    const opening = await body<Table<Poker>>(await created);
    const endpoint = `${game}/${opening.id}`;
    await expect(pokerCards(page)).toHaveCount(2);
    const opposite = page.getByRole("list", { name: "Seats and chip counts" });
    await expect(opposite.getByRole("img", { name: "Face-down card" })).toHaveCount(2);
    await expect(page.getByText(/Hand 1 · Preflop · Blinds/)).toBeVisible();
    const amount = page.getByRole("spinbutton", { name: "Street total (chips)" });
    const range = opening.position.legalActions!.raise!;
    await expect(amount).toHaveAttribute("min", String(range.minTo));
    await expect(amount).toHaveAttribute("max", String(range.maxTo));
    await expect(amount).toHaveValue(String(range.minTo));
    await targets(button(page, "Raise"), button(page, "Fold"), button(page, "Leave table"));
    await fits(page);
    await page.screenshot({ path: info.outputPath("holdem-bot-320.png"), fullPage: true });
    await desktopCrop(page, page.locator(".holdem-layout"), info.outputPath("holdem-desktop.png"));
    const raised = watch(page, `${endpoint}/actions`);
    await button(page, "Raise").tap();
    const raise = await raised;
    expect(raise.request().postDataJSON()).toEqual({ revision: opening.revision, action: { kind: "raise", to: range.minTo } });
    let after = await body<Table<Poker>>(raise);
    for (let turn = 0; !after.position.result && turn < 4; turn++) {
      expect(after.position.actingSeat).toBe(opening.seat);
      const legal = after.position.legalActions!;
      const kind = legal.canFold ? "fold" : "check";
      if (!legal.canFold) {
        expect(legal.canCheck).toBe(true);
        await expect(button(page, "Fold")).toHaveCount(0);
      }
      const sent = watch(page, `${endpoint}/actions`);
      await button(page, kind === "fold" ? "Fold" : "Check").tap();
      const action = await sent;
      expect(action.request().postDataJSON()).toEqual({ revision: after.revision, action: { kind } });
      after = await body<Table<Poker>>(action);
    }
    expect(after.position.result).not.toBeNull();
    const next = watch(page, `${endpoint}/next`);
    await button(page, "Next hand").tap();
    const deal = await next;
    expect(deal.request().postDataJSON()).toEqual({ revision: after.revision });
    expect((await body<Table<Poker>>(deal)).position.handNumber).toBe(2);
    const resumed = watch(page, endpoint, "GET");
    await page.reload();
    expect((await body<Table<Poker>>(await resumed)).position.handNumber).toBe(2);
    await expect(page.getByText(/Hand 2 ·/)).toBeVisible();
  });

  test("private Hold’em shares only public cards until a two-guest showdown", async ({ page, browser, baseURL }, info) => {
    const url = (path: string) => new URL(path, baseURL).href;
    const game = url("api/poker/tables");
    await page.goto(url("holdem/"));
    await page.getByRole("textbox", { name: "Display name" }).fill("Host");
    const made = watch(page, game);
    await button(page, "Invite a friend").tap();
    const waiting = await body<Table<Poker>>(await made);
    expect(waiting.status).toBe("waiting");
    const endpoint = `${game}/${waiting.id}`;
    const invitation = new URL(await page.getByRole("textbox", { name: "Invite link" }).inputValue());
    expect(invitation.pathname).toBe(new URL(url("holdem/")).pathname);
    expect(invitation.hash).toBe(`#${new URLSearchParams({ invite: waiting.invite! })}`);
    const peer = await browser.newContext({ viewport: portrait, hasTouch: true });
    try {
      const visitor = await peer.newPage();
      invitation.search = "?from=friend";
      await visitor.goto(invitation.href);
      await expect(visitor).toHaveURL(invitation.href);
      await visitor.getByRole("textbox", { name: "Display name" }).fill("Visitor");
      const joined = watch(visitor, url("api/poker/join"));
      await button(visitor, "Join table").tap();
      const view = await body<Table<Poker>>(await joined);
      invitation.hash = "";
      await expect(visitor).toHaveURL(invitation.href);
      expect(view.seat).not.toBe(waiting.seat);
      expect(view.position.ownCards).toHaveLength(2);
      const assertCards = async (viewer: Page, cards: Card[]) => {
        await expect(pokerCards(viewer)).toHaveCount(2);
        for (const card of cards) await expect(pokerCards(viewer).and(viewer.getByRole("img", {
          name: cardName(card.rank, suitNames[card.suit]), exact: true,
        }))).toBeVisible();
      };
      await assertCards(visitor, view.position.ownCards);
      await expect(visitor.getByRole("img", { name: "Face-down card", exact: true })).toHaveCount(2);
      await expect(page.getByRole("img", { name: "Face-down card", exact: true })).toHaveCount(2);
      const hostView = await page.request.get(endpoint);
      const host = await hostView.json() as Table<Poker>;
      await assertCards(page, host.position.ownCards);
      expect(new Set([...host.position.ownCards, ...view.position.ownCards].map(card => `${card.rank}/${card.suit}`)).size).toBe(4);
      expect(host.position.result).toBeNull();
      const leader = host.position.actingSeat === host.seat ? page : visitor;
      const responder = leader === page ? visitor : page;
      const acting = leader === page ? host : view;
      await leader.getByRole("spinbutton", { name: "Street total (chips)" }).fill(String(acting.position.legalActions!.raise!.maxTo));
      const raised = watch(leader, `${endpoint}/actions`);
      await button(leader, "Raise").tap();
      const allIn = await body<Table<Poker>>(await raised);
      expect(allIn.position.result).toBeNull();
      const called = watch(responder, `${endpoint}/actions`);
      await button(responder, /^Call \d+$/).tap();
      const showdown = await body<Table<Poker>>(await called);
      expect(showdown.position.board).toHaveLength(5);
      expect(showdown.position.result).toMatchObject({ reason: 1, showdownHands: expect.arrayContaining([
        expect.objectContaining({ seat: host.seat, cards: host.position.ownCards }),
        expect.objectContaining({ seat: view.seat, cards: view.position.ownCards }),
      ]) });
      for (const viewer of [page, visitor]) {
        await expect(viewer.getByRole("img", { name: "Face-down card", exact: true })).toHaveCount(0);
        await expect(viewer.getByRole("group", { name: "Community cards" }).getByRole("img")).toHaveCount(5);
        await expect(button(viewer, "Next hand")).toHaveCount(showdown.position.matchWinnerSeat == null ? 1 : 0);
      }
      await fits(visitor);
      await visitor.screenshot({ path: info.outputPath("holdem-showdown-320.png"), fullPage: true });
    } finally { await peer.close(); }
  });
});
