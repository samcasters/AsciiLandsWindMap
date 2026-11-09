# AsciiLands Wind Map

A Windows app for writing down the wind in [Asciilands](https://asciilands.io), one region at a time, and sharing that map with another player.

The colored hexes are the world from the [wiki map](https://wiki.asciilands.io/world.json). A clear grid sits on top of them. An arrow in the middle of a hex points **where the wind is going**. A bigger arrow is a stronger wind: `~` light, `≈` fresh, `≋` strong. Calm draws no arrow.

## Launch

Double-click `AsciiLandsWindMap.exe` in this folder. The first launch needs the internet so it can download region names. After that it opens the saved map if the wiki is offline, and checks for new or renamed regions whenever it can.

If `wind.sqlite` is missing, the app creates it in this same folder the first time it starts. That file holds your wind readings, notes, and tags.

## How to use it

- Drag the map to pan, scroll to zoom. **Show all** fits the whole world. The map opens zoomed in so the arrows are readable.
- **Day** shows the breeze the wiki calls onto land (10:00–19:00). **Night** shows the breeze out to sea (23:00–09:00). The hours between those are **Turning**.
- **Live** follows a clock you set with **Match game clock**. Type the time from the corner of the game. Leave the day length blank to hold that time, or enter how many real minutes one game day lasts and the clock keeps ticking.
- **Sync**, on the row under the clock, reads an open `https://asciilands.io/play` page. The seconds box is how often it reads, from 1 second to 10 minutes. A ship marks your hex at sea, and a person marks it on land. Ocean arrows follow the game's wind formula from that clock and keep moving between reads. A wind reading you entered still wins for that time of day. Details are in `sync.md`.
- Click a hex. Drag the compass marker. Direction is where the wind is going (the in-game compass shows the opposite, where it blows from). Distance from the center snaps to calm, `~`, `≈`, or `≋`.
- Save a note and tags on the tile. Built-in tags: deep water, shallow, stormy, river or lake, gale edge, favorite. You can type your own.
- Search by region name (Ctrl+F). The realm box is Europe, North America, or Asia Pacific. Wind for each realm is kept separate. Notes and tags belong to the land.

Hand readings are what you entered for the time of day on screen. Sync can also fill ocean tiles from the game client's wind formula. It does not invent a different formula, and it does not draw calculated wind on land.

## Share a map

**Export** writes a JSON file of the current realm: wind readings, notes, and tags. **Import** on another PC adds new tiles. When a tile already has different data, you choose **Keep mine** or **Use imported**, one tile at a time or for the whole file.

## Build

From this folder, with the .NET 8 SDK:

```
dotnet test
dotnet publish src\AsciiLandsWindMap\AsciiLandsWindMap.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -o publish
```

A later version can route a boat between tiles from these readings. The dhow fills from 35° off the wind and the cog from 45°. That route finder is not in this version. The readings and the hex neighbors are stored so it can be added without a new save file.
