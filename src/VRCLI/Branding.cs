namespace KibaLab.WorldDeployment;

public static class Branding
{
    public const string Credit = "by KIBA_";

    public static IReadOnlyList<string> LogoLines { get; } =
    [
        " ___ ___  ______  ______  _____    _______ ",
        "|   |   ||   __ \\|      ||     |_ |_     _|",
        "|   |   ||      <|   ---||       | _|   |_ ",
        " \\_____/ |___|__||______||_______||_______|"
    ];

    public static int LogoWidth { get; } = LogoLines.Max(line => line.Length);
    public static string Version { get; } =
        typeof(Branding).Assembly.GetName().Version?.ToString(3) ?? "dev";

    public static string FooterLine
    {
        get
        {
            string version = $"v{Version}";
            int spacing = Math.Max(1, LogoWidth - version.Length - Credit.Length);
            return version + new string(' ', spacing) + Credit;
        }
    }

    public static string LogoText => string.Join(Environment.NewLine, LogoLines) +
                                     Environment.NewLine + FooterLine;

    public static IReadOnlyList<string> Fit(int width) =>
        width >= LogoWidth ? LogoLines : ["VRCLI"];

    public static Task WriteAsync(TextWriter writer) =>
        writer.WriteLineAsync(LogoText);
}
