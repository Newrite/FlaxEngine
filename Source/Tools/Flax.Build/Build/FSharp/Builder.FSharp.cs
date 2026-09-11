// Copyright (c) Wojciech Figat. All rights reserved.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security;
using System.Security.Cryptography;
using System.Text;
using Flax.Build.Graph;
using Flax.Build.NativeCpp;
using Flax.Build.Projects;
using Flax.Build.Projects.VisualStudio;

namespace Flax.Build
{
    static partial class Builder
    {
        /// <summary>
        /// Gets the assembly an F# module compiles into: next to the C# assemblies of the target, named after its binary module.
        /// </summary>
        internal static string GetFSharpAssemblyPath(BuildData buildData, Module module)
        {
            var outputPath = Path.GetDirectoryName(buildData.Target.GetOutputFilePath(buildData.TargetOptions));
            return Path.Combine(outputPath, module.BinaryModuleName + ".dll");
        }

        /// <summary>
        /// Gets the managed assembly of the binary module if it is an F# module.
        /// </summary>
        internal static bool TryGetFSharpBinaryModulePath(BuildData buildData, IGrouping<string, Module> binaryModule, out string path)
        {
            var module = binaryModule.FirstOrDefault(x => x is FSharpModule);
            path = module != null ? GetFSharpAssemblyPath(buildData, module) : null;
            return module != null;
        }

