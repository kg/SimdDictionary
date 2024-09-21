Console.WriteLine("Hello, World!");

// Enough iterations for tiered compilation and pgo to work in disasmo
for (int i = 0; i < 1000000; i++) {
    DisasmHarness.TryAdd();
    DisasmHarness.TryGetValue();
    DisasmHarness.TryRemove();
    DisasmHarness.TryAdd();
    DisasmHarness.Clear();
    DisasmHarness.VectorLicm(unchecked((byte)i));
}