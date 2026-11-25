import { atLeast, parseServerMessage } from "../protocol";
import type { Ack, ClientMessage, Lease, Revisions, Snapshot } from "../protocol";
import { requestId } from "../session";

type RestartIssue = { message: string; uncertain: boolean };
const changed = "The Mahjong table changed or is no longer available. Continue to check the table.";
const unknown = "The restart wasn’t confirmed. Continue to check the Mahjong table before starting again.";

function botHost(state: Snapshot): boolean {
  const { hostSeat, seats, status } = state.room;
  const seat = state.self.seat;
  return !!state.game && (status === "playing" || status === "betweenHands" || status === "complete") && hostSeat === seat &&
    [0, 1, 2, 3].every(index => seats.some(member => member?.seat === index && member.kind === (index === seat ? "human" : "bot")));
}

function committed(ack: Ack, roomId: string): Revisions | undefined {
  return ack.status === "ok" && ack.roomId === roomId && ack.roomRevision !== null && ack.gameRevision !== null
    ? { roomRevision: ack.roomRevision, gameRevision: ack.gameRevision } : undefined;
}

export async function freshMahjong(mount: string, roomId: string): Promise<RestartIssue | undefined> {
  let socket: WebSocket;
  try { socket = new WebSocket(`${mount}ws`); }
  catch { return { message: "Couldn’t open the Mahjong connection. Continue to check the table.", uncertain: false }; }
  return new Promise(resolve => {
    let lease: Lease | undefined;
    let leasedAt = 0;
    const syncId = requestId();
    let baseline: Snapshot | undefined;
    let synced: Revisions | undefined;
    let restarted: Revisions | undefined;
    let restartId: string | undefined;
    let done = false;
    const timeout = window.setTimeout(disconnected, 8000);
    window.addEventListener("pagehide", disconnected, { once: true });

    function finish(message?: string, uncertain = false) {
      if (done) return;
      done = true;
      window.clearTimeout(timeout);
      window.removeEventListener("pagehide", disconnected);
      if (socket.readyState < WebSocket.CLOSING) socket.close(1000);
      resolve(message ? { message, uncertain } : undefined);
    }

    function disconnected() {
      finish(restartId ? unknown : "Couldn’t reach the Mahjong table. Continue to check the table.", !!restartId);
    }

    function send(message: ClientMessage) {
      try { socket.send(JSON.stringify(message)); }
      catch { disconnected(); }
    }

    socket.addEventListener("close", event => {
      if (event.code === 4001 && !restartId) finish("Another tab took over the Mahjong seat. Continue to check the table.");
      else disconnected();
    });
    socket.addEventListener("error", disconnected);
    socket.addEventListener("message", event => {
      if (done) return;
      const message = parseServerMessage(event.data);
      if (!message) return;
      if (message.type === "error") {
        finish(restartId ? unknown : changed, !!restartId);
      } else if (message.type === "lease" && !lease) {
        lease = message;
        leasedAt = performance.now();
        send({ v: 1, type: "sync", requestId: syncId, roomId });
      } else if (message.type === "ack" && message.requestId === syncId && !restartId) {
        synced = committed(message, roomId);
        if (!synced) finish(changed);
      } else if (message.type === "ack" && restartId && message.requestId === restartId) {
        if (message.status === "error") { finish(changed); return; }
        restarted = committed(message, roomId);
        // A bot may advance the game revision between attachment and the restart.
        if (!restarted || restarted.roomRevision !== baseline!.roomRevision + 1 ||
          restarted.gameRevision <= baseline!.gameRevision) finish(unknown, true);
      } else if (message.type === "snapshot" && synced) {
        if (message.roomId !== roomId) { finish(restartId ? unknown : changed, !!restartId); return; }
        if (!restartId && atLeast(message, synced)) {
          if (!botHost(message) || !lease ||
            Date.parse(lease.expiresAtUtc) - Date.parse(lease.serverTimeUtc) <= performance.now() - leasedAt) {
            finish(changed);
            return;
          }
          baseline = message;
          restartId = requestId();
          send({ v: 1, type: "command", requestId: restartId, lease: lease.lease,
            op: "restartMatch", payload: { roomId, expectedRoomRevision: message.roomRevision } });
        } else if (restarted && atLeast(message, restarted)) {
          if (botHost(message) && message.room.status === "playing" && message.game?.handNumber === 1 &&
              message.game.settlement === null && message.self.seat === baseline!.self.seat &&
              message.room.matchHands === baseline!.room.matchHands) finish();
          else finish(unknown, true);
        }
      }
    });
  });
}
