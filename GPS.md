# GPS

Three phases, in this order: Rivers, then GPS, then the in-game overlay. Each phase uses what the one before it saved. Nothing here changes the wind formula. Pictures, water edges, and the route stay in `wind.sqlite` on this PC.

The route uses the same idea as Google Maps and other street routers. The classic write-ups are Dijkstra (1959) and A* (Hart, Nilsson, and Raphael, 1968). Google’s Routes API does not minimize distance. It minimizes travel time, and traffic changes the time on each road. Traffic-aware routing treats a slow road as a more expensive edge: https://developers.google.com/maps/documentation/routes/understand-route-response. This map is small enough that A* on the hexes is the whole algorithm. A hex is an intersection. A shared side is a road. Wind is the traffic.

Hex sides stay in the game’s order: east, northeast, northwest, west, southwest, southeast. That is the same order as `Axial.NeighborDeltas`.

## Phase 1 — Rivers

### Read the current tile

While Sync is on, each read of `https://asciilands.io/play/` also reads the minimap for the region you are standing in. The screen reader already knows this element as `minimap-svg`. The browser read will take that same element, or the canvas inside it if the game draws the picture there.

Any blue pixel on that tile counts as water. The first implementation pass records the real element and the blue the game uses, from one open play tab, and writes those details at the bottom of this file. Until that check, the app does not guess a river from the biome name.

The picture and a six-edge water mask are saved for that region. A later read of the same region replaces the picture. It does not create a second hex.

### Draw it on our map

The saved picture is painted on top of the wiki color for that hex, and underneath the wind arrows, tile icons, labels, the player, and the route lines. Icons and arrows stay visible. Hexes with no picture keep the wiki color.

### Connect water into rivers

A side of a hex is a water edge when blue reaches that side. Two neighboring hexes are linked when one continuous stretch of blue crosses the side they share: the same blue body on a tile meets the neighbor's blue along that edge. Separate ponds on one tile stay separate, so a route cannot cross from one pond to the other. Ocean hexes link to each other on every shared side, and a river links into the ocean where its blue meets that ocean. A hex whose chart shows a river or a shoreline gets the same calculated sea wind as the ocean. Dry land draws no arrow.

### Update map

An **Update map** button reads the full in-game map, the one opened with M, not the corner minimap. It takes every revealed tile: places you have visited, and places the game marks seen because you were nearby. Question-mark tiles stay unread until the game reveals them. Tiles already stored are not added again. When the in-game name matches a wiki region, the detailed picture replaces that wiki hex’s flat color. A name that matches nothing on the wiki is reported and skipped, so the app does not invent a new region.

When the play page cannot be scripted, Update map reads those same labels through the screen reader Sync already uses. A visited label and a “Seen, not visited” label both count. A name that belongs to one wiki region is that region. A name shared by many regions, such as Open Sea, is the region whose hex sits under that label. Tiles that are off the screen stay unread until a later press, after the map shows them. This path does not click, type, or log in.

### Notice

The opening notice gains two facts: Sync reads the minimap picture, and, while GPS is on, the app writes a small overlay into that minimap. The data still stays in `wind.sqlite` on this PC. Because that is a new promise, the next launch shows the notice once more. After that acceptance, it stays quiet.

## Phase 2 — GPS

A **GPS** button sits under the clock. It asks for **Boat** or **Walk**, then for one click on the destination hex. Escape cancels the click. The route is drawn until you clear it.

### Boat

The route may only sail on ocean hexes, including ocean reached through a river that touches the ocean.

- On the water: sail from the current hex to the destination.
- On land: walk to the nearest chart water that connects through to the ocean, then sail. Boarding happens where blue meets the sea. The walk uses the walk rules below.
- Destination on land: the sail ends at the ocean hex nearest that land hex. The status line says the rest is a walk you do yourself. The route does not draw that leftover walk.

Wind is traffic, and only on ocean sides. Each ocean crossing has a base time. A wind going the way you sail shortens that time. A wind against you lengthens it. A stronger arrow changes the time more. The route is the path with the smallest total time, not the fewest hexes. Land and river approaches ignore the wind.

