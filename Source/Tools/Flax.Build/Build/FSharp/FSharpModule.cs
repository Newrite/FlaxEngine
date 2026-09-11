// Copyright (c) Wojciech Figat. All rights reserved.

using System.IO;

namespace Flax.Build
{
    /// <summary>
    /// The base class for game modules written in F#. The module's F# project file (an SDK-style .fsproj) lists the source files in compile order and the NuGet packages and assemblies they use; Flax.Build compiles it into a scripting binary module of its own, so F# scripts are listed, attached, serialized and hot-reloaded like C# ones.
    /// </summary>
    /// <remarks>
    /// Dependencies on other modules are declared in <see cref="Module.Setup"/> as for any module, in either direction: a C# module can depend on an F# module and an F# module on a C# or another F# module (but not both ways, as for any two assemblies).
    /// </remarks>
    /// <seealso cref="Flax.Build.GameModule" />
    public abstract class FSharpModule : GameModule
    {
        private string _projectFilePath;

        /// <summary>
        /// The F# project file path. Defaults to the file named after the module in the module folder (&lt;module folder&gt;/&lt;module name&gt;.fsproj).
        /// </summary>
        public string ProjectFilePath
        {
            get => _projectFilePath ?? Path.Combine(FolderPath, Name + ".fsproj");
            set => _projectFilePath = value;
        }

        /// <inheritdoc />
        public override void Init()
        {
            base.Init();

            // Compiled from the project file by Flax.Build (see Builder.BuildFSharpModule), neither by the C++ toolchain nor by the C# compiler
            BuildNativeCode = false;
            BuildCSharp = false;
        }
    }
}
