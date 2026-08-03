using Microsoft.Extensions.Logging;
using RelayCove.Client.Notifications;

namespace RelayCove.Client.Activation;

internal sealed class ClientNotificationActivationRouter : IDisposable
{
    private const int DefaultCompletedTargetLimit = 64;
    private static readonly TimeSpan DefaultCompletedTargetTtl = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan DefaultPendingTargetTtl = TimeSpan.FromMinutes(2);
    private readonly object stateGate = new();
    private readonly Action<ClientNotificationActivationTarget> navigationSink;
    private readonly Action windowActivationSink;
    private readonly ILogger<ClientNotificationActivationRouter> logger;
    private readonly TimeProvider timeProvider;
    private readonly int completedTargetLimit;
    private readonly TimeSpan completedTargetTtl;
    private readonly TimeSpan pendingTargetTtl;
    private readonly HashSet<ClientNotificationActivationTarget> completedTargets = [];
    private readonly Queue<CompletedTarget> completedTargetOrder = new();
    private ActiveAccount? activeAccount;
    private PendingTarget? pendingTarget;
    private bool disposed;

    public ClientNotificationActivationRouter(
        Action<ClientNotificationActivationTarget> navigationSink,
        ILogger<ClientNotificationActivationRouter> logger,
        int completedTargetLimit = DefaultCompletedTargetLimit,
        Action? windowActivationSink = null,
        TimeProvider? timeProvider = null,
        TimeSpan? completedTargetTtl = null,
        TimeSpan? pendingTargetTtl = null)
    {
        this.navigationSink = navigationSink ??
            throw new ArgumentNullException(nameof(navigationSink));
        this.windowActivationSink = windowActivationSink ?? (() => { });
        this.logger = logger ?? throw new ArgumentNullException(nameof(logger));
        this.timeProvider = timeProvider ?? TimeProvider.System;
        if (completedTargetLimit <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(completedTargetLimit));
        }

