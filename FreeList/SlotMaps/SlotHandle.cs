namespace FreeList;

public readonly struct SlotHandle(int index, int generation) : IEquatable<SlotHandle>
{
    public int Index { get; } = index;
    public int Generation { get; } = generation;
    
    public bool Equals(SlotHandle other)
    {
        return Index == other.Index && Generation == other.Generation;
    }

    public override bool Equals(object? obj)
    {
        return obj is SlotHandle other && Equals(other);
    }

    public override int GetHashCode()
    {
        return HashCode.Combine(Index, Generation);
    }
}