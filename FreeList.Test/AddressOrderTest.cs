using Xunit;

namespace FreeList.Test;

/// <summary>
/// 5단계: free 리스트를 주소 오름차순으로 유지하는 정책.
///
/// 핵심 불변식은 하나다.
///   Address 정책이면  DebugFreeList() == DebugFreeBlockStarts()   (순서까지)
///   Lifo 정책이면     둘이 집합으로 같다
///
/// 체인이 아레나 현실과 어긋나는 모든 버그가 이 한 줄에서 잡힌다.
/// </summary>
public class AddressOrderTest
{
    private const int HeaderSize = 4;
    private const int Req = 24;                      // need = max(16, 8 + align(24)) = 32
    private const int BlockNeed = 32;
    private const int Blocks = 8;
    private const int Cap = BlockNeed * Blocks;      // 256, 딱 8칸

    private static int Start(int offset) => offset - HeaderSize;

    /// <summary>32바이트 블록 8개로 아레나를 꽉 채운다. 빈 블록이 하나도 없는 상태.</summary>
    private static BlockAllocator Full(BlockAllocator.FreeOrder order, out int[] offsets)
    {
        var a = new BlockAllocator(Cap) { Order = order };
        offsets = new int[Blocks];
        for (int i = 0; i < Blocks; i++)
            Assert.True(a.TryAlloc(Req, out offsets[i]), $"{i}번째 할당이 실패했다");

        Assert.Empty(a.DebugFreeList());
        return a;
    }

    /// <summary>체인과 아레나가 일치하는지. Address면 순서까지, Lifo면 집합으로.</summary>
    private static void AssertChainMatchesArena(BlockAllocator a, BlockAllocator.FreeOrder order)
    {
        var chain = a.DebugFreeList();
        var arena = a.DebugFreeBlockStarts();

        Assert.Equal(arena.Length, chain.Length);
        Assert.Equal(arena.Length, a.GetStats().FreeBlockCount);
        Assert.Equal(chain.Length, chain.Distinct().Count());

        if (order == BlockAllocator.FreeOrder.Address)
            Assert.Equal(arena, chain);                       // 순서까지 같아야 한다
        else
            Assert.Equal(arena.OrderBy(x => x), chain.OrderBy(x => x));
    }

    // ── 삽입 위치 네 경우 ────────────────────────────────

    [Fact]
    public void 빈_리스트에_넣으면_혼자_머리가_된다()
    {
        var a = Full(BlockAllocator.FreeOrder.Address, out var off);

        a.Free(off[3]);

        Assert.Equal(new[] { Start(off[3]) }, a.DebugFreeList());
        AssertChainMatchesArena(a, BlockAllocator.FreeOrder.Address);
    }

    [Fact]
    public void 가장_낮은_주소는_머리에_들어간다()
    {
        var a = Full(BlockAllocator.FreeOrder.Address, out var off);

        a.Free(off[6]);          // 높은 주소부터
        a.Free(off[4]);
        a.Free(off[0]);          // 마지막에 제일 낮은 것 → 머리로 가야 한다

        Assert.Equal(new[] { Start(off[0]), Start(off[4]), Start(off[6]) }, a.DebugFreeList());
    }

    [Fact]
    public void 가장_높은_주소는_꼬리에_들어간다()
    {
        var a = Full(BlockAllocator.FreeOrder.Address, out var off);

        a.Free(off[0]);          // 낮은 주소부터
        a.Free(off[2]);
        a.Free(off[6]);          // 마지막에 제일 높은 것 → 끝까지 걸어가 꼬리로

        Assert.Equal(new[] { Start(off[0]), Start(off[2]), Start(off[6]) }, a.DebugFreeList());
    }

    [Fact]
    public void 중간_주소는_사이에_끼워진다()
    {
        var a = Full(BlockAllocator.FreeOrder.Address, out var off);

        a.Free(off[0]);
        a.Free(off[6]);
        a.Free(off[3]);          // 0과 6 사이로

        Assert.Equal(new[] { Start(off[0]), Start(off[3]), Start(off[6]) }, a.DebugFreeList());
    }

    // ── 정책 대조 ────────────────────────────────────────

