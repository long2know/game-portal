import { viewDistance } from "./lantern-course";
import type { Hazard, LanternCourse } from "./lantern-course";

type Point = readonly [number, number];
type World = readonly [number, number, number];
const tau = Math.PI * 2;
const near = -5;
const stone = ["#826558", "#886c5b", "#8e705a", "#795e54", "#92765f"];
const walls = ["#9b6154", "#8f554d", "#a26854", "#8c5f55"];
const tiles = ["#526c65", "#577770", "#4a6966", "#618575"];
const ink = "#282a33";
const fleck = (n: number) => {
  const value = Math.sin(n * 12.9898 + 6.14) * 43758.5453;
  return value - Math.floor(value);
};

function path(ctx: CanvasRenderingContext2D, points: readonly Point[]): void {
  ctx.beginPath();
  ctx.moveTo(...points[0]);
  for (let i = 1; i < points.length; i++) ctx.lineTo(...points[i]);
}

function fill(ctx: CanvasRenderingContext2D, color: string | CanvasGradient, points: readonly Point[]): void {
  path(ctx, points);
  ctx.closePath();
  ctx.fillStyle = color;
  ctx.fill();
}

function line(ctx: CanvasRenderingContext2D, color: string, width: number, ...points: Point[]): void {
  path(ctx, points);
  ctx.strokeStyle = color;
  ctx.lineWidth = width;
  ctx.stroke();
}

function oval(ctx: CanvasRenderingContext2D, color: string | CanvasGradient, x: number, y: number, rx: number, ry = rx): void {
  ctx.beginPath();
  ctx.ellipse(x, y, Math.max(.1, rx), Math.max(.1, ry), 0, 0, tau);
  ctx.fillStyle = color;
  ctx.fill();
}

function lantern(ctx: CanvasRenderingContext2D, x: number, y: number, size: number, cord = true): void {
  oval(ctx, "#ffc8792f", x, y, size * 1.8, size * 2.05);
  fill(ctx, "#6d4539", [[x - size, y - size * .48], [x, y - size * 1.25], [x + size, y - size * .48],
    [x + size * .74, y + size * .67], [x, y + size * 1.14], [x - size * .74, y + size * .67]]);
  fill(ctx, "#fabb60", [[x - size * .77, y - size * .37], [x, y - size], [x + size * .77, y - size * .37],
    [x + size * .57, y + size * .57], [x, y + size * .89], [x - size * .57, y + size * .57]]);
  fill(ctx, "#fff0b1", [[x, y - size * .68], [x + size * .44, y], [x, y + size * .67], [x - size * .44, y]]);
  if (cord) line(ctx, "#e8aa6b", Math.max(.55, size * .15), [x, y + size], [x, y + size * 1.51]);
}

export class LanternScene {
  private readonly backdrop = document.createElement("canvas");
  private readonly ctx: CanvasRenderingContext2D;
  private width = 0;
  private height = 0;
  private ratio = 1;
  private unit = 1;
  private horizon = 0;
  private foot = 0;

  constructor(private readonly canvas: HTMLCanvasElement) {
    const ctx = canvas.getContext("2d", { alpha: false });
    if (!ctx) throw new Error("Canvas unavailable");
    this.ctx = ctx;
  }

  resize(): void {
    const { width, height } = this.canvas.getBoundingClientRect();
    if (!width || !height) return;
    const ratio = Math.min(devicePixelRatio || 1, 1.75, Math.sqrt(1_600_000 / (width * height)));
    if (width === this.width && height === this.height && ratio === this.ratio) return;
    this.width = width;
    this.height = height;
    this.unit = Math.min(width * .25, height * .36);
    this.horizon = height * .34;
    this.foot = height * .81;
    this.ratio = ratio;
    this.backdrop.width = this.canvas.width = Math.round(width * ratio);
    this.backdrop.height = this.canvas.height = Math.round(height * ratio);
    this.ctx.setTransform(ratio, 0, 0, ratio, 0, 0);
    this.paintBackdrop();
  }

