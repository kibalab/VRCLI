namespace KibaLab.VRCLI;

internal static class TerminalInterruptFeedback
{
    private static readonly object Gate = new();
    private static Action<string>? handler;

    public static void Attach(Action<string> feedback)
    {
        lock (Gate) handler = feedback;
    }

    public static void Detach(Action<string> feedback)
    {
        lock (Gate)
        {
            if (handler == feedback) handler = null;
        }
    }

    public static bool Show(string message)
    {
        Action<string>? current;
        lock (Gate) current = handler;
        if (current == null) return false;
        current(message);
        return true;
    }
}
