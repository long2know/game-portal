import { decodeRecordings, readRecordings, recordingAccept, saveRecordings, validateFile, watchRecordings } from "./changsha-calls";
import type { Recording, Recordings } from "./changsha-calls";
import type { TableCall } from "./callouts";
import { button, el, text } from "./dom";
import { field } from "./screens";

const calls = [["chi", "Cha"], ["peng", "Peng"], ["hu", "Hu"]] as const;

export function changshaSettings(key: string, dialog: HTMLDialogElement, ready: (available: boolean) => void): HTMLElement {
  const status = el("p", "note");
  status.setAttribute("role", "status");
  const changes = el("p", "note");
  changes.setAttribute("role", "status");
  const details = el("details", "", el("summary", "", "Configure Changsha recordings"));
  const root = el("div", "form", status, details);
  const instructions = el("p", "note",
    "Ask a Changsha speaker to say each Mahjong call in their local dialect. Use only recordings you have permission to use. " +
    "Add one M4A (including Voice Memos), MP3 or WAV per call, 0.1–2 seconds and at most 192 KiB each. " +
    "On iPhone, trim each recording and save it to Files first.");
  const storage = el("p", "note",
    "Audition the clips, then save them together. They stay in this browser for this portal and are never uploaded. " +
    "Saving or deleting recordings doesn’t change your voice choice. Closing Settings discards unsaved files.");
  const audio = el("audio");
  const rows = new Map<TableCall, {
    input: HTMLInputElement; audition: HTMLButtonElement; message: HTMLElement; draft?: Recording | null; version: number;
  }>();
  const reset = button("Delete saved recordings", "btn subtle", () => void store());
  const save = button("Save recordings on this device", "btn secondary", () => {
    const chi = clip("chi"), peng = clip("peng"), hu = clip("hu");
    if (chi && peng && hu) void store({ chi, peng, hu });
  });
  let saved: Recordings | undefined;
  let reading = true, busy = false, canReset = false, closed = false, revision = 0;
  let previewUrl: string | undefined;

  function clip(call: TableCall): Recording | undefined {
    const draft = rows.get(call)?.draft;
    return draft === undefined ? saved?.[call] : draft ?? undefined;
  }

  function render(): void {
    save.disabled = busy || reading || !calls.every(([call]) => clip(call)) || ![...rows.values()].some(row => row.draft);
    reset.disabled = busy || reading || !canReset;
    for (const [call, row] of rows) {
      row.input.disabled = busy || reading;
      row.audition.disabled = busy || reading || !clip(call);
      if (row.draft === undefined) text(row.message, saved?.[call] ? "Saved clip." : "No clip chosen.");
    }
  }

  function stopPreview(): void {
    if (!previewUrl) return;
    audio.pause();
    audio.removeAttribute("src");
    audio.load();
    URL.revokeObjectURL(previewUrl);
    previewUrl = undefined;
  }

  function audition(call: TableCall): void {
    const recording = clip(call);
    if (!recording) return;
    stopPreview();
    const url = previewUrl = URL.createObjectURL(new Blob([recording.bytes], { type: recording.type }));
    audio.src = url;
    void audio.play().catch(() => {
      if (previewUrl !== url) return;
      stopPreview();
      text(changes, "Couldn’t audition the clip on this device.");
    });
  }

  function clearDrafts(): void {
    stopPreview();
    for (const row of rows.values()) {
      row.version++;
      row.draft = undefined;
      row.input.value = "";
    }
  }

  async function reload(): Promise<void> {
    const current = ++revision;
    reading = true;
    ready(false);
    text(status, "Checking saved Changsha recordings…");
    render();
    let next: Recordings | undefined, invalid = false;
    try {
      const pack = await readRecordings(key);
      if (pack) await decodeRecordings(pack);
      next = pack;
    } catch { invalid = true; }
    if (closed || current !== revision) return;
    saved = next;
    reading = false;
    canReset = !!saved || invalid;
    ready(!!saved);
    text(status, saved ? "All three Changsha recordings are saved. Select the voice above and save settings to use them."
      : invalid ? "Saved Changsha recordings couldn’t be read or played. Table cues still play."
      : "No Changsha recordings saved. This voice needs all three; otherwise table cues play.");
    render();
  }

  async function store(pack?: Recordings): Promise<void> {
    if (busy || reading) return;
    busy = true;
    render();
    try {
      await saveRecordings(key, pack);
      if (closed) return;
      clearDrafts();
      text(changes, pack ? "Recordings saved on this device. Your voice choice is unchanged." : "Saved recordings deleted.");
    } catch {
      if (!closed) text(changes, "Couldn’t change the saved recordings. Check browser storage and try again.");
    } finally {
      busy = false;
      if (!closed) render();
    }
  }

  const fields = calls.map(([call, label]) => {
    const input = el("input");
    input.type = "file";
    input.accept = recordingAccept;
    const message = el("p", "note");
    message.id = `changsha-${call}-recording`;
    message.setAttribute("role", "status");
    input.setAttribute("aria-describedby", message.id);
    const control = button(`Audition ${label}`, "btn subtle", () => audition(call));
    const row = { input, audition: control, message, version: 0, draft: undefined as Recording | null | undefined };
    rows.set(call, row);
    input.addEventListener("change", async () => {
      const file = input.files?.[0];
      if (!file) return;
      stopPreview();
      const version = ++row.version;
      row.draft = null;
      text(message, "Checking clip…");
      render();
      try {
        const clip = await validateFile(file);
        if (closed || version !== row.version) return;
        row.draft = clip;
        text(message, "Ready to audition and save.");
      } catch (error) {
        if (closed || version !== row.version) return;
        input.value = "";
        text(message, error instanceof Error ? error.message : "Couldn’t read this file.");
      }
      render();
    });
    return el("div", "", field(`${label} recording`, input), el("div", "row-actions", control, message));
  });
  details.append(el("div", "form", instructions, storage, ...fields, el("div", "row-actions", save, reset), changes));
  audio.addEventListener("ended", stopPreview);
  details.addEventListener("toggle", () => { if (!details.open) stopPreview(); });
  const unwatch = watchRecordings(key, () => { stopPreview(); void reload(); });
  const visibility = () => { if (document.hidden) stopPreview(); };
  document.addEventListener("visibilitychange", visibility);
  dialog.addEventListener("close", () => {
    closed = true;
    unwatch();
    document.removeEventListener("visibilitychange", visibility);
    clearDrafts();
    saved = undefined;
  }, { once: true });
  void reload();
  return root;
}
