using CommunityToolkit.Mvvm.Input;
using RelayCove.App.Services;
using RelayCove.App.ViewModels;
using RelayCove.Core;

namespace RelayCove.App.Tests;

public sealed class ChannelSettingsViewModelTests
{
    [Fact]
    public async Task OpenAsync_WhenSnapshotLoads_ProjectsFilterAndAdministratorAccess()
    {
        var session = new SettingsSession();
        using var viewModel = Create(session);

        await viewModel.OpenAsync(2);

        Assert.True(viewModel.IsOpen);
        Assert.Equal("engineering", viewModel.SelectedName);
        Assert.True(viewModel.CanAdminister);
        Assert.Single(viewModel.FilteredChannels);
        viewModel.ListMode = ChannelSettingsListMode.Available;
        Assert.Single(viewModel.FilteredChannels);
        Assert.Equal("design", viewModel.FilteredChannels.Single().Name);
    }

    [Fact]
    public async Task SaveEditAsync_WhenConfirmed_UpdatesOnlySelectedChannelAndReloads()
    {
        var session = new SettingsSession();
        using var viewModel = Create(session);
        await viewModel.OpenAsync(2);
        viewModel.BeginEditNameCommand.Execute(null);
        viewModel.EditValue = "platform";

        await ((IAsyncRelayCommand)viewModel.SaveEditCommand).ExecuteAsync(null);

        Assert.Equal((2L, "platform", (string?)null, (long?)null, false), session.LastUpdate);
        Assert.False(viewModel.IsEditDialogOpen);
    }

    [Fact]
    public async Task ConfirmAsync_WhenArchiveRequested_UsesSelectedChannelOnly()
    {
        var session = new SettingsSession();
        using var viewModel = Create(session);
        await viewModel.OpenAsync(2);
        viewModel.RequestArchiveCommand.Execute(null);

        await ((IAsyncRelayCommand)viewModel.ConfirmCommand).ExecuteAsync(null);

        Assert.Equal(2, session.ArchivedChannelId);
    }

    [Fact]
    public async Task FetchEmailAsync_WhenAllowed_CopiesOnlyAfterExplicitCommand()
    {
        var session = new SettingsSession();
        var interactions = new Interactions();
        using var viewModel = new ChannelSettingsViewModel(session, interactions, _ => Task.CompletedTask);
        await viewModel.OpenAsync(2);

        await ((IAsyncRelayCommand)viewModel.FetchEmailCommand).ExecuteAsync(null);
        Assert.Empty(interactions.Copied);
        await ((IAsyncRelayCommand)viewModel.CopyEmailCommand).ExecuteAsync(null);

        Assert.Equal("engineering@example.test", Assert.Single(interactions.Copied));
    }

    [Fact]
    public async Task FetchEmailAsync_WhenChannelChanges_DropsSupersededAddress()
    {
        var requested = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var response = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var session = new SettingsSession
        {
            EmailHandler = async (_, cancellationToken) =>
            {
                requested.SetResult();
                return await response.Task.WaitAsync(cancellationToken);
            }
        };
        using var viewModel = Create(session);
        await viewModel.OpenAsync(2);

        var fetch = ((IAsyncRelayCommand)viewModel.FetchEmailCommand).ExecuteAsync(null);
        await requested.Task;
        await ((IAsyncRelayCommand)viewModel.SelectChannelCommand).ExecuteAsync(
            viewModel.Channels.Single(channel => channel.ChannelId == 3));
        response.TrySetResult("engineering@example.test");
        await fetch;

        Assert.Equal(3, viewModel.SelectedChannel?.ChannelId);
        Assert.Null(viewModel.EmailAddress);
    }

    [Fact]
    public async Task SelectChannel_WhenAnotherSelectionArrives_CancelsSupersededLoad()
    {
        var firstSelectionStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstSelectionCancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var snapshotReads = 0;
        var session = new SettingsSession
        {
            SnapshotHandler = async cancellationToken =>
            {
                var read = Interlocked.Increment(ref snapshotReads);
                if (read != 2) return new SettingsSession().Snapshot;
                firstSelectionStarted.SetResult();
                try
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                    throw new InvalidOperationException("The superseded load should have been cancelled.");
                }
                catch (OperationCanceledException)
                {
                    firstSelectionCancelled.SetResult();
                    throw;
                }
            }
        };
        using var viewModel = Create(session);
        await viewModel.OpenAsync(2);

        var first = ((IAsyncRelayCommand)viewModel.SelectChannelCommand).ExecuteAsync(
            viewModel.Channels.Single(channel => channel.ChannelId == 3));
        await firstSelectionStarted.Task;
        var second = ((IAsyncRelayCommand)viewModel.SelectChannelCommand).ExecuteAsync(
            viewModel.Channels.Single(channel => channel.ChannelId == 2));
        await Task.WhenAll(first, second);

