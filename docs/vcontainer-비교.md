# VContainer의 FreeList와 무엇이 다른가

이 프로젝트의 출발점입니다. README [왜 이걸 만들었나](../README.md#왜-이걸-만들었나)와 [다시 원본으로 돌아가서](../README.md#다시-원본으로-돌아가서)의 상세판입니다.

README가 "왜 그렇게 만들었나"를 다뤘다면, 여기는 **구현 세부와 잠금 범위**를 봅니다.

---

## 출발점이 된 코드

```csharp
// VContainer 1.19.0 · Runtime/Unity/PlayerLoopRunner.cs
readonly FreeList<IPlayerLoopItem> runners = new FreeList<IPlayerLoopItem>(16);

for (var i = 0; i < span.Length; i++)
{
    var item = span[i];
    if (item != null)                    // null을 건너뜁니다
    {
        if (!item.MoveNext())
            runners.RemoveAt(i);         // 순회 도중 제거합니다
    }
}
```

## VContainer의 구현

```
// Runtime/Internal/FreeList.cs
T[] values;          백킹 배열
int lastIndex;       마지막 유효 위치 (-1 비었음, -2 해제됨)
object gate;         Add / RemoveAt 전용 lock

Add(T)      →  values를 처음부터 훑어 null인 자리를 찾습니다        O(n)
RemoveAt(i) →  values[i] = null.  i가 lastIndex면 뒤로 스캔해 재계산
AsSpan()    →  values.AsSpan(0, lastIndex + 1)
Length      →  lastIndex + 1      살아있는 개수가 아니라 "스캔 범위"입니다
성장        →  new T[len + len / 2]   (1.5배)
```

Unity 2021.3 이상에서는 배열을 `IntPtr`로 캐스팅해 0인 워드를 통째로 스캔하는 unsafe 경로가 붙지만, 여전히 선형 탐색입니다.

**free 체인이 없습니다.** 이름에 "free list"가 붙어 있지만 빈 칸을 잇는 링크도, `_freeHead`도 없습니다. 배열 하나가 전부입니다.

## 구현 차이 전체

| | VContainer `FreeList<T>` | 이 프로젝트 `SlotMap<T>` |
|---|---|---|
| **목적** | 매 프레임 순회하며 스스로를 제거하는 목록 | 안전한 핸들로 접근하는 슬롯 저장소 |
| **빈 칸 표시** | `null` (데이터 자리에) | 세대의 홀짝 비트 (별도 배열에) |
| **빈 칸 찾기** | 선형 탐색 O(n) | `_freeHead` O(1) |
| **죽은 핸들** | 막을 수단 없음 (인덱스가 곧 신원) | 세대 불일치로 거부 |
| **담을 타입** | 참조 타입만 (`null`이 필요하므로) | 값 타입 포함 전부 |
| **GC 누수** | `null` 대입이 곧 정리 | `IsReferenceOrContainsReferences<T>`로 조건부 정리 |
| **스레드** | 쓰기만 `lock`, 읽기는 단일 스레드 전제 | 단일 스레드 전용 |

아래 두 줄이 README에 안 담긴 부분이라 따로 봅니다.

### GC 누수 줄이 왜 다른가

VContainer는 `null`을 넣는 것이 곧 참조를 끊는 것이라 정리가 공짜입니다. `SlotMap`은 세대 비트로 비움을 표시하므로 `_items[index]`에 옛 참조가 그대로 남습니다. 그대로 두면 죽은 객체를 GC가 못 걷어갑니다.

그래서 `Remove`에서 조건부로 지웁니다.

```csharp
if (RuntimeHelpers.IsReferenceOrContainsReferences<T>())
    _items[index] = default;
```

`IsReferenceOrContainsReferences<T>()`는 JIT이 **컴파일 타임 상수로 접어버리는** 내장 함수입니다. `T`가 `int`나 `Vector3`처럼 참조를 안 품는 값 타입이면 이 `if` 전체가 기계어에서 사라집니다. 값 타입은 비용을 한 푼도 안 냅니다.

### 잠금 범위가 흥미로운 부분

`PlayerLoopRunner`에는 `lock`이 한 줄도 없습니다. `FreeList` 안쪽의 `Add`와 `RemoveAt`만 잠그고 **순회는 안 잠급니다.**

```
등록과 해제  →  여러 스레드에서 올 수 있습니다        그래서 lock
순회        →  PlayerLoop 스레드 하나만 합니다      그래서 lock 없음
```

이게 성립하는 이유가 다시 "당기지 않는다"입니다.

```
RemoveAt이 안 당긴다  →  순회 중인 인덱스가 안 흔들립니다
순회가 null을 검사     →  도중에 비어도 건너뜁니다
배열이 성장하면        →  순회 중인 Span은 옛 배열을 봅니다.
                          새 항목은 다음 프레임에 보입니다 (놓치는 게 아니라 늦는 것)
```

**안전한 API를 만든 게 아니라 안전한 사용법을 전제한 것입니다.** 문서에 없는 규칙이고 어기면 조용히 깨지지만, 코드는 단순하고 빠릅니다. 라이브러리가 자기 사용처를 알 때만 쓸 수 있는 선택입니다.

`_freeHead`와 `_next[]`를 두면 지켜야 할 불변식이 늘고 잠금 범위가 커집니다. 무잠금 읽기도 그만큼 어려워집니다. **체인을 안 만든 세 번째 이유가 이것입니다.**

## 구멍이 순회에 남는다는 것

세 제약 중 이게 제일 눈에 안 띕니다. 살아있는 게 3개여도 `lastIndex`가 100이면 101번 돕니다. `AsSpan()`이 `values.AsSpan(0, lastIndex + 1)`이라 전체 용량은 아니지만, 중간에 뚫린 구멍은 그대로 스캔됩니다.

```
꼬리에서 빠지면  →  lastIndex가 줄어 스캔 범위가 짧아집니다   (high-water mark)
중간에서 빠지면  →  구멍이 그대로 남아 계속 스캔됩니다
```

부분적 완화지 해결이 아닙니다.

이건 `SlotMap`에서 측정한 밀집도 문제와 **정확히 같은 것**입니다([측정 기록](측정-기록.md#벽시계-시간은-무엇을-재고-있는지-말해주지-않습니다)). 용량 100만에 살아있는 게 1000개면 100만 번 훑습니다. VContainer도 같은 구조인데 n이 수십 개라 비용이 안 되는 것뿐입니다.

## 셋을 나란히 놓으면

배열 개수로 보면 같은 문제에 대한 세 가지 답이 됩니다.

```
VContainer FreeList         values[]                          관리 정보를 안 만듭니다 (그때그때 찾습니다)
이 프로젝트 SlotMap          _items[] _next[] _generations[]   관리 정보를 옆 배열에 둡니다
이 프로젝트 BlockAllocator   byte[]                            관리 정보를 데이터 사이에 심습니다
```

최적화 방향도 정반대입니다.

```
VContainer     순회를 최적화합니다       →  제거가 O(1), 추가는 O(n)이어도 됩니다
이 프로젝트     할당과 해제를 최적화합니다  →  추가와 제거가 O(1), 순회는 밀집도에 맡깁니다
```

VContainer는 메모리 할당기를 만든 게 아닙니다. **"매 프레임 순회하면서 스스로를 제거하는 목록"**을 만들었고, 그 요구에 필요한 만큼만 만들었습니다.

**어느 것이 옳은지는 워크로드가 정합니다.** 4단계 측정에서 first-fit과 best-fit의 승자가 워크로드마다 뒤집혔던 것([측정 기록](측정-기록.md#최적-정책은-워크로드가-정합니다))과 같은 결론이고, VContainer의 선택은 자기 워크로드에서 정확히 맞습니다.

## 덤: 이 구조에는 이름이 있습니다

```csharp
SlotHandle { int Index; int Generation; }     // 이 프로젝트
Entity     { int Index; int Version;    }     // Unity DOTS
```

같은 구조입니다. 엔티티를 파괴하면 `Version`이 올라가고 옛 `Entity` 값으로는 접근이 거부됩니다. `EntityManager.Exists(entity)`가 `IsValid`가 하는 일입니다.

VContainer 내부를 읽다 시작한 것이 ECS 엔티티 시스템의 밑바닥까지 닿았습니다.

---

## 이어서 볼 것

- [설계 노트](설계-노트.md) 구조와 API
- [측정 기록](측정-기록.md) 밀집도와 할당 정책 측정
- [디버깅 기록](디버깅-기록.md) 만들면서 낸 버그와 회귀 테스트
