import { expect, test, type Page } from "@playwright/test";
import { EMPTY, findHint, INK, matchesClues, UNDECIDED, type Clues, type Mark } from "../src/portal/games/nonogram-logic";
import { inkPlates } from "../src/portal/games/nonogram-puzzles";
import { applyMark, blankBoard, readProgress, undoMark, writeProgress } from "../src/portal/games/nonogram-save";

// Original print proofs, deliberately independent of the shipped clue arrays.
const proofs: Record<string, string[]> = {
  "plate-01": ["..#..", ".###.", ".###.", "#####", "..#.."],
  "plate-02": ["..#..", "..##.", "..###", "..#..", "#####"],
  "plate-03": ["###..", "#.#..", "###..", "..#..", "..###"],
  "plate-04": ["..#..", ".##..", ".###.", "#####", ".###."],
  "plate-05": ["...##...", "..####..", "...##...", "#######.", ".#####.#", "..#####.", "..#####.", "...###.."],
  "plate-06": ["...##...", "..####..", ".######.", "..####..", "...##...", "....#...", "...##...", "..##...."],
  "plate-07": ["##.....#", "##....##", "###..###", "########", ".######.", "..####..", ".#.##.#.", "...##..."],
  "plate-08": ["........", "..####..", ".######.", "###..###", "##....##", "##....##", "###..###", "########"],
  "plate-09": ["....#.....", "...###..#.", "...##..##.", ".#.######.", ".########.", "..#######.", "...#####..", "....###...", ".....#....", "......##.."],
  "plate-10": ["......###.", "......##.#", "......##..", "......##..", "...#####..", ".#######..", "########..", ".######...", "...#.#....", "...#.#...."],
  "plate-11": ["...####...", "..######.#", ".#########", "###.######", "#########.", "#########.", ".#########", "..######.#", "...####...", "....#....."],
  "plate-12": [".......##.", "......####", ".......##.", "..#.......", ".###...#..", "#####.###.", "##########", "#####.####", "####...###", "##########"],
};

