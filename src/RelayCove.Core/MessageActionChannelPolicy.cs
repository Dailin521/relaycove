namespace RelayCove.Core;

public sealed record MessageActionChannelPolicy(bool IsArchived, bool CanDeleteOwn, bool CanDeleteAny);
