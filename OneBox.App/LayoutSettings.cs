using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using Forms = System.Windows.Forms;

namespace OneBox.App;

internal sealed class LayoutSettings
{
    private readonly string _path;
    private LayoutState _state = new(null, null, null, null);

    public LayoutSettings(string path)
    {
        _path = path;
        try
        {
            if (File.Exists(path))
                _state = JsonSerializer.Deserialize<LayoutState>(File.ReadAllText(path)) ?? _state;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
        {
            System.Diagnostics.Trace.TraceWarning($"读取窗口布局失败：{error.Message}");
        }
    }

    public void TrackPanes(Grid grid)
    {
        var columns = grid.ColumnDefinitions;
        if (_state.Panes is { Sidebar: > 0, Inbox: > 0, Reader: > 0 } panes)
        {
            columns[0].Width = new GridLength(panes.Sidebar, GridUnitType.Star);
            columns[1].Width = new GridLength(panes.Inbox, GridUnitType.Star);
            columns[2].Width = new GridLength(panes.Reader, GridUnitType.Star);
        }

        void SaveWidths() => Save(_state with
        {
            Panes = new PaneWidths(columns[0].Width.Value, columns[1].Width.Value, columns[2].Width.Value)
        });
        foreach (var splitter in grid.Children.OfType<GridSplitter>())
        {
            splitter.DragCompleted += (_, args) =>
            {
                if (!args.Canceled) SaveWidths();
            };
            splitter.AddHandler(UIElement.KeyDownEvent, new System.Windows.Input.KeyEventHandler((_, args) =>
            {
                if (args.Handled && args.Key is Key.Left or Key.Right) SaveWidths();
            }), handledEventsToo: true);
        }
    }

    public void TrackWindow(Window window, WindowKind kind)
    {
        var saved = kind switch
        {
            WindowKind.Main => _state.Main,
            WindowKind.Settings => _state.Settings,
            _ => _state.Compose
        };
        if (saved is { Width: > 0, Height: > 0 })
        {
            window.WindowState = WindowState.Normal;
            window.Loaded += Restore;
        }

        var lastState = window.WindowState;
        window.StateChanged += (_, _) =>
        {
            if (window.WindowState != WindowState.Minimized) lastState = window.WindowState;
        };
        var saveTimer = new DispatcherTimer(DispatcherPriority.Background, window.Dispatcher) { Interval = TimeSpan.FromMilliseconds(300) };
        saveTimer.Tick += (_, _) => SavePlacement();
        window.Loaded += StartTracking;
        window.Closing += (_, _) => SavePlacement();

        void StartTracking(object sender, RoutedEventArgs args)
        {
            window.Loaded -= StartTracking;
            window.SizeChanged += (_, _) => ScheduleSave();
            window.LocationChanged += (_, _) => ScheduleSave();
            window.StateChanged += (_, _) => ScheduleSave();
        }

        void ScheduleSave()
        {
            saveTimer.Stop();
            saveTimer.Start();
        }

        void SavePlacement()
        {
            saveTimer.Stop();
            var handle = new WindowInteropHelper(window).Handle;
            var placement = new WindowPlacement { Length = Marshal.SizeOf<WindowPlacement>() };
            if (handle == IntPtr.Zero || !GetWindowPlacement(handle, ref placement)) return;
            var bounds = placement.NormalPosition;
            if (bounds.Right <= bounds.Left || bounds.Bottom <= bounds.Top) return;
            var layout = new WindowLayout(bounds.Left, bounds.Top, bounds.Right - bounds.Left, bounds.Bottom - bounds.Top,
                Forms.Screen.FromHandle(handle).DeviceName, lastState == WindowState.Maximized);
            Save(kind switch
            {
                WindowKind.Main => _state with { Main = layout },
                WindowKind.Settings => _state with { Settings = layout },
                _ => _state with { Compose = layout }
            });
        }

        void Restore(object sender, RoutedEventArgs args)
        {
            window.Loaded -= Restore;
            var layout = saved!;
            var handle = new WindowInteropHelper(window).Handle;
            var screen = Forms.Screen.AllScreens.FirstOrDefault(item => item.DeviceName == layout.ScreenName)
                ?? Forms.Screen.FromHandle(handle);
            // 普通窗口的WINDOWPLACEMENT使用工作区坐标，原点需要扣除任务栏偏移。
            var workArea = new System.Drawing.Rectangle(screen.Bounds.Location, screen.WorkingArea.Size);
            var bounds = FitWindowBounds(layout, workArea);
            var placement = new WindowPlacement
            {
                Length = Marshal.SizeOf<WindowPlacement>(),
                ShowCommand = 1, // SW_SHOWNORMAL
                NormalPosition = new NativeRect(bounds.Left, bounds.Top, bounds.Right, bounds.Bottom)
            };
            if (SetWindowPlacement(handle, ref placement) && layout.Maximized)
                window.WindowState = WindowState.Maximized;
        }
    }

    internal static System.Drawing.Rectangle FitWindowBounds(WindowLayout layout, System.Drawing.Rectangle workArea)
    {
        var width = Math.Min(layout.Width, workArea.Width);
        var height = Math.Min(layout.Height, workArea.Height);
        return new System.Drawing.Rectangle(
            Math.Clamp(layout.Left, workArea.Left, workArea.Right - width),
            Math.Clamp(layout.Top, workArea.Top, workArea.Bottom - height), width, height);
    }

    private void Save(LayoutState state)
    {
        if (state == _state && File.Exists(_path)) return;
        try
        {
            var temporaryPath = _path + ".tmp";
            File.WriteAllText(temporaryPath, JsonSerializer.Serialize(state));
            File.Move(temporaryPath, _path, overwrite: true);
            _state = state;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            System.Diagnostics.Trace.TraceWarning($"保存窗口布局失败：{error.Message}");
        }
    }

    public enum WindowKind { Main, Settings, Compose }

    public sealed record LayoutState(PaneWidths? Panes, WindowLayout? Main, WindowLayout? Settings, WindowLayout? Compose);
    public sealed record PaneWidths(double Sidebar, double Inbox, double Reader);
    public sealed record WindowLayout(int Left, int Top, int Width, int Height, string ScreenName, bool Maximized);

    [StructLayout(LayoutKind.Sequential)]
    private readonly record struct NativeRect(int Left, int Top, int Right, int Bottom);

    [StructLayout(LayoutKind.Sequential)]
    private struct WindowPlacement
    {
        public int Length, Flags, ShowCommand;
        public System.Drawing.Point MinPosition, MaxPosition;
        public NativeRect NormalPosition;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowPlacement(IntPtr handle, ref WindowPlacement placement);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPlacement(IntPtr handle, ref WindowPlacement placement);
}
