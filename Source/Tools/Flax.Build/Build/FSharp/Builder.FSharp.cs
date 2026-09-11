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
        /// The project file is evaluated by MSBuild (see <see cref="FSharpProjectFile"/>), but compiling is not left to MSBuild: fsc from the .NET SDK the engine compiles its C# with is run directly, through the warm compiler host when it is built (Source/Tools/FlaxFSharpCompiler), which saves MSBuild's second or so on every edit. The task is ordered after the modules it depends on and before the modules depending on it by the task graph itself: it takes their assemblies as prerequisites, and they take its assembly (see <see cref="BuildTargetBindings"/>).
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
            var graph = buildData.Graph;
            var assemblyPath = GetFSharpAssemblyPath(buildData, module);
            var outputPath = Path.GetDirectoryName(assemblyPath);
            var dotnetSdk = DotNetSdk.Instance;
            if (!dotnetSdk.IsValid)
                throw new DotNetSdk.MissingException();
            var runtimeVersionParts = dotnetSdk.RuntimeVersionName.Split('.');
            var runtimeFramework = $"net{runtimeVersionParts[0]}.{runtimeVersionParts[1]}";

            // References the build provides: what the dependencies reference (eg. FSharp.Core and Newtonsoft.Json from the engine, packages of F# dependency modules) and the assemblies of the dependency modules
            var references = new List<string>();
            void AddReference(string path)
            {
                if (!string.IsNullOrEmpty(path) && !references.Contains(path, StringComparer.OrdinalIgnoreCase))
                    references.Add(path);
            }
            var inheritedReferences = moduleOptions.ScriptingAPI.FileReferences.ToList();
            foreach (var reference in inheritedReferences)
                AddReference(reference);
            var dependencyAssemblies = GetFSharpDependencyAssemblies(buildData, module, moduleOptions).ToList();
            foreach (var reference in dependencyAssemblies)
                AddReference(reference);
            if (!references.Any(IsFSharpCore))
            {
                // FSharp.Core ships with the engine (see EngineModule), fall back to the SDK one for an engine without it
                AddReference(Path.Combine(dotnetSdk.RootPath, "sdk", dotnetSdk.VersionName, "FSharp", "FSharp.Core.dll"));
            }
            var defines = GetFSharpDefines(buildData, moduleOptions);
            var engineAssembly = dependencyAssemblies.FirstOrDefault(IsEngineAssembly);

            // The project file items for this configuration: its source files in compile order, and the packages and assemblies it references
            var buildPropsPath = Path.Combine(moduleOptions.IntermediateFolder, module.Name + ".Build.props");
            WriteFSharpProps(buildPropsPath, "for this build configuration", runtimeFramework, defines, engineAssembly, null);
            var project = FSharpProjectFile.Load(projectFilePath, Path.Combine(moduleOptions.IntermediateFolder, "MSBuild"), new Dictionary<string, string>
            {
                { "Configuration", buildData.Configuration.ToString() },
                { "TargetFramework", runtimeFramework },
                { "FlaxGeneratedProps", buildPropsPath },

                // FSharp.Core comes with the engine
                { "DisableImplicitFSharpCoreReference", "true" },
            }, Utilities.GetDotNetPath());

            // The project references, except the ones the build provides: the engine, the dependency modules and FSharp.Core have to be the ones of this build
            var providedNames = new HashSet<string>(references.Select(Path.GetFileNameWithoutExtension), StringComparer.OrdinalIgnoreCase) { "FSharp.Core" };
            var projectReferences = project.References.Where(x => !providedNames.Contains(Path.GetFileNameWithoutExtension(x))).ToList();
            foreach (var reference in projectReferences)
                AddReference(reference);

            // The package assemblies (the ones deployable, which reference assemblies are not) are shared with the modules depending on this one, as types from them can appear in its code, the same way packages of C# modules are
            foreach (var reference in projectReferences)
            {
                if (project.CopyLocalFiles.Contains(reference, StringComparer.OrdinalIgnoreCase))
                    moduleOptions.ScriptingAPI.FileReferences.Add(reference);
            }

            // Deploy the referenced assemblies next to the module assembly, the same way as for C# modules (see BuildDotNet), and the project runtime files (packages with their dependencies)
            foreach (var reference in inheritedReferences.Concat(references.Where(IsFSharpCore)))
                DeployFSharpReference(graph, outputPath, reference);
            foreach (var file in project.CopyLocalFiles)
            {
                var dstFile = Path.Combine(outputPath, Path.GetFileName(file));
                if (!providedNames.Contains(Path.GetFileNameWithoutExtension(file)) && dstFile != file && !graph.HasCopyTask(dstFile, file))
                    graph.AddCopyFile(dstFile, file);
            }

            // Compiler arguments. The flag set is what MSBuild's Fsc task passes for an SDK project, plus diagnostics the editor Output Log can link to their source (it needs an absolute path and the line,column,endLine,endColumn span on a single line).
            var optimize = moduleOptions.ScriptingAPI.Optimization ?? buildData.Configuration == TargetConfiguration.Release;
            var args = new List<string>
            {
                "-o:" + assemblyPath,
                "--nologo", // As /nologo for C#: otherwise the compiler banner lands in the build log on every compile fsc runs
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

            // Settings for an IDE opening the project file: the ones of the editor build an IDE session sits next to. The IDE resolves the project's own references itself.
            if (IsFSharpIdeConfiguration(buildData.Target, buildData.Configuration, buildData.Platform.Target, buildData.Architecture))
            {
                var ideReferences = references.Where(x => !IsFSharpCore(x) && x != engineAssembly && !projectReferences.Contains(x)).ToList();
                WriteFSharpProps(GetFSharpIdePropsPath(buildData.Project, module), "from the editor Development build", runtimeFramework, defines, engineAssembly, ideReferences);
            }
        }

        private static bool IsFSharpCore(string path)
        {
            return string.Equals(Path.GetFileName(path), "FSharp.Core.dll", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsEngineAssembly(string path)
        {
            return string.Equals(Path.GetFileName(path), "FlaxEngine.CSharp.dll", StringComparison.OrdinalIgnoreCase);
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
        /// Gets the settings file for an IDE opening the F# project file: the project imports it (by default from Cache/Intermediate/FSharp/&lt;project name&gt;.Ide.props), as no Flax.Build runs to pass them.
        /// </summary>
        internal static string GetFSharpIdePropsPath(ProjectInfo project, Module module)
        {
            return Path.Combine(project.ProjectFolderPath, Configuration.IntermediateFolder, "FSharp", Path.GetFileNameWithoutExtension(((FSharpModule)module).ProjectFilePath) + ".Ide.props");
        }

        /// <summary>
        /// Writes the settings the project file imports (as FlaxGeneratedProps): the ones of the build for evaluating it, the ones of the editor build for an IDE.
        /// </summary>
        private static void WriteFSharpProps(string path, string origin, string runtimeFramework, List<string> defines, string engineAssembly, List<string> references)
        {
            var contents = new StringBuilder();
            contents.AppendLine($"<!-- Generated by Flax.Build {origin}. Do not edit. -->");
            contents.AppendLine("<Project>");
            contents.AppendLine("  <PropertyGroup>");
            contents.AppendLine($"    <TargetFramework>{runtimeFramework}</TargetFramework>");
            contents.AppendLine($"    <DefineConstants>{SecurityElement.Escape(string.Join(";", defines))}</DefineConstants>");
            if (engineAssembly != null)
                contents.AppendLine($"    <FlaxEngineAssembly>{SecurityElement.Escape(engineAssembly)}</FlaxEngineAssembly>");
            contents.AppendLine("    <OtherFlags>$(OtherFlags) --fullpaths --flaterrors --vserrors</OtherFlags>");
            contents.AppendLine("  </PropertyGroup>");
            if (references != null && references.Count != 0)
            {
                // The assemblies the build references besides the project's own ones (eg. the modules this one depends on, which the project file cannot know)
                contents.AppendLine("  <ItemGroup>");
                foreach (var reference in references)
                {
                    contents.AppendLine($"    <Reference Include=\"{SecurityElement.Escape(Path.GetFileNameWithoutExtension(reference))}\">");
                    contents.AppendLine($"      <HintPath>{SecurityElement.Escape(reference)}</HintPath>");
                    contents.AppendLine("      <Private>false</Private>");
                    contents.AppendLine("    </Reference>");
                }
                contents.AppendLine("  </ItemGroup>");
            }
            contents.AppendLine("</Project>");
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
