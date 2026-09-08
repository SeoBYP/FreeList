using System.Diagnostics;
using FreeList;

// BlockAllocator 워크로드 측정.
//
// 주 지표는 시간이 아니라 "탐색 횟수"다. 계층형 컴파일도 GC도 Debug 빌드도
// 카운터는 못 바꾸므로 정책 비교가 결정적이고 재현 가능해진다.
// 시간은 참고로만 잰다 — 반드시 Release로.
//
//     dotnet run -c Release --project FreeList
//
// 탐색은 성공/실패를 갈라서 센다. 실패한 탐색은 리스트를 끝까지 훑으므로
// 섞어 세면 first-fit의 수치가 부풀려진다 (작은 객체에서 절반 이상이 실패 몫이었다).

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

Console.WriteLine($"아레나 {Capacity / 1024}KB · 사용률 {FillTarget:P0}까지 채운 뒤 {Ops:N0}회 할당/해제 (시드 고정)");
Console.WriteLine();
Console.WriteLine($"{"시나리오",-18}{"정책",-7}{"탐색/할당",10}{"실패탐색",10}{"실패율",9}"
                + $"{"단편화율",10}{"최대단편",10}{"빈블록",8}{"오버헤드",9}{"ms",9}");
Console.WriteLine(new string('-', 100));

foreach (var (name, lo, hi) in scenarios)
{
    foreach (var policy in new[] { FitPolicy.First, FitPolicy.Best })
    {
        Run(name, lo, hi, policy, warmup: true);    // 계층형 컴파일 승격 유도
        Run(name, lo, hi, policy, warmup: false);
    }
    Console.WriteLine();
}

void Run(string name, int lo, int hi, FitPolicy policy, bool warmup)
{
    var alloc = new BlockAllocator(Capacity) { Policy = policy };
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
    double fragMax = 0;
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

        if (i % 4000 == 0) fragMax = Math.Max(fragMax, Fragmentation(alloc.GetStats()));
    }

    sw.Stop();
    if (warmup) return;

    var s = alloc.GetStats();
    double failRate = (double)fails / (alloc.AllocCount + fails);
    double failShare = alloc.ProbeCount == 0 ? 0 : (double)alloc.FailProbeCount / alloc.ProbeCount;

    Console.WriteLine($"{name,-18}{policy,-7}{alloc.ProbesPerAlloc,10:F2}{failShare,9:P0} "
                    + $"{failRate,8:P1}{Fragmentation(s),10:F3}{fragMax,10:F3}"
                    + $"{s.FreeBlockCount,8}{(double)s.OverheadBytes / Capacity,8:P1}"
                    + $"{sw.Elapsed.TotalMilliseconds,9:F1}");
}

// 빈 공간이 얼마나 잘게 쪼개졌나. 0 = 통짜, 1에 가까울수록 파편
static double Fragmentation(AllocatorStats s)
    => s.FreeBytes == 0 ? 0 : 1.0 - (double)s.LargestFreeBlock / s.FreeBytes;
