using System.Text.RegularExpressions;

namespace KibaLab.WorldDeployment;

public sealed partial class CompilerDiagnosticCollector(IProcessLineObserver? next = null) : IProcessLineObserver
{
    private readonly HashSet<string> errors = new(StringComparer.Ordinal);
    private readonly HashSet<string> warnings = new(StringComparer.Ordinal);

    public IReadOnlyCollection<string> Errors => errors;
    public IReadOnlyCollection<string> Warnings => warnings;

    public ValueTask<bool> OnLineAsync(string line, bool isError, CancellationToken cancellationToken)
    {
        Match match = CompilerDiagnosticRegex().Match(line);
        if (match.Success)
        {
            string normalized = line.Trim();
            if (string.Equals(match.Groups["severity"].Value, "error", StringComparison.OrdinalIgnoreCase))
                errors.Add(normalized);
            else
                warnings.Add(normalized);
        }

        return next?.OnLineAsync(line, isError, cancellationToken) ?? ValueTask.FromResult(false);
    }

    [GeneratedRegex(@"\b(?<severity>warning|error)\s+CS\d{4}\s*:", RegexOptions.IgnoreCase)]
    private static partial Regex CompilerDiagnosticRegex();
}
