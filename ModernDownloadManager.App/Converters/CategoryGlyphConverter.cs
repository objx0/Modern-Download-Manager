using Microsoft.UI.Xaml.Data;
namespace ModernDownloadManager.App.Converters;
public sealed class CategoryGlyphConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language) => value?.ToString() switch
    {
        "Compressed" => "\uE7B8", "Documents" => "\uE8A5", "Music" => "\uE8D6",
        "Programs" => "\uE756", "Video" => "\uE714", "Images" => "\uEB9F",
        "Unfinished" => "\uE823", "Finished" => "\uE73E", "Queued" => "\uE8EF",
        _ => "\uE896"
    };
    public object ConvertBack(object value, Type targetType, object parameter, string language) => throw new NotSupportedException();
}
