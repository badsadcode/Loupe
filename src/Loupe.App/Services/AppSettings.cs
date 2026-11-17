using System.IO;
using System.Text.Json;
using Loupe.App.Bridge;
using Loupe.Core.Files;

namespace Loupe.App.Services;

/// <summary>Per-user preferences, stored in %APPDATA%\Loupe\settings.json.</summary>
public sealed class AppSettings
{
    /// <summary>UI language: "en" (default) or "pl".</summary>
    public string Language { get; set; } = "en";
    /// <summary>Theme id (see css/themes.css).</summary>
    public string Theme { get; set; } = "graphite";
    /// <summary>Behind thumbnails and in the viewer: "theme" or a colour (#rrggbb). Neutral grey by default.</summary>
    public string ViewBackground { get; set; } = "#202020";
    public string LastFolder { get; set; } = "";
    /// <summary>Pinned folders and network shares shown at the top of the tree.</summary>
    public List<string> Favorites { get; set; } = [];
    public int ThumbSize { get; set; } = 180;
    /// <summary>Thumbnail columns; 0 = Auto (as many as the thumbnail size allows).</summary>
    public int GridColumns { get; set; }
    public bool ShowNames { get; set; } = true;
    public double TreeWidth { get; set; } = 270;

    // Saving
    /// <summary>JPEG quality when saving edits; 0 = same as the original file.</summary>
    public int SaveQuality { get; set; }
    public bool LosslessRotate { get; set; } = true;
    public int Workers { get; set; } = 4;

    // Folders (empty = default under %LOCALAPPDATA%\Loupe)
    public string BackupFolder { get; set; } = "";
    public string ThumbFolder { get; set; } = "";

    // Rename / merge
    public RenameRules RenameRules { get; set; } = new();
    public bool DeleteEmptyParts { get; set; } = true;
    public bool ConfirmDeleteParts { get; set; } = true;

    public static string DataFolder => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Loupe");

    public string BackupRoot => string.IsNullOrWhiteSpace(BackupFolder) ? Path.Combine(DataFolder, "Originals") : BackupFolder;
    public string ThumbRoot => string.IsNullOrWhiteSpace(ThumbFolder) ? Path.Combine(DataFolder, "Thumbs") : ThumbFolder;
    public static string UndoRoot => Path.Combine(DataFolder, "Undo");
    public static string RescanFile => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Loupe", "rescan.json");
    public bool RescanHeaders { get; set; }
    /// <summary>Quick toolbar tool ids in order ("|" = separator); empty = the default set.</summary>
    public List<string> Toolbar { get; set; } = [];

    private static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Loupe", "settings.json");

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath), BridgeHost.Json) ?? new();
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException) { }
        return new();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions(BridgeHost.Json) { WriteIndented = true }));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }
}

/// <summary>Opening things outside the app.</summary>
public static class Shell
{
    public static void OpenUrl(string url)
    {
        if (!url.StartsWith("https://", StringComparison.OrdinalIgnoreCase) &&
            !url.StartsWith("http://", StringComparison.OrdinalIgnoreCase)) return;
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true });
    }

    public static void OpenFolder(string path)
    {
        if (!Directory.Exists(path)) throw new BridgeException(Loc.T("Folder does not exist yet: {path}", ("path", path)));
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true });
    }

    /// <summary>Opens Explorer with the file selected.</summary>
    public static void ShowFile(string path)
    {
        if (!File.Exists(path)) { OpenFolder(Path.GetDirectoryName(path) ?? path); return; }
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true });
    }
}