        Assert.True(firstSelectionCancelled.Task.IsCompletedSuccessfully);
        Assert.Equal(2, viewModel.SelectedChannel?.ChannelId);
    }

    [Fact]
    public async Task CloseTopLayer_WhenChildDialogIsOpen_ClosesOnlyChildFirst()
    {
        var session = new SettingsSession();
        using var viewModel = Create(session);
        await viewModel.OpenAsync(2);

        viewModel.BeginEditNameCommand.Execute(null);
        viewModel.CloseTopLayerCommand.Execute(null);
        Assert.True(viewModel.IsOpen);
        Assert.False(viewModel.IsEditDialogOpen);

        viewModel.OpenCreateFolderCommand.Execute(null);
        viewModel.CloseTopLayerCommand.Execute(null);
        Assert.True(viewModel.IsOpen);
        Assert.False(viewModel.IsCreateFolderOpen);

        viewModel.RequestUnsubscribeCommand.Execute(null);
        viewModel.CloseTopLayerCommand.Execute(null);
        Assert.True(viewModel.IsOpen);
        Assert.False(viewModel.IsConfirmationOpen);

        viewModel.CloseTopLayerCommand.Execute(null);
        Assert.False(viewModel.IsOpen);
    }

    [Fact]
    public async Task FolderAndDescriptionEdits_WhenCleared_RemainExplicitWrites()
    {
        var session = new SettingsSession();
        using var viewModel = Create(session);
        await viewModel.OpenAsync(2);

        viewModel.ClearFolderCommand.Execute(null);
        Assert.True(viewModel.IsFolderDirty);
        await ((IAsyncRelayCommand)viewModel.SaveFolderCommand).ExecuteAsync(null);
        Assert.Equal((2L, (string?)null, (string?)null, (long?)null, true), session.LastUpdate);

        viewModel.BeginEditDescriptionCommand.Execute(null);
        viewModel.EditValue = string.Empty;
        await ((IAsyncRelayCommand)viewModel.SaveEditCommand).ExecuteAsync(null);
        Assert.Equal((2L, (string?)null, string.Empty, (long?)null, false), session.LastUpdate);
    }

    [Fact]
    public async Task DraftFolder_WhenPickerSelectsItem_ProjectsStableFolderId()
    {
        var session = new SettingsSession();
        using var viewModel = Create(session);
        await viewModel.OpenAsync(2);

        var folder = Assert.Single(viewModel.Folders);
        viewModel.DraftFolder = folder;
        Assert.Equal(9, viewModel.DraftFolderId);
        Assert.False(viewModel.IsFolderDirty);

        viewModel.DraftFolder = null;
        Assert.Null(viewModel.DraftFolderId);
        Assert.True(viewModel.IsFolderDirty);
    }

    [Fact]
    public async Task ConfirmAsync_WhenUnsubscribeRequested_UsesSettingsSelection()
    {
        var session = new SettingsSession();
        using var viewModel = Create(session);
        await viewModel.OpenAsync(2);

        viewModel.RequestUnsubscribeCommand.Execute(null);
        await ((IAsyncRelayCommand)viewModel.ConfirmCommand).ExecuteAsync(null);

        Assert.Equal(2, session.UnsubscribedChannelId);
    }

    [Fact]
    public async Task UpdateViewport_WhenNarrow_ShowsExactlyOnePane()
    {
        var session = new SettingsSession();
        using var viewModel = Create(session);
        viewModel.UpdateViewport(640);
        await viewModel.OpenAsync(2);

        Assert.True(viewModel.IsDesktopDetailVisible);
        Assert.Equal(new GridLength(0), viewModel.ListPaneWidth);
        Assert.Equal(GridLength.Star, viewModel.DetailPaneWidth);

        viewModel.BackToListCommand.Execute(null);
        Assert.True(viewModel.IsNarrowListVisible);
        Assert.Equal(GridLength.Star, viewModel.ListPaneWidth);
        Assert.Equal(new GridLength(0), viewModel.DetailPaneWidth);
    }

    [Fact]
    public async Task CreateChannel_WhenOpenedBeforeNameAndCancelled_DoesNotWrite()
    {
        var session = new SettingsSession();
        using var viewModel = Create(session);
        await viewModel.OpenAsync(2);

        Assert.True(viewModel.CanOpenCreateChannel);
        Assert.False(viewModel.CanSubmitCreateChannel);
        viewModel.OpenCreateChannelCommand.Execute(null);
        viewModel.CancelCreateChannelCommand.Execute(null);

        Assert.Equal(0, session.CreateCount);
    }

    [Fact]
    public async Task OpenCreate_WhenAdministrator_OpensSettingsAndCreateDialogDirectly()
    {
        var session = new SettingsSession();
        using var viewModel = Create(session);

        await viewModel.OpenCreateAsync(2);

        Assert.True(viewModel.IsOpen);
        Assert.True(viewModel.IsCreateChannelOpen);
        Assert.True(viewModel.CanOpenCreateChannel);
        Assert.Equal(0, viewModel.NewChannelPrivacyIndex);
        Assert.False(viewModel.NewChannelIsPrivate);
        Assert.True(viewModel.NewChannelHistoryPublic);
        Assert.False(viewModel.CanShareNewChannelHistory);
        Assert.Equal(0, session.CreateCount);
    }

    [Fact]
    public async Task CreateChannel_WhenSubmitted_UsesExplicitFormTarget()
    {
        var session = new SettingsSession();
        using var viewModel = Create(session);
        await viewModel.OpenAsync(2);
        viewModel.OpenCreateChannelCommand.Execute(null);
        viewModel.NewChannelName = "new-channel";
        viewModel.NewChannelIsPrivate = true;
        viewModel.NewChannelHistoryPublic = true;

        await ((IAsyncRelayCommand)viewModel.CreateChannelCommand).ExecuteAsync(null);

        Assert.Equal(1, session.CreateCount);
        Assert.Equal("new-channel", session.LastCreate?.Name);
        Assert.True(session.LastCreate?.IsPrivate);
        Assert.True(session.LastCreate?.HistoryPublicToSubscribers);
    }

    [Fact]
    public async Task PersonalSettings_WhenSubscribedNonAdministrator_AllowsExplicitColorAndWildcardWrites()
    {
        var session = new SettingsSession { IsOrganizationAdministrator = false };
        session.Details = session.Details with { CanAdministerChannelGroup = new AnonymousChannelGroupSetting([99], []) };
        using var viewModel = Create(session);
        await viewModel.OpenAsync(2);
        await ((IAsyncRelayCommand)viewModel.SelectTabCommand).ExecuteAsync("Personal");
        Assert.True(viewModel.CanChangePersonal);
        viewModel.PersonalColor = "#123456";
        await ((IAsyncRelayCommand)viewModel.SavePersonalColorCommand).ExecuteAsync(null);
        await ((IAsyncRelayCommand)viewModel.SetPersonalSettingCommand).ExecuteAsync("WildcardMentionsNotify");

        Assert.Equal((2L, ChannelPersonalSetting.Color, "#123456", (bool?)null), session.PersonalChanges[0]);
        Assert.Equal((2L, ChannelPersonalSetting.WildcardMentionsNotify, (string?)null, true), session.PersonalChanges[1]);
    }

    [Fact]
    public async Task PersonalColor_WhenPreviewedOrInvalid_DoesNotWriteUntilValidColorIsSaved()
    {
        var session = new SettingsSession();
        using var viewModel = Create(session);
        await viewModel.OpenAsync(2);
        await ((IAsyncRelayCommand)viewModel.SelectTabCommand).ExecuteAsync("Personal");

        viewModel.SelectPersonalColorCommand.Execute("#76ce90");

        Assert.Equal("#76CE90", viewModel.PersonalColor);
        Assert.Equal("#76CE90", viewModel.PersonalColorPreview);
        Assert.True(viewModel.ColorPalette.Single(option => option.Hex == "#76CE90").IsSelected);
        Assert.True(viewModel.CanSavePersonalColor);
        Assert.Empty(session.PersonalChanges);

        viewModel.PersonalColor = "blue";

        Assert.False(viewModel.HasValidPersonalColor);
        Assert.True(viewModel.HasPersonalColorError);
        Assert.False(viewModel.CanSavePersonalColor);
        Assert.Equal("#CBD5E1", viewModel.PersonalColorPreview);
        await ((IAsyncRelayCommand)viewModel.SavePersonalColorCommand).ExecuteAsync(null);
        Assert.Empty(session.PersonalChanges);

        viewModel.PersonalColor = "  #abcdef ";
        await ((IAsyncRelayCommand)viewModel.SavePersonalColorCommand).ExecuteAsync(null);
        Assert.Equal((2L, ChannelPersonalSetting.Color, "#ABCDEF", (bool?)null), Assert.Single(session.PersonalChanges));
    }

    [Fact]
    public async Task PersonalColorPicker_WhenCancelled_RestoresOriginalColorWithoutWriting()
    {
        var session = new SettingsSession();
        using var viewModel = Create(session);
        await viewModel.OpenAsync(2);
        await ((IAsyncRelayCommand)viewModel.SelectTabCommand).ExecuteAsync("Personal");

        viewModel.OpenPersonalColorPickerCommand.Execute(null);
        Assert.False(viewModel.IsCustomColorExpanded);
        viewModel.ToggleCustomColorCommand.Execute(null);
        Assert.True(viewModel.IsCustomColorExpanded);
        viewModel.SelectPersonalColorCommand.Execute("#E4523D");
        viewModel.CloseTopLayerCommand.Execute(null);

        Assert.False(viewModel.IsColorPickerOpen);
        Assert.False(viewModel.IsCustomColorExpanded);
        Assert.Equal("#336699", viewModel.PersonalColor);
        Assert.Empty(session.PersonalChanges);
    }

    [Fact]
    public async Task PersonalColorPicker_WhenConfirmed_UsesOfficialPaletteAndWritesOnce()
    {
        var session = new SettingsSession();
        using var viewModel = Create(session);
        await viewModel.OpenAsync(2);
        await ((IAsyncRelayCommand)viewModel.SelectTabCommand).ExecuteAsync("Personal");

        viewModel.OpenPersonalColorPickerCommand.Execute(null);
        Assert.True(viewModel.IsColorPickerOpen);
        Assert.Equal(24, viewModel.ColorPalette.Count);
        Assert.Equal("#A47462", viewModel.ColorPalette[0].Hex);
        Assert.Equal("#9987E1", viewModel.ColorPalette[^1].Hex);
        viewModel.SelectPersonalColorCommand.Execute("#4f8de4");
        await ((IAsyncRelayCommand)viewModel.SavePersonalColorCommand).ExecuteAsync(null);

        Assert.Equal((2L, ChannelPersonalSetting.Color, "#4F8DE4", (bool?)null), Assert.Single(session.PersonalChanges));
        Assert.False(viewModel.IsColorPickerOpen);
    }

    [Fact]
    public async Task AdvancedSettings_WhenNoDiff_DoesNotWrite_AndProjectsElevenGroupSettings()
    {
        var session = new SettingsSession();
        using var viewModel = Create(session);
        await viewModel.OpenAsync(2);
        await ((IAsyncRelayCommand)viewModel.SelectTabCommand).ExecuteAsync("Permissions");
        Assert.Equal(11, viewModel.Permissions.Count);

        await ((IAsyncRelayCommand)viewModel.SaveAdvancedCommand).ExecuteAsync(null);

        Assert.Empty(session.AdvancedChanges);
    }

    [Fact]
    public async Task AdvancedSettings_WhenNamedGroupChanges_UsesExactOldAndNewGroups()
    {
        var session = new SettingsSession();
        session.Details = session.Details with { CanSubscribeGroup = new NamedChannelGroupSetting(1) };
        using var viewModel = Create(session);
        await viewModel.OpenAsync(2);
        await ((IAsyncRelayCommand)viewModel.SelectTabCommand).ExecuteAsync("Permissions");
        viewModel.SelectedPermission = viewModel.Permissions.First(item => item.Name == ChannelGroupSettingName.CanSubscribe);

        Assert.Equal(1, viewModel.SelectedNamedGroup?.GroupId);
        viewModel.SelectedNamedGroup = viewModel.NamedGroups.First(group => group.GroupId == 2);
        Assert.True(viewModel.CanSaveSelectedNamedGroup);

        await ((IAsyncRelayCommand)viewModel.SaveNamedGroupCommand).ExecuteAsync(null);

        var change = Assert.Single(session.AdvancedChanges);
        Assert.Equal(ChannelGroupSettingName.CanSubscribe, change.Change.GroupSetting);
        Assert.Equal(new NamedChannelGroupSetting(1), change.Change.OldGroup);
        Assert.Equal(new NamedChannelGroupSetting(2), change.Change.NewGroup);
    }

    [Fact]
    public async Task Unarchive_WhenSelectedChannelIsArchived_CapturesChannelId()
    {
        var session = new SettingsSession();
        session.Details = session.Details with { IsArchived = true };
        session.Snapshot = session.Snapshot with { Channels = [new ChannelSummary(2, "engineering", null, true, 4, false, true)] };
        using var viewModel = Create(session);
        await viewModel.OpenAsync(2);

        await ((IAsyncRelayCommand)viewModel.UnarchiveCommand).ExecuteAsync(null);
        Assert.Equal(ChannelSettingsConfirmation.Unarchive, viewModel.Confirmation);
        await ((IAsyncRelayCommand)viewModel.ConfirmCommand).ExecuteAsync(null);

        Assert.Equal(2, session.UnarchivedChannelId);
    }

    [Fact]
    public async Task Members_WhenAddAndRemovePermissionsDiffer_OnlyPermittedWriteIsSent()
    {
        var session = new SettingsSession { IsOrganizationAdministrator = false };
        session.Details = session.Details with
        {
            CanAdministerChannelGroup = new AnonymousChannelGroupSetting([99], []),
            CanAddSubscribersGroup = new AnonymousChannelGroupSetting([10], []),
            CanRemoveSubscribersGroup = new AnonymousChannelGroupSetting([99], [])
        };
        using var viewModel = Create(session);
        await viewModel.OpenAsync(2);
        await ((IAsyncRelayCommand)viewModel.SelectTabCommand).ExecuteAsync("Subscribers");
        Assert.True(viewModel.CanAddMembers);
        Assert.False(viewModel.CanRemoveMembers);
        viewModel.Members.Add(new ChannelMemberItem(22, "candidate", false) { IsSelected = true });

        await ((IAsyncRelayCommand)viewModel.AddSelectedMembersCommand).ExecuteAsync(null);
        viewModel.RequestRemoveMemberCommand.Execute(new ChannelMemberItem(23, "member", true));

        var add = Assert.Single(session.AddMemberRequests);
        Assert.Equal(2, add.ChannelId);
        Assert.Equal([22], add.UserIds);
        Assert.True(add.SendNewSubscriptionMessages);
        Assert.Empty(session.RemoveMemberRequests);
        Assert.False(viewModel.IsMemberRemovalConfirmationOpen);
    }

    [Fact]
    public async Task RemoveMember_WhenPrivateSubscribedAndDirectGroupAllows_ConfirmsWritesOnceAndReloads()
    {
        var session = new SettingsSession { IsOrganizationAdministrator = false };
        session.Snapshot = session.Snapshot with
        {
            Channels = [new ChannelSummary(2, "engineering", "Build work", false, 4, true, true, "#336699", 12)]
        };
        session.Details = session.Details with
        {
            IsPrivate = true,
            CanAdministerChannelGroup = new AnonymousChannelGroupSetting([99], []),
            CanAddSubscribersGroup = new AnonymousChannelGroupSetting([99], []),
            CanRemoveSubscribersGroup = new AnonymousChannelGroupSetting([10], [])
        };
        using var viewModel = Create(session);
        await viewModel.OpenAsync(2);
        await ((IAsyncRelayCommand)viewModel.SelectTabCommand).ExecuteAsync("Subscribers");

        Assert.True(viewModel.CanRemoveMembers);
        var member = Assert.Single(viewModel.SubscribedMembers);
        viewModel.RequestRemoveMemberCommand.Execute(member);
        Assert.True(viewModel.IsMemberRemovalConfirmationOpen);
        Assert.Contains(member.Name, viewModel.MemberRemovalConfirmationText);
        Assert.True(viewModel.CanConfirmRemoveMember);

        await ((IAsyncRelayCommand)viewModel.ConfirmRemoveMemberCommand).ExecuteAsync(null);

        var request = Assert.Single(session.RemoveMemberRequests);
        Assert.Equal(2L, request.ChannelId);
        Assert.Equal([10L], request.UserIds);
        Assert.False(viewModel.IsMemberRemovalConfirmationOpen);
        Assert.Null(viewModel.PendingMemberRemoval);
    }

    [Fact]
    public async Task RemoveMember_WhenWriteFails_KeepsConfirmationAndShowsErrorWithoutRetry()
    {
        var session = new SettingsSession { RemoveMembersHandler = (_, _, _) => throw new InvalidOperationException("fail") };
        using var viewModel = Create(session);
        await viewModel.OpenAsync(2);
        await ((IAsyncRelayCommand)viewModel.SelectTabCommand).ExecuteAsync("Subscribers");
        viewModel.RequestRemoveMemberCommand.Execute(Assert.Single(viewModel.SubscribedMembers));

        await ((IAsyncRelayCommand)viewModel.ConfirmRemoveMemberCommand).ExecuteAsync(null);

        Assert.True(viewModel.IsMemberRemovalConfirmationOpen);
        Assert.True(viewModel.HasError);
        Assert.Single(session.RemoveMemberRequests);
        viewModel.IsBusy = true;
        Assert.False(viewModel.CanConfirmRemoveMember);
    }

    [Fact]
    public async Task NamedGroup_WhenAnonymousOrUnauthorized_DoesNotWrite()
    {
        var session = new SettingsSession();
        session.Details = session.Details with { CanSubscribeGroup = new AnonymousChannelGroupSetting([10], []) };
        using var viewModel = Create(session);
        await viewModel.OpenAsync(2);
        await ((IAsyncRelayCommand)viewModel.SelectTabCommand).ExecuteAsync("Permissions");
        viewModel.SelectPermissionCommand.Execute(viewModel.Permissions.First(item => item.Name == ChannelGroupSettingName.CanSubscribe));
        viewModel.SelectedNamedGroup = new ChannelUserGroup(2, "new group", false, [], []);

        await ((IAsyncRelayCommand)viewModel.SaveNamedGroupCommand).ExecuteAsync(null);

        Assert.Empty(session.AdvancedChanges);
    }

    [Fact]
    public async Task AdvancedSettings_WhenTopicsAndRetentionChange_WritesOnlyTheDifferenceToSelectedChannel()
    {
        var session = new SettingsSession();
        using var viewModel = Create(session);
        await viewModel.OpenAsync(2);
        await ((IAsyncRelayCommand)viewModel.SelectTabCommand).ExecuteAsync("Permissions");
        viewModel.DraftTopicsPolicy = ChannelTopicsPolicy.DisableEmptyTopic;
        viewModel.RetentionMode = 2;
        viewModel.RetentionDaysText = "30";

        await ((IAsyncRelayCommand)viewModel.SaveAdvancedCommand).ExecuteAsync(null);

        var request = Assert.Single(session.AdvancedChanges);
        Assert.Equal(2, request.ChannelId);
        Assert.Equal(ChannelTopicsPolicy.DisableEmptyTopic, request.Change.TopicsPolicy);
        Assert.Equal(ChannelRetentionPolicy.ForDays(30), request.Change.RetentionPolicy);
        Assert.Null(request.Change.IsPrivate);
    }

    [Fact]
    public async Task CreateChannel_WhenPrivacySelectionChanges_SendsOnlyPublicOrPrivate()
    {
        var session = new SettingsSession();
        using var viewModel = Create(session);
        await viewModel.OpenAsync(2);
        viewModel.OpenCreateChannelCommand.Execute(null);
        viewModel.NewChannelName = "private-channel";
        viewModel.NewChannelPrivacyIndex = 1;
        viewModel.NewChannelHistoryPublic = true;

        await ((IAsyncRelayCommand)viewModel.CreateChannelCommand).ExecuteAsync(null);

        Assert.True(session.LastCreate!.IsPrivate);
        Assert.False(session.LastCreate.IsWebPublic);
        Assert.True(session.LastCreate.HistoryPublicToSubscribers);
    }

    [Fact]
    public async Task PersonalTab_WhenArchived_DoesNotRequestPersonalSettings()
    {
        var session = new SettingsSession();
        session.Details = session.Details with { IsArchived = true };
        session.Snapshot = session.Snapshot with { Channels = [new ChannelSummary(2, "engineering", null, true, 4, false, true)] };
        using var viewModel = Create(session);
        await viewModel.OpenAsync(2);

        await ((IAsyncRelayCommand)viewModel.SelectTabCommand).ExecuteAsync("Personal");

        Assert.False(viewModel.CanChangePersonal);
        Assert.Equal(0, session.PersonalSettingsReads);
    }

    [Fact]
    public async Task AdvancedSettings_WhenNonOrganizationAdministratorChangesDefault_DoesNotWriteDefaultStream()
    {
        var session = new SettingsSession { IsOrganizationAdministrator = false };
        session.Details = session.Details with { CanAdministerChannelGroup = new AnonymousChannelGroupSetting([10], []) };
        using var viewModel = Create(session);
        await viewModel.OpenAsync(2);
        await ((IAsyncRelayCommand)viewModel.SelectTabCommand).ExecuteAsync("Permissions");
        viewModel.DraftIsDefault = !viewModel.DraftIsDefault;

        await ((IAsyncRelayCommand)viewModel.SaveAdvancedCommand).ExecuteAsync(null);

        Assert.Empty(session.AdvancedChanges);
    }

    [Fact]
    public async Task TabLoad_WhenPersonalResponseIsSuperseded_DoesNotBackfillAfterSelectingGeneral()
    {
        var requested = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var response = new TaskCompletionSource<ChannelPersonalSettings>(TaskCreationOptions.RunContinuationsAsynchronously);
        var session = new SettingsSession
        {
            PersonalSettingsHandler = async (_, _) =>
            {
                requested.SetResult();
                return await response.Task;
            }
        };
        using var viewModel = Create(session);
        await viewModel.OpenAsync(2);

        var personal = ((IAsyncRelayCommand)viewModel.SelectTabCommand).ExecuteAsync("Personal");
        await requested.Task;
        await ((IAsyncRelayCommand)viewModel.SelectTabCommand).ExecuteAsync("General");
        response.SetResult(new ChannelPersonalSettings(2, "#ffffff", false, false, null, null, null, null, null));
        await personal;

        Assert.True(viewModel.IsGeneralTab);
        Assert.Null(viewModel.PersonalSettings);
        Assert.False(viewModel.IsTabLoading);
    }

    [Fact]
    public void OverlayXaml_WhenRenderingSettingsTabs_ContainsSelectedVisualsAndMemberFocusTarget()
    {
        var source = File.ReadAllText(FindWorkspaceFile("src", "RelayCove.App", "Controls", "ChannelSettingsOverlayView.xaml"));
        var codeBehind = File.ReadAllText(FindWorkspaceFile("src", "RelayCove.App", "Controls", "ChannelSettingsOverlayView.xaml.cs"));

        Assert.Contains("ChannelRowTemplate", source);
        Assert.Contains("ItemTemplate=\"{StaticResource ChannelRowTemplate}\"", source);
        Assert.Contains("Tapped=\"OnChannelRowTapped\" CommandParameter=\"{Binding .}\"", source);
        Assert.Contains("SelectChannelCommand.Execute(channel)", codeBehind);
        Assert.DoesNotContain("BindingContext.SelectChannelCommand, Source={x:Reference Root}", source);
        Assert.Contains("Clicked=\"OnChannelSubscriptionClicked\"", source);
        Assert.Contains("ChangeChannelSubscriptionCommand.Execute(channel)", codeBehind);
        Assert.DoesNotContain("BindingContext.ChangeChannelSubscriptionCommand, Source={x:Reference Root}", source);
        Assert.Contains("EmptyView", source);
        Assert.Contains("SettingsSectionBorderStyle", source);
        Assert.Contains("LineBreakMode=\"WordWrap\"", source);
        Assert.Contains("SurfaceSelectedBrush", source);
        Assert.Contains("ArchiveActionLabel", source);
        Assert.Contains("ChannelCountLabel", source);
        Assert.Contains("ChannelSettingsToolButtonStyle", source);
        Assert.Contains("MaximumWidthRequest=\"1240\"", source);
        Assert.Contains("RefreshCommand", source);
        Assert.Contains("ClearFolderCommand", source);
        Assert.Contains("SubscribedMembers", source);
        Assert.Contains("CandidateMembers", source);
        Assert.Contains("ColorPalette", source);
        Assert.Contains("PersonalColorPreview", source);
        Assert.Contains("OpenPersonalColorPickerCommand.Execute(null)", codeBehind);
        Assert.Contains("IsColorPickerOpen", source);
        Assert.Contains("ColorPickerConfirmButton", source);
        Assert.Contains("PopoverAnchorBehavior", source);
        Assert.Contains("NativeColorPicker", source);
        Assert.Contains("VerticalScrollBarVisibility=\"Default\"", source);
        Assert.DoesNotContain("PrimaryTextColor", source);
        Assert.Contains("ToggleCustomColorCommand", source);
        Assert.Contains("IsCustomColorExpanded", source);
        Assert.Contains("Text=\"{Binding Error}\" IsVisible=\"{Binding HasError}\"", source);
        Assert.Contains("Clicked=\"OnColorSwatchClicked\"", source);
        Assert.Contains("SelectPersonalColorCommand.Execute(option.Hex)", codeBehind);
        Assert.Contains("<Grid RowDefinitions=\"Auto,*\">", source);
        Assert.Contains("<ScrollView Grid.Row=\"1\"", source);
        Assert.Contains("ChannelSettingsColorSwatchButtonStyle", source);
        Assert.Contains("CanSavePersonalColor", source);
        Assert.Contains("EmailLabel", source);
        Assert.Contains("CanAddSelectedMembers", source);
        Assert.Contains("MemberListHeight", source);
        Assert.Contains("CandidateListHeight", source);
        Assert.Contains("EditError", source);
        Assert.Contains("IsSubscribedListMode", source);
        Assert.Contains("IsGeneralTab", source);
        Assert.Contains("ChangeSubscriptionCommand", source);
        Assert.Contains("DraftFolder", source);
        Assert.Contains("SaveFolderCommand", source);
        Assert.Contains("CopyEmailCommand", source);
        Assert.Contains("AudibleNotifications", source);
        Assert.Contains("PushNotifications", source);
        Assert.Contains("EmailNotifications", source);
        Assert.Contains("WildcardMentionsNotify", source);
        Assert.Contains("SendNewSubscriptionMessages", source);
        Assert.Contains("Clicked=\"OnRemoveMemberClicked\"", source);
        Assert.Contains("<Button x:DataType=\"{x:Null}\" Grid.Column=\"1\" Text=\"移除\"", source);
        Assert.Contains("RequestRemoveMemberCommand.Execute(member)", codeBehind);
        Assert.Contains("DraftIsWebPublic", source);
        Assert.Contains("DraftHistoryPublic", source);
        Assert.Contains("DraftIsDefault", source);
        Assert.Contains("SelectedItem=\"{Binding SelectedPermission, Mode=TwoWay}\"", source);
        Assert.Contains("CancelEditCommand", source);
        Assert.Contains("NewChannelNameEntry", source);
        Assert.Contains("NewFolderNameEntry", source);
        Assert.Contains("MemberRemovalCancelButton", source);
        Assert.Contains("MemberRemovalConfirmationText", source);
        Assert.Contains("CanConfirmRemoveMember", source);
        Assert.Contains("MemberManagementStatus", source);
    }

    [Fact]
    public async Task Archive_WhenOnlyChannelAdministrator_DoesNotOpenConfirmation()
    {
        var session = new SettingsSession { IsOrganizationAdministrator = false };
        using var viewModel = Create(session);
        await viewModel.OpenAsync(2);

        Assert.False(viewModel.CanChangeArchive);
        viewModel.RequestArchiveCommand.Execute(null);

        Assert.Equal(ChannelSettingsConfirmation.None, viewModel.Confirmation);
    }

    [Fact]
    public async Task AdvancedSettings_WhenPrivateChannelHasMetadataButNoContent_DoesNotWrite()
    {
        var session = new SettingsSession();
        session.Snapshot = session.Snapshot with { Channels = [new ChannelSummary(2, "engineering", null, false, 4, true, false)] };
        session.Details = session.Details with
        {
            IsPrivate = true,
            CanAdministerChannelGroup = new AnonymousChannelGroupSetting([10], [])
        };
        using var viewModel = Create(session);
        await viewModel.OpenAsync(2);
        viewModel.DraftTopicsPolicy = ChannelTopicsPolicy.DisableEmptyTopic;

        await ((IAsyncRelayCommand)viewModel.SaveAdvancedCommand).ExecuteAsync(null);

        Assert.False(viewModel.CanChangeContentAdvanced);
        Assert.Empty(session.AdvancedChanges);
    }

    [Fact]
    public async Task MemberProjection_WhenFiltering_SplitsSubscribedAndCandidatesAndClampsHeights()
    {
        var session = new SettingsSession();
        using var viewModel = Create(session);
        await viewModel.OpenAsync(2);
        await ((IAsyncRelayCommand)viewModel.SelectTabCommand).ExecuteAsync("Subscribers");
        viewModel.Members.Add(new ChannelMemberItem(1, "Alice", true, "alice@example.test"));
        viewModel.Members.Add(new ChannelMemberItem(2, "Bob", false, "bob@example.test"));
        viewModel.MemberSearchText = "bo";

        Assert.Contains(viewModel.SubscribedMembers, member => member.Name == "Alice");
        Assert.Single(viewModel.CandidateMembers);
        Assert.InRange(viewModel.MemberListHeight, 72, 260);
        Assert.InRange(viewModel.CandidateListHeight, 72, 220);
    }

    [Fact]
    public async Task Subscribers_WhenRealmUsersAreAuthoritative_ProjectsMembersCandidatesAndEmailSearch()
    {
        var session = new SettingsSession
        {
            RealmUsers =
            [
                new UserProfile(10, "Current Member", "member@example.test"),
                new UserProfile(22, "Ada Lovelace", "ada@example.test"),
                new UserProfile(23, "Inactive", "inactive@example.test", isActive: false)
            ],
            MemberIds = [10]
        };
        using var viewModel = Create(session);
        await viewModel.OpenAsync(2);
        await ((IAsyncRelayCommand)viewModel.SelectTabCommand).ExecuteAsync("Subscribers");

        Assert.True(viewModel.IsMemberDataCurrent);
        var member = Assert.Single(viewModel.SubscribedMembers);
        Assert.Equal("member@example.test", member.Email);
        Assert.Single(viewModel.CandidateMembers);
        viewModel.MemberSearchText = "ada@example";
        Assert.Single(viewModel.CandidateMembers);
        Assert.Contains(viewModel.SubscribedMembers, item => item.UserId == 10);
        Assert.DoesNotContain(viewModel.CandidateMembers, item => item.UserId == 23);
    }

    [Fact]
    public async Task Subscribers_WhenMemberIdCannotBeResolved_FailsClosedWithoutMemberWrites()
    {
        var session = new SettingsSession { RealmUsers = [new UserProfile(10, "Current", "current@example.test")], MemberIds = [99] };
        using var viewModel = Create(session);
        await viewModel.OpenAsync(2);
        await ((IAsyncRelayCommand)viewModel.SelectTabCommand).ExecuteAsync("Subscribers");

        Assert.False(viewModel.IsMemberDataCurrent);
        Assert.Empty(viewModel.Members);
        Assert.False(viewModel.CanAddMembers);
        Assert.False(viewModel.CanRemoveMembers);
        Assert.True(viewModel.HasError);
    }

    [Fact]
    public async Task FolderAndViewport_WhenSelected_ProjectsClearGateAndCompactHeader()
    {
        var session = new SettingsSession();
        using var viewModel = Create(session);
        await viewModel.OpenAsync(2);
        var changed = new List<string?>();
        viewModel.PropertyChanged += (_, args) => changed.Add(args.PropertyName);

        Assert.True(viewModel.CanClearFolder);
        viewModel.ClearFolderCommand.Execute(null);
        Assert.False(viewModel.CanClearFolder);
        Assert.Contains(nameof(ChannelSettingsViewModel.CanClearFolder), changed);
        viewModel.UpdateViewport(560);
        Assert.True(viewModel.IsCompactSettingsHeader);
    }

    [Fact]
    public async Task DefaultChannel_WhenDraftBecomesPrivate_ClearsAndDisablesDefaultForEditAndCreate()
    {
        var session = new SettingsSession();
        using var viewModel = Create(session);
        await viewModel.OpenAsync(2);

        viewModel.DraftIsDefault = true;
        viewModel.DraftIsPrivate = true;

        Assert.False(viewModel.DraftIsDefault);
        Assert.False(viewModel.CanSetDraftDefault);

        viewModel.OpenCreateChannelCommand.Execute(null);
        viewModel.NewChannelIsDefault = true;
        viewModel.NewChannelIsPrivate = true;

        Assert.False(viewModel.NewChannelIsDefault);
        Assert.False(viewModel.CanSetNewChannelDefault);
    }

    private static ChannelSettingsViewModel Create(SettingsSession session) =>
        new(session, new Interactions(), _ => Task.CompletedTask);

    private static string FindWorkspaceFile(params string[] parts)
    {
        for (var directory = new DirectoryInfo(Directory.GetCurrentDirectory()); directory is not null; directory = directory.Parent)
        {
            var candidate = Path.Combine([directory.FullName, .. parts]);
            if (File.Exists(candidate)) return candidate;
        }

        throw new FileNotFoundException("Unable to locate RelayCove workspace source.");
    }

    private sealed class Interactions : IPlatformInteractionService
    {
        public List<string> Copied { get; } = [];
        public Task CopyTextAsync(string text, CancellationToken cancellationToken = default) { Copied.Add(text); return Task.CompletedTask; }
        public Task OpenUriAsync(Uri uri, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class SettingsSession : IClientSession
    {
        public ChannelSettingsSnapshot Snapshot { get; set; } = new(
            [new ChannelSummary(2, "engineering", "Build work", false, 4, false, true, "#336699", 12), new ChannelSummary(3, "design", null, false, 1)],
            [new ChannelFolder(9, "产品", null)], [new ChannelUserGroup(1, "old group", false, [10], []), new ChannelUserGroup(2, "new group", false, [10], [])], 10, true, false, new ChannelSettingsLimits(60, 1024, 60, 1024));
        public ChannelDetails Details { get; set; } = new(2, "engineering", "Build work", false, false, false, 4, 12, 9, 10, DateTimeOffset.UnixEpoch, new AnonymousChannelGroupSetting([10], []), new AnonymousChannelGroupSetting([10], []), new AnonymousChannelGroupSetting([10], [])) { HistoryPublicToSubscribers = true };
        public bool IsOrganizationAdministrator { get; set; } = true;
        public (long ChannelId, string? Name, string? Description, long? FolderId, bool ClearFolder) LastUpdate { get; private set; }
        public long? ArchivedChannelId { get; private set; }
        public long? UnsubscribedChannelId { get; private set; }
        public long? UnarchivedChannelId { get; private set; }
        public int CreateCount { get; private set; }
        public ChannelCreateOptions? LastCreate { get; private set; }
        public List<(long ChannelId, ChannelPersonalSetting Setting, string? Color, bool? Boolean)> PersonalChanges { get; } = [];
        public List<(long ChannelId, ChannelAdvancedSettingsChange Change)> AdvancedChanges { get; } = [];
        public List<(long ChannelId, long[] UserIds, bool SendNewSubscriptionMessages)> AddMemberRequests { get; } = [];
        public List<(long ChannelId, long[] UserIds)> RemoveMemberRequests { get; } = [];
        public int PersonalSettingsReads { get; private set; }
        public IReadOnlyList<UserProfile> RealmUsers { get; set; } = [new UserProfile(10, "Current user", "current@example.test"), new UserProfile(22, "Candidate user", "candidate@example.test")];
        public IReadOnlyList<long> MemberIds { get; set; } = [10];
        public Func<long, CancellationToken, Task<ChannelPersonalSettings>>? PersonalSettingsHandler { get; init; }
        public Func<CancellationToken, Task<ChannelSettingsSnapshot>>? SnapshotHandler { get; init; }
        public Func<long, CancellationToken, Task<string>>? EmailHandler { get; init; }
        public Func<long, IReadOnlyList<long>, CancellationToken, Task>? RemoveMembersHandler { get; init; }
        public AccountId? AccountId => null;
        public RealmEndpoint? ActiveRealm => null;
        public long? CurrentUserId => 10;
        public long MaxFileUploadBytes => 0;
        public ClientState State => ClientState.Empty;
        public ConversationKey? SelectedConversation => null;
        public ConversationHistoryState HistoryState => ConversationHistoryState.Empty;
        public IReadOnlyList<ConversationKey> RecentDirectMessages => [];
        public event EventHandler<ClientStateChangedEventArgs>? StateChanged { add { } remove { } }
        public Task<bool> RestoreAsync(CancellationToken cancellationToken = default) => Task.FromResult(false);
        public Task LoginAsync(string realm, string email, string password, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task LogoutAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task SelectConversationAsync(ConversationKey conversation, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task LoadOlderAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<IReadOnlyList<TopicSummary>> LoadTopicsAsync(long channelId, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<TopicSummary>>([]);
        public Task SendAsync(string content, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task SetReactionAsync(long messageId, EmojiReactionIdentity reaction, bool add, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task EditMessageAsync(long messageId, string content, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task DeleteMessageAsync(long messageId, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task SetMessageStarredAsync(long messageId, bool isStarred, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<UploadedAttachment> UploadAttachmentAsync(AttachmentUpload upload, CancellationToken cancellationToken = default) => Task.FromResult(new UploadedAttachment("x", "https://example.test/x"));
        public Task<RealmMediaResult> GetRealmMediaAsync(RealmMediaRequest request, CancellationToken cancellationToken = default) => Task.FromResult(new RealmMediaResult([], "image/png"));
        public Task UnsubscribeChannelAsync(long channelId, CancellationToken cancellationToken = default) { UnsubscribedChannelId = channelId; return Task.CompletedTask; }
        public Task<ChannelSettingsSnapshot> LoadChannelSettingsSnapshotAsync(CancellationToken cancellationToken = default) =>
            SnapshotHandler?.Invoke(cancellationToken) ?? Task.FromResult(Snapshot with { IsOrganizationAdministrator = IsOrganizationAdministrator });
        public Task<ChannelDetails> LoadChannelDetailsAsync(long channelId, CancellationToken cancellationToken = default) => Task.FromResult(Details with { ChannelId = channelId, Name = channelId == 2 ? Details.Name : "design" });
        public Task UpdateChannelAsync(long channelId, string? name, string? description, long? folderId, bool clearFolder = false, CancellationToken cancellationToken = default) { LastUpdate = (channelId, name, description, folderId, clearFolder); return Task.CompletedTask; }
        public Task<ChannelFolder> CreateChannelFolderAsync(string name, string? description, CancellationToken cancellationToken = default) => Task.FromResult(new ChannelFolder(10, name, description));
        public Task<string> GetChannelEmailAddressAsync(long channelId, CancellationToken cancellationToken = default) =>
            EmailHandler?.Invoke(channelId, cancellationToken) ?? Task.FromResult("engineering@example.test");
        public Task ArchiveChannelAsync(long channelId, CancellationToken cancellationToken = default) { ArchivedChannelId = channelId; return Task.CompletedTask; }
        public Task<ChannelSummary> CreateChannelAsync(ChannelCreateOptions options, CancellationToken cancellationToken = default) { CreateCount++; LastCreate = options; return Task.FromResult(new ChannelSummary(44, options.Name, options.Description, false, 1, options.IsPrivate, true)); }
        public Task<ChannelPersonalSettings> GetChannelPersonalSettingsAsync(long channelId, CancellationToken cancellationToken = default)
        {
            PersonalSettingsReads++;
            return PersonalSettingsHandler?.Invoke(channelId, cancellationToken) ?? Task.FromResult(new ChannelPersonalSettings(channelId, "#336699", false, false, null, null, null, null, null));
        }
        public Task SetChannelPersonalSettingAsync(long channelId, ChannelPersonalSettingChange change, CancellationToken cancellationToken = default) { PersonalChanges.Add((channelId, change.Setting, change.ColorValue, change.BooleanValue)); return Task.CompletedTask; }
        public Task<IReadOnlyList<UserProfile>> GetRealmUsersAsync(CancellationToken cancellationToken = default) => Task.FromResult(RealmUsers);
        public Task<IReadOnlyList<long>> GetChannelMemberIdsAsync(long channelId, CancellationToken cancellationToken = default) => Task.FromResult(MemberIds);
        public Task AddChannelMembersAsync(long channelId, IReadOnlyList<long> principalIds, bool sendNewSubscriptionMessages, CancellationToken cancellationToken = default) { AddMemberRequests.Add((channelId, principalIds.ToArray(), sendNewSubscriptionMessages)); return Task.CompletedTask; }
        public Task RemoveChannelMembersAsync(long channelId, IReadOnlyList<long> principalIds, CancellationToken cancellationToken = default)
        {
            RemoveMemberRequests.Add((channelId, principalIds.ToArray()));
            return RemoveMembersHandler?.Invoke(channelId, principalIds, cancellationToken) ?? Task.CompletedTask;
        }
        public Task UpdateChannelAdvancedSettingsAsync(long channelId, ChannelAdvancedSettingsChange change, CancellationToken cancellationToken = default) { AdvancedChanges.Add((channelId, change)); return Task.CompletedTask; }
        public Task UnarchiveChannelAsync(long channelId, CancellationToken cancellationToken = default) { UnarchivedChannelId = channelId; return Task.CompletedTask; }
        public Task MarkDisplayedReadAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task MarkDisplayedReadAsync(ConversationKey expectedConversation, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task ClearLocalCacheAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task StopAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
