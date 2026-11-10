# AsciiLands Wind Map

A Windows app that draws the Asciilands sea wind on a hex map, and keeps your notes on each tile.

This is a fan tool. It is not made or approved by the people who run [Asciilands](https://asciilands.io).

## Install

1. Unzip or copy this folder anywhere you like.
2. Double-click `AsciiLandsWindMap.exe`.
3. The first time, read the notice and press **I accept**. The app will not open the website, read your browser, or download the map until you do. **Decline and quit** closes the app. The notice comes back the next time you open it, until you accept. After you accept, it does not ask again on this PC. The **Notice** button in the app opens the same text whenever you want to read it.

You need an internet connection the first time, so the app can download region names. After that it opens the saved map even when you are offline.

## What you see

The colored hexes are the world from the [public wiki map](https://wiki.asciilands.io/world.json). An arrow in a hex points **where the wind is going**. A longer arrow is stronger wind:

| Mark | Strength |
| --- | --- |
| ~ | light |
| ≈ | fresh |
| ≋ | strong |

Calm draws no arrow. Ocean arrows follow the game's own wind math and change once each in-game hour. A tile whose chart shows a river or a shoreline gets that same wind. Dry land draws no arrow.

The window uses the dark olive look of the [play page](https://asciilands.io/play/).

## How to use it

- Drag the map to move it. Scroll to zoom. **Show all** fits the whole world.
- **Day** shows the breeze onto land (10:00–19:00). **Night** shows the breeze out to sea (23:00–09:00) and darkens the hexes, including the pictures on revealed tiles, so the sea stays one color. The hours between those are **Turning**.
- **Live** follows the game clock. If Sync cannot read the time, use **Match game clock**. Leave the day length blank to hold that time, or type how many real minutes one game day lasts.
- **Sync** turns on after you accept the notice. It reads an open play page every 5 seconds. A ship marks your hex at sea, and a person marks it on land. While Sync is on it also reads the minimap picture of the tile you are standing in. Blue on that picture is water, and the picture is drawn on top of the wiki color and under the arrows. Turn Sync off when you want it quiet. It stays off until you turn it on or open the app again. More detail is in [sync.md](sync.md).
- **Update map**, with the game map open (M), reads the tiles the game has revealed and lays each picture on the matching wiki hex. That includes places you have visited and places you have been near enough to see. Question-mark tiles stay unread until the game reveals them. When the browser can share the page, that includes every revealed tile. Otherwise it reads the revealed tiles on the map in front of you, and tells shared names such as Open Sea apart by where they sit. Pan, then press it again, to pick up tiles that were off screen. A tile already stored is left as it is. A name that matches nothing on the wiki is skipped.
- **GPS** sits under the clock. Choose **Boat** or **Walk**, then click the destination hex. Escape cancels the click. Boat sails the ocean, and wind changes the time the way traffic changes a road. If you are on land it walks you to the nearest river or shore whose blue runs through to the ocean. Two tiles connect when one continuous stretch of blue crosses the side they share. Separate ponds on one tile stay separate. If the destination is on land, the line stops at the nearest ocean tile. Walk stays on land, crosses rivers, and does not cross the ocean. Hexes you have already passed stay on the line, dimmer. The line in each hex runs from the middle to the side you entered and the side you leave, with an arrow for the direction. While that route is on, the game minimap shows the wind, the word GPS, and the same two lines for the hex you are in. When the page can be scripted, those marks are written into the minimap. When the page is only visible on screen, the same marks are drawn in a window over the minimap and clicks pass through it. Clearing GPS, turning Sync off, or closing the app removes that overlay.
- Click a hex to read its wind, description, note, mark, and tags. **X** closes that panel. **Edit** changes the description, note, mark, and tags. You cannot type a wind direction.
- Search by region name with the box at the top, or press Ctrl+F. The realm box is Europe, North America, or Asia Pacific. Wind for each realm is kept separate. Notes and tags belong to the land.

## Your data stays on this PC

The app keeps a file named `wind.sqlite` in this same folder. That file holds region names, your tile notes, the clock, the last calculated sea wind, and the minimap pictures read from the game. If the file is missing, the app creates it.

The app does not upload that file. It has no account and no server of its own. Sync reads the play page already open in your own browser. While GPS is on it draws a small overlay on the minimap: written into the page when the page can be scripted, or a click-through window over the minimap when the page is only visible on screen. **Sync map** downloads the public region list. Nothing from the play page is sent anywhere else.

## Share tile notes

**Export** writes a JSON file of the current realm: descriptions, notes, marks, and tags. It does not write wind. **Import** on another PC adds new tiles. When a tile already has different notes, you choose **Keep mine** or **Use imported**, one tile at a time or for the whole file. Wind readings in an older file are ignored.

Sending that JSON file to someone is your choice. The app does not send it for you.

## Build from source

You need the .NET 8 SDK. From this folder:

```
dotnet test
dotnet publish src\AsciiLandsWindMap\AsciiLandsWindMap.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -o publish
```

The finished app is `publish\AsciiLandsWindMap.exe`.

GPS speeds up with a tailwind and slows down with a headwind. It does not yet switch between the dhow (35° off the wind) and the cog (45°).
