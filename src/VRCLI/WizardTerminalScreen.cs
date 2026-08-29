using System.Text;

namespace KibaLab.WorldDeployment;

internal sealed class WizardTerminalScreen : IDisposable
{
    private static readonly object RetainedGate = new();
    private static bool retainedScreen;
    private readonly object renderGate = new();
    private readonly CancellationTokenSource resizeMonitorCancellation = new();
    private readonly List<string> previousFrame = [];
    private (int Width, int Height)? lastSize;
    private readonly List<(string Label, string Value)> summary = [];
    private readonly CancellationToken cancellationToken;
    private IReadOnlyList<string> context = [];
    private IReadOnlyList<string> route = ["ACCOUNT", "DEPLOYMENT", "REVIEW"];
    private string step = "01";
    private string title = "ACCOUNT";
    private string description = "Verify the publisher identity.";
    private string? notice;
    private string? busyMessage;
    private string? promptLabel;
    private string? promptValue;
    private string? promptHint;
    private IReadOnlyList<string>? choices;
    private int selectedChoice;
    private bool entered;
    private bool retainOnDispose;
    private Task? resizeMonitor;
    private volatile string? interruptMessage;

    private bool UseColor => Environment.GetEnvironmentVariable("NO_COLOR") == null;

    public WizardTerminalScreen(CancellationToken cancellationToken = default)
    {
        this.cancellationToken = cancellationToken;
    }

    public void Enter()
    {
        if (entered) return;
        entered = true;
        TerminalInterruptFeedback.Attach(ShowInterruptFeedback);
        Console.Write("\x1b[?1049h\x1b[?25l\x1b[2J\x1b[H");
        Render();
        resizeMonitor = MonitorResizeAsync(resizeMonitorCancellation.Token);
    }

    public void SetRoute(params string[] labels)
    {
        if (labels.Length == 0) throw new ArgumentException("A wizard route requires at least one section.", nameof(labels));
        route = labels;
        Render();
    }

    public void SetSection(string number, string sectionTitle, string sectionDescription)
    {
        step = number;
        title = sectionTitle;
        description = sectionDescription;
        context = [];
        notice = null;
        busyMessage = null;
        ClearPrompt();
        Render();
    }

    public void SetContext(IEnumerable<string> lines)
    {
        context = lines.ToArray();
        Render();
    }

    public void AddSummary(string label, string value)
    {
        lock (renderGate)
        {
            int existing = summary.FindIndex(row => string.Equals(row.Label, label, StringComparison.Ordinal));
            if (existing >= 0) summary[existing] = (label, value);
            else summary.Add((label, value));
            busyMessage = null;
            notice = null;
            Render();
        }
    }

    public void SetBusy(string message)
    {
        busyMessage = message;
        notice = null;
        ClearPrompt();
        Render();
    }

    public void SetNotice(string message)
    {
        notice = message;
        busyMessage = null;
        Render();
    }

    public string ReadText(string label, string? defaultValue, bool secret, bool acceptEmpty = false)
    {
        StringBuilder value = new();
        promptLabel = label;
        promptHint = defaultValue == null
            ? "Enter confirm  ·  Esc or Ctrl+C ×2 cancel"
            : $"Enter confirm  ·  default: {defaultValue}  ·  Esc or Ctrl+C ×2 cancel";
        choices = null;
        notice = null;
        busyMessage = null;
        while (true)
        {
            promptValue = secret ? new string('•', value.Length) : value.ToString();
            Render();
            ConsoleKeyInfo key = ReadKeyInterruptibly();
            if (key.Key == ConsoleKey.Escape) throw new OperationCanceledException("Wizard cancelled.");
            if (key.Key == ConsoleKey.Enter)
            {
                string result = value.Length == 0 && !acceptEmpty ? defaultValue ?? string.Empty : value.ToString();
                ClearPrompt();
                Render();
                return result.Trim().Trim('"');
            }
            if (key.Key == ConsoleKey.Backspace)
            {
                if (value.Length > 0) value.Length--;
                continue;
            }
            if (!char.IsControl(key.KeyChar)) value.Append(key.KeyChar);
        }
    }