        /// <summary>
        /// Adds the task compiling the F# module to the build graph.
        /// </summary>
        /// <remarks>
        /// The compiler is fsc from the .NET SDK the engine compiles its C# with, run directly (not through MSBuild) and through the warm compiler host when it is built (Source/Tools/FlaxFSharpCompiler). The task is ordered after the modules it depends on and before the modules depending on it by the task graph itself: it takes their assemblies as prerequisites, and they take its assembly (see <see cref="BuildTargetBindings"/>).
        /// </remarks>
        private static void BuildFSharpModule(BuildData buildData, FSharpModule module, BuildOptions moduleOptions)
        {
            if (buildData.Target.IsPreBuilt)
                return;
            if (buildData.BinaryModules != null && buildData.BinaryModules.Any(x => x.Contains(module) && x.Count() != 1))
                throw new Exception($"F# module {module.Name} must be the only module in its binary module {module.BinaryModuleName}.");
            var projectFilePath = module.ProjectFilePath;
            if (!File.Exists(projectFilePath))
                throw new Exception($"Missing F# project file {projectFilePath} of module {module.Name}.");
            var project = FSharpProjectFile.Load(projectFilePath);
            var graph = buildData.Graph;
            var assemblyPath = GetFSharpAssemblyPath(buildData, module);
            var outputPath = Path.GetDirectoryName(assemblyPath);
            var dotnetSdk = DotNetSdk.Instance;
            if (!dotnetSdk.IsValid)
                throw new DotNetSdk.MissingException();
            var runtimeVersionParts = dotnetSdk.RuntimeVersionName.Split('.');
            var runtimeFramework = $"net{runtimeVersionParts[0]}.{runtimeVersionParts[1]}";

            // References: what the dependencies reference (eg. FSharp.Core and Newtonsoft.Json from the engine), the assemblies of the dependency modules, the NuGet packages and the assemblies from the project file
            var references = new List<string>();
            void AddReference(string path)
            {
                if (!string.IsNullOrEmpty(path) && !references.Contains(path, StringComparer.OrdinalIgnoreCase))
                    references.Add(path);
            }
            foreach (var reference in moduleOptions.ScriptingAPI.FileReferences)
                AddReference(reference);
            var dependencyAssemblies = GetFSharpDependencyAssemblies(buildData, module, moduleOptions).ToList();
            var nugetPath = Utilities.GetNugetPackagesPath();
            dependencyAssemblies.AddRange(GetFSharpDependencyNugetPackages(buildData, moduleOptions).Select(x => x.GetLibPath(nugetPath)).Where(x => !string.IsNullOrEmpty(x)));
            foreach (var reference in dependencyAssemblies)
                AddReference(reference);
            foreach (var package in project.PackageReferences)
                AddReference(AddFSharpNugetPackage(buildData, moduleOptions, project, package, runtimeFramework));
            foreach (var reference in project.References)
                AddReference(reference);
            if (!references.Any(IsFSharpCore))
            {
                // FSharp.Core ships with the engine (see EngineModule), fall back to the SDK one for an engine without it
                AddReference(Path.Combine(dotnetSdk.RootPath, "sdk", dotnetSdk.VersionName, "FSharp", "FSharp.Core.dll"));
            }

            // Deploy referenced assemblies next to the module assembly, the same way as for C# modules (see BuildDotNet); NuGet packages are deployed with the target
            foreach (var reference in moduleOptions.ScriptingAPI.FileReferences.Concat(project.References).Concat(references.Where(IsFSharpCore)))
                DeployFSharpReference(graph, outputPath, reference);

            // Compiler arguments. The flag set is what MSBuild's Fsc task passes for an SDK project, plus diagnostics the editor Output Log can link to their source (it needs an absolute path and the line,column,endLine,endColumn span on a single line).
            var optimize = moduleOptions.ScriptingAPI.Optimization ?? buildData.Configuration == TargetConfiguration.Release;
            var args = new List<string>
            {
                "-o:" + assemblyPath,
                "--target:library",
                "--debug:portable",
                "--doc:" + Path.ChangeExtension(assemblyPath, ".xml"),
                "--noframework",
                optimize ? "--optimize+" : "--optimize-",
                "--deterministic+",
                "--simpleresolution",
                "--targetprofile:netcore",
                "--nocopyfsharpcore",
                "--highentropyva+",
                "--warn:4",
                "--fullpaths",
                "--flaterrors",
                "--vserrors",
            };

            // One switch per symbol: fsc accepts "--define:A;B" but defines nothing then
            var defines = GetFSharpDefines(buildData, moduleOptions);
            foreach (var define in defines)
                args.Add("--define:" + define);

            var referenceAssemblies = Path.Combine(dotnetSdk.RootPath, "packs", "Microsoft.NETCore.App.Ref", dotnetSdk.RuntimeVersionName, "ref", runtimeFramework);
            if (Directory.Exists(referenceAssemblies))
            {
                foreach (var reference in Directory.GetFiles(referenceAssemblies, "*.dll"))
                    args.Add("-r:" + reference);
            }
            else
            {
                Log.Warning($"Missing .NET reference assemblies at {referenceAssemblies}");
            }
            foreach (var reference in references)
                args.Add("-r:" + reference);

            var sourceFiles = new List<string>();
            foreach (var sourceFile in project.CompileItems)
            {
                if (File.Exists(sourceFile))
                    sourceFiles.Add(sourceFile);
                else
                    Log.Warning($"F# project {project.FilePath}: source file {sourceFile} does not exist");
            }
            args.AddRange(sourceFiles);

            // Values are not quoted: in an fsc response file the quotes become part of the value (and a quoted reference is silently ignored)
            var responseFile = Path.Combine(moduleOptions.IntermediateFolder, module.Name + ".fsc.rsp");
            Utilities.WriteFileIfChanged(responseFile, string.Join(Environment.NewLine, args));

            // Create F# compilation task
            var task = graph.Add<Task>();
            task.PrerequisiteFiles.Add(projectFilePath);
            task.PrerequisiteFiles.Add(responseFile);
            task.PrerequisiteFiles.AddRange(sourceFiles);
            task.PrerequisiteFiles.AddRange(references);
            task.ProducedFiles.Add(assemblyPath);
            task.ProducedFiles.Add(Path.ChangeExtension(assemblyPath, ".xml"));
            task.WorkingDirectory = Path.GetDirectoryName(projectFilePath);
            task.InfoMessage = "Compiling " + assemblyPath;
            task.Cost = task.PrerequisiteFiles.Count;
            var fscPath = Path.Combine(dotnetSdk.RootPath, "sdk", dotnetSdk.VersionName, "FSharp", "fsc.dll");
            var compilerHostPath = Path.Combine(Globals.EngineRoot, "Source", "Tools", "FlaxFSharpCompiler", "bin", "Release", "FlaxFSharpCompiler.exe");
            if (File.Exists(compilerHostPath))
            {
                // fsc has no compiler server, so keep one warm (the host falls back to plain fsc by itself if it misbehaves)
                task.CommandPath = compilerHostPath;
                task.CommandArguments = $"\"{responseFile}\" \"{fscPath}\"";
            }
            else
            {
                task.CommandPath = Utilities.GetDotNetPath();
                task.CommandArguments = $"exec \"{fscPath}\" \"@{responseFile}\"";
            }

            // Settings for an IDE opening the project file: the ones of the editor build an IDE session sits next to
            if (IsFSharpIdeConfiguration(buildData.Target, buildData.Configuration, buildData.Platform.Target, buildData.Architecture))
                WriteFSharpIdeProps(buildData, module, runtimeFramework, defines, dependencyAssemblies);
        }

