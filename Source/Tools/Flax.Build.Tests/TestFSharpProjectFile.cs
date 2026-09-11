// Copyright (c) Wojciech Figat. All rights reserved.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Flax.Build.NativeCpp;
using NUnit.Framework;

namespace Flax.Build.Tests
{
    /// <summary>
    /// Tests reading the F# project file an F# module is compiled from. The project is evaluated by the real MSBuild of the installed .NET SDK, restoring the OneOf package (from the NuGet cache, or from nuget.org).
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

        private string InFolder(string path)
        {
            return Path.GetFullPath(Path.Combine(_folder, path));
        }

        private void WriteFile(string path, string contents)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(InFolder(path)));
            File.WriteAllText(InFolder(path), contents);
        }

        private string WriteProject(string items)
        {
            WriteFile("Test.fsproj", "<Project Sdk=\"Microsoft.NET.Sdk\">\n" +
                                     "  <PropertyGroup><EnableDefaultCompileItems>false</EnableDefaultCompileItems><DisableImplicitFSharpCoreReference>true</DisableImplicitFSharpCoreReference></PropertyGroup>\n" +
                                     items + "\n</Project>");
            return InFolder("Test.fsproj");
        }

        private FSharpProjectFile Load(string configuration = "Release")
        {
            return FSharpProjectFile.Load(InFolder("Test.fsproj"), InFolder("Intermediate"), new Dictionary<string, string>
            {
                { "Configuration", configuration },
                { "TargetFramework", "net8.0" },
            });
        }

        /// <summary>
        /// Test that the source files keep the project file order (F# code can only use what precedes it).
        /// </summary>
        [Test]
        public void TestCompileItemsKeepProjectOrder()
        {
            WriteProject(@"<ItemGroup><Compile Include=""Zeta.fs"" /><Compile Include=""Alpha.fsi"" /><Compile Include=""Alpha.fs"" /></ItemGroup>
<ItemGroup><Compile Include=""Sub\Middle.fs"" /></ItemGroup>");

            CollectionAssert.AreEqual(new[] { InFolder("Zeta.fs"), InFolder("Alpha.fsi"), InFolder("Alpha.fs"), InFolder(@"Sub\Middle.fs") }, Load().CompileItems);
        }

        /// <summary>
        /// Test that conditions are evaluated for the build configuration.
        /// </summary>
        [Test]
        public void TestConditionsAreEvaluated()
        {
            WriteProject(@"<ItemGroup><Compile Include=""Common.fs"" /><Compile Include=""DebugOnly.fs"" Condition=""'$(Configuration)' == 'Debug'"" /></ItemGroup>");

            CollectionAssert.AreEqual(new[] { InFolder("Common.fs") }, Load("Release").CompileItems);
            CollectionAssert.AreEqual(new[] { InFolder("Common.fs"), InFolder("DebugOnly.fs") }, Load("Debug").CompileItems);
        }

        /// <summary>
        /// Test that wildcards and imported project files are evaluated, in project order.
        /// </summary>
        [Test]
        public void TestWildcardsAndImportsAreEvaluated()
        {
            WriteFile(@"Generated\B.fs", "module B");
            WriteFile(@"Generated\A.fs", "module A");
            WriteFile("Generated.props", @"<Project><ItemGroup><Compile Include=""Generated\*.fs"" /></ItemGroup></Project>");
            WriteProject(@"<Import Project=""Generated.props"" /><ItemGroup><Compile Include=""Main.fs"" /></ItemGroup>");

            CollectionAssert.AreEqual(new[] { InFolder(@"Generated\A.fs"), InFolder(@"Generated\B.fs"), InFolder("Main.fs") }, Load().CompileItems);
        }

        /// <summary>
        /// Test that NuGet resolves the packages, here with the version from central package management rather than the project file.
        /// </summary>
        [Test]
        public void TestPackagesAreResolvedByNuGet()
        {
            WriteFile("Directory.Packages.props", @"<Project>
  <PropertyGroup><ManagePackageVersionsCentrally>true</ManagePackageVersionsCentrally></PropertyGroup>
  <ItemGroup><PackageVersion Include=""OneOf"" Version=""3.0.271"" /></ItemGroup>
</Project>");
            WriteProject(@"<ItemGroup><Compile Include=""Main.fs"" /><PackageReference Include=""OneOf"" /></ItemGroup>");

            var project = Load();

            var oneOf = project.References.SingleOrDefault(x => Path.GetFileName(x) == "OneOf.dll");
            Assert.IsNotNull(oneOf, "the package assembly must be referenced: " + string.Join(", ", project.References));
            StringAssert.Contains("3.0.271", oneOf);
            StringAssert.Contains("netstandard2.0", oneOf, "NuGet picks the assemblies usable on the target framework");
            CollectionAssert.Contains(project.CopyLocalFiles, oneOf, "the package runtime assembly must be deployed");
            Assert.IsFalse(project.References.Any(x => x.Contains("Microsoft.NETCore.App.Ref")), "framework assemblies are referenced by the build itself");
        }

        /// <summary>
        /// Test reading assemblies referenced by path: deployed unless not copied locally (Private=false).
        /// </summary>
        [Test]
        public void TestReferencesByPath()
        {
            // Two different assemblies: references are resolved by assembly identity, not by file name
            var library = InFolder(@"Libs\Flax.Build.Tests.dll");
            var provided = InFolder(@"Libs\nunit.framework.dll");
            Directory.CreateDirectory(InFolder("Libs"));
            File.Copy(typeof(TestFSharpProjectFile).Assembly.Location, library);
            File.Copy(typeof(Assert).Assembly.Location, provided);
            WriteProject(@"<ItemGroup>
  <Compile Include=""Main.fs"" />
  <Reference Include=""Flax.Build.Tests""><HintPath>Libs\Flax.Build.Tests.dll</HintPath></Reference>
  <Reference Include=""nunit.framework""><HintPath>Libs\nunit.framework.dll</HintPath><Private>false</Private></Reference>
</ItemGroup>");

            var project = Load();

            CollectionAssert.IsSupersetOf(project.References, new[] { library, provided });
            CollectionAssert.Contains(project.CopyLocalFiles, library);
            CollectionAssert.DoesNotContain(project.CopyLocalFiles, provided);
        }

        /// <summary>
        /// Test that the evaluation is cached, and evaluated again when the project file changes or a source file is added (which can change what a wildcard includes) - but not when a source file is edited.
        /// </summary>
        [Test]
        public void TestEvaluationIsCached()
        {
            WriteFile("Main.fs", "module Main");
            var path = WriteProject(@"<ItemGroup><Compile Include=""*.fs"" /></ItemGroup>");

            Assert.IsFalse(Load().FromCache, "the first load evaluates the project");
            Assert.IsTrue(Load().FromCache, "an unchanged project must not be evaluated again");

            File.AppendAllText(InFolder("Main.fs"), "\nlet x = 1");
            Assert.IsTrue(Load().FromCache, "editing a source file must not evaluate the project again");

            File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddSeconds(5));
            Assert.IsFalse(Load().FromCache, "a changed project file must be evaluated again");

            WriteFile("Other.fs", "module Other");
            var project = Load();
            Assert.IsFalse(project.FromCache, "an added source file must evaluate the project again");
            CollectionAssert.AreEqual(new[] { InFolder("Main.fs"), InFolder("Other.fs") }, project.CompileItems);

            Assert.IsFalse(Load("Debug").FromCache, "other properties must evaluate the project again");
        }

        /// <summary>
        /// Test matching the framework names of package specifications, which can use the full names.
        /// </summary>
        [Test]
        public void TestNugetFrameworkNames()
        {
            Assert.IsTrue(NugetPackage.IsSameFramework(".NETStandard2.0", "netstandard2.0"));
            Assert.IsTrue(NugetPackage.IsSameFramework(".NETCoreApp3.1", "netcoreapp3.1"));
            Assert.IsTrue(NugetPackage.IsSameFramework(".NETCoreApp5.0", "net5.0"));
            Assert.IsTrue(NugetPackage.IsSameFramework("net8.0", "net8.0"));
            Assert.IsFalse(NugetPackage.IsSameFramework(".NETStandard2.0", "netstandard2.1"));
            Assert.IsFalse(NugetPackage.IsSameFramework(null, "net8.0"));
        }
    }
}
