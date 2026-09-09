using System.Diagnostics;
using FreeList;

// BlockAllocator 워크로드 측정.
//
// 축이 셋이다.  FreeOrder(리스트 순서) x FitPolicy(고르는 방법) x 워크로드
//
// 주 지표는 시간이 아니라 "탐색 횟수"다. 계층형 컴파일도 GC도 Debug 빌드도
// 카운터는 못 바꾸므로 정책 비교가 결정적이고 재현 가능해진다.
//
//     dotnet run -c Release --project FreeList
//
// 탐색을 세 가지로 나눠 본다.
//
//   탐색/할당   FindFit이 성공한 할당 한 번에 들여다본 빈 블록 수.
//               실패한 탐색은 리스트를 끝까지 훑으므로 섞어 세면 first-fit이 부풀려진다.
//   삽입/해제   PushFree가 자리를 찾느라 걸은 노드 수. 주소 순서에서만 0이 아니다.
//               이걸 안 세면 "주소 순서가 공짜로 이겼다"는 오염된 결론이 나온다.
//   총/연산     (전체 탐색 + 삽입 탐색) / (할당 + 해제).
//               분자와 분모가 같은 사건 집합을 센다. 정직한 총비용은 이 값이다.
//
// ns/탐색 열은 자기 진단용이다. 물리적으로는 모든 행이 비슷해야 한다.
// 특정 행만 10배씩 튀면 그건 자료구조가 아니라 측정 환경이 흔들린 것이다.
// (1차 측정에서 설정별 워밍업만으로는 부족해 앞 세 행이 10~20배 부풀었다.
//  Tier 1 승격이 백그라운드 컴파일이라 시간이 걸리기 때문이다.
//  그래서 지금은 모든 설정을 먼저 한 바퀴 돌린 뒤에 측정한다.)

#if DEBUG
Console.WriteLine("경고: Debug 빌드다. 탐색 횟수는 유효하지만 ms는 믿지 마라.");
Console.WriteLine("      dotnet run -c Release 로 다시 돌려라.");
Console.WriteLine();
#endif

const int Capacity = 256 * 1024;
const int Ops = 20_000;
const double FillTarget = 0.70;

var scenarios = new (string Name, int Lo, int Hi)[]
{
    ("작은 것    8~64B", 8, 64),
    ("섞임      8~512B", 8, 512),
    ("큰 것  256~2048B", 256, 2048),
};

var orders = new[] { BlockAllocator.FreeOrder.Lifo, BlockAllocator.FreeOrder.Address };
var policies = new[] { BlockAllocator.FitPolicy.First, BlockAllocator.FitPolicy.Best };

var configs =
    (from s in scenarios
     from o in orders
     from p in policies
     select (s.Name, s.Lo, s.Hi, Order: o, Policy: p)).ToArray();

// ── 0단계: 전체 워밍업 ────────────────────────────────────────
// 설정마다 바로 앞에서 한 번 돌리는 것으로는 부족했다. Tier 1 승격은
// 호출 횟수로 예약되고 백그라운드 스레드가 실제 컴파일을 하므로,
// 첫 몇 설정은 승격이 끝나기 전에 측정이 지나가버린다.
// 열두 설정을 전부 두 바퀴 돌린 뒤 잠깐 쉬어 컴파일이 끝나기를 기다린다.
Console.Write("워밍업 중");
for (int pass = 0; pass < 2; pass++)
{
    foreach (var c in configs) Run(c.Name, c.Lo, c.Hi, c.Order, c.Policy);
    Console.Write(".");
}
Thread.Sleep(300);   // 백그라운드 컴파일이 끝나기를 기다린다
Console.WriteLine(" 완료");
Console.WriteLine();

// ── 1단계: 측정 ──────────────────────────────────────────────
var results = new Dictionary<(string, BlockAllocator.FreeOrder, BlockAllocator.FitPolicy), Row>();

Console.WriteLine($"아레나 {Capacity / 1024}KB · 사용률 {FillTarget:P0}까지 채운 뒤 {Ops:N0}회 할당/해제 (시드 고정)");
Console.WriteLine();
Console.WriteLine($"{"시나리오",-18}{"순서",-9}{"정책",-7}{"탐색/할당",10}{"삽입/해제",10}{"총/연산",9}"
                + $"{"실패율",8}{"단편화",8}{"빈블록",8}{"오버헤드",9}{"ms",8}{"ns/탐색",9}");
Console.WriteLine(new string('-', 121));

string? lastName = null;
foreach (var c in configs)
{
    if (lastName != null && c.Name != lastName) Console.WriteLine();
    lastName = c.Name;

    var row = Run(c.Name, c.Lo, c.Hi, c.Order, c.Policy);
    results[(c.Name, c.Order, c.Policy)] = row;

    Console.WriteLine($"{c.Name,-18}{c.Order,-9}{c.Policy,-7}{row.Find,10:F2}{row.Insert,10:F2}"
                    + $"{row.Total,9:F2}{row.FailRate,8:P1}{row.Frag,8:F3}"
                    + $"{row.FreeBlocks,8}{row.Overhead,9:P1}{row.Ms,8:F1}{row.NsPerProbe,9:F1}");
}