  paint(run: LanternCourse, reduced: boolean): void {
    if (!this.width) return;
    const ctx = this.ctx;
    ctx.drawImage(this.backdrop, 0, 0, this.width, this.height);
    this.street(run.meters);
    this.quays(run.meters);
    for (let i = run.rows.length - 1; i >= 0; i--) {
      const row = run.rows[i];
      if ((row.at > run.meters || (row.at === run.meters && run.crash)) && row.at - run.meters <= viewDistance) {
        this.row(row, row.at - run.meters, reduced ? 0 : run.seconds);
      }
    }
    this.courier(run, reduced);
    for (let i = run.rows.length - 1; i >= 0; i--) {
      const row = run.rows[i];
      if ((row.at < run.meters || (row.at === run.meters && !run.crash)) && row.at >= run.meters + near) {
        this.row(row, row.at - run.meters, 0);
      }
    }
  }

  private paintBackdrop(): void {
    const ctx = this.backdrop.getContext("2d")!;
    const w = this.width;
    const h = this.height;
    const y = this.horizon;
    ctx.setTransform(this.ratio, 0, 0, this.ratio, 0, 0);
    const sky = ctx.createLinearGradient(0, 0, 0, y * 1.46);
    sky.addColorStop(0, "#172d3e");
    sky.addColorStop(.53, "#605167");
    sky.addColorStop(1, "#d98c6f");
    ctx.fillStyle = sky;
    ctx.fillRect(0, 0, w, h);

    const mx = w * .57;
    const my = h * .205;
    const radius = Math.min(w, h) * .052;
    const halo = ctx.createRadialGradient(mx, my, radius * .35, mx, my, radius * 3.15);
    halo.addColorStop(0, "#ffc09090");
    halo.addColorStop(.46, "#ffaf7652");
    halo.addColorStop(1, "#ffac7000");
    oval(ctx, halo, mx, my, radius * 3.15);
    oval(ctx, "#ffc78d", mx, my, radius);
    oval(ctx, "#f7a8784d", mx - radius * .26, my + radius * .22, radius * .46, radius * .16);

    for (let i = 0; i < 22; i++) {
      const x = w * fleck(i + 92);
      const yy = h * (.025 + .23 * fleck(i + 177));
      if (Math.hypot(x - mx, yy - my) > radius * 1.75) oval(ctx, "#eed4b998", x, yy, .45 + fleck(i) * .62);
    }

    for (let layer = 0; layer < 3; layer++) {
      const coast: Point[] = [[0, h]];
      for (let i = 0; i <= 19; i++) {
        const x = i / 19;
        const opening = .54 + .46 * Math.abs(x - .52) * 2;
        const ridge = (.38 * fleck(i + layer * 29) + .62 * Math.abs(Math.sin(x * 13 + layer * 2))) * opening;
        coast.push([w * x, y * (1.02 + layer * .17 - ridge * (.55 - layer * .1))]);
      }
      coast.push([w, h]);
      fill(ctx, ["#64566b", "#4b515f", "#344a52"][layer], coast);
    }

    const buildings = Math.max(16, Math.floor(w / 24));
    for (let i = 0; i <= buildings; i++) {
      const x = w * i / buildings;
      const s = Math.min(27, w / 18) * (.62 + fleck(i + 60) * .5);
      const base = y * (1.22 + fleck(i + 39) * .12);
      const top = base - s * (.7 + fleck(i + 122) * 1.8);
      fill(ctx, i % 2 ? "#37484b" : "#3c4f50", [[x - s * .65, base], [x - s * .65, top], [x + s * .65, top], [x + s * .65, base]]);
      fill(ctx, "#303e48", [[x - s, top + s * .12], [x - s * .58, top - s * .16], [x, top - s * .5],
        [x + s * .58, top - s * .16], [x + s, top + s * .12]]);
      if (i % 3 !== 1) {
        line(ctx, "#ae826a", .85, [x - s * .3, top + s * .36], [x - s * .1, top + s * .36]);
        line(ctx, "#d19c71", .85, [x + s * .17, top + s * .36], [x + s * .34, top + s * .36]);
      }
    }

    const water = ctx.createLinearGradient(0, y * 1.03, 0, h);
    water.addColorStop(0, "#536867");
    water.addColorStop(.44, "#355357");
    water.addColorStop(1, "#202e3a");
    ctx.fillStyle = water;
    ctx.fillRect(0, y * 1.27, w, h);
    for (let i = 0; i < 85; i++) {
      const depth = fleck(i + 263);
      const yy = h * (.436 + depth * .56);
      const x = w * fleck(i + 21);
      const length = this.unit * (.04 + depth * .5) * (.25 + fleck(i + 14));
      line(ctx, i % 6 === 0 ? "#c98e7350" : "#82aaa342", .45 + depth * .75, [x, yy], [x + length, yy]);
    }
  }

