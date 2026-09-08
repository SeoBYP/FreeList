using System.Buffers.Binary;

namespace FreeList;

public sealed class BlockAllocator
{
    private const int HeaderSize = 4; // int 하나
    private const int Alignment = 4; // 모든 블록 크기는 4의 배수
    private const int MinPayload = 8; // 이보다 작은 자리는 만들지 않는다
    private const int FooterSize = 4;
    private const int BlockOverhead = HeaderSize + FooterSize;
    private const int MinBlock = BlockOverhead + MinPayload;

    private readonly byte[] _arena;

    private int _freeHead;

    public int Capacity => _arena.Length;

    public BlockAllocator(int capacity)
    {
        if (capacity <= 0)
            throw new ArgumentException("capacity must be greater than 0");
        if (capacity < MinBlock)
            throw new ArgumentException("capacity must be at least " + MinBlock);
        if (capacity % Alignment != 0)
            throw new ArgumentException("capacity must be aligned");
       
        _freeHead = -1;
        _arena = new byte[capacity];
        WriteBlock(blockStart: 0, size: capacity, isFree: true);
        PushFree(0);
    }

    private static int Align(int n)
    {
        return (n + (Alignment - 1)) / Alignment * Alignment;
    }

    private void WriteBlock(int blockStart, int size, bool isFree)
    {
        if (size % Alignment != 0)
            throw new ArgumentException("size must be aligned");
        var headerPos = blockStart;
        BinaryPrimitives.WriteInt32LittleEndian(_arena.AsSpan(headerPos), size | (isFree ? 1 : 0));

        var footerPos = blockStart + size - FooterSize;
        BinaryPrimitives.WriteInt32LittleEndian(_arena.AsSpan(footerPos), size | (isFree ? 1 : 0));
    }

    private int PrevPos(int blockStart)
    {
        return blockStart + HeaderSize;
    }

    private int NextPos(int blockStart)
    {
        return blockStart + HeaderSize + 4;
    }

    private int ReadPrev(int blockStart)
    {
        return ReadRaw(PrevPos(blockStart));
    }

    private int ReadNext(int blockStart)
    {
        return ReadRaw(NextPos(blockStart));
    }

    private void WritePrev(int blockStart, int value)
    {
        var prevPos = PrevPos(blockStart);
        BinaryPrimitives.WriteInt32LittleEndian(_arena.AsSpan(prevPos), value);
    }

    private void WriteNext(int blockStart, int value)
    {
        var next = NextPos(blockStart);
        BinaryPrimitives.WriteInt32LittleEndian(_arena.AsSpan(next), value);
    }

    private int ReadRaw(int blockStart)
        => BinaryPrimitives.ReadInt32LittleEndian(_arena.AsSpan(blockStart));

    private int ReadSize(int blockStart)
    {
        var size = ReadRaw(blockStart);
        return size & ~1;
    }

    private int ReadPrevSize(int blockStart)
    {
        var size = ReadRaw(blockStart - FooterSize);
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
        var need = Math.Max(MinBlock, BlockOverhead + Align(size));
        
        var pos = _freeHead;
        var blockSize = 0;
        
        while (pos != -1)
        {
            blockSize = ReadSize(pos);
            if (blockSize >= need) break;
            pos = ReadNext(pos);
        }
        if (pos == -1)
        {
            offset = 0;
            return false;
        }

        RemoveFree(pos);
        
        var leftover = blockSize - need;
        if (leftover >= MinBlock) // "남는 게 충분하면"
        {
            WriteBlock(pos, need, false);
            WriteBlock(pos + need, leftover, true); // ← 쪼개고 있다
            PushFree(pos + need); // ← 쪼갠 나머지를 등록
        }
        else // "남는 게 너무 작으면"   
        {
            WriteBlock(pos, blockSize, false); 
        }

        offset = pos + HeaderSize;
        return true;
    }

    public bool Free(int offset)
    {
        if (offset < HeaderSize || offset >= Capacity)
            return false;
        if (offset % Alignment != 0)
            return false;
        var start = offset - HeaderSize;
        var size = ReadSize(start);
        if (size < MinBlock || start + size > Capacity)
            return false;
        // 이미 비어 있으면 스킵
        if (IsFree(start))
            return false;

        // 뒤와 합치지
        var next = start + size;
        if (next < Capacity && IsFree(next))
        {
            RemoveFree(next);
            size += ReadSize(next);
        }

        // 앞에 공간이 있으면 앞과 합치지
        if (start > 0)
        {
            var prevSize = ReadPrevSize(start);
            var prevStart = start - prevSize;
            if (IsFree(prevStart))
            {
                RemoveFree(prevStart);
                start = prevStart;
                size += prevSize;
            }
        }

        WriteBlock(start, size, true);
        PushFree(start);
        return true;
    }

    private void PushFree(int blockStart)
    {
        WritePrev(blockStart, -1);
        WriteNext(blockStart, _freeHead);
        if(_freeHead != -1)
            WritePrev(_freeHead, blockStart); // 기존 머리가 나를 앞으로 가리키게
        
        _freeHead = blockStart;
    }

    private void RemoveFree(int blockStart)
    {
        var prev = ReadPrev(blockStart);
        var next = ReadNext(blockStart);
        if (prev != -1)
            WriteNext(prev, next);
        else
            _freeHead = next;
        if (next != -1)
            WritePrev(next, prev);
    }
    
    public Span<byte> AsSpan(int offset)
    {
        var start = offset - HeaderSize;
        var length = ReadSize(start) - BlockOverhead;
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

            var payload = size - BlockOverhead;
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

            overheadBytes += BlockOverhead;
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