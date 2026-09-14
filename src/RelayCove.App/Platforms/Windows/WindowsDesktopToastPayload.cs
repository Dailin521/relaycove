using System.Xml.Linq;
using Microsoft.Toolkit.Uwp.Notifications;
using RelayCove.App.Services;

namespace RelayCove.App.Platforms.Windows;

internal static class WindowsDesktopToastPayload
{
    internal static string Build(AppMessageNotification notification, Uri? avatarUri, string activationToken)
    {
        var launch = new ToastArguments
        {
            { "conversation", notification.ConversationKey },
            { "session", activationToken }
        }.ToString();
        var binding = new XElement("binding", new XAttribute("template", "ToastGeneric"),
            new XElement("text", notification.Title), new XElement("text", notification.Body));
        if (WindowsAppNotificationService.CanUseAvatarUri(avatarUri))
        {
            binding.Add(new XElement("image", new XAttribute("placement", "appLogoOverride"),
                new XAttribute("hint-crop", "circle"), new XAttribute("src", avatarUri!.AbsoluteUri),
                new XAttribute("alt", notification.Title)));
        }
        return new XElement("toast", new XAttribute("launch", launch),
            new XElement("visual", binding), new XElement("audio", new XAttribute("silent", "true")))
            .ToString(SaveOptions.DisableFormatting);
    }

    internal static string? GetConversation(string arguments, string activationToken)
    {
        try
        {
            var parsed = ToastArguments.Parse(arguments);
            return parsed.TryGetValue("session", out var token) && token == activationToken &&
                parsed.TryGetValue("conversation", out var conversation) && !string.IsNullOrWhiteSpace(conversation)
                ? conversation : null;
        }
        catch { return null; }
    }
}
