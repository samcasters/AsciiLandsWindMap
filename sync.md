# Sync

Sync reads the open Asciilands game, shows where you are, and fills sea wind from the game's own formula. Region names sit in the middle of each hex at 30% opacity so the arrows stay easy to see.

The first time you open the app, a notice explains this read. Sync does not run until you accept. If you decline, the app closes and does not look at the website. After you accept, the notice stays away, and Sync starts on each time you open the app.

The wiki does not publish the formula. The game client does, in `browserlands/sailing.js` (`windAt`, `breeze`, `windPlace`) and `browserlands/visual-environment.js` (`hourAt`). This app copies that math. It does not invent a different one.

## Controls

**GPS** sits under the clock. **Sync** is on the row below it. After you accept the opening notice, Sync starts on each time the app opens and reads the game every 5 seconds. Turning it off lasts until you turn it on again or open the app.

**Sync map** still downloads region names from the wiki. **Update map** reads revealed tiles from the game map while that map is open: places you have visited, and places you have been near enough to see. Question-mark tiles stay unread. When the page cannot be scripted, it reads the revealed tiles showing on that map.

## What a tick does

Sync looks for Edge, Chrome, or Firefox on `https://asciilands.io/play/`. It does not click the page or send keys. While Sync is on it reads the minimap picture of the region you are in. Blue on that picture is water. The picture is saved in `wind.sqlite` and drawn over the wiki color, under the arrows, at the game atlas scale so a coast or river continues across the shared edge. **Update map** does the same for revealed tiles on the open world map, including tiles you have only been near, and it does not store a tile twice. A scripted page supplies every revealed tile. The screen reader supplies the revealed tiles in view, and places shared names by the hex they sit on. Tiles off that view wait until the map shows them. Question-mark tiles stay unread.

It reads the world clock, the region you are in, facing, and the deep-water wind mark when the game is showing it. Ocean draws a ship on that hex. Any other biome draws a person. The camera stays put. The place marker updates on each 5 second read.

Ocean arrows use the game client's wind formula. While Live is on they are drawn again every 15 seconds, and immediately when you pick Day, Night, or Live. Day and Night stay on the wind for 14:00 and 02:00. A tile whose chart shows a river or a shoreline gets that same wind. Dry land draws no arrow.

There is no way to type a wind direction. Export and import share descriptions, notes, marks, and tags. They do not share wind.

**GPS** asks for Boat or Walk, then one destination click. Boat sails ocean hexes. A land destination stops on the closest shoreline you can walk from to that tile, and any walk still inland is left for you. Wind is traffic on those crossings: a wind going your way shortens the time, and a wind against you lengthens it. A river is the way from land onto the sea when one continuous stretch of blue crosses from that tile into the next, and from there into the ocean. Separate ponds on one tile stay separate. Walk uses land and rivers and leaves the ocean closed. The route is two lines in each hex, from the center to the side you came from and the side you leave by. Hexes already passed stay drawn, dimmer. Leaving the line draws a new route from the hex you are in. While a route is on, that hex is read about once a second, so the line moves when you enter the next tile.

While that route is active, the minimap shows a wind arrow at the top left, `GPS` at the top right, and the same two lines for the hex you are in. The arrow points where the wind is going. A longer arrow is a stronger wind, and a small ring means calm.  The entry line is darker. When the page can be scripted, those marks are written into `#minimap-svg`. When the page is only visible on screen, the same marks are drawn in a window over the minimap and clicks pass through it. The marks are removed when GPS is cleared, Sync is turned off, or the app closes.
