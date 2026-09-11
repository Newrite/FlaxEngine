// Copyright (c) Wojciech Figat. All rights reserved.

using System;
using System.IO;
using NUnit.Framework;

namespace Flax.Build.Tests
{
    /// <summary>
    /// Tests reading the F# project file an F# module is compiled from.
    /// </summary>
    [TestFixture]
    public class TestFSharpProjectFile
    {
        private string _folder;

        [SetUp]
        public void SetUp()
        {
            _folder = Path.Combine(Path.GetTempPath(), "FlaxBuildTests_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_folder);
        }

        [TearDown]
        public void TearDown()
        {
            Directory.Delete(_folder, true);
        }

        private FSharpProjectFile Load(string items)
        {
            var path = Path.Combine(_folder, "Test.fsproj");
            File.WriteAllText(path, "<Project Sdk=\"Microsoft.NET.Sdk\">\n" + items + "\n</Project>");
            return FSharpProjectFile.Load(path);
        }

        private string InFolder(string path)
        {
            return Path.GetFullPath(Path.Combine(_folder, path));
        }

        /// <summary>
        /// Test that the source files keep the project file order (F# code can only use what precedes it).
        /// </summary>
        [Test]
        public void TestCompileItemsKeepProjectOrder()
        {
            var project = Load(@"<ItemGroup><Compile Include=""Zeta.fs"" /><Compile Include=""Alpha.fsi"" /><Compile Include=""Alpha.fs"" /></ItemGroup>
<ItemGroup><Compile Include=""Sub\Middle.fs"" /></ItemGroup>");

            CollectionAssert.AreEqual(new[] { InFolder("Zeta.fs"), InFolder("Alpha.fsi"), InFolder("Alpha.fs"), InFolder(@"Sub\Middle.fs") }, project.CompileItems);
        }

        /// <summary>
        /// Test that items MSBuild would have to evaluate are skipped rather than passed on as paths.
        /// </summary>
        [Test]
        public void TestUnevaluatedItemsAreSkipped()
        {
            var project = Load(@"<ItemGroup><Compile Include=""$(Generated)\A.fs"" /><Compile Include=""*.fs"" /><Compile Include=""B.fs"" /></ItemGroup>");

            CollectionAssert.AreEqual(new[] { InFolder("B.fs") }, project.CompileItems);
        }

        /// <summary>
        /// Test reading NuGet packages with the version as an attribute or an element.
        /// </summary>
        [Test]
        public void TestPackageReferences()
        {
            var project = Load(@"<ItemGroup>
  <PackageReference Include=""OneOf"" Version=""3.0.271"" />
  <PackageReference Include=""Other""><Version>1.2.3</Version></PackageReference>
  <PackageReference Include=""Floating"" Version=""1.*"" />
  <PackageReference Include=""Central"" />
</ItemGroup>");

            Assert.AreEqual(2, project.PackageReferences.Count);
            Assert.AreEqual("OneOf", project.PackageReferences[0].Name);
            Assert.AreEqual("3.0.271", project.PackageReferences[0].Version);
            Assert.AreEqual("Other", project.PackageReferences[1].Name);
            Assert.AreEqual("1.2.3", project.PackageReferences[1].Version);
        }

        /// <summary>
        /// Test reading assemblies referenced by path, skipping framework assemblies and ones found through MSBuild properties (eg. the engine assembly, which the build references itself).
        /// </summary>
        [Test]
        public void TestReferencesByPath()
        {
            var project = Load(@"<ItemGroup>
  <Reference Include=""Library""><HintPath>..\Libs\Library.dll</HintPath></Reference>
  <Reference Include=""FlaxEngine.CSharp""><HintPath>$(FlaxEngineAssembly)</HintPath><Private>false</Private></Reference>
  <Reference Include=""System.Numerics"" />
</ItemGroup>");

            CollectionAssert.AreEqual(new[] { InFolder(@"..\Libs\Library.dll") }, project.References);
        }

        /// <summary>
        /// Test picking the NuGet package assemblies usable on the runtime.
        /// </summary>
        [Test]
        public void TestSelectLibFramework()
        {
            Assert.AreEqual("netstandard2.0", FSharpProjectFile.SelectLibFramework(new[] { "net35", "net45", "netstandard1.3", "netstandard2.0" }, "net10.0"));
            Assert.AreEqual("net8.0", FSharpProjectFile.SelectLibFramework(new[] { "netstandard2.1", "net6.0", "net8.0", "net11.0" }, "net10.0"), "the newest .NET not newer than the runtime");
            Assert.AreEqual("netcoreapp3.1", FSharpProjectFile.SelectLibFramework(new[] { "netstandard2.1", "netcoreapp3.1" }, "net8.0"));
            Assert.AreEqual("netstandard2.0", FSharpProjectFile.SelectLibFramework(new[] { "net8.0-windows7.0", "netstandard2.0" }, "net8.0"), "platform specific assemblies need that platform");
            Assert.IsNull(FSharpProjectFile.SelectLibFramework(new[] { "net45" }, "net8.0"), ".NET Framework assemblies cannot be used");
        }
    }
}
