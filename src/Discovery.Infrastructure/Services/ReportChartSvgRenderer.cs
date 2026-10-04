using System.Globalization;
using System.Net;
using System.Text;

namespace Discovery.Infrastructure.Services;

/// <summary>
/// Renderiza os graficos dos relatorios como SVG inline.
///
/// Substitui a integracao com quickchart.io: alem de depender de um servico
/// externo (nao funciona offline/air-gapped), o payload com os dados do cliente
/// era enviado na URL para um terceiro a cada visualizacao/impressao.
/// O SVG e auto-contido, sem requisicao de rede.
/// </summary>
internal static class ReportChartSvgRenderer
{
    private static readonly string[] Palette =
    [
        "#0f4c81", "#3a7d44", "#b91c1c", "#f59e0b", "#7c3aed",
        "#0891b2", "#db2777", "#65a30d", "#ea580c", "#475569"
    ];

    private const int PadLeft = 64;
    private const int PadRight = 24;
    private const int PadTop = 40;
    private const int PadBottom = 64;

    public static string Render(string? type, IReadOnlyList<string> labels, IReadOnlyList<double> values, string title, int width, int height)
    {
        var w = Math.Clamp(width, 240, 1600);
        var h = Math.Clamp(height, 160, 1000);
        var chartType = (type ?? "bar").ToLowerInvariant();

        var body = chartType switch
        {
            "pie" or "doughnut" => BuildPie(labels, values, w, h, doughnut: chartType == "doughnut"),
            "line" => BuildLine(labels, values, w, h, sparkline: false),
            "sparkline" => BuildLine(labels, values, w, h, sparkline: true),
            "horizontalbar" => BuildHorizontalBar(labels, values, w, h),
            _ => BuildVerticalBar(labels, values, w, h)
        };

        return Wrap(body, title, w, h);
    }

    public static string RenderGauge(string title, double value, int width, int height)
    {
        var w = Math.Clamp(width, 240, 1600);
        var h = Math.Clamp(height, 160, 1000);

        // Percentual 0-100: o gauge do produto sempre representou conformidade.
        var percent = Math.Clamp(value, 0d, 100d);

        var cx = w / 2d;
        var cy = h * 0.72;
        var radius = Math.Min(w * 0.38, h * 0.56);
        var arcWidth = Math.Max(14, radius * 0.16);

        var sb = new StringBuilder();
        sb.Append(Arc(cx, cy, radius, 180, 0, "#e2e8f0", arcWidth));

        if (percent > 0)
        {
            var endAngle = 180 - (180 * (percent / 100d));
            sb.Append(Arc(cx, cy, radius, 180, endAngle, ColorFor(percent), arcWidth));
        }

        // Ponteiro
        var needleAngle = Math.PI * (1 - (percent / 100d));
        var nx = cx + (Math.Cos(needleAngle) * radius * 0.78);
        var ny = cy - (Math.Sin(needleAngle) * radius * 0.78);
        sb.Append("<line x1=\"").Append(N(cx)).Append("\" y1=\"").Append(N(cy))
          .Append("\" x2=\"").Append(N(nx)).Append("\" y2=\"").Append(N(ny))
          .Append("\" stroke=\"#16324F\" stroke-width=\"").Append(N(Math.Max(3, arcWidth * 0.22))).Append("\" stroke-linecap=\"round\" />");
        sb.Append("<circle cx=\"").Append(N(cx)).Append("\" cy=\"").Append(N(cy)).Append("\" r=\"").Append(N(Math.Max(6, arcWidth * 0.35))).Append("\" fill=\"#16324F\" />");

        sb.Append("<text x=\"").Append(N(cx)).Append("\" y=\"").Append(N(cy - radius * 0.32))
          .Append("\" text-anchor=\"middle\" font-size=\"").Append(N(Math.Max(18, radius * 0.26)))
          .Append("\" font-weight=\"700\" fill=\"#16324F\">").Append(N(percent)).Append("%</text>");

        return Wrap(sb.ToString(), title, w, h);
    }

