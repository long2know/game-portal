import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import { isDeepStrictEqual } from 'node:util';

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
  const words = (await readFile(lexicon, 'utf8')).split(/\r?\n/).filter(word => /^[A-Z]{2,7}$/.test(word));
  assert(words.length > 0, 'The bundled Word Tiles lexicon has no opening words');
  return words.sort((a, b) => b.length - a.length);
}

function openingPlacement({ rack, rows, columns }, words) {
  for (const word of words) {
    if (word.length > rack.length) continue;
    const available = [...rack];
    const placements = [];
    const row = Math.floor(rows / 2);
    const start = Math.floor(columns / 2) - Math.floor(word.length / 2);
    for (const letter of word) {
      let index = available.findIndex(tile => tile.letter === letter);
      if (index === -1) index = available.findIndex(tile => tile.blank);
      if (index === -1) break;
      const [tile] = available.splice(index, 1);
      placements.push({ row, column: start + placements.length, tileId: tile.id, ...(tile.blank ? { letter } : {}) });
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
const createTable = (call, game, cookie, bots, players = 2) =>
  call(`api/${game}/tables`, { cookie, body: { players, bots } });

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
  const { board, rack, rackSizes, variant, rackSize, rows, columns, premiums, exchangeLimit, bagCount, turn, lastMove } = table.position;
  assert(['duel', 'classic', 'quick'].includes(variant), 'Word Tiles did not project a known rules profile');
  const quick = variant === 'quick';
  const size = quick ? 11 : 15;
  assert.deepEqual([rows, columns, rackSize], [size, size, quick ? 6 : 7]);
  if (variant === 'classic') {
    assert(premiums.some(cell => cell.row === Math.floor(rows / 2) && cell.column === Math.floor(columns / 2)
      && cell.kind === 'double-word'), 'Classic did not project its center premium');
  } else if (quick) {
    assert.deepEqual(premiums, [], 'Quick must not have premiums');
  } else {
    const marks = new Map(premiums.map(cell => [`${cell.row},${cell.column}`, cell.kind]));
    assert.equal(marks.size, 64, 'Duel did not project 64 distinct premium squares');
    assert.equal(marks.has('7,7'), false, 'Duel must have a neutral start square');
    assert.deepEqual(['0,0', '0,3', '0,5', '1,1'].map(cell => marks.get(cell)),
      ['double-letter', 'double-word', 'triple-word', 'triple-letter']);
    assert.equal(bagCount + board.length + rackSizes[0] + rackSizes[1], 104);
    const faces = ['?', 'AEIORST', 'DLNU', 'GHY', 'BCFMPW', 'KV', '', '', 'X', '', 'JQZ'];
    for (const tile of [...rack, ...board.map(cell => cell.tile)])
      assert.equal(tile.points, faces.findIndex(letters => letters.includes(tile.blank ? '?' : tile.letter)),
        `Duel face value for ${tile.letter}`);
  }
  const expectedExchange = turn === table.seat && (variant !== 'classic' || bagCount >= 7)
    ? Math.min(quick ? 3 : 7, rack.length, bagCount) : 0;
  assert.equal(exchangeLimit, expectedExchange, 'Word Tiles projected an incorrect exchange limit for this seat');
  const own = new Set(rack.map(tile => tile.id));
  assert.equal(rack.length, rackSizes[table.seat], 'Word Tiles projected more than the viewer’s rack');
  assert.equal(own.size, rack.length, 'Word Tiles repeated a private tile');
  const visible = new Set([...own, ...board.map(cell => cell.tile.id)]);
  assert.equal(visible.size, own.size + board.length, 'Word Tiles repeated a board tile or left it in the rack');
  for (const tile of rack) {
    assert.equal(typeof tile.blank, 'boolean');
    if (tile.blank) assert.deepEqual([tile.letter, tile.points], ['?', 0]);
    else assert.match(tile.letter, /^[A-Z]$/);
  }
  for (const { tile } of board) {
    assert.match(tile.letter, /^[A-Z]$/);
    if (tile.blank) assert.equal(tile.points, 0);
  }
  const mover = /^Seat ([12]) played /.exec(lastMove);
  const recent = board.filter(cell => cell.recent === true);
  if (variant === 'duel' && mover)
    assert(recent.length > 0 && recent.length <= rackSize && recent.every(cell => cell.seat === Number(mover[1]) - 1));
  else assert.deepEqual(recent, [], 'Word Tiles marked tiles for a non-placement turn');
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

function verifyOpeningScore({ rows, columns, board, scores, premiums, rackSize, variant }, seat, words) {
  const center = [Math.floor(rows / 2), Math.floor(columns / 2)];
  assert(board.some(cell => cell.row === center[0] && cell.column === center[1]), 'Word Tiles opening missed the center');
  const vertical = board[0].column === board[1]?.column;
  const word = board.map((cell, index) => {
    assert.deepEqual([cell.row, cell.column, cell.seat],
      [board[0].row + (vertical ? index : 0), board[0].column + (vertical ? 0 : index), seat],
      'Word Tiles opening must be one contiguous word from the moving seat');
    return cell.tile.letter;
  }).join('');
  assert(words.includes(word), `Word Tiles opened with ${word}, which is not an allowed opening word`);
  const kinds = new Map(premiums.map(cell => [`${cell.row},${cell.column}`, cell.kind]));
  let base = 0;
  let wordMultiplier = 1;
  for (const { row, column, tile } of board) {
    const kind = kinds.get(`${row},${column}`);
    base += tile.points * (kind === 'double-letter' ? 2 : kind === 'triple-letter' ? 3 : 1);
    wordMultiplier *= kind === 'double-word' ? 2 : kind === 'triple-word' ? 3 : 1;
  }
  const rackBonus = board.length !== rackSize ? 0 : variant === 'duel' ? 35 : variant === 'classic' ? 50 : 0;
  assert.equal(scores[seat], base * wordMultiplier + rackBonus, 'Word Tiles did not score the opening premiums and rack bonus');
}

async function wordBotReply(call, host, words) {
  // A random rack can have no legal opening. A legal pass is not the placement proof.
  for (let attempt = 0; attempt < 8; attempt++) {
    const table = await createTable(call, 'words', host, true);
    assert.equal(table.status, 'playing', 'Word Tiles bot table did not start');
    assert.equal(table.seats[1]?.bot, true, 'Word Tiles did not seat a bot');
    assert.equal(table.position.turn, table.seat);
    assert.deepEqual(table.position.board, [], 'New Word Tiles board was not empty');
    assert.equal(table.position.variant, 'duel', 'New bot tables should start on Duel');
    checkWordProjection(table);
    const after = await act(call, 'words', table, host, { kind: 'pass' });
    assert.equal(after.position.turn, table.seat, 'Word Tiles bot did not return the turn');
    assert.match(after.position.lastMove, /^Seat 2 (played |passed\.$)/, 'Word Tiles did not record a bot reply');
    checkWordProjection(after);
    if (after.position.board.length > 0) {
      verifyOpeningScore(after.position, 1, words);
      return after;
    }
  }
  assert.fail('No Word Tiles bot placed a legal word in eight public opening deals');
}

async function seedWords(call, guests) {
  const { host, visitor } = guests;
  const words = await loadOpeningWords();
  const botReply = await wordBotReply(call, host, words);
  const cookies = [host, visitor];
  for (let deal = 0; deal < 3; deal++) {
    let pair = await invitePair(call, 'words', guests);
    for (let turn = 0; turn < 4 && pair[0].status === 'playing'; turn++) {
      checkWordProjection(pair[0], pair[1]);
      checkWordProjection(pair[1], pair[0]);
      const { turn: seat, board, variant } = pair[0].position;
      assert.equal(variant, 'duel', 'New invited tables should start on Duel');
      assert.deepEqual(board, [], 'Word Tiles did not start an empty board');
      const table = pair[seat];
      const placements = openingPlacement(table.position, words);
      const action = placements ? { kind: 'place', placements }
        : table.position.exchangeLimit > 0
          ? { kind: 'exchange', exchange: table.position.rack.slice(0, table.position.exchangeLimit).map(tile => tile.id) }
          : { kind: 'pass' };
      const after = await act(call, 'words', table, cookies[seat], action);
      pair = await Promise.all(cookies.map(cookie => readSeat(call, 'words', after, cookie)));
      if (placements) {
        const expected = placements.map(({ row, column, tileId, letter }) => {
          const tile = table.position.rack.find(entry => entry.id === tileId);
          return { row, column, seat, tile: { ...tile, letter: letter ?? tile.letter }, recent: true };
        });
        assert.deepEqual(after.position.board, expected, 'Word Tiles did not commit the requested public placement');
        verifyOpeningScore(after.position, seat, words);
        assert.deepEqual(pair[0].position.board, pair[1].position.board, 'Word Tiles guests saw different public boards');
        checkWordProjection(pair[0], pair[1]);
        checkWordProjection(pair[1], pair[0]);
        return { botReply, pair };
      }
    }
  }
  assert.fail('No playable Word Tiles opening after the public deal/exchange budget');
}

const roll = { kind: 'roll' };
const checkDice = (dice, count) => {
  assert.equal(dice.length, count);
  assert(dice.every(face => Number.isInteger(face) && face >= 1 && face <= 6), 'Expected server-rolled d6 faces');
};

async function rejectClientRoll(call, game, table, cookie, outsider) {
  const path = tablePath(game, table);
  const body = { revision: table.revision, action: roll };
  for (const requestOrigin of [null, 'https://unrelated-origin.invalid']) {
    await call(`${path}/actions`, { cookie, body, requestOrigin, status: 403, error: 'forbidden' });
  }
  await call(path, { status: 401, error: 'not_authenticated' });
  await call(`${path}/actions`, { cookie: outsider, body, status: 404, error: 'room_not_found' });
  const dice = Array(game === 'farkle' ? table.position.diceToRoll : 2).fill(6);
  for (const supplied of [{ dice }, { seed: 1 }]) {
    await call(`${path}/actions`, {
      cookie, body: { ...body, action: { ...roll, ...supplied } }, status: 400, error: 'invalid_request',
    });
  }
  assert.deepEqual(await readSeat(call, game, table, cookie), table, `${game} changed after a rejected roll`);
}

async function checkStaleRoll(call, game, table, after, cookie) {
  await call(`${tablePath(game, table)}/actions`, {
    cookie, body: { revision: table.revision, action: roll }, status: 409, error: 'stale_room',
  });
  assert.deepEqual(await readSeat(call, game, after, cookie), after, `${game} re-rolled on a stale revision`);
}

async function readDicePair(call, game, table, cookies, privateFields) {
  const pair = await Promise.all(cookies.map(cookie => readSeat(call, game, table, cookie)));
  const views = pair.map(({ position }) => {
    inspectProjection(position, new Set([...storageFields, 'seed', 'randomseed', 'rng', 'randomstate']), game);
    return Object.fromEntries(Object.entries(position).filter(([key]) => !privateFields.includes(key)));
  });
  assert.deepEqual(views[0], views[1], `${game} exposed different public dice or boards to the two seats`);
  return pair;
}

async function seedFarkle(call, guests) {
  const { host, visitor, outsider } = guests;
  const game = 'farkle';
  let botReply = await createTable(call, game, host, true);
  assert.equal(botReply.seats[1]?.bot, true, 'Farkle did not seat a bot');
  let scored = false;
  for (let attempt = 0; attempt < 24 && !(scored && botReply.position.recentTurns.some(turn => turn.seat === 1)); attempt++) {
    const { canBank, canRoll, choices, dice, scores, turnPoints } = botReply.position;
    assert.equal(botReply.position.turn, botReply.seat, 'Farkle did not return control after the bot turn');
    const choice = choices.toSorted((a, b) => b.points - a.points)[0];
    const indices = choice ? [...dice.keys()].filter(index => choice.mask & (1 << index)) : [];
    const action = canBank ? { kind: 'bank' } : canRoll ? roll : { kind: 'keep', dice: indices };
    botReply = await act(call, game, botReply, host, action);
    if (action.kind === 'keep') {
      assert(indices.length > 0 && choice.points > 0, 'Farkle did not offer a scoring keep');
      assert.equal(botReply.position.turnPoints, turnPoints + choice.points, 'Farkle did not add the kept dice’s points');
      assert.deepEqual(botReply.position.kept, indices, 'Farkle did not commit the scoring selection');
      if (indices.length === 1) {
        assert.equal(choice.points, { 1: 100, 5: 50 }[dice[indices[0]]], 'Wrong single-die score');
      }
      scored = true;
    } else if (action.kind === 'bank') {
      assert.equal(botReply.position.scores[0], scores[0] + turnPoints, 'Farkle did not bank the unbanked points');
    }
  }
  const bot = botReply.position.recentTurns.find(turn => turn.seat === 1);
  assert(scored && bot && (bot.kind === 'banked' ? bot.points > 0 : bot.kind === 'farkle' && bot.dice.length > 0),
    'Farkle did not record both a human scoring keep and a bot turn result');

  const cookies = [host, visitor];
  let pair = await invitePair(call, game, guests);
  for (let attempt = 0; attempt < 8; attempt++) {
    const seat = pair[0].position.turn;
    const before = pair[seat];
    assert.equal(before.position.canRoll, true, 'Farkle did not permit the current guest to roll');
    if (attempt === 0) await rejectClientRoll(call, game, before, cookies[seat], outsider);
    const after = await act(call, game, before, cookies[seat], roll);
    if (attempt === 0) await checkStaleRoll(call, game, before, after, cookies[seat]);
    pair = await readDicePair(call, game, after, cookies, ['choices', 'canRoll', 'canBank']);
    if (after.position.phase === 'keep') {
      checkDice(after.position.dice, 6);
      assert(after.position.choices.length > 0, 'Farkle offered no scoring keep on a playable roll');
      assert.deepEqual(pair[1 - seat].position.choices, [], 'Farkle offered the other guest scoring actions');
      assert.equal(pair[1 - seat].position.canRoll || pair[1 - seat].position.canBank, false,
        'Farkle let the nonacting guest roll or bank');
      return { botReply, pair };
    }
    const bust = after.position.recentTurns[0];
    assert.deepEqual([bust.kind, bust.seat, bust.points], ['farkle', seat, 0], 'Farkle did not record a bust');
    checkDice(bust.dice, 6);
  }
  assert.fail('No scoring roll across eight naturally dealt Farkle turns');
}

// Traditional Backgammon points 1–24, with Ivory positive and Umber negative.
const openingPoints = [
  -2, 0, 0, 0, 0, 5, 0, 3, 0, 0, 0, -5, 5, 0, 0, 0, -3, 0, -5, 0, 0, 0, 0, 2,
];

async function seedBackgammon(call, guests) {
  const { host, visitor, outsider } = guests;
  const game = 'backgammon';
  let botReply = await createTable(call, game, host, true);
  assert.equal(botReply.seats[1]?.bot, true, 'Backgammon did not seat a bot');
  for (let attempt = 0; attempt < 2 && botReply.position.lastTurn?.seat !== 1; attempt++) {
    const move = botReply.position.legalActions?.moves[0];
    assert(move, 'Backgammon did not offer a playable opening');
    botReply = await act(call, game, botReply, host, { kind: 'move', ...move });
  }
  assert.equal(botReply.position.lastTurn?.seat, 1, 'Backgammon did not play the bot’s opening turn');
  assert.equal(botReply.position.turn, botReply.seat, 'Backgammon did not return control to the human');
  assert.notDeepEqual(botReply.position.points.map(point => Math.min(point, 0)),
    openingPoints.map(point => Math.min(point, 0)), 'Backgammon did not move any bot checkers');

  const cookies = [host, visitor];
  const opening = await invitePair(call, game, guests);
  const { turn: seat, openingRoll } = opening[0].position;
  checkDice(openingRoll, 2);
  assert.notEqual(openingRoll[0], openingRoll[1], 'Backgammon did not break the opening tie');
  assert.equal(seat, openingRoll[0] > openingRoll[1] ? 0 : 1, 'The higher opening die did not start');
  const before = opening[seat];
  assert.deepEqual(before.position.points, openingPoints, 'Backgammon did not set the traditional opening board');
  assert.equal(opening[1 - seat].position.legalActions, null, 'Backgammon offered a move to both guests');
  const move = before.position.legalActions.moves[0];
  const after = await act(call, game, before, cookies[seat], { kind: 'move', ...move });
  const expected = [...openingPoints];
  const sign = seat === 0 ? 1 : -1;
  expected[move.from - 1] -= sign;
  expected[move.to - 1] += sign;
  assert.deepEqual(after.position.points, expected, 'The requested checker did not move on the public board');
  assert.deepEqual(after.position.remaining, before.position.remaining.filter(face => face !== move.die),
    'Backgammon did not spend exactly the die used by the checker');
  const last = after.position.legalActions.moves[0];
  const passed = await act(call, game, after, cookies[seat], { kind: 'move', ...last });
  const other = await readSeat(call, game, passed, cookies[1 - seat]);
  assert.equal(other.position.legalActions?.canRoll, true, 'Backgammon did not hand over the turn after the opening');
  await rejectClientRoll(call, game, other, cookies[1 - seat], outsider);
  const rolled = await act(call, game, other, cookies[1 - seat], roll);
  checkDice(rolled.position.dice, 2);
  const [first, second] = rolled.position.dice;
  assert.deepEqual(rolled.position.remaining, first === second ? [first, first, first, first] : [first, second],
    'Backgammon did not make the rolled dice available to play');
  assert.deepEqual(rolled.position.points, passed.position.points, 'Backgammon moved checkers while rolling');
  await checkStaleRoll(call, game, other, rolled, cookies[1 - seat]);
  return { botReply, pair: await readDicePair(call, game, rolled, cookies, ['legalActions']) };
}

const bigTwoGame = 'big-two';

function checkBigTwoProjection(table) {
  const fields = (value, keys) => assert.deepEqual(Object.keys(value).sort(), keys.split(' '),
    'Big Two exposed an unexpected projection field');
  fields(table, 'id invite position revision seat seats status');
  assert.equal(table.status, 'playing', 'Big Two bot hand did not stay in play');
  assert(table.invite === null, 'Big Two exposed an invite for a live table');
  assert.equal(table.seat, 0, 'Big Two changed the human seat');
  for (const seat of table.seats) fields(seat, 'bot name occupied seat');
  assert.deepEqual(table.seats.map(({ occupied, bot, seat }) => [occupied, bot, seat]),
    [[true, false, 0], [true, true, 1], [true, true, 2], [true, true, 3]], 'Big Two did not seat one guest and three bots');

  const { ownCards, lastPlay, legalMasks, canPass, seats, turnSeat, winnerSeat } = table.position;
  fields(table.position, 'canPass lastPlay legalMasks ownCards seats turnSeat winnerSeat');
  assert.equal(turnSeat, table.seat, 'Big Two did not return the turn to the human');
  assert.equal(winnerSeat, null, 'Big Two reported a winner before any seat went out');
  assert.deepEqual(seats.map(seat => seat.seat), [0, 1, 2, 3], 'Big Two omitted a public seat');
  for (const seat of seats) fields(seat, 'cardsRemaining seat');
  const counts = seats.map(seat => seat.cardsRemaining);
  assert(counts.every(count => Number.isInteger(count) && count >= 1 && count <= 13), 'Invalid Big Two card counts');
  assert.equal(ownCards.length, counts[table.seat], 'Big Two projected more than the caller’s hand');
  assert(legalMasks.every(mask => Number.isInteger(mask) && mask > 0 && mask < (1 << ownCards.length)),
    'Big Two legal play included a card outside the caller’s hand');
  assert.equal(canPass, lastPlay !== null, 'Big Two offered the wrong pass option');

  if (lastPlay) {
    fields(lastPlay, 'cards kind seat');
    const sizes = { single: 1, pair: 2, triple: 3, straight: 5, flush: 5, 'full-house': 5,
      'four-of-a-kind': 5, 'straight-flush': 5 };
    assert.equal(lastPlay.cards.length, sizes[lastPlay.kind], 'Big Two recorded an invalid play shape');
    assert(counts[lastPlay.seat] + lastPlay.cards.length <= 13, 'Big Two revealed cards that were not spent');
  }
  const visible = [...ownCards, ...(lastPlay?.cards ?? [])];
  for (const card of visible) {
    fields(card, 'id rank suit');
    assert(Number.isInteger(card.id) && card.id >= 0 && card.id < 52 &&
      Number.isInteger(card.rank) && card.rank >= 2 && card.rank <= 14 &&
      Number.isInteger(card.suit) && card.suit >= 0 && card.suit <= 3, 'Big Two exposed an invalid card');
  }
  assert.equal(new Set(visible.map(card => card.id)).size, visible.length,
    'Big Two repeated a card across the caller’s hand and the public trick');
  return counts;
}

async function seedBigTwo(call, { host, outsider }) {
  const started = await createTable(call, bigTwoGame, host, true, 4);
  const before = checkBigTwoProjection(started);
  const { ownCards, lastPlay, legalMasks, canPass } = started.position;
  assert.deepEqual([started.revision, ownCards.length], [0, 13], 'Big Two did not deal a full guest hand');
  let mask = legalMasks[0] ?? 0;
  if (lastPlay === null) {
    // Lead the weakest single, so at least one bot has a legal reply on a human opening.
    const opening = ownCards.findIndex(card => card.rank === 3 && card.suit === 1);
    assert(opening >= 0 && legalMasks.includes(1 << opening), 'Big Two omitted the three-of-diamonds opening');
    mask = 1 << opening;
  }
  const cards = ownCards.filter((_, index) => mask & (1 << index)).map(card => card.id);
  assert(cards.length > 0 || canPass, 'Big Two offered no playable action');
  const action = cards.length > 0 ? { kind: 'play', cards } : { kind: 'pass' };
  const played = await act(call, bigTwoGame, started, host, action);
  const after = checkBigTwoProjection(played);
  assert(isDeepStrictEqual(played.position.ownCards, ownCards.filter(card => !cards.includes(card.id))),
    'Big Two did not keep exactly the unplayed cards in the guest’s hand');
  for (let seat = 1; seat < 4; seat++) {
    assert([0, 1, 2, 3, 5].includes(before[seat] - after[seat]), 'Big Two bot did not consume a legal number of cards');
  }
  const botPosition = lastPlay === null ? played.position : started.position;
  const botPlay = botPosition.lastPlay;
  assert(botPlay?.seat > 0, 'Big Two did not record a public bot play');
  assert.equal(botPosition.seats[botPlay.seat].cardsRemaining, 13 - botPlay.cards.length,
    'Big Two bot did not spend the cards it played');
  assert(botPlay.cards.every(card => !cards.includes(card.id)), 'Big Two bot reused a card played by the human');
  if (lastPlay === null) assert.equal(botPlay.kind, 'single', 'Big Two bot did not follow the opening single');
  await call(tablePath(bigTwoGame, played), { cookie: outsider, status: 404, error: 'room_not_found' });
  return played;
}

const harborsGame = 'jade-harbors';

async function seedHarbors(call, { host, outsider }) {
  let table = await createTable(call, harborsGame, host, true, 3);
  assert.deepEqual([table.status, table.seat, table.revision, table.invite], ['playing', 0, 0, null],
    'Jade Harbors did not start a private bot table');
  assert.deepEqual(table.seats.map(({ seat, occupied, bot }) => [seat, occupied, bot]),
    [[0, true, false], [1, true, true], [2, true, true]], 'Jade Harbors did not seat one guest and two bots');

  const pier = ['placePier', 'buildPier', 'piers', 'nodes'];
  const route = ['placeRoute', 'buildRoute', 'routes', 'routes'];
  for (const [phase, kind, options, pieces] of [pier, route, pier, route]) {
    const position = table.position;
    assert.deepEqual([position.phase, position.turnSeat, position.turn, position.lastRoll], [phase, 0, 0, null],
      'Jade Harbors did not return the correct opening step to the guest');
    const target = position.legalActions[options][0];
    assert(Number.isInteger(target) && target >= 0, `Jade Harbors offered no legal ${kind} target`);
    table = await act(call, harborsGame, table, host, { kind, target });
    assert.equal(table.position[pieces].find(piece => piece.id === target)?.seat, table.seat,
      `Jade Harbors did not commit the requested ${kind}`);
  }

  const before = table.position;
  assert.deepEqual([before.phase, before.turnSeat, before.turn, before.lastRoll, before.legalActions.canRoll],
    ['roll', 0, 1, null, true], 'Jade Harbors did not make the first roll available after setup');
  const rolled = await act(call, harborsGame, table, host, roll);
  const { phase, turnSeat, turn, lastRoll, legalActions, ownResources, seats } = rolled.position;
  assert.deepEqual([phase, turnSeat, turn, legalActions.canRoll, legalActions.canEndTurn], ['build', 0, 1, false, true],
    'Jade Harbors did not enter the guest’s build phase with a legal End Turn');
  assert(Array.isArray(lastRoll), 'Jade Harbors did not expose the rolled dice pair');
  checkDice(lastRoll, 2);

  assert.deepEqual(Object.keys(rolled.position).sort(),
    ['lastRoll', 'legalActions', 'nodes', 'ownResources', 'phase', 'routes', 'seats', 'tiles', 'turn', 'turnSeat', 'winnerSeat'],
    'Jade Harbors exposed an unexpected position field');
  for (const seat of seats) {
    assert.deepEqual(Object.keys(seat).sort(), ['points', 'resourceCount', 'seat'],
      'Jade Harbors exposed more than public points and resource counts for a seat');
    assert(Number.isInteger(seat.resourceCount) && seat.resourceCount >= 0, 'Jade Harbors returned an invalid resource total');
  }
  assert.deepEqual(seats.map(({ seat, points }) => [seat, points]), [[0, 2], [1, 2], [2, 2]],
    'Jade Harbors did not finish setup for all three seats');
  assert(Array.isArray(ownResources) && ownResources.length === 4 &&
    ownResources.every(count => Number.isInteger(count) && count >= 0),
    'Jade Harbors did not project exactly the viewer’s four resource counts');
  assert.equal(ownResources.reduce((sum, count) => sum + count, 0), seats[rolled.seat].resourceCount,
    'Jade Harbors viewer holdings did not match the public total');

  const earned = [0, 0, 0, 0];
  const production = [0, 0, 0];
  const nodes = new Map(before.nodes.map(node => [node.id, node]));
  const total = lastRoll[0] + lastRoll[1];
  for (const { number, resource, nodeIds } of before.tiles) {
    if (number !== total || resource === null) continue;
    for (const id of nodeIds) {
      const { seat, level } = nodes.get(id);
      if (seat === null) continue;
      production[seat] += level;
      if (seat === rolled.seat) earned[resource] += level;
    }
  }
  assert.deepEqual(ownResources, before.ownResources.map((count, kind) => count + earned[kind]),
    'Jade Harbors did not commit the viewer’s production for the rolled pair');
  assert.deepEqual(seats.map(({ resourceCount }) => resourceCount),
    before.seats.map(({ seat, resourceCount }) => resourceCount + production[seat]),
    'Jade Harbors did not update public resource totals for the rolled pair');
  await call(tablePath(harborsGame, rolled), { cookie: outsider, status: 404, error: 'room_not_found' });
  return rolled;
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
  const farkle = await seedFarkle(call, guests);
  const backgammon = await seedBackgammon(call, guests);
  const bigTwo = await seedBigTwo(call, guests);
  const harbors = await seedHarbors(call, guests);
  console.log('Played private Hold’em and Jade Letters with different guests and bots; drew one Solitaire card and marked a unique Sudoku.');
  console.log('Played Farkle and Backgammon against bots and invited guests; rejected client dice and unauthorized or stale writes.');
  console.log('Played a legal Big Two turn with three bots and verified the guest-only hand.');
  console.log('Completed Jade Harbors setup with two bots and verified a server roll, guest holdings, and public totals.');
  return { poker, words, solo, farkle, backgammon, bigTwo, harbors };
}

export async function resumeExpansion(call, { host, visitor, outsider }, state) {
  for (const game of ['poker', 'words', 'farkle', 'backgammon']) {
    const { botReply, pair } = state[game];
    assert.deepEqual(await readSeat(call, game, botReply, host), botReply, `${game} did not restore the bot reply`);
    const resumed = await Promise.all([host, visitor].map(cookie => readSeat(call, game, pair[0], cookie)));
    assert.deepEqual(resumed, pair, `${game} did not restore the public position and both private seats`);
    if (game === 'words') {
      checkWordProjection(botReply);
      checkWordProjection(resumed[0], resumed[1]);
      checkWordProjection(resumed[1], resumed[0]);
    }
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
  const { bigTwo, harbors } = state;
  const { tableId } = await call(`api/${bigTwoGame}/resume`, { cookie: host });
  assert(tableId === bigTwo.id, 'Big Two did not offer the restored bot table');
  assert(isDeepStrictEqual(await readSeat(call, bigTwoGame, bigTwo, host), bigTwo),
    'Big Two did not recover the committed turn and the caller’s original hand');
  await call(tablePath(bigTwoGame, bigTwo), { cookie: outsider, status: 404, error: 'room_not_found' });
  console.log('Restored private cards and racks, Big Two’s bot table, dice and checker positions, and solo progress without exposing other guest data.');
  assert.deepEqual(await call(`api/${harborsGame}/resume`, { cookie: host }), { tableId: harbors.id },
    'Jade Harbors did not offer the restored bot table');
  assert.deepEqual(await readSeat(call, harborsGame, harbors, host), harbors,
    'Jade Harbors did not restore the exact position, dice pair, and viewer holdings');
  await call(tablePath(harborsGame, harbors), { cookie: outsider, status: 404, error: 'room_not_found' });
  console.log('Restored Jade Harbors with the original guest, exact board, and committed dice pair.');
}
