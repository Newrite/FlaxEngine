// Copyright (c) Wojciech Figat. All rights reserved.

using System.IO;
using Flax.Build.NativeCpp;

namespace Flax.Build
{
    /// <summary>
    /// The build module that is a part of the engine.
    /// </summary>
    /// <seealso cref="Flax.Build.Target" />
    public class EngineModule : Module
    {
        /// <inheritdoc />
        public override void Setup(BuildOptions options)
        {
            base.Setup(options);

            if (Name != "Core")
            {
                // All Engine modules include Core module
                options.PrivateDependencies.Add("Core");
            }

            if (options.Target.IsEditor)
            {
                // Use Debug module by default in Editor
                if (Name != "Debug")
                    options.PrivateDependencies.Add("Debug");

                options.ScriptingAPI.Defines.Add("FLAX_EDITOR");
            }
            else
            {
                options.ScriptingAPI.Defines.Add("FLAX_GAME");
            }

            // Use custom precompiled header file for the engine to boost compilation time
            options.CompileEnv.PrecompiledHeaderUsage = PrecompiledHeaderFileUsage.CreateManual;
            options.CompileEnv.PrecompiledHeaderSource = Utilities.NormalizePath(Path.Combine(Globals.EngineRoot, "Source/FlaxEngine.pch.h"));

            BinaryModuleName = "FlaxEngine";
            options.ScriptingAPI.Defines.Add("FLAX");
            options.ScriptingAPI.Defines.Add("FLAX_ASSERTIONS");
            var newtonsoftJsonPath = options.Platform?.HasDynamicCodeExecutionSupport ?? true ? "Newtonsoft.Json.dll" : "AOT/Newtonsoft.Json.dll";
            options.ScriptingAPI.FileReferences.Add(Utilities.RemovePathRelativeParts(Path.Combine(Globals.EngineRoot, "Source", "Platforms", "DotNet", newtonsoftJsonPath)));
            options.ScriptingAPI.SystemReferences.Add("System.ComponentModel.TypeConverter");

            // FSharp.Core is deployed exactly like Newtonsoft.Json: as an engine-level managed
            // dependency, so it lands in the binaries folder and is therefore on the host's TPA
            // list. That placement is load-bearing, not cosmetic.
            //
            // Resolved any other way, FSharp.Core is pulled in through the scripting ALC's
            // Resolving handler and lands INSIDE the collectible context. Measured consequence:
            // the scripting ALC then never unloads - 15 of 15 hot reloads logged "Scripting
            // AssemblyLoadContext was not unloaded", and the process accumulated the context's 6
            // assemblies on every single reload (43 -> 133 over 15 cycles). With the assembly on
            // the TPA the Default context wins resolution, the collectible context drops to 5
            // assemblies, and the count is flat at 46 across the same 15 reloads with zero
            // warnings.
            //
            // Optional by design: a fork with no F# in it simply does not ship the file, and the
            // reference is skipped rather than breaking the build.
            var fsharpCorePath = Utilities.RemovePathRelativeParts(Path.Combine(Globals.EngineRoot, "Source", "Platforms", "DotNet", "FSharp.Core.dll"));
            if (File.Exists(fsharpCorePath))
                options.ScriptingAPI.FileReferences.Add(fsharpCorePath);
        }
    }
}
