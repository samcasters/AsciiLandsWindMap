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

public sealed class ChartRead
{
    public string ZoneId { get; set; } = "";
    public string Name { get; set; } = "";
    public bool OpenSea { get; set; }
    public string Href { get; set; } = "";
    public string Png { get; set; } = "";
    public string Error { get; set; } = "";
    public bool Found { get; set; }
}

public sealed class VisitedRead
{
    public bool MapOpen { get; set; }
    public List<VisitedTile> Tiles { get; set; } = [];
}

public sealed class VisitedTile
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public bool OpenSea { get; set; }
    public string PngUrl { get; set; } = "";
}

public sealed class FetchedPng
{
    public string Url { get; set; } = "";
    public string Png { get; set; } = "";
    public string Error { get; set; } = "";
}

public sealed class AtlasScreen
{
    public bool MapOpen { get; init; }
    public List<AtlasHit> Hits { get; init; } = [];
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

    private static async Task<string> ReceiveAsync(ClientWebSocket web, int id, CancellationToken cancellationToken, int maxBytes = 1024 * 1024)
    {
        var buffer = new byte[1024 * 64];
        using var message = new MemoryStream();
        var messages = 0;
        var limit = Math.Max(40, maxBytes / buffer.Length + 8);
        while (messages++ < limit && message.Length < maxBytes)
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

    private const int ChartBytes = 6 * 1024 * 1024;

    private const string CurrentChartScript = """
        (async () => {
          const state = window.ak_wsState || {};
          const id = String(state.currentZoneId || '');
          const graph = window.ak_worldGraphState;
          const nodes = graph && graph.data && graph.data.nodes;
          let node = null;
          if (Array.isArray(nodes)) node = nodes.find(n => n && String(n.id) === id) || null;
          const openSea = !!(node && node.open_sea);
          const name = String(state.currentZoneName || (node && node.name) || '');
          const img = document.querySelector('#minimap-svg image[data-terrain-map]');
          let href = '';
          if (img) href = (img.href && img.href.baseVal) || img.getAttribute('href') || '';
          if (!href && node && node.map_rel) href = String(node.map_rel).replace(/\.a3d$/, '.minimap.png');
          let png = '';
          let error = '';
          if (href) {
            try {
              const abs = new URL(href, location.href).href;
              const res = await fetch(abs);
              if (!res.ok) error = 'http ' + res.status;
              else {
                const bytes = new Uint8Array(await res.arrayBuffer());
                let binary = '';
                const chunk = 0x8000;
                for (let i = 0; i < bytes.length; i += chunk)
                  binary += String.fromCharCode.apply(null, bytes.subarray(i, i + chunk));
                png = btoa(binary);
                href = abs;
              }
            } catch (e) {
              error = String(e && e.message || e);
            }
          }
          return { zoneId: id, name, openSea, href, png, error, found: !!document.getElementById('minimap-svg') };
        })()
        """;

    private const string VisitedScript = """
        (() => {
          const graph = window.ak_worldGraphState;
          const raw = graph && graph.data && graph.data.nodes;
          const list = Array.isArray(raw) ? raw : [];
          const disc = (graph && graph.discovery && graph.discovery.zones) || {};
          const current = String((window.ak_wsState && window.ak_wsState.currentZoneId) || '');
          const mapOpen = typeof ak_uiState !== 'undefined' && !!ak_uiState.travelOpen;
          const tiles = [];
          for (const node of list) {
            if (!node || !node.id) continue;
            const id = String(node.id);
            const state = id === current ? 'visited' : String(disc[id] || '').toLowerCase();
            if (state !== 'visited' && state !== 'discovered') continue;
            const rel = node.map_rel ? String(node.map_rel).replace(/\.a3d$/, '.minimap.png') : '';
            let pngUrl = '';
            if (rel) {
              try { pngUrl = new URL(rel, location.href).href; } catch (e) { pngUrl = rel; }
            }
            tiles.push({ id, name: node.name ? String(node.name) : '', openSea: !!node.open_sea, pngUrl });
          }
          return { mapOpen, tiles };
        })()
        """;

    public static Task<ChartRead?> ReadCurrentChartAsync(HttpClient http, CancellationToken cancellationToken) =>
        ReadJsonAsync<ChartRead>(http, CurrentChartScript, awaitPromise: true, cancellationToken);

    public static Task<VisitedRead?> ReadVisitedAsync(HttpClient http, CancellationToken cancellationToken) =>
        ReadJsonAsync<VisitedRead>(http, VisitedScript, awaitPromise: false, cancellationToken);

    public readonly record struct ScreenBox(double X, double Y, double Width, double Height);

    public static ScreenBox? MinimapBox()
    {
        try
        {
            foreach (var name in new[] { "chrome", "msedge", "firefox" })
            {
                foreach (var process in Process.GetProcessesByName(name))
                {
                    try
                    {
                        if (process.MainWindowHandle == IntPtr.Zero)
                            continue;
                        var title = process.MainWindowTitle ?? "";
                        if (!title.Contains("Asciilands", StringComparison.OrdinalIgnoreCase))
                            continue;
                        var window = AutomationElement.FromHandle(process.MainWindowHandle);
                        var minimap = FindById(window, "minimap-svg", 8000);
                        if (minimap == null)
                            continue;
                        var rect = minimap.Current.BoundingRectangle;
                        if (rect.Width < 40 || rect.Height < 40)
                            continue;
                        return new ScreenBox(rect.X, rect.Y, rect.Width, rect.Height);
                    }
                    catch (Exception ex) when (ex is ElementNotAvailableException or InvalidOperationException or System.Runtime.InteropServices.COMException)
                    {
                    }
                }
            }
        }
        catch (Exception ex) when (ex is ElementNotAvailableException or InvalidOperationException or System.Runtime.InteropServices.COMException)
        {
        }
        return null;
    }

    public static AtlasScreen? ReadAtlas()
    {
        try
        {
            return ReadAtlasCore();
        }
        catch (Exception ex) when (ex is ElementNotAvailableException or InvalidOperationException or System.Runtime.InteropServices.COMException)
        {
            return null;
        }
    }

    private static AtlasScreen? ReadAtlasCore()
    {
        AtlasScreen? closed = null;
        foreach (var name in new[] { "chrome", "msedge", "firefox" })
        {
            foreach (var process in Process.GetProcessesByName(name))
            {
                try
                {
                    if (process.MainWindowHandle == IntPtr.Zero)
                        continue;
                    var title = process.MainWindowTitle ?? "";
                    if (!title.Contains("Asciilands", StringComparison.OrdinalIgnoreCase))
                        continue;
                    var window = AutomationElement.FromHandle(process.MainWindowHandle);
                    var map = FindById(window, "travel-worldmap", 20000);
                    if (map == null)
                    {
                        closed ??= new AtlasScreen { MapOpen = false };
                        continue;
                    }
                    var rect = map.Current.BoundingRectangle;
                    if (rect.Width < 80 || rect.Height < 80)
                    {
                        closed ??= new AtlasScreen { MapOpen = false };
                        continue;
                    }
                    return new AtlasScreen { MapOpen = true, Hits = CollectHits(map) };
                }
                catch (Exception ex) when (ex is ElementNotAvailableException or InvalidOperationException or System.Runtime.InteropServices.COMException)
                {
                }
            }
        }
        return closed;
    }

    private static AutomationElement? FindById(AutomationElement root, string id, int cap)
    {
        var walker = TreeWalker.RawViewWalker;
        var stack = new Stack<AutomationElement>();
        stack.Push(root);
        var seen = 0;
        while (stack.Count > 0 && seen++ < cap)
        {
            var element = stack.Pop();
            string current;
            try
            {
                current = element.Current.AutomationId ?? "";
            }
            catch (ElementNotAvailableException)
            {
                continue;
            }
            if (current == id)
                return element;
            PushChildren(walker, stack, element);
        }
        return null;
    }

    private static List<AtlasHit> CollectHits(AutomationElement map)
    {
        var raw = new List<(string Name, double X, double Y, double W, double H)>();
        var walker = TreeWalker.RawViewWalker;
        var stack = new Stack<AutomationElement>();
        PushChildren(walker, stack, map);
        var seen = 0;
        while (stack.Count > 0 && seen++ < 30000)
        {
            var element = stack.Pop();
            string name;
            try
            {
                name = element.Current.Name ?? "";
            }
            catch (ElementNotAvailableException)
            {
                continue;
            }
            var place = AtlasLabel.PlaceName(name);
            if (place != null && !MarkedUnexplored(element))
            {
                try
                {
                    var rect = element.Current.BoundingRectangle;
                    if (rect.Width > 20 && rect.Height > 20)
                        raw.Add((place, rect.X + rect.Width / 2, rect.Y + rect.Height / 2, rect.Width, rect.Height));
                }
                catch (ElementNotAvailableException)
                {
                }
            }
            PushChildren(walker, stack, element);
        }
        if (raw.Count == 0)
            return [];
        var widths = raw.Select(item => item.W).OrderBy(value => value).ToArray();
        var heights = raw.Select(item => item.H).OrderBy(value => value).ToArray();
        var midWidth = widths[widths.Length / 2];
        var midHeight = heights[heights.Length / 2];
        var hits = new List<AtlasHit>();
        var occupied = new HashSet<(int X, int Y, string Name)>();
        foreach (var item in raw)
        {
            if (item.W < midWidth * 0.82 || item.W > midWidth * 1.18 || item.H < midHeight * 0.82 || item.H > midHeight * 1.18)
                continue;
            var cell = ((int)Math.Round(item.X / 8.0), (int)Math.Round(item.Y / 8.0), item.Name.ToLowerInvariant());
            if (!occupied.Add(cell))
                continue;
            hits.Add(new AtlasHit(item.Name, item.X, item.Y));
        }
        return hits;
    }

    private static bool MarkedUnexplored(AutomationElement element)
    {
        AutomationElement? child;
        var walker = TreeWalker.RawViewWalker;
        try
        {
            child = walker.GetFirstChild(element);
        }
        catch (ElementNotAvailableException)
        {
            return false;
        }
        var count = 0;
        while (child != null && count++ < 16)
        {
            try
            {
                if ((child.Current.Name ?? "").Trim() == "?")
                    return true;
            }
            catch (ElementNotAvailableException)
            {
            }
            try
            {
                child = walker.GetNextSibling(child);
            }
            catch (ElementNotAvailableException)
            {
                return false;
            }
        }
        return false;
    }

    private static void PushChildren(TreeWalker walker, Stack<AutomationElement> stack, AutomationElement element)
    {
        AutomationElement? child;
        try
        {
            child = walker.GetFirstChild(element);
        }
        catch (ElementNotAvailableException)
        {
            return;
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
                return;
            }
        }
    }

