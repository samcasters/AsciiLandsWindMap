# Sync

Read the open Asciilands game, show where you are, and fill sea wind from the game's own formula. Region names sit in the middle of each hex at 30% opacity so the arrows stay easy to see.

The wiki does not publish the formula. The game client does, in `browserlands/sailing.js` (`windAt`, `breeze`, `windPlace`) and `browserlands/visual-environment.js` (`hourAt`). This app copies that math. It does not invent a different one.

## Controls

Under the clock:

- A **Sync** toggle. It starts off each time the app opens.
- A seconds box from 1 to 600 (10 minutes). The number is remembered.

**Sync map** still downloads region names from the wiki.

## What a tick does

Sync looks for Edge, Chrome, or Firefox on `https://asciilands.io/play/`. It does not click the page or send keys.

It reads the world clock, the region you are in, facing, and the deep-water wind mark when the game is showing it. Ocean draws a ship on that hex. Any other biome draws a person. The camera stays put.

Sync reads the time, place, and wind mark shown on the play page. Ocean arrows use the game client's wind formula: the on-screen hour sets the land breeze, and the real clock sets the slow lane and the short gust. The arrows keep moving between reads. A hand reading in the same part of the day still wins.

Calculated wind never overwrites a reading you entered. A hand reading in the current time window wins. Export marks calculated rows as sync. Import never lets those replace a hand reading.

Land hexes get no calculated arrow. The route finder is not part of this.