const inkRuns = (line: string, ink = /#+/g): number[] => Array.from(line.matchAll(ink), match => match[0].length);
const proofClues = (proof: string[]): Clues => ({
  rows: proof.map(row => inkRuns(row)),
  columns: Array.from({ length: proof[0].length }, (_, column) => inkRuns(proof.map(row => row[column]).join(""))),
});
const marksFor = (proof: string[]): Mark[] => Array.from(proof.join(""), symbol => symbol === "#" ? INK : EMPTY);

function allSolutions({ rows, columns }: Clues): string[][] {
  const arrangements = (length: number, required: readonly number[]) =>
    Array.from({ length: 2 ** length }, (_, bits) => bits.toString(2).padStart(length, "0"))
      .filter(line => inkRuns(line, /1+/g).join(",") === required.join(","));
  const rowChoices = rows.map(clue => arrangements(columns.length, clue));
  const solutions: string[][] = [];
  let branches = 0;
  function visit(chosen: string[], columnChoices: string[][]): void {
    const row = chosen.length;
    if (row === rows.length) {
      solutions.push(chosen.map(line => line.replaceAll("0", ".").replaceAll("1", "#")));
      return;
    }
    for (const pattern of rowChoices[row]) {
      if (++branches > 100_000) throw new Error("Exhaustive check exceeded its branch budget");
      const compatible = columnChoices.map((layouts, column) => layouts.filter(line => line[row] === pattern[column]));
      if (compatible.every(layouts => layouts.length)) visit([...chosen, pattern], compatible);
    }
  }
  visit([], columns.map(clue => arrangements(rows.length, clue)));
  return solutions;
}

test("every original plate has exact ordered clues, one exhaustive solution and a deduction-only path", () => {
  expect(inkPlates.map(plate => plate.id)).toEqual(Object.keys(proofs));
  for (const size of [5, 8, 10]) expect(inkPlates.filter(plate => plate.rows.length === size)).toHaveLength(4);
  for (const plate of inkPlates) {
    const proof = proofs[plate.id];
    expect({ rows: plate.rows, columns: plate.columns }, plate.id).toEqual(proofClues(proof));
    expect(allSolutions(plate), plate.id).toEqual([proof]);
    const marks = Array<Mark>(proof.length ** 2).fill(UNDECIDED);
    const expected = marksFor(proof);
    for (let step = 0; step < marks.length; step++) {
      const deduction = findHint(plate, marks);
      if (deduction.kind !== "forced") throw new Error(`${plate.id}: ${deduction.kind} at mark ${step}`);
      if (marks[deduction.cell] !== UNDECIDED || deduction.mark !== expected[deduction.cell]) {
        throw new Error(`${plate.id}: deduction at cell ${deduction.cell} did not match the sole solution`);
      }
      marks[deduction.cell] = deduction.mark;
    }
    expect(matchesClues(plate, marks), plate.id).toBe(true);
  }
});

test("hints distinguish a forced mark, contradictory marks and no deduction; completion checks runs", () => {
  const ambiguous = { rows: [[1], [1]], columns: [[1], [1]] };
  expect(allSolutions(ambiguous)).toEqual([[".#", "#."], ["#.", ".#"]]);
  expect(findHint(ambiguous, [UNDECIDED, UNDECIDED, UNDECIDED, UNDECIDED])).toEqual({ kind: "stalled" });
  const anchored: Mark[] = [EMPTY, UNDECIDED, UNDECIDED, UNDECIDED];
  const forced = { kind: "forced", cell: 1, mark: INK, line: { axis: "row", index: 0 } };
  expect(findHint(ambiguous, anchored)).toEqual(forced);
  expect(findHint(ambiguous, anchored)).toEqual(forced);
  expect(anchored).toEqual([EMPTY, UNDECIDED, UNDECIDED, UNDECIDED]);

  const bell = inkPlates[0];
  const contradiction = blankBoard(25);
  expect(applyMark(contradiction, 0, INK)).toBe(true);
  // No single line refutes this mark initially; crossing deductions must catch it before returning any hint.
  expect(findHint(bell, contradiction.marks).kind).toBe("contradiction");
  expect(contradiction.marks[0]).toBe(INK);
  expect(undoMark(contradiction)).toBe(0);
  expect(findHint(bell, contradiction.marks)).toMatchObject({ kind: "forced", mark: INK, cell: 7 });

  const correct = ["##..", "..##", ".##.", "...."];
  const sameTotals = ["#.#.", ".#.#", ".##.", "...."];
  expect(matchesClues(proofClues(correct), marksFor(sameTotals))).toBe(false);
  expect(matchesClues(proofClues(correct), marksFor(correct).map(mark => mark === EMPTY ? UNDECIDED : mark))).toBe(true);
});

test("saved plates isolate corrupt records and keep real undo across reload without unbounded history", () => {
  const progress = readProgress(null);
  const board = blankBoard(25);
  progress.active = "plate-03";
  progress.boards.set("plate-01", board);
  expect(applyMark(board, 0, INK)).toBe(true);
  expect(applyMark(board, 0, INK)).toBe(false);
  expect(applyMark(board, 0, EMPTY)).toBe(true);
  expect(applyMark(board, 0, UNDECIDED)).toBe(true);
  const roundTrip = readProgress(writeProgress(progress));
  expect(roundTrip.active).toBe("plate-03");
  const restored = roundTrip.boards.get("plate-01")!;
  for (const mark of [EMPTY, INK, UNDECIDED]) {
    expect(undoMark(restored)).toBe(0);
    expect(restored.marks[0]).toBe(mark);
  }
  expect(undoMark(restored)).toBeUndefined();

  const record = {
    version: 1, active: "not-a-plate",
    boards: {
      "not-a-plate": { marks: "10000", undo: [[0, 0]] },
      "plate-01": { marks: "1" + "0".repeat(24), undo: [[25, 0]] },
      "plate-02": { marks: "1" + "9".repeat(24), undo: [] },
      "plate-03": { marks: "1".repeat(26), undo: [] },
    },
  };
  const recovered = readProgress(JSON.stringify(record));
  expect(recovered.active).toBe("plate-01");
  expect([...recovered.boards.keys()]).toEqual(["plate-01"]);
  expect(recovered.boards.get("plate-01")!.marks[0]).toBe(INK);
  expect(undoMark(recovered.boards.get("plate-01")!)).toBeUndefined();
  expect(readProgress(JSON.stringify({ ...record, padding: " ".repeat(9000) })).boards.size).toBe(0);
  expect(readProgress("{").boards.size).toBe(0);

  for (let i = 0; i < 64; i++) applyMark(board, 1, i % 2 ? INK : EMPTY);
  const bounded = readProgress(writeProgress(progress)).boards.get("plate-01")!;
  for (let i = 0; i < 32; i++) expect(undoMark(bounded)).toBe(1);
  expect(undoMark(bounded)).toBeUndefined();
  expect(bounded.marks[1]).toBe(INK);
});

const game = (page: Page) => page.getByRole("region", { name: "Ink Grids" });
const puzzle = (page: Page) => game(page).getByRole("grid");
const square = (page: Page, row: number, column: number) => puzzle(page).locator(`[data-cell="${row}-${column}"]`);
const tool = (page: Page, name: string) => game(page).getByRole("group", { name: "Marking tool" }).getByRole("button", { name });
const action = (page: Page, name: string) => game(page).getByRole("button", { name, exact: true });
const picker = (page: Page) => game(page).getByLabel("Puzzle plate");
const print = (page: Page) => game(page).getByRole("img", { name: /^Earned print:/ });
const inkUrl = (base: string) => new URL("nonograms/", base).href;
const keyFor = (mount: string) => `${mount}court-ink-grids-v1`;

async function noPageOverflow(page: Page) {
  expect(await page.evaluate(() => document.documentElement.scrollWidth)).toBeLessThanOrEqual(page.viewportSize()!.width);
}

test.describe("mounted Ink Grids", () => {
  test.beforeEach(({ baseURL }) => {
    test.skip(!baseURL || !/^\/(?:game-)?portal\/$/.test(new URL(baseURL).pathname), "Requires a portal mount.");
  });

  test("phone taps and the clue keyboard earn the authored print only from matching runs", async ({ page, baseURL }, info) => {
    const url = inkUrl(baseURL!);
    const base = new URL(baseURL!);
    const nonLocal: string[] = [];
    const errors: string[] = [];
    page.on("request", request => {
      const target = new URL(request.url());
      if (request.method() !== "GET" || target.origin !== base.origin ||
          (request.url() !== url && !target.pathname.startsWith(`${base.pathname}assets/`))) nonLocal.push(request.url());
    });
    page.on("websocket", socket => nonLocal.push(socket.url()));
    page.on("pageerror", error => errors.push(error.message));
    await page.setViewportSize({ width: 320, height: 730 });
    await page.emulateMedia({ reducedMotion: "reduce" });
    await page.goto(url);
    await expect(page).toHaveTitle("Ink Grids · Jade Court");
    await expect(page.getByRole("link", { name: /All games/ }).last()).toHaveAttribute("href", base.pathname);
    await expect(game(page)).toHaveAttribute("data-plate", "plate-01");
    await expect(puzzle(page)).toBeInViewport({ ratio: .98 });
    await noPageOverflow(page);
    for (const target of [square(page, 1, 1), square(page, 5, 5), tool(page, "X Empty"), action(page, "Enlarge grid")]) {
      const box = (await target.boundingBox())!;
      expect(Math.min(box.width, box.height)).toBeGreaterThanOrEqual(44);
    }
    await expect(game(page).locator(".ink-impression")).toHaveCount(0);
    await page.screenshot({ path: info.outputPath("ink-grids-portrait.png") });

    const row = puzzle(page).getByRole("button", { name: "Row 4 clues: 5" });
    const column = puzzle(page).getByRole("button", { name: "Column 3 clues: 5" });
    await row.tap();
    await expect(row).toHaveAttribute("aria-pressed", "true");
    await expect(page.locator(".ink-reading")).toHaveText("Row 4: 5");
    await expect(puzzle(page).locator("button[tabindex='0']")).toHaveCount(1);
    await row.press("ArrowRight");
    const keyboardTarget = square(page, 4, 1);
    await expect(keyboardTarget).toBeFocused();
    await keyboardTarget.press("i");
    await expect(keyboardTarget).toHaveAttribute("data-mark", "ink");
    await keyboardTarget.press("x");
    await expect(keyboardTarget).toHaveAttribute("data-mark", "empty");
    await expect(keyboardTarget).toHaveText("×");
    await keyboardTarget.press("e");
    await expect(keyboardTarget).toHaveAttribute("data-mark", "undecided");
    await expect(keyboardTarget).toHaveText("·");
    await expect(tool(page, "Erase")).toHaveAttribute("aria-pressed", "true");
    await square(page, 1, 3).press("ArrowUp");
    await expect(column).toBeFocused();
    await expect(column).toHaveAttribute("aria-pressed", "true");
    await expect(row).toHaveAttribute("aria-pressed", "false");
    await column.press("ArrowDown");
    await expect(square(page, 1, 3)).toBeFocused();

    await tool(page, "Ink").tap();
    await square(page, 1, 3).press("Space");
    await square(page, 1, 1).tap(); // Same total ink as the answer once all except the clapper are painted.
    const proof = proofs["plate-01"];
    for (let r = 0; r < proof.length; r++) {
      for (let c = 0; c < proof[r].length; c++) {
        if (proof[r][c] === "#" && !(c === 2 && (r === 0 || r === 4))) await square(page, r + 1, c + 1).tap();
      }
    }
    await expect(puzzle(page).locator("[data-mark='ink']")).toHaveCount(13);
    await expect(game(page)).toHaveAttribute("data-state", "playing");
    await expect(game(page).locator(".ink-impression")).toHaveCount(0);
    await tool(page, "X Empty").tap();
    await square(page, 1, 1).tap();
    await tool(page, "Ink").tap();
    await square(page, 5, 3).tap();
    await expect(print(page)).toHaveAccessibleName("Earned print: Temple Bell");
    await expect(print(page)).toBeVisible();
    const outline = await print(page).locator(".ink-silhouette").getAttribute("d");
    const inkPixels = Array.from(outline!.matchAll(/M(\d+) (\d+)h1v1h-1z/g), ([, x, y]) => Number(y) * 5 + Number(x));
    expect(inkPixels).toEqual(marksFor(proof).flatMap((mark, i) => mark === INK ? [i] : []));
    await expect(square(page, 1, 1)).toHaveAttribute("data-mark", "empty");
    await expect(square(page, 1, 2)).toHaveAttribute("data-mark", "undecided");
    await expect(game(page)).toHaveAttribute("data-state", "solved");
    expect(await page.locator(".ink-print").evaluate(node => getComputedStyle(node).animationName)).toBe("none");
    await page.screenshot({ path: info.outputPath("ink-grids-earned-print.png"), fullPage: true });

    await page.reload();
    await expect(print(page)).toBeVisible();
    await action(page, "Undo").tap();
    await expect(square(page, 5, 3)).toHaveAttribute("data-mark", "undecided");
    await expect(game(page).locator(".ink-impression")).toHaveCount(0);
    expect(nonLocal, "The game uses only its mounted document and same-origin static assets").toEqual([]);
    expect(errors).toEqual([]);
    expect(await page.context().cookies()).toEqual([]);
  });

  test("a contradiction leaves marks alone, and plate progress, undo and confirmed reset are isolated", async ({ page, baseURL }) => {
    const mount = new URL(baseURL!).pathname;
    const other = keyFor(mount === "/portal/" ? "/game-portal/" : "/portal/");
    const otherSave = JSON.stringify({
      version: 1, active: "plate-02", boards: { "plate-02": { marks: "1".repeat(25), undo: [] } },
    });
    await page.addInitScript(({ key, value }) => {
      if (localStorage.getItem(key) === null) localStorage.setItem(key, value);
    }, { key: other, value: otherSave });
    await page.goto(inkUrl(baseURL!));
    await expect(game(page)).toHaveAttribute("data-plate", "plate-01");
    await square(page, 1, 1).tap();
    const status = game(page).getByRole("status");
    const snapshot = () => page.evaluate(key => localStorage.getItem(key), keyFor(mount));
    const contradiction = await snapshot();
    expect(contradiction).not.toBeNull();
    await action(page, "Hint").tap();
    await expect(status).toHaveText(/No placement remains for (row|column) \d+\. Undo or revise a mark\./);
    await expect(square(page, 1, 1)).toHaveAttribute("data-mark", "ink");
    await expect(puzzle(page).locator("[data-hint]")).toHaveCount(0);
    expect(await snapshot()).toBe(contradiction);
    await picker(page).selectOption("plate-06");
    await expect(puzzle(page).getByRole("button", { name: "Column 1 clues: 0" })).toBeVisible();
    await tool(page, "X Empty").tap();
    await square(page, 1, 1).tap();
    await picker(page).selectOption("plate-01");
    await expect(square(page, 1, 1)).toHaveAttribute("data-mark", "ink");
    await action(page, "Undo").tap();
    const corrected = await snapshot();
    await action(page, "Hint").tap();
    await expect(status).toHaveText("The clues force ink at row 2, column 3.");
    await expect(square(page, 2, 3)).toHaveAttribute("data-hint", "ink");
    await expect(square(page, 2, 3)).toHaveAttribute("data-mark", "undecided");
    expect(await snapshot()).toBe(corrected);
    await picker(page).selectOption("plate-06");
    await expect(square(page, 1, 1)).toHaveAttribute("data-mark", "empty");
    expect(await page.evaluate(key => localStorage.getItem(key), other)).toBe(otherSave);

    await page.reload();
    await expect(picker(page)).toHaveValue("plate-06");
    await expect(square(page, 1, 1)).toHaveAttribute("data-mark", "empty");
    await action(page, "Undo").tap();
    await expect(square(page, 1, 1)).toHaveAttribute("data-mark", "undecided");
    await square(page, 2, 4).tap();
    await picker(page).selectOption("plate-01");
    await square(page, 2, 3).tap();
    await action(page, "Reset puzzle").tap();
    const dialog = page.getByRole("dialog", { name: "Reset puzzle?" });
    await dialog.getByRole("button", { name: "Cancel" }).tap();
    await expect(square(page, 2, 3)).toHaveAttribute("data-mark", "ink");
    await action(page, "Reset puzzle").tap();
    await dialog.getByRole("button", { name: "Reset puzzle" }).tap();
    await expect(square(page, 2, 3)).toHaveAttribute("data-mark", "undecided");
    await expect(action(page, "Undo")).toBeDisabled();
    await picker(page).selectOption("plate-06");
    await expect(square(page, 2, 4)).toHaveAttribute("data-mark", "ink");
    expect(await page.evaluate(key => localStorage.getItem(key), other)).toBe(otherSave);
  });

  test("enlarged ten-by-ten cells stay tappable with pinned clues in portrait and short landscape", async ({ page, baseURL }, info) => {
    await page.setViewportSize({ width: 320, height: 730 });
    await page.goto(inkUrl(baseURL!));
    await picker(page).selectOption("plate-12");
    await expect(puzzle(page)).toHaveAccessibleName("Plate 12 · 10 by 10 nonogram");
    await noPageOverflow(page);
    await action(page, "Enlarge grid").tap();
    const far = square(page, 10, 10);
    const well = page.locator(".ink-scroll");
    const nearBottom = () => well.evaluate(node => node.scrollTo(node.scrollWidth, node.scrollHeight));
    const verifyFar = async () => {
      const hit = (await far.boundingBox())!;
      expect(Math.min(hit.width, hit.height)).toBeGreaterThanOrEqual(44);
      expect(await page.evaluate(({ x, y }) => document.elementFromPoint(x, y)?.closest("button")?.dataset.cell,
        { x: hit.x + hit.width / 2, y: hit.y + hit.height / 2 })).toBe("10-10");
    };
    await nearBottom();
    await verifyFar();
    await far.tap();
    await expect(far).toHaveAttribute("data-mark", "ink");
    await noPageOverflow(page);

    await page.setViewportSize({ width: 740, height: 320 });
    await page.evaluate(() => scrollTo(0, 0));
    await nearBottom();
    await verifyFar();
    const scrollBox = (await well.boundingBox())!;
    const rowBox = (await puzzle(page).getByRole("button", { name: "Row 10 clues: 10" }).boundingBox())!;
    const columnBox = (await puzzle(page).getByRole("button", { name: "Column 10 clues: 1, 4" }).boundingBox())!;
    expect(Math.abs(rowBox.x - scrollBox.x)).toBeLessThanOrEqual(1);
    expect(Math.abs(columnBox.y - scrollBox.y)).toBeLessThanOrEqual(1);
    await expect(tool(page, "X Empty")).toBeInViewport({ ratio: .99 });
    await expect(action(page, "Hint")).toBeInViewport({ ratio: .99 });
    await tool(page, "X Empty").tap();
    await far.tap();
    await expect(far).toHaveAttribute("data-mark", "empty");
    await far.press("ArrowUp");
    await expect(square(page, 9, 10)).toBeFocused();
    await noPageOverflow(page);
    await page.screenshot({ path: info.outputPath("ink-grids-landscape-enlarged.png") });
    await action(page, "Fit grid").tap();
    await expect(action(page, "Enlarge grid")).toBeVisible();
    await picker(page).selectOption("plate-01");
    await expect(puzzle(page)).toBeInViewport({ ratio: .99 });
  });

  test("storage can be blocked and play continues offline after the local assets load", async ({ page, context, baseURL }) => {
    await page.addInitScript(() => {
      Object.defineProperty(window, "localStorage", { get() { throw new DOMException("Blocked", "SecurityError"); } });
    });
    await page.goto(inkUrl(baseURL!));
    await expect(game(page).getByText(/Browser storage is unavailable/)).toBeVisible();
    await context.setOffline(true);
    await square(page, 1, 3).tap();
    await picker(page).selectOption("plate-09");
    await picker(page).selectOption("plate-01");
    await expect(square(page, 1, 3)).toHaveAttribute("data-mark", "ink");
    await action(page, "Undo").tap();
    await expect(square(page, 1, 3)).toHaveAttribute("data-mark", "undecided");
  });
});