    public static async Task<IReadOnlyList<FetchedPng>> FetchPngsAsync(HttpClient http, IReadOnlyList<string> urls, CancellationToken cancellationToken)
    {
        if (urls.Count == 0)
            return [];
        var expression = "(async () => { const urls = " + JsonSerializer.Serialize(urls) + @";
          const out = [];
          for (const url of urls) {
            try {
              const res = await fetch(new URL(url, location.href).href);
              if (!res.ok) { out.push({ url, error: String(res.status) }); continue; }
              const bytes = new Uint8Array(await res.arrayBuffer());
              if (bytes.length > 700000) { out.push({ url, error: 'large' }); continue; }
              let binary = '';
              const chunk = 0x8000;
              for (let i = 0; i < bytes.length; i += chunk)
                binary += String.fromCharCode.apply(null, bytes.subarray(i, i + chunk));
              out.push({ url, png: btoa(binary) });
            } catch (e) {
              out.push({ url, error: String(e && e.message || e) });
            }
          }
          return out;
        })()";
        var rows = await ReadJsonAsync<List<FetchedPng>>(http, expression, awaitPromise: true, cancellationToken).ConfigureAwait(false);
        return rows ?? [];
    }

    public static async Task<IReadOnlyDictionary<string, byte[]?>> DownloadPngsAsync(HttpClient http, IReadOnlyList<string> urls, CancellationToken cancellationToken)
    {
        var result = new Dictionary<string, byte[]?>(StringComparer.Ordinal);
        for (var offset = 0; offset < urls.Count; offset += 4)
        {
            var batch = urls.Skip(offset).Take(4).ToArray();
            var jobs = batch.Select(async url =>
            {
                try
                {
                    var bytes = await http.GetByteArrayAsync(url, cancellationToken).ConfigureAwait(false);
                    if (bytes.Length < 8 || bytes.Length > 700000 || bytes[0] != 0x89 || bytes[1] != 0x50)
                        return (url, (byte[]?)null);
                    return (url, (byte[]?)bytes);
                }
                catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException)
                {
                    return (url, (byte[]?)null);
                }
            });
            foreach (var (url, bytes) in await Task.WhenAll(jobs).ConfigureAwait(false))
                result[url] = bytes;
        }
        return result;
    }

    public static Task RemoveOverlayAsync(HttpClient http, CancellationToken cancellationToken) =>
        EvaluateValueJsonAsync(http, MinimapOverlay.RemoveScript, awaitPromise: false, ChartBytes, cancellationToken);

    public static Task<string?> InstallOverlayAsync(HttpClient http, string script, CancellationToken cancellationToken) =>
        EvaluateValueJsonAsync(http, script, awaitPromise: false, ChartBytes, cancellationToken);

    private static async Task<T?> ReadJsonAsync<T>(HttpClient http, string expression, bool awaitPromise, CancellationToken cancellationToken)
    {
        var json = await EvaluateValueJsonAsync(http, expression, awaitPromise, ChartBytes, cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(json))
            return default;
        try
        {
            return JsonSerializer.Deserialize<T>(json, JsonOpts);
        }
        catch (JsonException)
        {
            return default;
        }
    }

    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };

    private static async Task<string?> EvaluateValueJsonAsync(HttpClient http, string expression, bool awaitPromise, int maxBytes, CancellationToken cancellationToken)
    {
        try
        {
            var socket = await FindPageSocketAsync(http, cancellationToken).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(socket))
            {
                using var web = new ClientWebSocket();
                await web.ConnectAsync(new Uri(socket), cancellationToken).ConfigureAwait(false);
                await SendAsync(web, 1, "Runtime.evaluate", new { expression, returnByValue = true, awaitPromise }, null, cancellationToken).ConfigureAwait(false);
                return ValueJson(await ReceiveAsync(web, 1, cancellationToken, maxBytes).ConfigureAwait(false));
            }

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
                    var targetId = FindPlayTarget(await ReceiveAsync(web, 1, cancellationToken).ConfigureAwait(false));
                    if (targetId == null)
                        return null;
                    await SendAsync(web, 2, "Target.attachToTarget", new { targetId, flatten = true }, null, cancellationToken).ConfigureAwait(false);
                    var sessionId = SessionId(await ReceiveAsync(web, 2, cancellationToken).ConfigureAwait(false));
                    if (sessionId == null)
                        continue;
                    await SendAsync(web, 3, "Runtime.evaluate", new { expression, returnByValue = true, awaitPromise }, sessionId, cancellationToken).ConfigureAwait(false);
                    return ValueJson(await ReceiveAsync(web, 3, cancellationToken, maxBytes).ConfigureAwait(false));
                }
                catch (Exception ex) when (ex is WebSocketException or JsonException or InvalidOperationException or TaskCanceledException)
                {
                }
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or WebSocketException or JsonException or InvalidOperationException or TaskCanceledException)
        {
        }
        return null;
    }

    private static string? ValueJson(string cdp)
    {
        using var doc = JsonDocument.Parse(cdp);
        if (!doc.RootElement.TryGetProperty("result", out var result))
            return null;
        if (result.TryGetProperty("exceptionDetails", out _))
            return null;
        if (!result.TryGetProperty("result", out var inner) || !inner.TryGetProperty("value", out var value))
            return null;
        return value.GetRawText();
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
