import type { TableCall } from "./callouts";

type Pack<T> = Record<TableCall, T>;
// Store bounded buffers: WebKit can reject native Blob writes to IndexedDB.
export type Recording = { bytes: ArrayBuffer; type: string };
export type Recordings = Pack<Recording>;

const limit = 192 * 1024;
const formats: ReadonlyMap<string, string> = new Map([
  ["m4a", "audio/mp4"], ["mp3", "audio/mpeg"], ["wav", "audio/wav"],
]);
const mimeTypes = new Set(formats.values());
export const recordingAccept = ".m4a,.mp3,.wav," + [...mimeTypes].join(",");

function checkSize(size: number): void {
  if (!size || size > limit) throw new Error("Choose a nonempty clip no larger than 192 KiB.");
}

async function decode(recording: Recording, context: BaseAudioContext): Promise<AudioBuffer> {
  if (!mimeTypes.has(recording.type)) throw new Error("Choose an M4A, MP3 or WAV audio file.");
  checkSize(recording.bytes.byteLength);
  // Web Audio takes ownership of its input; retain the exact bytes for saving and auditioning.
  const buffer = await context.decodeAudioData(recording.bytes.slice(0)).catch(() => {
    throw new Error("This browser couldn’t decode the clip. Try an M4A, MP3 or WAV that plays on this device.");
  });
  if (buffer.duration < .1 || buffer.duration > 2 || buffer.numberOfChannels > 2)
    throw new Error("Choose a mono or stereo clip between 0.1 and 2 seconds.");
  for (let channel = 0; channel < buffer.numberOfChannels; channel++) {
    const pcm = buffer.getChannelData(channel);
    if (pcm.reduce((sum, sample) => sum + sample * sample, 0) >= pcm.length * .003 ** 2) return buffer;
  }
  throw new Error("The clip is silent or too quiet.");
}

let validator: OfflineAudioContext | undefined;
const validationContext = () => validator ??= new OfflineAudioContext(1, 1, 24000);

export async function validateFile(file: File): Promise<Recording> {
  const type = formats.get(file.name.match(/\.([^.]+)$/)?.[1].toLowerCase() ?? "");
  if (!type) throw new Error("Choose an M4A, MP3 or WAV audio file.");
  checkSize(file.size);
  const recording = { bytes: await file.arrayBuffer(), type };
  await decode(recording, validationContext());
  return recording;
}

export async function decodeRecordings(recordings: Recordings, context: BaseAudioContext = validationContext()): Promise<Pack<AudioBuffer>> {
  const [chi, peng, hu] = await Promise.all([
    decode(recordings.chi, context), decode(recordings.peng, context), decode(recordings.hu, context),
  ]);
  return { chi, peng, hu };
}

const database = "jade-court-changsha-calls";
const storeName = "recordings";

async function useStore<T>(mode: IDBTransactionMode, action: (store: IDBObjectStore) => IDBRequest<T>): Promise<T> {
  const db = await new Promise<IDBDatabase>((resolve, reject) => {
    const request = indexedDB.open(database, 1);
    request.onupgradeneeded = () => request.result.createObjectStore(storeName);
    request.onerror = () => reject(request.error);
    request.onsuccess = () => resolve(request.result);
  });
  try {
    return await new Promise<T>((resolve, reject) => {
      const tx = db.transaction(storeName, mode);
      const request = action(tx.objectStore(storeName));
      tx.oncomplete = () => resolve(request.result);
      tx.onabort = () => reject(tx.error);
    });
  } finally { db.close(); }
}

export async function readRecordings(key: string): Promise<Recordings | undefined> {
  const isRecording = (clip: unknown): clip is Recording => !!clip && typeof clip === "object" &&
    "bytes" in clip && clip.bytes instanceof ArrayBuffer && "type" in clip && typeof clip.type === "string";
  const saved = await useStore<unknown>("readonly", store => store.get(key));
  if (saved === undefined) return undefined;
  if (!saved || typeof saved !== "object" || !("chi" in saved) || !("peng" in saved) || !("hu" in saved) ||
      !isRecording(saved.chi) || !isRecording(saved.peng) || !isRecording(saved.hu))
    throw new Error("The saved recordings are incomplete.");
  return { chi: saved.chi, peng: saved.peng, hu: saved.hu };
}

const changes = new EventTarget();
const channel = (() => {
  try {
    const channel = new BroadcastChannel(database);
    channel.onmessage = event => {
      if (typeof event.data === "string") changes.dispatchEvent(new Event(event.data));
    };
    return channel;
  } catch { return undefined; }
})();

export function watchRecordings(key: string, changed: () => void): () => void {
  changes.addEventListener(key, changed);
  return () => changes.removeEventListener(key, changed);
}

export async function saveRecordings(key: string, recordings?: Recordings): Promise<void> {
  if (recordings) await useStore("readwrite", store => store.put(recordings, key));
  else await useStore("readwrite", store => store.delete(key));
  changes.dispatchEvent(new Event(key));
  try { channel?.postMessage(key); } catch { /* The other tab can still reload its settings. */ }
}
