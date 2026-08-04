using System.Reflection;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using RelayCove.Client.Accounts;
using RelayCove.Client.Search;
using RelayCove.Shared.Messages;

namespace RelayCove.Client.Tests.Search;

public sealed class ClientSearchPresentationTests
{
    [Fact]
    public void Create_WhenBodyAndAttachmentMatch_PresentsVisibleFieldsAndRedactsToString()
    {
        var result = CreateResult(
            snippet: "机密正文命中",
            matchedAttachmentFileName: "季度机密报告.xlsx");

        var presentation = ClientSearchResultPresentation.Create(result);

        Assert.Same(result, presentation.Result);
        Assert.Equal("私密频道 · 测试发送者", presentation.ConversationAndSender);
        Assert.Equal("机密正文命中", presentation.Snippet);
        Assert.Equal("匹配附件：季度机密报告.xlsx", presentation.AttachmentLabel);
        Assert.True(presentation.HasMatchedAttachment);
        Assert.False(string.IsNullOrWhiteSpace(presentation.Timestamp));

        var text = presentation.ToString();
        Assert.Contains("[REDACTED]", text, StringComparison.Ordinal);
        Assert.DoesNotContain("机密正文命中", text, StringComparison.Ordinal);
        Assert.DoesNotContain("季度机密报告.xlsx", text, StringComparison.Ordinal);
        Assert.DoesNotContain("私密频道", text, StringComparison.Ordinal);
        Assert.DoesNotContain("测试发送者", text, StringComparison.Ordinal);
        Assert.DoesNotContain(result.ConversationId.ToString("D"), text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(result.MessageId.ToString(), text, StringComparison.Ordinal);
    }

    [Fact]
    public void Create_WhenOnlyAttachmentMatches_UsesUnderstandableBodyPlaceholder()
    {
        var presentation = ClientSearchResultPresentation.Create(CreateResult(
            snippet: string.Empty,
            matchedAttachmentFileName: "附件命中.pdf"));

        Assert.Equal("正文为空；结果由附件名称匹配。", presentation.Snippet);
        Assert.Equal("匹配附件：附件命中.pdf", presentation.AttachmentLabel);
        Assert.True(presentation.HasMatchedAttachment);
    }

    [Fact]
    public void Create_WhenNoAttachmentMatches_HidesAttachmentPresentation()
    {
        var presentation = ClientSearchResultPresentation.Create(CreateResult(
            snippet: "仅正文命中",
            matchedAttachmentFileName: null));

        Assert.Equal("仅正文命中", presentation.Snippet);
        Assert.Equal(string.Empty, presentation.AttachmentLabel);
        Assert.False(presentation.HasMatchedAttachment);
    }

    [Fact]
    public async Task SearchSurface_WhenDisplayed_UsesExplicitControlsAndDoesNotMakeResultsLive()
    {
        await RunOnStaAsync(() =>
        {
            var window = CreateVisibleWindow();
            try
            {
                window.SearchPanel.Visibility = Visibility.Visible;
                Layout(window);

                Assert.Equal(AutomationLiveSetting.Off,
                    AutomationProperties.GetLiveSetting(window.MessageSearchResultList));
                Assert.Equal(AutomationLiveSetting.Polite,
                    AutomationProperties.GetLiveSetting(window.MessageSearchStatusText));
                Assert.Equal("执行消息搜索",
                    AutomationProperties.GetName(window.RunSearchButton));
                Assert.Contains(
                    "按 Enter 或点击搜索",
                    Assert.IsType<string>(window.MessageSearchTextBox.ToolTip),
                    StringComparison.Ordinal);

                window.MessageSearchTextBox.Text = "只修改输入不会自动联网";
                Layout(window);

                Assert.Empty(window.MessageSearchResultList.Items);
                Assert.DoesNotContain(
                    "正在搜索",
                    window.MessageSearchStatusText.Text,
                    StringComparison.Ordinal);
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public async Task SearchResultTemplate_WhenAttachmentMatches_ShowsExactVisibleContentWithoutHiddenIdentity()
    {
        await RunOnStaAsync(() =>
        {
            var window = CreateVisibleWindow();
            try
            {
                var result = CreateResult(
                    snippet: "可见搜索摘要",
                    matchedAttachmentFileName: "可见附件.zip");
                var presentation = ClientSearchResultPresentation.Create(result);
                window.SearchPanel.Visibility = Visibility.Visible;
                window.MessageSearchResultList.ItemsSource = new[] { presentation };
                Layout(window);

                var resultButton = Assert.Single(
                    FindVisualDescendants<Button>(window.MessageSearchResultList),
                    button => ReferenceEquals(button.DataContext, presentation));
                Assert.Equal("打开搜索结果", AutomationProperties.GetName(resultButton));
                Assert.DoesNotContain(
                    result.MessageId.ToString(),
                    AutomationProperties.GetName(resultButton),
                    StringComparison.Ordinal);
                Assert.DoesNotContain(
                    result.ConversationId.ToString("D"),
                    AutomationProperties.GetName(resultButton),
                    StringComparison.OrdinalIgnoreCase);

                var visibleTexts = FindVisualDescendants<TextBlock>(resultButton)
                    .Where(textBlock => textBlock.IsVisible)
                    .Select(textBlock => textBlock.Text)
                    .ToArray();
                Assert.Contains(presentation.ConversationAndSender, visibleTexts);
                Assert.Contains("可见搜索摘要", visibleTexts);
                Assert.Contains("匹配附件：可见附件.zip", visibleTexts);
                Assert.Equal(
                    AutomationLiveSetting.Off,
                    AutomationProperties.GetLiveSetting(window.MessageSearchResultList));
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public async Task ApplyAccountShellSnapshot_WhenAccountIsNoLongerActive_ClearsSearchResults()
    {
        await RunOnStaAsync(() =>
        {
            var window = CreateVisibleWindow();
            try
            {
                var presentation = ClientSearchResultPresentation.Create(CreateResult(
                    snippet: "注销后不可保留的摘要",
                    matchedAttachmentFileName: "注销后不可保留.txt"));
                window.SearchPanel.Visibility = Visibility.Visible;
                window.MessageSearchResultList.ItemsSource = new[] { presentation };
                Layout(window);
                Assert.Single(window.MessageSearchResultList.Items);

                window.ApplyAccountShellSnapshot(ClientAccountShellSnapshot.SignedOut());
                Layout(window);

                Assert.Empty(window.MessageSearchResultList.Items);
                Assert.DoesNotContain(presentation, window.MessageSearchResultList.Items.Cast<object>());
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public async Task ApplySearchResultsInvalidated_WhenSensitiveResultsAreVisible_ClearsPayloadSynchronously()
    {
        await RunOnStaAsync(() =>
        {
            var window = CreateVisibleWindow();
            try
            {
                var presentation = ClientSearchResultPresentation.Create(CreateResult(
                    snippet: "撤权后不可保留的摘要",
                    matchedAttachmentFileName: "撤权后不可保留.txt"));
                window.SearchPanel.Visibility = Visibility.Visible;
                window.MessageSearchResultList.ItemsSource = new[] { presentation };
                Layout(window);
                Assert.Single(window.MessageSearchResultList.Items);

                window.ApplySearchResultsInvalidated();

                Assert.Empty(window.MessageSearchResultList.Items);
                Assert.DoesNotContain(
                    "撤权后不可保留",
                    window.MessageSearchStatusText.Text,
                    StringComparison.Ordinal);
                Assert.Contains(
                    "已因账户、会话或消息状态变化而清除",
                    window.MessageSearchStatusText.Text,
                    StringComparison.Ordinal);
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public async Task OnSearchResultsInvalidated_WhenRaisedFromBackground_ClearsBeforeReturningWithoutDelayedCallback()
    {
        await RunOnStaAsync(() =>
        {
            var window = CreateVisibleWindow();
            try
            {
                var sensitiveResult = ClientSearchResultPresentation.Create(CreateResult(
                    snippet: "后台撤权后不可保留的摘要",
                    matchedAttachmentFileName: "后台撤权后不可保留.txt"));
                window.SearchPanel.Visibility = Visibility.Visible;
                window.MessageSearchResultList.ItemsSource = new[] { sensitiveResult };

                var conversationId = Guid.NewGuid();
                const long targetMessageId = 126;
                var lease = EstablishSearchHighlightLease(
                    window,
                    conversationId,
                    targetMessageId,
                    navigationVersion: 13);
                window.ApplyMessageListSnapshot(CreateReadyTargetSnapshot(
                    conversationId,
                    targetMessageId,
                    revision: 1));
                Layout(window);
                var targetCard = Assert.Single(
                    FindVisualDescendants<Border>(window.MessageList),
                    card =>
                        string.Equals(card.Tag as string, "MessageCard", StringComparison.Ordinal) &&
                        card.DataContext is ClientMessageListItemPresentation item &&
                        item.ServerMessageId == targetMessageId);
                Assert.Single(window.MessageSearchResultList.Items);
                Assert.True(GetLeaseBoolean(lease, "IsMaterialized"));
                Assert.Equal(new Thickness(2), targetCard.BorderThickness);

                var invalidationCall = Task.Run(() =>
                    GetOnSearchResultsInvalidatedMethod().Invoke(window, parameters: null));
                PumpDispatcherUntil(
                    window.Dispatcher,
                    () => invalidationCall.IsCompleted,
                    TimeSpan.FromSeconds(2));
                invalidationCall.GetAwaiter().GetResult();

                Assert.Null(window.MessageSearchResultList.ItemsSource);
                Assert.Empty(window.MessageSearchResultList.Items);
                Assert.Null(GetSearchHighlightLease(window));
                Assert.Equal(Colors.White,
                    Assert.IsType<SolidColorBrush>(targetCard.Background).Color);
                Assert.Equal(new Thickness(1), targetCard.BorderThickness);

                var replacement = ClientSearchResultPresentation.Create(CreateResult(
                    snippet: "失效完成后写入的新结果",
                    matchedAttachmentFileName: null));
                window.MessageSearchResultList.ItemsSource = new[] { replacement };
                PumpDispatcher(window.Dispatcher, TimeSpan.FromMilliseconds(100));

                Assert.Same(replacement, Assert.Single(window.MessageSearchResultList.Items));
                Assert.Null(GetSearchHighlightLease(window));
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public async Task ApplyMessageListSnapshot_WhenExactSearchTargetIsRealized_HighlightsUntilCardIsRecycled()
    {
        await RunOnStaAsync(() =>
        {
            var window = CreateVisibleWindow();
            try
            {
                var conversationId = Guid.NewGuid();
                const long targetMessageId = 42;
                var targetSnapshot = CreateReadyTargetSnapshot(
                    conversationId,
                    targetMessageId,
                    revision: 1);
                var lease = EstablishSearchHighlightLease(
                    window,
                    conversationId,
                    targetMessageId,
                    navigationVersion: 7);

                window.ApplyMessageListSnapshot(targetSnapshot);
                Layout(window);

                var targetCard = Assert.Single(
                    FindVisualDescendants<Border>(window.MessageList),
                    card =>
                        string.Equals(card.Tag as string, "MessageCard", StringComparison.Ordinal) &&
                        card.DataContext is ClientMessageListItemPresentation item &&
                        item.ServerMessageId == targetMessageId);
                Assert.Same(lease, GetSearchHighlightLease(window));
                Assert.True(GetLeaseBoolean(lease, "IsMaterialized"));
                Assert.Same(targetCard, GetLeaseProperty<Border>(lease, "HighlightedCard"));
                Assert.Equal(Color.FromRgb(0xFF, 0xF3, 0xC4),
                    Assert.IsType<SolidColorBrush>(targetCard.Background).Color);
                Assert.Equal(Color.FromRgb(0xE5, 0x9A, 0x13),
                    Assert.IsType<SolidColorBrush>(targetCard.BorderBrush).Color);
                Assert.Equal(new Thickness(2), targetCard.BorderThickness);

                targetCard.DataContext = CreateMessageItem(
                    conversationId,
                    targetMessageId + 1);

                Assert.Null(GetSearchHighlightLease(window));
                Assert.Equal(Colors.White,
                    Assert.IsType<SolidColorBrush>(targetCard.Background).Color);
                Assert.Equal(Color.FromRgb(0xE5, 0xEB, 0xF2),
                    Assert.IsType<SolidColorBrush>(targetCard.BorderBrush).Color);
                Assert.Equal(new Thickness(1), targetCard.BorderThickness);

                targetCard.ClearValue(FrameworkElement.DataContextProperty);
                window.ApplyMessageListSnapshot(targetSnapshot with { Revision = 2 });
                Layout(window);

                Assert.Null(GetSearchHighlightLease(window));
                Assert.Equal(Colors.White,
                    Assert.IsType<SolidColorBrush>(targetCard.Background).Color);
                Assert.Equal(new Thickness(1), targetCard.BorderThickness);
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public async Task ApplyMessageListSnapshot_WhenSearchHighlightIsMaterialized_RestoresCardAfterTwoSeconds()
    {
        await RunOnStaAsync(() =>
        {
            var window = CreateVisibleWindow();
            try
            {
                var conversationId = Guid.NewGuid();
                const long targetMessageId = 84;
                var lease = EstablishSearchHighlightLease(
                    window,
                    conversationId,
                    targetMessageId,
                    navigationVersion: 11);

                window.ApplyMessageListSnapshot(CreateReadyTargetSnapshot(
                    conversationId,
                    targetMessageId,
                    revision: 1));
                Layout(window);

                var targetCard = Assert.Single(
                    FindVisualDescendants<Border>(window.MessageList),
                    card =>
                        string.Equals(card.Tag as string, "MessageCard", StringComparison.Ordinal) &&
                        card.DataContext is ClientMessageListItemPresentation item &&
                        item.ServerMessageId == targetMessageId);
                Assert.True(GetLeaseBoolean(lease, "IsMaterialized"));
                Assert.Equal(new Thickness(2), targetCard.BorderThickness);

                PumpDispatcher(window.Dispatcher, TimeSpan.FromMilliseconds(2500));

                Assert.Null(GetSearchHighlightLease(window));
                Assert.Equal(Colors.White,
                    Assert.IsType<SolidColorBrush>(targetCard.Background).Color);
                Assert.Equal(Color.FromRgb(0xE5, 0xEB, 0xF2),
                    Assert.IsType<SolidColorBrush>(targetCard.BorderBrush).Color);
                Assert.Equal(new Thickness(1), targetCard.BorderThickness);
            }
            finally
            {
                window.Close();
            }
        });
    }

    private static SearchResultDto CreateResult(
        string snippet,
        string? matchedAttachmentFileName) =>
        new(
            MessageId: 987654321,
            ConversationId: Guid.NewGuid(),
            ConversationName: "私密频道",
            SenderName: "测试发送者",
            Snippet: snippet,
            CreatedAt: new DateTimeOffset(2026, 8, 4, 12, 34, 0, TimeSpan.Zero),
            MatchedAttachmentFileName: matchedAttachmentFileName);

    private static ClientMessageListSnapshot CreateReadyTargetSnapshot(
        Guid conversationId,
        long targetMessageId,
        long revision)
    {
        var item = CreateMessageItem(conversationId, targetMessageId);
        return new ClientMessageListSnapshot(
            ClientMessageListStatus.Ready,
            conversationId,
            [item],
            IsLoading: false,
            HasMoreBefore: false,
            HasMoreAfter: false,
            TargetMessageId: targetMessageId,
            LastLoadStatus: null,
            revision);
    }

    private static ClientMessageListItemPresentation CreateMessageItem(
        Guid conversationId,
        long messageId)
    {
        var message = new MessageDto(
            messageId,
            Guid.NewGuid(),
            conversationId,
            Guid.NewGuid(),
            "测试发送者",
            MessageType.Text,
            $"消息 {messageId}",
            ReplyToMessageId: null,
            Attachments: Array.Empty<AttachmentDto>(),
            MentionUserIds: Array.Empty<Guid>(),
            DateTimeOffset.UtcNow);
        return Assert.Single(ClientMessageListPresenter.Present(
            [message],
            currentUserId: Guid.NewGuid()));
    }

    private static object EstablishSearchHighlightLease(
        MainWindow window,
        Guid conversationId,
        long messageId,
        long navigationVersion)
    {
        var leaseType = typeof(MainWindow).GetNestedType(
            "SearchHighlightLease",
            BindingFlags.NonPublic) ?? throw new InvalidOperationException(
                "SearchHighlightLease was not found.");
        var lease = Activator.CreateInstance(
            leaseType,
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
            binder: null,
            args: [conversationId, messageId, navigationVersion],
            culture: null) ?? throw new InvalidOperationException(
                "SearchHighlightLease could not be created.");
        GetSearchHighlightLeaseField().SetValue(window, lease);
        return lease;
    }

    private static object? GetSearchHighlightLease(MainWindow window) =>
        GetSearchHighlightLeaseField().GetValue(window);

    private static FieldInfo GetSearchHighlightLeaseField() =>
        typeof(MainWindow).GetField(
            "searchHighlightLease",
            BindingFlags.Instance | BindingFlags.NonPublic) ??
        throw new InvalidOperationException("searchHighlightLease was not found.");

    private static MethodInfo GetOnSearchResultsInvalidatedMethod() =>
        typeof(MainWindow).GetMethod(
            "OnSearchResultsInvalidated",
            BindingFlags.Instance | BindingFlags.NonPublic) ??
        throw new InvalidOperationException("OnSearchResultsInvalidated was not found.");

    private static bool GetLeaseBoolean(object lease, string propertyName) =>
        Assert.IsType<bool>(GetLeaseProperty<object>(lease, propertyName));

    private static T? GetLeaseProperty<T>(object lease, string propertyName)
        where T : class =>
        lease.GetType().GetProperty(propertyName, BindingFlags.Instance | BindingFlags.Public)?
            .GetValue(lease) as T;

    private static MainWindow CreateVisibleWindow()
    {
        var window = new MainWindow
        {
            Width = 1200,
            Height = 800,
            ShowInTaskbar = false,
            ShowActivated = false,
            Opacity = 0,
            WindowStartupLocation = WindowStartupLocation.Manual,
            Left = -32000,
            Top = -32000,
        };
        window.LoginPanel.Visibility = Visibility.Collapsed;
        window.AccountPanel.Visibility = Visibility.Visible;
        window.Show();
        return window;
    }

    private static void Layout(MainWindow window)
    {
        window.Dispatcher.Invoke(static () => { }, DispatcherPriority.Loaded);
        window.Measure(new Size(window.Width, window.Height));
        window.Arrange(new Rect(0, 0, window.Width, window.Height));
        window.UpdateLayout();
        if (window.MessageSearchResultList.Items.Count != 0)
        {
            window.MessageSearchResultList.ScrollIntoView(
                window.MessageSearchResultList.Items[0]);
            window.MessageSearchResultList.UpdateLayout();
        }

        window.Dispatcher.Invoke(static () => { }, DispatcherPriority.Render);
    }

    private static void PumpDispatcher(Dispatcher dispatcher, TimeSpan duration)
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer(
            DispatcherPriority.ApplicationIdle,
            dispatcher)
        {
            Interval = duration,
        };
        timer.Tick += Stop;
        timer.Start();
        Dispatcher.PushFrame(frame);
        timer.Stop();
        timer.Tick -= Stop;
        return;

        void Stop(object? sender, EventArgs e)
        {
            _ = sender;
            _ = e;
            frame.Continue = false;
        }
    }

    private static void PumpDispatcherUntil(
        Dispatcher dispatcher,
        Func<bool> condition,
        TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (!condition())
        {
            if (DateTimeOffset.UtcNow >= deadline)
            {
                throw new TimeoutException("The background dispatcher call did not complete.");
            }

            PumpDispatcher(dispatcher, TimeSpan.FromMilliseconds(10));
        }
    }

    private static IEnumerable<T> FindVisualDescendants<T>(DependencyObject parent)
        where T : DependencyObject
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            var child = VisualTreeHelper.GetChild(parent, index);
            if (child is T match)
            {
                yield return match;
            }

            foreach (var descendant in FindVisualDescendants<T>(child))
            {
                yield return descendant;
            }
        }
    }

    private static Task RunOnStaAsync(Action action)
    {
        var completion = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                action();
                completion.SetResult();
            }
            catch (Exception exception)
            {
                completion.SetException(exception);
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return completion.Task;
    }
}
