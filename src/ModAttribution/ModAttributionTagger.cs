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

        /// <summary>
        /// Rebuilds the assetlookup dictionary from the data-defloadcache-mod
        /// attributes embedded on top-level def nodes during caching. Used by
        /// TryLoadCached on cache hit to reproduce the mod-attribution state
        /// that CombineIntoUnifiedXML would have built during a normal load.
        ///
        /// Reuses REAL LoadableXmlAsset instances from the original assetlookup
        /// parameter (passed in BEFORE the doc is mutated). We can't construct
        /// synthetic LoadableXmlAssets because its fields are readonly. So we
        /// walk the existing lookup first to build a packageId → LoadableXmlAsset
        /// map, then after the doc is replaced, look up each new node's
        /// LoadableXmlAsset by its attribute.
        ///
        /// Returns the count of successfully-mapped nodes.
        /// </summary>
        public static int RebuildAssetLookup(XmlDocument doc, Dictionary<XmlNode, LoadableXmlAsset> assetlookup, Dictionary<string, LoadableXmlAsset> packageIdToAsset)
        {
            if (doc?.DocumentElement == null) return 0;

            int rebuilt = 0;
            int stripped = 0;
            int missingMod = 0;

            foreach (XmlNode node in doc.DocumentElement.ChildNodes)
            {
                if (node.NodeType != XmlNodeType.Element) continue;
                if (!(node is XmlElement element)) continue;

                string packageId = element.GetAttribute(AttributeName);

                // Strip our cache attribute so it doesn't pollute the live doc
                // that ParseAndProcessXML will read. RimWorld's DirectXmlToObject
                // generally ignores unknown attributes but removing ours is
                // strictly safer.
                if (!string.IsNullOrEmpty(packageId))
                {
                    element.RemoveAttribute(AttributeName);
                    stripped++;
                }
                else
                {
                    continue;
                }

                if (packageIdToAsset.TryGetValue(packageId, out var asset))
                {
                    assetlookup[node] = asset;
                    rebuilt++;
                }
                else
                {
                    missingMod++;
                }
            }

            Log.Message($"ModAttributionTagger: stripped {stripped} cache attributes, rebuilt {rebuilt} assetlookup entries, {missingMod} packageIds not found in live load");
            return rebuilt;
        }
    }
}
