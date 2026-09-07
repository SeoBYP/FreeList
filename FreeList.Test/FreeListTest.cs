using System.Runtime.CompilerServices;

namespace FreeList.Test;

/// <summary>
/// SlotMap 회귀 테스트. 각 테스트는 실제로 겪었던 버그 하나씩을 붙잡고 있다.
/// 코드를 고치다 옛 버그가 되살아나면 여기서 잡힌다.
/// </summary>
public class FreeListTest
{
    // ── 생성자 ────────────────────────────────────────────

    [Fact]
    public void 생성_직후에는_비어있다()
    {
        var map = new SlotMap<int>(8);

        Assert.Equal(0, map.Count);
    }

    // 경계값: 유일한 칸에 체인 종료(-1)가 제대로 걸리는가
    [Fact]
    public void capacity가_1이어도_동작한다()
    {
        var map = new SlotMap<int>(1);

        var handle = map.Add(42);

        Assert.Equal(0, handle.Index);
        Assert.Equal(1, map.Count);
        Assert.True(map.TryGet(handle, out int value));
        Assert.Equal(42, value);
    }

    // new int[0]은 합법이라 통과하고 그 다음 _next[-1]에서 터진다
    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(-100)]
    public void capacity가_0이하면_예외(int capacity)
    {
        Assert.ThrowsAny<ArgumentException>(() => new SlotMap<int>(capacity));
    }

    // ── Add ───────────────────────────────────────────────

    // 과거 버그: return _count 로 증가 후 값을 돌려줘 한 칸씩 밀렸다
    [Fact]
    public void Add는_실제_삽입_인덱스를_반환한다()
    {
        var map = new SlotMap<int>(8);

        Assert.Equal(0, map.Add(10).Index);
        Assert.Equal(1, map.Add(20).Index);
        Assert.Equal(2, map.Add(30).Index);
    }

    // 첫 발급 세대는 1. 0은 default(SlotHandle)과 겹치므로 비워둔다
    [Fact]
    public void 첫_발급_세대는_1이다()
    {
        var map = new SlotMap<int>(8);

        Assert.Equal(1, map.Add(10).Generation);
    }

    [Fact]
    public void Add하면_Count가_증가한다()
    {
        var map = new SlotMap<int>(8);

        map.Add(10);
        map.Add(20);

        Assert.Equal(2, map.Count);
    }

    // 과거 버그: Array.Fill(_next, -1) 로 초기 체인이 0번 하나뿐이었다
    [Fact]
    public void 초기_체인이_전체_칸을_잇는다()
    {
        const int capacity = 16;
        var map = new SlotMap<int>(capacity);

        for (int i = 0; i < capacity; i++)
            Assert.True(map.Add(i * 100).Index >= 0, $"{i}번째 Add 실패 — 체인이 중간에 끊겼다");

        Assert.Equal(capacity, map.Count);
    }

    // 체인이 순환하면 같은 칸이 여러 번 나가고 데이터가 조용히 덮어써진다
    [Fact]
    public void 동시에_살아있는_핸들은_중복되지_않는다()
    {
        const int capacity = 16;
        var map = new SlotMap<int>(capacity);
        var seen = new HashSet<SlotHandle>();

        for (int i = 0; i < capacity; i++)
        {
            var handle = map.Add(i);
            Assert.True(seen.Add(handle), $"핸들 #{handle.Index}(g{handle.Generation}) 중복 발급 — 체인이 순환한다");
        }
    }

    // ── Remove ────────────────────────────────────────────

    [Fact]
    public void 살아있는_칸은_제거된다()
    {
        var map = new SlotMap<int>(8);
        var handle = map.Add(10);

        Assert.True(map.Remove(handle));
        Assert.Equal(0, map.Count);
    }

    // 이중 반납. 막지 않으면 _next[i] = i 가 되어 체인이 자기를 가리킨다
    [Fact]
    public void 같은_핸들로_두_번_제거하면_거부된다()
    {
        var map = new SlotMap<int>(8);
        var handle = map.Add(10);

        Assert.True(map.Remove(handle));
        Assert.False(map.Remove(handle));
    }

    // 이중 반납의 피해는 Remove가 아니라 그 뒤의 Add에서 드러난다
    [Fact]
    public void 이중_제거_후에도_체인이_망가지지_않는다()
    {
        var map = new SlotMap<int>(4);

        var a = map.Add(10);
        map.Add(20);
        map.Add(30);
        map.Add(40);

        map.Remove(a);
        map.Remove(a);      // 거부되어야 함

        var x = map.Add(50);
        var y = map.Add(60);

        Assert.Equal(a.Index, x.Index);          // 그 한 칸을 재사용
        Assert.NotEqual(x.Index, y.Index);       // 자기 순환이면 같은 칸이 두 번 나온다
    }

    // 과거 버그: _alives[index] 를 먼저 읽어 음수 인덱스에서 예외가 났다
    [Theory]
    [InlineData(-1)]
    [InlineData(-100)]
    [InlineData(8)]
    [InlineData(100000)]
    public void 범위_밖_인덱스는_예외없이_거부된다(int index)
    {
        var map = new SlotMap<int>(8);
        map.Add(10);

        Assert.False(map.Remove(new SlotHandle(index, 1)));
    }

    [Fact]
    public void 할당하지_않은_칸은_제거할_수_없다()
    {
        var map = new SlotMap<int>(8);
        map.Add(10);                    // 0번만 사용

        Assert.False(map.Remove(new SlotHandle(5, 1)));
        Assert.Equal(1, map.Count);
    }

    // 검사 전에 _count-- 를 하면 음수까지 내려간다
    [Fact]
    public void 거부된_제거는_Count를_바꾸지_않는다()
    {
        var map = new SlotMap<int>(8);
        var handle = map.Add(10);
        map.Remove(handle);

        map.Remove(handle);                        // 이중 반납
        map.Remove(new SlotHandle(-1, 1));         // 음수
        map.Remove(new SlotHandle(9999, 1));       // 범위 초과

        Assert.Equal(0, map.Count);
    }

    // ── 재사용 ────────────────────────────────────────────

    // FreeList가 FreeList인 이유. 과거엔 _count를 삽입 위치로 써서 재사용을 못 했다
    [Fact]
    public void 제거한_칸을_다음_Add가_재사용한다()
    {
        var map = new SlotMap<int>(8);

        var a = map.Add(10);
        map.Add(20);
        map.Remove(a);

        Assert.Equal(a.Index, map.Add(30).Index);
    }

    // 반납분은 체인 맨 앞에 꽂히므로 최근 반납분이 먼저 나온다
    [Fact]
    public void 재사용_순서는_LIFO다()
    {
        var map = new SlotMap<int>(8);

        var a = map.Add(10);
        var b = map.Add(20);
        var c = map.Add(30);

        map.Remove(a);
        map.Remove(c);

        Assert.Equal(c.Index, map.Add(40).Index);
        Assert.Equal(a.Index, map.Add(50).Index);
        Assert.Equal(3, map.Add(60).Index);        // 반납분 소진 후엔 한 번도 안 쓴 칸

        Assert.True(map.TryGet(b, out int vb));
        Assert.Equal(20, vb);
    }

    [Fact]
    public void 제거는_다른_칸을_건드리지_않는다()
    {
        var map = new SlotMap<int>(8);

        var a = map.Add(10);
        var b = map.Add(20);
        var c = map.Add(30);

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
        var map = new SlotMap<int>(8);
        var handle = map.Add(1234);

        Assert.True(map.TryGet(handle, out int value));
        Assert.Equal(1234, value);
    }

    // _items를 지우지 않아도 되는 이유. 판단 기준은 데이터가 아니라 생존 표식과 세대다
    [Fact]
    public void 제거된_칸은_TryGet이_거부한다()
    {
        var map = new SlotMap<int>(8);
        var handle = map.Add(1234);
        map.Remove(handle);

        Assert.False(map.TryGet(handle, out int value));
        Assert.Equal(default(int), value);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(8)]
    [InlineData(99999)]
    public void 범위_밖_인덱스는_TryGet도_거부한다(int index)
    {
        var map = new SlotMap<int>(8);
        map.Add(10);

        Assert.False(map.TryGet(new SlotHandle(index, 1), out _));
    }

    // Get이 -1로 실패를 알리던 설계의 문제. -1은 유효한 값이라 구분이 안 됐다
    [Fact]
    public void 저장된_마이너스1과_실패는_구분된다()
    {
        var map = new SlotMap<int>(8);
        var handle = map.Add(-1);

        Assert.True(map.TryGet(handle, out int alive));
        Assert.Equal(-1, alive);

        map.Remove(handle);

        Assert.False(map.TryGet(handle, out _));
    }

    // ── 세대 핸들 ─────────────────────────────────────────

    // 칸이 재사용되면 옛 식별자가 새 주인을 가리킨다.
    // 예외도 없고 false도 아니라, 죽은 대상을 만지는 줄 모르고 쓰게 된다.
    // 인덱스에 발급 회차(세대)를 붙여야 막을 수 있다.
    [Fact]
    public void 재사용된_칸을_옛_핸들로_읽을_수_없다()
    {
        var map = new SlotMap<string>(8);

        var old = map.Add("고블린");
        map.Remove(old);
        map.Add("드래곤");          // 같은 칸을 재사용

        Assert.False(map.TryGet(old, out _));
    }

    // 읽기보다 위험한 경우 — 옛 핸들로 남의 객체를 지워버린다.
    [Fact]
    public void 재사용된_칸을_옛_핸들로_제거할_수_없다()
    {
        var map = new SlotMap<string>(8);

        var old = map.Add("고블린");
        map.Remove(old);
        var current = map.Add("드래곤");

        Assert.False(map.Remove(old));
        Assert.True(map.TryGet(current, out var alive));   // 드래곤은 무사해야 한다
        Assert.Equal("드래곤", alive);
    }

    // 같은 칸이라도 발급 회차가 다르면 다른 핸들이다
    [Fact]
    public void 재사용된_핸들은_옛_핸들과_다르다()
    {
        var map = new SlotMap<string>(8);

        var old = map.Add("고블린");
        map.Remove(old);
        var reused = map.Add("드래곤");

        Assert.Equal(old.Index, reused.Index);              // 물리적으로 같은 칸
        Assert.NotEqual(old.Generation, reused.Generation); // 논리적으로 다른 주인
        Assert.NotEqual(old, reused);
    }

    // default(SlotHandle)은 {0, 0}. 세대를 1부터 시작한 덕에 자동으로 무효가 된다
    [Fact]
    public void 기본값_핸들은_무효다()
    {
        var map = new SlotMap<string>(8);
        map.Add("A");                                       // 0번 칸을 채워둔다

        Assert.False(map.TryGet(default, out _));
        Assert.False(map.Remove(default));
        Assert.Equal(1, map.Count);
    }

    // 확장이 끼어들어도 세대는 보존돼야 한다. Resize에서 _generations를 빼먹으면 여기서 잡힌다
    [Fact]
    public void 확장을_건너뛴_옛_핸들도_거부된다()
    {
        var map = new SlotMap<string>(2);

        var old = map.Add("고블린");
        map.Add("오크");
        map.Remove(old);
        map.Add("드래곤");          // old 자리 재사용
        map.Add("리치");            // 가득 → 확장

        Assert.False(map.TryGet(old, out _));
        Assert.False(map.Remove(old));
    }

    // ── 확장 ──────────────────────────────────────────────

    [Fact]
    public void 가득_차면_배열을_늘린다()
    {
        const int capacity = 4;
        var map = new SlotMap<int>(capacity);

        for (int i = 0; i < capacity; i++)
            map.Add(i);

        var handle = map.Add(999);

        Assert.True(handle.Index >= capacity, "늘어난 구간의 칸을 받아야 한다");
        Assert.Equal(capacity + 1, map.Count);
    }

    // 확장에서 제일 흔한 실수. _items, _next, _alives, _generations 네 배열을 모두 옮겨야 한다
    [Fact]
    public void 확장_후에도_기존_데이터가_살아있다()
    {
        const int capacity = 4;
        var map = new SlotMap<int>(capacity);

        var handles = new SlotHandle[capacity];
        for (int i = 0; i < capacity; i++)
            handles[i] = map.Add(i * 10);

        map.Add(999);   // 확장 발생

        for (int i = 0; i < capacity; i++)
        {
            Assert.True(map.TryGet(handles[i], out int value), $"확장 후 핸들 #{handles[i].Index}가 죽었다");
            Assert.Equal(i * 10, value);
        }
    }

    // 새로 만든 배열은 0으로 채워진다. -1이 아니므로 새 구간 체인을 직접 이어야 한다
    [Fact]
    public void 확장으로_늘어난_칸을_전부_쓸_수_있다()
    {
        const int capacity = 4;
        var map = new SlotMap<int>(capacity);
        var seen = new HashSet<int>();

        for (int i = 0; i < capacity * 4; i++)
        {
            var handle = map.Add(i);
            Assert.True(handle.Index >= 0, $"{i}번째 Add 실패 — 새 칸이 체인에 안 이어졌다");
            Assert.True(seen.Add(handle.Index), $"인덱스 {handle.Index} 중복 할당 — 새 구간 체인이 잘못됐다");
        }

        Assert.Equal(capacity * 4, map.Count);
    }

    // ── 참조 타입 누수 ───────────────────────────────────

    // Remove가 참조를 놨는지는 반환값으로 못 본다. 그래서 GC로 간접 확인한다.
    // 아무도 안 붙잡고 있어야 수거되므로, 수거됐다 == SlotMap이 놨다.
    // WeakReference는 가리키되 GC 생존 판정에는 안 세는 참조 — 붙잡지 않고 관찰한다.
    // SlotMap.Remove의 IsReferenceOrContainsReferences 블록을 주석 처리하면 실패해야 한다.
    [Fact]
    public void 참조_타입을_제거하면_GC가_수거한다()
    {
        var map = new SlotMap<byte[]>(8);
        var (handle, weak) = CreateWeakReference(map);

        map.Remove(handle);

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        Assert.False(weak.IsAlive);
    }

    // ── 종합 ──────────────────────────────────────────────

    /// <summary>
    /// 손으로 짠 시나리오는 생각한 경우만 확인한다. 랜덤으로 두들기며 매 스텝 검사:
    /// Count가 실제 생존 수와 같은가 / 살아있는 핸들이 또 발급되지 않는가 / 값이 온전한가
    /// </summary>
    [Fact]
    public void 랜덤_5000회_동안_불변식이_유지된다()
    {
        var map = new SlotMap<int>(64);
        var expected = new Dictionary<SlotHandle, int>();
        var random = new Random(12345);   // 고정 시드 = 실패 시 그대로 재현

        for (int step = 0; step < 5000; step++)
        {
            bool doAdd = expected.Count == 0 || random.Next(2) == 0;

            if (doAdd)
            {
                int value = random.Next(-1000, 1000);   // 음수를 섞어 -1 함정을 확인
                var handle = map.Add(value);

                Assert.False(expected.ContainsKey(handle),
                    $"step {step}: 살아있는 핸들 #{handle.Index}(g{handle.Generation})이 또 발급됐다");
                expected[handle] = value;
            }
            else
            {
                var victim = expected.Keys.ElementAt(random.Next(expected.Count));
                Assert.True(map.Remove(victim), $"step {step}: 살아있는 핸들 #{victim.Index} 제거 실패");
                expected.Remove(victim);
            }

            Assert.Equal(expected.Count, map.Count);
        }

        foreach (var (handle, value) in expected)
        {
            Assert.True(map.TryGet(handle, out int actual), $"핸들 #{handle.Index}가 죽어 있다");
            Assert.Equal(value, actual);
        }
    }

    /// <summary>
    /// 세대 검사에 구멍이 있으면 여기서 잡힌다.
    /// 죽은 핸들을 모아두고 매 스텝 다시 찔러본다 — 하나라도 통과하면 실패.
    /// </summary>
    [Fact]
    public void 죽은_핸들은_언제나_거부된다()
    {
        var map = new SlotMap<int>(8);
        var live = new List<SlotHandle>();
        var dead = new List<SlotHandle>();
        var random = new Random(7);

        for (int step = 0; step < 2000; step++)
        {
            if (live.Count == 0 || random.Next(2) == 0)
            {
                live.Add(map.Add(random.Next(1000)));
            }
            else
            {
                int i = random.Next(live.Count);
                var victim = live[i];
                live.RemoveAt(i);
                Assert.True(map.Remove(victim), $"step {step}: 살아있는 핸들 제거 실패");
                dead.Add(victim);
            }

            // 최근에 죽은 핸들 20개를 매번 다시 찔러본다
            for (int k = Math.Max(0, dead.Count - 20); k < dead.Count; k++)
            {
                var stale = dead[k];
                Assert.False(map.TryGet(stale, out _),
                    $"step {step}: 죽은 핸들 #{stale.Index}(g{stale.Generation})로 읽혔다");
                Assert.False(map.Remove(stale),
                    $"step {step}: 죽은 핸들 #{stale.Index}(g{stale.Generation})로 제거됐다");
            }

            Assert.Equal(live.Count, map.Count);
        }
    }

    // 별도 메서드인 이유: 반환되면 지역 변수 data가 사라져 강한 참조가 SlotMap 것만 남는다.
    // 테스트 메서드 안에서 만들면 그 변수가 계속 붙잡아 Remove를 해도 수거되지 않는다.
    // 객체 자체를 반환하면 안 된다 — 받는 쪽 변수가 다시 붙잡는다.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static (SlotHandle handle, WeakReference weak) CreateWeakReference(SlotMap<byte[]> map)
    {
        var data = new byte[1024];
        var weak = new WeakReference(data);
        var handle = map.Add(data);
        return (handle, weak);
    }
}
