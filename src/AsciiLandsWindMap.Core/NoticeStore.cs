namespace AsciiLandsWindMap;

/// <summary>
/// Remembers that this PC accepted the opening notice. The flag sits beside the app, not on a server.
/// </summary>
public static class NoticeStore
{
    public const string FileName = "notice-accepted-2";

    public static bool IsAccepted(string folder) =>
        File.Exists(Path.Combine(folder, FileName));

    public static void Accept(string folder)
    {
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, FileName), "accepted\n");
    }
}

public static class NoticeText
{
    public const string Title = "Before you use this app";

    public const string Body =
        """
        This app is a fan-made map for Asciilands. It is not made, owned, or approved by the people who run Asciilands or asciilands.io. The game, its name, and its website belong to their owners.

        You can use the app only if you accept this notice. If you decline, or if you close this window, the app quits. It will not open the website, read your browser, or download the map. The next time you start the app, this notice comes back. After you accept, it is not shown again on this computer.

        What the app reads from https://asciilands.io/play/

        After you accept, Sync looks at a play page you already have open in Edge, Chrome, or Firefox on this PC. It checks that tab about every 5 seconds. It does not click, type, or log in. It does not read your password, and it does not read other tabs. While GPS is on, it draws a small overlay on the minimap: the wind on your hex, the word GPS, and the next step of the route. When the page can be scripted, that overlay is written into the minimap. When the page can only be read from the screen, the same overlay is a window over the minimap and clicks pass through it. It removes that overlay when GPS is cleared, when Sync is turned off, or when the app closes.

        From that play page it reads:
        • the region you are in
        • whether that region is sea or land
        • the direction you are facing
        • sailing speed, when the game is showing it
        • the world clock
        • the wind mark, when the game is showing it
        • the weather on that region, when the game exposes it
        • whether you are on the edge of the region
        • the minimap picture of the region you are standing in, while Sync is on
        • the pictures of regions the game has revealed, when you press Update map with the game map open. That includes places you have visited and places you have been near enough to see. Question-mark tiles are left unread
        • nearby place names on the minimap, when the app has to read the game window on screen

        It uses those details to mark your hex, to time the sea-wind arrows, and to draw rivers and a route. Blue on a minimap picture is treated as water. A tile with a river or a shoreline on that picture gets a sea-wind arrow. If it cannot read the browser tab directly, it reads the labels from the game window already on your screen. Update map still saves the pictures of revealed tiles it can see, including tiles you have only been near. The GPS overlay is drawn in a window over the minimap.

        What else it downloads

        It downloads the public region list from https://wiki.asciilands.io/world.json so the hexes have names. That file is the map outline. It does not contain your character.

        Where the data goes

        The app stores its data in a file named wind.sqlite, in the same folder as the app, on this PC. That file holds the region list, the notes, marks, and tags you write, the game time used for the arrows, the calculated wind for each sea tile, and the minimap pictures and water edges read from the game. Your position on the map is shown while the app is open. It is not written into that file. The route is kept while the app is open and is not uploaded.

        The app does not upload this data. It has no account and no server of its own. Nothing read from the play page is sent anywhere else. It stays on the PC where you run the app. If you export a notes file and send that file yourself, that is you choosing to share it.

        Other things you should know

        The arrows copy the game's own wind math. They can be wrong. Do not treat them as an official reading.

        You are responsible for following the rules of Asciilands and of your browser. If those rules do not allow a tool that reads the play page, decline and quit.

        The app is provided as is, with no warranty of any kind. The author is not liable for lost notes, a wrong wind reading, a penalty from the game, or any other loss that comes from using the app.
        """;
}
