// Copyright (c) Wojciech Figat. All rights reserved.

using System;

namespace Flax.Build.Projects.VisualStudio
{
    /// <summary>
    /// The F# project file of an F# module (see <see cref="FSharpModule"/>) in a Visual Studio solution. It is hand-written (it owns the F# compile order), so it is never generated, and Flax.Build builds the module, so the IDE never builds it.
    /// </summary>
    /// <seealso cref="Flax.Build.Projects.VisualStudio.VisualStudioProject" />
    public sealed class VisualStudioFSharpProject : VisualStudioProject
    {
        /// <summary>
        /// Visual Studio project type of SDK-style F# projects.
        /// </summary>
        public override Guid ProjectTypeGuid => new Guid("6EC3EE1D-3C4E-46DD-8F32-0CC8E7565705");

        /// <inheritdoc />
        public override void Generate(string solutionPath, bool isMainProject)
        {
        }
    }
}
