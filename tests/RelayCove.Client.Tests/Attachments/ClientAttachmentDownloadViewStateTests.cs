using RelayCove.Client.Attachments;
using RelayCove.Client.Sync;

namespace RelayCove.Client.Tests.Attachments;

public sealed class ClientAttachmentDownloadViewStateTests
{
    private static readonly Guid ConversationId =
        Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid MessageClientId =
        Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid FirstAttachmentId =
        Guid.Parse("33333333-3333-3333-3333-333333333333");
    private static readonly Guid SecondAttachmentId =
        Guid.Parse("44444444-4444-4444-4444-444444444444");

    [Fact]
    public void Constructor_WhenNotDownloaded_ExposesDownloadAction()
    {
        var state = CreateState(contextVersion: 7);

        Assert.Equal(ClientAttachmentDownloadPhase.Idle, state.Phase);
        Assert.Equal(ClientAttachmentDownloadAction.Download, state.Action);
        Assert.Equal("下载", state.ActionLabel);
        Assert.True(state.CanInvoke);
        Assert.False(state.ShowProgress);
        Assert.Equal(0, state.Percent);
        Assert.Equal("尚未下载。", state.StatusText);
        Assert.Equal("下载附件：report.pdf", state.AutomationName);
    }

    [Fact]
    public void Constructor_WhenDownloaded_ExposesShowInFolderAction()
    {
        var state = CreateState(contextVersion: 7, isDownloaded: true);

        Assert.Equal(ClientAttachmentDownloadPhase.Downloaded, state.Phase);
        Assert.Equal(ClientAttachmentDownloadAction.ShowInFolder, state.Action);
        Assert.Equal("在文件夹中显示", state.ActionLabel);
        Assert.True(state.CanInvoke);
        Assert.False(state.ShowProgress);
        Assert.Equal(100, state.Percent);
        Assert.Equal("在文件夹中显示附件：report.pdf", state.AutomationName);
    }

    [Fact]
    public void TryBeginDownload_WhenContextIsCurrent_ExposesCancelActionAndNotifiesBindings()
    {
        var state = CreateState(contextVersion: 7);
        var changed = new List<string?>();
        state.PropertyChanged += (_, args) => changed.Add(args.PropertyName);

        var started = TryBegin(state, contextVersion: 7, out var flight);

        Assert.True(started);
        Assert.NotNull(flight);
        Assert.Equal(ClientAttachmentDownloadPhase.Downloading, state.Phase);
        Assert.Equal(ClientAttachmentDownloadAction.Cancel, state.Action);
        Assert.Equal("取消", state.ActionLabel);
        Assert.True(state.CanInvoke);
        Assert.True(state.ShowProgress);
        Assert.Equal("取消下载附件：report.pdf", state.AutomationName);
        Assert.Contains(nameof(state.Phase), changed);
        Assert.Contains(nameof(state.Action), changed);
        Assert.Contains(nameof(state.ActionLabel), changed);
        Assert.Contains(nameof(state.CanInvoke), changed);
        Assert.Contains(nameof(state.ShowProgress), changed);
        Assert.Contains(nameof(state.AutomationName), changed);
        Assert.Contains(nameof(state.StatusText), changed);
    }

    [Theory]
    [InlineData(false, 7)]
    [InlineData(true, 8)]
    public void TryBeginDownload_WhenReadyOrVersionIsStale_Rejects(
        bool ready,
        long currentContextVersion)
    {
        var state = CreateState(contextVersion: 7);

        var started = state.TryBeginDownload(
            ready,
            ConversationId,
            MessageClientId,
            FirstAttachmentId,
            currentContextVersion,
            out var flight);

        Assert.False(started);
        Assert.Null(flight);
        Assert.Equal(ClientAttachmentDownloadPhase.Idle, state.Phase);
    }

    [Fact]
    public void TryApplyProgress_WhenCurrent_IsMonotonicAndBucketsStatusAnnouncements()
    {
        var state = CreateState(contextVersion: 7);
        Assert.True(TryBegin(state, contextVersion: 7, out var flight));
        Assert.NotNull(flight);

        Assert.True(ApplyProgress(state, flight, flight, contextVersion: 7, percent: 61));
        Assert.True(ApplyProgress(state, flight, flight, contextVersion: 7, percent: 69));
        Assert.False(ApplyProgress(state, flight, flight, contextVersion: 7, percent: 40));

        Assert.Equal(69, state.Percent);
        Assert.Equal("正在下载… 60%", state.StatusText);
    }

