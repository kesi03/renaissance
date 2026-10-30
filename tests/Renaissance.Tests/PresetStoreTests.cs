using Renaissance.PresetEngine;
using Xunit;

namespace Renaissance.Tests;

public sealed class PresetStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "renaissance-tests-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }

    private static RPreset Make(string name) => new()
    {
        Name = name, PluginName = "Synth: One", Vendor = "Acme/Co", UniqueId = 99,
        Parameters = [0.1f, 0.9f], Chunk = [1, 2, 3], Tags = ["pad"],
    };

    [Fact]
    public void Save_list_load_roundtrip()
    {
        var store = new PresetStore(_root);
        var path = store.Save(Make("Lead"));
        var listed = store.List("Acme/Co", "Synth: One", 99);

        Assert.Single(listed);
        Assert.Equal("Lead", listed[0].Name);
        var loaded = store.Load(path);
        Assert.Equal([0.1f, 0.9f], loaded.Parameters);
        Assert.Equal(new byte[] { 1, 2, 3 }, loaded.Chunk);
        Assert.Contains("pad", loaded.Tags);
    }

    [Fact]
    public void Rename_moves_file_and_updates_name()
    {
        var store = new PresetStore(_root);
        var path = store.Save(Make("Old"));
        var renamed = store.Rename(path, "New");

        Assert.False(File.Exists(path));
        Assert.Equal("New", store.Load(renamed).Name);
    }

    [Fact]
    public void Rename_refuses_to_overwrite_existing()
    {
        var store = new PresetStore(_root);
        var a = store.Save(Make("A"));
        store.Save(Make("B"));
        Assert.Throws<IOException>(() => store.Rename(a, "B"));
    }

    [Fact]
    public void Delete_removes_file()
    {
        var store = new PresetStore(_root);
        var path = store.Save(Make("Gone"));
        store.Delete(path);
        Assert.Empty(store.List("Acme/Co", "Synth: One", 99));
    }

    [Fact]
    public void Recent_list_is_ordered_unique_and_capped()
    {
        var store = new PresetStore(_root);
        var paths = Enumerable.Range(0, 12).Select(i => store.Save(Make("P" + i))).ToList();
        foreach (var p in paths) store.AddRecent(99, p, max: 8);
        store.AddRecent(99, paths[5], max: 8);

        var recent = store.GetRecent(99);
        Assert.Equal(8, recent.Count);
        Assert.Equal(paths[5], recent[0]);
        Assert.Equal(recent.Count, recent.Distinct().Count());
    }

    [Theory]
    [InlineData("a/b:c", "a_b_c")]
    [InlineData("  ", "Unnamed")]
    [InlineData("name.", "name")]
    public void Sanitize_produces_safe_names(string input, string expected) =>
        Assert.Equal(expected, PresetStore.Sanitize(input));
}
