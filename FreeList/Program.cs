using FreeList;

const int capacity = 4;
var map = new SlotMap(capacity);

var indices = new int[capacity];
for (int i = 0; i < capacity; i++)
    indices[i] = map.Add(i * 10);
    
map.Add(999);