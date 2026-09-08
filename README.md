# FreeList

**빈 칸을 어떻게 기억할 것인가.**

객체를 미리 잡아두고 재사용하는 구조에서는 "어느 칸이 비어 있는가"를 어딘가 적어둬야 한다. 그 답을 두 가지로 만들고, 실제 라이브러리 구현과 비교하고, 성능을 측정한 기록이다.

```
SlotMap<T>        고정 크기 칸 + 세대 핸들
BlockAllocator    가변 크기 블록 + 경계 태그 + 병합 + 명시적 free 리스트
```

`.NET 10` · 테스트 74개 · 단일 스레드 · 학습용

---

## 왜 이걸 만들었나

Unity DI 라이브러리 **VContainer**의 내부를 읽다가 이 코드를 봤다.

```csharp
// VContainer 1.19.0 · Runtime/Unity/PlayerLoopRunner.cs
readonly FreeList<IPlayerLoopItem> runners = new FreeList<IPlayerLoopItem>(16);

for (var i = 0; i < span.Length; i++)
{
    var item = span[i];
    if (item != null)                    // null을 건너뛴다
    {
        if (!item.MoveNext())
            runners.RemoveAt(i);         // 순회 도중 제거
    }
}
```

`List<T>`면 될 것 같은데 왜 `FreeList<T>`일까. `null` 검사는 왜 있을까.

궁금해서 직접 만들어봤다. 만들고 나서 다시 읽으니 **같은 이름인데 다른 물건**이었다.

---

## 빈 칸 목록은 어디에 두어야 하나

가장 단순한 답은 `Stack<int>`에 빈 인덱스를 쌓아두는 것이다.

```
_items   [ A ][   ][ C ][   ][ E ]
_free    Stack<int> { 1, 3 }        ← 배열이 하나 더 필요하다
```

동작은 한다. 그런데 원소 100만 개짜리 배열이면 최악의 경우 **100만 개짜리 스택이 하나 더** 생긴다. 메모리를 아끼려고 만든 구조가 메모리를 두 배로 쓴다.

FreeList의 발상은 이렇다.

> **빈 칸은 어차피 아무도 안 읽는다. 그 자리에 "다음 빈 칸 번호"를 적어두자.**

```
         칸 0     칸 1     칸 2     칸 3     칸 4
_items  [  A  ] [     ] [  C  ] [     ] [  E  ]
_next   [  -  ] [  3  ] [  -  ] [ -1  ] [  -  ]
                   ↑                ↑
_freeHead = 1 ─────┘                └── -1 = 체인 끝
```

추가 배열 없이 빈 칸 목록이 만들어지고, 할당과 반납이 모두 O(1)이 된다. `Add`는 `_freeHead`가 가리키는 칸을 가져가고, `Remove`는 반납한 칸을 체인 **맨 앞**에 꽂는다.

맨 뒤에 붙이려면 끝까지 걸어가야 해서 O(n)이 된다. 그래서 앞에 꽂고, 그 결과 **가장 최근에 반납한 칸이 먼저 재사용된다.**

---

## 재사용된 칸은 어떻게 구분하나

인덱스만 주고받으면 이런 일이 생긴다.

```
i = 3 에 A 를 넣음      →  누군가 3을 들고 다닌다
A 를 제거              →  3번이 비었다
B 를 넣음              →  3번이 재사용된다

옛 3으로 접근          →  B 가 나온다.  거부할 방법이 없다
```

인덱스가 곧 신원이라 "몇 번째 주인인가"를 표현할 수 없다.

그래서 **발급 회차를 같이 준다.**

```csharp
readonly struct SlotHandle
{
    public int Index { get; }
    public int Generation { get; }
}
```

`Remove`가 세대를 올리는 것만으로 **그 칸을 가리키던 모든 옛 핸들이 한 번에 무효**가 된다. 옛 핸들 목록을 관리할 필요가 없다.

### 세대 하나가 두 가지를 말한다

`Add`와 `Remove`가 각각 세대를 1씩 올린다. 그러면 **상태가 바뀔 때마다 최하위 비트가 토글된다.**

```
세대  0  →  Add  →  1  →  Remove  →  2  →  Add  →  3
     짝수         홀수            짝수         홀수
     비었음        살아있음         비었음        살아있음
```

정수 하나가 "몇 번째 주인인가"와 "지금 살아있는가"를 동시에 표현한다. `bool[] _alives` 배열이 통째로 사라졌다.

