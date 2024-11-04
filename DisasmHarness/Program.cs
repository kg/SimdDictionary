Console.WriteLine("Hello, World!");

DisasmHarness.Dict.Add(DisasmHarness.PresentKey, default);
// Enough iterations for tiered compilation and pgo to work in disasmo
for (int i = 0; i < 1000000; i++) {
    DisasmHarness.TryAdd(DisasmHarness.Dict, DisasmHarness.PresentKey);
    DisasmHarness.TryGetValue(DisasmHarness.Dict, i, DisasmHarness.MissingKey, DisasmHarness.PresentKey);
    DisasmHarness.TryRemove(DisasmHarness.Dict, i, DisasmHarness.MissingKey, DisasmHarness.PresentKey);
    DisasmHarness.TryAdd(DisasmHarness.Dict, DisasmHarness.PresentKey);
    DisasmHarness.Clear(DisasmHarness.Dict);
}