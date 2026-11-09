using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Windows.Automation;

namespace AsciiLandsWindMap;

public sealed class GameSnapshot
{
    public bool Found { get; init; }
    public bool ClockReady { get; init; }
    public string? ZoneId { get; init; }
    public string Title { get; init; } = "";
    public string Meta { get; init; } = "";
    public string Facing { get; init; } = "";
    public string Speed { get; init; } = "";
    public string WindLabel { get; init; } = "";
    public string WindGlyph { get; init; } = "";
    public bool WindHidden { get; init; } = true;
    public int? ClockMinute { get; init; }
    public ServerClock? Clock { get; init; }
    public bool PlayerEdge { get; init; }
    public string? ForcedWeather { get; init; }
    public string Detail { get; init; } = "";
    public bool FromScreen { get; init; }
    public IReadOnlyList<string> NearbyNames { get; init; } = [];
}

public static class GameBrowser
{
    private const string ReadScript = """
        (() => {
          const clock = window.akWorldClock;
          const body = clock && clock.clock;
          const state = window.ak_wsState || {};
          const wind = document.getElementById('sailing-wind-bearing');
          const exits = Array.isArray(state.zoneExits) ? state.zoneExits : [];
          const walked = new Set(exits.filter(e => e && e.mode !== 'teleport').map(e => e.side));
          const sides = ['east','northeast','northwest','west','southwest','southeast'];
          const weather = state.zoneWeather;
          return {
            href: location.href,
            title: (document.getElementById('zone-title') || {}).innerText || '',
            meta: (document.getElementById('zone-meta') || {}).innerText || '',
            facing: (document.getElementById('ingame-compass-facing') || {}).innerText || '',
            speed: (document.querySelector('#sailing-point .sailing-point-speed') || {}).textContent || '',
            windLabel: wind ? (wind.getAttribute('aria-label') || '') : '',
            windGlyph: wind ? (wind.textContent || '') : '',
            windHidden: !wind || !!wind.hidden,
            clockText: (document.getElementById('zone-clock') || {}).innerText || '',
            zoneId: state.currentZoneId || '',
            serverTimeMs: body ? body.server_time_ms : null,
            epochMs: body ? body.epoch_ms : null,
            cycleMs: body ? body.cycle_ms : null,
            dayFraction: body ? body.day_fraction : null,
            clockVersion: body ? body.version : null,
            playerEdge: exits.length > 0 && sides.some(side => !walked.has(side)),
            weatherKind: weather && weather.kind || '',
            weatherUntil: weather ? weather.until : null
          };
        })()
        """;

