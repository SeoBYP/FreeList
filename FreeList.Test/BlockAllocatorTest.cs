namespace FreeList.Test;

/// <summary>
/// BlockAllocator 회귀 테스트.
/// 불변식: UsedBytes + FreeBytes + OverheadBytes == Capacity
/// 1단계(헤더 + 최초적합 + 분할)까지는 "병합" 그룹만 Red다.
/// </summary>
public class BlockAllocatorTest
{
    // ── 도우미 ────────────────────────────────────────

    private static void Fill(BlockAllocator a, int offset, byte pattern)
    {
        var span = a.AsSpan(offset);
        for (int i = 0; i < span.Length; i++)
            span[i] = pattern;
    }

    private static bool IsFilled(BlockAllocator a, int offset, byte pattern)
    {
        var span = a.AsSpan(offset);
        for (int i = 0; i < span.Length; i++)
            if (span[i] != pattern) return false;
        return true;
    }

    private static List<int> FillArena(BlockAllocator a, int size)
    {
        var offsets = new List<int>();
        while (a.TryAlloc(size, out int offset))
        {
            offsets.Add(offset);
            Assert.True(offsets.Count <= a.Capacity / size,
                $"{a.Capacity}바이트 아레나에서 {size}바이트를 {offsets.Count}번 줬다 — 블록이 겹친다");
        }
        return offsets;
    }

    // ── 생성자 ────────────────────────────────────────

    [Fact]
    public void 생성_직후_아레나는_빈_블록_하나다()
    {
        var a = new BlockAllocator(1024);

        var s = a.GetStats();
        Assert.Equal(0, s.UsedBytes);
        Assert.Equal(1, s.FreeBlockCount);
        Assert.True(s.LargestFreeBlock > 0, "통짜 빈 블록 하나로 시작해야 한다");
    }

    [Fact]
    public void 모든_바이트는_사용_빈공간_오버헤드_중_하나다()
    {
        const int capacity = 1024;
        var a = new BlockAllocator(capacity);

        var s = a.GetStats();
        Assert.Equal(capacity, s.UsedBytes + s.FreeBytes + s.OverheadBytes);
        Assert.True(s.OverheadBytes > 0, "헤더가 0바이트일 수는 없다");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(-1024)]
    public void capacity가_0이하면_예외(int capacity)
    {
        Assert.ThrowsAny<ArgumentException>(() => new BlockAllocator(capacity));
    }

    [Fact]
    public void capacity가_너무_작으면_예외()
    {
        Assert.ThrowsAny<ArgumentException>(() => new BlockAllocator(4));
    }

    // ── TryAlloc ──────────────────────────────────────

    [Fact]
    public void 할당하면_범위_안의_오프셋을_준다()
    {
        var a = new BlockAllocator(1024);

        Assert.True(a.TryAlloc(64, out int offset));
        Assert.InRange(offset, 0, a.Capacity - 1);
    }

    // 정렬 때문에 더 줄 수는 있어도 덜 주면 안 된다
    [Fact]
    public void 요청한_크기_이상을_돌려준다()
    {
        var a = new BlockAllocator(1024);

        Assert.True(a.TryAlloc(37, out int offset));

        int actual = a.AsSpan(offset).Length;
        Assert.True(actual >= 37, $"37바이트를 요청했는데 {actual}바이트만 줬다");
    }

    [Fact]
    public void 할당하면_UsedBytes가_늘어난다()
    {
        var a = new BlockAllocator(1024);

        Assert.True(a.TryAlloc(64, out _));

        Assert.True(a.GetStats().UsedBytes >= 64);
    }

    [Fact]
    public void 용량보다_큰_요청은_거부된다()
    {
        var a = new BlockAllocator(1024);

        Assert.False(a.TryAlloc(2048, out _));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(-1000)]
    public void 크기가_0이하인_요청은_거부된다(int size)
    {
        var a = new BlockAllocator(1024);

        Assert.False(a.TryAlloc(size, out _));
    }

