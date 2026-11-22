import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';

function inspectProjection(value, forbidden, label, visit = () => {}) {
  if (!value || typeof value !== 'object') return;
  for (const [key, child] of Object.entries(value)) {
    assert(!forbidden.has(key.toLowerCase()), `${label} exposed ${key}`);
    inspectProjection(child, forbidden, label, visit);
  }
  if (!Array.isArray(value)) visit(value);
}

const storageFields = ['checkpoint', 'payload', 'digest'];

function uniqueSudokuSolution(given) {
  const digits = [1, 2, 3, 4, 5, 6, 7, 8, 9];
  assert(Array.isArray(given) && given.length === 81, 'Sudoku must have 81 cells');
  const cells = [...given.keys()];
  const unit = i => [Math.floor(i / 9), i % 9, Math.floor(i / 27) * 3 + Math.floor(i % 9 / 3)];
  const units = cells.map(unit);
  const peers = units.map((here, i) => cells.filter(j => i !== j && here.some((n, k) => n === units[j][k])));
  assert(cells.every(i => given[i] === 0 || (digits.includes(given[i])
    && peers[i].every(j => given[i] !== given[j]))), 'Sudoku gave invalid or conflicting clues');

  const board = [...given];
  let solution;
  let count = 0;
  function search() {
    let index = -1;
    let choices;
    for (const i of cells) {
      if (board[i] !== 0) continue;
      const available = digits.filter(digit => peers[i].every(j => board[j] !== digit));
      if (available.length === 0) return;
      if (!choices || available.length < choices.length) {
        index = i;
        choices = available;
        if (choices.length === 1) break;
      }
    }
    if (index === -1) {
      solution ??= [...board];
      count++;
      return;
    }
    for (const digit of choices) {
      board[index] = digit;
      search();
      if (count === 2) break;
    }
    board[index] = 0;
  }
  search();
  assert.equal(count, 1, 'The publicly projected Sudoku clues did not have exactly one solution');
  return solution;
}

async function loadOpeningWords() {
  const lexicon = new URL('../src/JadeCourt.Words/Lexicon/words.txt', import.meta.url);
  const words = (await readFile(lexicon, 'utf8')).split(/\r?\n/).filter(word => /^[A-Z]{2,6}$/.test(word));
  assert(words.length > 0, 'The bundled Word Tiles lexicon has no opening words');
  return words;
}

function openingPlacement(rack, words) {
  for (const word of words) {
    const available = [...rack];
    const placements = [];
    for (const letter of word) {
      const index = available.findIndex(tile => tile.letter === letter);
      if (index === -1) break;
      const [tile] = available.splice(index, 1);
      placements.push({ row: 5, column: 5 + placements.length, tileId: tile.id });
    }
    if (placements.length === word.length) return placements;
  }
  return null;
}

function tablePath(game, table) {
  assert.match(table.id, /^[0-9a-f]{32}$/, `${game} did not return a table ID`);
  return `api/${game}/tables/${table.id}`;
}

const readSeat = (call, game, table, cookie) => call(tablePath(game, table), { cookie });
const createTable = (call, game, cookie, bots) =>
  call(`api/${game}/tables`, { cookie, body: { players: 2, bots } });

async function act(call, game, table, cookie, action) {
  const next = await call(`${tablePath(game, table)}/actions`, {
    cookie, body: { revision: table.revision, action },
  });
  assert.equal(next.id, table.id, `${game} action changed tables`);
  assert.equal(next.revision, table.revision + 1, `${game} action did not commit one table revision`);
  assert.equal(next.seat, table.seat, `${game} action changed seats`);
  return next;
}

async function invitePair(call, game, { host, visitor, outsider }) {
  const waiting = await createTable(call, game, host, false);
  assert.equal(waiting.status, 'waiting', `${game} invite table started before the other guest joined`);
  assert.equal(waiting.position, null, `${game} projected a deal before the other guest joined`);
  assert(/^[0-9a-f]{32}$/.test(waiting.invite ?? ''), `${game} did not issue a private invite`);
  await call(tablePath(game, waiting), { cookie: outsider, status: 404, error: 'room_not_found' });
  const joined = await call(`api/${game}/join`, { cookie: visitor, body: { invite: waiting.invite } });
  const creator = await readSeat(call, game, waiting, host);
  for (const [seat, table] of [creator, joined].entries()) {
    assert.equal(table.id, waiting.id, `${game} invited guest got a different table`);
    assert.equal(table.status, 'playing', `${game} did not start a full table`);
    assert.equal(table.seat, seat, `${game} assigned distinct guests the wrong seats`);
    assert.equal(table.revision, waiting.revision + 1, `${game} join did not commit`);
    assert(table.invite === null, `${game} revealed a started table's invite`);
  }
  await call(tablePath(game, waiting), { cookie: outsider, status: 404, error: 'room_not_found' });
  return [creator, joined];
}

