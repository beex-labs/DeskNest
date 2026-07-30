using System.Text;
using OpenCvSharp;
using Sdcb.PaddleOCR;

namespace BeeX.OCR;

internal static class OcrLayoutBuilder
{
    public static string Build(PaddleOcrResult result)
    {
        if (result.Regions.Length == 0)
        {
            return string.Empty;
        }

        List<TextRegion> regions = result.Regions
            .Select(region => CreateRegion(region))
            .Where(region => !string.IsNullOrWhiteSpace(region.Text))
            .OrderBy(region => region.CenterY)
            .ThenBy(region => region.Left)
            .ToList();

        if (regions.Count == 0)
        {
            return string.Empty;
        }

        double baseHeight = Median(regions.Select(region => region.Height));
        double lineThreshold = Math.Max(8.0, baseHeight * 0.58);
        var lines = new List<List<TextRegion>>();

        foreach (TextRegion region in regions)
        {
            List<TextRegion>? line = lines.FirstOrDefault(items => Math.Abs(items.Average(item => item.CenterY) - region.CenterY) <= lineThreshold);
            if (line == null)
            {
                line = [];
                lines.Add(line);
            }

            line.Add(region);
        }

        double leftEdge = regions.Min(region => region.Left);
        double charWidth = EstimateCharWidth(regions);
        double indentUnit = Math.Max(12.0, charWidth * 2.0);

        var output = new StringBuilder();
        string previousText = string.Empty;
        foreach (List<TextRegion> line in lines.OrderBy(items => items.Average(item => item.CenterY)))
        {
            line.Sort((left, right) => left.Left.CompareTo(right.Left));
            string text = BuildLineText(line, charWidth);
            text = NormalizeLeadingMarker(OcrTextPostProcessor.CleanLayoutLine(text));
            if (text.Length == 0)
            {
                continue;
            }

            int indent = GetIndentSpaces(line[0].Left, leftEdge, indentUnit);
            text = RecoverListMarker(text, previousText);
            if (output.Length > 0)
            {
                output.AppendLine();
            }

            if (indent > 0)
            {
                output.Append(' ', indent);
            }

            output.Append(text);
            previousText = text;
        }

        return output.ToString();
    }

    private static TextRegion CreateRegion(PaddleOcrResultRegion region)
    {
        RotatedRect rect = region.Rect;
        Point2f[] points = rect.Points();
        float left = points.Min(point => point.X);
        float right = points.Max(point => point.X);
        float top = points.Min(point => point.Y);
        float bottom = points.Max(point => point.Y);
        string text = region.Text ?? string.Empty;

        return new TextRegion(
            OcrTextPostProcessor.CleanLayoutLine(text),
            left,
            right,
            top,
            bottom,
            rect.Center.Y);
    }

    private static string BuildLineText(IReadOnlyList<TextRegion> line, double charWidth)
    {
        var builder = new StringBuilder();
        TextRegion? previous = null;

        foreach (TextRegion region in line)
        {
            string text = region.Text.Trim();
            if (text.Length == 0)
            {
                continue;
            }

            if (previous != null)
            {
                double gap = region.Left - previous.Right;
                if (gap > Math.Max(5.0, charWidth * 0.75) && NeedsSpaceBetween(builder, text))
                {
                    builder.Append(' ');
                }
            }

            builder.Append(text);
            previous = region;
        }

        return builder.ToString();
    }

    private static bool NeedsSpaceBetween(StringBuilder builder, string right)
    {
        if (builder.Length == 0 || right.Length == 0)
        {
            return false;
        }

        char left = builder[^1];
        char first = right[0];
        if (IsCjk(left) && IsCjk(first))
        {
            return false;
        }

        return true;
    }

    private static string NormalizeLeadingMarker(string line)
    {
        string text = line.Trim();
        if (text.Length == 0)
        {
            return text;
        }

        if (text[0] is '•' or '·' or '●' or '。')
        {
            return "• " + RemoveLeadingIconNoise(text[1..].TrimStart());
        }

        if (text.Length >= 2 && text[0] is '-' or '–' or '—' && !char.IsWhiteSpace(text[1]))
        {
            return "- " + text[1..].TrimStart();
        }

        if (text.Length >= 2 && char.IsDigit(text[0]) && text[1] is '.' or '、' or ')' or '）')
        {
            return text[..2] + " " + text[2..].TrimStart();
        }

        return text;
    }

    private static string RemoveLeadingIconNoise(string text)
    {
        string value = text;
        while (value.Length > 0 && value[0] is '□' or '■' or '√' or '×')
        {
            value = value[1..].TrimStart();
        }

        return value;
    }

    private static string RecoverListMarker(string text, string previousText)
    {
        if (!HasLeadingMarker(previousText) || HasLeadingMarker(text) || text.Length < 3)
        {
            return text;
        }

        if (!EndsSentence(previousText) || LooksLikeSectionLabel(text))
        {
            return text;
        }

        return "• " + text;
    }

    private static bool HasLeadingMarker(string text)
    {
        string trimmed = text.TrimStart();
        if (trimmed.Length == 0)
        {
            return false;
        }

        if (trimmed[0] is '•' or '-' or '·' or '●')
        {
            return true;
        }

        return trimmed.Length >= 2 && char.IsDigit(trimmed[0]) && trimmed[1] is '.' or '、' or ')' or '）';
    }

    private static bool EndsSentence(string text)
    {
        string trimmed = text.TrimEnd();
        return trimmed.EndsWith('。') || trimmed.EndsWith('.') || trimmed.EndsWith('！') || trimmed.EndsWith('!') ||
            trimmed.EndsWith('？') || trimmed.EndsWith('?');
    }

    private static bool LooksLikeSectionLabel(string text)
    {
        string trimmed = text.Trim();
        return trimmed.Length <= 12 && (trimmed.EndsWith(':') || trimmed.EndsWith('：'));
    }

    private static int GetIndentSpaces(double left, double leftEdge, double indentUnit)
    {
        double delta = Math.Max(0.0, left - leftEdge);
        if (delta < indentUnit * 0.8)
        {
            return 0;
        }

        return Math.Min(12, (int)Math.Round(delta / indentUnit) * 2);
    }

    private static double EstimateCharWidth(IReadOnlyList<TextRegion> regions)
    {
        List<double> widths = regions
            .Where(region => region.Text.Length > 0)
            .Select(region => Math.Max(1.0, region.Width / Math.Max(1, region.Text.Length)))
            .OrderBy(width => width)
            .ToList();

        return widths.Count == 0 ? 8.0 : Median(widths);
    }

    private static double Median(IEnumerable<double> values)
    {
        List<double> ordered = values.OrderBy(value => value).ToList();
        if (ordered.Count == 0)
        {
            return 0.0;
        }

        int middle = ordered.Count / 2;
        return ordered.Count % 2 == 1
            ? ordered[middle]
            : (ordered[middle - 1] + ordered[middle]) / 2.0;
    }

    private static bool IsCjk(char c)
    {
        return c is >= '\u3400' and <= '\u9fff';
    }

    private sealed record TextRegion(
        string Text,
        double Left,
        double Right,
        double Top,
        double Bottom,
        double CenterY)
    {
        public double Width => Math.Max(1.0, Right - Left);
        public double Height => Math.Max(1.0, Bottom - Top);
    }
}
