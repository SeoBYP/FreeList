using System.Text;

namespace FreeList;

public class SlotMap
{
    private int[] _items;
    private int[] _next;
    private bool[] _alives;
    private int _freeHead;
    private int _count;

    public int Count => _count;
    
    public SlotMap(int capacity = 1024)
    {
        if (capacity <= 0)
            throw new ArgumentException("capacity must be greater than 0");

        _items = new int[capacity];
        _next = new int[capacity];
        _alives = new bool[capacity];
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
    public int Add(int item)
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

        return index;
    }

    /// <summary>
    /// 그 칸을 비우고 free 체인에 되돌림
    /// </summary>
    /// <param name="index"></param>
    public bool Remove(int index)
    {
        if(!IsValid(index))
            return false;   
        _count--;
        _next[index] = _freeHead;
        _freeHead = index;
        _alives[index] = false;
        return true;
    }

    /// <summary>
    /// 값 꺼내기
    /// </summary>
    /// <param name="index"></param>
    /// <returns></returns>
    public bool TryGet(int index, out int value)
    {
        if (!IsValid(index))
        {
            value = default;
            return false;
        }

        value = _items[index];
        return true;
    }

    private bool IsValid(int index)
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
        return true;
    }

    private void Resize()
    {
        var newCapacity = _items.Length * 2;
        var newItems = new int[newCapacity];
        var newNext = new int[newCapacity];
        var newAlives = new bool[newCapacity];
        var oldCapacity = _items.Length;
        
        Array.Copy(_items, newItems, _items.Length);
        Array.Copy(_alives, newAlives, _alives.Length);
        Array.Copy(_next, newNext, oldCapacity);
        for (int i = oldCapacity; i < newNext.Length; i++)
        {
            newNext[i] = i + 1;
        }
        newNext[newCapacity - 1] = _freeHead;
        
        _freeHead = oldCapacity;
        
        _items = newItems;
        _next = newNext;
        _alives = newAlives;
    }
}