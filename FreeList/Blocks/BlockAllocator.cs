using System.Buffers.Binary;

namespace FreeList;

public sealed class BlockAllocator
{
    private const int HeaderSize = 4; // int 하나
    private const int Alignment = 4; // 모든 블록 크기는 4의 배수
    private const int MinPayload = 4; // 이보다 작은 자리는 만들지 않는다
    private const int MinBlock = HeaderSize + MinPayload; // 8

    private readonly byte[] _arena;

    public int Capacity => _arena.Length;

    public BlockAllocator(int capacity)
    {
        if (capacity <= 0)
            throw new ArgumentException("capacity must be greater than 0");
        if (capacity < MinBlock)
            throw new ArgumentException("capacity must be at least " + MinBlock);
        if (capacity % Alignment != 0)
            throw new ArgumentException("capacity must be aligned");
        _arena = new byte[capacity];
        WriteHeader(blockStart: 0, size: capacity, isFree: true);
    }

    private static int Align(int n)
    {
        return (n + (Alignment - 1)) / Alignment * Alignment;
    }

    private void WriteHeader(int blockStart, int size, bool isFree)
    {
        if (size % Alignment != 0)
            throw new ArgumentException("size must be aligned");
        BinaryPrimitives.WriteInt32LittleEndian(_arena.AsSpan(blockStart), size | (isFree ? 1 : 0));
    }

    private int ReadRaw(int blockStart)
        => BinaryPrimitives.ReadInt32LittleEndian(_arena.AsSpan(blockStart));

    private int ReadSize(int blockStart)
    {
        var size = ReadRaw(blockStart);
        return size & ~1;
    }

    private bool IsFree(int blockStart)
    {
        var size = ReadRaw(blockStart);
        return (size & 1) != 0;
    }

    public bool TryAlloc(int size, out int offset)
    {
        if (size <= 0)
        {
            offset = 0;
            return false;
        }

        var need = HeaderSize + Align(size);
        var pos = 0;
        var blockSize = 0;
        while (pos < Capacity)
        {
            blockSize = ReadSize(pos);

            if (IsFree(pos) && blockSize >= need)
            {
                break;
            }

            pos += blockSize;
        }

        // 끝까지 못찾은 상태
        if (pos >= Capacity)
        {
            offset = 0;
            return false;
        }

        var leftover = blockSize - need;
        if (leftover >= MinBlock) // "남는 게 충분하면"
        {
            WriteHeader(pos, need, false);
            WriteHeader(pos + need, leftover, true); // ← 쪼개고 있다
        }
        else // "남는 게 너무 작으면"   
        {
            WriteHeader(pos, blockSize, false); // ← 통째로 주고 있다
        }

        offset = pos + HeaderSize;
        return true;
    }

    public bool Free(int offset)
    {
        if (offset < HeaderSize || offset >= Capacity)
            return false;
        if(offset % Alignment != 0)
            return false;
        var start = offset - HeaderSize;
        var size = ReadSize(start);
        if (size < MinBlock || start + size > Capacity)
            return false;
        // 이미 비어 있으면 스킵
        if (IsFree(start))
            return false;
        WriteHeader(start, size, true);
        return true;
    }

    public Span<byte> AsSpan(int offset)
    {
        var start = offset - HeaderSize;
        var length = ReadSize(start) - HeaderSize;
        return _arena.AsSpan(offset, length);
    }

    public AllocatorStats GetStats()
    {
        int pos = 0;
        int freeBytes = 0;
        int freeBlockCount = 0;
        int usedBytes = 0;
        int overheadBytes = 0;
        int largestFreeBlock = 0;
        while (pos < Capacity)
        {
            var size = ReadSize(pos);
            if (size < MinBlock)
                throw new InvalidOperationException($"블록 크기가 너무 작다 — pos={pos}, size={size}");
            if (pos + size > Capacity)
                throw new Exception($"블록 헤더가 깨졌다 — pos={pos}, size={size}");

            var payload = size - HeaderSize;
            if (IsFree(pos))
            {
                freeBytes += payload;
                freeBlockCount++;
                largestFreeBlock = Math.Max(largestFreeBlock, payload);
            }
            else
            {
                usedBytes += payload;
            }

            overheadBytes += HeaderSize;
            pos += size;
        }

        return new AllocatorStats
        {
            FreeBlockCount = freeBlockCount,
            LargestFreeBlock = largestFreeBlock,
            UsedBytes = usedBytes,
            FreeBytes = freeBytes,
            OverheadBytes = overheadBytes,
        };
    }
}