// ── 순서를 바꾼 것이 무엇을 얻고 무엇을 잃었나 ─────────────────
Console.WriteLine();
Console.WriteLine();
Console.WriteLine("=== Lifo → Address 로 바꿨을 때 ===");
Console.WriteLine($"{"시나리오",-18}{"정책",-7}{"단편화",20}{"빈블록",18}{"총탐색/연산",20}");
Console.WriteLine(new string('-', 84));

foreach (var (name, _, _) in scenarios)
{
    foreach (var policy in policies)
    {
        var l = results[(name, BlockAllocator.FreeOrder.Lifo, policy)];
        var d = results[(name, BlockAllocator.FreeOrder.Address, policy)];
        Console.WriteLine($"{name,-18}{policy,-7}"
                        + $"{l.Frag,9:F3} → {d.Frag,-9:F3}"
                        + $"{l.FreeBlocks,8} → {d.FreeBlocks,-8}"
                        + $"{l.Total,9:F2} → {d.Total,-9:F2}");
    }
}

// ── ns/탐색이 흔들리면 시간 열을 믿을 수 없다 ──────────────────
var ns = results.Values.Select(r => r.NsPerProbe).Where(v => v > 0).ToArray();
if (ns.Length > 0 && ns.Max() / ns.Min() > 4)
{
    Console.WriteLine();
    Console.WriteLine($"주의: ns/탐색이 {ns.Min():F1} ~ {ns.Max():F1}로 {ns.Max() / ns.Min():F1}배 흔들린다.");
    Console.WriteLine("      측정 환경이 아직 안정되지 않았다는 뜻이므로 ms 열은 비교에 쓰지 마라.");
    Console.WriteLine("      탐색 횟수 쪽은 카운터라 이 문제의 영향을 받지 않는다.");
}

Row Run(string name, int lo, int hi,
        BlockAllocator.FreeOrder order, BlockAllocator.FitPolicy policy)
{
    var alloc = new BlockAllocator(Capacity) { Order = order, Policy = policy };
    var rng = new Random(12345);
    var live = new List<int>();
    long used = 0;

    // 1단계: 목표 사용률까지 채운다.
    // GetStats()는 아레나 전체를 걷으므로 매번 부르면 O(n^2)가 된다. 증분으로 센다.
    while ((double)used / Capacity < FillTarget)
    {
        if (!alloc.TryAlloc(rng.Next(lo, hi + 1), out int off)) break;
        used += alloc.AsSpan(off).Length;
        live.Add(off);
    }

    alloc.ResetCounters();

    // 2단계: 정상 상태에서 할당/해제를 섞어 돌린다
    long fails = 0;
    var sw = Stopwatch.StartNew();

    for (int i = 0; i < Ops; i++)
    {
        if (live.Count > 0 && rng.Next(2) == 0)
        {
            int j = rng.Next(live.Count);
            (live[j], live[^1]) = (live[^1], live[j]);   // 끝과 바꿔치기 후 pop = O(1) 제거
            alloc.Free(live[^1]);
            live.RemoveAt(live.Count - 1);
        }
        else
        {
            if (alloc.TryAlloc(rng.Next(lo, hi + 1), out int off)) live.Add(off);
            else fails++;
        }
    }

    sw.Stop();

    var s = alloc.GetStats();
    long ops = alloc.AllocCount + alloc.FreeCount;
    long probes = alloc.ProbeCount + alloc.InsertProbeCount;

    return new Row
    {
        Find = alloc.ProbesPerAlloc,
        Insert = alloc.InsertProbesPerFree,
        Total = ops == 0 ? 0 : (double)probes / ops,
        FailRate = (double)fails / (alloc.AllocCount + fails),
        Frag = s.FreeBytes == 0 ? 0 : 1.0 - (double)s.LargestFreeBlock / s.FreeBytes,
        FreeBlocks = s.FreeBlockCount,
        Overhead = (double)s.OverheadBytes / Capacity,
        Ms = sw.Elapsed.TotalMilliseconds,
        NsPerProbe = probes == 0 ? 0 : sw.Elapsed.TotalMilliseconds * 1_000_000 / probes,
    };
}

struct Row
{
    public double Find;        // 성공한 할당 한 번당 FindFit 탐색
    public double Insert;      // 해제 한 번당 PushFree 탐색
    public double Total;       // (전체 탐색 + 삽입 탐색) / (할당 + 해제)
    public double FailRate;
    public double Frag;        // 1 - 최대빈블록 / 전체빈공간.  0 = 통짜
    public int FreeBlocks;
    public double Overhead;
    public double Ms;
    public double NsPerProbe;  // 자기 진단용. 행마다 크게 다르면 ms를 믿을 수 없다
}
