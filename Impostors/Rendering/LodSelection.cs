namespace Impostors
{
    public struct LodSelection
    {
        public int Level;
        public float HighDetailOpacity;

        public readonly bool Crossfading => HighDetailOpacity > 0 && HighDetailOpacity < 1;
    }
}
