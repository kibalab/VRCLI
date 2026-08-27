using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace KibaLab.WorldDeployment;

public sealed class TerminalProgressRenderer : IProcessLineObserver
{
    private static readonly string[] Spinner = ["⠋", "⠙", "⠹", "⠸", "⠼", "⠴", "⠦", "⠧", "⠇", "⠏"];
    private static readonly IReadOnlyDictionary<string, string> AllStageNames = new Dictionary<string, string>
    {
        ["BOOT"] = "Request validation",
        ["DEPENDENCIES"] = "VPM dependencies",
        ["BRIDGE"] = "Unity bridge",
        ["UNITY"] = "Unity startup",
        ["AUTH"] = "Authentication",
        ["CONTEXT"] = "Project context",
        ["PREPARE"] = "Project preparation",
        ["WORLD"] = "World metadata",
        ["SDK"] = "SDK initialization",
        ["OWNERSHIP"] = "Ownership consent",
        ["BUILD"] = "Validation and build",
        ["SIGNATURE"] = "Bundle signature",
        ["UPLOAD"] = "Upload and server update",
        ["CHECK"] = "Preflight report"
    };

    private readonly object gate = new();
    private readonly TextWriter output;
    private readonly OperationMode operation;
    private readonly string[] stageOrder;
    private readonly IReadOnlyDictionary<string, string> stageNames;
    private readonly Func<(int Width, int Height)> terminalSize;
    private readonly CancellationToken cancellationToken;
    private readonly Dictionary<string, StageState> stages;
    private readonly Queue<string> activeDetails = new();
    private readonly Queue<string> recentErrors = new();
    private readonly CancellationTokenSource animationCancellation = new();
    private readonly long deploymentStarted = Stopwatch.GetTimestamp();
    private readonly List<string> previousFrame = [];
    private Task? animationTask;
    private string? activeStage;
    private string lastMessage = "Preparing deployment";
    private double? uploadProgress;
    private int spinnerIndex;
    private bool started;
    private bool finished;
    private bool paused;
    private bool verificationPrompt;
    private IReadOnlyList<(string Method, string Label)> verificationOptions = [];
    private int verificationSelection;
    private string? verificationInputLabel;
    private string? verificationInputValue;
    private string? verificationNotice;
    private string? overviewProject;
    private string? overviewTarget;
    private string? overviewScene;
    private string? overviewPlatform;

    private bool UseColor => Environment.GetEnvironmentVariable("NO_COLOR") == null;

    public TerminalProgressRenderer(
        TextWriter output,
        Func<(int Width, int Height)>? terminalSize = null,
        CancellationToken cancellationToken = default)
        : this(output, OperationMode.Deploy, terminalSize, cancellationToken)
    {
    }

    public TerminalProgressRenderer(
        TextWriter output,
        OperationMode operation,
        Func<(int Width, int Height)>? terminalSize = null,
        CancellationToken cancellationToken = default)
    {
        this.output = output;
        this.operation = operation;
        this.terminalSize = terminalSize ?? ReadTerminalSize;
        this.cancellationToken = cancellationToken;
        stageOrder = operation switch
        {
            OperationMode.Meta =>
            ["BOOT", "AUTH", "CONTEXT", "WORLD", "UPLOAD"],
            OperationMode.Check =>
            ["BOOT", "DEPENDENCIES", "BRIDGE", "UNITY", "AUTH", "CONTEXT", "PREPARE", "WORLD", "SDK", "CHECK"],
            _ =>
            ["BOOT", "DEPENDENCIES", "BRIDGE", "UNITY", "AUTH", "CONTEXT", "PREPARE", "WORLD", "SDK", "OWNERSHIP", "BUILD", "SIGNATURE", "UPLOAD"]
        };
        stageNames = AllStageNames;
        stages = stageOrder.ToDictionary(stage => stage, _ => StageState.Pending, StringComparer.Ordinal);
        lastMessage = operation switch
        {
            OperationMode.Meta => "Preparing metadata update",
            OperationMode.Check => "Preparing preflight check",
            _ => "Preparing deployment"
        };
    }