    [Fact]
    public void LIFO는_최근_해제한_것을_머리에_둔다()
    {
        var a = Full(BlockAllocator.FreeOrder.Lifo, out var off);

        a.Free(off[0]);
        a.Free(off[4]);
        a.Free(off[2]);

        // 넣은 역순. 주소와 무관하다
        Assert.Equal(new[] { Start(off[2]), Start(off[4]), Start(off[0]) }, a.DebugFreeList());
        AssertChainMatchesArena(a, BlockAllocator.FreeOrder.Lifo);
    }

    [Fact]
    public void 같은_해제_순서에서_두_정책이_다른_리스트를_만든다()
    {
        var lifo = Full(BlockAllocator.FreeOrder.Lifo, out var o1);
        var addr = Full(BlockAllocator.FreeOrder.Address, out var o2);

        foreach (int i in new[] { 0, 4, 2 })
        {
            lifo.Free(o1[i]);
            addr.Free(o2[i]);
        }

        Assert.NotEqual(lifo.DebugFreeList(), addr.DebugFreeList());
        Assert.Equal(lifo.DebugFreeList().OrderBy(x => x), addr.DebugFreeList().OrderBy(x => x));
    }

    // ── 병합과 분할 ──────────────────────────────────────

    [Fact]
    public void 앞_블록과_병합하면_병합된_시작_주소가_들어간다()
    {
        var a = Full(BlockAllocator.FreeOrder.Address, out var off);

        a.Free(off[6]);
        a.Free(off[0]);                       // 리스트: [0번, 6번]
        a.Free(off[1]);                       // 0번과 병합된다. start가 앞으로 이동

        // 들어가야 하는 것은 off[1]이 아니라 병합된 블록의 시작, 즉 off[0]의 자리
        Assert.Equal(new[] { Start(off[0]), Start(off[6]) }, a.DebugFreeList());
        AssertChainMatchesArena(a, BlockAllocator.FreeOrder.Address);
    }

    [Fact]
    public void 뒤_블록과_병합해도_리스트가_어긋나지_않는다()
    {
        var a = Full(BlockAllocator.FreeOrder.Address, out var off);

        a.Free(off[6]);
        a.Free(off[3]);                       // 리스트: [3번, 6번]
        a.Free(off[2]);                       // 3번을 흡수한다. 3번은 리스트에서 빠져야 한다

        Assert.Equal(new[] { Start(off[2]), Start(off[6]) }, a.DebugFreeList());
        AssertChainMatchesArena(a, BlockAllocator.FreeOrder.Address);
    }

    [Fact]
    public void 양쪽과_병합하면_리스트에서_둘이_빠지고_하나가_들어간다()
    {
        var a = Full(BlockAllocator.FreeOrder.Address, out var off);

        a.Free(off[2]);
        a.Free(off[4]);
        a.Free(off[7]);                       // 리스트: [2번, 4번, 7번]
        a.Free(off[3]);                       // 2번과 4번을 동시에 흡수

        Assert.Equal(new[] { Start(off[2]), Start(off[7]) }, a.DebugFreeList());
        Assert.Equal(2, a.GetStats().FreeBlockCount);
        AssertChainMatchesArena(a, BlockAllocator.FreeOrder.Address);
    }

    [Fact]
    public void 분할된_나머지는_원래_블록_자리에_들어간다()
    {
        var a = Full(BlockAllocator.FreeOrder.Address, out var off);

        a.Free(off[0]);
        a.Free(off[1]);                       // 0번과 병합 → 시작 0, 크기 64인 빈 블록
        a.Free(off[6]);                       // 리스트: [0, 6번]

        Assert.True(a.TryAlloc(Req, out int fresh));   // 64짜리에서 32를 떼어낸다

        // 남은 32는 off[1] 자리에 생기고, 리스트에서 0번이 있던 순서 자리를 그대로 물려받는다
        Assert.Equal(Start(off[0]), Start(fresh));
        Assert.Equal(new[] { Start(off[1]), Start(off[6]) }, a.DebugFreeList());
        AssertChainMatchesArena(a, BlockAllocator.FreeOrder.Address);
    }