    private static string Wrap(string body, string? title, int w, int h)
    {
        var sb = new StringBuilder();
        sb.Append("<svg xmlns=\"http://www.w3.org/2000/svg\" role=\"img\" viewBox=\"0 0 ").Append(w).Append(' ').Append(h)
          .Append("\" width=\"").Append(w).Append("\" height=\"").Append(h)
          .Append("\" style=\"max-width:100%;height:auto;font-family:Segoe UI,Arial,sans-serif\">");

        if (!string.IsNullOrWhiteSpace(title))
        {
            sb.Append("<text x=\"").Append(w / 2).Append("\" y=\"20\" text-anchor=\"middle\" font-size=\"14\" font-weight=\"600\" fill=\"#16324F\">")
              .Append(Escape(title)).Append("</text>");
        }

        sb.Append(body).Append("</svg>");
        return sb.ToString();
    }

    private static string BuildVerticalBar(IReadOnlyList<string> labels, IReadOnlyList<double> values, int w, int h)
    {
        var sb = new StringBuilder();
        var plotW = w - PadLeft - PadRight;
        var plotH = h - PadTop - PadBottom;
        var max = MaxValue(values);

        sb.Append(Axes(w, h, plotW, plotH, max));

        var count = values.Count;
        var slot = (double)plotW / Math.Max(1, count);
        var barW = Math.Min(slot * 0.68, 70);
        var labelStep = Math.Max(1, (int)Math.Ceiling(count / 12d));

        for (var i = 0; i < count; i++)
        {
            var value = Math.Max(0, values[i]);
            var barH = (value / max) * plotH;
            var x = PadLeft + (slot * i) + ((slot - barW) / 2);
            var y = PadTop + (plotH - barH);

            sb.Append("<rect x=\"").Append(N(x)).Append("\" y=\"").Append(N(y))
              .Append("\" width=\"").Append(N(barW)).Append("\" height=\"").Append(N(barH))
              .Append("\" rx=\"3\" fill=\"").Append(ColorFor(i)).Append("\"><title>")
              .Append(Escape(labels[i])).Append(": ").Append(N(values[i])).Append("</title></rect>");

            sb.Append("<text x=\"").Append(N(x + (barW / 2))).Append("\" y=\"").Append(N(y - 4))
              .Append("\" text-anchor=\"middle\" font-size=\"10\" fill=\"#475569\">").Append(N(values[i])).Append("</text>");

            if (i % labelStep == 0)
            {
                sb.Append("<text x=\"").Append(N(x + (barW / 2))).Append("\" y=\"").Append(PadTop + plotH + 16)
                  .Append("\" text-anchor=\"middle\" font-size=\"10\" fill=\"#475569\">")
                  .Append(Escape(Truncate(labels[i], 14))).Append("</text>");
            }
        }

        return sb.ToString();
    }

    private static string BuildHorizontalBar(IReadOnlyList<string> labels, IReadOnlyList<double> values, int w, int h)
    {
        var sb = new StringBuilder();
        var labelW = Math.Min(160, Math.Max(90, w / 4));
        var plotX = labelW + 12;
        var plotW = w - plotX - 56;
        var plotH = h - PadTop - 16;
        var max = MaxValue(values);

        var count = values.Count;
        var rowH = (double)plotH / Math.Max(1, count);
        var barH = Math.Min(rowH * 0.62, 34);

        sb.Append("<line x1=\"").Append(plotX).Append("\" y1=\"").Append(PadTop - 8)
          .Append("\" x2=\"").Append(plotX).Append("\" y2=\"").Append(PadTop + plotH)
          .Append("\" stroke=\"#cbd5e1\" stroke-width=\"1\" />");

        for (var i = 0; i < count; i++)
        {
            var value = Math.Max(0, values[i]);
            var barW = (value / max) * plotW;
            var y = PadTop + (rowH * i) + ((rowH - barH) / 2);

            sb.Append("<rect x=\"").Append(plotX).Append("\" y=\"").Append(N(y))
              .Append("\" width=\"").Append(N(barW)).Append("\" height=\"").Append(N(barH))
              .Append("\" rx=\"3\" fill=\"").Append(ColorFor(i)).Append("\"><title>")
              .Append(Escape(labels[i])).Append(": ").Append(N(values[i])).Append("</title></rect>");

            sb.Append("<text x=\"").Append(plotX - 8).Append("\" y=\"").Append(N(y + (barH / 2) + 4))
              .Append("\" text-anchor=\"end\" font-size=\"10\" fill=\"#475569\">")
              .Append(Escape(Truncate(labels[i], 22))).Append("</text>");

            sb.Append("<text x=\"").Append(N(plotX + barW + 6)).Append("\" y=\"").Append(N(y + (barH / 2) + 4))
              .Append("\" font-size=\"10\" fill=\"#475569\">").Append(N(values[i])).Append("</text>");
        }

        return sb.ToString();
    }