    public static bool ShouldUse(TerminalMode mode, bool verbose)
    {
        if (mode == TerminalMode.Plain) return false;
        if (mode == TerminalMode.Tui)
        {
            TryEnableVirtualTerminal();
            return !Console.IsOutputRedirected;
        }
        if (verbose || Console.IsOutputRedirected || Console.IsErrorRedirected) return false;
        if (string.Equals(Environment.GetEnvironmentVariable("TERM"), "dumb", StringComparison.OrdinalIgnoreCase)) return false;

        string[] ciVariables =
        [
            "CI",
            "GITHUB_ACTIONS",
            "JENKINS_URL",
            "GITLAB_CI",
            "TF_BUILD",
            "TEAMCITY_VERSION",
            "BUILDKITE"
        ];
        return !ciVariables.Any(name => IsTruthy(Environment.GetEnvironmentVariable(name))) &&
               TryEnableVirtualTerminal();
    }

    public void SetOverview(string project, string target, string scene, string platform)
    {
        lock (gate)
        {
            overviewProject = project;
            overviewTarget = target;
            overviewScene = scene;
            overviewPlatform = platform;
        }
    }

    public void Start()
    {
        lock (gate)
        {
            if (started) return;
            started = true;
            if (WizardTerminalScreen.ConsumeRetainedScreen())
                output.Write("\x1b[?25l\x1b[2J\x1b[H");
            else
                output.Write("\x1b[?1049h\x1b[?25l\x1b[2J\x1b[H");
            TerminalInterruptFeedback.Attach(ShowInterruptFeedback);
            RenderFrameUnsafe(force: true);
        }
        animationTask = AnimateAsync(animationCancellation.Token);
    }

    public ValueTask<bool> OnLineAsync(string line, bool isError, CancellationToken cancellationToken)
    {
        lock (gate)
        {
            if (ProgressLine.TryParse(line, out ProgressLine? progress))
            {
                ApplyProgressUnsafe(progress!);
            }
            else if ((isError || LooksLikeError(line)) && !IsIgnorableDiagnostic(line))
            {
                recentErrors.Enqueue(line);
                while (recentErrors.Count > 8) recentErrors.Dequeue();
            }
        }
        return ValueTask.FromResult(true);
    }

    public void Report(string area, string message, bool startsPhase = false)
    {
        lock (gate)
        {
            ApplyProgressUnsafe(new ProgressLine(string.Empty, area, message, startsPhase, null));
        }
    }

    public T PromptForTwoFactor<T>(Func<T> prompt)
    {
        lock (gate)
        {
            paused = true;
            verificationPrompt = true;
            lastMessage = "VRChat requested an additional sign-in step";
            RenderFrameUnsafe();
            MoveInputCursorUnsafe();
            output.Write("\x1b[?25h");
            output.Flush();
        }

        T result = prompt();

        lock (gate)
        {
            output.Write("\x1b[?25l");
            verificationPrompt = false;
            paused = false;
            lastMessage = "Submitting two-factor verification";
            RenderFrameUnsafe(force: true);
        }
        return result;
    }

    public InteractiveTwoFactorAnswer PromptForTwoFactor(IReadOnlyList<string> methods)
    {
        List<(string Method, string Label)> options = [];
        if (methods.Contains("totp", StringComparer.OrdinalIgnoreCase))
            options.Add(("totp", "Authenticator app code"));
        if (methods.Contains("emailOtp", StringComparer.OrdinalIgnoreCase))
            options.Add(("emailOtp", "Email one-time code"));
        if (methods.Contains("otp", StringComparer.OrdinalIgnoreCase))
            options.Add(("otp", "Recovery code"));
        if (options.Count == 0)
            throw new InvalidOperationException("VRChat requested an unsupported two-factor method.");

        lock (gate)
        {
            paused = true;
            verificationPrompt = true;
            verificationOptions = options;
            verificationSelection = 0;
            verificationInputLabel = null;
            verificationInputValue = null;
            verificationNotice = null;
            lastMessage = "VRChat requested an additional sign-in step";
            RenderFrameUnsafe(force: true);
        }

        if (options.Count > 1)
        {
            while (true)
            {
                ConsoleKeyInfo key = ReadKeyInterruptibly();
                if (key.Key == ConsoleKey.Escape) return EndVerification(string.Empty, string.Empty);
                if (key.Key == ConsoleKey.Enter) break;
                lock (gate)
                {
                    if (key.Key is ConsoleKey.UpArrow || key.KeyChar is 'k' or 'K')
                        verificationSelection = (verificationSelection - 1 + options.Count) % options.Count;
                    else if (key.Key is ConsoleKey.DownArrow || key.KeyChar is 'j' or 'J')
                        verificationSelection = (verificationSelection + 1) % options.Count;
                    else if (char.IsDigit(key.KeyChar))
                    {
                        int numeric = key.KeyChar - '1';
                        if (numeric >= 0 && numeric < options.Count) verificationSelection = numeric;
                    }
                    RenderFrameUnsafe();
                }
            }
        }

        (string method, string label) = options[verificationSelection];
        StringBuilder code = new();
        lock (gate)
        {
            verificationOptions = [];
            verificationInputLabel = label;
            RenderFrameUnsafe(force: true);
        }
        while (true)
        {
            ConsoleKeyInfo key = ReadKeyInterruptibly();
            if (key.Key == ConsoleKey.Escape) return EndVerification(method, string.Empty);
            if (key.Key == ConsoleKey.Enter)
            {
                bool valid = method != "totp" || (code.Length == 6 && code.ToString().All(char.IsDigit));
                if (code.Length > 0 && valid) return EndVerification(method, code.ToString());
                lock (gate)
                {
                    verificationNotice = method == "totp"
                        ? "Enter the current 6-digit authenticator code."
                        : "A verification value is required.";
                    RenderFrameUnsafe();
                }
                continue;
            }
            if (key.Key == ConsoleKey.Backspace)
            {
                if (code.Length > 0) code.Length--;
            }
            else if (!char.IsControl(key.KeyChar))
            {
                code.Append(key.KeyChar);
            }
            lock (gate)
            {
                verificationInputValue = new string('•', code.Length);
                verificationNotice = null;
                RenderFrameUnsafe();
            }
        }
    }

