// Copyright (c) Wojciech Figat. All rights reserved.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Flax.Build
{
    /// <summary>
    /// The items of an F# project file (.fsproj) Flax.Build compiles it from: the source files in compile order, the assemblies the code references and the files to deploy with it.
    /// </summary>
    /// <remarks>
    /// The project file is evaluated by MSBuild itself (with a restore), so it means exactly what it means to an IDE: properties, conditions, wildcards and imports are evaluated and NuGet resolves the packages (floating versions, central package management and package dependencies included). As that takes about a second, the result is cached and evaluated again only when the project file or anything it imports changes, the evaluation properties change, source files are added or removed in the project folder, or a resolved file disappears.
    /// </remarks>
    public sealed class FSharpProjectFile
    {
        private const string CacheVersion = "1";

        /// <summary>
        /// The project file path (absolute).
        /// </summary>
        public string FilePath;

        /// <summary>
        /// The source files in compile order (absolute paths). F# code can only use what precedes it, so the order is significant and never changed.
        /// </summary>
        public readonly List<string> CompileItems = new List<string>();

        /// <summary>
        /// The assemblies the code references (absolute paths): the compile-time assemblies of the NuGet packages and the assemblies referenced by path, without the .NET framework ones.
        /// </summary>
        public readonly List<string> References = new List<string>();

        /// <summary>
        /// The files to deploy next to the compiled assembly (absolute paths): the runtime files of the NuGet packages, their dependencies included, and the assemblies referenced by path that are copied locally.
        /// </summary>
        public readonly List<string> CopyLocalFiles = new List<string>();

        /// <summary>
        /// True if the items come from the cache of an earlier evaluation, false if MSBuild evaluated the project now.
        /// </summary>
        public bool FromCache;

        /// <summary>
        /// Loads the project file items, evaluating the project with MSBuild unless the cached result is up to date.
        /// </summary>
        /// <param name="projectFilePath">The project file path.</param>
        /// <param name="intermediateFolder">The folder for the cache and for the restore output (obj).</param>
        /// <param name="properties">The MSBuild global properties to evaluate the project with (eg. Configuration, TargetFramework). Values cannot contain semicolons.</param>
        /// <param name="dotnetPath">The dotnet executable path.</param>
        /// <returns>The project file items.</returns>
        public static FSharpProjectFile Load(string projectFilePath, string intermediateFolder, IReadOnlyDictionary<string, string> properties, string dotnetPath = "dotnet")
        {
            projectFilePath = Path.GetFullPath(projectFilePath);
            intermediateFolder = Path.GetFullPath(intermediateFolder);
            Directory.CreateDirectory(intermediateFolder);
            var cachePath = Path.Combine(intermediateFolder, Path.GetFileNameWithoutExtension(projectFilePath) + ".Items.cache");
            var sourceFiles = GetSourceFilesSignature(Path.GetDirectoryName(projectFilePath));

            var result = ReadCache(cachePath, projectFilePath, properties, sourceFiles);
            if (result != null)
            {
                result.FromCache = true;
                return result;
            }

            Log.Info($"Evaluating F# project {projectFilePath}");
            result = Evaluate(projectFilePath, intermediateFolder, properties, dotnetPath, out var imports);
            WriteCache(cachePath, result, properties, sourceFiles, imports);
            return result;
        }

        private static FSharpProjectFile Evaluate(string projectFilePath, string intermediateFolder, IReadOnlyDictionary<string, string> properties, string dotnetPath, out List<string> imports)
        {
            var restoreFolder = Path.Combine(intermediateFolder, "obj").Replace('\\', '/') + '/';
            var startInfo = new ProcessStartInfo(dotnetPath)
            {
                WorkingDirectory = Path.GetDirectoryName(projectFilePath),
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            startInfo.ArgumentList.Add("msbuild");
            startInfo.ArgumentList.Add(projectFilePath);
            startInfo.ArgumentList.Add("-restore");
            startInfo.ArgumentList.Add("-nologo");
            startInfo.ArgumentList.Add("-nodeReuse:false");
            startInfo.ArgumentList.Add("-verbosity:quiet");

            // Resolving the references is what makes NuGet pick the package assemblies (the same target a build runs before the compiler)
            startInfo.ArgumentList.Add("-target:ResolveAssemblyReferences");
            startInfo.ArgumentList.Add("-getItem:Compile");
            startInfo.ArgumentList.Add("-getItem:ReferencePath");
            startInfo.ArgumentList.Add("-getItem:ReferenceCopyLocalPaths");
            startInfo.ArgumentList.Add("-getProperty:MSBuildAllProjects");

            // Package runtime files are copied locally for libraries too, and the restore output stays out of the project folder
            startInfo.ArgumentList.Add("-property:CopyLocalLockFileAssemblies=true");
            startInfo.ArgumentList.Add("-property:BaseIntermediateOutputPath=" + restoreFolder);
            startInfo.ArgumentList.Add("-property:MSBuildProjectExtensionsPath=" + restoreFolder);
            foreach (var e in properties)
            {
                if (e.Value.Contains(';'))
                    throw new ArgumentException($"MSBuild property {e.Key} value cannot contain semicolons: {e.Value}", nameof(properties));
                startInfo.ArgumentList.Add($"-property:{e.Key}={e.Value}");
            }

            // Run the SDK MSBuild, not the one Flax.Build might have been started from
            foreach (var key in startInfo.Environment.Keys.Where(x => x.StartsWith("MSBuild", StringComparison.OrdinalIgnoreCase)).ToArray())
                startInfo.Environment.Remove(key);

            string output, error;
            int exitCode;
            using (var process = Process.Start(startInfo))
            {
                var outputTask = process.StandardOutput.ReadToEndAsync();
                var errorTask = process.StandardError.ReadToEndAsync();
                process.WaitForExit();
                output = outputTask.Result;
                error = errorTask.Result;
                exitCode = process.ExitCode;
            }
            if (exitCode != 0)
                throw new Exception($"Failed to evaluate F# project {projectFilePath}:\n{output}{error}");

            var result = new FSharpProjectFile { FilePath = projectFilePath };
            imports = new List<string> { projectFilePath };
            try
            {
                using (var document = JsonDocument.Parse(output))
                {
                    var root = document.RootElement;
                    var items = root.GetProperty("Items");
                    foreach (var item in GetItems(items, "Compile"))
                        result.CompileItems.Add(GetMetadata(item, "FullPath"));
                    foreach (var item in GetItems(items, "ReferencePath"))
                    {
                        // The framework is referenced by the build itself
                        if (string.IsNullOrEmpty(GetMetadata(item, "FrameworkReferenceName")))
                            result.References.Add(GetMetadata(item, "FullPath"));
                    }
                    foreach (var item in GetItems(items, "ReferenceCopyLocalPaths"))
                        result.CopyLocalFiles.Add(GetMetadata(item, "FullPath"));
                    if (root.TryGetProperty("Properties", out var evaluatedProperties) && evaluatedProperties.TryGetProperty("MSBuildAllProjects", out var allProjects))
                    {
                        foreach (var path in (allProjects.GetString() ?? string.Empty).Split(';'))
                        {
                            if (!string.IsNullOrWhiteSpace(path))
                                imports.Add(Path.GetFullPath(path.Trim()));
                        }
                    }
                }
            }
            catch (JsonException ex)
            {
                throw new Exception($"Failed to read the evaluation of F# project {projectFilePath}:\n{output}{error}", ex);
            }
            return result;
        }

        private static IEnumerable<JsonElement> GetItems(JsonElement items, string name)
        {
            return items.TryGetProperty(name, out var list) ? list.EnumerateArray() : Enumerable.Empty<JsonElement>();
        }

        private static string GetMetadata(JsonElement item, string name)
        {
            return item.TryGetProperty(name, out var value) ? value.GetString() : null;
        }

        /// <summary>
        /// Gets the signature of the F# source files in the project folder: adding or removing one can change what a wildcard includes, without changing any imported file.
        /// </summary>
        private static string GetSourceFilesSignature(string projectFolder)
        {
            var files = Directory.EnumerateFiles(projectFolder, "*.*", SearchOption.AllDirectories)
                                 .Where(x => x.EndsWith(".fs", StringComparison.OrdinalIgnoreCase) || x.EndsWith(".fsi", StringComparison.OrdinalIgnoreCase))
                                 .Select(x => Path.GetRelativePath(projectFolder, x).Replace('\\', '/').ToLowerInvariant())
                                 .Where(x => !x.StartsWith("obj/") && !x.StartsWith("bin/"))
                                 .OrderBy(x => x, StringComparer.Ordinal);
            return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join("|", files))));
        }

        private static long GetTimestamp(string path)
        {
            return File.Exists(path) ? File.GetLastWriteTimeUtc(path).Ticks : -1;
        }

        private static void WriteCache(string cachePath, FSharpProjectFile result, IReadOnlyDictionary<string, string> properties, string sourceFiles, List<string> imports)
        {
            var lines = new List<string> { "version " + CacheVersion, "sources " + sourceFiles };
            lines.AddRange(properties.Select(x => $"property {x.Key}={x.Value}"));
            lines.AddRange(imports.Distinct(StringComparer.OrdinalIgnoreCase).Select(x => $"import {GetTimestamp(x)} {x}"));
            lines.AddRange(result.CompileItems.Select(x => "compile " + x));
            lines.AddRange(result.References.Select(x => "reference " + x));
            lines.AddRange(result.CopyLocalFiles.Select(x => "copy " + x));
            File.WriteAllLines(cachePath, lines);
        }

        private static FSharpProjectFile ReadCache(string cachePath, string projectFilePath, IReadOnlyDictionary<string, string> properties, string sourceFiles)
        {
            if (!File.Exists(cachePath))
                return null;
            var result = new FSharpProjectFile { FilePath = projectFilePath };
            var cachedProperties = new Dictionary<string, string>();
            bool validVersion = false, validSources = false;
            foreach (var line in File.ReadAllLines(cachePath))
            {
                var separator = line.IndexOf(' ');
                if (separator < 0)
                    return null;
                var value = line.Substring(separator + 1);
                switch (line.Substring(0, separator))
                {
                case "version":
                    validVersion = value == CacheVersion;
                    break;
                case "sources":
                    validSources = value == sourceFiles;
                    break;
                case "property":
                {
                    var equals = value.IndexOf('=');
                    if (equals < 0)
                        return null;
                    cachedProperties[value.Substring(0, equals)] = value.Substring(equals + 1);
                    break;
                }
                case "import":
                {
                    // An imported file that changed (or appeared, or disappeared)
                    var pathStart = value.IndexOf(' ');
                    if (pathStart < 0 || !long.TryParse(value.Substring(0, pathStart), out var timestamp) || timestamp != GetTimestamp(value.Substring(pathStart + 1)))
                        return null;
                    break;
                }
                case "compile":
                    result.CompileItems.Add(value);
                    break;
                case "reference":
                    result.References.Add(value);
                    break;
                case "copy":
                    result.CopyLocalFiles.Add(value);
                    break;
                default:
                    return null;
                }
            }
            if (!validVersion || !validSources)
                return null;
            if (cachedProperties.Count != properties.Count || properties.Any(x => !cachedProperties.TryGetValue(x.Key, out var v) || v != x.Value))
                return null;

            // A resolved file that is gone (eg. a package removed from the NuGet cache)
            if (result.References.Concat(result.CopyLocalFiles).Any(x => !File.Exists(x)))
                return null;
            return result;
        }
    }
}
