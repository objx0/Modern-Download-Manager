namespace ModernDownloadManager.Core.Models;

public enum DownloadCategory
{
    General,
    Compressed,
    Documents,
    Music,
    Video,
    Programs,
    Images
}

public static class DownloadCategoryResolver
{
    private static readonly Dictionary<string, DownloadCategory> ExtensionMap = new(StringComparer.OrdinalIgnoreCase)
    {
        [".zip"] = DownloadCategory.Compressed,
        [".rar"] = DownloadCategory.Compressed,
        [".7z"] = DownloadCategory.Compressed,
        [".tar"] = DownloadCategory.Compressed,
        [".gz"] = DownloadCategory.Compressed,

        [".pdf"] = DownloadCategory.Documents,
        [".doc"] = DownloadCategory.Documents,
        [".docx"] = DownloadCategory.Documents,
        [".xls"] = DownloadCategory.Documents,
        [".xlsx"] = DownloadCategory.Documents,
        [".ppt"] = DownloadCategory.Documents,
        [".pptx"] = DownloadCategory.Documents,
        [".txt"] = DownloadCategory.Documents,

        [".mp3"] = DownloadCategory.Music,
        [".flac"] = DownloadCategory.Music,
        [".wav"] = DownloadCategory.Music,
        [".m4a"] = DownloadCategory.Music,

        [".mp4"] = DownloadCategory.Video,
        [".mkv"] = DownloadCategory.Video,
        [".avi"] = DownloadCategory.Video,
        [".mov"] = DownloadCategory.Video,
        [".webm"] = DownloadCategory.Video,

        [".exe"] = DownloadCategory.Programs,
        [".msi"] = DownloadCategory.Programs,
        [".msix"] = DownloadCategory.Programs,

        [".png"] = DownloadCategory.Images,
        [".jpg"] = DownloadCategory.Images,
        [".jpeg"] = DownloadCategory.Images,
        [".gif"] = DownloadCategory.Images,
        [".webp"] = DownloadCategory.Images,
        [".svg"] = DownloadCategory.Images,
    };

    public static DownloadCategory Resolve(string fileName)
    {
        var ext = Path.GetExtension(fileName);
        return !string.IsNullOrEmpty(ext) && ExtensionMap.TryGetValue(ext, out var category)
            ? category
            : DownloadCategory.General;
    }
}