    private InteractiveTwoFactorAnswer EndVerification(string method, string code)
    {
        lock (gate)
        {
            verificationPrompt = false;
            verificationOptions = [];
            verificationInputLabel = null;
            verificationInputValue = null;
            verificationNotice = null;
            paused = false;
            lastMessage = string.IsNullOrEmpty(code)
                ? "Two-factor verification cancelled"
                : "Submitting two-factor verification";
            RenderFrameUnsafe(force: true);
        }
        return new InteractiveTwoFactorAnswer(method, code);
    }

    public async Task FinishAsync(bool success)
    {
        animationCancellation.Cancel();
        if (animationTask != null)
        {
            try
            {
                await animationTask;
            }
            catch (OperationCanceledException)
            {
            }
        }

        lock (gate)
        {
            if (finished) return;
            finished = true;
            if (activeStage != null)
            {
                stages[activeStage] = success ? StageState.Complete : StageState.Failed;
                activeStage = null;
            }
            RenderFrameUnsafe(force: true);
            TerminalInterruptFeedback.Detach(ShowInterruptFeedback);
            output.Write("\x1b[?25h\x1b[?1049l");

            string elapsed = FormatElapsed(Stopwatch.GetElapsedTime(deploymentStarted));
            if (success)
            {
                output.WriteLine("  " + Paint("◆ " + CompletionLabel(true), "32;1") + Paint("  " + elapsed, "90"));
            }
            else
            {
                output.WriteLine("  " + Paint("◆ " + CompletionLabel(false), "31;1") + Paint("  " + elapsed, "90"));
                output.WriteLine(Paint("    " + Fit(lastMessage, 4), "31"));
                if (recentErrors.Count > 0)
                {
                    output.WriteLine();
                    output.WriteLine(Paint("  Recent Unity output", "90;1"));
                    foreach (string line in recentErrors) output.WriteLine("    " + line);
                }
            }
            output.Flush();
        }
    }

