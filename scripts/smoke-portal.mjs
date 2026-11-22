import assert from 'node:assert/strict';
import { randomBytes } from 'node:crypto';
import { readFile, writeFile } from 'node:fs/promises';
import { request as httpRequest } from 'node:http';
import { request as httpsRequest } from 'node:https';
import { gzipSync } from 'node:zlib';
import { playExpansion, resumeExpansion } from './smoke-expansion.mjs';

const [phase, url, appVersion, commitSha, stateFile] = process.argv.slice(2);
assert(['seed', 'resume'].includes(phase) && url && appVersion && commitSha && stateFile,
  'Usage: node scripts/smoke-portal.mjs {seed|resume} APP_URL VERSION COMMIT STATE_FILE');
assert(/^(?:[0-9a-f]{40}|unknown)$/.test(commitSha), 'Expected a full Git SHA or unknown');
const app = new URL(url);
const { origin, pathname } = app;
const mount = pathname.slice(0, -1);
const expansionRoutes = ['holdem', 'solitaire', 'sudoku', 'word-tiles', 'farkle', 'backgammon'];
assert(['http:', 'https:'].includes(app.protocol) && ['/portal', '/game-portal'].includes(mount)
  && url === `${origin}${mount}/`,
  'APP_URL must be the HTTP(S) /portal/ or /game-portal/ mount, with no query or fragment');
assert(app.protocol !== 'https:' || process.env.NODE_TLS_REJECT_UNAUTHORIZED !== '0',
  'Portal HTTPS smoke requires certificate verification');

function fetchApp(path, init) {
  return fetch(new URL(path, app), { redirect: 'error', signal: AbortSignal.timeout(5000), ...init });
}

function checkResponse(response, type, path) {
  assert.equal(response.status, 200, path);
  assert.match(response.headers.get('content-type') ?? '', type, path);
  return response;
}

async function json(path, { cookie, body, requestOrigin = origin, status = 200, error } = {}) {
  const response = await fetchApp(path, {
    method: body === undefined ? 'GET' : 'POST',
    headers: {
      ...(requestOrigin === null ? {} : { Origin: requestOrigin }),
      ...(cookie ? { Cookie: cookie } : {}),
      ...(body === undefined ? {} : { 'Content-Type': 'application/json' }),
    },
    body: JSON.stringify(body),
  });
  assert.equal(response.status, status, path);
  assert.match(response.headers.get('content-type') ?? '', /^application\/json\b/i, path);
  if (path.startsWith('api/')) {
    assert.match(response.headers.get('cache-control') ?? '', /\bno-store\b/i, path);
  }
  const value = await response.json();
  if (error) assert.equal(value.error?.code, error, path);
  return { value, response };
}

const jsonValue = async (...args) => (await json(...args)).value;

async function checkShell(path, title) {
  const response = checkResponse(await fetchApp(path), /^text\/html\b/i, path);
  assert.match(response.headers.get('cache-control') ?? '', /\b(?:no-cache|no-store)\b/i, path);
  const html = await response.text();
  assert(html.includes(`data-base="${pathname}"`), `${path} must use the portal mount for assets and API`);
  assert(html.includes(`<title>${title}</title>`), `${path} did not return the ${title} shell`);
  return html;
}

