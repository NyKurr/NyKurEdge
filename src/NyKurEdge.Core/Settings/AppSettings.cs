using NyKurEdge.Core.Appearance;

namespace NyKurEdge.Core.Settings;

public enum EdgeSide
{
    Left,
    Right,
}

public enum AccentMode
{
    Automatic,
    Manual,
}

public enum NotificationPrivacy
{
    AppOnly,
    SenderAndTitle,
    FullPreview,
}

public enum AnimationIntensity
{
    Quiet,
    Balanced,
    Expressive,
}

public sealed record AppSettings
{
    public const int CurrentSchemaVersion = 1;

    public int SchemaVersion { get; init; } = CurrentSchemaVersion;

    public bool LaunchOnStartup { get; init; }

    public EdgeSide EdgeSide { get; init; } = EdgeSide.Right;

    public AppearanceSettings Appearance { get; init; } = new();

    public MediaSettings Media { get; init; } = new();

    public NotificationSettings Notifications { get; init; } = new();

    public ClockSettings Clock { get; init; } = new();

    public AppSettings Normalize()
    {
        var manualAccent = AccentColor.TryParse(Appearance.ManualAccent, out var parsed)
            ? parsed.ToHex()
            : AccentColor.Default.ToHex();

        return this with
        {
            SchemaVersion = CurrentSchemaVersion,
            Appearance = Appearance with
            {
                ManualAccent = manualAccent,
                EdgeThickness = Math.Clamp(Appearance.EdgeThickness, 10, 24),
            },
            Clock = Clock with
            {
                IntervalMinutes = Math.Clamp(Clock.IntervalMinutes, 15, 240),
                HoldSeconds = Math.Clamp(Clock.HoldSeconds, 2, 12),
            },
            Notifications = Notifications with
            {
                SourceOverrides = new Dictionary<string, bool>(
                    Notifications.SourceOverrides,
                    StringComparer.OrdinalIgnoreCase),
            },
        };
    }
}

public sealed record AppearanceSettings
{
    public AccentMode AccentMode { get; init; } = AccentMode.Automatic;

    public string ManualAccent { get; init; } = "#7286E8";

    public AnimationIntensity AnimationIntensity { get; init; } = AnimationIntensity.Balanced;

    public int EdgeThickness { get; init; } = 14;
}

public sealed record MediaSettings
{
    public bool Enabled { get; init; } = true;
}

public sealed record NotificationSettings
{
    public bool Enabled { get; init; }

    public NotificationPrivacy Privacy { get; init; } = NotificationPrivacy.FullPreview;

    public Dictionary<string, bool> SourceOverrides { get; init; } = new(StringComparer.OrdinalIgnoreCase);
}

public sealed record ClockSettings
{
    public bool Enabled { get; init; } = true;

    public int IntervalMinutes { get; init; } = 60;

    public int HoldSeconds { get; init; } = 5;
}
