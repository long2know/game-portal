import { expect } from "@playwright/test";
import type { Page, Response } from "@playwright/test";
import type { Table } from "../src/portal/api";

export const urls = (baseURL: string) => (path = "") => new URL(path, new URL("/portal/", baseURL)).href;
export const projected = (response: Response) => response.json() as Promise<Table>;
export const state = (page: Page) => page.locator(".turn-text");
export const tap = (page: Page, name: string | RegExp) => page.getByRole("button", { name, exact: true }).tap();

export function receive(page: Page, url: string, method: string) {
  return page.waitForResponse(response => response.url() === url && response.request().method() === method);
}

export async function fitsPhone(page: Page) {
  expect(await page.evaluate(() => document.documentElement.scrollWidth)).toBeLessThanOrEqual(page.viewportSize()!.width);
}

export type Point = [number, number];
type MotionTrace = {
  effect: string; side: string; piece: string; start: Point; path: Point[]; delay: number;
  flipTilt?: number[];
  started?: boolean; landing?: { center: Point; opacity: number };
  sample?: { center: Point; height: number; opacity: number }; finished?: boolean; cancelled?: boolean;
};
export type MotionPage = Window & { motion: MotionTrace[]; holdMotion: boolean };

export async function observeMotion(page: Page, sampleAt: "in-flight" | "landing" = "in-flight") {
  await page.addInitScript(sampleAt => {
    const probe = window as unknown as MotionPage;
    probe.motion = [];
    const animate = Element.prototype.animate;
    Element.prototype.animate = function (frames, options) {
      const layer = this.closest<HTMLElement>(".motion-layer");
      if (!layer) return animate.call(this, frames, options);
      const origin = layer.getBoundingClientRect();
      const center = ({ x, y, width, height }: DOMRect): Point =>
        [x + width / 2 - origin.x, y + height / 2 - origin.y];
      const start = center(this.getBoundingClientRect());
      const art = this.querySelector<HTMLElement>(".token")!;
      const animation = animate.call(this, frames, options);
      const effect = (animation.effect as KeyframeEffect);
      const transforms = effect.getKeyframes().map(frame => new DOMMatrix(frame.transform as string | undefined));
      const entry: MotionTrace = {
        effect: [...this.classList].find(name => name !== "motion-piece")!.slice("motion-".length),
        side: art.dataset.side!, piece: art.dataset.piece!, start,
        path: transforms.map(transform => [start[0] + transform.m41, start[1] + transform.m42]),
        delay: effect.getTiming().delay!,
      };
      if (entry.effect === "flip") entry.flipTilt = transforms.map(transform => transform.m22);
      probe.motion.push(entry);
      const sample = () => {
        if (!this.isConnected || animation.playState === "idle" || animation.playState === "finished") return;
        const progress = effect.getComputedTiming().progress ?? 0;
        if (progress < .16 || progress > .85) {
          requestAnimationFrame(sample);
          return;
        }
        const rect = this.getBoundingClientRect();
        entry.sample = { center: center(rect), height: rect.height, opacity: Number(getComputedStyle(this).opacity) };
        if (probe.holdMotion) animation.pause();
      };
      if (sampleAt === "landing") {
        void animation.ready.then(() => {
          entry.started = animation.startTime !== null && Number(effect.getComputedTiming().activeDuration) > 0;
        }, () => {});
      } else requestAnimationFrame(sample);
      void animation.finished.then(() => {
        entry.finished = true;
        // The game's cleanup awaits the same promise. Read the attached, filled effect first.
        if (sampleAt === "landing" && this.isConnected) {
          entry.landing = { center: center(this.getBoundingClientRect()), opacity: Number(getComputedStyle(this).opacity) };
        }
      }, () => { entry.cancelled = true; });
      return animation;
    };
  }, sampleAt);
  return () => page.evaluate(() => (window as unknown as MotionPage).motion);
}