    public static async Task<GameSnapshot> ReadAsync(HttpClient http, CancellationToken cancellationToken)
    {
        string? socket = null;
        try
        {
            socket = await FindPageSocketAsync(http, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            return await ScreenOrAsync(BrowserOpen() ? "debug-port" : "missing").ConfigureAwait(false);
        }

        if (string.IsNullOrWhiteSpace(socket))
        {
            var throughBrowser = await ReadThroughBrowserSocketAsync(cancellationToken).ConfigureAwait(false);
            if (throughBrowser != null)
                return throughBrowser;
            return await ScreenOrAsync(BrowserOpen() ? "debug-port" : "no-tab").ConfigureAwait(false);
        }

        try
        {
            using var web = new ClientWebSocket();
            await web.ConnectAsync(new Uri(socket), cancellationToken).ConfigureAwait(false);
            var payload = JsonSerializer.Serialize(new
            {
                id = 1,
                method = "Runtime.evaluate",
                @params = new { expression = ReadScript, returnByValue = true, awaitPromise = false },
            });
            await web.SendAsync(Encoding.UTF8.GetBytes(payload), WebSocketMessageType.Text, true, cancellationToken).ConfigureAwait(false);
            var json = await ReceiveAsync(web, 1, cancellationToken).ConfigureAwait(false);
            return Parse(json);
        }
        catch (Exception ex) when (ex is WebSocketException or JsonException or InvalidOperationException or TaskCanceledException)
        {
            var screen = await ScreenOrAsync("unreadable").ConfigureAwait(false);
            return screen.FromScreen ? screen : new GameSnapshot { Found = true, Detail = "unreadable" };
        }
    }

    private static async Task<string?> FindPageSocketAsync(HttpClient http, CancellationToken cancellationToken)
    {
        foreach (var port in DebugPorts())
        {
            try
            {
                using var response = await http.GetAsync($"http://127.0.0.1:{port}/json/list", cancellationToken).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                    continue;
                await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
                var pages = await JsonSerializer.DeserializeAsync<List<DebugPage>>(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
                var socket = pages?
                    .FirstOrDefault(page => (page.Url ?? "").Contains("asciilands.io/play", StringComparison.OrdinalIgnoreCase))
                    ?.WebSocketDebuggerUrl;
                if (!string.IsNullOrWhiteSpace(socket))
                    return socket;
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
            {
            }
        }
        return null;
    }

    private static IEnumerable<int> DebugPorts()
    {
        yield return 9222;
        foreach (var (port, _) in ActivePorts())
        {
            if (port != 9222)
                yield return port;
        }
    }

    private static IEnumerable<(int Port, string Path)> ActivePorts()
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        foreach (var relative in new[] { @"Google\Chrome\User Data", @"Microsoft\Edge\User Data" })
        {
            var parsed = ReadActivePort(Path.Combine(local, relative, "DevToolsActivePort"));
            if (parsed != null)
                yield return parsed.Value;
        }
    }

    private static (int Port, string Path)? ReadActivePort(string file)
    {
        if (!File.Exists(file))
            return null;
        try
        {
            using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(stream);
            var lines = reader.ReadToEnd().Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            if (lines.Length > 0 && int.TryParse(lines[0].Trim(), out var port) && port > 0)
                return (port, lines.Length > 1 ? lines[1].Trim() : "");
        }
        catch (IOException)
        {
        }
        return null;
    }

    private static async Task<GameSnapshot?> ReadThroughBrowserSocketAsync(CancellationToken cancellationToken)
    {
        foreach (var (port, path) in ActivePorts())
        {
            if (string.IsNullOrWhiteSpace(path))
                continue;
            var url = $"ws://127.0.0.1:{port}{(path.StartsWith('/') ? path : "/" + path)}";
            try
            {
                using var web = new ClientWebSocket();
                await web.ConnectAsync(new Uri(url), cancellationToken).ConfigureAwait(false);
                await SendAsync(web, 1, "Target.getTargets", null, null, cancellationToken).ConfigureAwait(false);
                var listed = await ReceiveAsync(web, 1, cancellationToken).ConfigureAwait(false);
                var targetId = FindPlayTarget(listed);
                if (targetId == null)
                    return new GameSnapshot { Found = true, Detail = "no-tab" };
                await SendAsync(web, 2, "Target.attachToTarget", new { targetId, flatten = true }, null, cancellationToken).ConfigureAwait(false);
                var attached = await ReceiveAsync(web, 2, cancellationToken).ConfigureAwait(false);
                var sessionId = SessionId(attached);
                if (sessionId == null)
                    continue;
                await SendAsync(web, 3, "Runtime.evaluate", new { expression = ReadScript, returnByValue = true }, sessionId, cancellationToken).ConfigureAwait(false);
                return Parse(await ReceiveAsync(web, 3, cancellationToken).ConfigureAwait(false));
            }
            catch (Exception ex) when (ex is WebSocketException or JsonException or InvalidOperationException or TaskCanceledException)
            {
            }
        }
        return null;
    }

    private static async Task SendAsync(ClientWebSocket web, int id, string method, object? parameters, string? sessionId, CancellationToken cancellationToken)
    {
        var payload = new Dictionary<string, object?> { ["id"] = id, ["method"] = method };
        if (parameters != null)
            payload["params"] = parameters;
        if (sessionId != null)
            payload["sessionId"] = sessionId;
        var bytes = JsonSerializer.SerializeToUtf8Bytes(payload);
        await web.SendAsync(bytes, WebSocketMessageType.Text, true, cancellationToken).ConfigureAwait(false);
    }

    private static string? FindPlayTarget(string json)
    {
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("result", out var result) || !result.TryGetProperty("targetInfos", out var infos))
            return null;
        foreach (var info in infos.EnumerateArray())
        {
            var type = info.TryGetProperty("type", out var typeProp) ? typeProp.GetString() : "";
            var url = info.TryGetProperty("url", out var urlProp) ? urlProp.GetString() ?? "" : "";
            if (type == "page" && url.Contains("asciilands.io/play", StringComparison.OrdinalIgnoreCase)
                && info.TryGetProperty("targetId", out var id))
                return id.GetString();
        }
        return null;
    }

    private static string? SessionId(string json)
    {
        using var doc = JsonDocument.Parse(json);
        if (doc.RootElement.TryGetProperty("result", out var result)
            && result.TryGetProperty("sessionId", out var session))
            return session.GetString();
        return null;
    }

    private static async Task<GameSnapshot> ScreenOrAsync(string fallback)
    {
        try
        {
            var screen = await Task.Run(ReadVisible).ConfigureAwait(false);
            if (screen != null)
                return screen;
        }
        catch (Exception ex) when (ex is ElementNotAvailableException or InvalidOperationException)
        {
        }
        return new GameSnapshot { Detail = fallback == "no-tab" && !BrowserOpen() ? "missing" : fallback };
    }

    private static GameSnapshot? ReadVisible()
    {
        foreach (var name in new[] { "chrome", "msedge", "firefox" })
        {
            foreach (var process in Process.GetProcessesByName(name))
            {
                try
                {
                    if (process.MainWindowHandle == IntPtr.Zero)
                        continue;
                    var window = AutomationElement.FromHandle(process.MainWindowHandle);
                    var nodes = Index(window);
                    if (!nodes.ContainsKey("zone-title") && !nodes.ContainsKey("zone-clock") && !nodes.ContainsKey("sailing-wind-bearing"))
                        continue;
                    var title = TextOf(nodes, "zone-title");
                    var clock = TextOf(nodes, "zone-clock");
                    var wind = TextOf(nodes, "sailing-wind-bearing");
                    var speed = TextOf(nodes, "sailing-point");
                    var facing = TextOf(nodes, "ingame-compass-facing");
                    var meta = TextOf(nodes, "zone-meta");
                    var nearby = nodes.TryGetValue("minimap-svg", out var minimap)
                        ? ChildNames(minimap).Where(IsPlaceLabel).ToList()
                        : [];
                    var glyph = wind.Contains('≋') ? "≋" : wind.Contains('≈') ? "≈" : wind.Contains('~') ? "~" : "";
                    return new GameSnapshot
                    {
                        Found = true,
                        FromScreen = true,
                        ZoneId = GameHud.ZoneId(meta, title),
                        Title = title,
                        Meta = meta,
                        Facing = facing,
                        Speed = Knots(speed),
                        WindLabel = wind,
                        WindGlyph = glyph,
                        WindHidden = wind.Length == 0,
                        ClockMinute = GameHud.ClockMinute(clock),
                        NearbyNames = nearby,
                        Detail = "visible",
                    };
                }
                catch (Exception ex) when (ex is ElementNotAvailableException or InvalidOperationException or System.Runtime.InteropServices.COMException)
                {
                }
            }
        }
        return null;
    }

    private static Dictionary<string, AutomationElement> Index(AutomationElement root)
    {
        var found = new Dictionary<string, AutomationElement>(StringComparer.Ordinal);
        var walker = TreeWalker.RawViewWalker;
        var stack = new Stack<AutomationElement>();
        stack.Push(root);
        var seen = 0;
        while (stack.Count > 0 && seen++ < 4000)
        {
            var element = stack.Pop();
            string id;
            try
            {
                id = element.Current.AutomationId ?? "";
            }
            catch (ElementNotAvailableException)
            {
                continue;
            }
            if (id.Length > 0 && !found.ContainsKey(id))
                found[id] = element;
            AutomationElement? child;
            try
            {
                child = walker.GetFirstChild(element);
            }
            catch (ElementNotAvailableException)
            {
                continue;
            }
            while (child != null)
            {
                stack.Push(child);
                try
                {
                    child = walker.GetNextSibling(child);
                }
                catch (ElementNotAvailableException)
                {
                    break;
                }
            }
        }
        return found;
    }

    private static string TextOf(Dictionary<string, AutomationElement> nodes, string id)
    {
        if (!nodes.TryGetValue(id, out var element))
            return "";
        var own = SafeName(element);
        var children = string.Join(" ", ChildNames(element));
        var combined = (own + " " + children).Trim();
        if (GameHud.ClockMinute(own) == null && GameHud.ClockMinute(combined) != null)
            return combined;
        if (own.Length > 0 && !own.Equals(id, StringComparison.OrdinalIgnoreCase))
            return own;
        return children.Length > 0 ? children : own;
    }

    private static IEnumerable<string> ChildNames(AutomationElement element)
    {
        var walker = TreeWalker.RawViewWalker;
        AutomationElement? child;
        try
        {
            child = walker.GetFirstChild(element);
        }
        catch (ElementNotAvailableException)
        {
            yield break;
        }
        var count = 0;
        while (child != null && count++ < 20)
        {
            var name = SafeName(child);
            if (name.Length > 0)
                yield return name;
            try
            {
                child = walker.GetNextSibling(child);
            }
            catch (ElementNotAvailableException)
            {
                yield break;
            }
        }
    }

    private static string SafeName(AutomationElement element)
    {
        try
        {
            return (element.Current.Name ?? "").Trim();
        }
        catch (ElementNotAvailableException)
        {
            return "";
        }
    }

    private static string Knots(string text)
    {
        var match = System.Text.RegularExpressions.Regex.Match(text, @"\d+(?:\.\d+)?\s*kn", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        return match.Success ? match.Value.Trim() : "";
    }

    private static bool IsPlaceLabel(string name)
    {
        if (name.Length < 2)
            return false;
        if (name.Equals("You", StringComparison.OrdinalIgnoreCase) || name.Equals("Sailboat", StringComparison.OrdinalIgnoreCase))
            return false;
        if (name.Contains("minimap", StringComparison.OrdinalIgnoreCase))
            return false;
        return true;
    }

    public static bool BrowserOpen() =>
        Process.GetProcessesByName("msedge").Concat(Process.GetProcessesByName("chrome")).Concat(Process.GetProcessesByName("firefox"))
            .Any(process =>
            {
                try
                {
                    return (process.MainWindowTitle ?? "").Contains("Asciilands", StringComparison.OrdinalIgnoreCase)
                        || (process.MainWindowTitle ?? "").Contains("asciilands", StringComparison.OrdinalIgnoreCase);
                }
                catch
                {
                    return false;
                }
            });

    public static string? TitledFamily()
    {
        if (Titled("msedge"))
            return "msedge";
        if (Titled("chrome"))
            return "chrome";
        if (Titled("firefox"))
            return "firefox";
        return null;
    }

    public static string? PreferredBrowser()
    {
        if (Titled("msedge"))
            return "msedge";
        if (Titled("chrome"))
            return "chrome";
        if (Process.GetProcessesByName("msedge").Length > 0)
            return "msedge";
        if (Process.GetProcessesByName("chrome").Length > 0)
            return "chrome";
        return File.Exists(EdgePath()) ? "msedge" : File.Exists(ChromePath()) ? "chrome" : null;
    }

    private static bool Titled(string name) =>
        Process.GetProcessesByName(name).Any(process =>
        {
            try
            {
                return (process.MainWindowTitle ?? "").Contains("Asciilands", StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return false;
            }
        });

    private static string EdgePath() => First(
        @"C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe",
        @"C:\Program Files\Microsoft\Edge\Application\msedge.exe");

    private static string ChromePath() => First(
        @"C:\Program Files\Google\Chrome\Application\chrome.exe",
        @"C:\Program Files (x86)\Google\Chrome\Application\chrome.exe",
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"Google\Chrome\Application\chrome.exe"));

    private static string First(params string[] paths) => paths.FirstOrDefault(File.Exists) ?? paths[0];

    private static async Task<string> ReceiveAsync(ClientWebSocket web, int id, CancellationToken cancellationToken)
    {
        var buffer = new byte[1024 * 64];
        using var message = new MemoryStream();
        var messages = 0;
        while (messages++ < 40 && message.Length < 1024 * 1024)
        {
            var result = await web.ReceiveAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (result.MessageType == WebSocketMessageType.Close)
                throw new WebSocketException("The browser closed the debug connection.");
            message.Write(buffer, 0, result.Count);
            if (!result.EndOfMessage)
                continue;
            var text = Encoding.UTF8.GetString(message.ToArray());
            message.SetLength(0);
            if (ReplyId(text) == id)
                return text;
        }

        throw new WebSocketException("The browser debug reply did not arrive.");
    }

    private static int? ReplyId(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("id", out var property) && property.TryGetInt32(out var id))
                return id;
        }
        catch (JsonException)
        {
        }
        return null;
    }

