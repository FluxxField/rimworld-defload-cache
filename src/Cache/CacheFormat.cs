using System.IO;
using System.IO.Compression;
using System.Xml;

namespace FluxxField.DefLoadCache
{
    /// <summary>
    /// XmlDocument &lt;-&gt; gzipped UTF-8 byte stream. No mod-attribution
    /// processing, that lives in ModAttributionTagger.
    /// </summary>
    internal static class CacheFormat
    {
        /// <summary>
        /// Streams the XmlDocument directly to a gzipped file on disk.
        /// No intermediate byte[] allocation, so memory usage stays flat
        /// even for large documents (50-100MB uncompressed).
        /// Returns the compressed file size in bytes.
        /// </summary>
        public static long SerializeToFile(XmlDocument doc, string filePath)
        {
            using (var fs = new FileStream(filePath, FileMode.Create, FileAccess.Write, FileShare.None, bufferSize: 65536))
            using (var gz = new GZipStream(fs, CompressionLevel.Fastest))
            using (var writer = XmlWriter.Create(gz, new XmlWriterSettings
            {
                Encoding = System.Text.Encoding.UTF8,
                Indent = false,
                OmitXmlDeclaration = false,
            }))
            {
                doc.Save(writer);
                writer.Flush();
                gz.Flush();
                return fs.Length;
            }
        }
    }
}