    [Fact]
    public void 거부된_할당은_통계를_바꾸지_않는다()
    {
        var a = new BlockAllocator(1024);
        var before = a.GetStats();

        a.TryAlloc(2048, out _);
        a.TryAlloc(0, out _);

        var after = a.GetStats();
        Assert.Equal(before.UsedBytes, after.UsedBytes);
        Assert.Equal(before.FreeBlockCount, after.FreeBlockCount);
    }

    // 오프셋 비교보다 실제 바이트로 확인하는 편이 확실하다
    [Fact]
    public void 연속_할당한_블록들은_겹치지_않는다()
    {
        var a = new BlockAllocator(1024);
        var offsets = new List<int>();

        for (byte i = 1; i <= 5; i++)
        {
            Assert.True(a.TryAlloc(32, out int offset), $"{i}번째 할당 실패");
            offsets.Add(offset);
            Fill(a, offset, i);
        }

        for (byte i = 1; i <= 5; i++)
            Assert.True(IsFilled(a, offsets[i - 1], i),
                $"{i}번 블록이 오염됐다 — 앞뒤 블록과 영역이 겹친다");
    }

    // ── Free ──────────────────────────────────────────

    [Fact]
    public void 할당한_블록은_해제된다()
    {
        var a = new BlockAllocator(1024);
        a.TryAlloc(64, out int offset);

        Assert.True(a.Free(offset));
        Assert.Equal(0, a.GetStats().UsedBytes);
    }

    [Fact]
    public void 해제해도_아레나_총량은_그대로다()
    {
        const int capacity = 1024;
        var a = new BlockAllocator(capacity);
        a.TryAlloc(100, out int x);
        a.TryAlloc(100, out _);

        a.Free(x);

        var s = a.GetStats();
        Assert.Equal(capacity, s.UsedBytes + s.FreeBytes + s.OverheadBytes);
    }

    // 2단계(병합)를 넣으면 이 테스트가 다시 깨질 수 있다.
    // 해제된 블록이 앞 블록에 흡수되면 offset 앞에 그 블록의 헤더가 없어지기 때문이다.
    [Fact]
    public void 같은_블록을_두_번_해제하면_거부된다()
    {
        var a = new BlockAllocator(1024);
        a.TryAlloc(64, out int offset);

        Assert.True(a.Free(offset));
        Assert.False(a.Free(offset));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(-1000)]
    [InlineData(1024)]
    [InlineData(999999)]
    public void 범위_밖_오프셋은_예외없이_거부된다(int offset)
    {
        var a = new BlockAllocator(1024);
        a.TryAlloc(64, out _);

        Assert.False(a.Free(offset));
    }

    [Fact]
    public void 거부된_해제는_통계를_바꾸지_않는다()
    {
        var a = new BlockAllocator(1024);
        a.TryAlloc(64, out _);
        int used = a.GetStats().UsedBytes;

        a.Free(-1);
        a.Free(99999);

        Assert.Equal(used, a.GetStats().UsedBytes);
    }

    // ── 재사용 ────────────────────────────────────────

    [Fact]
    public void 해제한_블록을_다음_할당이_재사용한다()
    {
        var a = new BlockAllocator(1024);
        a.TryAlloc(64, out int first);
        a.Free(first);

        Assert.True(a.TryAlloc(64, out int second));
        Assert.Equal(first, second);
    }

    [Fact]
    public void 전부_해제하면_사용량이_0이_된다()
    {
        var a = new BlockAllocator(1024);
        var all = FillArena(a, 64);

        foreach (int offset in all)
            Assert.True(a.Free(offset), $"오프셋 {offset} 해제 실패");

        Assert.Equal(0, a.GetStats().UsedBytes);
    }

    // 누수가 있으면 반복할수록 쓸 수 있는 공간이 줄어 언젠가 실패한다
    [Fact]
    public void 할당_해제_1000회_반복이_공간을_잠식하지_않는다()
    {
        var a = new BlockAllocator(1024);

        for (int i = 0; i < 1000; i++)
        {
            Assert.True(a.TryAlloc(200, out int offset), $"{i}회차 할당 실패 — 공간이 잠식됐다");
            Assert.True(a.Free(offset), $"{i}회차 해제 실패");
        }
    }

