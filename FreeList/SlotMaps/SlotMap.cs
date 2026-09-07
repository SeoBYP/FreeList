using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;

namespace FreeList;

public class SlotMap<T>
{
    private T[] _items;
    private int[] _next;
    private bool[] _alives;
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
        _alives = new bool[capacity];
        _generations = new int[capacity];
        Array.Fill(_generations, 1);
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
    /// 빈 칸에 넣고 그 인덱스를 반환
    /// </summary>
    /// <param name="item"></param>
    /// <returns></returns>
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
        _alives[index] = true;

        // Add할 때 현재 세대 + Index를 같이 반환
        return new SlotHandle(index, _generations[index]);
    }

    /// <summary>
    /// 그 칸을 비우고 free 체인에 되돌림
    /// </summary>
    /// <param name="index"></param>
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
        _alives[index] = false;
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
    /// 값 꺼내기
    /// 이 메서드가 false를 반환할 때는 value가 null일 수 있다"고 알려주는 거
    /// </summary>
    /// <param name="index"></param>
    /// <param name="value"></param>
    /// <returns></returns>
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

        // 이미 죽은 값이면 종료
        if (_alives[index] == false)
        {
            return false;
        }

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
        var newAlives = new bool[newCapacity];
        var newGenerations = new int[newCapacity];
        var oldCapacity = _items.Length;
        
        Array.Copy(_items, newItems, oldCapacity);
        Array.Copy(_alives, newAlives, oldCapacity);
        Array.Copy(_next, newNext, oldCapacity);
        Array.Copy(_generations, newGenerations, oldCapacity);
        for (int i = oldCapacity; i < newCapacity; i++)
        {
            newNext[i] = i + 1;
            newGenerations[i] = 1;
        }
        newNext[newCapacity - 1] = _freeHead;
        
        _freeHead = oldCapacity;
        
        _items = newItems;
        _next = newNext;
        _alives = newAlives;
        _generations = newGenerations;
    }
}