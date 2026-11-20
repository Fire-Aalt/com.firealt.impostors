using System.Collections;
using System.Threading.Tasks;
using Impostors.Editor;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace Impostors.Tests
{
    public sealed class ImpostorRendererTests
    {
        private const string AN_EXISTING_FRAME_DEBUGGER_CAPTURE_IS_PRESERVED_DISABLE = "An existing Frame Debugger capture is preserved. Disable it before running this validation.";
        private const string NATIVE_DRAWS_FOR_INSTANCES = "{0} native draws for {1} instances.";

        [UnityTest]
        public IEnumerator NativeBatchingAndLifetimeRemainValid()
        {
            yield return Validate();
        }

        private static IEnumerator Validate()
        {
            if (FrameDebugger.enabled)
            {
                Assert.Ignore(AN_EXISTING_FRAME_DEBUGGER_CAPTURE_IS_PRESERVED_DISABLE);
            }

            // Frame Debugger only reports instance counts for direct draws. Indirect counts live in a GPU buffer.
            bool indirectDraws = ImpostorWorldRenderer.IndirectDraws;
            ImpostorWorldRenderer.IndirectDraws = false;
            Task<ImpostorBrgValidation.Report> operation;
            try
            {
                operation = ImpostorBrgValidation.RunAsync();
                while (!operation.IsCompleted)
                {
                    yield return null;
                }
            }
            finally
            {
                ImpostorWorldRenderer.IndirectDraws = indirectDraws;
            }

            Assert.That(operation.IsFaulted, Is.False, operation.Exception?.GetBaseException().ToString());
            ImpostorBrgValidation.Report report = operation.Result;
            Assert.That(report.SubmittedInstances, Is.EqualTo(report.Placements));
            Assert.That(report.DatasetReplacementPassed, Is.True);
            Assert.That(report.SourceMeshReleasePassed, Is.True);
            Assert.That(report.UnusedAssetUnloadPassed, Is.True);
            Assert.That(report.RepeatedHostEnablePassed, Is.True);
            if (!report.NativeMeasurementAvailable && Application.isBatchMode)
                Assert.Ignore(report.MeasurementError);
            Assert.That(report.NativeMeasurementAvailable, Is.True, report.MeasurementError);
            Assert.That(report.NativeForwardInstances, Is.EqualTo(report.Placements));
            Assert.That(report.NativeCounterIsolationPassed, Is.True);
            Assert.That(report.Batched, Is.True, string.Format(NATIVE_DRAWS_FOR_INSTANCES, report.NativeForwardDrawCalls, report.Placements));
        }
    }
}