    private static string BuildLine(IReadOnlyList<string> labels, IReadOnlyList<double> values, int w, int h, bool sparkline)
    {
        var sb = new StringBuilder();
        var padLeft = sparkline ? 8 : PadLeft;
        var padBottom = sparkline ? 8 : PadBottom;
        var plotW = Math.Max(1, w - padLeft - PadRight);
        var plotH = Math.Max(1, h - PadTop - padBottom);
        var max = MaxValue(values);

        if (!sparkline)
            sb.Append(Axes(w, h, plotW, plotH, max));

        var count = values.Count;
        var step = count > 1 ? (double)plotW / (count - 1) : 0;
        var points = new StringBuilder();

        for (var i = 0; i < count; i++)
        {
            var x = padLeft + (step * i);
            var y = PadTop + (plotH - ((Math.Max(0, values[i]) / max) * plotH));
            if (i > 0) points.Append(' ');
            points.Append(N(x)).Append(',').Append(N(y));

            if (!sparkline)
            {
                sb.Append("<circle cx=\"").Append(N(x)).Append("\" cy=\"").Append(N(y)).Append("\" r=\"3\" fill=\"")
                  .Append(ColorFor(0)).Append("\"><title>").Append(Escape(labels[i])).Append(": ").Append(N(values[i])).Append("</title></circle>");
            }
        }

        sb.Append("<polyline fill=\"none\" stroke=\"").Append(ColorFor(0))
          .Append("\" stroke-width=\"").Append(sparkline ? "1.5" : "2").Append("\" points=\"").Append(points).Append("\" />");

        if (!sparkline)
        {
            var labelStep = Math.Max(1, (int)Math.Ceiling(count / 12d));
            for (var i = 0; i < count; i += labelStep)
            {
                sb.Append("<text x=\"").Append(N(padLeft + (step * i))).Append("\" y=\"").Append(PadTop + plotH + 16)
                  .Append("\" text-anchor=\"middle\" font-size=\"10\" fill=\"#475569\">")
                  .Append(Escape(Truncate(labels[i], 14))).Append("</text>");
            }
        }

        return sb.ToString();
    }

    private static string BuildPie(IReadOnlyList<string> labels, IReadOnlyList<double> values, int w, int h, bool doughnut)
    {
        var sb = new StringBuilder();
        var total = values.Sum(v => Math.Max(0, v));
        var cx = w / 2d;
        var cy = (h / 2d) + 8;
        var radius = Math.Min(w * 0.32, (h - PadTop - 16) * 0.42);

        if (total <= 0 || radius <= 0)
        {
            sb.Append("<circle cx=\"").Append(N(cx)).Append("\" cy=\"").Append(N(cy)).Append("\" r=\"").Append(N(Math.Max(1, radius)))
              .Append("\" fill=\"#e2e8f0\" />");
            return sb.ToString();
        }

        var innerRadius = doughnut ? radius * 0.55 : 0d;
        var startAngle = 0d;

        for (var i = 0; i < values.Count; i++)
        {
            var value = Math.Max(0, values[i]);
            if (value <= 0)
                continue;

            var sweep = 360d * (value / total);
            var endAngle = startAngle + sweep;
            sb.Append(Sector(cx, cy, radius, innerRadius, startAngle, endAngle, ColorFor(i)));
            sb.Append("<title>").Append(Escape(labels[i])).Append(": ").Append(N(values[i])).Append("</title>");
            startAngle = endAngle;
        }

        return sb.ToString();
    }

