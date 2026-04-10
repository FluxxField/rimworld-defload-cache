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

            Log.Message($"Stamped {stamped} defs with mod attribution ({missing} unattributed)");
        }

        /// <summary>
        /// Strips data-defloadcache-mod attributes from all top-level def nodes.
        /// Called after serialization so the live doc passed to ParseAndProcessXML
        /// on cache-miss runs is identical to the doc on cache-hit runs (where
        /// RebuildAssetLookup strips them during deserialization).
        /// </summary>
        public static void UnstampAttributions(XmlDocument doc)
        {
            if (doc?.DocumentElement == null) return;

            foreach (XmlNode node in doc.DocumentElement.ChildNodes)
            {
                if (node.NodeType != XmlNodeType.Element) continue;
                if (!(node is XmlElement element)) continue;
                if (element.HasAttribute(AttributeName))
                {
                    element.RemoveAttribute(AttributeName);
                }
            }
        }

        /// <summary>
        /// Rebuilds the assetlookup dictionary from the data-defloadcache-mod
        /// attributes embedded on top-level def nodes during caching. Also
        /// populates countsByMod with per-packageId node counts for post-load
        /// validation (counts are collected before attributes are stripped).
        ///
        /// Returns the count of successfully-mapped assetlookup entries.
        /// </summary>
        public static int RebuildAssetLookup(
            XmlDocument doc,
            Dictionary<XmlNode, LoadableXmlAsset> assetlookup,
            Dictionary<string, LoadableXmlAsset> packageIdToAsset,
            out Dictionary<string, int> countsByMod)
        {
            countsByMod = new Dictionary<string, int>();
            if (doc?.DocumentElement == null) return 0;

            int rebuilt = 0;
            int stripped = 0;
            int missingMod = 0;

            foreach (XmlNode node in doc.DocumentElement.ChildNodes)
            {
                if (node.NodeType != XmlNodeType.Element) continue;
                if (!(node is XmlElement element)) continue;

                string packageId = element.GetAttribute(AttributeName);

                // Count before stripping — this feeds CacheValidator
                if (!string.IsNullOrEmpty(packageId))
                {
                    if (countsByMod.ContainsKey(packageId))
                        countsByMod[packageId]++;
                    else
                        countsByMod[packageId] = 1;
                }

                // Strip our cache attribute so it doesn't pollute the live doc
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

            Log.Message($"Rebuilt {rebuilt} def attributions from cache ({missingMod} mods not found in live load)");
            return rebuilt;
        }
    }
}
