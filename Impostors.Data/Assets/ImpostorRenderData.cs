using System;

namespace Impostors.Data
{
    /// <summary>CPU-side placements. This object is never serialized into a scene or asset.</summary>
    public sealed class ImpostorRenderData
    {
        public ImpostorRenderData(ImpostorDefinition[] definitions, ImpostorInstance[] instances)
        {
            Definitions = definitions ?? throw new ArgumentNullException(nameof(definitions));
            Instances = instances ?? throw new ArgumentNullException(nameof(instances));
        }

        public ImpostorDefinition[] Definitions { get; }
        public ImpostorInstance[] Instances { get; }

        public static implicit operator ImpostorRenderData(ImpostorInstanceSet data)
        {
            return data ? new ImpostorRenderData(data.ResolveDefinitions(), data.Instances) : null;
        }
    }
}
