namespace FreeList;

/// <summary>
/// SlotMap의 원소를 가리키는 핸들. 인덱스 + 발급 회차(세대).
/// 칸이 재사용되면 세대가 달라지므로 옛 핸들은 자동으로 무효가 된다.
/// 발급은 SlotMap만 할 수 있도록 생성자를 internal로 둔다.
/// </summary>
public readonly struct SlotHandle : IEquatable<SlotHandle>
{
    public int Index { get; }
    public int Generation { get; }

    internal SlotHandle(int index, int generation)
    {
        Index = index;
        Generation = generation;
    }
    
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

    public static bool operator ==(SlotHandle left, SlotHandle right)
    {
        return left.Equals(right);
    }

    public static bool operator !=(SlotHandle left, SlotHandle right)
    {
        return !left.Equals(right);
    }

    // 디버거와 테스트 실패 메시지에 #3(g2) 형태로 찍힌다
    public override string ToString()
    {
        return $"#{Index}(g{Generation})";
    }
}