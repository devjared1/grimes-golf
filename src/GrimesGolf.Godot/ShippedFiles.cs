using System.IO.Compression;
using Godot;

namespace GrimesGolf.Game;

/// <summary>
/// The courses and physics presets that sit in folders beside the Windows app, for builds that have no such folders
/// (the iPad's). Their build packs both folders into <c>res://shipped.zip</c> (see <c>.github/workflows/ios.yml</c>),
/// and this unpacks it into the data folder the first time the app starts, and again after an update. Courses are
/// replaced; a preset already there is kept, as it may have been changed in the tuner.
/// </summary>
internal static class ShippedFiles
{
    private const string Pack = "res://shipped.zip";

    /// <summary>This build carries its courses and presets inside it.</summary>
    public static bool Packed { get; } = Godot.FileAccess.FileExists(Pack);

    private static string _dataFolder = OS.GetUserDataDir();

    public static string Courses => Path.Combine(_dataFolder, "shipped", "courses");
    public static string Presets => Path.Combine(_dataFolder, "tuning");

    /// <summary>Unpacks the courses and presets into the data folder, unless this version's are already there. Never throws.</summary>
    public static void Unpack(string dataFolder, Action<string> log)
    {
        if (!Packed) return;
        _dataFolder = dataFolder;
        try
        {
            byte[] zip = Godot.FileAccess.GetFileAsBytes(Pack);
            string stamp = $"{ProjectSettings.GetSetting("application/config/version")} {zip.Length}";
            string marker = Path.Combine(dataFolder, "shipped", "unpacked.txt");
            if (File.Exists(marker) && File.ReadAllText(marker) == stamp) return;

            if (Directory.Exists(Courses)) Directory.Delete(Courses, recursive: true);
            int files = 0;
            using var archive = new ZipArchive(new MemoryStream(zip), ZipArchiveMode.Read);
            foreach (var entry in archive.Entries)
            {
                string name = entry.FullName.Replace('\\', '/');
                if (name.EndsWith('/')) continue;   // a folder
                string? target = name.StartsWith("courses/") ? Path.Combine(Courses, name["courses/".Length..])
                               : name.StartsWith("tuning/") ? Path.Combine(Presets, name["tuning/".Length..])
                               : null;
                if (target == null || name.Split('/').Contains("..")) continue;
                bool preset = name.StartsWith("tuning/");
                if (preset && File.Exists(target)) continue;
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                entry.ExtractToFile(target, overwrite: true);
                files++;
            }
            Directory.CreateDirectory(Path.GetDirectoryName(marker)!);
            File.WriteAllText(marker, stamp);
            log($"Unpacked the courses and physics presets that came with the app ({files} files).");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            log($"Couldn't unpack the courses that came with the app: {ex.Message}");
        }
    }
}
