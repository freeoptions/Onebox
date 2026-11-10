namespace OneBox.App;

public partial class App : System.Windows.Application
{
    public static IReadOnlyList<int> FontScalePercentList { get; } = [85, 100, 115, 130, 150];

    public static void ApplyFontScale(int percent)
    {
        var scale = percent / 100d;
        foreach (var size in new[] { 10, 11, 12, 13, 14, 19, 20, 23, 25, 27 })
            Current.Resources[$"FontSize{size}"] = size * scale;
        Current.Resources["BodyLineHeight"] = 26 * scale;
        Current.Resources["HelpLineHeight"] = 22 * scale;
    }

    public static System.Windows.Input.Cursor ColumnResizeCursor { get; } =
        System.Windows.Interop.CursorInteropHelper.Create(
            new Microsoft.Win32.SafeHandles.SafeFileHandle(System.Windows.Forms.Cursors.VSplit.Handle, ownsHandle: false));

    public static System.Windows.Input.Cursor RowResizeCursor { get; } =
        System.Windows.Interop.CursorInteropHelper.Create(
            new Microsoft.Win32.SafeHandles.SafeFileHandle(System.Windows.Forms.Cursors.HSplit.Handle, ownsHandle: false));
}