```csharp
if ((_generations[index] & 1) == 0) return false;   // 짝수 = 비었음
if (generation != _generations[index]) return false; // 세대 불일치 = 옛 핸들
```

이 구조에는 이름이 있다. Unity DOTS의 `Entity`가 정확히 같은 모양이다.

```csharp
SlotHandle { int Index; int Generation; }     // 이 프로젝트
Entity     { int Index; int Version;    }     // Unity DOTS
```

---

## 칸 크기가 제각각이면 무엇이 달라지나

`SlotMap`은 "칸 하나"를 준다. `Vector3` 100개를 연속으로 담을 자리는 못 준다.

`BlockAllocator`는 **바이트 수**를 받아 그만큼의 자리를 준다. 그러면 칸에 번호를 매길 수가 없다 — 크기가 다 다르니까.

그래서 **관리 정보를 옆 배열이 아니라 데이터 사이에 심는다.**

```
아레나 = byte[] 하나

 0      4              20     24        40     44 ......... 63
[ 24 ][   payload   ][ 24 ][ 20 ][ pay ][ 20 ][   41(비었음)   ]
 헤더                  푸터  헤더         푸터
 └────── 블록 24바이트 ─────┘└─ 블록 20바이트 ─┘└─ 블록 40, 비었음 ─┘

헤더 = 블록 전체 크기 | (비었으면 1)
       크기가 4의 배수라 최하위 비트가 늘 비어 있다
```

**크기 자체가 다음 블록을 가리키는 링크**가 된다. `0 → 24 → 44 → 끝`으로 걸으면 아레나 전체를 훑을 수 있고, 별도 인덱스가 필요 없다.

| 자리 | 역할 |
|---|---|
| **헤더** (앞 4B) | 크기 + 빈 플래그. 다음 블록 위치 계산 |
| **푸터** (뒤 4B) | 같은 값의 사본. **앞** 블록 시작을 O(1)로 역산 → 앞쪽 병합 |
| **빈 블록의 payload 앞 8B** | free 리스트의 `prev`/`next`. 빈 칸은 안 쓰니 공짜 |

```
빈 블록:   [헤더4 | prev4 | next4 | ...남은 payload... | 푸터4]
사용 블록: [헤더4 |        사용자 데이터              | 푸터4]
                   ↑ 할당되는 순간 링크는 덮어써진다
```

`SlotMap`의 `_freeHead` + `_next[]`를 **별도 배열 없이** 재현한 것이다.

### 해제하면 양옆을 확인한다

```
[사용][빈 300][사용]        →  Free(가운데)  →  [사용][빈 300][사용]
[빈 300][빈 300][사용]      →  Free(왼쪽)    →  [   빈 608   ][사용]
                                                 ↑ 가운데 헤더/푸터가 payload로 흡수된다
```

합치지 않으면 **빈 공간 900바이트가 300짜리 조각 셋으로 흩어져 500 요청이 실패**한다. 그게 외부 단편화다.

---

## 30초 만에 써보기

```csharp
// SlotMap — 값을 보관해준다
var map = new SlotMap<string>(8);

var goblin = map.Add("고블린");
map.TryGet(goblin, out var name);    // true, "고블린"

map.Remove(goblin);
map.TryGet(goblin, out _);           // false — 세대가 달라 거부된다

foreach (var (handle, value) in map) { /* 살아있는 것만, 할당 없이 */ }
```

```csharp
// BlockAllocator — 자리만 준다
var alloc = new BlockAllocator(1024);

alloc.TryAlloc(64, out int offset);  // "64바이트짜리 자리 줘"
alloc.AsSpan(offset)[0] = 0xAB;      // 값을 쓰는 건 호출자 몫

alloc.Free(offset);                  // 양옆이 비었으면 자동으로 합쳐진다
```

`SlotMap`은 **값을 보관해주는 그릇**이고 `BlockAllocator`는 **자리를 나눠주는 관리인**이다. 후자는 안에 뭐가 들어가는지 모른다.

---

## 재보면 예상이 맞나

두 번 쟀고, 두 번 다 예상과 달랐다.

### 순회 비용은 `Count`가 아니라 `Capacity`에 비례한다

용량 100만, .NET 10 Release. 밀집도를 바꿔가며 전체 순회 시간을 쟀다.

| 밀집도 | 살아있는 원소 | 총 시간 |
|---|---|---|
| 0.1% | 1,000 | 0.35ms |
| 10% | 100,000 | 0.44ms |
| 100% | 1,000,000 | 0.85ms |

