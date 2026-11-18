using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using OneBox.Storage;
using KeyEventHandler = System.Windows.Input.KeyEventHandler;
using RichTextBox = System.Windows.Controls.RichTextBox;
using TextBox = System.Windows.Controls.TextBox;
using TextBoxBase = System.Windows.Controls.Primitives.TextBoxBase;

namespace OneBox.App;

public static class SelectionAutoCopy
{
    private static readonly Lazy<Task<Dispatcher>> ClipboardDispatcher = new(StartClipboardDispatcher);
    private static readonly DiagnosticLog ClipboardLog = new(Path.Combine(AppContext.BaseDirectory, "logs"), "clipboard");
    private static volatile bool _enabled = true;
    private static string? _pendingText;

    public static bool Enabled
    {
        get => _enabled;
        set => _enabled = value;
    }

    public static void Register()
    {
        EventManager.RegisterClassHandler(typeof(TextBoxBase), UIElement.MouseLeftButtonUpEvent,
            new MouseButtonEventHandler((sender, _) => QueueCopy((TextBoxBase)sender)), true);
        EventManager.RegisterClassHandler(typeof(TextBoxBase), UIElement.KeyUpEvent,
            new KeyEventHandler((sender, e) =>
            {
                if (e.Key is Key.Left or Key.Right or Key.Up or Key.Down or Key.Home or Key.End or Key.PageUp or Key.PageDown or Key.A)
                    QueueCopy((TextBoxBase)sender);
            }), true);
    }

    private static void QueueCopy(TextBoxBase control)
    {
        if (!Enabled) return;
        control.Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() =>
        {
            if (control is TextBox textBox) Copy(textBox.SelectedText);
            else if (control is RichTextBox richTextBox) Copy(richTextBox.Selection.Text);
        }));
    }

    public static void Copy(string text)
    {
        if (!Enabled || string.IsNullOrWhiteSpace(text)) return;
        if (Interlocked.Exchange(ref _pendingText, text) is null) _ = CopyPendingTextAsync();
    }

    private static async Task CopyPendingTextAsync()
    {
        var dispatcher = await ClipboardDispatcher.Value.ConfigureAwait(false);
        await dispatcher.InvokeAsync(() =>
        {
            var text = Interlocked.Exchange(ref _pendingText, null);
            if (!Enabled || text is null) return;
            try { System.Windows.Clipboard.SetText(text); }
            catch (ExternalException) { }
        });
    }

    internal static async Task<bool> CopyExplicitAsync(string text)
    {
        var dispatcher = await ClipboardDispatcher.Value.ConfigureAwait(false);
        return await dispatcher.InvokeAsync(() =>
        {
            Interlocked.Exchange(ref _pendingText, null);
            try
            {
                NativeClipboard.SetText(text);
                return true;
            }
            catch (Win32Exception error)
            {
                ClipboardLog.TryAppend("告警", "显式复制失败", $"stage={error.Message} win32={error.NativeErrorCode}");
                return false;
            }
        }, DispatcherPriority.Send);
    }

    private static Task<Dispatcher> StartClipboardDispatcher()
    {
        var ready = new TaskCompletionSource<Dispatcher>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            ready.SetResult(Dispatcher.CurrentDispatcher);
            Dispatcher.Run();
        }) { IsBackground = true, Name = "OneBox Clipboard" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return ready.Task;
    }
}
