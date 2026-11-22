import "./lantern-run.css";
import { button, el, text } from "../dom";
import type { PortalHost } from "../host";
import { LanternCourse, pace } from "./lantern-course";
import type { Hazard, Move } from "./lantern-course";
import { LanternScene } from "./lantern-scene";

type Phase = "ready" | "running" | "paused" | "over";
const moves: readonly [Move, string, string, string][] = [
  ["left", "←", "Move left", "ArrowLeft A"],
  ["jump", "↑", "Jump", "ArrowUp W"],
  ["slide", "↓", "Slide", "ArrowDown S"],
  ["right", "→", "Move right", "ArrowRight D"],
];
const keys = new Map(moves.flatMap(([move, , , shortcuts]) => shortcuts.toLowerCase().split(" ").map(key => [key, move])));
const courseTips: Record<Hazard, string> = {
  kiln: "Change lanes before the terracotta kilns.",
  rail: "Jump just before you reach the gold rail.",
  gate: "Slide just before you reach the jade gate.",
};

function readBest(key: string): number {
  try {
    const n = Number(localStorage.getItem(key));
    if (Number.isSafeInteger(n) && n > 0) return n;
  } catch { /* Storage may be unavailable. */ }
  return 0;
}

function runButton(label: string, className: string, activate: () => void): HTMLButtonElement {
  const control = button(label, className, activate);
  control.addEventListener("touchend", event => {
    const touch = event.changedTouches[0];
    if (!touch || event.touches.length || !event.cancelable || control.disabled ||
        !control.contains(document.elementFromPoint(touch.clientX, touch.clientY))) return;
    // Act on release; do not wait for or duplicate the browser's synthesized click.
    event.preventDefault();
    activate();
  }, { passive: false });
  return control;
}

