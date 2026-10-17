namespace CodexSwitch.Core.Files;

public interface IAtomicFileStore
{
    bool Exists(string path);

    byte[] ReadAllBytes(string path);

    byte[] ReadBounded(string path, int maximumBytes)
    {
        var bytes = ReadAllBytes(path);
        return bytes.Length <= maximumBytes ? bytes : throw new InvalidDataException("File exceeds the permitted size.");
    }

    void WriteAtomically(string path, byte[] contents);

    void Delete(string path);
}
