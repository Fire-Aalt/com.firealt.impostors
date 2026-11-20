using System;
using System.Runtime.InteropServices;
using Unity.Collections;
using Unity.Entities;
using UnityEditor;
using UnityEngine;
using Hash128 = UnityEngine.Hash128;

namespace EntityPainter.Authoring
{
    /// <summary>
    /// Bakes a <see cref="PaintedEntities"/> component into one spawner entity: the prototypes' baked entity prefabs
    /// and a blob holding the packed records and each prototype's record count. <see cref="PaintedEntitySpawnSystem"/>
    /// does the instancing.
    /// </summary>
    internal sealed class PaintedEntitiesBaker : Baker<PaintedEntities>
    {
        private const string INCONSISTENT_DATA =
            "Painted Entities '{0}' has mismatched prototype, count and record data. It is baked without instances.";
        private const string MISSING_PROTOTYPE = "missing";

        public override void Bake(PaintedEntities authoring)
        {
            Entity spawner = GetEntity(TransformUsageFlags.None);
            if (!authoring.IsConsistent)
            {
                Debug.LogError(string.Format(INCONSISTENT_DATA, authoring.name), authoring);
                return;
            }

            GameObject[] prototypes = authoring.Prototypes;
            int[] counts = authoring.Counts;
            byte[] records = authoring.Records;

            // Any change to the records, the ranges or a prototype asset gives the spawner a new hash and respawns it.
            var hash = new Hash128();
            hash.Append(records);
            hash.Append(counts);

            var prefabs = new PaintedPrefab[prototypes.Length];
            for (int i = 0; i < prototypes.Length; i++)
            {
                GameObject prototype = prototypes[i];
                DependsOn(prototype);

                Entity prefab = Entity.Null;
                if (prototype)
                {
                    prefab = GetEntity(prototype, TransformUsageFlags.Dynamic);
                    hash.Append(AssetDatabase.GetAssetDependencyHash(AssetDatabase.GetAssetPath(prototype)).ToString());
                }
                else
                {
                    hash.Append(MISSING_PROTOTYPE);
                }

                prefabs[i] = new PaintedPrefab { Prefab = prefab };
            }

            BlobAssetReference<PaintedInstanceBlob> blob = BuildBlob(records, counts);
            AddBlobAsset(ref blob, out _);
            AddComponent(spawner, new PaintedInstances { Value = blob, Hash = hash });

            DynamicBuffer<PaintedPrefab> buffer = AddBuffer<PaintedPrefab>(spawner);
            foreach (PaintedPrefab prefab in prefabs)
            {
                buffer.Add(prefab);
            }
        }

        private static BlobAssetReference<PaintedInstanceBlob> BuildBlob(byte[] bytes, int[] counts)
        {
            ReadOnlySpan<PaintedRecord> records = MemoryMarshal.Cast<byte, PaintedRecord>(bytes);
            using var builder = new BlobBuilder(Allocator.Temp);
            ref PaintedInstanceBlob root = ref builder.ConstructRoot<PaintedInstanceBlob>();
            BlobBuilderArray<PaintedRecord> array = builder.Allocate(ref root.Records, records.Length);
            for (int i = 0; i < records.Length; i++)
            {
                array[i] = records[i];
            }

            BlobBuilderArray<int> ranges = builder.Allocate(ref root.Counts, counts.Length);
            for (int i = 0; i < counts.Length; i++)
            {
                ranges[i] = counts[i];
            }

            return builder.CreateBlobAssetReference<PaintedInstanceBlob>(Allocator.Persistent);
        }
    }
}