```
순회 시간 ≈ 용량 × 0.35ns  +  원소 수 × 0.5ns
              (죽은 칸 스캔)     (튜플·핸들 생성)
```

**살아있는 게 1000개여도 100만 칸을 훑는다.** 그리고 `Capacity`는 한 번 커지면 줄지 않는다.

이 결과로 dense/sparse 분리를 **보류**했다 — 없앨 수 있는 건 앞항 0.35ms뿐이고, 그건 60fps 프레임 예산의 2%다.

### 탐색을 136배 줄였더니 단편화가 두 배가 됐다

`BlockAllocator`에서 빈 블록만 따라가는 free 리스트를 넣었더니 탐색이 극적으로 줄었다.

| 워크로드 | 아레나 전체 걷기 | free 리스트 | 배수 |
|---|---|---|---|
| 작은 것 8~64B | 2491 | 18.3 | **136배** |
| 섞임 8~512B | 426 | 4.6 | 93배 |
| 큰 것 256~2048B | 70.7 | 2.6 | 27배 |

그런데 단편화율이 반대로 갔다.

| 워크로드 | 아레나 전체 걷기 | free 리스트 |
|---|---|---|
| 작은 것 | 0.481 | **0.996** |
| 섞임 | 0.192 | **0.954** |

원인은 **LIFO**다. 아레나를 걷는 방식은 늘 주소가 가장 낮은 블록을 고르니 할당이 앞쪽에 뭉치고 뒤에 큰 연속 공간이 남는다. free 리스트는 최근 해제된 걸 머리에 꽂으니 주소 순서를 잃는다.

**속도를 136배 얻으면서 공간 효율을 잃었다.** 재보기 전에는 순수한 개선인 줄 알았다.

### 그리고 최적 정책은 워크로드가 정한다

| 워크로드 | 이기는 쪽 | 근거 |
|---|---|---|
| 작은 것 8~64B | **best-fit** | 단편화 0.998 → 0.086, 탐색은 거의 안 늘어난다 |
| 섞임 8~512B | **first-fit** | best-fit이 38배 비싸다 |

"어느 정책이 좋은가"에 정답이 없다. **워크로드를 정하지 않으면 질문 자체가 성립하지 않는다.**

→ 세 번을 다시 잰 과정과 지표 정의 오류까지: [측정 기록](docs/측정-기록.md)

---

## VContainer는 왜 다르게 만들었나

만들고 나서 원본을 다시 읽으니, 이름만 같고 **최적화 방향이 정반대**였다.

```
// VContainer · Runtime/Internal/FreeList.cs
T[] values;   int lastIndex;   object gate;

Add(T)      →  values를 처음부터 훑어 null인 자리를 찾는다   O(n)
RemoveAt(i) →  values[i] = null
```

**free 체인이 없다.** `_freeHead`도 없다. 배열 하나가 전부고, 빈 자리를 매번 선형 탐색한다.

| | VContainer `FreeList<T>` | 이 프로젝트 `SlotMap<T>` |
|---|---|---|
| 목적 | 매 프레임 순회하며 스스로를 제거하는 목록 | 안전한 핸들로 접근하는 슬롯 저장소 |
| 빈 칸 표시 | `null` — 데이터 자리에 | 세대 홀짝 — 별도 배열에 |
| 빈 칸 찾기 | 선형 탐색 O(n) | `_freeHead` O(1) |
| 죽은 핸들 | 막을 수단 없음 | 세대 불일치로 거부 |
| 담을 타입 | 참조 타입만 | 값 타입 포함 전부 |

### `null`을 고른 순간 나머지가 결정됐다

```
"매 프레임 순회하며 그 자리에서 제거"
        ↓
List<T>.RemoveAt은 뒤를 당긴다 → 순회 인덱스가 어긋나 항목을 건너뛴다
        ↓
당기지 말고 그 자리를 비운다
        ↓
"비었다"를 무엇으로 표시하나 → null (참조 타입이면 공짜)
        ↓
   ┌───────────────┼──────────────────────┐
   ↓               ↓                      ↓
참조 타입만      체인을 적을 자리가 없다      구멍이 배열에 남는다
FreeList<int> 불가  → Add가 O(n)           → 순회가 O(lastIndex+1)
                                          → if (item != null) 검사
```

세 제약이 따로따로가 아니라 **한 결정의 세 얼굴**이다. 그리고 그 결정은 실수가 아니라 요구에서 강제됐다.

### 그래서 옳은 선택이다

