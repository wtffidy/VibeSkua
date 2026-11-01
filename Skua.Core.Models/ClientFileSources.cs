using System;
using System.IO;
using System.Reflection;

using System.Threading;

namespace Skua.Core.Models;

public static class ClientFileSources
{
    public static string AssemblyVersion { get; } = GetInformationalVersion();
    public static bool IsNightly { get; } = AssemblyVersion.Contains('-', StringComparison.Ordinal);
    public static string DisplayVersion { get; } = IsNightly ? AssemblyVersion + "-N" : AssemblyVersion;

    public static string LegacySkuaDIR { get; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Skua");
    public static string SkuaDIR { get; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "VibeSkua");
    public static string VibeSkuaDIR => SkuaDIR;
    public static string SkuaSettingsDIR { get; } = Path.Combine(SkuaDIR, "Skua.settings.json");
    public static string SkuaScriptsDIR { get; } = Path.Combine(SkuaDIR, "Scripts");
    public static string SkuaThemesDIR { get; } = Path.Combine(SkuaDIR, "themes");
    public static string SkuaOptionsDIR { get; } = Path.Combine(SkuaDIR, "options");
    public static string SkuaPluginsDIR { get; } = Path.Combine(SkuaDIR, "plugins");
    public static string SkuaAdvancedSkillsFile { get; } = Path.Combine(SkuaDIR, "AdvancedSkills.json");
    public static string SkuaQuestsFile { get; } = Path.Combine(SkuaDIR, "QuestData.json");
    public static string SkuaScriptsCommitFile { get; } = Path.Combine(SkuaDIR, "scripts-commit.txt");
    public static string SkuaScriptDatesFile { get; } = Path.Combine(SkuaDIR, "ScriptDates.json");
    public static string SkuaScriptsCacheFile { get; } = Path.Combine(SkuaDIR, "scripts_cache.json");
    public static string SkuaJunkItemsFile { get; } = Path.Combine(SkuaScriptsDIR, "JunkItems.json");

    private static bool _migrationAttempted;
    private static readonly object _migrationLock = new();

    public static void EnsureInitialized()
    {
        if (_migrationAttempted)
            return;

        lock (_migrationLock)
        {
            if (_migrationAttempted)
                return;
            _migrationAttempted = true;

            string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            string vibeSkuaDir = Path.Combine(appData, "VibeSkua");
            string legacySkuaDir = Path.Combine(appData, "Skua");
            string migrationMarker = Path.Combine(vibeSkuaDir, ".migration_done");

            try
            {
                bool isAlreadyMigrated = File.Exists(migrationMarker) ||
                                         File.Exists(Path.Combine(vibeSkuaDir, "Skua.settings.json")) ||
                                         File.Exists(Path.Combine(vibeSkuaDir, "ClientSettings.json"));

                if (!isAlreadyMigrated)
                {
                    string mutexName = OperatingSystem.IsWindows() ? @"Global\VibeSkua.Migration.Init" : "VibeSkua.Migration.Init";
                    using var migrationMutex = new Mutex(false, mutexName);
                    bool hasHandle = false;
                    try
                    {
                        try
                        {
                            hasHandle = migrationMutex.WaitOne(5000, false);
                        }
                        catch (AbandonedMutexException)
                        {
                            hasHandle = true;
                        }

                        bool stillNotMigrated = !File.Exists(migrationMarker) &&
                                                !File.Exists(Path.Combine(vibeSkuaDir, "Skua.settings.json")) &&
                                                !File.Exists(Path.Combine(vibeSkuaDir, "ClientSettings.json"));

                        if (stillNotMigrated)
                        {
                            Directory.CreateDirectory(vibeSkuaDir);

                            if (Directory.Exists(legacySkuaDir))
                            {
                                CopyDirectoryRecursive(legacySkuaDir, vibeSkuaDir, new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "plugins" });
                            }

                            try
                            {
                                File.WriteAllText(migrationMarker, DateTime.UtcNow.ToString("o"));
                            }
                            catch
                            {
                            }
                        }
                    }
                    finally
                    {
                        if (hasHandle)
                            migrationMutex.ReleaseMutex();
                    }
                }
            }
            catch
            {
                // Suppress migration errors to ensure client startup is uninterrupted
            }
        }
    }

    private static void CopyDirectoryRecursive(string sourceDir, string targetDir, HashSet<string>? excludedTopLevelDirs = null)
    {
        Directory.CreateDirectory(targetDir);

        foreach (string file in Directory.EnumerateFiles(sourceDir))
        {
            try
            {
                string destFileName = Path.Combine(targetDir, Path.GetFileName(file));
                if (!File.Exists(destFileName))
                {
                    File.Copy(file, destFileName, overwrite: false);
                }
            }
            catch
            {
                // Continue on individual file copy failure
            }
        }

        foreach (string subDir in Directory.EnumerateDirectories(sourceDir))
        {
            try
            {
                string subDirName = Path.GetFileName(subDir);
                if (excludedTopLevelDirs != null && excludedTopLevelDirs.Contains(subDirName))
                    continue;

                string destSubDir = Path.Combine(targetDir, subDirName);
                CopyDirectoryRecursive(subDir, destSubDir, null);
            }
            catch
            {
                // Continue on individual directory copy failure
            }
        }
    }

    private static string GetInformationalVersion()
    {
        string? informational = Assembly.GetEntryAssembly()?
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion;

        if (!string.IsNullOrWhiteSpace(informational))
            return informational.Split('+', 2)[0];

        return Assembly.GetEntryAssembly()?.GetName().Version?.ToString() ?? "0.0.0.0";
    }
}
