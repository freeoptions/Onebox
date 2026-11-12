using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using KeyEventHandler = System.Windows.Input.KeyEventHandler;
using RichTextBox = System.Windows.Controls.RichTextBox;
using TextBox = System.Windows.Controls.TextBox;
using TextBoxBase = System.Windows.Controls.Primitives.TextBoxBase;

namespace OneBox.App;

public static class SelectionAutoCopy
{
    public static bool Enabled { get; set; } = true;

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
        try { System.Windows.Clipboard.SetText(text); }
        catch (ExternalException) { }
    }
}
