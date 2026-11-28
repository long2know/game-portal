import type { AudioChoices, LanternAudio } from "./audio";
import type { Modal } from "./dialog";
import { button, el, text } from "./dom";
import type { Guest, Payloads, Preferences, Snapshot } from "./protocol";
import { field, nameField, submit, toggle } from "./screens";

interface Settings {
  guest: Guest;
  snapshot: () => Snapshot | null;
  available: boolean;
  portal: boolean;
  audio: LanternAudio;
  modal: Modal;
  opener: HTMLButtonElement;
  leave: (payload: Payloads["leaveRoom"]) => boolean;
  releasingSeat: () => boolean;
  save: (name: string, prefs: Preferences, form: HTMLFormElement, control: HTMLButtonElement, choices?: AudioChoices) => void;
}

function select(options: [string, string][], value: string): HTMLSelectElement {
  const input = el("select");
  for (const [id, label] of options) {
    const option = el("option", "", label);
    option.value = id;
    input.add(option);
  }
  input.value = value;
  return input;
}

export function showSettings({ guest, snapshot, available, portal, audio, modal, opener, leave, releasingSeat, save }: Settings): (enabled: boolean) => void {
  const name = nameField(guest.displayName);
  name.required = true;
  const preferences = guest.preferences;
  const sound = toggle("Tile and turn sounds", preferences.sound);
  const music = toggle("Quiet table music", preferences.music);
  const motion = toggle("Reduce visual motion", preferences.reducedMotion);
  const volume = el("input");
  volume.type = "range";
  volume.min = "0";
  volume.max = "100";
  volume.value = String(Math.round(preferences.volume * 100));
  const readout = el("span", "", `Volume · ${volume.value}%`);
  const track = audio.deviceChoices ? select([["still", "Still Water"], ["bamboo", "Bamboo by the Xiang"]], audio.choices.track) : undefined;
  const recordingKey = audio.callRecordingKey;
  const voice = recordingKey ? select([
    ["off", "Off"], ["mixed", "English Cha · Mandarin Peng/Hu"],
    ["lower", "Mandarin · lower"], ["higher", "Mandarin · higher"],
    ["changsha", "Changsha dialect (your recordings)"],
  ], audio.choices.voice) : undefined;
  let changshaReady = false;
  const chosenTrack = () => track?.value === "bamboo" ? "bamboo" : "still";
  const preview = () => audio.preview({
    sound: sound.input.checked, music: [music.input.checked, Number(volume.value) / 100, chosenTrack()],
  });
  track?.addEventListener("change", preview);
  sound.input.addEventListener("click", preview);
  sound.input.addEventListener("change", preview);
  music.input.addEventListener("click", preview);
  music.input.addEventListener("change", preview);
  volume.addEventListener("input", event => {
    text(readout, `Volume · ${volume.value}%`);
    preview();
    if (event.isTrusted && navigator.userActivation?.isActive) audio.unlock();
  });
  preview();
  const audioNote = el("p", "note", audio.note);
  audioNote.id = "table-audio-note";
  audioNote.setAttribute("role", "status");
  audio.changed = () => text(audioNote, audio.note);
  const test = button("Test sound", "btn subtle");
  test.setAttribute("aria-describedby", audioNote.id);
  test.addEventListener("click", event => {
    if (!event.isTrusted) return;
    event.preventDefault();
    audio.testSound(() => form.isConnected && !form.hidden && modal.root.open && modal.kind === "settings");
  });
  const control = submit("Save settings");
  const actions = el("div", "row-actions", control);
  const form = el("form", "form", field("Display name", name), sound.field, music.field);
  if (track) form.append(field("Soundtrack", track));
  form.append(el("label", "field", readout, volume), test, audioNote);
  if (voice && recordingKey) {
    const note = el("p", "note", "Calls follow tile sounds and volume. The soundtrack and voice are saved in this browser for this portal.");
    note.id = "device-audio-note";
    voice.setAttribute("aria-describedby", note.id);
    const option = voice.querySelector<HTMLOptionElement>('option[value="changsha"]')!;
    option.disabled = true;
    const recordings = el("div", "note", "Checking Changsha recordings…");
    form.append(field("Spoken table calls", voice), note, recordings);
    void import("./changsha-settings").then(({ changshaSettings }) => {
      if (!form.isConnected || !modal.root.open) return;
      recordings.replaceWith(changshaSettings(recordingKey, modal.root, ready => {
        changshaReady = ready;
        option.disabled = !ready;
      }));
    }, () => text(recordings, "Changsha recordings are unavailable. Table cues still play."));
  }
  form.append(motion.field, actions);
  const roomId = snapshot()?.roomId;
  const room = () => {
    const state = snapshot();
    return state?.roomId === roomId ? state : null;
  };
  const canRelease = (state: Snapshot | null) => portal && state?.room.status === "playing" &&
    state.room.seats.some(seat => seat.kind === "human" && seat.seat !== state.self.seat);
  let enabled = available;
  const leaveButton = button("Leave table", "btn subtle", () => {
    const state = room();
    if (enabled && state) leave({ roomId: state.roomId, expectedRoomRevision: state.roomRevision });
  });
  const reserveNote = el("p", "note", "Your seat stays reserved. Timed decisions continue at the table.");
  if (roomId) {
    actions.append(leaveButton);
    form.append(reserveNote);
  }
  const handoffLabel = "Leave and let a bot play";
  const handoff = button(handoffLabel, "btn secondary", () => {
    if (!enabled || !canRelease(room())) return;
    show(true);
    cancel.focus();
  });
  const handoffActions = el("div", "row-actions", handoff);
  if (portal && roomId) form.append(handoffActions);
  const cancel = button("Cancel", "btn subtle", () => {
    show(false);
    (handoffActions.hidden ? leaveButton : handoff).focus();
  });
  const confirm = button(handoffLabel, "btn primary", () => {
    const state = room();
    if (!enabled || !state || !canRelease(state)) return;
    if (!leave({ roomId: state.roomId, expectedRoomRevision: state.roomRevision, replaceWithBot: true })) update(false);
  });
  const warning = el("p", "detail", "A bot takes your place in the current hand. The other players keep playing. Your seat won’t be reserved, and you can’t rejoin this hand.");
  warning.id = "seat-handoff-warning";
  confirm.setAttribute("aria-describedby", warning.id);
  const handoffStatus = el("p", "note");
  handoffStatus.setAttribute("role", "status");
  const confirmation = el("div", "form", warning, el("div", "row-actions", cancel, confirm), handoffStatus);
  const content = portal && roomId ? el("div", "", form, confirmation) : form;
  function show(confirming = false): void {
    form.hidden = confirming;
    confirmation.hidden = !confirming;
    modal.show("settings", confirming ? `${handoffLabel}?` : "Settings", content, opener);
  }
  function update(available: boolean): void {
    enabled = available;
    const state = room();
    const reserve = state?.room.status === "playing" || state?.room.status === "betweenHands";
    leaveButton.hidden = !state;
    leaveButton.disabled = !available;
    text(leaveButton, reserve ? "Back to lobby" : "Leave table");
    reserveNote.hidden = !reserve;
    const eligible = canRelease(state);
    handoffActions.hidden = !eligible;
    handoff.disabled = !available;
    confirm.disabled = !available || !eligible;
    const pending = releasingSeat();
    cancel.disabled = pending;
    const status = pending ? "Waiting for the table to confirm your handoff…" :
      eligible ? "" : "The handoff is no longer available. Return to Settings to review the table.";
    handoffStatus.hidden = !status;
    text(handoffStatus, status);
  }
  form.addEventListener("submit", event => {
    event.preventDefault();
    const displayName = name.value.trim();
    if (!displayName || /[\u0000-\u001f\u007f-\u009f]/u.test(displayName)) {
      modal.message("Enter a display name of 1–24 characters without controls.");
      name.focus();
      return;
    }
    if (voice?.value === "changsha" && !changshaReady && audio.choices.voice !== "changsha") {
      modal.message("Save all three Changsha recordings before enabling this voice.");
      return;
    }
    const prefs: Preferences = { sound: sound.input.checked, music: music.input.checked,
      volume: Number(volume.value) / 100, reducedMotion: motion.input.checked };
    save(displayName, prefs, form, control, voice ? { track: chosenTrack(), voice: voice.value as AudioChoices["voice"] } : undefined);
  });
  update(available);
  show();
  void import("./fullscreen").then(module => module.addFullscreen(actions),
    () => actions.before(el("p", "note", "Full screen unavailable.")));
  return update;
}
