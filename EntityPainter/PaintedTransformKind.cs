namespace EntityPainter
{
    /// <summary>Which transform component a painted instance's placement is written to.</summary>
    public enum PaintedTransformKind : byte
    {
        None,
        LocalTransform,
        LocalToWorld,
    }
}
