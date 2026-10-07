using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Drawing.Imaging;
using System.Text.RegularExpressions;
using AvaloniaBitmap = Avalonia.Media.Imaging.Bitmap;
using Microsoft.Win32;
using System.Diagnostics;
using System.Runtime.Versioning;

namespace StellarModManager.Services;


public enum GameInstallSource { Steam }
public record GameInstallationInfo(DirectoryInfo Directory, AvaloniaBitmap? Icon, GameInstallSource Source);

public static partial class GameLocatorService
{
    public const string ExecutableName = "StellarDrive.exe";
    public const string GamesDirectory = "steamapps/common/";
    public const string LibraryVdfPath = "steamapps/libraryfolders.vdf";

    // todo: do we really neeed to have this? registry has this, but not removing it for now, just in case
    // trying both because I think Steam switched to 64 bit? My install is in the 32 directory tho... 
    public static readonly string[] ProgramFilesPaths =
    [
        Environment.ExpandEnvironmentVariables("%ProgramW6432%"),     // 64 bit
        Environment.ExpandEnvironmentVariables("%ProgramFiles(x86)%") // 32 bit
    ];

    //using registry reference is safer than assuming program files. 
    [SupportedOSPlatform("windows")]
    public static readonly (RegistryKey root, string subKey, string valueName)[] RegistryPaths =
    [
        (Registry.CurrentUser, @"SOFTWARE\Valve\Steam", "SteamPath"),
        (Registry.LocalMachine, @"SOFTWARE\WOW6432Node\Valve\Steam", "InstallPath")
    ];

    [GeneratedRegex("""(?:"path")(?:\s*)(?:")(?<path>.*)(?:")""")]
    private static partial Regex PathExtractionRegex();

    public static IReadOnlyList<GameInstallationInfo> Locate()
    {
        if (!OperatingSystem.IsWindows()) return [];

        try
        {
            var installs = GetSteamLibraryDirectories()
                .Where(d => d.Exists)
                .SelectMany(d => d.GetDirectories())
                .Where(d => d.Name.Contains("StellarDrive"))
                .Select(d => CollectInstallInfo(d, GameInstallSource.Steam))
                .OfType<GameInstallationInfo>();

            return [.. installs];
        }
        catch
        {
            return [];
        }
    }

    private static IEnumerable<DirectoryInfo> GetSteamPathFromRegistry()
    {
        if (!OperatingSystem.IsWindows()) return [];

        var ret = new List<DirectoryInfo>();
        foreach (var (root, subKey, valueName) in RegistryPaths)
        {
            var steamPath = root.OpenSubKey(subKey)?.GetValue(valueName) as string;
            if (steamPath is not null)
            {
                ret.Add(new DirectoryInfo(steamPath));
            }
        }
        return ret;
    }


    private static IEnumerable<DirectoryInfo> GetSteamLibraryDirectories()
    {
        var PotentialSteamPaths = GetSteamPathFromRegistry().ToList();
        PotentialSteamPaths.AddRange(ProgramFilesPaths.Select(
            p => new DirectoryInfo(Path.Combine(p, "steam"))
        ));

        string? fileContent = null;
        foreach (var PotentialSteam in PotentialSteamPaths)
        {
            if (!PotentialSteam.Exists) continue;

            var filePath = Path.Combine(PotentialSteam.FullName, LibraryVdfPath);

            if (Path.Exists(filePath))
            {
                fileContent = File.ReadAllText(filePath);
                break;
            }
        }
        if (fileContent is null) return [];

        var gamePaths = PathExtractionRegex()
            .Matches(fileContent)
            .Select(m => new DirectoryInfo(Path.Combine(m.Groups["path"].Value.Replace(@"\\", @"\"), GamesDirectory)))
            .DistinctBy(d => d.FullName);

        return gamePaths;
    }

    private static GameInstallationInfo? CollectInstallInfo(DirectoryInfo directory, GameInstallSource source)
    {
        var executablePath = Path.Combine(directory.FullName, ExecutableName);
        if (!Path.Exists(executablePath)) return null;

        AvaloniaBitmap? icon = null;
        if (OperatingSystem.IsWindowsVersionAtLeast(major: 6, minor: 1))
        {
            using var fileIcon = Icon.ExtractAssociatedIcon(executablePath);
            using var systemBitmap = fileIcon?.ToBitmap();

            // https://github.com/AvaloniaUI/Avalonia/discussions/5908
            var data = systemBitmap?.LockBits(new Rectangle(0, 0, systemBitmap.Width, systemBitmap.Height), ImageLockMode.ReadWrite, PixelFormat.Format32bppArgb);
            if (data is not null)
            {
                icon = new AvaloniaBitmap
                (
                    format: Avalonia.Platform.PixelFormat.Bgra8888,
                    alphaFormat: Avalonia.Platform.AlphaFormat.Premul,
                    data: data.Scan0,
                    size: new Avalonia.PixelSize(data.Width, data.Height),
                    dpi: new Avalonia.Vector(96, 96),
                    stride: data.Stride
                );
                systemBitmap?.UnlockBits(data);
            }
        }

        return new(directory, icon, source);
    }
}
