using System.Windows;
using System.Windows.Controls;
using RelayCove.Client.Controls;
using RelayCove.Client.Presentation;

namespace RelayCove.Client.Tests.Desktop;

[Collection(WpfTestCollection.Name)]
public sealed class ChatHeaderControlPresentationTests
{
    [Fact]
    public async Task ChatHeaderControl_WhenPresentationPropertiesAreSet_BindsConversationStateAndMemberAvailability()
    {
        await RunOnStaAsync(() =>
        {
            var header = new ChatHeaderControl
            {
                Heading = "产品设计",
                Notice = "讨论客户端体验和 rc.25 视觉改造",
                MembersSummary = "成员：8 人",
                IsMembersEnabled = true,
            };

            header.UpdateLayout();

            Assert.Equal("产品设计", header.HeadingText.Text);
            Assert.Equal("讨论客户端体验和 rc.25 视觉改造", header.NoticeText.Text);
            Assert.Equal("成员：8 人", header.MembersSummaryText.Text);
            Assert.True(header.MembersButton.IsEnabled);
        });
    }

    [Fact]
    public async Task ChatHeaderControl_WhenActionsAreClicked_RaisesTypedCoordinatorIntents()
    {
        await RunOnStaAsync(() =>
        {
            var header = new ChatHeaderControl();
            var membersRequests = 0;
            var searchRequests = 0;
            var unavailableRequests = new List<ClientUiFeatureId>();
            header.MembersRequested += (_, _) => membersRequests++;
            header.SearchRequested += (_, _) => searchRequests++;
            header.UnavailableFeatureRequested += (_, e) => unavailableRequests.Add(e.FeatureId);

            RaiseClick(header.MembersButton);
            RaiseClick(header.SearchButton);
            RaiseClick(header.PinButton);
            RaiseClick(header.NotificationsButton);
            RaiseClick(header.MoreButton);

            Assert.Equal(1, membersRequests);
            Assert.Equal(1, searchRequests);
            Assert.Equal(
            [
                ClientUiFeatureId.ConversationPin,
                ClientUiFeatureId.ConversationNotifications,
                ClientUiFeatureId.ConversationMore,
            ],
            unavailableRequests);
        });
    }

    private static void RaiseClick(Button button) =>
        button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

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
