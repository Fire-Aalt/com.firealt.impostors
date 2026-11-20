using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Transforms;

namespace EntityPainter
{
    /// <summary>
    /// A runtime copy of a baked prefab that painted instances are instantiated from. The copy drops EntityGuid, so
    /// live-baking patches to the baked prefab never reach the instances, and it carries the spawner's
    /// <see cref="PaintedInstanceOwner"/>, so destroying an owner removes its template together with its instances.
    /// </summary>
    public struct PaintedTemplate
    {
        public Entity Entity;
        public PaintedTransformKind Kind;

        /// <summary>The prefab root's own transform, applied inside each painted placement.</summary>
        public LocalTransform BaseLocal;

        /// <summary>The prefab root's own matrix, for prefabs whose root only has LocalToWorld.</summary>
        public float4x4 BaseWorld;

        /// <summary>Copies <paramref name="prefab"/> into a template owned by <paramref name="owner"/>.</summary>
        public static PaintedTemplate Create(EntityManager entityManager, Entity prefab, PaintedInstanceOwner owner)
        {
            Entity template = entityManager.Instantiate(prefab);
            using (NativeArray<Entity> group = GetGroup(entityManager, template))
            {
                entityManager.AddComponent<Prefab>(group);
                entityManager.RemoveComponent<EntityGuid>(group);
                entityManager.AddSharedComponent(group, owner);
            }

            var result = new PaintedTemplate
            {
                Entity = template,
                BaseLocal = LocalTransform.Identity,
                BaseWorld = float4x4.identity,
            };

            if (entityManager.HasComponent<LocalTransform>(template))
            {
                result.Kind = PaintedTransformKind.LocalTransform;
                result.BaseLocal = entityManager.GetComponentData<LocalTransform>(template);
            }
            else if (entityManager.HasComponent<LocalToWorld>(template))
            {
                result.Kind = PaintedTransformKind.LocalToWorld;
                result.BaseWorld = entityManager.GetComponentData<LocalToWorld>(template).Value;
            }

            return result;
        }

        public readonly LocalTransform ComposeLocal(in PaintedRecord record)
        {
            return LocalTransform.FromPositionRotationScale(record.Position, record.DecodeRotation(), record.DecodeScale())
                .TransformTransform(BaseLocal);
        }

        public readonly float4x4 ComposeWorld(in PaintedRecord record)
        {
            return math.mul(float4x4.TRS(record.Position, record.DecodeRotation(), record.DecodeScale()), BaseWorld);
        }

        /// <summary>Places one instance on the main thread. The spawn system writes in parallel jobs instead.</summary>
        public readonly void Write(EntityManager entityManager, Entity instance, in PaintedRecord record)
        {
            switch (Kind)
            {
                case PaintedTransformKind.LocalTransform:
                    entityManager.SetComponentData(instance, ComposeLocal(record));
                    break;
                case PaintedTransformKind.LocalToWorld:
                    entityManager.SetComponentData(instance, new LocalToWorld { Value = ComposeWorld(record) });
                    break;
            }
        }

        private static NativeArray<Entity> GetGroup(EntityManager entityManager, Entity root)
        {
            if (entityManager.HasBuffer<LinkedEntityGroup>(root))
            {
                return entityManager.GetBuffer<LinkedEntityGroup>(root).Reinterpret<Entity>().ToNativeArray(Allocator.Temp);
            }

            var group = new NativeArray<Entity>(1, Allocator.Temp);
            group[0] = root;
            return group;
        }
    }
}