    [Fact]
    public void 분할_삽입은_리스트를_걷지_않는다()
    {
        var a = new BlockAllocator(4096) { Order = BlockAllocator.FreeOrder.Address };

        // 앞쪽에 32짜리 빈 블록을 스무 개쯤 흩뿌려 리스트를 길게 만든다
        var live = new List<int>();
        for (int i = 0; i < 40; i++)
            if (a.TryAlloc(Req, out int o)) live.Add(o);
        for (int i = 0; i < live.Count; i += 2)
            a.Free(live[i]);

        int listLen = a.DebugFreeList().Length;
        Assert.True(listLen > 10, $"리스트가 충분히 길어야 의미 있는 검사다 (지금 {listLen})");

        int freeBlocksBefore = a.GetStats().FreeBlockCount;
        a.ResetCounters();

        // 72바이트가 필요한 요청. 앞쪽 32짜리들은 전부 안 맞으니 꼬리의 큰 블록까지 간다.
        // 거기서 분할이 일어나고, 나머지는 "방금 뺀 블록이 있던 자리"에 그대로 들어가야 한다.
        Assert.True(a.TryAlloc(64, out _));

        Assert.Equal(freeBlocksBefore, a.GetStats().FreeBlockCount);   // 분할이 실제로 일어났다
        Assert.Equal(0L, a.InsertProbeCount);                          // 그런데 걷지는 않았다
        AssertChainMatchesArena(a, BlockAllocator.FreeOrder.Address);
    }

    // ── 계측 ─────────────────────────────────────────────

    [Fact]
    public void 삽입_탐색도_세어야_한다()
    {
        var a = Full(BlockAllocator.FreeOrder.Address, out var off);
        a.ResetCounters();

        a.Free(off[0]);                       // 빈 리스트 → 걷지 않는다
        Assert.Equal(0L, a.InsertProbeCount);

        a.Free(off[7]);                       // 꼬리까지 걸어야 한다 → 최소 1
        Assert.True(a.InsertProbeCount > 0,
            "PushFree가 리스트를 걷는데 세지 않으면 '주소 순서가 공짜'라는 결론이 나온다");
    }

    [Fact]
    public void ResetCounters가_삽입_카운터도_지운다()
    {
        var a = Full(BlockAllocator.FreeOrder.Address, out var off);

        a.Free(off[0]);
        a.Free(off[5]);
        a.Free(off[2]);

        a.ResetCounters();

        Assert.Equal(0L, a.InsertProbeCount);
        Assert.Equal(0L, a.FreeCount);
    }

    // ── 두 정책이 같아야 하는 것 ─────────────────────────

    [Theory]
    [InlineData(BlockAllocator.FreeOrder.Lifo)]
    [InlineData(BlockAllocator.FreeOrder.Address)]
    public void 정책과_무관하게_해제한_공간은_다시_쓸_수_있다(BlockAllocator.FreeOrder order)
    {
        var a = Full(order, out var off);

        Assert.False(a.TryAlloc(Req, out _), "꽉 찼는데 할당이 성공했다");

        a.Free(off[3]);
        Assert.True(a.TryAlloc(Req, out int reused));
        Assert.Equal(Start(off[3]), Start(reused));
    }

    [Theory]
    [InlineData(BlockAllocator.FreeOrder.Lifo)]
    [InlineData(BlockAllocator.FreeOrder.Address)]
    public void 정책과_무관하게_이중_해제는_거부된다(BlockAllocator.FreeOrder order)
    {
        var a = Full(order, out var off);

        Assert.True(a.Free(off[2]));
        Assert.False(a.Free(off[2]));
        AssertChainMatchesArena(a, order);
    }

    [Theory]
    [InlineData(BlockAllocator.FreeOrder.Lifo)]
    [InlineData(BlockAllocator.FreeOrder.Address)]
    public void 정책과_무관하게_전부_해제하면_통짜로_돌아온다(BlockAllocator.FreeOrder order)
    {
        var a = Full(order, out var off);

        foreach (int o in off) Assert.True(a.Free(o));

        Assert.Single(a.DebugFreeList());
        Assert.Equal(0, a.DebugFreeList()[0]);
        Assert.Equal(1, a.GetStats().FreeBlockCount);
        Assert.Equal(Cap - 8, a.GetStats().LargestFreeBlock);   // BlockOverhead 8
    }

