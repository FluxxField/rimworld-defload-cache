using System;
using System.IO;
using System.Text;

namespace FluxxField.DefLoadCache.Tests.Helpers
{
    /// <summary>
    /// Creates a unique temp directory for fake mod layouts in tests.
    /// Use inside a `using` block; deletes the directory on dispose.
    /// </summary>
    public sealed class FakeModFolder : IDisposable
    {
        public string RootDir { get; }

        public FakeModFolder()
        {
            RootDir = Path.Combine(
                Path.GetTempPath(),
                "deflcache-test-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(RootDir);
        }

        public void WriteFile(string relativePath, string content) =>
            WriteFile(relativePath, Encoding.UTF8.GetBytes(content));

        public void WriteFile(string relativePath, byte[] content)
        {
            string fullPath = Path.Combine(RootDir, relativePath);
            string dir = Path.GetDirectoryName(fullPath)!;
            Directory.CreateDirectory(dir);
            File.WriteAllBytes(fullPath, content);
        }

        public void WriteAbout(string xml) => WriteFile("About/About.xml", xml);

        public string LoadFolder(string relative) => Path.Combine(RootDir, relative);

        public void Dispose()
        {
            try { Directory.Delete(RootDir, recursive: true); }
            catch { /* best-effort cleanup */ }
        }
    }
}
