# VContainer의 FreeList와 무엇이 다른가

이 프로젝트의 출발점. README [왜 만들었나](../README.md#왜-만들었나)에서 이어진다.

---

## 개요

### 출발점이 된 코드

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

### VContainer의 구현

```
// Runtime/Internal/FreeList.cs
T[] values;          백킹 배열
int lastIndex;       마지막 유효 위치 (-1 비었음, -2 해제됨)
object gate;         Add / RemoveAt 전용 lock

Add(T)      →  values를 처음부터 훑어 null인 자리를 찾는다        O(n)
RemoveAt(i) →  values[i] = null.  i가 lastIndex면 뒤로 스캔해 재계산
성장        →  new T[len + len / 2]   (1.5배)
```

Unity 2021.3+ 에서는 배열을 `IntPtr`로 캐스팅해 0인 워드를 통째로 스캔하는 unsafe 경로가 붙지만, 여전히 선형 탐색이다.

**free 체인이 없다.** 이름에 "free list"가 붙어 있지만 빈 칸을 잇는 링크도, `_freeHead`도 없다. 배열 하나가 전부다.

### 차이

| | VContainer `FreeList<T>` | 이 프로젝트 `SlotMap<T>` |
|---|---|---|
| **목적** | 매 프레임 순회하며 스스로를 제거하는 목록 | 안전한 핸들로 접근하는 슬롯 저장소 |
| **빈 칸 표시** | `null` — 데이터 자리에 | 세대의 홀짝 비트 — 별도 배열에 |
| **빈 칸 찾기** | 선형 탐색 O(n) | `_freeHead` O(1) |
| **죽은 핸들** | 막을 수단 없음 (인덱스가 곧 신원) | 세대 불일치로 거부 |
| **담을 타입** | 참조 타입만 (`null`이 필요하므로) | 값 타입 포함 전부 |
| **GC 누수** | `null` 대입이 곧 정리 | `IsReferenceOrContainsReferences<T>`로 조건부 정리 |
| **스레드** | 쓰기만 `lock`, 읽기는 단일 스레드 전제 | 단일 스레드 전용 |

### 세 제약은 하나의 결정에서 나왔다

`null`을 빈 표시로 고른 것 하나가 세 가지를 동시에 결정한다.

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
참조 타입만      체인을 적을 자리가 없다     구멍이 배열에 남는다
FreeList<int> 불가  → Add가 O(n) 선형탐색   → 순회가 O(lastIndex+1)
                                          → if (item != null) 검사 필요
```

세 제약이 따로따로가 아니라 **한 결정의 세 얼굴**이다. 그리고 그 결정은 실수가 아니라 요구에서 강제된 것이다 — 순회 중 그 자리에서 제거하려면 당기면 안 되고, 당기지 않으려면 빈 자리를 남겨야 하고, 남기려면 표시가 필요하다.

**세 번째가 특히 눈에 안 띈다.** 살아있는 게 3개여도 `lastIndex`가 100이면 101번 돈다. `AsSpan()`이 `values.AsSpan(0, lastIndex + 1)`이라 전체 용량은 아니지만, 중간에 뚫린 구멍은 그대로 스캔된다.

```
꼬리에서 빠지면  →  lastIndex가 줄어 스캔 범위가 짧아진다   (high-water mark)
중간에서 빠지면  →  구멍이 그대로 남아 계속 스캔된다
```

이건 `SlotMap`에서 측정한 밀집도 문제와 **정확히 같은 것**이다 (7절 문제 2). 용량 100만에 살아있는 게 1000개면 100만 번 훑는다. VContainer도 같은 구조인데 n이 수십 개라 비용이 안 되는 것뿐이다.

### 왜 체인을 안 만들었나

이게 처음 궁금했던 것이고, 답은 셋이다.

**① `Add`가 드물고 n이 작다**

```
Add       진입점 등록 — 스코프 빌드 시점에 몇 번
RemoveAt  MoveNext()가 false를 반환할 때
순회      매 프레임
```

매 프레임 도는 건 순회지 `Add`가 아니다. `Add`가 O(n)이어도 초당 60번 불릴 일이 없고, 기본 용량이 16이며 진입점은 보통 수십 개다. **없는 문제를 풀 이유가 없다.**

**② `null`을 빈 표시로 고른 순간 체인 둘 자리가 사라진다**

체인을 만들려면 빈 칸 어딘가에 "다음 빈 칸 번호"를 적어야 하는데, `null`로 비워버리면 적을 데가 없다. 둘은 한 세트다.

그리고 `null`로 비우는 게 **목적 그 자체**였다. `List<T>.RemoveAt`은 뒤 원소를 앞으로 당겨서 순회 중 인덱스를 어긋나게 만든다. `null`로 비우면 안 흔들린다. **순회 중 제거가 이 자료구조의 존재 이유고, 슬롯 재사용은 부산물이다.**

**③ 상태가 적을수록 잠글 것이 적다**

`FreeList`는 `Add`/`RemoveAt`만 잠그고 순회는 안 잠근다. 이게 성립하는 이유가 "당기지 않는다" + "상태가 `values` 하나뿐"이다.

```
RemoveAt이 안 당긴다  →  순회 중인 인덱스가 안 흔들린다
순회가 null을 검사     →  도중에 비어도 건너뛴다
배열이 성장하면        →  순회 중인 Span은 옛 배열을 본다.
                          새 항목은 다음 프레임에 보인다 (놓치는 게 아니라 늦는 것)
```

`_freeHead`와 `_next[]`를 두면 지켜야 할 불변식이 늘고 잠금 범위가 커진다. 무잠금 읽기도 그만큼 어려워진다.

### 최적화 방향이 정반대다

```
VContainer     순회를 최적화한다       →  제거가 O(1), 추가는 O(n)이어도 됨
이 프로젝트     할당·해제를 최적화한다   →  추가·제거가 O(1), 순회는 밀집도에 맡김
```

VContainer는 메모리 할당기를 만든 게 아니다. **"매 프레임 순회하면서 스스로를 제거하는 목록"**을 만들었고, 그 요구에 필요한 만큼만 만들었다.

### 셋을 나란히 놓으면

배열 개수로 보면 같은 문제에 대한 세 가지 답이 된다.

```
VContainer FreeList    values[]                          관리 정보를 안 만든다 (그때그때 찾는다)
이 프로젝트 SlotMap     _items[] _next[] _generations[]   관리 정보를 옆 배열에 둔다
이 프로젝트 BlockAllocator   byte[]                       관리 정보를 데이터 사이에 심는다
```

**어느 것이 옳은지는 워크로드가 정한다.** 4단계 측정에서 first-fit과 best-fit의 승자가 워크로드마다 뒤집혔던 것([`docs/디버깅-기록.md`](docs/디버깅-기록.md))과 같은 결론이고, VContainer의 선택은 자기 워크로드에서 정확히 맞다.

### 덤 — 이 구조에는 이름이 있다

```csharp
SlotHandle { int Index; int Generation; }     // 이 프로젝트
Entity     { int Index; int Version;    }     // Unity DOTS
```

같은 구조다. 엔티티를 파괴하면 `Version`이 올라가고 옛 `Entity` 값으로는 접근이 거부된다. `EntityManager.Exists(entity)`가 `IsValid`가 하는 일이다.

VContainer 내부를 읽다 시작한 것이 ECS 엔티티 시스템의 밑바닥까지 닿았다.

---
