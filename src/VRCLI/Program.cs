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

        if (InteractiveMetadataEditor.ShouldStart(args))
        {
            try
            {
                return await InteractiveMetadataEditor.RunAsync(args, cancellation.Token);
            }
            finally
            {
                WizardTerminalScreen.CloseRetainedScreen();
            }
        }

        InteractiveWizardResult? wizard = null;
        if (InteractiveWizard.ShouldStart(args))
        {
            try
            {
                wizard = await InteractiveWizard.RunAsync(args, cancellation.Token);
            }
            catch (VrchatCredentialException exception)
            {
                Console.Error.WriteLine("  × Account validation failed");
                Console.Error.WriteLine("    " + exception.Message);
                return ExitCodes.AuthenticationFailed;
            }
            if (wizard == null)
            {
                Console.WriteLine("  ◇ " + InteractiveWizard.CancellationMessage(args));
                return ExitCodes.Success;
            }
            args = wizard.Arguments;
            InteractiveWizard.ApplySecrets(wizard.TemporarySecrets);
        }

        try
        {
            DeploymentApplication application = new(Console.Out, Console.Error);
            return await application.RunAsync(args, cancellation.Token);
        }
        finally
        {
            if (wizard != null) InteractiveWizard.ClearSecrets(wizard.TemporarySecrets);
            WizardTerminalScreen.CloseRetainedScreen();
        }
    }
}
