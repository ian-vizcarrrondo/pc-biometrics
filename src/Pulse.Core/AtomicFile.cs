using System.Text;

namespace Pulse.Core;

public static class AtomicFile
{
    /// <summary>Writes to a temp file in the same folder, then swaps it into place so a crash never leaves a half-written file.</summary>
    public static void WriteAllText(string path, string content)
    {
        var dir = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(dir);
        var tmp = Path.Combine(dir, Path.GetFileName(path) + "." + Guid.NewGuid().ToString("N")[..8] + ".tmp");
        try
        {
            File.WriteAllText(tmp, content, new UTF8Encoding(false));
            if (File.Exists(path)) File.Replace(tmp, path, null);
            else File.Move(tmp, path);
        }
        finally
        {
            if (File.Exists(tmp)) try { File.Delete(tmp); } catch (IOException) { }
        }
    }
}