  private scale(z: number): number { return 12 / (12 + z); }

  private point([x, y, z]: World): Point {
    const s = this.scale(z);
    return [this.width * .5 + x * this.unit * s, this.horizon + (this.foot - this.horizon - y * this.unit * .72) * s];
  }

  private polygon(color: string, ...points: World[]): void {
    fill(this.ctx, color, points.map(point => this.point(point)));
  }

  private edge(color: string, width: number, ...points: World[]): void {
    line(this.ctx, color, width, ...points.map(point => this.point(point)));
  }

  private face(color: string, x: number, y: number, w: number, h: number, z: number): void {
    this.polygon(color, [x, y, z], [x + w, y, z], [x + w, y + h, z], [x, y + h, z]);
  }

  private floor(color: string, x: number, z: number, w: number, depth: number): void {
    this.polygon(color, [x, 0, z], [x + w, 0, z], [x + w, 0, z + depth], [x, 0, z + depth]);
  }

  private street(distance: number): void {
    this.floor("#354047", -1.74, near, 3.48, viewDistance - near);
    this.floor("#554d4b", -1.64, near, 3.28, viewDistance - near);
    const length = 4.6;
    for (let index = Math.floor((distance + viewDistance) / length); index >= Math.floor((distance + near) / length); index--) {
      const nearTile = Math.max(near, index * length - distance);
      const farTile = (index + 1) * length - distance;
      const depth = farTile - nearTile;
      for (let lane = 0; lane < 3; lane++) {
        const color = stone[Math.floor(fleck(index * 5 + lane + 40) * stone.length)];
        const x = lane - 1.5;
        this.floor(color, x + .013, nearTile, .974, depth);
        const cut = nearTile + depth * .49;
        const offset = (index + lane) % 2 ? .26 : .72;
        this.edge("#574d4c80", .6, [x, 0, cut], [x + 1, 0, cut]);
        this.edge("#5a4c465c", .6, [x + offset, 0, nearTile], [x + offset, 0, cut]);
        this.edge("#a9897160", .8, [x + .08, 0, nearTile], [x + .9, 0, nearTile]);
      }
      for (const side of [-1, 1]) {
        const trim = side === 1 ? 1.52 : -1.63;
        this.floor(index % 2 ? "#aa9675" : "#857963", trim, nearTile, .11, depth * .967);
        if (index % 2 === 0) {
          const z = nearTile + depth * .3;
          this.floor("#e6b47a", side === 1 ? 1.43 : -1.47, z, .035, depth * .52);
        }
      }
      if (nearTile > 17) {
        const fade = Math.round(Math.min(.52, (nearTile - 17) / 173) * 255).toString(16).padStart(2, "0");
        this.floor(`#6f7077${fade}`, -1.64, nearTile, 3.28, depth);
      }
    }
    for (const x of [-1, 0, 1]) {
      this.edge("#f9cf8a8f", 1.15, [x - .35, 0, 0], [x - .23, 0, 0]);
      this.edge("#f9cf8a8f", 1.15, [x + .23, 0, 0], [x + .35, 0, 0]);
    }
  }

