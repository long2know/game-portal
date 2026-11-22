import type { AudioChoices, LanternAudio } from "./audio";
import type { Modal } from "./dialog";
import { button, el, text } from "./dom";
import type { Guest, Preferences, Snapshot } from "./protocol";
import { field, nameField, submit, toggle } from "./screens";

interface Settings {
  guest: Guest;
  state: Snapshot | null;
  available: boolean;
  audio: LanternAudio;
  modal: Modal;
  opener: HTMLButtonElement;
  leave: () => void;
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

export function showSettings({ guest, state, available, audio, modal, opener, leave, save }: Settings): HTMLButtonElement[] {
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
  const voice = audio.deviceChoices ? select([
    ["off", "Off"], ["mixed", "English Chow · Mandarin Peng/Hu"],
    ["lower", "Mandarin · lower"], ["higher", "Mandarin · higher"],
  ], audio.choices.voice) : undefined;
  const chosenTrack = () => track?.value === "bamboo" ? "bamboo" : "still";
  const preview = () => audio.preview([music.input.checked, Number(volume.value) / 100, chosenTrack()]);
  track?.addEventListener("change", preview);
  music.input.addEventListener("change", preview);
  volume.addEventListener("input", () => {
    text(readout, `Volume · ${volume.value}%`);
    preview();
  });
  const audioNote = el("p", "note", audio.note);
  audioNote.setAttribute("role", "status");
  audio.changed = () => text(audioNote, audio.note);
  const control = submit("Save settings");
  const actions = el("div", "row-actions", control);
  const reserve = state?.room.status === "playing" || state?.room.status === "betweenHands";
  const leaveButton = button(reserve ? "Back to lobby" : "Leave table", "btn subtle", leave);
  leaveButton.disabled = !available;
  if (state) actions.append(leaveButton);
  const form = el("form", "form", field("Display name", name), sound.field, music.field);
  if (track) form.append(field("Soundtrack", track));
  form.append(el("label", "field", readout, volume), audioNote);
  if (voice) {
    const note = el("p", "note", "Calls follow tile sounds and volume. The soundtrack and voice are saved in this browser for this portal.");
    note.id = "device-audio-note";
    voice.setAttribute("aria-describedby", note.id);
    form.append(field("Spoken table calls", voice), note);
  }
  form.append(motion.field, actions);
  if (reserve) form.append(el("p", "note", "Your seat stays reserved. Timed decisions continue at the table."));
  form.addEventListener("submit", event => {
    event.preventDefault();
    const displayName = name.value.trim();
    if (!displayName || /[\u0000-\u001f\u007f-\u009f]/u.test(displayName)) {
      modal.message("Enter a display name of 1–24 characters without controls.");
      name.focus();
      return;
    }
    const prefs: Preferences = { sound: sound.input.checked, music: music.input.checked,
      volume: Number(volume.value) / 100, reducedMotion: motion.input.checked };
    if (prefs.sound || prefs.music) void audio.unlock(true);
    save(displayName, prefs, form, control, voice ? { track: chosenTrack(), voice: voice.value as AudioChoices["voice"] } : undefined);
  });
  modal.show("settings", "Settings", form, opener);
  void import("./fullscreen").then(module => module.addFullscreen(actions),
    () => actions.before(el("p", "note", "Full screen unavailable.")));
  return state ? [leaveButton] : [];
}