        private static bool IsFSharpCore(string path)
        {
            return string.Equals(Path.GetFileName(path), "FSharp.Core.dll", StringComparison.OrdinalIgnoreCase);
        }

        private static List<string> GetFSharpDefines(BuildData buildData, BuildOptions moduleOptions)
        {
            var defines = new List<string>(moduleOptions.ScriptingAPI.Defines);
            if (buildData.Configuration == TargetConfiguration.Debug && !defines.Contains("DEBUG"))
                defines.Add("DEBUG");
            return defines;
        }

        /// <summary>
        /// Gets the assemblies of the modules the F# module depends on, the same ones a C# module would reference (see <see cref="BuildTargetBindings"/>).
        /// </summary>
        private static IEnumerable<string> GetFSharpDependencyAssemblies(BuildData buildData, Module module, BuildOptions moduleOptions)
        {
            var outputPath = Path.GetDirectoryName(buildData.Target.GetOutputFilePath(buildData.TargetOptions));
            foreach (var dependencyName in moduleOptions.PublicDependencies.Concat(moduleOptions.PrivateDependencies))
            {
                var dependencyModule = buildData.Rules.GetModule(dependencyName);
                if (dependencyModule == null ||
                    string.IsNullOrEmpty(dependencyModule.BinaryModuleName) ||
                    dependencyModule.BinaryModuleName == module.BinaryModuleName ||
                    !buildData.Modules.ContainsKey(dependencyModule))
                    continue;

                // Module built by this target
                if (buildData.BinaryModules != null && buildData.BinaryModules.Any(x => x.Key == dependencyModule.BinaryModuleName))
                {
                    if (dependencyModule is FSharpModule)
                        yield return GetFSharpAssemblyPath(buildData, dependencyModule);
                    else if (dependencyModule.BuildCSharp)
                        yield return Path.Combine(outputPath, dependencyModule.BinaryModuleName + ".CSharp.dll");
                }

                // Module built by a referenced target (eg. the engine)
                var referencedBuild = buildData.FinReferenceBuildModule(dependencyModule.BinaryModuleName);
                if (referencedBuild != null && !string.IsNullOrEmpty(referencedBuild.ManagedPath))
                    yield return referencedBuild.ManagedPath;
            }
        }

        /// <summary>
        /// Gets the NuGet packages of the modules the F# module depends on (directly or not): types from them can appear in the code it uses, which the compiler then needs to resolve.
        /// </summary>
        private static HashSet<NugetPackage> GetFSharpDependencyNugetPackages(BuildData buildData, BuildOptions moduleOptions)
        {
            var result = new HashSet<NugetPackage>();
            var visited = new HashSet<Module>();
            var dependencies = new Stack<string>(moduleOptions.PublicDependencies.Concat(moduleOptions.PrivateDependencies));
            while (dependencies.Count != 0)
            {
                var dependencyModule = buildData.Rules.GetModule(dependencies.Pop());
                if (dependencyModule == null || !visited.Add(dependencyModule) || !buildData.Modules.TryGetValue(dependencyModule, out var dependencyOptions))
                    continue;
                result.AddRange(dependencyOptions.NugetPackageReferences);
                foreach (var dependencyName in dependencyOptions.PublicDependencies.Concat(dependencyOptions.PrivateDependencies))
                    dependencies.Push(dependencyName);
            }
            return result;
        }

