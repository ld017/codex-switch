using System.Text;
using CodexSwitch.Core.Files;

namespace CodexSwitch.Core.Tests.Files;

public sealed class AtomicFileStoreTests
{
    [Fact]
    public void WriteAtomically_replaces_existing_bytes_and_removes_temporary_file()
    {
        using var directory = new TestDirectory();
        var path = directory.File("config.toml");
        File.WriteAllText(path, "old", Encoding.UTF8);
        var store = new AtomicFileStore();

        store.WriteAtomically(path, Encoding.UTF8.GetBytes("new"));

        Assert.Equal("new", File.ReadAllText(path, Encoding.UTF8));
        Assert.Empty(Directory.GetFiles(directory.Path, "*.tmp"));
    }
}
