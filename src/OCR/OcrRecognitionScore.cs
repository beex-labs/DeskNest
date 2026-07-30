namespace BeeX.OCR;

internal static class OcrRecognitionScore
{
    public static int Score(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return 0;
        }

        string[] lines = text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        int useful = 0;
        int latin = 0;
        int cjk = 0;
        int digits = 0;
        int symbols = 0;
        int shortNoiseLines = 0;
        int leadingPunctuation = 0;

        foreach (string rawLine in lines)
        {
            string line = rawLine.Trim();
            int lineUseful = 0;

            if (line.Length == 0)
            {
                continue;
            }

            if (IsPunctuationLike(line[0]))
            {
                leadingPunctuation++;
            }

            foreach (char c in line)
            {
                if ((c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z'))
                {
                    latin++;
                    useful++;
                    lineUseful++;
                }
                else if (char.IsDigit(c))
                {
                    digits++;
                    useful++;
                    lineUseful++;
                }
                else if (IsCjk(c))
                {
                    cjk++;
                    useful++;
                    lineUseful++;
                }
                else if (!char.IsWhiteSpace(c) && !IsCommonTextSymbol(c))
                {
                    symbols++;
                }
            }

            if (lineUseful <= 1 && line.Length <= 3)
            {
                shortNoiseLines++;
            }
        }

        int nonEmptyLines = lines.Count(line => !string.IsNullOrWhiteSpace(line));
        int score = useful * 12 + latin * 2 + digits * 2 + nonEmptyLines * 5;

        if (latin > 0 && cjk > 0)
        {
            score -= Math.Min(cjk, latin + digits) * 4;
        }

        score -= symbols * 12;
        score -= shortNoiseLines * 42;
        score -= leadingPunctuation * 10;

        return Math.Max(0, score);
    }

    public static bool IsGoodEnough(string text, OcrImageStats stats)
    {
        int score = Score(text);
        if (score < 70)
        {
            return false;
        }

        return !stats.LooksDark && !stats.LowContrast;
    }

    public static bool IsStrongResult(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        int useful = 0;
        int suspicious = 0;

        foreach (char c in text)
        {
            if ((c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z') || char.IsDigit(c) || IsCjk(c))
            {
                useful++;
            }
            else if (!char.IsWhiteSpace(c) && !IsCommonTextSymbol(c))
            {
                suspicious++;
            }
        }

        return Score(text) >= 150 && useful >= 6 && suspicious <= Math.Max(1, useful / 12);
    }

    private static bool IsCjk(char c)
    {
        return c is >= '\u3400' and <= '\u9fff';
    }

    private static bool IsCommonTextSymbol(char c)
    {
        return c is '-' or '_' or '.' or ',' or ':' or ';' or '/' or '\\' or '(' or ')' or '[' or ']' or '#'
            or '+' or '=' or '&' or '%' or '@' or '\'' or '"' or ' ';
    }

    private static bool IsPunctuationLike(char c)
    {
        return char.IsPunctuation(c) || c is '、' or '，' or '。' or '；' or '：';
    }
}
