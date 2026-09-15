namespace CodexSwitch.Core.Files;

public interface IAtomicFileStore
{
    bool Exists(string path);

    byte[] ReadAllBytes(string path);

    void WriteAtomically(string path, byte[] contents);

    void Delete(string path);
}
