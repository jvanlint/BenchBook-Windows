using BenchBook.Core;
using BenchBook.Core.Model;
using Windows.System;

namespace BenchBook.App.Services;

/// <summary>Opens files in their default app and links in the browser.</summary>
internal static class Opener
{
    /// <summary>
    /// Opens a copy of the original, named as the user knows it, so another app
    /// saving over it can never change the stored file.
    /// </summary>
    public static async Task<bool> OpenInDefaultApp(LibraryLocation location, Asset asset)
    {
        if (location.FullPath(asset.OriginalRelativePath) is not { } original || !File.Exists(original)) return false;
        var folder = Path.Combine(Path.GetTempPath(), "BenchBook Open", asset.Id);
        Directory.CreateDirectory(folder);
        var name = SafeName(asset.OriginalFilename) ?? Path.GetFileName(original);
        if (!Path.HasExtension(name)) name += Path.GetExtension(original);
        var copy = Path.Combine(folder, name);
        await Task.Run(() => File.Copy(original, copy, overwrite: true));
        var file = await Windows.Storage.StorageFile.GetFileFromPathAsync(copy);
        return await Launcher.LaunchFileAsync(file);
    }

    public static async Task<bool> OpenLink(string? address) =>
        Uri.TryCreate(address, UriKind.Absolute, out var url) && url.Scheme is "http" or "https" && await Launcher.LaunchUriAsync(url);

    private static string? SafeName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        var clean = string.Concat(name.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c)).Trim().TrimEnd('.');
        return LibraryPaths.IsSafeName(clean) ? clean : null;
    }
}

/// <summary>Tag and markup colours, by their stored names (behaviour.md, "Colours").</summary>
internal static class NamedColors
{
    public static Windows.UI.Color? Color(string? name) => name switch
    {
        "red" => Hex(0xFF3B30),
        "orange" => Hex(0xFF9500),
        "yellow" => Hex(0xFFCC00),
        "green" => Hex(0x34C759),
        "mint" => Hex(0x00C7BE),
        "teal" => Hex(0x30B0C7),
        "blue" => Hex(0x007AFF),
        "indigo" => Hex(0x5856D6),
        "purple" => Hex(0xAF52DE),
        "pink" => Hex(0xFF2D55),
        "brown" => Hex(0xA2845E),
        "gray" => Hex(0x8E8E93),
        _ => null,
    };

    /// <summary>The tag's colour, or the system accent colour when it has none.</summary>
    public static Windows.UI.Color TagColor(string? name) =>
        Color(name) ?? (Windows.UI.Color)Microsoft.UI.Xaml.Application.Current.Resources["SystemAccentColor"];

    public static string DisplayName(string name) => name == "gray" ? "Grey" : char.ToUpperInvariant(name[0]) + name[1..];

    private static Windows.UI.Color Hex(int rgb) =>
        Windows.UI.Color.FromArgb(255, (byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);
}