    [Fact]
    public void LateCallbacks_WhenSelectionMovesAwayThenBack_RejectA1Flight()
    {
        var firstState = CreateState(contextVersion: 1);
        var secondState = CreateState(contextVersion: 3);
        Assert.True(TryBegin(firstState, contextVersion: 1, out var firstFlight));
        Assert.True(TryBegin(secondState, contextVersion: 3, out var secondFlight));
        Assert.NotNull(firstFlight);
        Assert.NotNull(secondFlight);

        var applied = ApplyProgress(
            firstState,
            firstFlight,
            secondFlight,
            contextVersion: 3,
            percent: 50);
        var outcomeApplied = firstState.TryApplyOutcome(
            ready: true,
            ConversationId,
            MessageClientId,
            FirstAttachmentId,
            currentContextVersion: 3,
            firstFlight,
            activeFlight: secondFlight,
            new ClientAttachmentDownloadOutcome(
                ClientAttachmentDownloadStatus.Completed,
                "opaque.cache"));

        Assert.False(applied);
        Assert.False(outcomeApplied);
        Assert.Equal(0, firstState.Percent);
        Assert.Equal(ClientAttachmentDownloadPhase.Downloading, firstState.Phase);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    public void TryApplyProgress_WhenAnyExactGateComponentIsStale_Rejects(int staleComponent)
    {
        var state = CreateState(contextVersion: 7);
        Assert.True(TryBegin(state, contextVersion: 7, out var flight));
        Assert.NotNull(flight);

        var applied = state.TryApplyProgress(
            ready: staleComponent != 0,
            staleComponent == 1 ? Guid.NewGuid() : ConversationId,
            staleComponent == 2 ? Guid.NewGuid() : MessageClientId,
            staleComponent == 3 ? Guid.NewGuid() : FirstAttachmentId,
            currentContextVersion: staleComponent == 4 ? 8 : 7,
            flight,
            activeFlight: staleComponent == 5
                ? new ClientAttachmentDownloadFlight(state.Context)
                : flight,
            new ClientAttachmentDownloadProgress(bytesWritten: 50, totalBytes: 100));

        Assert.False(applied);
        Assert.Equal(0, state.Percent);
    }

    [Fact]
    public void TryApplyOutcome_WhenFlightIdentityWasReplaced_RejectsOldResult()
    {
        var state = CreateState(contextVersion: 7);
        Assert.True(TryBegin(state, contextVersion: 7, out var oldFlight));
        Assert.NotNull(oldFlight);
        var replacementFlight = new ClientAttachmentDownloadFlight(state.Context);
        var outcome = new ClientAttachmentDownloadOutcome(
            ClientAttachmentDownloadStatus.Completed,
            LocalPath: "opaque.cache");

        var applied = ApplyOutcome(state, oldFlight, replacementFlight, outcome);

        Assert.False(applied);
        Assert.Equal(ClientAttachmentDownloadPhase.Downloading, state.Phase);
        Assert.Equal(0, state.Percent);
    }

    [Fact]
    public void Progress_WhenMessageHasTwoAttachments_RemainsIsolatedPerAttachment()
    {
        var firstState = CreateState(contextVersion: 7, attachmentId: FirstAttachmentId);
        var secondState = CreateState(contextVersion: 7, attachmentId: SecondAttachmentId);
        Assert.True(TryBegin(firstState, contextVersion: 7, out var firstFlight));
        Assert.True(TryBegin(secondState, contextVersion: 7, out var secondFlight,
            attachmentId: SecondAttachmentId));
        Assert.NotNull(firstFlight);
        Assert.NotNull(secondFlight);

        Assert.True(ApplyProgress(
            firstState,
            firstFlight,
            firstFlight,
            contextVersion: 7,
            percent: 25));
        Assert.True(ApplyProgress(
            secondState,
            secondFlight,
            secondFlight,
            contextVersion: 7,
            percent: 75,
            attachmentId: SecondAttachmentId));

        Assert.Equal(25, firstState.Percent);
        Assert.Equal(75, secondState.Percent);
    }

    [Fact]
    public void TryCancel_WhenOwnedFlightIsCurrent_DisablesActionAndRejectsLateProgress()
    {
        var state = CreateState(contextVersion: 7);
        Assert.True(TryBegin(state, contextVersion: 7, out var flight));
        Assert.NotNull(flight);

        var canceled = state.TryCancel(
            ready: true,
            ConversationId,
            MessageClientId,
            FirstAttachmentId,
            currentContextVersion: 7,
            flight,
            activeFlight: flight);
        var lateProgressApplied = ApplyProgress(
            state,
            flight,
            flight,
            contextVersion: 7,
            percent: 50);
        var lateOutcomeApplied = ApplyOutcome(
            state,
            flight,
            activeFlight: null,
            new ClientAttachmentDownloadOutcome(
                ClientAttachmentDownloadStatus.Completed,
                "opaque.cache"));

        Assert.True(canceled);
        Assert.False(lateProgressApplied);
        Assert.False(lateOutcomeApplied);
        Assert.Equal(ClientAttachmentDownloadPhase.Canceling, state.Phase);
        Assert.Equal(ClientAttachmentDownloadAction.Cancel, state.Action);
        Assert.Equal("正在取消…", state.ActionLabel);
        Assert.False(state.CanInvoke);
        Assert.True(state.ShowProgress);
        Assert.Equal("正在取消附件下载：report.pdf", state.AutomationName);
    }

    [Fact]
    public void TryApplyOutcome_WhenCanceled_ExposesRetryAction()
    {
        var state = CreateState(contextVersion: 7);
        Assert.True(TryBegin(state, contextVersion: 7, out var flight));
        Assert.NotNull(flight);
        Assert.True(state.TryCancel(
            ready: true,
            ConversationId,
            MessageClientId,
            FirstAttachmentId,
            currentContextVersion: 7,
            flight,
            activeFlight: flight));

        var applied = ApplyOutcome(
            state,
            flight,
            flight,
            ClientAttachmentDownloadOutcome.Failure(ClientAttachmentDownloadStatus.Canceled));

        Assert.True(applied);
        Assert.Equal(ClientAttachmentDownloadPhase.Failed, state.Phase);
        Assert.Equal(ClientAttachmentDownloadAction.Download, state.Action);
        Assert.Equal("重试", state.ActionLabel);
        Assert.True(state.CanInvoke);
        Assert.False(state.ShowProgress);
        Assert.Equal("下载已取消，可重试。", state.StatusText);
        Assert.Equal("重试下载附件：report.pdf", state.AutomationName);
    }

    [Fact]
    public void SynchronizePersistedDownloaded_WhenIdleOrDownloaded_RefreshesStableState()
    {
        var state = CreateState(contextVersion: 7);

        Assert.True(state.SynchronizePersistedDownloaded(isDownloaded: true));
        Assert.Equal(ClientAttachmentDownloadPhase.Downloaded, state.Phase);
        Assert.Equal(100, state.Percent);
        Assert.Equal(ClientAttachmentDownloadAction.ShowInFolder, state.Action);

        Assert.True(state.SynchronizePersistedDownloaded(isDownloaded: false));
        Assert.Equal(ClientAttachmentDownloadPhase.Idle, state.Phase);
        Assert.Equal(0, state.Percent);
        Assert.Equal(ClientAttachmentDownloadAction.Download, state.Action);
        Assert.Equal("尚未下载。", state.StatusText);
    }

    [Fact]
    public void SynchronizePersistedDownloaded_WhenFlightIsRunning_DoesNotOverwriteIt()
    {
        var state = CreateState(contextVersion: 7);
        Assert.True(TryBegin(state, contextVersion: 7, out var flight));
        Assert.NotNull(flight);
        Assert.True(ApplyProgress(state, flight, flight, contextVersion: 7, percent: 40));

        var synchronized = state.SynchronizePersistedDownloaded(isDownloaded: true);

        Assert.False(synchronized);
        Assert.Equal(ClientAttachmentDownloadPhase.Downloading, state.Phase);
        Assert.Equal(40, state.Percent);
        Assert.Equal("正在下载… 40%", state.StatusText);

        Assert.True(state.TryCancel(
            ready: true,
            ConversationId,
            MessageClientId,
            FirstAttachmentId,
            currentContextVersion: 7,
            flight,
            activeFlight: flight));
        Assert.False(state.SynchronizePersistedDownloaded(isDownloaded: true));
        Assert.Equal(ClientAttachmentDownloadPhase.Canceling, state.Phase);
    }

    [Theory]
    [InlineData(3,
        "附件正在其他任务中下载，请稍后重试。")]
    [InlineData(4,
        "附件不可用或已被移除。")]
    [InlineData(5,
        "登录已失效，请重新登录。")]
    [InlineData(6,
        "已失去此会话的访问权限。")]
    [InlineData(7,
        "无权下载此附件。")]
    [InlineData(9,
        "附件缓存空间不足，请清理空间后重试。")]
    [InlineData(10,
        "网络暂时不可用，请稍后重试。")]
    [InlineData(11,
        "附件响应未通过安全校验。")]
    [InlineData(12,
        "服务器暂时无法提供附件。")]
    [InlineData(13,
        "本地附件缓存不可用。")]
    public void TryApplyOutcome_WhenDownloadFails_MapsStableStatusText(
        int statusValue,
        string expectedStatusText)
    {
        var state = CreateState(contextVersion: 7);
        Assert.True(TryBegin(state, contextVersion: 7, out var flight));
        Assert.NotNull(flight);

        var applied = ApplyOutcome(
            state,
            flight,
            flight,
            ClientAttachmentDownloadOutcome.Failure(
                (ClientAttachmentDownloadStatus)statusValue));

        Assert.True(applied);
        Assert.Equal(ClientAttachmentDownloadPhase.Failed, state.Phase);
        Assert.Equal(expectedStatusText, state.StatusText);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void TryApplyOutcome_WhenDownloadSucceeds_ExposesShowInFolder(
        int statusValue)
    {
        var state = CreateState(contextVersion: 7);
        Assert.True(TryBegin(state, contextVersion: 7, out var flight));
        Assert.NotNull(flight);

        var applied = ApplyOutcome(
            state,
            flight,
            flight,
            new ClientAttachmentDownloadOutcome(
                (ClientAttachmentDownloadStatus)statusValue,
                "opaque.cache"));

        Assert.True(applied);
        Assert.Equal(ClientAttachmentDownloadPhase.Downloaded, state.Phase);
        Assert.Equal(ClientAttachmentDownloadAction.ShowInFolder, state.Action);
        Assert.Equal(100, state.Percent);
        Assert.Equal("已下载。", state.StatusText);
    }

    [Fact]
    public void ToString_WhenCalled_RedactsDisplayNameAndIdentifiers()
    {
        var state = CreateState(contextVersion: 7);
        Assert.True(TryBegin(state, contextVersion: 7, out var flight));
        Assert.NotNull(flight);

        var text = string.Join(" | ", state.Context, flight, state);

        Assert.DoesNotContain(ConversationId.ToString(), text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(MessageClientId.ToString(), text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(FirstAttachmentId.ToString(), text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("report.pdf", text, StringComparison.Ordinal);
        Assert.Contains("[REDACTED]", text, StringComparison.Ordinal);
    }

    private static ClientAttachmentDownloadViewState CreateState(
        long contextVersion,
        bool isDownloaded = false,
        Guid? attachmentId = null) =>
        new(
            new ClientAttachmentDownloadContext(
                ConversationId,
                MessageClientId,
                attachmentId ?? FirstAttachmentId,
                contextVersion),
            "report.pdf",
            isDownloaded);

    private static bool TryBegin(
        ClientAttachmentDownloadViewState state,
        long contextVersion,
        out ClientAttachmentDownloadFlight? flight,
        Guid? attachmentId = null) =>
        state.TryBeginDownload(
            ready: true,
            ConversationId,
            MessageClientId,
            attachmentId ?? FirstAttachmentId,
            contextVersion,
            out flight);

    private static bool ApplyProgress(
        ClientAttachmentDownloadViewState state,
        ClientAttachmentDownloadFlight flight,
        ClientAttachmentDownloadFlight? activeFlight,
        long contextVersion,
        int percent,
        Guid? attachmentId = null) =>
        state.TryApplyProgress(
            ready: true,
            ConversationId,
            MessageClientId,
            attachmentId ?? FirstAttachmentId,
            contextVersion,
            flight,
            activeFlight,
            new ClientAttachmentDownloadProgress(percent, totalBytes: 100));

    private static bool ApplyOutcome(
        ClientAttachmentDownloadViewState state,
        ClientAttachmentDownloadFlight flight,
        ClientAttachmentDownloadFlight? activeFlight,
        ClientAttachmentDownloadOutcome outcome) =>
        state.TryApplyOutcome(
            ready: true,
            ConversationId,
            MessageClientId,
            state.Context.AttachmentId,
            state.Context.ContextVersion,
            flight,
            activeFlight,
            outcome);
}