    // ── 분할 ──────────────────────────────────────────

    // 분할이 없으면 통짜 빈 블록을 통째로 넘겨서 두 번째 할당부터 전부 실패한다
    [Fact]
    public void 작은_요청이_큰_빈_블록을_통째로_먹지_않는다()
    {
        const int capacity = 1024;
        var a = new BlockAllocator(capacity);

        Assert.True(a.TryAlloc(16, out _));

        int largest = a.GetStats().LargestFreeBlock;
        Assert.True(largest > capacity / 2,
            $"16바이트를 줬는데 남은 최대 빈 블록이 {largest}바이트뿐이다 — 분할하지 않았다");
    }

    [Fact]
    public void 분할하고_남은_공간도_할당할_수_있다()
    {
        var a = new BlockAllocator(1024);
        a.TryAlloc(16, out _);

        Assert.True(a.TryAlloc(256, out _), "분할하고 남은 자리를 쓰지 못한다");
    }

    // 쪼개면 아무도 못 쓰는 조각이 생기는 경우
    [Fact]
    public void 남는_자리가_너무_작으면_쪼개지_않는다()
    {
        var a = new BlockAllocator(1024);
        int usable = a.GetStats().LargestFreeBlock;

        Assert.True(a.TryAlloc(usable - 2, out _));

        var s = a.GetStats();
        Assert.Equal(0, s.FreeBlockCount);
        Assert.Equal(a.Capacity, s.UsedBytes + s.FreeBytes + s.OverheadBytes);
    }

    // ── 병합 (2단계. 그전까지 Red) ────────────────────

    // 900바이트가 비어 있어도 300짜리 조각 셋으로 흩어져 있으면 실패한다 = 외부 단편화
    [Fact]
    public void 해제한_조각들을_합쳐야_큰_요청이_성공한다()
    {
        var a = new BlockAllocator(1024);
        Assert.True(a.TryAlloc(300, out int x));
        Assert.True(a.TryAlloc(300, out int y));
        Assert.True(a.TryAlloc(300, out int z));

        a.Free(x);
        a.Free(y);
        a.Free(z);

        Assert.True(a.TryAlloc(900, out _), "병합이 없다");
    }

    // 헤더만으로 가능하다
    [Fact]
    public void 뒤_블록과_병합된다()
    {
        var a = new BlockAllocator(1024);
        var all = FillArena(a, 64);
        Assert.True(all.Count >= 4);
        int tail = a.GetStats().FreeBlockCount;   // 꼬리에 자투리가 남을 수 있다

        a.Free(all[1]);
        Assert.Equal(tail + 1, a.GetStats().FreeBlockCount);

        a.Free(all[0]);

        Assert.Equal(tail + 1, a.GetStats().FreeBlockCount);
    }

    // 앞 블록의 크기를 알아야 하므로 푸터가 필요하다
    [Fact]
    public void 앞_블록과_병합된다()
    {
        var a = new BlockAllocator(1024);
        var all = FillArena(a, 64);
        Assert.True(all.Count >= 4);
        int tail = a.GetStats().FreeBlockCount;

        a.Free(all[0]);
        Assert.Equal(tail + 1, a.GetStats().FreeBlockCount);

        a.Free(all[1]);

        Assert.Equal(tail + 1, a.GetStats().FreeBlockCount);
    }

    [Fact]
    public void 양쪽과_동시에_병합된다()
    {
        var a = new BlockAllocator(1024);
        var all = FillArena(a, 64);
        Assert.True(all.Count >= 5);
        int tail = a.GetStats().FreeBlockCount;

        a.Free(all[0]);
        a.Free(all[2]);
        Assert.Equal(tail + 2, a.GetStats().FreeBlockCount);

        a.Free(all[1]);

        Assert.Equal(tail + 1, a.GetStats().FreeBlockCount);
    }