function checkPokerProjection(table, rival) {
  const view = table.position;
  assert.equal(view.viewerSeat, table.seat, 'Hold’em projected the wrong seat');
  assert.equal(view.ownCards.length, 2, 'Hold’em must project two own cards');
  assert.equal(view.result, null, 'Hold’em revealed a result before showdown');
  const keyOf = ({ rank, suit }) => `${rank}/${suit}`;
  const cards = [...view.ownCards, ...view.board];
  const allowed = new Set(cards.map(keyOf));
  assert.equal(allowed.size, cards.length, 'Hold’em repeated a card in one view');
  if (rival) {
    for (const card of rival.position.ownCards) {
      assert(!allowed.has(keyOf(card)), 'Hold’em exposed another guest’s card');
    }
  }
  inspectProjection(table, new Set([...storageFields, 'deck', 'holecards', 'showdownhands']), 'Hold’em', value => {
    if ('rank' in value && 'suit' in value) {
      assert(Number.isInteger(value.rank) && value.rank >= 2 && value.rank <= 14
        && Number.isInteger(value.suit) && value.suit >= 0 && value.suit <= 3,
      'Hold’em projected an invalid card');
      assert(allowed.has(keyOf(value)), 'Hold’em projected a card outside the board and own hand');
    }
  });
}

async function seedPoker(call, guests) {
  const { host, visitor } = guests;
  const botTable = await createTable(call, 'poker', host, true);
  assert.equal(botTable.status, 'playing', 'Hold’em bot table did not start');
  assert.equal(botTable.seats[1]?.bot, true, 'Hold’em did not seat a bot');
  assert.equal(botTable.position.actingSeat, botTable.seat);
  checkPokerProjection(botTable);
  const botReply = await act(call, 'poker', botTable, host, { kind: 'call' });
  assert(botReply.position.revision > botTable.position.revision + 1,
    'Hold’em saved the human action without any bot reply');
  assert.equal(botReply.position.actingSeat, botTable.seat, 'Hold’em bot did not return the turn');
  assert.deepEqual(botReply.position.ownCards, botTable.position.ownCards, 'Hold’em bot reply changed the guest’s cards');
  checkPokerProjection(botReply);

  const [creator, joined] = await invitePair(call, 'poker', guests);
  checkPokerProjection(creator, joined);
  checkPokerProjection(joined, creator);
  assert.equal(creator.position.board.length, 0, 'Hold’em leaked community cards before the flop');
  assert.equal(joined.position.legalActions, null, 'Hold’em offered actions to the wrong seat');
  const called = await act(call, 'poker', creator, host, { kind: 'call' });
  const guestTurn = await readSeat(call, 'poker', called, visitor);
  assert.equal(guestTurn.position.actingSeat, guestTurn.seat, 'Hold’em did not pass the turn to the invited guest');
  assert.equal(guestTurn.position.legalActions?.canCheck, true, 'Hold’em omitted the guest’s legal check');
  const checked = await act(call, 'poker', guestTurn, visitor, { kind: 'check' });
  assert.equal(checked.position.board.length, 3, 'Hold’em did not reveal the flop after both guest actions');
  const readHost = await readSeat(call, 'poker', checked, host);
  assert.deepEqual(readHost.position.board, checked.position.board, 'Hold’em showed different public boards');
  assert.deepEqual(readHost.position.ownCards, creator.position.ownCards, 'Hold’em changed the host’s deal');
  assert.deepEqual(checked.position.ownCards, joined.position.ownCards, 'Hold’em changed the guest’s deal');
  checkPokerProjection(readHost, checked);
  checkPokerProjection(checked, readHost);
  return { botReply, pair: [readHost, checked] };
}

