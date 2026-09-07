using FreeList;

const int capacity = 4;
var map = new SlotMap<int>(capacity);

var indices = new SlotHandle[capacity];
for (int i = 0; i < capacity; i++)
    indices[i] = map.Add(i * 10);
    
map.Add(999);