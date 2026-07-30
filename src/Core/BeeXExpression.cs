using System.Globalization;

namespace BeeX.DeskNest;

static class BeeXExpression
{
    public static bool TryEvaluate(string text, out double value)
    {
        try { var parser = new Parser(text); value = parser.Parse(); return double.IsFinite(value); }
        catch { value = 0; return false; }
    }

    sealed class Parser(string text)
    {
        int index;
        public double Parse() { var value = Expression(); Skip(); if (index != text.Length) throw new FormatException(); return value; }
        double Expression() { var value = Term(); while (true) { Skip(); if (Take('+')) value += Term(); else if (Take('-')) value -= Term(); else return value; } }
        double Term() { var value = Factor(); while (true) { Skip(); if (Take('*') || Take('×')) value *= Factor(); else if (Take('/') || Take('÷')) value /= Factor(); else return value; } }
        double Factor() { Skip(); if (Take('+')) return Factor(); if (Take('-')) return -Factor(); if (Take('(')) { var value = Expression(); Skip(); if (!Take(')')) throw new FormatException(); return value; } return Number(); }
        double Number() { Skip(); var start = index; while (index < text.Length && (char.IsDigit(text[index]) || text[index] is '.' or ',')) index++; if (start == index || !double.TryParse(text[start..index].Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var value)) throw new FormatException(); return value; }
        bool Take(char c) { if (index < text.Length && text[index] == c) { index++; return true; } return false; }
        void Skip() { while (index < text.Length && char.IsWhiteSpace(text[index])) index++; }
    }
}