function checkWordProjection(table, rival) {
  const { rack, rackSizes } = table.position;
  const own = new Set(rack.map(tile => tile.id));
  assert.equal(rack.length, rackSizes[table.seat], 'Word Tiles projected more than the viewer’s rack');
  assert.equal(own.size, rack.length, 'Word Tiles repeated a private tile');
  const visible = new Set([...own, ...table.position.board.map(cell => cell.tile.id)]);
  if (rival) {
    for (const tile of rival.position.rack) {
      assert(!visible.has(tile.id), 'Word Tiles exposed another guest’s rack');
    }
  }
  inspectProjection(table, new Set([...storageFields, 'bag', 'racks']), 'Word Tiles', value => {
    if ('letter' in value && 'id' in value) {
      assert(visible.has(value.id), 'Word Tiles projected a tile outside the board and own rack');
    }
  });
}

async function wordBotReply(call, host) {
  // A random rack can have no legal opening. A legal pass is not the placement proof.
  for (let attempt = 0; attempt < 8; attempt++) {
    const table = await createTable(call, 'words', host, true);
    assert.equal(table.status, 'playing', 'Word Tiles bot table did not start');
    assert.equal(table.seats[1]?.bot, true, 'Word Tiles did not seat a bot');
    assert.equal(table.position.turn, table.seat);
    assert.deepEqual(table.position.board, [], 'New Word Tiles board was not empty');
    const after = await act(call, 'words', table, host, { kind: 'pass' });
    assert.equal(after.position.turn, table.seat, 'Word Tiles bot did not return the turn');
    assert.match(after.position.lastMove, /^Seat 2 (played |passed\.$)/, 'Word Tiles did not record a bot reply');
    checkWordProjection(after);
    if (after.position.board.length > 0) {
      assert(after.position.board.every(cell => cell.seat === 1), 'Word Tiles did not credit the bot’s placements');
      assert(after.position.scores[1] > 0, 'Word Tiles did not score the bot’s word');
      return after;
    }
  }
  assert.fail('No Word Tiles bot placed a legal word in eight public opening deals');
}

async function seedWords(call, guests) {
  const { host, visitor } = guests;
  const botReply = await wordBotReply(call, host);
  const words = await loadOpeningWords();
  const cookies = [host, visitor];
  for (let deal = 0; deal < 3; deal++) {
    let pair = await invitePair(call, 'words', guests);
    for (let turn = 0; turn < 4 && pair[0].status === 'playing'; turn++) {
      checkWordProjection(pair[0], pair[1]);
      checkWordProjection(pair[1], pair[0]);
      const { turn: seat, board, rows, columns } = pair[0].position;
      assert.deepEqual([rows, columns, board.length], [11, 11, 0], 'Word Tiles did not start an empty 11×11 board');
      const table = pair[seat];
      const placements = openingPlacement(table.position.rack, words);
      const action = placements ? { kind: 'place', placements }
        : { kind: 'exchange', exchange: table.position.rack.slice(0, 3).map(tile => tile.id) };
      const after = await act(call, 'words', table, cookies[seat], action);
      pair = await Promise.all(cookies.map(cookie => readSeat(call, 'words', after, cookie)));
      if (placements) {
        assert.deepEqual(after.position.board.map(cell => ({
          row: cell.row, column: cell.column, tileId: cell.tile.id,
        })), placements, 'Word Tiles did not commit the requested public placement');
        assert(after.position.scores[seat] > 0, 'Word Tiles did not score the accepted word');
        assert.deepEqual(pair[0].position.board, pair[1].position.board, 'Word Tiles guests saw different public boards');
        checkWordProjection(pair[0], pair[1]);
        checkWordProjection(pair[1], pair[0]);
        return { botReply, pair };
      }
    }
  }
  assert.fail('No playable Word Tiles opening after the public deal/exchange budget');
}

function checkSolitaireProjection(table) {
  const { tableau } = table.position;
  assert.equal(tableau.length, 7, 'Solitaire did not project seven columns');
  for (const [index, column] of tableau.entries()) {
    assert.equal(column.length, index + 1, 'Solitaire dealt the wrong column height');
    assert.equal(column.at(-1)?.faceUp, true, 'Solitaire did not expose a playable top card');
    for (const covered of column.slice(0, -1)) {
      assert.deepEqual(covered, { rank: null, suit: null, faceUp: false }, 'Solitaire exposed a covered card');
    }
  }
  inspectProjection(table, new Set([...storageFields, 'deck', 'stock', 'covered']), 'Solitaire');
}

const soloVariants = { solitaire: 'draw-one', sudoku: 'standard' };

