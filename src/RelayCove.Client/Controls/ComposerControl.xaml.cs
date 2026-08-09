using System.Windows;
using DragEventArgs = System.Windows.DragEventArgs;
using WpfControls = System.Windows.Controls;
using WpfInput = System.Windows.Input;
using WpfPrimitives = System.Windows.Controls.Primitives;

namespace RelayCove.Client.Controls;

public partial class ComposerControl : WpfControls.UserControl
{
    public static readonly RoutedEvent InteractionRequestedEvent = EventManager.RegisterRoutedEvent(
        nameof(InteractionRequested),
        RoutingStrategy.Bubble,
        typeof(EventHandler<ClientControlInteractionRequestedEventArgs>),
        typeof(ComposerControl));

    public ComposerControl() => InitializeComponent();

    public event EventHandler<ClientControlInteractionRequestedEventArgs> InteractionRequested
    {
        add => AddHandler(InteractionRequestedEvent, value);
        remove => RemoveHandler(InteractionRequestedEvent, value);
    }

    internal WpfControls.Border Surface => ComposerSurface;
    internal WpfControls.Border ReplyPanel => ReplyComposerPanel;
    internal WpfControls.TextBlock ReplySenderText => ReplyComposerSenderText;
    internal WpfControls.TextBlock ReplyContentText => ReplyComposerContentText;
    internal WpfControls.Border MentionPanel => MentionPickerPanel;
    internal WpfControls.TextBox MentionSearchTextBoxElement => MentionSearchTextBox;
    internal WpfControls.Button MentionSearchButtonElement => MentionSearchButton;
    internal WpfControls.TextBlock MentionSearchStatusTextElement => MentionSearchStatusText;
    internal WpfControls.ListBox MentionCandidateListElement => MentionCandidateList;
    internal WpfControls.TextBlock SelectedMentionHeadingTextElement => SelectedMentionHeadingText;
    internal WpfControls.ItemsControl SelectedMentionListElement => SelectedMentionList;
    internal WpfControls.Border SelectedAttachmentPanelElement => SelectedAttachmentPanel;
    internal WpfControls.TextBlock SelectedAttachmentHeadingTextElement => SelectedAttachmentHeadingText;
    internal WpfControls.ItemsControl SelectedAttachmentListElement => SelectedAttachmentList;
    internal WpfControls.Border AttachmentInputDropTargetElement => AttachmentInputDropTarget;
    internal WpfPrimitives.Thumb ResizeThumb => ComposerResizeThumb;
    internal WpfControls.TextBox MessageTextBox => MessageComposerTextBox;
    internal WpfControls.Button SelectAttachmentsButtonElement => SelectAttachmentsButton;
    internal WpfControls.Button MentionPickerButtonElement => MentionPickerButton;
    internal WpfControls.StackPanel SupplementaryActionsPanel => ComposerSupplementaryActionsPanel;
    internal WpfControls.Button SendButton => SendMessageButton;
    internal WpfControls.Grid UploadProgressPanel => AttachmentUploadProgressPanel;
    internal WpfControls.ProgressBar UploadProgressBar => AttachmentUploadProgressBar;
    internal WpfControls.TextBlock UploadProgressText => AttachmentUploadProgressText;
    internal WpfControls.TextBlock StatusText => MessageComposerStatusText;

    private void OnCancelReplyClicked(object sender, RoutedEventArgs e) =>
        Raise("CancelReplyRequested", sender, e);

    private void OnCloseMentionPickerClicked(object sender, RoutedEventArgs e) =>
        Raise("CloseMentionPickerRequested", sender, e);

    private void OnMentionSearchTextChanged(object sender, WpfControls.TextChangedEventArgs e) =>
        Raise("MentionSearchTextChanged", sender, e);

    private void OnMentionSearchPreviewKeyDown(object sender, WpfInput.KeyEventArgs e) =>
        Raise("MentionSearchPreviewKeyDown", sender, e);

    private void OnMentionSearchClicked(object sender, RoutedEventArgs e) =>
        Raise("MentionSearchRequested", sender, e);

    private void OnMentionCandidateClicked(object sender, RoutedEventArgs e) =>
        Raise("MentionCandidateRequested", sender, e);

    private void OnRemoveMentionClicked(object sender, RoutedEventArgs e) =>
        Raise("RemoveMentionRequested", sender, e);

    private void OnRemoveAttachmentClicked(object sender, RoutedEventArgs e) =>
        Raise("RemoveAttachmentRequested", sender, e);

    private void OnAttachmentInputPreviewDragEnter(object sender, DragEventArgs e) =>
        Raise("AttachmentDragEnter", sender, e);

    private void OnAttachmentInputPreviewDragOver(object sender, DragEventArgs e) =>
        Raise("AttachmentDragOver", sender, e);

    private void OnAttachmentInputPreviewDragLeave(object sender, DragEventArgs e) =>
        Raise("AttachmentDragLeave", sender, e);

    private void OnAttachmentInputPreviewDrop(object sender, DragEventArgs e) =>
        Raise("AttachmentDrop", sender, e);

    private void OnAttachmentInputPreviewKeyDown(object sender, WpfInput.KeyEventArgs e) =>
        Raise("AttachmentPreviewKeyDown", sender, e);

    private void OnResizeDragDelta(object sender, WpfPrimitives.DragDeltaEventArgs e) =>
        Raise("ResizeDragDelta", sender, e);

    private void OnMessageTextChanged(object sender, WpfControls.TextChangedEventArgs e) =>
        Raise("MessageTextChanged", sender, e);

    private void OnMessagePreviewKeyDown(object sender, WpfInput.KeyEventArgs e) =>
        Raise("MessagePreviewKeyDown", sender, e);

    private void OnSelectAttachmentsClicked(object sender, RoutedEventArgs e) =>
        Raise("SelectAttachmentsRequested", sender, e);

    private void OnMentionPickerClicked(object sender, RoutedEventArgs e) =>
        Raise("MentionPickerRequested", sender, e);

    private void OnUnavailableClicked(object sender, RoutedEventArgs e) =>
        Raise("UnavailableFeatureRequested", sender, e);

    private void OnSendMessageClicked(object sender, RoutedEventArgs e) =>
        Raise("SendRequested", sender, e);

    private void Raise(string interaction, object sender, object originalEventArgs) =>
        RaiseEvent(new ClientControlInteractionRequestedEventArgs(
            InteractionRequestedEvent,
            this,
            interaction,
            sender,
            originalEventArgs));
}
