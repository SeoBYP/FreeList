using System.Buffers.Binary;

namespace FreeList;

public sealed class BlockAllocator
{
    public enum FitPolicy { First, Best }
    public FitPolicy Policy { get; set; } = FitPolicy.First;
    
    public enum FreeOrder { Lifo, Address }
    public FreeOrder Order { get; set; } = FreeOrder.Lifo;
    
    private const int HeaderSize = 4; // int 하나
    private const int Alignment = 4; // 모든 블록 크기는 4의 배수
    private const int MinPayload = 8; // 이보다 작은 자리는 만들지 않는다
    private const int FooterSize = 4;
    private const int BlockOverhead = HeaderSize + FooterSize;
    private const int MinBlock = BlockOverhead + MinPayload;

    private readonly byte[] _arena;

    private int _freeHead;

    // ── 측정용 계측. 동작에는 영향이 없다 ──────────────
    private long _probeCount;       // 탐색 루프가 빈 블록을 들여다본 총 횟수
    private long _failProbeCount;   // 그중 실패한 탐색이 쓴 몫
    private long _allocCount;       // 성공한 할당 수
    private long _insertProbeCount;   // PushFree의 탐색 루프가 노드를 지나간 횟수
    private long _freeCount;          // 성공한 해제 수

    public long ProbeCount => _probeCount;
    public long FailProbeCount => _failProbeCount;
    public long AllocCount => _allocCount;
    
    public long InsertProbeCount => _insertProbeCount;
    public long FreeCount => _freeCount;

    public double InsertProbesPerFree
        => _freeCount == 0 ? 0 : (double)_insertProbeCount / _freeCount;
    /// <summary>
    /// 성공한 탐색만 센 평균. 실패한 탐색은 리스트를 끝까지 훑으므로
    /// 섞어서 세면 지표가 오염된다 (작은 객체 워크로드에서 절반 이상이 실패 몫이었다).
    /// </summary>
    public double ProbesPerAlloc
        => _allocCount == 0 ? 0 : (double)(_probeCount - _failProbeCount) / _allocCount;

    public void ResetCounters()
    {
        _probeCount = 0;
        _failProbeCount = 0;
        _allocCount = 0;
        _insertProbeCount = 0;
        _freeCount = 0;
    }

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
        if (size <= 0 || size > Capacity)
        {
            offset = 0;
            return false;
        }
        var need = Math.Max(MinBlock, BlockOverhead + Align(size));
        
        var probesBefore = _probeCount;
        var pos = FindFit(need);
        if (pos == -1)
        {
            _failProbeCount += _probeCount - probesBefore;
            offset = 0;
            return false;
        }
        var blockSize = ReadSize(pos);
        
        var prevFree = ReadPrev(pos);
        var nextFree = ReadNext(pos);

        RemoveFree(pos);
        
        var leftover = blockSize - need;
        if (leftover >= MinBlock)
        {
            WriteBlock(pos, need, false);
            WriteBlock(pos + need, leftover, true);
            if (Order == FreeOrder.Address)
                LinkFree(prevFree, pos + need, nextFree);
            else
                PushFree(pos + need);
        }
        else   // 쪼개면 아무도 못 쓰는 조각이 남는다. 통째로 준다
        {
            WriteBlock(pos, blockSize, false); 
        }

        _allocCount++;
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
        // 이중 해제 거부
        if (IsFree(start))
            return false;

        // 뒤 블록이 비었으면 흡수한다 (헤더만으로 가능)
        var next = start + size;
        if (next < Capacity && IsFree(next))
        {
            RemoveFree(next);
            size += ReadSize(next);
        }

        // 앞 블록이 비었으면 그쪽에 흡수된다 (푸터로 시작 위치를 역산)
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
        _freeCount++;
        
