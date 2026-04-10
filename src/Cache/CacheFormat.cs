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
        public static byte[] Serialize(XmlDocument doc)
        {
            using (var ms = new MemoryStream())
            {
                using (var gz = new GZipStream(ms, CompressionLevel.Fastest, leaveOpen: true))
                using (var writer = XmlWriter.Create(gz, new XmlWriterSettings
                {
                    Encoding = System.Text.Encoding.UTF8,
                    Indent = false,
                    OmitXmlDeclaration = false,
                }))
                {
                    doc.Save(writer);
                }
                return ms.ToArray();
            }
        }

        public static XmlDocument Deserialize(byte[] bytes)
        {
            using (var ms = new MemoryStream(bytes))
            using (var gz = new GZipStream(ms, CompressionMode.Decompress))
            using (var reader = XmlReader.Create(gz))
            {
                var doc = new XmlDocument();
                doc.Load(reader);
                return doc;
            }
        }
    }
}