        this.completedTargetLimit = completedTargetLimit;
        this.completedTargetTtl = completedTargetTtl ?? DefaultCompletedTargetTtl;
        this.pendingTargetTtl = pendingTargetTtl ?? DefaultPendingTargetTtl;
        if (this.completedTargetTtl <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(completedTargetTtl));
        }

        if (this.pendingTargetTtl <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(pendingTargetTtl));
        }
    }

    public IDisposable ActivateAccount(
        string accountScopeId,
        Func<ClientNotificationActivationTarget, bool> targetAuthorizer)
    {
        WindowsNotificationIdentity.ValidateAccountScopeId(accountScopeId);
        ArgumentNullException.ThrowIfNull(targetAuthorizer);
        var account = new ActiveAccount(accountScopeId, targetAuthorizer);
        ClientNotificationActivationTarget? targetToReplay = null;
        lock (stateGate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            activeAccount = account;
            ClearCompletedTargets();
            var now = timeProvider.GetUtcNow();
            if (pendingTarget is { } pending)
            {
                if (pending.ExpiresAt > now &&
                    string.Equals(
                        pending.Target.AccountScopeId,
                        accountScopeId,
                        StringComparison.Ordinal))
                {
                    targetToReplay = pending.Target;
                }
                else
                {
                    pendingTarget = null;
                }
            }
        }

        var lease = new ActiveAccountLease(this, account);
        if (targetToReplay is not null)
        {
            _ = TryRoute(targetToReplay);
        }

        return lease;
    }

    public ClientNotificationActivationRouteStatus TryRoute(
        ClientNotificationActivationTarget target)
    {
        ArgumentNullException.ThrowIfNull(target);
        lock (stateGate)
        {
            if (disposed)
            {
                return ClientNotificationActivationRouteStatus.Stopping;
            }

            var account = activeAccount;
            if (account is null)
            {
                pendingTarget = new PendingTarget(
                    target,
                    timeProvider.GetUtcNow() + pendingTargetTtl);
                logger.LogWarning(
                    "A notification activation was parked because no account is active.");
                return ClientNotificationActivationRouteStatus.NoActiveAccount;
            }

            if (!string.Equals(
                    account.AccountScopeId,
                    target.AccountScopeId,
                    StringComparison.Ordinal))
            {
                logger.LogWarning(
                    "A notification activation was rejected for a different account.");
                return ClientNotificationActivationRouteStatus.AccountMismatch;
            }

            bool isAuthorized;
            try
            {
                isAuthorized = account.TargetAuthorizer(target);
            }
            catch (Exception exception)
            {
                logger.LogError(
                    "Checking notification activation access failed; " +
                    "errorType={ErrorType}.",
                    exception.GetType().Name);
                return ClientNotificationActivationRouteStatus.AccessDenied;
            }

            if (!isAuthorized)
            {
                logger.LogWarning(
                    "A notification activation was rejected by current access state.");
                return ClientNotificationActivationRouteStatus.AccessDenied;
            }

            try
            {
                windowActivationSink();
            }
            catch (Exception exception)
            {
                logger.LogError(
                    "Activating the window for an authorized notification failed; " +
                    "errorType={ErrorType}.",
                    exception.GetType().Name);
                return ClientNotificationActivationRouteStatus.NavigationFailed;
            }

            var now = timeProvider.GetUtcNow();
            PruneCompletedTargets(now);
            if (completedTargets.Contains(target))
            {
                ClearPendingTarget(target);
                logger.LogDebug(
                    "A duplicate notification navigation was ignored after window activation.");
                return ClientNotificationActivationRouteStatus.Duplicate;
            }

            try
            {
                navigationSink(target);
            }
            catch (Exception exception)
            {
                logger.LogError(
                    "Dispatching an authorized notification navigation failed; " +
                    "errorType={ErrorType}.",
                    exception.GetType().Name);
                return ClientNotificationActivationRouteStatus.NavigationFailed;
            }

            RememberCompletedTarget(target);
            ClearPendingTarget(target);
            return ClientNotificationActivationRouteStatus.Accepted;
        }
    }

    public void Dispose()
    {
        lock (stateGate)
        {
            if (disposed)
            {
                return;
            }

            disposed = true;
            activeAccount = null;
            pendingTarget = null;
            ClearCompletedTargets();
        }
    }

    private void RememberCompletedTarget(ClientNotificationActivationTarget target)
    {
        completedTargets.Add(target);
        completedTargetOrder.Enqueue(new CompletedTarget(
            target,
            timeProvider.GetUtcNow() + completedTargetTtl));
        while (completedTargetOrder.Count > completedTargetLimit)
        {
            completedTargets.Remove(completedTargetOrder.Dequeue().Target);
        }
    }

    private void PruneCompletedTargets(DateTimeOffset now)
    {
        while (completedTargetOrder.TryPeek(out var completed) &&
               completed.ExpiresAt <= now)
        {
            completedTargetOrder.Dequeue();
            completedTargets.Remove(completed.Target);
        }
    }

    private void ClearAccount(ActiveAccount account)
    {
        lock (stateGate)
        {
            if (ReferenceEquals(activeAccount, account))
            {
                activeAccount = null;
                ClearCompletedTargets();
            }
        }
    }

    private void ClearCompletedTargets()
    {
        completedTargets.Clear();
        completedTargetOrder.Clear();
    }

    private void ClearPendingTarget(ClientNotificationActivationTarget target)
    {
        if (pendingTarget?.Target == target)
        {
            pendingTarget = null;
        }
    }

    private sealed record ActiveAccount(
        string AccountScopeId,
        Func<ClientNotificationActivationTarget, bool> TargetAuthorizer);

    private sealed record CompletedTarget(
        ClientNotificationActivationTarget Target,
        DateTimeOffset ExpiresAt);

    private sealed record PendingTarget(
        ClientNotificationActivationTarget Target,
        DateTimeOffset ExpiresAt);

    private sealed class ActiveAccountLease : IDisposable
    {
        private ClientNotificationActivationRouter? owner;
        private readonly ActiveAccount account;

        public ActiveAccountLease(
            ClientNotificationActivationRouter owner,
            ActiveAccount account)
        {
            this.owner = owner;
            this.account = account;
        }

        public void Dispose() =>
            Interlocked.Exchange(ref owner, null)?.ClearAccount(account);
    }
}