    [Theory]
    [InlineData(BlockAllocator.FreeOrder.Lifo)]
    [InlineData(BlockAllocator.FreeOrder.Address)]
    public void 정책과_무관하게_이웃을_건드려도_내_데이터는_그대로다(BlockAllocator.FreeOrder order)
    {
        var a = new BlockAllocator(1024) { Order = order };

        Assert.True(a.TryAlloc(64, out int left));
        Assert.True(a.TryAlloc(64, out int mine));
        Assert.True(a.TryAlloc(64, out int right));

        a.AsSpan(mine).Fill(0xCD);

        a.Free(left);
        a.Free(right);
        a.TryAlloc(32, out _);
        a.TryAlloc(48, out _);

        foreach (byte b in a.AsSpan(mine))
            Assert.Equal(0xCD, b);
    }

    // ── 랜덤 스트레스 ────────────────────────────────────

    [Theory]
    [InlineData(BlockAllocator.FreeOrder.Lifo)]
    [InlineData(BlockAllocator.FreeOrder.Address)]
    public void 랜덤_2000회_동안_체인과_아레나가_일치한다(BlockAllocator.FreeOrder order)
    {
        var a = new BlockAllocator(8192) { Order = order };
        var rng = new Random(20260908);        // 고정 시드. 실패하면 그대로 재현된다
        var live = new List<int>();

        for (int step = 0; step < 2000; step++)
        {
            if (live.Count > 0 && rng.Next(2) == 0)
            {
                int j = rng.Next(live.Count);
                (live[j], live[^1]) = (live[^1], live[j]);
                Assert.True(a.Free(live[^1]), $"{step}번째 스텝의 해제가 실패했다");
                live.RemoveAt(live.Count - 1);
            }
            else
            {
                if (a.TryAlloc(rng.Next(8, 256), out int o)) live.Add(o);
            }

            if (step % 50 != 0) continue;

            var chain = a.DebugFreeList();
            var arena = a.DebugFreeBlockStarts();

            Assert.Equal(arena.Length, chain.Length);
            Assert.Equal(chain.Length, chain.Distinct().Count());

            if (order == BlockAllocator.FreeOrder.Address)
                Assert.True(arena.SequenceEqual(chain),
                    $"{step}번째 스텝에서 체인이 주소 순서를 벗어났다\n"
                    + $"  아레나: [{string.Join(", ", arena)}]\n"
                    + $"  체인  : [{string.Join(", ", chain)}]");
            else
                Assert.Equal(arena.OrderBy(x => x), chain.OrderBy(x => x));
        }

        foreach (int o in live) Assert.True(a.Free(o));
        Assert.Single(a.DebugFreeList());
    }

    [Fact]
    public void 주소_순서는_인접한_빈_블록이_리스트에_둘_남는_것을_허용하지_않는다()
    {
        // 병합이 제대로 되면 리스트에서 이웃한 두 항목은 절대 맞닿아 있지 않다.
        // 주소 순서라 이 검사가 O(n) 한 번으로 끝난다.
        var a = new BlockAllocator(4096) { Order = BlockAllocator.FreeOrder.Address };
        var rng = new Random(4242);
        var live = new List<int>();

        for (int step = 0; step < 500; step++)
        {
            if (live.Count > 0 && rng.Next(2) == 0)
            {
                int j = rng.Next(live.Count);
                (live[j], live[^1]) = (live[^1], live[j]);
                a.Free(live[^1]);
                live.RemoveAt(live.Count - 1);
            }
            else if (a.TryAlloc(rng.Next(8, 128), out int o)) live.Add(o);

            var chain = a.DebugFreeList();
            var stats = a.GetStats();

            for (int i = 1; i < chain.Length; i++)
                Assert.True(chain[i - 1] < chain[i],
                    $"{step}번째 스텝에서 순서가 깨졌다: {chain[i - 1]} 다음에 {chain[i]}");

            Assert.Equal(4096, Cap0(stats));
        }

        static int Cap0(AllocatorStats s) => s.UsedBytes + s.FreeBytes + s.OverheadBytes;
    }
}