async function seedSolo(call, { host, visitor }) {
  const played = {};
  for (const [game, variant] of Object.entries(soloVariants)) {
    const path = `api/solo/${game}`;
    assert.deepEqual(await call(path, { cookie: host }), { table: null }, `${game} did not start with an empty slot`);
    const started = await call(path, { cookie: host, body: { variant } });
    assert.equal(started.game, game, `${game} opened a different game`);
    assert.equal(started.variant, variant, `${game} did not open the requested variant`);
    assert.equal(started.position.finished, false, `${game} started finished`);
    assert.equal(started.position.moves, 0, `${game} started with recorded moves`);

    const move = action => call(`${path}/moves`, {
      cookie: host, body: { revision: started.revision, action },
    });
    if (game === 'solitaire') {
      checkSolitaireProjection(started);
      assert.equal(started.position.stockCount, 24, 'Solitaire did not deal a full stock');
      assert.equal(started.position.wasteCount, 0, 'Solitaire started with waste');
      const drawn = await move({ kind: 'draw' });
      checkSolitaireProjection(drawn);
      assert.equal(drawn.position.stockCount, started.position.stockCount - 1, 'Solitaire did not draw exactly one card');
      assert.equal(drawn.position.wasteCount, 1, 'Solitaire did not add the draw to waste');
      assert.equal(drawn.position.waste?.faceUp, true, 'Solitaire did not expose the drawn card');
      assert.deepEqual(drawn.position.tableau, started.position.tableau, 'A stock draw changed the tableau');
      played[game] = drawn;
    } else {
      const hidden = new Set([...storageFields, 'solution', 'answer']);
      inspectProjection(started, hidden, 'Sudoku');
      const { given, values } = started.position;
      assert.deepEqual(values, given, 'Sudoku started with content other than the clues');
      const solution = uniqueSudokuSolution(given);
      assert.equal(given.filter(Boolean).length, 37, 'Sudoku did not publish the standard clue count');
      const index = given.indexOf(0);
      const value = solution[index];
      const marked = await move({ kind: 'set', index, value });
      inspectProjection(marked, hidden, 'Sudoku');
      assert.deepEqual(marked.position.given, given, 'A Sudoku move changed the fixed clues');
      assert.deepEqual(marked.position.values, values.with(index, value), 'Sudoku did not save exactly the requested mark');
      assert.deepEqual(marked.position.conflicts, [], 'Sudoku marked a correct digit as conflicting');
      played[game] = marked;
    }
    assert.equal(played[game].revision, started.revision + 1, `${game} did not commit one revision`);
    assert.equal(played[game].game, started.game, `${game} move changed the game`);
    assert.equal(played[game].variant, started.variant, `${game} move changed the variant`);
    assert.equal(played[game].position.moves, 1, `${game} did not record the move`);
    assert.deepEqual(await call(path, { cookie: visitor }), { table: null }, `${game} copied progress to another guest`);
  }
  return played;
}

export async function playExpansion(call, guests) {
  const poker = await seedPoker(call, guests);
  const words = await seedWords(call, guests);
  const solo = await seedSolo(call, guests);
  console.log('Played private Hold’em and Word Tiles with different guests and bots; drew one Solitaire card and marked a unique Sudoku.');
  return { poker, words, solo };
}

export async function resumeExpansion(call, { host, visitor, outsider }, state) {
  for (const game of ['poker', 'words']) {
    const { botReply, pair } = state[game];
    assert.deepEqual(await readSeat(call, game, botReply, host), botReply, `${game} did not restore the bot reply`);
    const resumed = await Promise.all([host, visitor].map(cookie => readSeat(call, game, pair[0], cookie)));
    assert.deepEqual(resumed, pair, `${game} did not restore the public position and both private seats`);
    for (const cookie of [host, visitor]) {
      assert.deepEqual(await call(`api/${game}/resume`, { cookie }), { tableId: pair[0].id },
        `${game} did not offer to resume the restored private table`);
    }
    await call(tablePath(game, pair[0]), { cookie: outsider, status: 404, error: 'room_not_found' });
  }
  for (const [game, played] of Object.entries(state.solo)) {
    assert.deepEqual(await call(`api/solo/${game}`, { cookie: host }), { table: played },
      `${game} did not restore the actual move`);
    assert.deepEqual(await call(`api/solo/${game}`, { cookie: visitor }), { table: null },
      `${game} revealed restored progress to another guest`);
  }
  console.log('Restored the original Hold’em cards, Word Tiles racks, and solo progress without exposing them to a rival guest.');
}