  private quays(distance: number): void {
    const gap = 13.2;
    for (let i = Math.floor((distance + viewDistance) / gap); i >= Math.floor((distance + near - 8) / gap); i--) {
      for (const side of [-1, 1]) {
        const z = i * gap - distance + (side === 1 ? 5 : 0);
        if (z > viewDistance || z + 7 < near) continue;
        const hash = i * 3 + side + 11;
        const x = side * (2.02 + fleck(hash) * .49);
        const end = x + side * (1.5 + fleck(hash + 7) * .9);
        const left = Math.min(x, end);
        const right = Math.max(x, end);
        const back = z + 6.9;
        const start = Math.max(near, z);
        const height = .92 + fleck(hash + 4) * 1.19;
        const pigment = Math.floor(fleck(hash + 66) * walls.length);
        const bank = side === 1 ? 1.92 : -3.9;
        this.floor(i % 2 ? "#54635b" : "#606658", bank, start, 1.98, back - start);
        this.floor("#bda27c", side === 1 ? 1.91 : -1.96, start, .055, back - start);
        if (i % 5 !== 2) {
          this.polygon(side === 1 ? "#774f4b" : walls[pigment], [x, 0, start], [x, height, start],
            [x, height, back], [x, 0, back]);
          const verge = x - side * .2;
          this.polygon(tiles[pigment], [verge, height - .08, start], [x, height + .42, start],
            [x, height + .42, back], [verge, height - .08, back]);
          this.edge("#85a991", Math.max(.55, this.scale(start) * 1.25), [verge, height - .08, start], [verge, height - .08, back]);
          for (let bay = 1; bay < 4; bay++) {
            const beam = z + bay * 1.66;
            if (beam < near) continue;
            this.edge("#623f3c", Math.max(.65, this.scale(beam) * 2.05), [x, .07, beam], [x, height, beam]);
            this.polygon("#c892686b", [x, height * .34, beam], [x, height * .68, beam],
              [x, height * .68, beam + .9], [x, height * .34, beam + .9]);
          }
          if (z >= near) {
            this.face(walls[pigment], left, 0, right - left, height, z);
            this.face("#3e373c", left + .25, 0, .27, height * .71, z);
            this.face("#e1aa72", left + .65, height * .36, .5, height * .37, z);
            for (const bar of [.77, .9, 1.03]) this.face("#6c4e42", left + bar, height * .36, .034, height * .37, z);
            this.polygon("#344e50", [left - .23, height - .08, z], [left + .12, height + .22, z],
              [(left + right) / 2, height + .54, z], [right - .03, height + .12, z], [right + .23, height - .08, z]);
            this.edge("#a2b397", Math.max(.6, this.scale(z) * 1.3), [left - .23, height - .08, z], [right + .23, height - .08, z]);
          }
        }
        if (z + 1.2 >= near) {
          const hook = z + 1.2;
          const post = side * 1.97;
          const lamp = post - side * .27;
          this.edge("#453a3b", Math.max(1, this.scale(hook) * 3.1), [post, 0, hook], [post, 1.63, hook], [lamp, 1.63, hook]);
          this.edge("#cf9b72", Math.max(.5, this.scale(hook)), [lamp, 1.64, hook], [lamp, 1.48, hook]);
          lantern(this.ctx, ...this.point([lamp, 1.36, hook]), Math.max(1.4, this.unit * this.scale(hook) * .073));
        }
      }
    }
  }

  private row(row: LanternCourse["next"], z: number, seconds: number): void {
    row.hazards.forEach((hazard, lane) => {
      if (hazard) this.hazard(hazard, lane - 1, z);
    });
    if (row.lantern !== undefined) {
      const hazard = row.hazards[row.lantern];
      const y = hazard === "gate" ? .4 : hazard === "rail" ? 1.04 : .78;
      const bob = Math.sin(seconds * 5 + z * .06) * (seconds ? .048 : 0);
      lantern(this.ctx, ...this.point([row.lantern - 1, y + bob, z]), this.unit * this.scale(z) * .111);
    }
  }

