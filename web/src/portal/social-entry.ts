import type { Guest } from "../protocol";
import type { InviteTable } from "./invite-handoff";
import type { OnlineLobby } from "./social";

export interface LobbyHooks {
  mount: string;
  openGuest: (refresh?: boolean) => Promise<Guest>;
  saveName: (name: string) => Promise<Guest>;
}

export interface LobbyState {
  ready: boolean;
  busy: boolean;
  table?: InviteTable;
}

export const preferenceKey = (mount: string) => `${mount}court-availability-change`;

export function onlineLobby(trigger: HTMLButtonElement, hooks: LobbyHooks, ready = false, localOnly = false) {
  const tabId = Array.from(crypto.getRandomValues(new Uint8Array(16)), value => value.toString(16).padStart(2, "0")).join("");
  const preference = preferenceKey(hooks.mount);
  let state: LobbyState = { ready, busy: false };
  let loading: Promise<OnlineLobby> | undefined;
  let lobby: OnlineLobby | undefined;
  let probed = false;
  let probe: Promise<void> | undefined;
  let nextProbe = 0;
  trigger.title = "Online lobby";
  trigger.setAttribute("aria-label", trigger.title);
  trigger.setAttribute("aria-haspopup", "dialog");

  function load() {
    return loading ??= import("./social").then(async ({ OnlineLobby }) => {
      await probe;
      trigger.removeAttribute("data-lobby-error");
      trigger.setAttribute("aria-label", "Online lobby");
      return lobby = new OnlineLobby(trigger, hooks, tabId, () => state, nextProbe);
    }).catch(error => {
      loading = undefined;
      trigger.setAttribute("data-lobby-error", "");
      trigger.setAttribute("aria-label", "Online lobby unavailable. Try again.");
      throw error;
    });
  }

  function discover() {
    if (loading || probe || probed || !state.ready || document.hidden || !navigator.onLine || Date.now() < nextProbe) return;
    if (localOnly) {
      try { if (!localStorage.getItem(preference)?.startsWith("+")) return; }
      catch { return; }
    }
    probed = true;
    // Discover the persisted choice without advertising this tab or loading the panel on a cold visit.
    probe = fetch(`${hooks.mount}api/social/presence`, {
      method: "POST", mode: "same-origin", cache: "no-store", signal: AbortSignal.timeout(8000),
      headers: { "Content-Type": "application/json" }, body: JSON.stringify({ tabId, active: false, busy: false }),
    }).then(async response => {
      if (response.ok && (await response.json())?.available === true) void load().catch(() => {});
      if (response.status === 429) {
        nextProbe = Date.now() + 30_000;
        window.setTimeout(() => { probed = false; discover(); }, 30_000);
      }
    }).catch(() => {}).finally(() => { probe = undefined; });
  }

  const invalidate = () => {
    probed = false;
    if (lobby) lobby.refresh();
    else discover();
  };
  const open = (opener: HTMLButtonElement, invite = false) => {
    void load().then(lobby => {
      if (opener.isConnected && !document.hidden && !opener.closest("[hidden]")) lobby.open(opener, invite);
    }).catch(() => {});
  };
  trigger.addEventListener("click", () => open(trigger));
  window.addEventListener("storage", event => { if (event.key === preference) invalidate(); });
  window.addEventListener("online", () => { if (!loading) invalidate(); });
  document.addEventListener("visibilitychange", () => { if (!document.hidden && !loading) invalidate(); });
  window.addEventListener("pageshow", event => { if (event.persisted && !loading) invalidate(); });
  discover();

  return {
    open,
    update(next: LobbyState) {
      const changed = state.ready !== next.ready || state.busy !== next.busy ||
        state.table?.tableId !== next.table?.tableId || state.table?.game !== next.table?.game;
      state = next;
      if (changed) {
        if (lobby) lobby.refresh();
        else discover();
      }
    },
  };
}
