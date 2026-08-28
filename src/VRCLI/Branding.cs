namespace KibaLab.WorldDeployment;

public static class Branding
{
    public static IReadOnlyList<string> LogoLines { get; } =
    [
        " ___ ___  ______  ______  _____    _______ ",
        "|   |   ||   __ \\|      ||     |_ |_     _|",
        "|   |   ||      <|   ---||       | _|   |_ ",
        " \\_____/ |___|__||______||_______||_______|"
    ];

    public static string LogoText => string.Join(Environment.NewLine, LogoLines);

    public static IReadOnlyList<string> Fit(int width) =>
        width >= LogoLines.Max(line => line.Length) ? LogoLines : ["VRCLI"];

    public static Task WriteAsync(TextWriter writer) =>
        writer.WriteLineAsync(LogoText);
}