  private hazard(hazard: Hazard, x: number, z: number): void {
    const s = this.scale(z);
    const weight = this.unit * s;
    const left = x - .39;
    const right = x + .39;
    const base = this.point([x, 0, z]);
    oval(this.ctx, "#291f276c", ...base, weight * .49, weight * .069);
    if (hazard === "kiln") {
      this.polygon("#bb7356", [left, 1.19, z], [right, 1.19, z], [right, 1.19, z + 1.15], [left, 1.19, z + 1.15]);
      const side = x < 0 ? right : left;
      this.polygon("#633c41", [side, 0, z], [side, 1.19, z], [side, 1.19, z + 1.15], [side, 0, z + 1.15]);
      this.face("#662f33", left, 0, .78, 1.19, z);
      this.face("#bc644f", left + .032, .07, .716, 1.052, z);
      this.face("#823f3e", left + .084, .18, .612, .8, z);
      this.edge("#fd9c70", Math.max(.65, weight * .023), [left + .035, .07, z], [left + .035, 1.12, z],
        [right - .035, 1.12, z], [right - .035, .07, z]);
      this.edge("#ffcf9a", Math.max(.75, weight * .036), [x - .14, .47, z], [x + .14, .81, z]);
      this.edge("#ffcf9a", Math.max(.75, weight * .036), [x - .14, .81, z], [x + .14, .47, z]);
      this.face("#3c3035", left + .14, .22, .5, .096, z);
    } else if (hazard === "rail") {
      for (const post of [left + .037, right - .135]) {
        this.face("#694735", post, 0, .1, .57, z);
        this.face("#d99451", post, .02, .021, .52, z);
      }
      this.polygon("#ffd28a", [left, .58, z], [right, .58, z], [right, .58, z + .6], [left, .58, z + .6]);
      this.face("#a76a35", left, .29, .78, .29, z);
      this.face("#edb971", left + .025, .34, .73, .204, z);
      for (const at of [x - .19, x, x + .19]) {
        this.edge("#75442f", Math.max(.68, weight * .022), [at - .055, .41, z], [at, .49, z], [at + .055, .41, z]);
      }
    } else {
      for (const post of [left, right - .095]) {
        this.face("#1f5855", post, 0, .095, 1.51, z);
        this.face("#84bfa7", post + .015, .038, .024, 1.42, z);
        this.face("#bb945e", post - .017, .029, .126, .13, z);
      }
      this.polygon("#9bc3a4", [left - .066, 1.55, z], [right + .066, 1.55, z],
        [right + .04, 1.55, z + .58], [left - .04, 1.55, z + .58]);
      this.face("#306f6e", left - .055, 1.33, .89, .222, z);
      this.polygon("#62ac97", [left + .088, 1.33, z], [right - .088, 1.33, z], [right - .088, .97, z],
        [x, .86, z], [left + .088, .97, z]);
      this.edge("#e0f2bd", Math.max(.68, weight * .028), [x - .117, 1.16, z], [x, 1.005, z], [x + .117, 1.16, z]);
    }
  }

