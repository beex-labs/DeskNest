using System.Text;
using System.Text.RegularExpressions;

namespace BeeX.OCR;

internal static partial class OcrTextPostProcessor
{
    public static string Clean(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return text;
        }

        string[] lines = text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        for (int i = 0; i < lines.Length; i++)
        {
            lines[i] = CleanLine(lines[i]);
        }

        return string.Join(Environment.NewLine, lines.Where(line => line.Length > 0));
    }

    public static string CleanLayoutLine(string line)
    {
        return CleanLine(line);
    }

    public static string CleanLayoutText(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return text;
        }

        string[] lines = text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        for (int i = 0; i < lines.Length; i++)
        {
            string raw = lines[i];
            int indent = raw.Length - raw.TrimStart().Length;
            string cleaned = CleanLayoutLine(raw);
            lines[i] = cleaned.Length == 0 ? string.Empty : new string(' ', Math.Min(indent, 12)) + cleaned;
        }

        return string.Join(Environment.NewLine, lines.Where(line => line.Length > 0));
    }

    private static string CleanLine(string line)
    {
        line = RepairSplitCjkGlyphs(line);
        line = CjkSpacingPattern().Replace(line, "");
        line = RemoveLeadingUiIconNoise(line);

        if (!LooksLikeLatinLine(line))
        {
            line = BulletIconNoisePattern().Replace(line, "$1");
            return MultipleSpacesPattern().Replace(line, " ").Trim();
        }

        string cleaned = line;
        cleaned = BulletIconNoisePattern().Replace(cleaned, "$1");
        cleaned = LatinOpenBoxPattern().Replace(cleaned, "LI");
        cleaned = LatinVerticalPattern().Replace(cleaned, "I");
        cleaned = LatinCirclePattern().Replace(cleaned, "O");
        cleaned = MultipleSpacesPattern().Replace(cleaned, " ").Trim();
        cleaned = JoinBrokenCodeWords(cleaned);

        return cleaned;
    }

    private static string JoinBrokenCodeWords(string line)
    {
        string[] parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2)
        {
            return line;
        }

        var merged = new List<string>();
        foreach (string part in parts)
        {
            if (merged.Count > 0 && ShouldJoinCodeWord(merged[^1], part))
            {
                merged[^1] += part;
            }
            else
            {
                merged.Add(part);
            }
        }

        return string.Join(" ", merged);
    }

    private static bool ShouldJoinCodeWord(string left, string right)
    {
        if (right.Length > 3 || !right.All(char.IsLetter) || !left.All(char.IsLetterOrDigit))
        {
            return false;
        }

        if (right.Length > 1 && right.All(c => c is >= 'A' and <= 'Z'))
        {
            return false;
        }

        return right[0] is >= 'A' and <= 'Z' && HasInternalUppercase(left) && left.Any(char.IsLower);
    }

    private static bool HasInternalUppercase(string value)
    {
        for (int i = 1; i < value.Length; i++)
        {
            if (value[i] is >= 'A' and <= 'Z')
            {
                return true;
            }
        }

        return false;
    }

    private static string RepairSplitCjkGlyphs(string line)
    {
        return line.Replace("\u53e6\u5202", "\u522b", StringComparison.Ordinal);
    }

    private static string RemoveLeadingUiIconNoise(string line)
    {
        string trimmed = line.Trim();
        if (trimmed.Length < 2)
        {
            return IsStandaloneIconNoise(trimmed) ? string.Empty : trimmed;
        }

        string compact = trimmed.Replace(" ", "", StringComparison.Ordinal);
        foreach (string label in CommonNavigationLabels)
        {
            if (compact.Equals(label, StringComparison.Ordinal))
            {
                return label;
            }

            foreach (char marker in LeadingIconNoiseMarkers)
            {
                string markerText = marker.ToString();
                if (compact.Equals(markerText + label, StringComparison.Ordinal))
                {
                    return label;
                }
            }
        }

        if (trimmed.Length >= 3 &&
            LeadingIconNoiseMarkers.Contains(trimmed[0]) &&
            char.IsWhiteSpace(trimmed[1]))
        {
            string rest = trimmed[2..].TrimStart();
            if (IsShortCjkUiLabel(rest))
            {
                return rest;
            }
        }

        return trimmed;
    }

    private static bool IsStandaloneIconNoise(string value)
    {
        return value.Length == 1 && LeadingIconNoiseMarkers.Contains(value[0]);
    }

    private static bool IsShortCjkUiLabel(string value)
    {
        if (value.Length is < 2 or > 5)
        {
            return false;
        }

        return value.All(IsCjk);
    }

    private static bool LooksLikeLatinLine(string line)
    {
        int latin = 0;
        int digits = 0;
        int cjk = 0;

        foreach (char c in line)
        {
            if ((c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z'))
            {
                latin++;
            }
            else if (char.IsDigit(c))
            {
                digits++;
            }
            else if (IsCjk(c))
            {
                cjk++;
            }
        }

        return latin + digits >= 3 && latin + digits >= cjk * 2;
    }

    private static bool IsCjk(char c)
    {
        return c is >= '\u3400' and <= '\u9fff';
    }

    private static readonly string[] CommonNavigationLabels =
    [
        "桌面",
        "下载",
        "文档",
        "图片",
        "音乐",
        "视频",
        "此电脑",
        "网络"
    ];

    private static readonly char[] LeadingIconNoiseMarkers = ['0', 'O', 'o', '〇', '○', '。', '到', '的', '□', '■', '×', '唱', '方', ',', '，', '、', '√', '<', '>', 'へ', 'く'];

    [GeneratedRegex("(?<=[A-Za-z])\\s*[\u51f5]\\s*(?=[A-Za-z])")]
    private static partial Regex LatinOpenBoxPattern();

    [GeneratedRegex("(?<=[A-Za-z])\\s*[\u4e28\uff5c]\\s*(?=[A-Za-z])")]
    private static partial Regex LatinVerticalPattern();

    [GeneratedRegex("(?<=[A-Za-z])\\s*[\u3007\u25cb\u53e3]\\s*(?=[A-Za-z])")]
    private static partial Regex LatinCirclePattern();

    [GeneratedRegex("(?<=[\u3400-\u9fff])\\s+(?=[\u3400-\u9fff])")]
    private static partial Regex CjkSpacingPattern();

    [GeneratedRegex("[ \t]{2,}")]
    private static partial Regex MultipleSpacesPattern();

    [GeneratedRegex("^([•·●\\-]\\s*)[□■√×]\\s*")]
    private static partial Regex BulletIconNoisePattern();
}