```
Add       진입점 등록 — 스코프 빌드 시점에 몇 번
순회      매 프레임
```

매 프레임 도는 건 순회지 `Add`가 아니다. 기본 용량 16, 진입점은 보통 수십 개. **없는 문제를 풀 이유가 없다.**

`SlotMap`에서 잰 밀집도 문제도 VContainer에 똑같이 존재하지만, n이 수십 개라 비용이 안 될 뿐이다. 같은 설계, 다른 규모, 다른 결론.

→ 잠금 범위까지 포함한 전체 비교: [VContainer 비교](docs/vcontainer-비교.md)

---

## 무엇을 막고 무엇을 못 막나

두 구현의 안전성 수준이 **의도적으로 다르다.**

| | `SlotMap<T>` | `BlockAllocator` |
|---|---|---|
| 식별자 | `SlotHandle { Index, Generation }` | `int offset` |
| 이중 해제 | 세대로 거부 | 헤더의 빈 플래그로 거부 |
| 재사용된 자리에 옛 식별자 | 거부 | **못 막는다** |
| 블록 한가운데를 가리키는 위치 | 해당 없음 | **못 막는다** |

`int offset` 하나로는 "몇 번째 발급분인가"를 표현할 수 없다. **C의 `free(ptr)`가 use-after-free를 못 막는 것과 같은 이유**고, 교과서적 할당기를 만드는 것이 목적이라 그대로 뒀다.

막으려면 헤더에 세대를 두고 핸들을 반환하면 된다. 대가는 블록당 4바이트, 오버헤드가 8에서 12로. **안전성과 공간의 맞교환**이고 이 프로젝트는 후자를 골랐다.

---

## 실행

```bash
git clone https://github.com/SeoBYP/FreeList.git
cd FreeList

dotnet build
dotnet test                                  # 74개

dotnet run -c Release --project FreeList     # 할당 정책 측정
```

`-c Release`를 빼면 프로그램이 먼저 경고를 찍는다. Debug 빌드로 재면 숫자가 최대 30배까지 어긋난다.

### 테스트 구성

| 대상 | 개수 | 성격 |
|---|---|---|
| `SlotMap` | 39 | 생성자·Add·Remove·재사용·TryGet·세대·확장·GC·순회·랜덤 |
| `BlockAllocator` | 35 | 생성자·TryAlloc·Free·재사용·분할·병합·무결성·단편화·랜덤 |

모든 테스트가 실제로 겪은 버그 하나씩을 붙잡고 있다. 랜덤 스트레스 2종은 고정 시드를 써서 실패하면 그대로 재현된다.

---

## 더 읽을 것

| 문서 | 내용 |
|---|---|
| [설계 노트](docs/설계-노트.md) | 구조 다이어그램, API 전체, 개념 정리 |
| [측정 기록](docs/측정-기록.md) | 세 번 다시 잰 과정, 지표 정의 오류, 정책 비교 전문 |
| [VContainer 비교](docs/vcontainer-비교.md) | 잠금 범위, 세 구현을 나란히 놓기 |
| [디버깅 기록](docs/디버깅-기록.md) | 나머지 버그와 회귀 테스트 |

---

## 앞으로

- [ ] **주소 순서 free 리스트** — LIFO가 만든 단편화를 되돌리면서 탐색 이득은 유지할 수 있는지
- [ ] **크기 클래스** — 자주 쓰는 크기를 따로 관리하면 탐색과 단편화가 어떻게 달라지는지
- [ ] **스레드 안전 버전** — `lock` 기준선 → 스레드 로컬 캐시 → `{index:32, version:32}`를 `long` 하나에 담은 CAS. 지금 만든 세대 핸들이 그대로 ABA 방어막이 된다

---

## 참고 자료

- [VContainer — hadashiA](https://github.com/hadashiA/VContainer) — 출발점. `Runtime/Internal/FreeList.cs`, `Runtime/Unity/PlayerLoopRunner.cs`
- [Slotmap: The budget allocator you probably should use](https://electrp.com/posts/slotmap/) — 세대 핸들 설계와 트레이드오프
- [Memory Allocation Strategies Part 5: Free List Allocator — gingerBill](https://www.gingerbill.org/article/2021/11/30/memory-allocation-strategies-005/) — 가변 크기 블록의 정석
- [Solving the ABA Problem for Lock-Free Free Lists — moodycamel](https://moodycamel.com/blog/2014/solving-the-aba-problem-for-lock-free-free-lists) — 스레드 안전 버전 필독
