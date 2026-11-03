// SlopTank modification notice: added or changed by SlopTank on 2026-11-03.
using System;
using System.IO;
using System.Linq;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using Microsoft.Extensions.Logging;

namespace Emby.Server.Implementations.Plugins
{
    /// <summary>
    /// Installs the packaged Intro Skipper plugin on a clean server profile.
    /// </summary>
    internal static class BundledIntroSkipper
    {
        private const string PluginId = "c83d86bb-a1e0-4c35-a113-e2101cf4ee6b";
        private const string PluginName = "Intro Skipper";
        private const string AssemblyName = "IntroSkipper.dll";
        private const string ConfigurationFileName = "IntroSkipper.xml";

        private const string DefaultConfiguration = "<?xml version=\"1.0\" encoding=\"utf-8\"?>\n"
            + "<PluginConfiguration>\n"
            + "  <AutoDetectIntros>true</AutoDetectIntros>\n"
            + "  <ReanalyzeSettledSeasons>true</ReanalyzeSettledSeasons>\n"
            + "  <UpdateMediaSegments>true</UpdateMediaSegments>\n"
            + "  <ScanIntroduction>true</ScanIntroduction>\n"
            + "</PluginConfiguration>\n";

        /// <summary>
        /// Copies the bundled plugin into the profile if no version is already installed.
        /// </summary>
        /// <param name="pluginsPath">The plugin discovery directory.</param>
        /// <param name="plugins">The plugins discovered during startup.</param>
        /// <param name="loadManifest">The server's standard plugin manifest loader.</param>
        /// <param name="logger">The plugin manager logger.</param>
        /// <returns><c>true</c> when a new plugin directory was published.</returns>
        internal static bool InstallIfNeeded(
            string pluginsPath,
            System.Collections.Generic.IReadOnlyCollection<LocalPlugin> plugins,
            Func<string, LocalPlugin> loadManifest,
            ILogger logger)
        {
            var sourcePath = Path.Combine(AppContext.BaseDirectory, "defaults", "intro-skipper");
            if (!Directory.Exists(sourcePath))
            {
                return false;
            }

            try
            {
                if (plugins.Any(plugin => plugin.Id.Equals(Guid.Parse(PluginId))) || HasInstalledPlugin(pluginsPath, loadManifest))
                {
                    return false;
                }

                var sourcePlugin = loadManifest(sourcePath);
                ValidateBundle(sourcePath, sourcePlugin);

                var pluginVersion = sourcePlugin.Manifest.Version;
                if (!Version.TryParse(pluginVersion, out _))
                {
                    throw new InvalidDataException($"Bundled plugin version is invalid: {pluginVersion}");
                }

                var destinationPath = Path.Combine(pluginsPath, PluginName + "_" + pluginVersion);
                var profilePath = Directory.GetParent(Path.GetFullPath(pluginsPath))?.FullName
                    ?? throw new InvalidOperationException($"Plugin directory has no profile parent: {pluginsPath}");
                var stagingPath = Path.Combine(profilePath, ".intro-skipper-stage-" + Guid.NewGuid().ToString("N"));

                try
                {
                    CopyDirectory(sourcePath, stagingPath);
                    SeedConfiguration(Path.Combine(pluginsPath, "configurations", ConfigurationFileName), logger);
                    Directory.CreateDirectory(pluginsPath);
                    Directory.Move(stagingPath, destinationPath);
                    return true;
                }
                finally
                {
                    if (Directory.Exists(stagingPath))
                    {
                        try
                        {
                            Directory.Delete(stagingPath, recursive: true);
                        }
                        catch (IOException cleanupError)
                        {
                            logger.LogWarning(cleanupError, "Could not remove interrupted plugin staging at {Path}.", stagingPath);
                        }
                        catch (UnauthorizedAccessException cleanupError)
                        {
                            logger.LogWarning(cleanupError, "Access denied removing interrupted plugin staging at {Path}.", stagingPath);
                        }
                    }
                }
            }
#pragma warning disable CA1031 // Preserve the bundle failure and its path before startup reports it.
            catch (Exception exception)
#pragma warning restore CA1031
            {
                logger.LogError(exception, "Unable to install bundled Intro Skipper from {Path}.", sourcePath);
                throw;
            }
        }

