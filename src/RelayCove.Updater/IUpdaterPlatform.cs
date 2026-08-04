using System.Diagnostics;

namespace RelayCove.Updater;

internal interface IUpdaterPlatform
{
    string ExecutablePath { get; }

    bool ProcessMatches(int processId, long startTimeUtcTicks);

    bool IsProcessRunning(int processId);

    void Start(string executablePath, IEnumerable<string> arguments, string workingDirectory);
}