  private courier(run: LanternCourse, reduced: boolean): void {
    const ctx = this.ctx;
    const z = run.crash ? -.9 : 0;
    const [x, sole] = this.point([run.x - 1, 0, z]);
    const u = this.unit * this.scale(z);
    const phase = run.seconds * 18;
    const stride = run.lift || run.sliding || !run.seconds ? 0 : Math.sin(phase);
    const bob = reduced ? 0 : Math.abs(stride) * 2.3;
    const slide = run.sliding;
    const jump = run.lift > .03;
    oval(ctx, "#24233081", x, sole + 2, u * (.27 - Math.min(run.lift, 1.4) * .063), u * .054);
    ctx.save();
    ctx.translate(x, sole - u * .72 * run.lift);
    ctx.scale(u / 86, u / 86);
    ctx.rotate(run.crash ? -.17 : (run.lane - run.x) * .12);
    ctx.lineCap = "round";
    ctx.lineJoin = "round";
    const hips = slide ? -18 : -27 - bob;
    const neck = slide ? -34 : -62 - bob;
    const torso = slide ? -7 : 0;
    const scarf = reduced ? 1 : Math.sin(phase * .59) * 3 + 2;
    const shins: [number, number, number][] = slide ? [[-17, -16, -2], [12, 23, -3]]
      : [[-8, -10 - stride * 6, jump ? -8 : Math.min(0, stride * 11)], [8, 10 + stride * 6, jump ? -14 : Math.min(0, -stride * 11)]];
    for (const [thigh, toe, rise] of shins) {
      line(ctx, "#242d3c", 10, [thigh * .74, hips - 3], [thigh, (hips + rise) / 2], [toe, rise - 3]);
      line(ctx, "#4f5c63", 2.6, [thigh + 1, (hips + rise) / 2], [toe + 1, rise - 3]);
      line(ctx, "#b18a68", 3.1, [toe - 3, rise - 1], [toe + 5, rise - 1]);
      line(ctx, ink, 4, [toe - 4, rise - 3], [toe + 4, rise - 3]);
    }
    line(ctx, "#275456", 9, [torso - 13, neck + 9], [-22 + stride * 2, neck + 19], [-19 + stride * 3, hips + 1]);
    line(ctx, "#bf916e", 4.5, [-20 + stride * 3, hips - 1], [-20 + stride * 3, hips + 3]);
    line(ctx, "#3d786a", 9, [torso + 13, neck + 9], [22 - stride * 3, neck + 20], [20 - stride * 2, hips - 1]);
    fill(ctx, "#234b50", [[torso - 15, neck + 4], [torso + 13, neck + 2], [17, hips - 5], [12, hips + 5],
      [-15, hips + 4], [-18, hips - 5]]);
    fill(ctx, "#46826c", [[torso - 12, neck + 5], [torso + 11, neck + 4], [12, hips - 8], [-13, hips - 8]]);
    line(ctx, "#8cb79a", 1.35, [torso - 10, neck + 9], [-10, hips - 10]);
    line(ctx, "#dbad72", 3.7, [-15, hips - 5], [13, hips - 5]);
    line(ctx, "#cda679", 3, [torso - 13, neck + 8], [12, hips - 4]);
    fill(ctx, "#583f3a", [[5, hips - 13], [17, hips - 14], [18, hips + 2], [9, hips + 3]]);
    lantern(ctx, 12.5, hips - 7.2, 4.2, false);
    fill(ctx, "#bc584e", [[torso - 4, neck + 3], [torso + 12, neck + 5], [torso + 16, neck + 12],
      [torso + 25 + scarf, neck + 17], [torso + 23 + scarf, neck + 22], [torso + 13, neck + 17], [torso + 6, neck + 10]]);
    line(ctx, "#e28d66", 1.5, [torso + 11, neck + 9], [torso + 19 + scarf, neck + 17]);
    oval(ctx, "#433437", torso, neck - 2, 10, 9);
    fill(ctx, "#533f38", [[torso - 26, neck - 5], [torso - 3, neck - 23], [torso + 21, neck - 10],
      [torso + 25, neck - 4], [torso + 10, neck + 2], [torso - 11, neck + 2]]);
    fill(ctx, "#e2ba7d", [[torso - 25, neck - 7], [torso - 12, neck - 15], [torso - 3, neck - 24],
      [torso + 14, neck - 13], [torso + 25, neck - 7], [torso + 9, neck - 3], [torso - 12, neck - 3]]);
    fill(ctx, "#b77b51", [[torso - 3, neck - 23], [torso + 13, neck - 13], [torso + 24, neck - 7],
      [torso + 8, neck - 4], [torso + 1, neck - 10]]);
    line(ctx, "#ffe1a0", 1.4, [torso - 24, neck - 7], [torso - 11, neck - 4], [torso + 8, neck - 4], [torso + 23, neck - 7]);
    line(ctx, "#fff0aa9c", 1, [torso - 14, neck - 10], [torso - 5, neck - 19]);
    ctx.restore();

    const flash = run.seconds - run.pickupAt;
    if (!reduced && flash < .42) {
      ctx.save();
      ctx.globalAlpha = 1 - flash / .42;
      for (let i = 0; i < 5; i++) {
        const angle = i * tau / 5;
        const px = x + Math.cos(angle) * (12 + flash * 78);
        const py = sole - u * .61 + Math.sin(angle) * (8 + flash * 56) - flash * 23;
        fill(ctx, "#ffe1a0", [[px, py - 4], [px + 2.4, py], [px, py + 4], [px - 2.4, py]]);
      }
      ctx.restore();
    }
  }
}