        /// <summary>
        /// Adds the NuGet package from the F# project file to the module packages (so it is deployed with the target, including its dependencies, like the packages of C# modules) and returns the assembly to reference.
        /// </summary>
        private static string AddFSharpNugetPackage(BuildData buildData, BuildOptions moduleOptions, FSharpProjectFile project, FSharpProjectFile.PackageReference package, string runtimeFramework)
        {
            // The global packages folder uses lowercase ids and versions
            var nugetPath = Utilities.GetNugetPackagesPath();
            var name = package.Name.ToLowerInvariant();
            var version = package.Version.ToLowerInvariant();
            var libFolder = Path.Combine(nugetPath, name, version, "lib");
            if (!Directory.Exists(libFolder))
            {
                // Restore it the same way as for C# modules
                var restorePackage = new NugetPackage(name, version, runtimeFramework);
                var added = moduleOptions.NugetPackageReferences.Add(restorePackage);
                RestoreNugetPackages(buildData.Graph, buildData.Target, moduleOptions);
                if (added)
                    moduleOptions.NugetPackageReferences.Remove(restorePackage);
            }

            var framework = Directory.Exists(libFolder) ? FSharpProjectFile.SelectLibFramework(Directory.GetDirectories(libFolder).Select(Path.GetFileName), runtimeFramework) : null;
            if (framework == null)
            {
                Log.Error($"F# project {project.FilePath}: NuGet package {package.Name} {package.Version} has no assemblies for {runtimeFramework} (nuget: {nugetPath})");
                return null;
            }
            var nugetPackage = new NugetPackage(name, version, framework);
            moduleOptions.NugetPackageReferences.Add(nugetPackage);
            return nugetPackage.GetLibPath(nugetPath);
        }

        private static void DeployFSharpReference(TaskGraph graph, string outputPath, string srcFile)
        {
            var dstFile = Path.Combine(outputPath, Path.GetFileName(srcFile));
            if (dstFile == srcFile || graph.HasCopyTask(dstFile, srcFile))
                return;
            graph.AddCopyFile(dstFile, srcFile);

            var srcPdb = Path.ChangeExtension(srcFile, "pdb");
            if (File.Exists(srcPdb))
                graph.AddCopyFile(Path.ChangeExtension(dstFile, "pdb"), srcPdb);

            var srcXml = Path.ChangeExtension(srcFile, "xml");
            if (File.Exists(srcXml))
                graph.AddCopyFile(Path.ChangeExtension(dstFile, "xml"), srcXml);
        }

        /// <summary>
        /// Checks if the configuration is the one of the build an IDE session sits next to: the Development editor for the platform Flax.Build runs on.
        /// </summary>
        private static bool IsFSharpIdeConfiguration(Target target, TargetConfiguration configuration, TargetPlatform platform, TargetArchitecture architecture)
        {
            return target.IsEditor &&
                   configuration == TargetConfiguration.Development &&
                   platform == Platform.BuildPlatform.Target &&
                   architecture == Platform.BuildTargetArchitecture;
        }

        /// <summary>
        /// Gets the settings file for an IDE opening the F# project file: the project imports it (by default from Cache/Intermediate/FSharp/&lt;project name&gt;.Ide.props), as it runs no Flax.Build to pass them.
        /// </summary>
        internal static string GetFSharpIdePropsPath(ProjectInfo project, Module module)
        {
            return Path.Combine(project.ProjectFolderPath, Configuration.IntermediateFolder, "FSharp", Path.GetFileNameWithoutExtension(((FSharpModule)module).ProjectFilePath) + ".Ide.props");
        }

