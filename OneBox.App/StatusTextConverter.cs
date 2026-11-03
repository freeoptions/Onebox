using System.Globalization;
using System.Windows.Data;
using OneBox.Core;

namespace OneBox.App;

public sealed class StatusTextConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value switch
    {
        ConnectionStatus.Online => "已连接",
        ConnectionStatus.Connecting => "连接中",
        ConnectionStatus.NeedsAuthorization => "需要重新授权",
        ConnectionStatus.Failed => "同步失败",
        _ => "离线"
    };

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
