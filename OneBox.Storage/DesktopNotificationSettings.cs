namespace OneBox.Storage;

public enum NotificationCorner { BottomRight, BottomLeft, TopRight, TopLeft }

public sealed record DesktopNotificationSettings
{
    public bool Enabled { get; init; } = true;
    public NotificationCorner Corner { get; init; } = NotificationCorner.BottomRight;
    public string ScreenDeviceName { get; init; } = "";
    public int DurationSeconds { get; init; } = 5;
    public int MaxVisible { get; init; } = 3;
    public bool ShowContent { get; init; } = true;
}
