namespace RelayCove.Updater;

public static class Program
{
    public static int Main(string[] args)
    {
        return UpdaterApplication.Run(args, new SystemUpdaterPlatform());
    }
}
