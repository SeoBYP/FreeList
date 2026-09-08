using FreeList;

// 64 크기로 영역 할당
// 아레나 전체가 다 메모리가 비워지게 된다
var allocator = new BlockAllocator(64);

// 8 크기의 메모리를 할당한다.
// 이때 할당이 성공하면 offset에 할당된 메모리의 위치(인덱스)가 저장된다.
// 이때 할당되는 건 HeaderSize + 8 = 12의 공간이 할당된다.
// 그래서 남은 공간은 64 - 12 = 52가 된다. 이안에서 아직 Header가 존재한다.
// 따라서 담을 수 잇는 공간은 52 - HeaderSize = 48이 된다.
// 그리고 0~12가지는 Free가 false로 되고 12~63는 Free가 true로 된다.
var result = allocator.TryAlloc(8, out int offset);
Console.WriteLine($"result: {result}, offset: {offset}");
allocator.GetStats().Print();

var result2 = allocator.TryAlloc(8, out int offset2);
Console.WriteLine($"result: {result2}, offset: {offset2}");
allocator.GetStats().Print();

var result3 = allocator.TryAlloc(8, out int offset3);
Console.WriteLine($"result: {result3}, offset: {offset3}");
allocator.GetStats().Print();

var result4 = allocator.TryAlloc(8, out int offset4);
Console.WriteLine($"result: {result4}, offset: {offset4}");
allocator.GetStats().Print();

Console.WriteLine("Freeing...");
allocator.Free(offset3);
allocator.GetStats().Print();

var result5 = allocator.TryAlloc(20, out int offset5);
Console.WriteLine($"result: {result5}, offset: {offset5}");
allocator.GetStats().Print();