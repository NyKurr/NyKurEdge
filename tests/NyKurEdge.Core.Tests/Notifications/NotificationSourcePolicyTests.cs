using NyKurEdge.Core.Notifications;
using NyKurEdge.Core.Settings;

namespace NyKurEdge.Core.Tests.Notifications;

[TestClass]
public sealed class NotificationSourcePolicyTests
{
    [TestMethod]
    public void OverallDisabledSettingBlocksEverySource()
    {
        Assert.IsFalse(NotificationSourcePolicy.Allows(new NotificationSettings(), "Discord"));
    }

    [TestMethod]
    public void MissingOverrideIsAllowedWhenIntegrationIsEnabled()
    {
        var settings = new NotificationSettings { Enabled = true };

        Assert.IsTrue(NotificationSourcePolicy.Allows(settings, "Discord"));
    }

    [TestMethod]
    public void DisabledOverrideIsCaseInsensitive()
    {
        var settings = new NotificationSettings
        {
            Enabled = true,
            SourceOverrides = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase)
            {
                ["Telegram"] = false,
            },
        };

        Assert.IsFalse(NotificationSourcePolicy.Allows(settings, "telegram"));
    }

    [TestMethod]
    public void PrivacyLevelControlsPreviewContent()
    {
        var notification = new NotificationSnapshot(
            1,
            "source",
            "Application",
            "Sender",
            "Private message",
            DateTimeOffset.UtcNow,
            null);

        Assert.AreEqual(string.Empty, notification.PreviewFor(NotificationPrivacy.AppOnly));
        Assert.AreEqual("Sender", notification.PreviewFor(NotificationPrivacy.SenderAndTitle));
        Assert.AreEqual("Private message", notification.PreviewFor(NotificationPrivacy.FullPreview));
    }
}