export function mount({ main, mount }: PortalHost): void {
  const storageKey = `jade-court:lantern-run:best:${mount}`;
  let best = readBest(storageKey);
  let run = new LanternCourse();
  let phase: Phase = "ready";
  let frame = 0;
  let previous: number | undefined;
  let swipe: { id: number; x: number; y: number; used: boolean } | undefined;

  const root = el("section", "lantern-run");
  const title = el("h1", "", "Lantern Run");
  title.id = "run-title";
  root.setAttribute("aria-labelledby", title.id);
  const scorer = el("dl", "run-scores");
  scorer.setAttribute("aria-label", "Run scores");
  const stat = (label: string, id: string) => {
    const value = el("dd");
    value.dataset.stat = id;
    scorer.append(el("div", "", el("dt", "", label), value));
    return value;
  };
  const distance = stat("Distance", "distance");
  const lanterns = stat("Lanterns", "lanterns");
  const score = stat("Score", "score");
  const localBest = stat("Local best", "best");
  localBest.title = "Best finished run on this browser, for this portal address";
  const heading = el("header", "run-heading",
    el("div", "", el("p", "eyebrow", "Jade Court Arcade · Solo"), title), scorer);

  const canvas = el("canvas", "run-canvas");
  canvas.setAttribute("aria-hidden", "true");
  const scene = new LanternScene(canvas);
  const next = el("span", "run-next-text");
  next.id = "run-next";
  next.setAttribute("role", "status");
  const approach = el("span", "run-approach");
  approach.setAttribute("aria-hidden", "true");
  const pause = runButton("Pause", "button run-pause", () => pauseRun());
  pause.setAttribute("aria-keyshortcuts", "P Escape");
  const timing = el("span", "run-next-label", "Next");
  const waypoint = el("div", "run-waypoint", timing, next, approach);
  const strip = el("div", "run-strip",
    waypoint, pause);

  const panelKicker = el("p", "eyebrow");
  const panelTitle = el("h2");
  panelTitle.id = "run-panel-title";
  const panelCopy = el("p", "run-panel-copy");
  panelCopy.id = "run-panel-copy";
  const play = runButton("Start run", "button primary run-play", () => {
    if (phase === "paused") begin(false);
    else if (phase === "ready" || phase === "over") begin(true);
  });
  const panel = el("div", "run-panel", panelKicker, panelTitle, panelCopy, play);
  panel.setAttribute("role", "group");
  panel.setAttribute("aria-labelledby", panelTitle.id);
  play.setAttribute("aria-describedby", panelCopy.id);
  const overlay = el("div", "run-overlay", panel);
  const stage = el("div", "run-stage", canvas, strip, overlay);
  stage.tabIndex = -1;
  stage.setAttribute("role", "region");
  stage.setAttribute("aria-label", "Lantern Run course");
  stage.setAttribute("aria-describedby", "run-input-help");

  const controls = el("div", "run-controls");
  controls.setAttribute("role", "group");
  controls.setAttribute("aria-label", "Run controls");
  const actions = moves.map(([move, icon, label, shortcuts]) => {
    const control = runButton("", "run-control", () => act(move));
    control.setAttribute("aria-label", label);
    control.setAttribute("aria-keyshortcuts", shortcuts);
    const arrow = el("span", "run-control-icon", icon);
    arrow.setAttribute("aria-hidden", "true");
    control.append(arrow, el("span", "run-control-label", label));
    controls.append(control);
    return control;
  });
  const help = el("summary", "", "Course notes");
  const inputHelp = el("p", "", "Use arrows or WASD, swipe on the course, or use the four buttons. P or Esc pauses.");
  const notes = el("details", "run-notes", help, el("div", "run-guide",
    el("p", "run-guide-rail", "↑ Gold rail · Jump over it"),
    el("p", "run-guide-gate", "↓ Jade gate · Slide underneath"),
    el("p", "run-guide-kiln", "× Terracotta kiln · Switch lanes"),
    inputHelp,
    el("p", "", "Score: one point per meter, plus 25 per lantern. Best runs are saved on this browser only, if storage is available.")));
  const hint = el("p", "run-input-help", "Arrow keys / WASD · Swipe or tap");
  hint.id = "run-input-help";
  const less = el("input");
  less.type = "checkbox";
  const motion = el("label", "run-motion", less, "Reduce effects");
  root.append(heading, el("div", "run-deck", stage, controls),
    el("div", "run-footer", notes, hint, motion));
  main.append(root);

  const motionQuery = matchMedia("(prefers-reduced-motion: reduce)");
  const reducedByPreference = () => motionQuery.matches || document.documentElement.dataset.reducedMotion === "true";
  let reducedByChoice = false;
  const paint = () => scene.paint(run, reducedByChoice || reducedByPreference());

  function update(): void {
    text(distance, `${Math.floor(run.meters)} m`);
    text(lanterns, String(run.lanterns));
    text(score, String(run.score));
    text(localBest, String(best));
    if (phase !== "running") return;
    const row = run.next;
    const hazard = row.hazards[run.lane];
    let message: string;
    if (hazard === "rail") message = "Jump the gold rail";
    else if (hazard === "gate") message = "Slide under the jade gate";
    else if (hazard === "kiln") message = row.safe < run.lane ? "Kiln ahead · Move left" : "Kiln ahead · Move right";
    else if (row.lantern === run.lane) message = "Collect the lantern";
    else message = "Clear lane";
    text(next, message);
    const gap = row.at - run.meters;
    const imminent = hazard && gap <= pace(run.meters) * .33;
    text(timing, imminent ? "Now" : "Next");
    waypoint.classList.toggle("run-imminent", !!imminent);
    text(approach, `${Math.ceil(gap)} m`);
  }

  function show(nextPhase: Phase, kicker: string, caption: string, copy: string, action: string): void {
    phase = nextPhase;
    root.dataset.state = phase;
    const playing = phase === "running";
    overlay.hidden = playing;
    strip.hidden = !playing;
    actions.forEach(control => { control.disabled = !playing; });
    text(panelKicker, kicker);
    text(panelTitle, caption);
    text(panelCopy, copy);
    text(play, action);
    update();
  }

  function tick(now: number): void {
    if (phase !== "running") return;
    if (previous !== undefined) run.advance((now - previous) / 1000);
    previous = now;
    paint();
    if (run.crash) {
      const brokeRecord = run.score > best;
      if (brokeRecord) {
        best = run.score;
        try { localStorage.setItem(storageKey, String(best)); } catch { /* Keep the in-page best. */ }
      }
      const object = { kiln: "Kiln", rail: "Gold rail", gate: "Jade gate" }[run.crash];
      show("over", brokeRecord ? "New local best" : "Courier’s tally", "End of the run",
        `${object} · ${run.score} points. ${courseTips[run.crash]}`, "Run again");
      play.focus({ preventScroll: true });
      return;
    }
    update();
    frame = requestAnimationFrame(tick);
  }

  function begin(fresh: boolean): void {
    if (document.hidden) return;
    if (fresh) run = new LanternCourse();
    previous = undefined;
    swipe = undefined;
    show("running", "", "", "", "");
    paint();
    stage.focus({ preventScroll: true });
    frame = requestAnimationFrame(tick);
  }

  function pauseRun(): void {
    if (phase !== "running") return;
    cancelAnimationFrame(frame);
    previous = undefined;
    swipe = undefined;
    show("paused", "Night dispatch", "Run paused", "Take a breath. The course will wait here.", "Resume run");
    if (!document.hidden) play.focus({ preventScroll: true });
  }

  function act(move: Move): void {
    if (phase !== "running") return;
    run.move(move);
    update();
  }

  document.addEventListener("keydown", event => {
    if (event.defaultPrevented || event.altKey || event.ctrlKey || event.metaKey ||
        (event.target instanceof Element && event.target.closest("input, textarea, select, [contenteditable]"))) return;
    const key = event.key.toLowerCase();
    if ((key === "p" || key === "escape") && (phase === "running" || phase === "paused")) {
      event.preventDefault();
      if (!event.repeat) {
        if (phase === "running") pauseRun();
        else begin(false);
      }
    }
    const move = keys.get(key);
    if (phase !== "running" || !move) return;
    event.preventDefault();
    if (!event.repeat) act(move);
  });
  document.addEventListener("visibilitychange", () => {
    if (document.hidden) pauseRun();
    else if (phase === "paused" && document.activeElement === stage) play.focus({ preventScroll: true });
  });
  window.addEventListener("pagehide", pauseRun);

  canvas.addEventListener("pointerdown", event => {
    if (phase !== "running" || !event.isPrimary || event.pointerType === "mouse") return;
    swipe = { id: event.pointerId, x: event.clientX, y: event.clientY, used: false };
    canvas.setPointerCapture(event.pointerId);
  });
  function moveSwipe(event: PointerEvent): void {
    if (!swipe || swipe.id !== event.pointerId || swipe.used) return;
    const dx = event.clientX - swipe.x;
    const dy = event.clientY - swipe.y;
    if (Math.max(Math.abs(dx), Math.abs(dy)) < 22) return;
    swipe.used = true;
    act(Math.abs(dx) > Math.abs(dy) ? dx < 0 ? "left" : "right" : dy < 0 ? "jump" : "slide");
  }
  canvas.addEventListener("pointermove", moveSwipe);
  canvas.addEventListener("pointerup", event => { moveSwipe(event); if (swipe?.id === event.pointerId) swipe = undefined; });
  canvas.addEventListener("lostpointercapture", event => { if (swipe?.id === event.pointerId) swipe = undefined; });
  canvas.addEventListener("pointercancel", event => { if (swipe?.id === event.pointerId) swipe = undefined; });

  const motionChanged = () => {
    const forced = reducedByPreference();
    less.disabled = forced;
    less.checked = forced || reducedByChoice;
    motion.title = forced ? "Reduced motion is on in your system or portal preferences" : "Reduce decorative movement";
    paint();
  };
  motionQuery.addEventListener("change", motionChanged);
  document.addEventListener("court-motion-preference", motionChanged);
  less.addEventListener("change", () => { reducedByChoice = less.checked; paint(); });
  const resize = () => { scene.resize(); paint(); };
  new ResizeObserver(resize).observe(canvas);
  show("ready", "Night dispatch · 01", "Carry the light",
    "Dodge kilns, jump gold rails, and slide under jade gates. Gather lanterns along the way.", "Start run");
  resize();
  motionChanged();
}