        private static void WriteFSharpIdeProps(BuildData buildData, FSharpModule module, string runtimeFramework, List<string> defines, List<string> dependencyAssemblies)
        {
            var engineAssembly = dependencyAssemblies.FirstOrDefault(x => string.Equals(Path.GetFileName(x), "FlaxEngine.CSharp.dll", StringComparison.OrdinalIgnoreCase));
            var contents = new StringBuilder();
            contents.AppendLine("<!-- Generated by Flax.Build from the editor Development build. Do not edit. -->");
            contents.AppendLine("<Project>");
            contents.AppendLine("  <PropertyGroup>");
            contents.AppendLine($"    <TargetFramework>{runtimeFramework}</TargetFramework>");
            contents.AppendLine($"    <DefineConstants>{SecurityElement.Escape(string.Join(";", defines))}</DefineConstants>");
            if (engineAssembly != null)
                contents.AppendLine($"    <FlaxEngineAssembly>{SecurityElement.Escape(engineAssembly)}</FlaxEngineAssembly>");
            contents.AppendLine("    <OtherFlags>$(OtherFlags) --fullpaths --flaterrors --vserrors</OtherFlags>");
            contents.AppendLine("  </PropertyGroup>");
            var moduleAssemblies = dependencyAssemblies.Where(x => x != engineAssembly).ToList();
            if (moduleAssemblies.Count != 0)
            {
                // The assemblies of the modules this one depends on (the project file cannot know Flax modules)
                contents.AppendLine("  <ItemGroup>");
                foreach (var assembly in moduleAssemblies)
                {
                    contents.AppendLine($"    <Reference Include=\"{SecurityElement.Escape(Path.GetFileNameWithoutExtension(assembly))}\">");
                    contents.AppendLine($"      <HintPath>{SecurityElement.Escape(assembly)}</HintPath>");
                    contents.AppendLine("      <Private>false</Private>");
                    contents.AppendLine("    </Reference>");
                }
                contents.AppendLine("  </ItemGroup>");
            }
            contents.AppendLine("</Project>");
            var path = GetFSharpIdePropsPath(buildData.Project, module);
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            Utilities.WriteFileIfChanged(path, contents.ToString());
        }

        /// <summary>
        /// Gets the F# module assembly an IDE should reference: the one of the Development editor build an IDE session sits next to (the IDE never builds the F# project).
        /// </summary>
        internal static string GetFSharpIdeAssemblyPath(Project project, Module module)
        {
            foreach (var configuration in project.Configurations)
            {
                if (configuration.Target != null && configuration.TargetBuildOptions != null &&
                    IsFSharpIdeConfiguration(configuration.Target, configuration.Configuration, configuration.Platform, configuration.Architecture))
                {
                    var outputPath = Path.GetDirectoryName(configuration.Target.GetOutputFilePath(configuration.TargetBuildOptions));
                    return Path.Combine(outputPath, module.BinaryModuleName + ".dll");
                }
            }
            return null;
        }

        /// <summary>
        /// Creates the solution project for the F# module: its own, hand-written project file, which owns the compile order and so is never generated.
        /// </summary>
        internal static Project CreateFSharpProject(FSharpModule module, ProjectGenerator generator, Project mainProject, Target[] targets)
        {
            var projectFilePath = module.ProjectFilePath;
            var name = Path.GetFileNameWithoutExtension(projectFilePath);
            var project = new VisualStudioFSharpProject
            {
                Generator = generator,
                Name = name,
                BaseName = module.BinaryModuleName,

                // Not TargetType.DotNetCore: the Visual Studio generator writes Properties/launchSettings.json next to those projects, which would put a generated file into the module source folder
                Type = null,
                Targets = targets,
                SearchPaths = Array.Empty<string>(),
                WorkspaceRootPath = mainProject.WorkspaceRootPath,
                GroupName = mainProject.GroupName,
                SourceFiles = new List<string>(),

                // Stable across regeneration, as Visual Studio keys per-project UI state by it
                ProjectGuid = new Guid(MD5.HashData(Encoding.UTF8.GetBytes(name + "|" + Utilities.NormalizePath(Utilities.MakePathRelativeTo(projectFilePath, mainProject.WorkspaceRootPath)).ToLowerInvariant()))),
            };
            project.Path = projectFilePath;

            // A single AnyCPU configuration. The solution maps every configuration to it without building it: Flax.Build builds F# and the IDE building it too would race on the output.
            var template = mainProject.Configurations[0];
            project.Configurations.Add(new Project.ConfigurationData
            {
                Name = "Debug|AnyCPU",
                Text = "Debug",
                Platform = Platform.BuildPlatform.Target,
                PlatformName = Platform.BuildPlatform.Target.ToString(),
                Architecture = TargetArchitecture.AnyCPU,
                ArchitectureName = "AnyCPU",
                Configuration = TargetConfiguration.Debug,
                ConfigurationName = "Debug",
                Target = template.Target,
                TargetBuildOptions = template.TargetBuildOptions,
                Modules = new Dictionary<Module, BuildOptions>(),
            });
            return project;
        }
    }
}