    [Fact]
    public void 전부_해제하면_처음의_통짜_상태로_돌아간다()
    {
        var a = new BlockAllocator(1024);
        int original = a.GetStats().LargestFreeBlock;

        var all = FillArena(a, 64);
        foreach (int offset in all)
            a.Free(offset);

        var s = a.GetStats();
        Assert.Equal(1, s.FreeBlockCount);
        Assert.Equal(original, s.LargestFreeBlock);
    }

    // ── 데이터 무결성 ─────────────────────────────────

    [Fact]
    public void 쓴_데이터가_유지된다()
    {
        var a = new BlockAllocator(1024);
        a.TryAlloc(64, out int offset);

        Fill(a, offset, 0xAB);

        Assert.True(IsFilled(a, offset, 0xAB));
    }

    [Fact]
    public void 이웃_블록을_써도_내_블록은_멀쩡하다()
    {
        var a = new BlockAllocator(1024);
        var all = FillArena(a, 48);
        Assert.True(all.Count >= 3);

        for (int i = 0; i < all.Count; i++)
            Fill(a, all[i], (byte)(i + 1));

        for (int i = 0; i < all.Count; i++)
            Assert.True(IsFilled(a, all[i], (byte)(i + 1)),
                $"{i}번 블록이 오염됐다 — 헤더가 payload를 침범한다");
    }

    [Fact]
    public void 이웃을_해제하고_다시_할당해도_내_데이터는_그대로다()
    {
        var a = new BlockAllocator(1024);
        a.TryAlloc(64, out int x);
        a.TryAlloc(64, out int keep);
        a.TryAlloc(64, out int z);

        Fill(a, keep, 0x5A);

        a.Free(x);
        a.Free(z);
        a.TryAlloc(64, out _);
        a.TryAlloc(64, out _);

        Assert.True(IsFilled(a, keep, 0x5A), "살아있는 이웃이 덮어써졌다");
    }

    // ── 단편화 ────────────────────────────────────────

    // 떨어져 있는 구멍은 병합해도 합쳐지지 않는다
    [Fact]
    public void 걸러서_해제하면_외부_단편화가_남는다()
    {
        var a = new BlockAllocator(2048);
        var all = FillArena(a, 64);
        Assert.True(all.Count >= 8);

        for (int i = 0; i < all.Count; i += 2)
            a.Free(all[i]);

        var s = a.GetStats();
        Assert.True(s.FreeBytes > 500, $"빈 바이트가 {s.FreeBytes}밖에 안 된다");
        Assert.True(s.LargestFreeBlock < s.FreeBytes / 2,
            $"빈 바이트 {s.FreeBytes} 중 최대 빈 블록이 {s.LargestFreeBlock}");
    }

    // ── 종합 ──────────────────────────────────────────

    [Fact]
    public void 랜덤_2000회_동안_불변식이_유지된다()
    {
        const int capacity = 4096;
        var a = new BlockAllocator(capacity);
        var live = new Dictionary<int, byte>();
        var random = new Random(12345);   // 고정 시드 = 실패 시 그대로 재현
        byte pattern = 0;

        for (int step = 0; step < 2000; step++)
        {
            bool doAlloc = live.Count == 0 || random.Next(2) == 0;

            if (doAlloc)
            {
                int size = random.Next(1, 200);
                if (a.TryAlloc(size, out int offset))
                {
                    Assert.False(live.ContainsKey(offset),
                        $"step {step}: 사용 중인 오프셋 {offset}이 또 할당됐다");

                    pattern = (byte)(pattern == 255 ? 1 : pattern + 1);
                    Fill(a, offset, pattern);
                    live[offset] = pattern;
                }
            }
            else
            {
                int victim = live.Keys.ElementAt(random.Next(live.Count));
                Assert.True(a.Free(victim), $"step {step}: 살아있는 블록 {victim} 해제 실패");
                live.Remove(victim);
            }

            var s = a.GetStats();
            Assert.Equal(capacity, s.UsedBytes + s.FreeBytes + s.OverheadBytes);

            foreach (var (offset, expected) in live)
                Assert.True(IsFilled(a, offset, expected),
                    $"step {step}: 블록 {offset}의 데이터가 깨졌다");
        }
    }
}
