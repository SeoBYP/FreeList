using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;

namespace FreeList;

public class SlotMap<T>
{
    private T[] _items;
    private int[] _next;
    // 세대 관리 배열
    private int[] _generations;
    private int _freeHead;
    private int _count;

    public int Count => _count;
    
    public SlotMap(int capacity = 1024)
    {
        if (capacity <= 0)
            throw new ArgumentException("capacity must be greater than 0");

        _items = new T[capacity];
        _next = new int[capacity];
        _generations = new int[capacity];
        for (int i = 0; i < _items.Length; i++)
        {
            _next[i] = i + 1;
        }
        
        // 마지막은 다음이 없으므로 -1로 초기화 한다.
        _next[_items.Length - 1] = -1;
        _freeHead = 0;
        _count = 0;
    }

    /// <summary>
    /// 빈 칸에 넣고 핸들을 발급한다. 빈 칸이 없으면 용량을 늘린다.
    /// </summary>
    /// <param name="item">저장할 값</param>
    /// <returns>이 원소를 가리키는 핸들</returns>
    public SlotHandle Add(T item)
    {
        if (_freeHead == -1)
        {
            Resize();
        }
        _items[_freeHead] = item;

        int index = _freeHead;
        _freeHead = _next[_freeHead];
        _count++;
        _generations[index]++;
        
        // Add할 때 현재 세대 + Index를 같이 반환
        return new SlotHandle(index, _generations[index]);
    }

    /// <summary>
    /// 그 칸을 비우고 free 체인에 되돌린다.
    /// </summary>
    /// <param name="handle">Add가 발급한 핸들</param>
    /// <returns>제거했으면 true. 범위 밖이거나 이미 비었거나 옛 핸들이면 false.</returns>
    public bool Remove(SlotHandle handle)
    {
        return Remove(handle.Index, handle.Generation);
    }
    
    private bool Remove(int index, int generation)
    {
        if(!IsValid(index, generation))
            return false;   
        _count--;
        _next[index] = _freeHead;
        _freeHead = index;
        // 제거할 때 세대 증가, 이전 세대 재사용 방지
        _generations[index]++;
        // 지정한 형식이 참조 형식인지 아니면 참조 또는 참조가 포함된 값 형식인지를 나타내는 값을 반환합니다.
        if (RuntimeHelpers.IsReferenceOrContainsReferences<T>())
        {
            _items[index] = default;
        }
        return true;
    }

    /// <summary>
    /// 살아있는 칸이면 값을 꺼내고 true, 아니면 false.
    /// MaybeNullWhen(false)는 "false를 반환할 때만 value가 null일 수 있다"를 컴파일러에 알린다.
    /// </summary>
    /// <param name="handle">Add가 발급한 핸들</param>
    /// <param name="value">성공 시 저장된 값. 실패 시 default이며 호출자는 쓰면 안 된다.</param>
    /// <returns>꺼냈으면 true</returns>
    public bool TryGet(SlotHandle handle, [MaybeNullWhen(false)] out T value)
    {
        return TryGet(handle.Index, handle.Generation, out value);
    }

    private bool TryGet(int index, int generation, [MaybeNullWhen(false)] out T value)
    {
        if (!IsValid(index, generation))
        {
            value = default;
            return false;
        }

        value = _items[index];
        return true;
    }

    private bool IsValid(int index, int generation)
    {
        // 음수, 길이를 초과한 인덱스 입력시 종료
        if (index < 0 || index >= _items.Length)
        {
            return false;
        }
        
        // 세대에서 홀수면 값이 있는 Index => 유효한 Index
        // 짝수면 값이 비워져 있는 Index => 유효하지 않은 Index
        if ((_generations[index] & 1) == 0)
        {
            return false;
        }
        
        // 유효하지 않은 세대면 종료
        if (generation != _generations[index])
        {
            return false;
        }
        return true;
    }

    private void Resize()
    {
        var newCapacity = _items.Length * 2;
        var newItems = new T[newCapacity];
        var newNext = new int[newCapacity];
        var newGenerations = new int[newCapacity];
        var oldCapacity = _items.Length;
        
        Array.Copy(_items, newItems, oldCapacity);
        Array.Copy(_next, newNext, oldCapacity);
        Array.Copy(_generations, newGenerations, oldCapacity);
        for (int i = oldCapacity; i < newCapacity; i++)
        {
            newNext[i] = i + 1;
        }
        newNext[newCapacity - 1] = _freeHead;
        
        _freeHead = oldCapacity;
        
        _items = newItems;
        _next = newNext;
        _generations = newGenerations;
    }
    
    public struct Enumerator
    {
        private readonly SlotMap<T> _map;
        private int _index;
        
        internal Enumerator(SlotMap<T> map)
        {
            _map = map;
            _index = -1;
        }
        
        public bool MoveNext()
        {
            for (int i = _index + 1; i < _map._items.Length; i++)
            {
                if ((_map._generations[i] & 1) == 1)
                {
                    _index = i;
                    return true;
                }
            }
            return false;
        }

        public (SlotHandle Handle, T Value) Current
        {
            get
            {
                var handle = new SlotHandle(_index, _map._generations[_index]);
                return (handle, _map._items[_index]);
            }
        }
    }
    
    public Enumerator GetEnumerator()
    {
        return new Enumerator(this);
    }
}