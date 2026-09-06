namespace FreeList.Test;

/// <summary>
/// SlotMap 회귀 테스트. 각 테스트는 실제로 겪었던 버그 하나씩을 붙잡고 있다.
/// 마지막 Grow 3건은 아직 미구현이라 실패한다(Red).
/// </summary>
public class FreeListTest
{
    // ── 생성자 ────────────────────────────────────────────

    [Fact]
    public void 생성_직후에는_비어있다()
    {
        var map = new SlotMap(8);

        Assert.Equal(0, map.Count);
    }

    // 경계값: 유일한 칸에 체인 종료(-1)가 제대로 걸리는가
    [Fact]
    public void capacity가_1이어도_동작한다()
    {
        var map = new SlotMap(1);

        int index = map.Add(42);

        Assert.Equal(0, index);
        Assert.Equal(1, map.Count);
        Assert.True(map.TryGet(index, out int value));
        Assert.Equal(42, value);
    }

    // new int[0]은 합법이라 통과하고 그 다음 _next[-1]에서 터진다
    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(-100)]
    public void capacity가_0이하면_예외(int capacity)
    {
        Assert.ThrowsAny<ArgumentException>(() => new SlotMap(capacity));
    }

    // ── Add ───────────────────────────────────────────────

    // 과거 버그: return _count 로 증가 후 값을 돌려줘 한 칸씩 밀렸다
    [Fact]
    public void Add는_실제_삽입_인덱스를_반환한다()
    {
        var map = new SlotMap(8);

        Assert.Equal(0, map.Add(10));
        Assert.Equal(1, map.Add(20));
        Assert.Equal(2, map.Add(30));
    }

    [Fact]
    public void Add하면_Count가_증가한다()
    {
        var map = new SlotMap(8);

        map.Add(10);
        map.Add(20);

        Assert.Equal(2, map.Count);
    }

    // 과거 버그: Array.Fill(_next, -1) 로 초기 체인이 0번 하나뿐이었다
    [Fact]
    public void 초기_체인이_전체_칸을_잇는다()
    {
        const int capacity = 16;
        var map = new SlotMap(capacity);

        for (int i = 0; i < capacity; i++)
            Assert.True(map.Add(i * 100) >= 0, $"{i}번째 Add 실패 — 체인이 중간에 끊겼다");

        Assert.Equal(capacity, map.Count);
    }

    // 체인이 순환하면 같은 칸이 여러 번 나가고 데이터가 조용히 덮어써진다
    [Fact]
    public void 할당된_인덱스는_중복되지_않는다()
    {
        const int capacity = 16;
        var map = new SlotMap(capacity);
        var seen = new HashSet<int>();

        for (int i = 0; i < capacity; i++)
        {
            int index = map.Add(i);
            Assert.True(seen.Add(index), $"인덱스 {index} 중복 할당 — 체인이 순환한다");
        }
    }

    // ── Remove ────────────────────────────────────────────

    [Fact]
    public void 살아있는_칸은_제거된다()
    {
        var map = new SlotMap(8);
        int index = map.Add(10);

        Assert.True(map.Remove(index));
        Assert.Equal(0, map.Count);
    }

    // 이중 반납. 막지 않으면 _next[i] = i 가 되어 체인이 자기를 가리킨다
    [Fact]
    public void 같은_칸을_두_번_제거하면_거부된다()
    {
        var map = new SlotMap(8);
        int index = map.Add(10);

        Assert.True(map.Remove(index));
        Assert.False(map.Remove(index));
    }

    // 이중 반납의 피해는 Remove가 아니라 그 뒤의 Add에서 드러난다
    [Fact]
    public void 이중_제거_후에도_체인이_망가지지_않는다()
    {
        var map = new SlotMap(4);

        int a = map.Add(10);
        map.Add(20);
        map.Add(30);
        map.Add(40);

        map.Remove(a);
        map.Remove(a);      // 거부되어야 함

        int x = map.Add(50);
        int y = map.Add(60);

        Assert.Equal(a, x);
        Assert.NotEqual(x, y);   // 자기 순환이면 같은 칸이 두 번 나온다
    }

    // 과거 버그: _alives[index] 를 먼저 읽어 음수 인덱스에서 예외가 났다
    [Theory]
    [InlineData(-1)]
    [InlineData(-100)]
    [InlineData(8)]
    [InlineData(100000)]
    public void 범위_밖_인덱스는_예외없이_거부된다(int index)
    {
        var map = new SlotMap(8);
        map.Add(10);

        Assert.False(map.Remove(index));
    }

    [Fact]
    public void 할당하지_않은_칸은_제거할_수_없다()
    {
        var map = new SlotMap(8);
        map.Add(10);

        Assert.False(map.Remove(5));
        Assert.Equal(1, map.Count);
    }

    // 검사 전에 _count-- 를 하면 음수까지 내려간다
    [Fact]
    public void 거부된_제거는_Count를_바꾸지_않는다()
    {
        var map = new SlotMap(8);
        int index = map.Add(10);
        map.Remove(index);

        map.Remove(index);
        map.Remove(-1);
        map.Remove(9999);

        Assert.Equal(0, map.Count);
    }

    // ── 재사용 ────────────────────────────────────────────

    // FreeList가 FreeList인 이유. 과거엔 _count를 삽입 위치로 써서 재사용을 못 했다
    [Fact]
    public void 제거한_칸을_다음_Add가_재사용한다()
    {
        var map = new SlotMap(8);

        int a = map.Add(10);
        map.Add(20);
        map.Remove(a);

        Assert.Equal(a, map.Add(30));
    }

    // 반납분은 체인 맨 앞에 꽂히므로 최근 반납분이 먼저 나온다
    [Fact]
    public void 재사용_순서는_LIFO다()
    {
        var map = new SlotMap(8);

        int a = map.Add(10);
        int b = map.Add(20);
        int c = map.Add(30);

        map.Remove(a);
        map.Remove(c);

        Assert.Equal(c, map.Add(40));
        Assert.Equal(a, map.Add(50));
        Assert.Equal(3, map.Add(60));   // 반납분 소진 후엔 한 번도 안 쓴 칸

        Assert.True(map.TryGet(b, out int vb));
        Assert.Equal(20, vb);
    }

    [Fact]
    public void 제거는_다른_칸을_건드리지_않는다()
    {
        var map = new SlotMap(8);

        int a = map.Add(10);
        int b = map.Add(20);
        int c = map.Add(30);

        map.Remove(b);

        Assert.True(map.TryGet(a, out int va));
        Assert.Equal(10, va);
        Assert.True(map.TryGet(c, out int vc));
        Assert.Equal(30, vc);
    }

    // ── TryGet ────────────────────────────────────────────

    [Fact]
    public void 살아있는_칸은_저장한_값을_돌려준다()
    {
        var map = new SlotMap(8);
        int index = map.Add(1234);

        Assert.True(map.TryGet(index, out int value));
        Assert.Equal(1234, value);
    }

    // _items를 지우지 않아도 되는 이유. 판단 기준은 데이터가 아니라 _alives다
    [Fact]
    public void 제거된_칸은_TryGet이_거부한다()
    {
        var map = new SlotMap(8);
        int index = map.Add(1234);
        map.Remove(index);

        Assert.False(map.TryGet(index, out int value));
        Assert.Equal(default(int), value);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(8)]
    [InlineData(99999)]
    public void 범위_밖_인덱스는_TryGet도_거부한다(int index)
    {
        var map = new SlotMap(8);
        map.Add(10);

        Assert.False(map.TryGet(index, out _));
    }

    // Get이 -1로 실패를 알리던 설계의 문제. -1은 유효한 값이라 구분이 안 됐다
    [Fact]
    public void 저장된_마이너스1과_실패는_구분된다()
    {
        var map = new SlotMap(8);
        int index = map.Add(-1);

        Assert.True(map.TryGet(index, out int alive));
        Assert.Equal(-1, alive);

        map.Remove(index);

        Assert.False(map.TryGet(index, out _));
    }

    // ── 종합 ──────────────────────────────────────────────

    /// <summary>
    /// 손으로 짠 시나리오는 생각한 경우만 확인한다. 랜덤으로 두들기며 매 스텝 검사:
    /// Count가 실제 생존 수와 같은가 / 같은 인덱스가 두 번 할당되지 않는가 / 값이 온전한가
    /// </summary>
    [Fact]
    public void 랜덤_5000회_동안_불변식이_유지된다()
    {
        const int capacity = 64;
        var map = new SlotMap(capacity);
        var expected = new Dictionary<int, int>();
        var random = new Random(12345);   // 고정 시드 = 실패 시 그대로 재현

        for (int step = 0; step < 5000; step++)
        {
            bool doAdd = expected.Count == 0 || random.Next(2) == 0;

            if (doAdd)
            {
                int value = random.Next(-1000, 1000);   // 음수를 섞어 -1 함정을 확인
                int index = map.Add(value);

                if (index == -1)
                {
                    Assert.Equal(capacity, expected.Count);
                }
                else
                {
                    Assert.False(expected.ContainsKey(index),
                        $"step {step}: 사용 중인 인덱스 {index}가 또 할당됐다");
                    expected[index] = value;
                }
            }
            else
            {
                int victim = expected.Keys.ElementAt(random.Next(expected.Count));
                Assert.True(map.Remove(victim), $"step {step}: 살아있는 칸 {victim} 제거 실패");
                expected.Remove(victim);
            }

            Assert.Equal(expected.Count, map.Count);
        }

        foreach (var (index, value) in expected)
        {
            Assert.True(map.TryGet(index, out int actual), $"인덱스 {index}가 죽어 있다");
            Assert.Equal(value, actual);
        }
    }

    // ── Grow (미구현 — Red) ───────────────────────────────

    [Fact]
    public void 가득_차면_배열을_늘린다()
    {
        const int capacity = 4;
        var map = new SlotMap(capacity);

        for (int i = 0; i < capacity; i++)
            map.Add(i);

        int index = map.Add(999);

        Assert.True(index >= capacity, "늘어난 구간의 칸을 받아야 한다");
        Assert.Equal(capacity + 1, map.Count);
    }

    // Grow에서 제일 흔한 실수. _items, _next, _alives 셋 다 옮겨야 한다
    [Fact]
    public void Grow_후에도_기존_데이터가_살아있다()
    {
        const int capacity = 4;
        var map = new SlotMap(capacity);

        var indices = new int[capacity];
        for (int i = 0; i < capacity; i++)
            indices[i] = map.Add(i * 10);

        Assert.True(map.Add(999) >= 0);

        for (int i = 0; i < capacity; i++)
        {
            Assert.True(map.TryGet(indices[i], out int value), $"Grow 후 인덱스 {indices[i]}가 죽었다");
            Assert.Equal(i * 10, value);
        }
    }

    // Array.Resize는 새 자리를 0으로 채운다. -1이 아니다 — 새 구간 체인을 직접 이어야 한다
    [Fact]
    public void Grow로_늘어난_칸을_전부_쓸_수_있다()
    {
        const int capacity = 4;
        var map = new SlotMap(capacity);
        var seen = new HashSet<int>();

        for (int i = 0; i < capacity * 4; i++)
        {
            int index = map.Add(i);
            Assert.True(index >= 0, $"{i}번째 Add 실패 — 새 칸이 체인에 안 이어졌다");
            Assert.True(seen.Add(index), $"인덱스 {index} 중복 할당 — 새 구간 체인이 잘못됐다");
        }

        Assert.Equal(capacity * 4, map.Count);
    }
}