    private static string Axes(int w, int h, int plotW, int plotH, double max)
    {
        var sb = new StringBuilder();
        sb.Append("<line x1=\"").Append(PadLeft).Append("\" y1=\"").Append(PadTop)
          .Append("\" x2=\"").Append(PadLeft).Append("\" y2=\"").Append(PadTop + plotH)
          .Append("\" stroke=\"#cbd5e1\" stroke-width=\"1\" />");
        sb.Append("<line x1=\"").Append(PadLeft).Append("\" y1=\"").Append(PadTop + plotH)
          .Append("\" x2=\"").Append(PadLeft + plotW).Append("\" y2=\"").Append(PadTop + plotH)
          .Append("\" stroke=\"#cbd5e1\" stroke-width=\"1\" />");

        for (var step = 0; step <= 4; step++)
        {
            var y = PadTop + (plotH - (plotH * (step / 4d)));
            var value = max * (step / 4d);
            sb.Append("<line x1=\"").Append(PadLeft).Append("\" y1=\"").Append(N(y))
              .Append("\" x2=\"").Append(PadLeft + plotW).Append("\" y2=\"").Append(N(y))
              .Append("\" stroke=\"#eef2f7\" stroke-width=\"1\" />");
            sb.Append("<text x=\"").Append(PadLeft - 8).Append("\" y=\"").Append(N(y + 4))
              .Append("\" text-anchor=\"end\" font-size=\"10\" fill=\"#94a3b8\">").Append(N(value)).Append("</text>");
        }

        return sb.ToString();
    }

    private static string Sector(double cx, double cy, double radius, double innerRadius, double startDeg, double endDeg, string color)
    {
        var largeArc = (endDeg - startDeg) > 180 ? 1 : 0;
        var startOuter = Polar(cx, cy, radius, startDeg);
        var endOuter = Polar(cx, cy, radius, endDeg);

        var sb = new StringBuilder();
        sb.Append("<path d=\"M ").Append(N(startOuter.X)).Append(' ').Append(N(startOuter.Y))
          .Append(" A ").Append(N(radius)).Append(' ').Append(N(radius)).Append(" 0 ").Append(largeArc).Append(" 1 ")
          .Append(N(endOuter.X)).Append(' ').Append(N(endOuter.Y));

        if (innerRadius > 0)
        {
            var endInner = Polar(cx, cy, innerRadius, endDeg);
            var startInner = Polar(cx, cy, innerRadius, startDeg);
            sb.Append(" L ").Append(N(endInner.X)).Append(' ').Append(N(endInner.Y))
              .Append(" A ").Append(N(innerRadius)).Append(' ').Append(N(innerRadius)).Append(" 0 ").Append(largeArc).Append(" 0 ")
              .Append(N(startInner.X)).Append(' ').Append(N(startInner.Y));
        }
        else
        {
            sb.Append(" L ").Append(N(cx)).Append(' ').Append(N(cy));
        }

        sb.Append(" Z\" fill=\"").Append(color).Append("\" stroke=\"#ffffff\" stroke-width=\"1\" />");
        return sb.ToString();
    }

    private static string Arc(double cx, double cy, double radius, double startDeg, double endDeg, string color, double strokeWidth)
    {
        var largeArc = Math.Abs(endDeg - startDeg) > 180 ? 1 : 0;
        var start = Polar(cx, cy, radius, startDeg);
        var end = Polar(cx, cy, radius, endDeg);

        return "<path d=\"M " + N(start.X) + " " + N(start.Y) +
               " A " + N(radius) + " " + N(radius) + " 0 " + largeArc + " 1 " + N(end.X) + " " + N(end.Y) +
               "\" fill=\"none\" stroke=\"" + color + "\" stroke-width=\"" + N(strokeWidth) + "\" stroke-linecap=\"round\" />";
    }

    private static (double X, double Y) Polar(double cx, double cy, double radius, double degrees)
    {
        var radians = Math.PI * degrees / 180d;
        return (cx + (radius * Math.Cos(radians)), cy - (radius * Math.Sin(radians)));
    }

    private static string ColorFor(int index) => Palette[Math.Abs(index) % Palette.Length];

    private static string ColorFor(double percent)
    {
        if (percent >= 90) return "#16a34a";
        if (percent >= 70) return "#65a30d";
        if (percent >= 50) return "#f59e0b";
        return "#dc2626";
    }

    private static double MaxValue(IReadOnlyList<double> values)
    {
        var max = 0d;
        foreach (var value in values)
        {
            if (value > max) max = value;
        }
        return max <= 0 ? 1d : max;
    }

    private static string Truncate(string value, int maxLength)
        => value.Length <= maxLength ? value : value[..(maxLength - 1)] + "...";

    private static string Escape(string? value) => WebUtility.HtmlEncode(value ?? string.Empty);

    private static string N(double value) => Math.Round(value, 2).ToString(CultureInfo.InvariantCulture);
}
