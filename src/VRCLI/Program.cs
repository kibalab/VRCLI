namespace KibaLab.WorldDeployment;

internal static class Program
{
    public static async Task<int> Main(string[] args)
    {
        Console.OutputEncoding = new System.Text.UTF8Encoding(false);
        using CancellationTokenSource cancellation = new();
        DateTimeOffset? firstInterrupt = null;
        object interruptGate = new();
        Console.CancelKeyPress += (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            lock (interruptGate)
            {
                DateTimeOffset now = DateTimeOffset.UtcNow;
                if (firstInterrupt.HasValue && now - firstInterrupt.Value <= TimeSpan.FromSeconds(30))
                {
                    cancellation.Cancel();
                    TerminalInterruptFeedback.Show("Cancelling…");
                    firstInterrupt = null;
                    return;
                }

                firstInterrupt = now;
                if (!TerminalInterruptFeedback.Show("Press Ctrl+C again to cancel."))
                    Console.Error.WriteLine("VRCLI: Press Ctrl+C again to cancel.");
            }
        };

        InteractiveWizardResult? wizard = null;
        if (InteractiveDeployWizard.ShouldStart(args))
        {
            try
            {
                wizard = InteractiveDeployWizard.Run(cancellation.Token);
            }
            catch (VrchatCredentialException exception)
            {
                Console.Error.WriteLine("  × Account validation failed");
                Console.Error.WriteLine("    " + exception.Message);
                return ExitCodes.AuthenticationFailed;
            }
            if (wizard == null)
            {
                Console.WriteLine("  ◇ Deployment cancelled. No build or upload was started.");
                return ExitCodes.Success;
            }
            args = wizard.Arguments;
            InteractiveDeployWizard.ApplySecrets(wizard.TemporarySecrets);
        }

        try
        {
            DeploymentApplication application = new(Console.Out, Console.Error);
            return await application.RunAsync(args, cancellation.Token);
        }
        finally
        {
            if (wizard != null) InteractiveDeployWizard.ClearSecrets(wizard.TemporarySecrets);
            WizardTerminalScreen.CloseRetainedScreen();
        }
    }
}
