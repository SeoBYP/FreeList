using FreeList;

var map = new SlotMap<int>(1);

var handle = map.Add(42);

map.TryGet(handle, out int value);

Console.WriteLine(value);