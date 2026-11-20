using System.Collections.Generic;
using EntityPainter.Editor;
using NUnit.Framework;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Mathematics;
using UnityEngine;

namespace EntityPainter.Tests
{
    public sealed class PaintedRecordTests
    {
        private const float MAX_ROTATION_ERROR_DEGREES = 0.2f;
        private const float MAX_SCALE_ERROR = 0.001f;
        private const int SAMPLES = 2000;
        private const int SEED = 1234;

        [Test]
        public void RecordIsEighteenBytes()
        {
            Assert.AreEqual(PaintedRecord.SIZE, UnsafeUtility.SizeOf<PaintedRecord>());
        }

        [Test]
        public void RoundTripKeepsPositionRotationAndScale()
        {
            var random = new Unity.Mathematics.Random(SEED);
            for (int i = 0; i < SAMPLES; i++)
            {
                float3 position = random.NextFloat3(-5000f, 5000f);
                quaternion rotation = random.NextQuaternionRotation();
                float scale = random.NextFloat(0.05f, 20f);

                PaintedRecord record = PaintedRecord.Encode(position, rotation, scale);

                Assert.AreEqual(position, record.Position);
                float error = math.degrees(AngleBetween(rotation, record.DecodeRotation()));
                Assert.Less(error, MAX_ROTATION_ERROR_DEGREES);
                Assert.AreEqual(scale, record.DecodeScale(), scale * MAX_SCALE_ERROR);
            }
        }

        [Test]
        public void AppendAndRemoveKeepPrototypeRanges()
        {
            var target = new GameObject().AddComponent<PaintedEntities>();
            try
            {
                var first = new GameObject();
                var second = new GameObject();
                Assert.AreEqual(0, PaintedRecordsEditing.EnsurePrototype(target, first));
                Assert.AreEqual(1, PaintedRecordsEditing.EnsurePrototype(target, second));

                PaintedRecordsEditing.Append(target, new Dictionary<int, List<PaintedRecord>>
                {
                    [1] = new() { At(10f), At(11f) },
                    [0] = new() { At(0f) },
                });
                PaintedRecordsEditing.Append(target, new Dictionary<int, List<PaintedRecord>>
                {
                    [0] = new() { At(1f) },
                });

                CollectionAssert.AreEqual(new[] { 2, 2 }, target.Counts);
                CollectionAssert.AreEqual(new[] { 0f, 1f, 10f, 11f }, Xs(target));
                Assert.IsTrue(target.IsConsistent);

                PaintedRecordsEditing.Remove(target, new[] { false, true, true, false }, 2);

                CollectionAssert.AreEqual(new[] { 1, 1 }, target.Counts);
                CollectionAssert.AreEqual(new[] { 0f, 11f }, Xs(target));
                Assert.IsTrue(target.IsConsistent);

                Object.DestroyImmediate(first);
                Object.DestroyImmediate(second);
            }
            finally
            {
                Object.DestroyImmediate(target.gameObject);
            }
        }

        private static PaintedRecord At(float x)
        {
            return PaintedRecord.Encode(new float3(x, 0f, 0f), quaternion.identity, 1f);
        }

        private static float[] Xs(PaintedEntities target)
        {
            System.ReadOnlySpan<PaintedRecord> records = PaintedRecordsEditing.AsRecords(target.Records);
            var xs = new float[records.Length];
            for (int i = 0; i < records.Length; i++)
            {
                xs[i] = records[i].Position.x;
            }

            return xs;
        }

        private static float AngleBetween(quaternion a, quaternion b)
        {
            float dot = math.min(1f, math.abs(math.dot(a.value, b.value)));
            return 2f * math.acos(dot);
        }
    }
}
