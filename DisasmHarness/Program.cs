Console.WriteLine("Hello, World!");

DisasmHarness.Dict.Add(DisasmHarness.PresentKey, default);
// Enough iterations for tiered compilation and pgo to work in disasmo
for (int i = 0; i < 1000000; i++) {
    DisasmHarness.TryAdd();
    DisasmHarness.TryGetValue(i);
    DisasmHarness.TryRemove(i);
    DisasmHarness.TryAdd();
    DisasmHarness.Clear();
    DisasmHarness.VectorLicm(unchecked((byte)i));
}