using System.Globalization;
using System.Text;

namespace KibaLab.WorldDeployment;

internal static class TerminalText
{
    public static int Width(string value) => value.EnumerateRunes().Sum(RuneWidth);

    public static string Truncate(string value, int width)
    {
        width = Math.Max(1, width);
        if (Width(value) <= width) return value;
        int contentWidth = Math.Max(0, width - 1);
        StringBuilder result = new();
        int used = 0;
        foreach (Rune rune in value.EnumerateRunes())
        {
            int runeWidth = RuneWidth(rune);
            if (used + runeWidth > contentWidth) break;
            result.Append(rune.ToString());
            used += runeWidth;
        }
        return result.Append('…').ToString();
    }

    public static string PadRight(string value, int width)
    {
        int padding = Math.Max(0, width - Width(value));
        return value + new string(' ', padding);
    }

    private static int RuneWidth(Rune rune)
    {
        UnicodeCategory category = Rune.GetUnicodeCategory(rune);
        if (category is UnicodeCategory.Control or UnicodeCategory.NonSpacingMark or
            UnicodeCategory.EnclosingMark or UnicodeCategory.Format)
            return 0;

        int value = rune.Value;
        return value is >= 0x1100 and <= 0x115F or
               0x2329 or 0x232A or
               >= 0x2E80 and <= 0xA4CF or
               >= 0xAC00 and <= 0xD7A3 or
               >= 0xF900 and <= 0xFAFF or
               >= 0xFE10 and <= 0xFE19 or
               >= 0xFE30 and <= 0xFE6F or
               >= 0xFF00 and <= 0xFF60 or
               >= 0xFFE0 and <= 0xFFE6 or
               >= 0x1F300 and <= 0x1FAFF or
               >= 0x20000 and <= 0x3FFFD
            ? 2
            : 1;
    }
}
