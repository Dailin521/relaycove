using System.ComponentModel;
using System.Runtime.CompilerServices;
using RelayCove.Client.Sync;

namespace RelayCove.Client.Attachments;

internal sealed class ClientAttachmentDownloadViewState : INotifyPropertyChanged
{
    private ClientAttachmentDownloadPhase phase;
    private int percent;
    private int announcedProgressBucket;
    private string statusText;

    public ClientAttachmentDownloadViewState(
        ClientAttachmentDownloadContext context,
        string displayName,
        bool isDownloaded)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (string.IsNullOrWhiteSpace(displayName))
        {
            throw new ArgumentException("A display name is required.", nameof(displayName));
        }

        Context = context;
        DisplayName = displayName;
        phase = isDownloaded
            ? ClientAttachmentDownloadPhase.Downloaded
            : ClientAttachmentDownloadPhase.Idle;
        percent = isDownloaded ? 100 : 0;
        announcedProgressBucket = isDownloaded ? 10 : 0;
        statusText = isDownloaded ? "已下载。" : "尚未下载。";
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public ClientAttachmentDownloadContext Context { get; }

    public string DisplayName { get; }

    public ClientAttachmentDownloadPhase Phase
    {
        get => phase;
        private set
        {
            if (!SetField(ref phase, value))
            {
                return;
            }

            RaisePhaseDependentPropertiesChanged();
        }
    }

    public int Percent
    {
        get => percent;
        private set => SetField(ref percent, value);
    }

    public string StatusText
    {
        get => statusText;
        private set => SetField(ref statusText, value);
    }

    public ClientAttachmentDownloadAction Action =>
        Phase switch
        {
            ClientAttachmentDownloadPhase.Downloading or
                ClientAttachmentDownloadPhase.Canceling => ClientAttachmentDownloadAction.Cancel,
            ClientAttachmentDownloadPhase.Downloaded => ClientAttachmentDownloadAction.ShowInFolder,
            _ => ClientAttachmentDownloadAction.Download,
        };

    public string ActionLabel =>
        Phase switch
        {
            ClientAttachmentDownloadPhase.Failed => "重试",
            ClientAttachmentDownloadPhase.Downloading => "取消",
            ClientAttachmentDownloadPhase.Canceling => "正在取消…",
            ClientAttachmentDownloadPhase.Downloaded => "在文件夹中显示",
            _ => "下载",
        };

    public bool CanInvoke => Phase != ClientAttachmentDownloadPhase.Canceling;

    public bool ShowProgress =>
        Phase is ClientAttachmentDownloadPhase.Downloading or ClientAttachmentDownloadPhase.Canceling;

    public string AutomationName =>
        Phase switch
        {
            ClientAttachmentDownloadPhase.Failed => $"重试下载附件：{DisplayName}",
            ClientAttachmentDownloadPhase.Downloading => $"取消下载附件：{DisplayName}",
            ClientAttachmentDownloadPhase.Canceling => $"正在取消附件下载：{DisplayName}",
            ClientAttachmentDownloadPhase.Downloaded => $"在文件夹中显示附件：{DisplayName}",
            _ => $"下载附件：{DisplayName}",
        };

    public bool TryBeginDownload(
        bool ready,
        Guid? currentConversationId,
        Guid? currentMessageClientId,
        Guid? currentAttachmentId,
        long currentContextVersion,
        out ClientAttachmentDownloadFlight? flight)
    {
        flight = null;
        if (Action != ClientAttachmentDownloadAction.Download ||
            !CanInvoke ||
            !ClientAttachmentDownloadViewPolicy.IsCurrentContext(
                ready,
                Context,
                currentConversationId,
                currentMessageClientId,
                currentAttachmentId,
                currentContextVersion))
        {
            return false;
        }

        flight = new ClientAttachmentDownloadFlight(Context);
        Percent = 0;
        announcedProgressBucket = 0;
        Phase = ClientAttachmentDownloadPhase.Downloading;
        StatusText = "正在下载… 0%";
        return true;
    }

    public bool TryApplyProgress(
        bool ready,
        Guid? currentConversationId,
        Guid? currentMessageClientId,
        Guid? currentAttachmentId,
        long currentContextVersion,
        ClientAttachmentDownloadFlight flight,
        ClientAttachmentDownloadFlight? activeFlight,
        ClientAttachmentDownloadProgress progress)
    {
        ArgumentNullException.ThrowIfNull(flight);
        ArgumentNullException.ThrowIfNull(progress);

        if (Phase != ClientAttachmentDownloadPhase.Downloading ||
            progress.Percent < Percent ||
            !ClientAttachmentDownloadViewPolicy.IsCurrent(
                ready,
                Context,
                flight,
                currentConversationId,
                currentMessageClientId,
                currentAttachmentId,
                currentContextVersion,
                activeFlight))
        {
            return false;
        }

        Percent = progress.Percent;
        var progressBucket = progress.Percent / 10;
        if (progressBucket > announcedProgressBucket)
        {
            announcedProgressBucket = progressBucket;
            StatusText = $"正在下载… {progressBucket * 10}%";
        }

        return true;
    }

