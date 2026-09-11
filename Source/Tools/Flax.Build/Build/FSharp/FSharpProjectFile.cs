// Copyright (c) Wojciech Figat. All rights reserved.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml.Linq;

namespace Flax.Build
{
    /// <summary>
    /// The items of an F# project file (.fsproj) Flax.Build compiles it from: the source files in compile order, the NuGet packages and the assemblies referenced by path.
    /// </summary>
    /// <remarks>
    /// The project file is the single source of truth for them, as it is for an IDE opening it. MSBuild itself is not run, so MSBuild properties, wildcards and conditions are not evaluated: items using them are skipped.
    /// </remarks>
    public sealed class FSharpProjectFile
    {
        /// <summary>
        /// A NuGet package reference.
        /// </summary>
        public struct PackageReference
        {
            /// <summary>
            /// The package id.
            /// </summary>
            public string Name;

            /// <summary>
            /// The exact package version.
            /// </summary>
            public string Version;
        }

        /// <summary>
        /// The project file path (absolute).
        /// </summary>
        public string FilePath;

        /// <summary>
        /// The source files in compile order (absolute paths). F# code can only use what precedes it, so the order is significant and never changed.
        /// </summary>
        public readonly List<string> CompileItems = new List<string>();

        /// <summary>
        /// The NuGet packages the code uses.
        /// </summary>
        public readonly List<PackageReference> PackageReferences = new List<PackageReference>();

        /// <summary>
        /// The assemblies referenced by path (Reference items with a HintPath), as absolute paths.
        /// </summary>
        public readonly List<string> References = new List<string>();

        /// <summary>
        /// Loads the project file.
        /// </summary>
        /// <param name="path">The project file path.</param>
        /// <returns>The project file items.</returns>
        public static FSharpProjectFile Load(string path)
        {
            var result = new FSharpProjectFile { FilePath = Path.GetFullPath(path) };
            var folder = Path.GetDirectoryName(result.FilePath);
            var document = XDocument.Load(result.FilePath);
            foreach (var item in document.Descendants().Where(x => x.Parent != null && x.Parent.Name.LocalName == "ItemGroup"))
            {
                var include = (string)item.Attribute("Include");
                if (string.IsNullOrEmpty(include))
                    continue;
                switch (item.Name.LocalName)
                {
                case "Compile":
                    if (IsEvaluated(include))
                        Log.Warning($"F# project {result.FilePath}: <Compile Include=\"{include}\"> uses MSBuild properties or wildcards, which Flax.Build does not evaluate. List the source files explicitly.");
                    else
                        result.CompileItems.Add(Path.GetFullPath(Path.Combine(folder, include)));
                    break;
                case "PackageReference":
                {
                    var version = (string)item.Attribute("Version") ?? (string)item.Elements().FirstOrDefault(x => x.Name.LocalName == "Version");
                    if (string.IsNullOrEmpty(version) || IsEvaluated(version))
                        Log.Warning($"F# project {result.FilePath}: package {include} has no exact version, so it is skipped.");
                    else
                        result.PackageReferences.Add(new PackageReference { Name = include, Version = version });
                    break;
                }
                case "Reference":
                {
                    // References without a path are framework assemblies, and ones using properties are resolved by the build itself (eg. the engine assembly an IDE finds through the generated props file)
                    var hintPath = (string)item.Elements().FirstOrDefault(x => x.Name.LocalName == "HintPath");
                    if (!string.IsNullOrEmpty(hintPath) && !IsEvaluated(hintPath))
                        result.References.Add(Path.GetFullPath(Path.Combine(folder, hintPath)));
                    break;
                }
                }
            }
            return result;
        }

        /// <summary>
        /// Selects the framework of the NuGet package assemblies (a folder name in the package lib folder) to use on the given .NET runtime: the newest .NET (5 or newer) that is not newer than the runtime, then .NET Core, then .NET Standard.
        /// </summary>
        /// <param name="frameworks">The frameworks the package has assemblies for (eg. net8.0, netstandard2.0).</param>
        /// <param name="runtimeFramework">The runtime framework (eg. net8.0).</param>
        /// <returns>The framework to use, or null if none can be used (eg. a .NET Framework only package).</returns>
        public static string SelectLibFramework(IEnumerable<string> frameworks, string runtimeFramework)
        {
            if (!Version.TryParse(runtimeFramework.Substring(3), out var runtime))
                throw new ArgumentException($"Invalid runtime framework {runtimeFramework}.", nameof(runtimeFramework));

            string result = null;
            var resultRank = -1;
            Version resultVersion = null;
            foreach (var framework in frameworks)
            {
                int rank;
                Version version;
                if (framework.StartsWith("netstandard", StringComparison.OrdinalIgnoreCase) && Version.TryParse(framework.Substring(11), out version))
                    rank = 0;
                else if (framework.StartsWith("netcoreapp", StringComparison.OrdinalIgnoreCase) && Version.TryParse(framework.Substring(10), out version) && version <= runtime)
                    rank = 1;
                else if (framework.StartsWith("net", StringComparison.OrdinalIgnoreCase) && Version.TryParse(framework.Substring(3), out version) && version.Major >= 5 && version <= runtime)
                    rank = 2; // Skips .NET Framework (eg. net45) and platform specific ones (eg. net8.0-windows)
                else
                    continue;
                if (rank > resultRank || (rank == resultRank && version > resultVersion))
                {
                    result = framework;
                    resultRank = rank;
                    resultVersion = version;
                }
            }
            return result;
        }

        private static bool IsEvaluated(string value)
        {
            return value.Contains("$(") || value.Contains('*') || value.Contains('?');
        }
    }
}