    public int ReadChoice(string label, IReadOnlyList<string> options)
    {
        promptLabel = label;
        promptHint = "↑/↓ or j/k move  ·  Enter confirm  ·  Esc or Ctrl+C ×2 cancel";
        promptValue = null;
        choices = options;
        selectedChoice = 0;
        notice = null;
        busyMessage = null;
        while (true)
        {
            Render();
            ConsoleKeyInfo key = ReadKeyInterruptibly();
            if (key.Key == ConsoleKey.Escape) throw new OperationCanceledException("Wizard cancelled.");
            if (key.Key == ConsoleKey.Enter)
            {
                int result = selectedChoice;
                ClearPrompt();
                Render();
                return result;
            }
            if (key.Key is ConsoleKey.UpArrow || key.KeyChar is 'k' or 'K')
                selectedChoice = (selectedChoice - 1 + options.Count) % options.Count;
            else if (key.Key is ConsoleKey.DownArrow || key.KeyChar is 'j' or 'J')
                selectedChoice = (selectedChoice + 1) % options.Count;
            else if (char.IsDigit(key.KeyChar))
            {
                int numeric = key.KeyChar - '1';
                if (numeric >= 0 && numeric < options.Count) selectedChoice = numeric;
            }
        }
    }

    public bool ReadYesNo(string label, bool defaultValue)
    {
        int selected = ReadChoice(label, defaultValue ? ["Yes", "No"] : ["No", "Yes"]);
        return defaultValue ? selected == 0 : selected == 1;
    }

    public void ShowReview(IReadOnlyList<(string Label, string Value)> rows)
    {
        lock (renderGate)
        {
            summary.Clear();
            summary.AddRange(rows);
            context = [];
            notice = null;
            busyMessage = null;
            ClearPrompt();
            Render();
        }
    }

    public void RetainForOperation()
    {
        if (!entered) return;
        lock (RetainedGate) retainedScreen = true;
        retainOnDispose = true;
    }

    public static bool ConsumeRetainedScreen()
    {
        lock (RetainedGate)
        {
            if (!retainedScreen) return false;
            retainedScreen = false;
            return true;
        }
    }

    public static void CloseRetainedScreen()
    {
        lock (RetainedGate)
        {
            if (!retainedScreen) return;
            retainedScreen = false;
            Console.Write("\x1b[?25h\x1b[?1049l");
            Console.Out.Flush();
        }
    }

    public void Dispose()
    {
        if (!entered) return;
        entered = false;
        resizeMonitorCancellation.Cancel();
        if (resizeMonitor != null)
        {
            try
            {
                resizeMonitor.GetAwaiter().GetResult();
            }
            catch (OperationCanceledException)
            {
            }
        }
        TerminalInterruptFeedback.Detach(ShowInterruptFeedback);
        if (retainOnDispose) return;
        Console.Write("\x1b[?25h\x1b[?1049l");
        Console.Out.Flush();
    }

