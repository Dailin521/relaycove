using System.ComponentModel;

namespace RelayCove.Updater;

internal static class UpdaterApplication
{
    internal static int Run(string[] args, IUpdaterPlatform platform)
    {
        if (UpdaterArgumentParser.IsHelp(args))
        {
            Console.Out.WriteLine(UpdaterArgumentParser.HelpText);
            return (int)UpdaterExitCode.Success;
        }

        if (!UpdaterArgumentParser.TryParse(args, out var options))
        {
            Console.Error.WriteLine("Invalid updater arguments.");
            return (int)UpdaterExitCode.InvalidArguments;
        }

        try
        {
            return RunApply(options!, platform);
        }
        catch (InvalidDataException)
        {
            Console.Error.WriteLine("Update validation failed.");
            return (int)UpdaterExitCode.ValidationFailed;
        }
        catch (UnauthorizedAccessException)
        {
            Console.Error.WriteLine("Update could not be applied.");
            return (int)UpdaterExitCode.ApplyFailed;
        }
        catch (IOException)
        {
            Console.Error.WriteLine("Update could not be applied.");
            return (int)UpdaterExitCode.ApplyFailed;
        }
        catch (InvalidOperationException)
        {
            Console.Error.WriteLine("Update could not be applied.");
            return (int)UpdaterExitCode.ApplyFailed;
        }
        catch (Win32Exception)
        {
            Console.Error.WriteLine("Update could not be applied.");
            return (int)UpdaterExitCode.ApplyFailed;
        }
    }

    private static int RunApply(UpdaterOptions options, IUpdaterPlatform platform)
    {
        var layout = UpdateLayout.Create(options, platform.ExecutablePath);
        if (!options.Bootstrapped && layout.IsExecutableInsideTarget)
        {
            layout.StartBootstrap(options, platform);
            return (int)UpdaterExitCode.Success;
        }

        if (options.Bootstrapped && layout.IsExecutableInsideTarget)
        {
            throw new InvalidDataException("Bootstrap location is invalid.");
        }

        using (layout.AcquireLock())
        {
            layout.RecoverIfNecessary();
            layout.ValidateInputs(options.ArchivePath, options.CurrentVersion.ToString());
            WaitForClient(options, platform);
            var staging = layout.CreateStaging();
            try
            {
                new PortablePackageValidator().ValidateAndExtract(options, staging);
                layout.Activate(staging);
                layout.MarkLaunchIntent();
                try
                {
                    platform.Start(Path.Combine(options.TargetPath, "RelayCove.Client.exe"), Array.Empty<string>(), options.TargetPath);
                }
                catch
                {
                    layout.RestoreAfterLaunchFailure();
                    throw;
                }
                layout.Complete();
                Console.Out.WriteLine("Update applied.");
                return (int)UpdaterExitCode.Success;
            }
            finally
            {
                if (Directory.Exists(staging))
                {
                    Directory.Delete(staging, true);
                }
            }
        }
    }

    private static void WaitForClient(UpdaterOptions options, IUpdaterPlatform platform)
    {
        if (!platform.ProcessMatches(options.WaitProcessId, options.WaitProcessStartTimeUtcTicks))
        {
            if (!platform.IsProcessRunning(options.WaitProcessId))
            {
                return;
            }

            throw new InvalidDataException("Client process identity is invalid.");
        }

        var deadline = DateTime.UtcNow.AddSeconds(options.WaitTimeoutSeconds);
        while (platform.IsProcessRunning(options.WaitProcessId))
        {
            if (DateTime.UtcNow >= deadline)
            {
                throw new InvalidOperationException("Client did not exit.");
            }

            Thread.Sleep(100);
        }
    }
}
