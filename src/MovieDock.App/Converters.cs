using System.Collections;
using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;

namespace MovieDock.App;

/// <summary>列表 → 空格分隔字符串（标签列用）。</summary>
public sealed class ListToStringConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is IEnumerable items
            ? string.Join("  ", items.Cast<object>().Select(o => o?.ToString()))
            : "";

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>状态码 → 中文文案。ConverterParameter="sub" 时走字幕状态表。</summary>
public sealed class StatusTextConverter : IValueConverter
{
    private static readonly Dictionary<string, string> TaskStatus = new()
    {
        ["queued"] = "排队中", ["active"] = "下载中", ["paused"] = "已暂停",
        ["waiting"] = "等待中", ["interrupted"] = "已中断", ["complete"] = "已完成", ["error"] = "出错",
    };

    private static readonly Dictionary<string, string> SubtitleStatus = new()
    {
        ["searching"] = "搜索中", ["done"] = "已匹配", ["failed"] = "未匹配", ["skipped"] = "已跳过",
    };

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var s = value as string ?? "";
        if (s.Length == 0) return "—";
        var table = parameter as string == "sub" ? SubtitleStatus : TaskStatus;
        return table.TryGetValue(s, out var zh) ? zh : s;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>任务状态 → 前景色（完成绿 / 出错红 / 下载中蓝 / 暂停橙 / 中断灰）。</summary>
public sealed class StatusBrushConverter : IValueConverter
{
    private static readonly Brush Green = Freeze(new SolidColorBrush(Color.FromRgb(0x1e, 0x8e, 0x3e)));
    private static readonly Brush Red = Freeze(new SolidColorBrush(Color.FromRgb(0xd3, 0x2f, 0x2f)));
    private static readonly Brush Blue = Freeze(new SolidColorBrush(Color.FromRgb(0x15, 0x65, 0xc0)));
    private static readonly Brush Orange = Freeze(new SolidColorBrush(Color.FromRgb(0xe6, 0x8a, 0x00)));
    private static readonly Brush Gray = Freeze(new SolidColorBrush(Color.FromRgb(0x75, 0x75, 0x75)));

    private static Brush Freeze(Brush b) { b.Freeze(); return b; }

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        (value as string) switch
        {
            "complete" => Green,
            "error" => Red,
            "active" => Blue,
            "paused" => Orange,
            "interrupted" => Gray,
            _ => Brushes.Black,
        };

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
