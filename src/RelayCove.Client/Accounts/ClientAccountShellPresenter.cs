using RelayCove.Client.Auth;
using RelayCove.Client.Sync;
using RelayCove.Shared.Realtime;

namespace RelayCove.Client.Accounts;

internal static class ClientAccountShellPresenter
{
    public static string DescribeNotificationAvailability(bool? isAvailable) =>
        isAvailable switch
        {
            true => "系统通知：可用",
            false => "系统通知：不可用（账户仍可使用）",
            null => "系统通知：初始化中",
        };

    public static ClientAccountShellPresentation Present(ClientAccountShellSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var showLogin = snapshot.Phase is
            ClientAccountShellPhase.SignedOut or
            ClientAccountShellPhase.Restoring or
            ClientAccountShellPhase.SigningIn;
        var isBusy = snapshot.Phase is
            ClientAccountShellPhase.Restoring or
            ClientAccountShellPhase.SigningIn or
            ClientAccountShellPhase.Starting or
            ClientAccountShellPhase.Retrying or
            ClientAccountShellPhase.SigningOut or
            ClientAccountShellPhase.Stopping;
        var (heading, detail) = Describe(snapshot);
        return new ClientAccountShellPresentation(
            showLogin,
            isBusy,
            heading,
            detail,
            snapshot.DisplayName ?? string.Empty,
            snapshot.ServerBaseUri?.AbsoluteUri ?? string.Empty,
            DescribeConnection(snapshot.ConnectionState),
            DescribeSync(snapshot.LastSyncStatus),
            snapshot.Phase == ClientAccountShellPhase.Active,
            snapshot.Phase is ClientAccountShellPhase.Active or
                ClientAccountShellPhase.Retrying);
    }

    private static (string Heading, string Detail) Describe(
        ClientAccountShellSnapshot snapshot) =>
        snapshot.Phase switch
        {
            ClientAccountShellPhase.Restoring =>
                ("正在恢复账户", "正在安全读取当前 Windows 用户的登录凭据。"),
            ClientAccountShellPhase.SigningIn =>
                ("正在登录", "正在验证账户并建立安全会话。"),
            ClientAccountShellPhase.Starting =>
                ("正在准备账户", "正在打开账户缓存、实时连接并执行首次同步。"),
            ClientAccountShellPhase.Active =>
                ("账户已就绪", DescribeActive(snapshot)),
            ClientAccountShellPhase.Retrying =>
                ("正在重新连接", "正在重试实时连接并重新同步权威状态。"),
            ClientAccountShellPhase.SigningOut =>
                ("正在退出账户", "正在撤销通知入口并清理本地会话。"),
            ClientAccountShellPhase.Stopping =>
                ("正在退出 RelayCove", "正在安全释放账户资源。"),
            _ => DescribeSignedOut(snapshot),
        };

    private static (string Heading, string Detail) DescribeSignedOut(
        ClientAccountShellSnapshot snapshot)
    {
        var retrySuffix = snapshot.RetryAfter is { } retryAfter
            ? $" 建议约 {Math.Max(1, (int)Math.Ceiling(retryAfter.TotalSeconds))} 秒后重试。"
            : string.Empty;
        var detail = snapshot.AuthenticationStatus switch
        {
            PersistentClientAuthenticationStatus.NoStoredCredential =>
                "输入服务器地址和账户凭据以开始。",
            PersistentClientAuthenticationStatus.ValidationFailed =>
                "请检查服务器地址、用户名和密码格式。",
            PersistentClientAuthenticationStatus.AuthenticationFailed =>
                "服务器未接受该账户凭据，请重新输入。",
            PersistentClientAuthenticationStatus.RateLimited =>
                "登录请求过于频繁。" + retrySuffix,
            PersistentClientAuthenticationStatus.ServiceUnavailable =>
                "服务器暂时不可用，请稍后重试。",
            PersistentClientAuthenticationStatus.CredentialCorrupt =>
                "保存的登录凭据已损坏，请重新登录。",
            PersistentClientAuthenticationStatus.CredentialUnavailable =>
                "当前无法读取保存的登录凭据，请手动登录。",
            PersistentClientAuthenticationStatus.ProtocolError =>
                "服务器响应与当前客户端不兼容。",
            PersistentClientAuthenticationStatus.SessionAlreadyActive =>
                "已有账户会话正在收敛，请稍后重试。",
            PersistentClientAuthenticationStatus.RemoteFailure =>
                "未能连接服务器，请检查网络后重试。",
            _ when snapshot.LastLogoutStatus is not null =>
                "账户已退出，可登录其他账户。",
            _ => "输入服务器地址和账户凭据以开始。",
        };
        if (snapshot.LastLogoutStatus == ClientLogoutStatus.CredentialClearFailed)
        {
            detail += " 本地凭据清理未完全成功，请检查当前用户的应用数据目录写入权限。";
        }

        return ("登录 RelayCove", detail);
    }

    private static string DescribeActive(ClientAccountShellSnapshot snapshot) =>
        snapshot.LastSyncStatus switch
        {
            ClientSyncRunStatus.Completed => "权威状态已同步，可以接收通知。",
            ClientSyncRunStatus.AuthenticationRequired =>
                "服务器要求重新认证；请退出后重新登录。",
            ClientSyncRunStatus.CursorInvalid =>
                "同步游标已失效，请重试以重建权威状态。",
            ClientSyncRunStatus.LocalCacheFailure =>
                "本地账户缓存暂不可用，通知导航保持关闭。",
            _ => "账户已打开；同步尚未完成，可使用重试再次连接。",
        };

    private static string DescribeConnection(ConnectionState state) => state switch
    {
        ConnectionState.Connected => "已连接",
        ConnectionState.Connecting => "连接中",
        ConnectionState.Reconnecting => "重连中",
        _ => "未连接",
    };

    private static string DescribeSync(ClientSyncRunStatus? status) => status switch
    {
        ClientSyncRunStatus.Completed => string.Empty,
        ClientSyncRunStatus.AuthenticationRequired => "需要重新登录",
        ClientSyncRunStatus.TransientFailure => "同步暂时失败",
        ClientSyncRunStatus.ProtocolError => "同步协议错误",
        ClientSyncRunStatus.CursorInvalid => "同步需要重建",
        ClientSyncRunStatus.LocalCacheFailure => "本地缓存错误",
        ClientSyncRunStatus.RemoteFailure => "远端同步失败",
        ClientSyncRunStatus.Canceled => "同步已取消",
        _ => "正在同步",
    };
}
