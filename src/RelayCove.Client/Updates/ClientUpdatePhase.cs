namespace RelayCove.Client.Updates;

internal enum ClientUpdatePhase
{
    Idle,
    Checking,
    NoUpdate,
    OptionalAvailable,
    MandatoryAvailable,
    Downloading,
    Downloaded,
    Failed,
}