    private static GameSnapshot Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("result", out var result)
            || !result.TryGetProperty("result", out var inner)
            || !inner.TryGetProperty("value", out var value))
            return new GameSnapshot { Found = true, Detail = "unreadable" };

        var href = Text(value, "href");
        if (!href.Contains("asciilands.io/play", StringComparison.OrdinalIgnoreCase))
            return new GameSnapshot { Detail = "no-tab" };

        var title = Text(value, "title");
        var meta = Text(value, "meta");
        var zone = GameHud.ZoneId(Text(value, "zoneId"), meta, title);
        double? server = Num(value, "serverTimeMs");
        double? epoch = Num(value, "epochMs");
        double? cycle = Num(value, "cycleMs");
        ServerClock? clock = null;
        if (server is double serverMs && epoch is double epochMs && cycle is double cycleMs)
        {
            var day = Num(value, "dayFraction") ?? (2.0 / 3.0);
            var version = (int)(Num(value, "clockVersion") ?? 0);
            clock = new ServerClock(serverMs, epochMs, cycleMs, day, version, DateTime.UtcNow);
        }

        var until = Num(value, "weatherUntil");
        var kind = Text(value, "weatherKind");
        string? forced = null;
        if (until is double weatherUntil && server is double serverNow && weatherUntil > serverNow && kind.Length > 0)
            forced = kind;

        return new GameSnapshot
        {
            Found = true,
            ClockReady = clock != null,
            ZoneId = zone,
            Title = title,
            Meta = meta,
            Facing = Text(value, "facing"),
            Speed = Text(value, "speed").Trim(),
            WindLabel = Text(value, "windLabel"),
            WindGlyph = Text(value, "windGlyph").Trim(),
            WindHidden = value.TryGetProperty("windHidden", out var hidden) && hidden.ValueKind == JsonValueKind.True,
            ClockMinute = GameHud.ClockMinute(Text(value, "clockText")),
            Clock = clock,
            PlayerEdge = value.TryGetProperty("playerEdge", out var edge) && edge.ValueKind == JsonValueKind.True,
            ForcedWeather = forced,
            Detail = clock == null ? "no-clock" : "ok",
        };
    }

    private static string Text(JsonElement value, string name) =>
        value.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.String
            ? property.GetString() ?? ""
            : "";

    private static double? Num(JsonElement value, string name)
    {
        if (!value.TryGetProperty(name, out var property))
            return null;
        if (property.ValueKind == JsonValueKind.Number && property.TryGetDouble(out var number))
            return number;
        return null;
    }

    private sealed class DebugPage
    {
        [System.Text.Json.Serialization.JsonPropertyName("url")]
        public string? Url { get; set; }

        [System.Text.Json.Serialization.JsonPropertyName("webSocketDebuggerUrl")]
        public string? WebSocketDebuggerUrl { get; set; }
    }
}