async function checkApplication() {
  assert.deepEqual((await json('health/ready')).value, { status: 'ready' });
  assert.deepEqual((await json('health/live')).value, { status: 'live' });
  assert.deepEqual((await json('api/version')).value, { appVersion, protocolVersion: 1, commitSha });
  const missing = ['no-such-board/', 'farkle/not-a-table/', 'backgammon/not-a-table/', 'assets/infra-smoke-missing.js'];
  if (app.protocol === 'http:') missing.push('/api/version', '/mahjong/');
  for (const path of missing) {
    const response = await fetchApp(path);
    assert.equal(response.status, 404, `Portal unexpectedly served ${path}`);
    await response.body?.cancel();
  }
  for (const path of [mount, 'chess', ...expansionRoutes]) {
    const response = await fetchApp(`${path}?from=smoke%2Fredirect`, { redirect: 'manual' });
    assert.equal(response.status, 308, `${path} did not redirect to its canonical route`);
    assert.equal(response.headers.get('location'), `${new URL(path, app).pathname}/?from=smoke%2Fredirect`);
    await response.body?.cancel();
  }

  const title = 'Jade Court · Board Games';
  const html = await checkShell('', title);
  await checkShell('mahjong/', 'Jade Court · Changsha Mahjong');
  for (const game of expansionRoutes) await checkShell(`${game}/`, title);
  const { games } = (await json('api/board/games')).value;
  assert(Array.isArray(games), 'Missing the mounted catalog');
  for (const id of ['chess', 'checkers', 'chinese-checkers', 'go', 'reversi']) {
    assert(games.some(game => game.id === id), `Missing ${id} from the mounted catalog`);
  }
  assert.equal(new Set(games.map(game => game.id)).size, games.length, 'Duplicate catalog slugs');
  for (const game of games) {
    assert.match(game.id, /^[a-z][a-z0-9-]*$/, 'Unexpected board game slug');
    assert(game.playerCounts.includes(2) && game.variants.length > 0, `Catalog cannot start a two-player ${game.id} game`);
    await checkShell(`${game.id}/`, title);
  }

  const types = { js: /^(application|text)\/javascript\b/i, css: /^text\/css\b/i, svg: /^image\/svg\+xml\b/i };
  const assets = new Map([...html.matchAll(/\b(?:src|href)=(["'])([^"'?#]+\.(js|css|svg))\1/g)]
    .map(([, , path, extension]) => [path, extension]));
  for (const extension of ['js', 'css']) {
    assert([...assets.values()].includes(extension), `Portal missing a ${extension} entry asset`);
  }
  let coldGzipBytes = gzipSync(html).byteLength;
  for (const [path, extension] of assets) {
    assert(path.startsWith(`${pathname}assets/`), `Portal asset ${path} is outside the mount`);
    const asset = checkResponse(await fetchApp(path), types[extension], path);
    assert.match(asset.headers.get('cache-control') ?? '', /\bimmutable\b/i, `Hashed cache policy: ${path}`);
    const bytes = await asset.arrayBuffer();
    assert(bytes.byteLength > 0, `${path} was empty`);
    coldGzipBytes += gzipSync(bytes).byteLength;
  }
  assert(coldGzipBytes <= 24 * 1024, `Cold portal shell: ${coldGzipBytes} gzip bytes exceeds 24 KiB`);
  console.log(`Cold portal shell: ${coldGzipBytes} gzip bytes.`);
  return games;
}

async function guest(displayName) {
  const { value, response } = await json('api/guest', { body: { displayName } });
  assert.equal(value.displayName, displayName, 'Guest name did not persist');
  const cookie = response.headers.getSetCookie().find(header => header.startsWith('mahjong_guest='));
  assert(cookie, 'Guest cookie was not issued');
  assert(/;\s*httponly\b/i.test(cookie), 'Guest cookie must be HttpOnly');
  assert(/;\s*samesite=lax\b/i.test(cookie), 'Guest cookie must be SameSite=Lax');
  const cookiePath = cookie.split(';').map(part => part.trim()).find(part => /^path=/i.test(part))?.slice(5);
  assert.equal(cookiePath, mount, 'Guest cookie must be scoped to the mount');
  assert(!/;\s*domain=/i.test(cookie), 'Guest cookie must be host-only');
  assert.equal(/;\s*secure\b/i.test(cookie), app.protocol === 'https:', 'Unexpected guest cookie Secure attribute');
  return cookie.split(';', 1)[0];
}

function upgradeStatus(requestOrigin, cookie) {
  return new Promise((resolve, reject) => {
    const request = app.protocol === 'https:' ? httpsRequest : httpRequest;
    const upgrade = request(new URL('ws', app), {
      signal: AbortSignal.timeout(5000),
      headers: {
        Connection: 'Upgrade',
        Upgrade: 'websocket',
        'Sec-WebSocket-Key': randomBytes(16).toString('base64'),
        'Sec-WebSocket-Version': '13',
        ...(requestOrigin ? { Origin: requestOrigin } : {}),
        ...(cookie ? { Cookie: cookie } : {}),
      },
    }, response => {
      response.resume();
      resolve(response.statusCode);
    });
    upgrade.on('upgrade', (_response, socket) => {
      socket.destroy();
      resolve(101);
    });
    upgrade.on('error', reject);
    upgrade.end();
  });
}

const tables = 'api/board/tables';
function tablePath(table) {
  assert(typeof table.id === 'string' && table.id.length > 0, 'Table id was missing');
  return `${tables}/${encodeURIComponent(table.id)}`;
}
const readTable = async (table, cookie) => (await json(tablePath(table), { cookie })).value;
const resumeTable = async (game, cookie) => (await json(`api/board/resume?game=${game}`, { cookie })).value;
const play = async (table, cookie) => {
  const moveId = table.position.moves[0]?.id;
  assert(typeof moveId === 'string' && moveId.length > 0, `${table.game} offered no playable move`);
  const { value: { turnMotion, ...committed } } = await json(`${tablePath(table)}/moves`, {
    cookie, body: { revision: table.revision, moveId },
  });
  assert.equal(committed.id, table.id, 'Move response changed tables');
  assert.equal(committed.revision, table.revision + 1, 'Move did not commit one revision');
  assert.notDeepEqual(committed.position.cells, table.position.cells, 'Move did not change the board');
  assert.equal(turnMotion?.fromRevision, table.revision, 'Motion must start at the prior revision');
  assert.equal(turnMotion.steps[0]?.move.id, moveId, 'Motion must start with the accepted move');
  assert.equal(turnMotion.steps.at(-1)?.move.id, committed.position.lastMove.id, 'Motion must finish with the last move');
  const cells = new Map(table.position.cells.map(cell => [cell.id, cell]));
  for (const { changes } of turnMotion.steps) {
    for (const cell of changes) {
      assert(cells.has(cell.id), 'Motion changed an unknown cell');
      cells.set(cell.id, cell);
    }
  }
  assert.deepEqual(committed.position.cells.map(cell => cells.get(cell.id)), committed.position.cells,
    'Motion must replay to the committed board');
  return committed;
};

async function endTable(table, cookie, command, status) {
  const { value: ended } = await json(`${tablePath(table)}/${command}`, {
    cookie, body: { revision: table.revision },
  });
  assert.equal(ended.id, table.id, `${command} returned another table`);
  assert.equal(ended.revision, table.revision + 1, `${command} did not commit`);
  assert.equal(ended.status, status);
  assert.equal(ended.invite, null, `${command} did not revoke the invite`);
  assert.equal(ended.position.turn, null, `${command} left a playable turn`);
  assert.deepEqual(ended.position.moves, [], `${command} left legal moves`);
  assert.deepEqual(ended.position.cells, table.position.cells, `${command} changed the board`);
  return ended;
}

const games = await checkApplication();
if (phase === 'seed') {
  for (const requestOrigin of [null, 'https://unrelated-origin.invalid']) {
    await json('api/guest', { body: {}, requestOrigin, status: 403, error: 'forbidden' });
  }
  const host = await guest('Portal Smoke Host');
  const visitor = await guest('Portal Smoke Visitor');
  const outsider = await guest('Portal Smoke Outsider');
  assert.equal(new Set([host, visitor, outsider]).size, 3, 'Independent guests shared a cookie');
  assert.equal(await upgradeStatus(origin), 401, 'Unauthenticated mounted WebSocket');
  assert.equal(await upgradeStatus(null, host), 403, 'Origin-less mounted WebSocket');
  assert.equal(await upgradeStatus('https://unrelated-origin.invalid', host), 403, 'Cross-origin mounted WebSocket');
  assert.equal(await upgradeStatus(origin, host), 101, 'Same-origin mounted WebSocket');

  const { value: solo } = await json(tables, {
    cookie: host, body: { game: 'chess', variant: 'standard', players: 2, bots: true, botDifficulty: 'hard' },
  });
  assert.equal(solo.game, 'chess');
  assert.equal(solo.botDifficulty, 'hard', 'Chess did not use the requested Hard preset');
  assert.equal(solo.status, 'playing');
  assert.equal(solo.seat, 0);
  assert.equal(solo.position.turn, solo.seat);
  assert.equal(solo.seats.find(seat => seat.seat === 1)?.bot, true, 'Chess bot seat was missing');
  const botReply = await play(solo, host);
  assert.equal(botReply.botDifficulty, solo.botDifficulty, 'Chess move changed the bot preset');
  assert.equal(botReply.position.turn, solo.seat, 'Chess bot did not return the turn to the human');
  const blackCells = table => table.position.cells.filter(cell => cell.piece && cell.owner === 1);
  assert(blackCells(solo).length > 0, 'Chess was missing its opposing pieces');
  assert.notDeepEqual(blackCells(botReply), blackCells(solo), 'Chess bot did not move an opposing piece');

  const variant = games.find(game => game.id === 'reversi').variants[0].id;
  const { value: waiting } = await json(tables, {
    cookie: host, body: { game: 'reversi', variant, players: 2, bots: false },
  });
  assert.equal(waiting.status, 'waiting');
  assert.equal(waiting.position, null);
  assert.match(waiting.invite, /^[0-9a-f]{32}$/, 'Invite-only table did not issue a private code');
  await json(tablePath(waiting), { cookie: outsider, status: 404, error: 'room_not_found' });
  const { value: joined } = await json('api/board/join', { cookie: visitor, body: { invite: waiting.invite } });
  assert.equal(joined.id, waiting.id);
  assert.equal(joined.status, 'playing');
  assert.equal(joined.invite, null, 'A started table must not disclose its invite code');
  assert.equal(joined.seat, 1);
  const hostTurn = await readTable(waiting, host);
  assert.equal(hostTurn.seat, 0);
  assert.equal(hostTurn.position.turn, 0);
  const hostMove = await play(hostTurn, host);
  const visitorTurn = await readTable(waiting, visitor);
  assert.equal(visitorTurn.seat, 1);
  assert.equal(visitorTurn.position.turn, 1);
  assert.equal(visitorTurn.revision, hostMove.revision);
  await json(`${tablePath(waiting)}/moves`, {
    cookie: visitor,
    body: { revision: hostTurn.revision, moveId: visitorTurn.position.moves[0].id },
    status: 409,
    error: 'stale_room',
  });
  const visitorMove = await play(visitorTurn, visitor);
  const expansion = await playExpansion(jsonValue, { host, visitor, outsider });
  await writeFile(stateFile, JSON.stringify({
    host, visitor, outsider, botReply, visitorMove, reversiInvite: waiting.invite, expansion,
  }), { mode: 0o600, flag: 'wx' });
  console.log('Verified portal routes, cookies and WebSocket admission; played a Chess bot and two guests exchanged Reversi moves.');
} else {
  const { host, visitor, outsider, botReply, visitorMove, reversiInvite, expansion } =
    JSON.parse(await readFile(stateFile, 'utf8'));
  assert.deepEqual(await readTable(botReply, host), botReply, 'Chess bot table or guest did not survive');
  assert.deepEqual(await readTable(visitorMove, visitor), visitorMove, 'Reversi invite table or guest did not survive');
  assert.deepEqual(await resumeTable('reversi', host),
    { tableId: visitorMove.id }, 'Host could not find the recovered invite table');
  console.log('Recovered both portal tables with their original guest cookies, seats and positions.');
  await resumeExpansion(jsonValue, { host, visitor, outsider }, expansion);

  const left = await endTable(visitorMove, visitor, 'leave', 'closed');
  assert.equal(left.endReason, 'left', 'Reversi leave did not record its reason');
  assert.equal(left.endedBySeat, visitorMove.seat, 'Reversi leave did not record the departing seat');
  assert.equal((await readTable(left, host)).status, 'closed', 'Reversi host did not see the leave');
  for (const cookie of [host, visitor]) {
    assert.deepEqual(await resumeTable('reversi', cookie), { tableId: null }, 'Reversi resume offered a closed table');
  }
  await json('api/board/join', {
    cookie: visitor, body: { invite: reversiInvite }, status: 404, error: 'room_not_found',
  });

  const resigned = await endTable(botReply, host, 'resign', 'finished');
  assert.deepEqual(resigned.result, { cause: 'resignation', loserSeat: 0, winnerSeat: 1 });
  assert.deepEqual(resigned.position.winners, [1], 'Chess board did not record the win by resignation');
  assert.deepEqual(await resumeTable('chess', host), { tableId: null }, 'Chess resume offered a resigned table');
  console.log('Closed the live Reversi table, revoked its invite, and finished Chess by resignation.');
}
