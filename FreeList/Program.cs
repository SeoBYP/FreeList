using FreeList;

var map = new SlotMap<string>(8);

var a = map.Add("A");
var b = map.Add("B");
var c = map.Add("C");
var d = map.Add("D");
var eh = map.Add("E");

map.Remove(b);   // 1번 구멍
map.Remove(d);   // 3번 구멍

var e = map.GetEnumerator();
while (e.MoveNext())
    Console.WriteLine(e.Current);
    
    
foreach (var (handle, value) in map)
    Console.WriteLine($"{handle} = {value}");