    public bool TryCancel(
        bool ready,
        Guid? currentConversationId,
        Guid? currentMessageClientId,
        Guid? currentAttachmentId,
        long currentContextVersion,
        ClientAttachmentDownloadFlight flight,
        ClientAttachmentDownloadFlight? activeFlight)
    {
        ArgumentNullException.ThrowIfNull(flight);

        if (Phase != ClientAttachmentDownloadPhase.Downloading ||
            !ClientAttachmentDownloadViewPolicy.IsCurrent(
                ready,
                Context,
                flight,
                currentConversationId,
                currentMessageClientId,
                currentAttachmentId,
                currentContextVersion,
                activeFlight))
        {
            return false;
        }

        Phase = ClientAttachmentDownloadPhase.Canceling;
        StatusText = "正在取消下载…";
        return true;
    }

    public bool TryApplyOutcome(
        bool ready,
        Guid? currentConversationId,
        Guid? currentMessageClientId,
        Guid? currentAttachmentId,
        long currentContextVersion,
        ClientAttachmentDownloadFlight flight,
        ClientAttachmentDownloadFlight? activeFlight,
        ClientAttachmentDownloadOutcome outcome)
    {
        ArgumentNullException.ThrowIfNull(flight);
        ArgumentNullException.ThrowIfNull(outcome);

        if (Phase is not (ClientAttachmentDownloadPhase.Downloading or
                ClientAttachmentDownloadPhase.Canceling) ||
            !ClientAttachmentDownloadViewPolicy.IsCurrent(
                ready,
                Context,
                flight,
                currentConversationId,
                currentMessageClientId,
                currentAttachmentId,
                currentContextVersion,
                activeFlight))
        {
            return false;
        }

        if (outcome.Status is ClientAttachmentDownloadStatus.Completed or
            ClientAttachmentDownloadStatus.AlreadyDownloaded)
        {
            Percent = 100;
            announcedProgressBucket = 10;
            Phase = ClientAttachmentDownloadPhase.Downloaded;
            StatusText = "已下载。";
            return true;
        }

        Phase = ClientAttachmentDownloadPhase.Failed;
        StatusText = GetFailureStatusText(outcome.Status);
        return true;
    }

    public bool SynchronizePersistedDownloaded(bool isDownloaded)
    {
        if (Phase is ClientAttachmentDownloadPhase.Downloading or
            ClientAttachmentDownloadPhase.Canceling)
        {
            return false;
        }

        Percent = isDownloaded ? 100 : 0;
        announcedProgressBucket = isDownloaded ? 10 : 0;
        Phase = isDownloaded
            ? ClientAttachmentDownloadPhase.Downloaded
            : ClientAttachmentDownloadPhase.Idle;
        StatusText = isDownloaded ? "已下载。" : "尚未下载。";
        return true;
    }

    public override string ToString() =>
        $"{nameof(ClientAttachmentDownloadViewState)} {{ Context = [REDACTED], " +
        $"DisplayName = [REDACTED], Phase = {Phase}, Percent = {Percent} }}";

    private static string GetFailureStatusText(ClientAttachmentDownloadStatus status) =>
        status switch
        {
            ClientAttachmentDownloadStatus.InProgress =>
                "附件正在其他任务中下载，请稍后重试。",
            ClientAttachmentDownloadStatus.AttachmentUnavailable =>
                "附件不可用或已被移除。",
            ClientAttachmentDownloadStatus.AuthenticationRequired =>
                "登录已失效，请重新登录。",
            ClientAttachmentDownloadStatus.AccessRevoked =>
                "已失去此会话的访问权限。",
            ClientAttachmentDownloadStatus.AccessDenied =>
                "无权下载此附件。",
            ClientAttachmentDownloadStatus.Canceled =>
                "下载已取消，可重试。",
            ClientAttachmentDownloadStatus.QuotaExceeded =>
                "附件缓存空间不足，请清理空间后重试。",
            ClientAttachmentDownloadStatus.TransientFailure =>
                "网络暂时不可用，请稍后重试。",
            ClientAttachmentDownloadStatus.ProtocolError =>
                "附件响应未通过安全校验。",
            ClientAttachmentDownloadStatus.RemoteFailure =>
                "服务器暂时无法提供附件。",
            ClientAttachmentDownloadStatus.LocalCacheFailure =>
                "本地附件缓存不可用。",
            _ => "下载失败，请重试。",
        };

    private bool SetField<T>(
        ref T field,
        T value,
        [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }

        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }

    private void RaisePhaseDependentPropertiesChanged()
    {
        OnPropertyChanged(nameof(Action));
        OnPropertyChanged(nameof(ActionLabel));
        OnPropertyChanged(nameof(CanInvoke));
        OnPropertyChanged(nameof(ShowProgress));
        OnPropertyChanged(nameof(AutomationName));
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
