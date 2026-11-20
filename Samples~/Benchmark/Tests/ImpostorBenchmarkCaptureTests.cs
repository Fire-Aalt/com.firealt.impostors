using System;
using System.IO;
using NUnit.Framework;
using UnityEngine;

namespace Impostors.Samples.Benchmark.Tests
{
    public sealed class ImpostorBenchmarkCaptureTests
    {
        [Test]
        public void CaptureRequiresCurrentPlaySessionAndCannotWriteFromEditMode()
        {
            var host = new GameObject("Capture session test");
            string directory = Path.GetFullPath(Path.Combine("Temp", "CaptureGuard-" + Guid.NewGuid().ToString("N")));
            try
            {
                var benchmark = host.AddComponent<ImpostorBenchmark>();
                Assert.That(benchmark.IsCapturing, Is.False);
                Assert.Throws<InvalidOperationException>(() => benchmark.BeginCapture(directory));
                Assert.That(Directory.Exists(directory), Is.False);
                benchmark.StopCapture();
                benchmark.StopCapture();
                host.SetActive(false);
                host.SetActive(true);
                Assert.That(benchmark.IsCapturing, Is.False);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(host);
            }
        }
    }
}
