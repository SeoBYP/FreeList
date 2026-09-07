using System.Diagnostics;
using FreeList;

const int Capacity = 1_000_000;
const int Warmup   = 30;     // ← 3으로 한 번, 30으로 한 번 돌려서 비교
const int Runs     = 7;

long sink = 0;

// 중앙값과 함께 각 회차 시간을 "측정 순서 그대로" 돌려준다.
// 정렬해버리면 워밍업 부족(앞쪽만 느림)을 볼 수 없다.
(double Median, double[] Runs) Measure(Func<long> pass)
{
    for (int w = 0; w < Warmup; w++)
        sink += pass();

    var runs = new double[Runs];
    var sw = new Stopwatch();

    for (int r = 0; r < Runs; r++)
    {
        sw.Restart();
        long local = pass();
        sw.Stop();

        runs[r] = sw.Elapsed.TotalMilliseconds;
        sink += local;
    }

    var sorted = (double[])runs.Clone();   // 원본 순서 보존
    Array.Sort(sorted);
    return (sorted[Runs / 2], runs);
}

void Row(string label, int count, (double Median, double[] Runs) m)
{
    Console.WriteLine($"{label,-16} {count,10:N0} {m.Median,10:F3} {m.Median * 1e6 / count,12:F2}");
    Console.WriteLine($"{"",-16} {"회차별:",10}  {string.Join("  ", m.Runs.Select(t => t.ToString("F3")))}");
}

Console.WriteLine($"용량 {Capacity:N0} / 측정 {Runs}회 중앙값 / 워밍업 {Warmup}회");
Console.WriteLine();
Console.WriteLine($"{"케이스",-16} {"Count",10} {"시간(ms)",10} {"원소당(ns)",12}");
Console.WriteLine(new string('-', 60));

// ───────── 기준선 ─────────
{
    var raw = new int[Capacity];
    for (int i = 0; i < Capacity; i++)
        raw[i] = i;

    Row("기준선 int[]", Capacity, Measure(() =>
    {
        long s = 0;
        foreach (int v in raw) s += v;
        return s;
    }));
}

Console.WriteLine();

// ───────── SlotMap: 희소한 것부터 ─────────
double[] densities = { 0.001, 0.01, 0.1, 0.5, 1.0 };

foreach (double density in densities)
{
    var map = new SlotMap<int>(Capacity);
    var handles = new SlotHandle[Capacity];

    for (int i = 0; i < Capacity; i++)
        handles[i] = map.Add(i);

    int step = (int)Math.Round(1.0 / density);
    for (int i = 0; i < Capacity; i++)
        if (i % step != 0)
            map.Remove(handles[i]);

    handles = null!;              // 8MB 회수 — 측정 중 GC가 끼어들 여지를 줄인다
    GC.Collect();
    GC.WaitForPendingFinalizers();
    GC.Collect();

    Row($"핸들+값 {density:P1}", map.Count, Measure(() =>
    {
        long s = 0;
        foreach (var (_, value) in map) s += value;
        return s;
    }));
}

Console.WriteLine();
Console.WriteLine($"(sink = {sink})");