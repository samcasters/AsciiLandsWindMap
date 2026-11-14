using System.Text.Json;

namespace AsciiLandsWindMap;

/// <summary>Wind arrow drawn in the top left of the game minimap. Points are in the 240-unit overlay.</summary>
public readonly record struct WindArrow(
    bool Shown,
    double TailX,
    double TailY,
    double BaseX,
    double BaseY,
    double TipX,
    double TipY,
    double LeftX,
    double LeftY,
    double RightX,
    double RightY)
{
    public static WindArrow Calm { get; } = new(
        false,
        MinimapOverlay.CenterX, MinimapOverlay.CenterY,
        MinimapOverlay.CenterX, MinimapOverlay.CenterY,
        MinimapOverlay.CenterX, MinimapOverlay.CenterY,
        MinimapOverlay.CenterX, MinimapOverlay.CenterY,
        MinimapOverlay.CenterX, MinimapOverlay.CenterY);
}

public static class MinimapOverlay
{
    public const string ElementId = "windmap-gps";
    public const string WindId = "windmap-wind";
    public const double CenterX = 30;
    public const double CenterY = 28;

    /// <summary>Arrow where the wind is going. 0 is north and degrees grow clockwise, matching the map arrows.</summary>
    public static WindArrow Arrow(WindSample? sample)
    {
        if (sample is not WindSample at || at.Cancelled || at.Strength <= 0)
            return WindArrow.Calm;
        var strength = Math.Clamp(at.Strength, 1, 3);
        var length = strength switch
        {
            1 => 20d,
            2 => 28d,
            _ => 36d,
        };
        var rad = CompassText.Normalize(at.DirectionDeg) * Math.PI / 180.0;
        var dx = Math.Sin(rad);
        var dy = -Math.Cos(rad);
        var px = -dy;
        var py = dx;
        var tipX = CenterX + dx * length * 0.58;
        var tipY = CenterY + dy * length * 0.58;
        var tailX = CenterX - dx * length * 0.42;
        var tailY = CenterY - dy * length * 0.42;
        const double head = 9;
        var baseX = tipX - dx * head;
        var baseY = tipY - dy * head;
        const double wing = 4.8;
        return new WindArrow(
            true,
            tailX, tailY,
            baseX, baseY,
            tipX, tipY,
            baseX + px * wing, baseY + py * wing,
            baseX - px * wing, baseY - py * wing);
    }

    public static string Markup(WindArrow arrow, int enterSide, int exitSide)
    {
        var wind = arrow.Shown ? Shaft(arrow) : CalmMark();
        var hands = Hand(enterSide, "#6a7560", inward: true) + Hand(exitSide, "#eef5d8", inward: false);
        return $$"""
            <style>
            #{{ElementId}} text { font-family: Trebuchet MS, sans-serif; font-size: 11px; }
            </style>
            <g id="{{WindId}}">{{wind}}</g>
            <text x="232" y="16" text-anchor="end" fill="#9de46c" stroke="#17251a" stroke-width="2" paint-order="stroke">GPS</text>
            {{hands}}
            """;
    }

    public static string InstallScript(WindArrow arrow, int enterSide, int exitSide)
    {
        var json = JsonSerializer.Serialize(Markup(arrow, enterSide, exitSide));
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

    private static string Shaft(WindArrow arrow) =>
        FormattableString.Invariant($"<line x1=\"{arrow.TailX:0.##}\" y1=\"{arrow.TailY:0.##}\" x2=\"{arrow.BaseX:0.##}\" y2=\"{arrow.BaseY:0.##}\" stroke=\"#17251a\" stroke-width=\"5\" stroke-linecap=\"round\"/><line x1=\"{arrow.TailX:0.##}\" y1=\"{arrow.TailY:0.##}\" x2=\"{arrow.BaseX:0.##}\" y2=\"{arrow.BaseY:0.##}\" stroke=\"#eef5d8\" stroke-width=\"2.4\" stroke-linecap=\"round\"/><polygon fill=\"#eef5d8\" stroke=\"#17251a\" stroke-width=\"1.2\" stroke-linejoin=\"round\" paint-order=\"stroke\" points=\"{arrow.TipX:0.##},{arrow.TipY:0.##} {arrow.LeftX:0.##},{arrow.LeftY:0.##} {arrow.RightX:0.##},{arrow.RightY:0.##}\"/>");

    private static string CalmMark() =>
        FormattableString.Invariant($"<circle cx=\"{CenterX:0.##}\" cy=\"{CenterY:0.##}\" r=\"5\" fill=\"none\" stroke=\"#17251a\" stroke-width=\"3.6\"/><circle cx=\"{CenterX:0.##}\" cy=\"{CenterY:0.##}\" r=\"5\" fill=\"none\" stroke=\"#eef5d8\" stroke-width=\"1.8\"/>");

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
}
