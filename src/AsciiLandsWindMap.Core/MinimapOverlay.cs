using System.Text.Json;

namespace AsciiLandsWindMap;

public static class MinimapOverlay
{
    public const string ElementId = "windmap-gps";

    public static string WindWords(WindSample? sample)
    {
        if (sample is not WindSample at || at.Cancelled || at.Strength <= 0)
            return "No wind arrow";
        return $"{CompassText.Mark(at.Strength)} {CompassText.StrengthWord(at.Strength)} {CompassText.Going(at.DirectionDeg)}";
    }

    public static string Markup(string windWords, int enterSide, int exitSide)
    {
        var wind = Xml(windWords);
        var hands = Hand(enterSide, "#6a7560", inward: true) + Hand(exitSide, "#eef5d8", inward: false);
        return $$"""
            <style>
            #{{ElementId}} text { font-family: Trebuchet MS, sans-serif; font-size: 11px; }
            </style>
            <text x="8" y="16" fill="#eef5d8" stroke="#17251a" stroke-width="2" paint-order="stroke">{{wind}}</text>
            <text x="232" y="16" text-anchor="end" fill="#9de46c" stroke="#17251a" stroke-width="2" paint-order="stroke">GPS</text>
            {{hands}}
            """;
    }

    public static string InstallScript(string windWords, int enterSide, int exitSide)
    {
        var json = JsonSerializer.Serialize(Markup(windWords, enterSide, exitSide));
        return $$"""
            (() => {
              const svg = document.getElementById('minimap-svg');
              if (!svg) return { ok: false };
              const ns = 'http://www.w3.org/2000/svg';
              let layer = document.getElementById('{{ElementId}}');
              if (!layer || layer.parentNode !== svg) {
                layer = document.createElementNS(ns, 'g');
                layer.id = '{{ElementId}}';
                layer.setAttribute('pointer-events', 'none');
                svg.appendChild(layer);
              }
              layer.innerHTML = {{json}};
              return { ok: true };
            })()
            """;
    }

    public static string RemoveScript { get; } =
        $"(() => {{ const node = document.getElementById('{ElementId}'); if (node) node.remove(); return true; }})()";

    private static string Hand(int side, string color, bool inward)
    {
        if (side < 0 || side > 5)
            return "";
        var (dx, dy) = Axial.SideVector(side);
        const double reach = 92;
        var x2 = 120 + dx * reach;
        var y2 = 120 + dy * reach;
        var line = FormattableString.Invariant(
            $"<line x1=\"120\" y1=\"120\" x2=\"{x2:0.##}\" y2=\"{y2:0.##}\" stroke=\"{color}\" stroke-width=\"3\" stroke-linecap=\"round\"/>");
        var dirX = inward ? -dx : dx;
        var dirY = inward ? -dy : dy;
        var along = inward ? 0.55 : 1;
        var tipX = 120 + dx * reach * along;
        var tipY = 120 + dy * reach * along;
        var backX = tipX - dirX * 12;
        var backY = tipY - dirY * 12;
        var px = -dirY;
        var py = dirX;
        var wing = 5.5;
        var arrow = FormattableString.Invariant(
            $"<polygon fill=\"{color}\" points=\"{tipX:0.##},{tipY:0.##} {backX + px * wing:0.##},{backY + py * wing:0.##} {backX - px * wing:0.##},{backY - py * wing:0.##}\"/>");
        return line + arrow;
    }

    private static string Xml(string text) =>
        text.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");
}