        private static bool HasInstalledPlugin(string pluginsPath, Func<string, LocalPlugin> loadManifest)
        {
            if (!Directory.Exists(pluginsPath))
            {
                return false;
            }

            return Directory.EnumerateDirectories(pluginsPath, "*", SearchOption.TopDirectoryOnly)
                .Any(path => loadManifest(path).Id.Equals(Guid.Parse(PluginId)));
        }

        private static void ValidateBundle(string sourcePath, LocalPlugin plugin)
        {
            if (!plugin.Id.Equals(Guid.Parse(PluginId)))
            {
                throw new InvalidDataException($"Bundled plugin GUID is unexpected: {plugin.Id}");
            }

            if (!string.Equals(plugin.Name, PluginName, StringComparison.Ordinal))
            {
                throw new InvalidDataException($"Bundled plugin name is unexpected: {plugin.Name}");
            }

            if (!Version.TryParse(plugin.Manifest.Version, out _)
                || !Version.TryParse(plugin.Manifest.TargetAbi, out _)
                || plugin.Manifest.Status != PluginStatus.Active)
            {
                throw new InvalidDataException("Bundled plugin manifest has an invalid version, target ABI, or status.");
            }

            if ((plugin.Manifest.Assemblies.Count > 0
                    && !plugin.Manifest.Assemblies.Any(assembly => string.Equals(assembly, AssemblyName, StringComparison.OrdinalIgnoreCase)))
                || !File.Exists(Path.Combine(sourcePath, AssemblyName)))
            {
                throw new InvalidDataException($"Bundled plugin manifest or payload is missing {AssemblyName}.");
            }

            if (!plugin.IsEnabledAndSupported)
            {
                throw new InvalidDataException($"Bundled plugin target ABI {plugin.Manifest.TargetAbi} is not supported.");
            }
        }

        private static void SeedConfiguration(string configurationPath, ILogger logger)
        {
            if (File.Exists(configurationPath))
            {
                return;
            }

            var configurationDirectory = Path.GetDirectoryName(configurationPath)
                ?? throw new InvalidOperationException($"Configuration path has no parent: {configurationPath}");
            Directory.CreateDirectory(configurationDirectory);
            var temporaryPath = configurationPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                File.WriteAllText(temporaryPath, DefaultConfiguration);
                try
                {
                    File.Move(temporaryPath, configurationPath);
                }
                catch (IOException exception) when (File.Exists(configurationPath))
                {
                    logger.LogDebug(exception, "Retaining Intro Skipper settings created by another startup writer at {Path}.", configurationPath);
                }
            }
            finally
            {
                if (File.Exists(temporaryPath))
                {
                    File.Delete(temporaryPath);
                }
            }
        }

        private static void CopyDirectory(string sourcePath, string destinationPath)
        {
            var sourceAttributes = File.GetAttributes(sourcePath);
            if ((sourceAttributes & FileAttributes.ReparsePoint) != 0)
            {
                throw new InvalidDataException($"Bundled plugin contains a reparse point: {sourcePath}");
            }

            Directory.CreateDirectory(destinationPath);
            foreach (var sourceFile in Directory.EnumerateFiles(sourcePath, "*", SearchOption.TopDirectoryOnly))
            {
                var attributes = File.GetAttributes(sourceFile);
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                {
                    throw new InvalidDataException($"Bundled plugin contains a reparse point: {sourceFile}");
                }

                File.Copy(sourceFile, Path.Combine(destinationPath, Path.GetFileName(sourceFile)));
            }

            foreach (var sourceDirectory in Directory.EnumerateDirectories(sourcePath, "*", SearchOption.TopDirectoryOnly))
            {
                if ((File.GetAttributes(sourceDirectory) & FileAttributes.ReparsePoint) != 0)
                {
                    throw new InvalidDataException($"Bundled plugin contains a reparse point: {sourceDirectory}");
                }

                CopyDirectory(sourceDirectory, Path.Combine(destinationPath, Path.GetFileName(sourceDirectory)));
            }
        }
    }
}
