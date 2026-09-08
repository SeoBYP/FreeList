namespace FreeList;

public readonly struct AllocatorStats
{
    // 현재 얼마나 많은 바이트를 쓰고 있는 지를 나타내는 값
    public int UsedBytes { get; init; }
    public int FreeBytes { get; init;}
    public int OverheadBytes { get; init;}
    public int FreeBlockCount { get; init;}
    public int LargestFreeBlock { get; init;}
    
    
}