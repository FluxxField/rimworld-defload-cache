using System.Collections.Generic;
using System.Xml;
using Verse;

namespace FluxxField.DefLoadCache
{
    /// <summary>
    /// Embeds source-mod packageId attributes onto top-level def nodes when
    /// caching. Stage D will use RebuildAssetLookup to rebuild the assetlookup
    /// dictionary from those attributes on cache hit.
    ///
    /// Stage C only uses StampAttributions (the write-path helper). Stage D
    /// will add RebuildAssetLookup (the read-path helper).
    /// </summary>
    internal static class ModAttributionTagger
    {
        /// <summary>The attribute name we stamp on each top-level def node.</summary>
        public const string AttributeName = "data-defloadcache-mod";

        /// <summary>
        /// Walks the merged doc's top-level def nodes and stamps each with a
        /// data-defloadcache-mod attribute pulled from the existing assetlookup.
        /// Mutates the doc in place. Call BEFORE serialization.
        ///
        /// Nodes that have no entry in assetlookup (e.g. nodes inserted by
        /// patches that don't register with the lookup) are left unstamped.
        /// Stage D's RebuildAssetLookup will handle unstamped nodes gracefully
        /// by leaving them out of the rebuilt assetlookup — ParseAndProcessXML
        /// passes null to XmlInheritance.TryRegister for those, which is the
        /// same behavior as vanilla when the lookup misses.
        /// </summary>
        public static void StampAttributions(XmlDocument doc, Dictionary<XmlNode, LoadableXmlAsset> assetlookup)
        {
            if (doc?.DocumentElement == null) return;

            int stamped = 0;
            int missing = 0;

            // Snapshot the child nodes first because we're mutating attributes
            // while iterating — not strictly required for attribute edits, but
            // cheap insurance against XmlNodeList lazy evaluation quirks.
            var children = new List<XmlNode>();
            foreach (XmlNode node in doc.DocumentElement.ChildNodes)
            {
                children.Add(node);
            }

            foreach (var node in children)
            {
                if (node.NodeType != XmlNodeType.Element) continue;
                if (!(node is XmlElement element)) continue;

                if (assetlookup.TryGetValue(node, out var asset) && asset?.mod?.PackageId != null)
                {
                    element.SetAttribute(AttributeName, asset.mod.PackageId);
                    stamped++;
                }
                else
                {
                    missing++;
                }
            }

            Log.Message($"ModAttributionTagger: stamped {stamped} nodes, {missing} had no mod attribution");
        }
    }
}
