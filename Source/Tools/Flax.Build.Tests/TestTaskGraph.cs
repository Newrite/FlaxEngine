// Copyright (c) Wojciech Figat. All rights reserved.

using System;
using System.Diagnostics;
using System.IO;
using Flax.Build.Graph;
using NUnit.Framework;

namespace Flax.Build.Tests
{
    /// <summary>
    /// Tests the task graph tasks that run a process. The editor starts Flax.Build with no console of its own, so a task
    /// command that asks a question and waits for an answer hangs the build (and the editor with it) forever.
    /// </summary>
    [TestFixture]
    public class TestTaskGraph
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

        [Test]
        public void CopyFileIntoMissingFolderCompletesWithoutInput()
        {
            var srcFile = Path.Combine(_folder, "source.txt");
            File.WriteAllText(srcFile, "content");

            // The output folder of a module is made by whatever writes into it first, so a copy can be the one to get there
            var dstFile = Path.Combine(_folder, "Output", "source.txt");

            var graph = new TaskGraph(_folder);
            var task = graph.AddCopyFile(dstFile, srcFile);

            var exitCode = RunTaskCommand(task, out var output);

            Assert.AreEqual(0, exitCode, "the copy command failed: " + output);
            Assert.IsTrue(File.Exists(dstFile), "the file was not copied: " + output);
            Assert.AreEqual("content", File.ReadAllText(dstFile));
        }

        /// <summary>
        /// Runs the command of the task the way the local executor does, with no input to answer a question with.
        /// </summary>
        private static int RunTaskCommand(Flax.Build.Graph.Task task, out string output)
        {
            var startInfo = new ProcessStartInfo
            {
                WorkingDirectory = task.WorkingDirectory,
                FileName = task.CommandPath,
                Arguments = task.CommandArguments,
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            using (var process = Process.Start(startInfo))
            {
                process.StandardInput.Close();
                var stdout = process.StandardOutput.ReadToEndAsync();
                var stderr = process.StandardError.ReadToEndAsync();
                if (!process.WaitForExit(30000))
                {
                    process.Kill();
                    Assert.Fail($"the task command did not end in 30s, it waits for input: {task.CommandPath} {task.CommandArguments}");
                }
                output = stdout.Result + stderr.Result;
                return process.ExitCode;
            }
        }
    }
}
