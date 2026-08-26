using System.Globalization;
using System.Text.RegularExpressions;

namespace KibaLab.VRCLI;

public sealed record VrcliProgressLine(
    string Elapsed,
    string Area,
    string Message,
    bool StartsPhase,
    double? OverallProgress)
{
    private static readonly Regex LinePattern = new(
        @"^\[VRCLI\]\[(?<elapsed>\d{2}:\d{2}:\d{2}\.\d{3})\]\[(?<area>[A-Z]+)\] (?<message>.*)$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex ProgressPattern = new(
        @"overall (?<percent>\d+(?:\.\d+)?)%",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    public static bool TryParse(string line, out VrcliProgressLine? progress)
    {
        progress = null;
        int prefix = line.IndexOf("[VRCLI][", StringComparison.Ordinal);
        if (prefix < 0) return false;

        Match match = LinePattern.Match(line[prefix..]);
        if (!match.Success) return false;

        string message = match.Groups["message"].Value;
        bool startsPhase = message.StartsWith("▶ ", StringComparison.Ordinal);
        if (startsPhase) message = message[2..];

        double? overall = null;
        Match percentage = ProgressPattern.Match(message);
        if (percentage.Success && double.TryParse(
                percentage.Groups["percent"].Value,
                NumberStyles.AllowDecimalPoint,
                CultureInfo.InvariantCulture,
                out double parsed))
        {
            overall = Math.Clamp(parsed / 100d, 0d, 1d);
        }

        progress = new VrcliProgressLine(
            match.Groups["elapsed"].Value,
            match.Groups["area"].Value,
            message,
            startsPhase,
            overall);
        return true;
    }
}