        return true;
    }

    private void LinkFree(int prev, int blockStart, int next)
    {
        // 첫 시작 부분
        if (prev == -1)
            _freeHead = blockStart;
        else // 아니면 이전 부분에 blockStart를 연결한다.
            WriteNext(prev, blockStart);
        
        WritePrev(blockStart, prev);
        WriteNext(blockStart, next);
        
        // 다음 부분에 blockStart를 연결한다.
        if (next != -1)
            WritePrev(next, blockStart);
    }

    private void PushFree(int blockStart)
    {
        if (Order == FreeOrder.Lifo)
        {
            LinkFree(-1, blockStart, _freeHead);
            return;
        }
        
        // 나보다 주소가 큰 첫 노드를 찾는다. 그게 b, 직전에 지나온 것이 a
        var prev = -1;
        var cur = _freeHead;

        while (cur != -1 && cur < blockStart)
        {
            _insertProbeCount++;
            prev = cur;
            cur = ReadNext(cur);
        }
        
        LinkFree(prev, blockStart, cur);
    }

    private void RemoveFree(int blockStart)
    {
        var prev = ReadPrev(blockStart);
        var next = ReadNext(blockStart);
        if (prev != -1)
            WriteNext(prev, next);
        else
            _freeHead = next;   // 내가 머리였으니 다음을 머리로
        if (next != -1)
            WritePrev(next, prev);
    }
    
    public Span<byte> AsSpan(int offset)
    {
        if (offset < HeaderSize || offset >= Capacity)
            throw new ArgumentException(nameof(offset));
        if (offset % Alignment != 0)
            throw new ArgumentException(nameof(offset));
        
        var start = offset - HeaderSize;
        var blockSize  = ReadSize(start);
        
        if(blockSize  < MinBlock || start + blockSize  > Capacity)
            throw new ArgumentException(nameof(offset));
        if (IsFree(start))
            throw new ArgumentException("Block is free");

        var size = blockSize - BlockOverhead;
        return _arena.AsSpan(offset, size);
    }

    private int FindFit(int need)
    {
        switch (Policy)
        {
            case FitPolicy.First:
            {
                var pos = _freeHead;
                while (pos != -1)
                {
                    _probeCount++;
                    if(ReadSize(pos) >= need)
                        return pos;
                    pos = ReadNext(pos);
                }
                return -1;
            }
            case FitPolicy.Best:
            {
                var best = -1;
                var bestSize = 0;
                var pos = _freeHead;
                while (pos != -1)
                {
                    _probeCount++;
                    var bs = ReadSize(pos);
                    if(bs >= need && (best == -1 || bs < bestSize))
                    {
                        best = pos;
                        bestSize = bs;
                        if (bs == need)   // 정확히 맞으면 더 나은 후보는 없다
                            return best;
                    }
                    pos = ReadNext(pos);
                }
                return best;
            }
            default:
                return -1;
        }
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

    /// <summary>
    /// 진단용. 아레나를 물리적으로 걸으며 빈 블록의 시작 위치를 주소 오름차순으로 모은다.
    /// GetStats와 같은 루프이며, 세는 대신 위치를 담는다는 것만 다르다.
    /// </summary>
    public int[] DebugFreeBlockStarts()
    {
        var starts = new List<int>();
        var pos = 0;
        while (pos < Capacity)
        {
            var size = ReadSize(pos);
            if (size < MinBlock)
                throw new InvalidOperationException($"블록 크기가 너무 작다 — pos={pos}, size={size}");
            if (pos + size > Capacity)
                throw new InvalidOperationException($"블록 헤더가 깨졌다 — pos={pos}, size={size}");

            if (IsFree(pos))
                starts.Add(pos);

            pos += size;
        }

        return starts.ToArray();
    }

    /// <summary>
    /// 진단용. free 리스트를 머리부터 따라가며 블록 시작 위치를 체인 순서대로 모은다.
    ///
    /// DebugFreeBlockStarts()와 비교하는 것이 이 자료구조의 핵심 불변식이다.
    ///   Order가 Address면 두 결과가 순서까지 같아야 하고,
    ///   Lifo면 집합으로 같아야 한다.
    /// </summary>
    public int[] DebugFreeList()
    {
        // 빈 블록은 아무리 많아도 이 개수를 넘을 수 없다. 넘었다면 체인에 순환이 있다.
        // 이 방어가 없으면 링크가 꼬였을 때 테스트가 빨간불 대신 멈춘다.
        var limit = Capacity / MinBlock;

        var chain = new List<int>();
        var pos = _freeHead;
        while (pos != -1)
        {
            if (pos < 0 || pos >= Capacity || pos % Alignment != 0)
                throw new InvalidOperationException(
                    $"free 리스트가 블록 시작이 아닌 곳을 가리킨다 — pos={pos}, "
                    + $"지금까지 경로=[{string.Join(", ", chain)}]");

            if (chain.Count >= limit)
                throw new InvalidOperationException(
                    $"free 리스트에 순환이 있다 — {limit}개를 넘게 걸었다. "
                    + $"_freeHead={_freeHead}, 경로=[{string.Join(", ", chain.Take(20))}...]");

            chain.Add(pos);
            pos = ReadNext(pos);
        }

        return chain.ToArray();
    }
}