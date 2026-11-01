using System;
using System.IO;

namespace Skua.Manager.Avalonia.Services;

internal static class SkuaExecutableLocator
{
    public static string ExecutablePath { get; } = Resolve();

    public static string WorkingDirectory =>
        Path.GetDirectoryName(ExecutablePath) ?? AppContext.BaseDirectory;

    private static string Resolve()
    {
        return OperatingSystem.IsWindows() ? ResolveWindows() : ResolveUnix();
    }

    private static string ResolveWindows()
    {
        string vibePath = Path.Combine(AppContext.BaseDirectory, "VibeSkua.exe");
        if (File.Exists(vibePath))
            return vibePath;

        string deployedPath = Path.Combine(AppContext.BaseDirectory, "Skua.exe");
        if (File.Exists(deployedPath))
            return deployedPath;

        string repositoryRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
        string[] candidates =
        [
            Path.Combine(repositoryRoot, "Build", "VibeSkua.exe"),
            Path.Combine(repositoryRoot, "Build", "Skua.exe"),
            Path.Combine(repositoryRoot, "Skua.App.Avalonia", "bin", "Release", "net10.0-windows", "VibeSkua.exe"),
            Path.Combine(repositoryRoot, "Skua.App.Avalonia", "bin", "Release", "net10.0-windows", "Skua.exe"),
            Path.Combine(repositoryRoot, "Skua.App.Avalonia", "bin", "Release", "net10.0-windows", "win-x64", "VibeSkua.exe"),
            Path.Combine(repositoryRoot, "Skua.App.Avalonia", "bin", "Release", "net10.0-windows", "win-x64", "Skua.exe"),
            Path.Combine(repositoryRoot, "Skua.App.Avalonia", "bin", "Debug", "net10.0-windows", "VibeSkua.exe"),
            Path.Combine(repositoryRoot, "Skua.App.Avalonia", "bin", "Debug", "net10.0-windows", "Skua.exe"),
            Path.Combine(repositoryRoot, "Skua.App.Avalonia", "bin", "Debug", "net10.0-windows", "win-x64", "VibeSkua.exe"),
            Path.Combine(repositoryRoot, "Skua.App.Avalonia", "bin", "Debug", "net10.0-windows", "win-x64", "Skua.exe")
        ];

        foreach (string candidate in candidates)
        {
            if (File.Exists(candidate))
                return candidate;
        }

        return deployedPath;
    }

    private static string ResolveUnix()
    {
        string userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string repositoryRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
        string[] candidates =
        [
            Path.Combine(AppContext.BaseDirectory, "VibeSkua"),
            Path.Combine(AppContext.BaseDirectory, "VibeSkua.app", "Contents", "MacOS", "VibeSkua"),
            Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "VibeSkua.app", "Contents", "MacOS", "VibeSkua")),
            Path.Combine("/Applications", "VibeSkua.app", "Contents", "MacOS", "VibeSkua"),
            Path.Combine(userProfile, "Applications", "VibeSkua.app", "Contents", "MacOS", "VibeSkua"),
            Path.Combine(repositoryRoot, "Build", "VibeSkua"),
            Path.Combine(repositoryRoot, "Build", "VibeSkua.app", "Contents", "MacOS", "VibeSkua"),
            Path.Combine(repositoryRoot, "Skua.App.Avalonia", "bin", "Release", "net10.0", "osx-arm64", "VibeSkua"),
            Path.Combine(repositoryRoot, "Skua.App.Avalonia", "bin", "Release", "net10.0", "osx-x64", "VibeSkua"),
            Path.Combine(repositoryRoot, "Skua.App.Avalonia", "bin", "Debug", "net10.0", "osx-arm64", "VibeSkua"),
            Path.Combine(repositoryRoot, "Skua.App.Avalonia", "bin", "Debug", "net10.0", "osx-x64", "VibeSkua")
        ];

        foreach (string candidate in candidates)
        {
            if (File.Exists(candidate))
                return candidate;
        }

        return candidates[0];
    }
}