    private async Task AnimateAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            await Task.Delay(120, cancellationToken);
            lock (gate)
            {
                if (paused || finished || activeStage == null) continue;
                spinnerIndex = (spinnerIndex + 1) % Spinner.Length;
                RenderFrameUnsafe();
            }
        }
    }

    private void ApplyProgressUnsafe(ProgressLine progress)
    {
        if (!progress.StartsPhase && activeStage == progress.Area && !IsNoisyDetail(progress.Message))
            AddDetailUnsafe(lastMessage);

        if (progress.StartsPhase)
        {
            if (progress.Area == "COMPLETE")
            {
                if (activeStage != null) stages[activeStage] = StageState.Complete;
                activeStage = null;
                lastMessage = progress.Message;
                RenderFrameUnsafe();
                return;
            }

            if (stages.ContainsKey(progress.Area))
            {
                int incomingIndex = Array.IndexOf(stageOrder, progress.Area);
                int activeIndex = activeStage == null ? -1 : Array.IndexOf(stageOrder, activeStage);
                if (stages[progress.Area] == StageState.Complete && incomingIndex <= activeIndex) return;

                if (activeStage != null && activeStage != progress.Area)
                    stages[activeStage] = StageState.Complete;
                if (activeStage != progress.Area)
                {
                    activeStage = progress.Area;
                    stages[activeStage] = StageState.Active;
                    uploadProgress = null;
                    activeDetails.Clear();
                }
            }
        }

        lastMessage = progress.Message;
        if (progress.OverallProgress.HasValue) uploadProgress = progress.OverallProgress;
        RenderFrameUnsafe();
    }

    private void AddDetailUnsafe(string message)
    {
        if (string.IsNullOrWhiteSpace(message) || activeDetails.LastOrDefault() == message) return;
        activeDetails.Enqueue(message);
        while (activeDetails.Count > 8) activeDetails.Dequeue();
    }

    private void RenderFrameUnsafe(bool force = false)
    {
        if (!started) return;
        List<string> frame = BuildFrameUnsafe();
        int rows = Math.Max(frame.Count, previousFrame.Count);
        for (int index = 0; index < rows; index++)
        {
            string next = index < frame.Count ? frame[index] : string.Empty;
            string previous = index < previousFrame.Count ? previousFrame[index] : string.Empty;
            if (!force && string.Equals(next, previous, StringComparison.Ordinal)) continue;
            output.Write($"\x1b[{index + 1};1H\x1b[2K");
            output.Write(next);
        }
        previousFrame.Clear();
        previousFrame.AddRange(frame);
        output.Flush();
    }

    private List<string> BuildFrameUnsafe()
    {
        (int width, int height) = CurrentSize();
        List<string> lines = [];
        lines.Add(Paint(" ◆ VRCLI", "36;1") + Paint("  /  " + OperationTitle(), "90;1"));
        lines.Add(Paint(" " + new string('─', width - 2), "90"));

        if (height >= 18 && overviewProject != null)
        {
            if (width >= 86)
            {
                lines.Add(" " + Paint("PROJECT  ", "90") + Truncate(overviewProject, 28) +
                          Paint("    PLATFORM  ", "90") + Truncate(overviewPlatform ?? string.Empty, 22));
                lines.Add(" " + Paint("TARGET   ", "90") + Truncate(overviewTarget ?? string.Empty, width - 11));
                lines.Add(" " + Paint("SCENE    ", "90") + Truncate(overviewScene ?? string.Empty, width - 11));
            }
            else
            {
                lines.Add(" " + Paint("PROJECT   ", "90") + Truncate(overviewProject, width - 12));
                lines.Add(" " + Paint("TARGET    ", "90") + Truncate(overviewTarget ?? string.Empty, width - 12));
                lines.Add(" " + Paint("SCENE     ", "90") + Truncate(overviewScene ?? string.Empty, width - 12));
                lines.Add(" " + Paint("PLATFORM  ", "90") + Truncate(overviewPlatform ?? string.Empty, width - 12));
            }
            lines.Add(string.Empty);
        }
        else
        {
            lines.Add(Paint(" " + OperationDescription(), "90"));
            lines.Add(string.Empty);
        }

        int detailCapacity = DetailCapacity(height);
        List<string> footer = verificationPrompt
            ? BuildVerificationFooter(width)
            : BuildDeploymentFooter(width);
        int reservedFooter = footer.Count;
        int stageBudget = Math.Max(3, height - lines.Count - reservedFooter - detailCapacity);
        IReadOnlyList<string> visibleStages = SelectVisibleStages(stageBudget);
        int hiddenBefore = Array.IndexOf(stageOrder, visibleStages[0]);
        if (hiddenBefore > 0) lines.Add(Paint($"   ↑ {hiddenBefore} earlier stages", "90"));

        foreach (string stage in visibleStages)
        {
            StageState state = stages[stage];
            bool active = stage == activeStage;
            string icon = state switch
            {
                StageState.Complete => Paint("✓", "32;1"),
                StageState.Failed => Paint("×", "31;1"),
                StageState.Active => Paint(Spinner[spinnerIndex], "36;1"),
                _ => Paint("·", "90")
            };
            string name = active ? Paint(stageNames[stage], "36;1") : stageNames[stage];
            string suffix = active
                ? Paint("  " + Truncate(lastMessage, Math.Max(8, width - stageNames[stage].Length - 10)), "90")
                : string.Empty;
            lines.Add(" " + icon + "  " + name + suffix);

            if (active && detailCapacity > 0)
            {
                foreach (string detail in activeDetails.TakeLast(detailCapacity))
                    lines.Add(Paint("    │ " + Truncate(detail, width - 7), "90"));
            }
        }

        int hiddenAfter = stageOrder.Length - hiddenBefore - visibleStages.Count;
        if (hiddenAfter > 0) lines.Add(Paint($"   ↓ {hiddenAfter} upcoming stages", "90"));

        while (lines.Count < height - reservedFooter) lines.Add(string.Empty);
        if (lines.Count > height - reservedFooter) lines.RemoveRange(height - reservedFooter, lines.Count - (height - reservedFooter));

        lines.AddRange(footer);
        return lines.Take(height).ToList();
    }

    private List<string> BuildDeploymentFooter(int width) =>
    [
        Paint(" " + new string('─', width - 2), "90"),
        " " + Paint(activeStage == null ? "◇" : Spinner[spinnerIndex], "36;1") +
            "  " + Truncate(lastMessage, Math.Max(10, width - 7)),
        BuildProgressFooter(width),
        Paint(" Ctrl+C ×2 cancel", "90") + AlignRight(Paint("VRCLI " + VersionText(), "90"), width - 1, 14)
    ];

    private List<string> BuildVerificationFooter(int width)
    {
        List<string> lines =
        [
            Paint(" ╭─ Verification required " + new string('─', Math.Max(1, width - 27)) + "╮", "33;1"),
            Paint(" │ VRChat requested an additional sign-in step.", "90")
        ];
        if (verificationOptions.Count > 0)
        {
            for (int index = 0; index < verificationOptions.Count; index++)
            {
                string marker = index == verificationSelection ? Paint("❯", "36;1") : " ";
                string label = index == verificationSelection
                    ? Paint(verificationOptions[index].Label, "36;1")
                    : verificationOptions[index].Label;
                lines.Add($" │  {marker}  {label}");
            }
            lines.Add(Paint(" │  ↑/↓ or j/k move  ·  Enter confirm  ·  Esc cancel", "90"));
        }
        else
        {
            lines.Add(" │  " + Paint("›", "36;1") + "  " + (verificationInputLabel ?? "Verification code"));
            lines.Add(" │     " + Paint("▌ ", "36;1") +
                      (string.IsNullOrEmpty(verificationInputValue) ? Paint("Type a value", "90") : verificationInputValue));
            if (verificationNotice != null) lines.Add(" │  " + Paint("!  " + verificationNotice, "33;1"));
            lines.Add(Paint(" │  Enter confirm  ·  Esc cancel", "90"));
        }
        lines.Add(Paint(" ╰" + new string('─', width - 2) + "╯", "33"));
        return lines;
    }

    private IReadOnlyList<string> SelectVisibleStages(int budget)
    {
        int count = Math.Clamp(budget, 3, stageOrder.Length);
        if (count == stageOrder.Length) return stageOrder;
        int activeIndex = activeStage == null
            ? Math.Max(0, Array.FindLastIndex(stageOrder, stage => stages[stage] == StageState.Complete))
            : Array.IndexOf(stageOrder, activeStage);
        int start = Math.Clamp(activeIndex - count / 2, 0, stageOrder.Length - count);
        return stageOrder.Skip(start).Take(count).ToArray();
    }

    private string BuildProgressFooter(int width)
    {
        if (!uploadProgress.HasValue) return Paint(" Waiting for operation events", "90");
        int barWidth = Math.Clamp(width - 16, 8, 48);
        int complete = Math.Clamp((int)Math.Round(uploadProgress.Value * barWidth), 0, barWidth);
        return " " + Paint(new string('━', complete), "36;1") + Paint(new string('─', barWidth - complete), "90") +
               Paint("  " + (uploadProgress.Value * 100d).ToString("F0") + "%", "36;1");
    }

    private void MoveInputCursorUnsafe()
    {
        int row = Math.Max(1, CurrentSize().Height - 2);
        output.Write($"\x1b[{row};1H\x1b[2K  ");
    }

    private string OperationTitle() => operation switch
    {
        OperationMode.Meta => "WORLD META",
        OperationMode.Check => "PREFLIGHT CHECK",
        _ => "WORLD DEPLOY"
    };

    private string OperationDescription() => operation switch
    {
        OperationMode.Meta => "Update VRChat world metadata without a bundle build",
        OperationMode.Check => "Compile and inspect VRChat upload readiness without uploading",
        _ => "Build, sign, and publish a VRChat world"
    };

    private string CompletionLabel(bool success) => (operation, success) switch
    {
        (OperationMode.Meta, true) => "Metadata update complete",
        (OperationMode.Meta, false) => "Metadata update failed",
        (OperationMode.Check, true) => "Preflight check passed",
        (OperationMode.Check, false) => "Preflight check failed",
        (_, true) => "Deployment complete",
        _ => "Deployment failed"
    };

    private ConsoleKeyInfo ReadKeyInterruptibly()
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                if (Console.KeyAvailable) return Console.ReadKey(intercept: true);
            }
            catch (InvalidOperationException)
            {
                return Console.ReadKey(intercept: true);
            }
            Thread.Sleep(25);
        }
    }

    private void ShowInterruptFeedback(string message)
    {
        lock (gate)
        {
            lastMessage = message;
            RenderFrameUnsafe();
        }
    }

    private static string AlignRight(string value, int totalWidth, int visibleLength)
    {
        int spaces = Math.Max(1, totalWidth - visibleLength);
        return new string(' ', spaces) + value;
    }

    private string Paint(string value, string code) => UseColor ? "\x1b[" + code + "m" + value + "\x1b[0m" : value;

    private static bool LooksLikeError(string line) =>
        line.Contains("error CS", StringComparison.OrdinalIgnoreCase) ||
        line.Contains("Exception:", StringComparison.OrdinalIgnoreCase) ||
        line.Contains("Scripts have compiler errors", StringComparison.OrdinalIgnoreCase);

    private static bool IsIgnorableDiagnostic(string line) =>
        line.Contains("debugger-agent: Unable to listen", StringComparison.OrdinalIgnoreCase) ||
        line.Contains("Exception occured while accepting client connection", StringComparison.OrdinalIgnoreCase);

    private static bool IsTruthy(string? value) =>
        !string.IsNullOrWhiteSpace(value) &&
        !string.Equals(value, "0", StringComparison.OrdinalIgnoreCase) &&
        !string.Equals(value, "false", StringComparison.OrdinalIgnoreCase);

    private static string FormatElapsed(TimeSpan elapsed) => elapsed.TotalMinutes >= 1
        ? $"{(int)elapsed.TotalMinutes}m {elapsed.Seconds}s"
        : $"{elapsed.TotalSeconds:F1}s";

    private static int DetailCapacity(int height)
    {
        if (height < 18) return 0;
        if (height < 24) return 1;
        if (height < 30) return 2;
        if (height < 38) return 3;
        return 4;
    }

    private static bool IsNoisyDetail(string message) =>
        message.Contains("overall ", StringComparison.OrdinalIgnoreCase) ||
        message.Contains("Uploading ", StringComparison.OrdinalIgnoreCase);

    private (int Width, int Height) CurrentSize()
    {
        (int width, int height) = terminalSize();
        return (Math.Clamp(width, 32, 160), Math.Clamp(height, 10, 100));
    }

    private static (int Width, int Height) ReadTerminalSize()
    {
        try
        {
            return (Console.WindowWidth, Console.WindowHeight);
        }
        catch (IOException)
        {
            return (100, 30);
        }
    }

    private string Fit(string value, int reserved = 0)
    {
        int width = Math.Max(20, CurrentSize().Width - reserved);
        return Truncate(value, width);
    }

    private static string Truncate(string value, int width)
    {
        width = Math.Max(1, width);
        if (value.Length <= width) return value;
        return value[..Math.Max(1, width - 1)] + "…";
    }

    private static string VersionText() =>
        typeof(TerminalProgressRenderer).Assembly.GetName().Version?.ToString(3) ?? "dev";

    private static bool TryEnableVirtualTerminal()
    {
        if (!OperatingSystem.IsWindows()) return true;
        IntPtr handle = GetStdHandle(-11);
        if (handle == IntPtr.Zero || handle == new IntPtr(-1)) return false;
        if (!GetConsoleMode(handle, out uint mode)) return false;
        return SetConsoleMode(handle, mode | 0x0004);
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GetStdHandle(int standardHandle);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetConsoleMode(IntPtr consoleHandle, out uint mode);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetConsoleMode(IntPtr consoleHandle, uint mode);

    private enum StageState
    {
        Pending,
        Active,
        Complete,
        Failed
    }
}