### Walk

Walk uses land hexes and river hexes. Ocean hexes are closed. Wind is ignored. The route is the shortest walk.

### Already traveled

Sync already knows the hex you are in. When you enter the next hex of the route, every hex behind you is marked passed. Those hexes stay on the line, dimmer, and they are not part of the trip you still have to make. If you leave the line, the route is calculated again from the hex you are in to the same destination. That is the same split a street GPS draws between the driven part and the part still ahead.

### The line

Each hex on the route gets two strokes from its center, like clock hands. One goes to the middle of the side you entered. The other goes to the middle of the side you leave. Arrowheads point the way out. The first hex has only the exit hand. The last hex has only the entry hand. Passed hexes use the dimmer stroke.

## Phase 3 — In-game overlay

This runs only while a GPS route is active. When the play tab can be scripted, the app adds a few lines of HTML and CSS inside the minimap element. When the page can only be read from the screen, the same marks are drawn in a click-through window over that minimap. Either way the marks go away when GPS is cleared, Sync is turned off, or the app closes. If the game redraws the minimap, the layer is put back on the next read.

On that minimap only:

- Top left: the wind direction on your hex, in the same words the app already uses.
- Top right: the text `GPS`.
- The same two clock hands for the hex you are in, with arrowheads. The hand on the side you already came from is darkened. The hand toward the next hex stays bright.

Nothing else is added to the page. The app still does not click, type, or log in.

## Out of this plan

- No upload, and no second copy of the data.
- No change to how sea wind is calculated.
- No route through water the map has not seen, except open ocean already on the wiki map.
- No second boat type in this pass. The dhow at 35° and the cog at 45° can be a later switch. This pass only speeds up and slows down with the wind.

## Build order

1. Save this plan as `GPS.md`.
2. Extend the notice, and show it once more on the next launch.
3. Read one minimap while Sync is on, save the picture and the six water edges, and paint it under the arrows.
4. Link water edges into rivers, including the join to the ocean.
5. Add **Update map** for the open world map, matched by name, with no duplicate hexes.
6. Add the GPS button, Boat and Walk, and A* with wind as traffic.
7. Draw the clock-hand route, dim the hexes already passed, and reroute if you leave the line.
8. Write the minimap overlay, and remove it when GPS is off.

## Check against a live tab

Read from the public play client (`game_web_app.js` and `browserlands/maps.js`, cache `f4404516d17f`), before the blue test was fixed in code.

- The corner minimap is `#minimap-svg`, an SVG with viewBox `0 0 240 240`. The terrain is an `<image data-terrain-map="true">`, not a canvas. Its address is the zone `map_rel` with `.a3d` replaced by `.minimap.png`. `refreshZoneMinimap` replaces the SVG children when the zone changes, so the GPS layer is put back on the next read.
- Water is a blue pixel: blue at least 48, stronger than red and green, and at least 18 above the next channel. These client colors are not water: chart backing `#263c39`, exit dots `#c8d5a1`, buildings `#e5d6a2`, wildlife `#efb68b`, unexplored fog `rgb(24, 40, 54)`. Open-sea fill `rgb(38, 76, 108)` is blue, and an `open_sea` tile is water on all six edges. A real `.minimap.png` was not on a public URL, so the threshold is this rule rather than one sampled file.
- The full map lives in `#travel-worldmap` inside `#travel-worldmap-panel`. It is open when `ak_uiState.travelOpen` is set, which is the M key. Revealed tiles are `ak_worldGraphState.discovery.zones[id] === 'visited'` or `'discovered'`, plus the zone you are standing in. On screen, both `. Visited.` and `. Seen, not visited.` labels are read. A tile the game draws with `?` stays unread.
- A visited node exposes `id`, `name`, `open_sea`, and `map_rel`. The current zone name is `ak_wsState.currentZoneName`. The picture is stored on the wiki hex with the same id, or, when the id is new, the one wiki hex with the same name.
