using System.IO;
using System.IO.Compression;
using System.Xml;

namespace FluxxField.DefLoadCache
{
    /// <summary>
    /// XmlDocument to/from gzipped binary-XML byte stream. Binary XML keeps the
    /// same infoset semantics as text XML but avoids the expensive UTF-8 text
    /// parse on cache-hit launches, speeding up deserialization.
    ///
    /// No mod-attribution processing, that lives in ModAttributionTagger.
    ///
    /// Binary XML format based on work by CriDos
    /// (https://github.com/CriDos/rimworld-defload-cache).
    /// </summary>
    internal static class CacheFormat
    {
        /// <summary>
        /// Streams the XmlDocument directly to a gzipped binary-XML file on disk.
        /// No intermediate byte[] allocation, so memory usage stays flat
        /// even for large documents.
        /// Returns the compressed file size in bytes.
        /// </summary>
        public static long SerializeToFile(XmlDocument doc, string filePath)
        {
            using (var fs = new FileStream(filePath, FileMode.Create, FileAccess.Write, FileShare.None, bufferSize: 65536))
            using (var gz = new GZipStream(fs, CompressionLevel.Fastest))
            using (var writer = XmlDictionaryWriter.CreateBinaryWriter(gz))
            {
                doc.Save(writer);
                writer.Flush();
                gz.Flush();
                return fs.Length;
            }
        }

        /// <summary>
        /// Loads a gzipped binary-XML cache stream directly into an existing
        /// XmlDocument. Uses XmlDictionaryReader for fast binary parsing.
        /// </summary>
        public static void LoadInto(XmlDocument doc, Stream gzipStream)
        {
            using (var reader = XmlDictionaryReader.CreateBinaryReader(gzipStream, XmlDictionaryReaderQuotas.Max))
            {
                doc.Load(reader);
            }
        }
    }
}
