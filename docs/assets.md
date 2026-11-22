# Jade Court asset provenance

| What ships | Authorship and location |
|---|---|
| Jade Court name, emblem, favicon, controls | Original for this project. The header and browser tab use the same [ivory tile with a jade one-dot medallion](../web/src/jade-court.svg). Controls are drawn in [`web/src/dom.ts`](../web/src/dom.ts) and [`web/src/main.ts`](../web/src/main.ts). |
| Suited tile marks | [FluffyStuff/riichi-mahjong-tiles](https://github.com/FluffyStuff/riichi-mahjong-tiles/tree/26e127ba2117f45cdce5ea0225748cc0cfad3169/Regular) at `26e127ba2117f45cdce5ea0225748cc0cfad3169`, only `Regular/Man1.svg`–`Man9.svg`, `Regular/Sou1.svg`–`Sou9.svg`, and `Regular/Pin1.svg`–`Pin9.svg`. Both the pinned [README](https://github.com/FluffyStuff/riichi-mahjong-tiles/blob/26e127ba2117f45cdce5ea0225748cc0cfad3169/README.md#license) and [LICENSE](https://github.com/FluffyStuff/riichi-mahjong-tiles/blob/26e127ba2117f45cdce5ea0225748cc0cfad3169/LICENSE.md) declare the assets public domain under [CC0 1.0](https://creativecommons.org/publicdomain/zero/1.0/). |
| Tile bodies, backs, table, wall, lighting and type | Original CSS surfaces in [`web/src/styles.css`](../web/src/styles.css) and layout in [`web/src/table.ts`](../web/src/table.ts). The tilted CSS 2.5D projection sets light, raised tops against shaded parallelogram sidewalls. Ivory and jade bands expose the slab layers; dark feet and contact shadows ground them on the felt. The browser uses its installed system and serif fonts; no font files are bundled. |
| Lantern Hall lamps, screen, felt inlays and circular dial | Original [`hall.css`](../web/src/hall.css) surfaces and [lamp](../web/src/hall-lantern.svg), [room screen](../web/src/hall-room.svg), [corner inlay](../web/src/hall-inlay.svg) and [dragon engraving](../web/src/hall-dragon.svg). The dragon’s offset light edges and dark cuts are original vector artwork, not traced or copied from the reference. Its head and coil flank the separate [dial](../web/src/dial.ts), which groups the wind, timer and wall count with pips for server-projected dice. |
| Articulated seat characters | Original inline SVG art and pose code for Maple (Fox), Pip (Rabbit), Tallow (Tanuki) and Nori (Dragon) in [`web/src/actors.ts`](../web/src/actors.ts), with original styling in [`web/src/actors.css`](../web/src/actors.css). The body, head, upper arms, forearms and wrists move on committed live discards, calls, wins and losses. |
| Visual effects | Original motion and inline SVG stamp glyphs in [`web/src/effects.ts`](../web/src/effects.ts), with trails, rings and bursts styled in [`web/src/effects.css`](../web/src/effects.css) and placed by [`web/src/table.ts`](../web/src/table.ts). The [win scene](../web/src/finale.ts) uses original lantern and votive art. Deal packets use the original tile backs; draw and discard copies reuse the same CC0 marks as the real tiles. No animation library, sprite images or video assets. |
| Mahjong sound effects | Original, procedurally synthesized Web Audio cues in [`web/src/audio.ts`](../web/src/audio.ts). Tile and turn effects use the guest's saved settings. |
| Still Water, the Jade Court soundtrack | An original 32-second score with felt-key and bell-like colors, a soft pad, and a short space echo. The [composition and synthesizer](../web/src/portal/ambient.js) are shared by the portal and its Mahjong child, loaded only when music can play. No samples, recordings, third-party music or audio downloads. Music is off by default, uses the guest’s volume, waits for a browser gesture, and rests in hidden tabs. In Mahjong Settings, changes to music and its volume can be heard before saving; closing discards unsaved previews. |
| Portal lobby and playing surfaces | The [Go table study](../web/src/portal/court-study.svg), [catalog marks](../web/src/portal/art.ts), [board lattices](../web/src/portal/board.ts), [piece motion](../web/src/portal/motion.ts) and [matte materials](../web/src/portal/styles.css) were created for Jade Court. Mahjong and the portal use separate entry bundles and share the original favicon. |
| Portal chess pieces | [Colin M. L. Burnett’s cburnett set](https://github.com/lichess-org/lila/tree/b071608bc4ce71b31cda36ffa493100e3dc3580f/public/piece/cburnett), twelve original drawings from the pinned `lichess-org/lila` tree. Its [COPYING.md](https://github.com/lichess-org/lila/blob/b071608bc4ce71b31cda36ffa493100e3dc3580f/COPYING.md) expressly licenses this directory GPLv2+; this distribution uses [GPLv3](../LICENSE). See the [shipped notice](../scripts/licenses/cburnett-NOTICE.txt) for the original blob IDs, license evidence and changes. |
| Hold’em and Solitaire cards | [Native rank/suit indices and traditional pips](../web/src/portal/games/cards.ts), [crosshatched backs](../web/src/portal/games/cards.css), and twelve editable [SVG-cards court illustrations](../web/src/portal/assets/bellot) by David Bellot and contributors, distributed under [LGPL 2.1](../scripts/licenses/Bellot-Courts-LGPL-2.1.txt). No card fonts are bundled. |

The original project assets and code are under [GPL-3.0](../LICENSE); the adapted tile marks are CC0. We kept each suit’s painted geometry, including the calligraphed 萬, the bamboo bird and the coin rosettes. We dropped editor metadata and unused definitions, rounded vector coordinates at tile scale, adjusted the inks for ivory tile bodies, and shared repeated paths as internal fragments. There are no honor, bonus or red-five tiles, upstream code, external SVG references, scripts or fonts.

[`web/src/tile-suits.svg`](../web/src/tile-suits.svg) contains all 27 suited faces: `Man` maps to characters, `Sou` to bamboo, and `Pin` to dots. [`web/src/tile.ts`](../web/src/tile.ts) references each symbol from the same app origin. Vite emits one fingerprinted sheet, first requested when the table renders a marked face. The lazy `actors` JS and CSS carry the cast, Hall styling, dial and win scene, and load from that origin on entering the table or its waiting room. The smaller Hall SVGs are bundled into the CSS and the characters are inline. The fingerprinted dragon SVG is requested by that stylesheet only when the playing felt is present. It is static, follows the existing table projection, and sits below the dial and discards even with reduced motion. The landing illustration uses original CSS tile backs and an inline one-dot seal and does not request any of these table assets. Settings loads its optional Fullscreen helper separately. The app can serve its artwork without internet access; there are no reference clips, remote asset services or bundled 3D models. The [renderer fixtures](../web/e2e/fixtures/) and Playwright captures are not included in the published app.

## Portal material studies

The portal opens with a cropped Go board: ink lines on maple, ivory and dark stones on intersections, and a visible wooden edge. The directory uses small game-specific marks rather than repeating full decorative scenes. The actual boards use those same matte wood, baize and oxide colors; selected squares and motion still use the existing control layer.

Chess setup, the board and moving pieces share the same [editable sprite](../web/src/portal/assets/cburnett.svg). The twelve upstream files were matched to their pinned Git blob IDs before import; only their `g`, `path` and `circle` geometry and presentation attributes were copied into symbols. No scripts, styles, external references or upstream branding are included. Vite copies the sheet verbatim to a fingerprinted same-origin URL. The lobby's index mark is a checker pattern, so the sheet is first requested on Chess rather than on the lobby. The notice and root GPL text are included in server and container publishes via [`Directory.Build.targets`](../Directory.Build.targets).

From the pinned [SVG-cards source](https://github.com/htdebeer/SVG-cards/blob/6d88bf9c594997b82bc5668ab0877a4e9b717665/svg-cards-indented.svg), only the Jack, Queen and King paintings for each suit ship. The editable SVGs keep the two mirrored placements and original inks; the publisher copies them verbatim to separate fingerprinted same-origin URLs. Each costs roughly 13–19 KiB gzip and is requested only when its face is rendered. The lobby, card backs, numbers and Aces never request portraits. Rank and suit indices remain native, with a typographic fallback if a portrait is unavailable. See the [shipped notices](../scripts/licenses/Bellot-Courts-NOTICE.txt) for source hashes, exact license and authors files, and how to replace the artwork.

[Lichess analysis](https://lichess.org/analysis), [CardGames.io Backgammon](https://cardgames.io/backgammon/) and [Board Game Arena Farkle](https://boardgamearena.com/gamepanel?game=farkle) informed the focus on readable pieces and controls; no layouts or assets were copied. When the Backgammon and Farkle routes are listed, their existing `tableau()` marks will show a split maple and walnut board with a visible bar, and ivory dice with black pips on felt, respectively. The same palette works for their playing surfaces without relying on decorative shadows or color alone to convey play.

## Static asset budget

Use Vite's production `web/dist` and Node 26.13.1 `gzipSync` on each file, then sum the results. Count `index.html`, its entry JS and CSS, and the favicon against the **32 KiB** cold landing budget. Add the lazy `actors` JS and CSS, the dragon engraving and the same-origin `tile-suits` SVG for full play; count the Settings Fullscreen chunk separately. This estimates compressed static assets, not HTTP transfer size or server compression.

The two HTML entries share only the small emblem asset at startup; there is no shared preload or board-game bundle in the Mahjong shell. The published `/portal/` lobby is **about 13.4 KiB gzip** against a **24 KiB** budget, down from **16,472 bytes** on the same baseline. This includes the CSS-loaded Go study (**589 gzip bytes**). Chess requests a separate **1,848 gzip-byte** sprite; the lobby does not fetch it. `/portal/mahjong/` is unchanged at **32,704 bytes** against **32 KiB**. The optional shared soundtrack module adds **1,825 gzip bytes** only when playing music.

The pre-portal Jade Court baseline was **236 bytes under** the 32,768-byte cold budget.

| Static assets | Raw bytes | Gzip bytes |
|---|---:|---:|
| Cold landing: HTML, entry CSS and JS, and shared emblem/favicon | 99,009 | 32,532 |
| Deferred `actors` JS and CSS, including Hall and dial | 41,943 | 13,923 |
| Deferred dragon engraving | 6,235 | 2,068 |
| Deferred CC0 suited-face sheet | 84,694 | 11,795 |
| Full play without the Settings Fullscreen chunk | 231,881 | 60,318 |
| Optional Settings Fullscreen JS | 1,217 | 636 |
| All static assets | 233,098 | 60,954 |

The earlier frozen integrated Lantern Hall package (2026-11-21; `artifacts/lantern-hall-final/budget.json`) was **155 bytes under** the 32,768-byte cold budget.

| Static assets | Raw bytes | Gzip bytes |
|---|---:|---:|
| Cold landing: HTML, entry CSS and JS, and favicon | 98,943 | 32,613 |
| Deferred `actors` JS and CSS, including Hall and dial | 41,219 | 13,806 |
| Deferred CC0 suited-face sheet | 84,694 | 11,795 |
| Full play without the Settings Fullscreen chunk | 224,856 | 58,214 |
| Optional Settings Fullscreen JS | 1,217 | 638 |
| All static assets | 226,073 | 58,852 |

The first combined depth and seat-actor build (2026-11-21) is **historical**; it was **86 bytes under** the 32,768-byte cold budget.

| Historical static assets | Raw bytes | Gzip bytes |
|---|---:|---:|
| Cold landing: HTML, entry CSS and JS, and favicon | 98,735 | 32,682 |
| Deferred `actors` JS and CSS | 15,192 | 5,610 |
| Deferred CC0 suited-face sheet | 84,694 | 11,795 |
| Full table including landing assets | 198,621 | 50,087 |

The following inventory is **historical**: it was measured for the combined desktop, tile-art and motion pass (2026-11-20), before depth and actors.

| Historical static assets | Raw bytes | Gzip bytes |
|---|---:|---:|
| Cold landing: HTML, CSS, JS module and original favicon | 92,438 | 30,634 |
| Deferred table-only CC0 suited-face sheet | 84,694 | 11,795 |
| Full table including landing assets | 177,132 | 42,429 |

Earlier measurements used the same per-file estimate: the landscape publish was 82,026 bytes raw / 27,286 bytes gzip for the full web output before the independent art sheet. The desktop and tile-art pass, before motion, was 81,903 / 26,910 for its shell and 166,597 / 38,705 with the sheet.

Pinned development tools retain their own licenses: Vite 7.3.8 (MIT), TypeScript 5.9.3 (Apache-2.0) and Playwright 1.65.1 (Apache-2.0). The [`vite.config.ts`](../web/vite.config.ts) build emits a modern entry module and deferred `actors` and Fullscreen modules, without bundling Vite's legacy module-preload polyfill or these tools.

[Reference observations](references.md) establish broad presentation cues, not asset rights or fidelity: the four tilted edge lanes and bottom hand are visible in B1 (00:05.100–00:25.003), separated groups and action controls in B1 (01:16.738–02:47.823), and an offset last tile in Y1 (00:02.010–00:12.012). Jade Court uses its own palette, interface, audio and accessible input geometry, with tile marks from the independent CC0 source above. The WeChat still is observation evidence only; no image from it is copied into the app. The sample clips do not establish this Changsha rules profile, end-to-end rendering parity, or audio equivalence.