    private void Render(bool force = false)
    {
        lock (renderGate)
        {
            if (!entered) return;
            (int width, int height) = Size();
            bool resized = lastSize.HasValue && lastSize.Value != (width, height);
            lastSize = (width, height);
            if (resized)
            {
                Console.Write("\x1b[2J\x1b[H");
                previousFrame.Clear();
                force = true;
            }
            List<string> frame = [];
            string routeText = width >= 64
                ? string.Join(
                    Paint("  →  ", "90"),
                    route.Select((label, index) => Step(
                        (index + 1).ToString("00"),
                        label)))
                : Step(step, title);
            IReadOnlyList<string> logo = height >= 24 ? Branding.Fit(width - 2) : ["VRCLI"];
            bool fullLogo = logo.Count > 1;
            if (fullLogo)
            {
                foreach (string line in logo) frame.Add(" " + Paint(line, "36;1"));
                frame.Add(" " + Paint(Branding.FooterLine, "90;1"));
                frame.Add(string.Empty);
                frame.Add(" " + routeText);
            }
            else
            {
                frame.Add(Paint(" ◆ VRCLI", "36;1") + Paint("  /  ", "90") + routeText);
            }
            frame.Add(Paint(" " + new string('─', width - 2), "90"));
            frame.Add(string.Empty);
            frame.Add(" " + Paint(Truncate(title, width - 2), "1"));
            frame.Add(Paint("    " + Truncate(description, width - 5), "90"));
            frame.Add(string.Empty);

            int summaryLimit = Math.Max(1, height - 17 - (fullLogo ? 6 : 0) - (choices?.Count ?? 0));
            foreach ((string label, string value) in summary.TakeLast(summaryLimit))
                frame.Add(" " + Paint("✓", "32;1") + "  " + Paint(TerminalText.PadRight(label, 11), "90") + Truncate(value, width - 18));

            if (context.Count > 0)
            {
                frame.Add(string.Empty);
                foreach (string line in context.Take(ContextCapacity(height)))
                    frame.Add(Paint("    " + Truncate(line, width - 5), "90"));
            }

            if (busyMessage != null)
            {
                frame.Add(string.Empty);
                frame.Add(" " + Paint("⠋", "36;1") + "  " + Truncate(busyMessage, width - 6));
            }
            if (notice != null)
            {
                frame.Add(string.Empty);
                frame.Add(" " + Paint("!", "33;1") + "  " + Truncate(notice, width - 6));
            }

            int visibleChoiceCount = choices == null
                ? 0
                : Math.Min(choices.Count, Math.Max(1, height - 12));
            int promptHeight = choices == null ? 5 : visibleChoiceCount + 5;
            while (frame.Count < height - promptHeight) frame.Add(string.Empty);
            if (frame.Count > height - promptHeight) frame.RemoveRange(height - promptHeight, frame.Count - (height - promptHeight));

            frame.Add(Paint(" " + new string('─', width - 2), "90"));
            if (promptLabel == null)
            {
                frame.Add(Paint(" Waiting for input…", "90"));
            }
            else if (choices == null)
            {
                frame.Add(" " + Paint("›", "36;1") + "  " + Truncate(promptLabel, width - 6));
                bool empty = string.IsNullOrEmpty(promptValue);
                string display = Truncate(empty ? "Type a value" : promptValue!, width - 7);
                frame.Add("    " + Paint("▌ ", "36;1") + (empty ? Paint(display, "90") : display));
            }
            else
            {
                frame.Add(" " + Paint("?", "36;1") + "  " + Truncate(promptLabel, width - 6));
                int firstChoice = Math.Clamp(
                    selectedChoice - visibleChoiceCount / 2,
                    0,
                    Math.Max(0, choices.Count - visibleChoiceCount));
                for (int index = firstChoice; index < firstChoice + visibleChoiceCount; index++)
                {
                    string marker = index == selectedChoice ? Paint("❯", "36;1") : " ";
                    string fittedChoice = Truncate(choices[index], width - 7);
                    string choice = index == selectedChoice ? Paint(fittedChoice, "36;1") : fittedChoice;
                    frame.Add($"   {marker}  {choice}");
                }
            }
            frame.Add(Paint(" " + (promptHint ?? "Esc or Ctrl+C ×2 cancel"), "90"));
            frame.Add(Paint(" " + new string('─', width - 2), "90"));

            frame = frame.Take(height).ToList();
            int rows = Math.Max(frame.Count, previousFrame.Count);
            for (int index = 0; index < rows; index++)
            {
                string next = index < frame.Count ? frame[index] : string.Empty;
                string previous = index < previousFrame.Count ? previousFrame[index] : string.Empty;
                if (!force && string.Equals(next, previous, StringComparison.Ordinal)) continue;
                Console.Write($"\x1b[{index + 1};1H\x1b[2K{next}");
            }
            previousFrame.Clear();
            previousFrame.AddRange(frame);
            Console.Out.Flush();
        }
    }

    private async Task MonitorResizeAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            await Task.Delay(100, cancellationToken);
            if (lastSize != Size()) Render(force: true);
        }
    }

    private string Step(string number, string label) => number == step
        ? Paint(number + " " + label, "36;1")
        : Paint(number + " " + label, "90");

    private static int ContextCapacity(int height)
    {
        if (height < 18) return 0;
        if (height < 24) return 1;
        if (height < 30) return 2;
        if (height < 38) return 3;
        return 4;
    }

    private void ClearPrompt()
    {
        promptLabel = null;
        promptValue = null;
        promptHint = null;
        choices = null;
    }

    private ConsoleKeyInfo ReadKeyInterruptibly()
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (lastSize != Size()) Render(force: true);
            string? message = interruptMessage;
            if (message != null)
            {
                interruptMessage = null;
                notice = message;
                Render();
            }
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
        interruptMessage = message;
    }

    private string Paint(string value, string code) => UseColor ? "\x1b[" + code + "m" + value + "\x1b[0m" : value;

    private static string Truncate(string value, int width)
    {
        width = Math.Max(1, width);
        return TerminalText.Truncate(value, width);
    }

    private static (int Width, int Height) Size()
    {
        try
        {
            return (Math.Clamp(Console.WindowWidth, 40, 140), Math.Clamp(Console.WindowHeight, 16, 80));
        }
        catch (IOException)
        {
            return (100, 30);
        }
    }
}
