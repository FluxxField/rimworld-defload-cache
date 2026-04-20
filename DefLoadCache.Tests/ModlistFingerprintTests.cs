using System.Collections.Generic;
using FluxxField.DefLoadCache.Tests.Helpers;
using Xunit;

namespace FluxxField.DefLoadCache.Tests
{
    public class ModlistFingerprintTests
    {
        [Fact]
        public void BuildModFragmentFromDisk_EmptyMod_ReturnsNonEmptyString()
        {
            using var mod = new FakeModFolder();
            mod.WriteAbout("<ModMetaData><name>Empty</name></ModMetaData>");

            var loadFolders = new List<string>();
            string fragment = ModlistFingerprint.BuildModFragmentFromDisk(
                "test.empty", mod.RootDir, loadFolders);

            Assert.False(string.IsNullOrEmpty(fragment));
        }

        [Fact]
        public void VersionScopedDllChange_InvalidatesFragment()
        {
            using var mod = new FakeModFolder();
            mod.WriteAbout("<ModMetaData><name>T</name></ModMetaData>");
            mod.WriteFile("1.6/Assemblies/foo.dll", new byte[] { 0x01, 0x02 });

            var loadFolders = new List<string> { mod.LoadFolder("1.6") };
            string before = ModlistFingerprint.BuildModFragmentFromDisk(
                "test.versioned", mod.RootDir, loadFolders);

            mod.WriteFile("1.6/Assemblies/foo.dll", new byte[] { 0xAA, 0xBB, 0xCC });
            string after = ModlistFingerprint.BuildModFragmentFromDisk(
                "test.versioned", mod.RootDir, loadFolders);

            Assert.NotEqual(before, after);
        }

        [Fact]
        public void AboutXmlChange_InvalidatesFragment()
        {
            using var mod = new FakeModFolder();
            mod.WriteAbout("<ModMetaData><name>T</name><modDependencies/></ModMetaData>");

            var loadFolders = new List<string> { mod.LoadFolder("1.6") };
            string before = ModlistFingerprint.BuildModFragmentFromDisk(
                "test.about", mod.RootDir, loadFolders);

            // About.xml is fingerprinted as a whole file, so any edit — including
            // non-modVersion attributes like modDependencies — must change the fragment.
            mod.WriteAbout("<ModMetaData><name>T</name><modDependencies><li><packageId>x.y</packageId></li></modDependencies></ModMetaData>");
            string after = ModlistFingerprint.BuildModFragmentFromDisk(
                "test.about", mod.RootDir, loadFolders);

            Assert.NotEqual(before, after);
        }

        [Fact]
        public void RootScopedDllChange_InvalidatesFragment()
        {
            using var mod = new FakeModFolder();
            mod.WriteAbout("<ModMetaData />");
            mod.WriteFile("Assemblies/foo.dll", new byte[] { 0x01 });

            var loadFolders = new List<string>();
            string before = ModlistFingerprint.BuildModFragmentFromDisk(
                "test.root", mod.RootDir, loadFolders);

            mod.WriteFile("Assemblies/foo.dll", new byte[] { 0x99 });
            string after = ModlistFingerprint.BuildModFragmentFromDisk(
                "test.root", mod.RootDir, loadFolders);

            Assert.NotEqual(before, after);
        }

        [Fact]
        public void BothRootAndVersionAssemblies_BothFingerprinted()
        {
            using var mod = new FakeModFolder();
            mod.WriteAbout("<ModMetaData />");
            mod.WriteFile("Assemblies/legacy.dll", new byte[] { 0x01 });
            mod.WriteFile("1.6/Assemblies/modern.dll", new byte[] { 0x02 });

            var loadFolders = new List<string> { mod.LoadFolder("1.6") };
            string baseline = ModlistFingerprint.BuildModFragmentFromDisk(
                "test.both", mod.RootDir, loadFolders);

            // Mutate legacy dll only -> fragment must change
            mod.WriteFile("Assemblies/legacy.dll", new byte[] { 0xAA });
            string afterLegacy = ModlistFingerprint.BuildModFragmentFromDisk(
                "test.both", mod.RootDir, loadFolders);
            Assert.NotEqual(baseline, afterLegacy);

            // Mutate modern dll only (rest restored) -> fragment must change
            mod.WriteFile("Assemblies/legacy.dll", new byte[] { 0x01 });
            mod.WriteFile("1.6/Assemblies/modern.dll", new byte[] { 0xBB });
            string afterModern = ModlistFingerprint.BuildModFragmentFromDisk(
                "test.both", mod.RootDir, loadFolders);
            Assert.NotEqual(baseline, afterModern);
        }

        [Fact]
        public void IdenticalLayouts_ProduceIdenticalFragments()
        {
            using var mod = new FakeModFolder();
            mod.WriteAbout("<ModMetaData><name>X</name></ModMetaData>");
            mod.WriteFile("1.6/Defs/Things.xml", "<Defs />");
            mod.WriteFile("1.6/Patches/p.xml", "<Patch />");
            mod.WriteFile("1.6/Assemblies/a.dll", new byte[] { 0x01 });

            var loadFolders = new List<string> { mod.LoadFolder("1.6") };
            string a = ModlistFingerprint.BuildModFragmentFromDisk("p.id", mod.RootDir, loadFolders);
            string b = ModlistFingerprint.BuildModFragmentFromDisk("p.id", mod.RootDir, loadFolders);

            Assert.Equal(a, b);
        }

        [Fact]
        public void DefsFileChange_InvalidatesFragment()
        {
            using var mod = new FakeModFolder();
            mod.WriteAbout("<ModMetaData />");
            mod.WriteFile("1.6/Defs/Things.xml", "<Defs>v1</Defs>");

            var loadFolders = new List<string> { mod.LoadFolder("1.6") };
            string before = ModlistFingerprint.BuildModFragmentFromDisk("p.id", mod.RootDir, loadFolders);

            mod.WriteFile("1.6/Defs/Things.xml", "<Defs>v2-much-longer-content</Defs>");
            string after = ModlistFingerprint.BuildModFragmentFromDisk("p.id", mod.RootDir, loadFolders);

            Assert.NotEqual(before, after);
        }

        [Fact]
        public void PatchesFileChange_InvalidatesFragment()
        {
            using var mod = new FakeModFolder();
            mod.WriteAbout("<ModMetaData />");
            mod.WriteFile("1.6/Patches/p.xml", "<Patch />");

            var loadFolders = new List<string> { mod.LoadFolder("1.6") };
            string before = ModlistFingerprint.BuildModFragmentFromDisk("p.id", mod.RootDir, loadFolders);

            mod.WriteFile("1.6/Patches/p.xml", "<Patch>updated content here</Patch>");
            string after = ModlistFingerprint.BuildModFragmentFromDisk("p.id", mod.RootDir, loadFolders);

            Assert.NotEqual(before, after);
        }

        [Fact]
        public void FileAdded_InvalidatesFragment()
        {
            using var mod = new FakeModFolder();
            mod.WriteAbout("<ModMetaData />");

            var loadFolders = new List<string> { mod.LoadFolder("1.6") };
            string before = ModlistFingerprint.BuildModFragmentFromDisk("p.id", mod.RootDir, loadFolders);

            mod.WriteFile("1.6/Defs/NewFile.xml", "<Defs />");
            string after = ModlistFingerprint.BuildModFragmentFromDisk("p.id", mod.RootDir, loadFolders);

            Assert.NotEqual(before, after);
        }

        [Fact]
        public void FileRemoved_InvalidatesFragment()
        {
            using var mod = new FakeModFolder();
            mod.WriteAbout("<ModMetaData />");
            mod.WriteFile("1.6/Defs/ToDelete.xml", "<Defs />");

            var loadFolders = new List<string> { mod.LoadFolder("1.6") };
            string before = ModlistFingerprint.BuildModFragmentFromDisk("p.id", mod.RootDir, loadFolders);

            System.IO.File.Delete(System.IO.Path.Combine(mod.RootDir, "1.6/Defs/ToDelete.xml"));
            string after = ModlistFingerprint.BuildModFragmentFromDisk("p.id", mod.RootDir, loadFolders);

            Assert.NotEqual(before, after);
        }

        [Fact]
        public void SameContentDifferentMtime_FragmentUnchanged()
        {
            using var mod = new FakeModFolder();
            mod.WriteAbout("<ModMetaData />");
            mod.WriteFile("1.6/Defs/Things.xml", "<Defs />");

            var loadFolders = new List<string> { mod.LoadFolder("1.6") };
            string before = ModlistFingerprint.BuildModFragmentFromDisk("p.id", mod.RootDir, loadFolders);

            // Bump mtime without changing content (the Steam re-download case).
            string defPath = System.IO.Path.Combine(mod.RootDir, "1.6/Defs/Things.xml");
            System.IO.File.SetLastWriteTimeUtc(defPath, System.DateTime.UtcNow.AddHours(1));

            string after = ModlistFingerprint.BuildModFragmentFromDisk("p.id", mod.RootDir, loadFolders);

            Assert.Equal(before, after);
        }

        [Fact]
        public void DifferentContentSameMtime_FragmentChanges()
        {
            using var mod = new FakeModFolder();
            mod.WriteAbout("<ModMetaData />");
            // Use same-length payloads so byteLength doesn't pick up the slack —
            // this test is specifically about content-vs-content detection.
            mod.WriteFile("1.6/Defs/Things.xml", "<Defs>v1</Defs>");

            var loadFolders = new List<string> { mod.LoadFolder("1.6") };
            string defPath = System.IO.Path.Combine(mod.RootDir, "1.6/Defs/Things.xml");
            var originalMtime = System.IO.File.GetLastWriteTimeUtc(defPath);

            string before = ModlistFingerprint.BuildModFragmentFromDisk("p.id", mod.RootDir, loadFolders);

            // Same length, different bytes (both writes use FakeModFolder.WriteFile
            // which encodes UTF-8 without a BOM, so bytes:N is identical).
            mod.WriteFile("1.6/Defs/Things.xml", "<Defs>v9</Defs>");
            System.IO.File.SetLastWriteTimeUtc(defPath, originalMtime);

            string after = ModlistFingerprint.BuildModFragmentFromDisk("p.id", mod.RootDir, loadFolders);

            Assert.NotEqual(before, after);
        }

        [Fact]
        public void TexturesFolderChange_FragmentUnchanged()
        {
            using var mod = new FakeModFolder();
            mod.WriteAbout("<ModMetaData />");
            mod.WriteFile("Textures/icon.png", new byte[] { 0x01, 0x02, 0x03 });

            var loadFolders = new List<string> { mod.LoadFolder("1.6") };
            string before = ModlistFingerprint.BuildModFragmentFromDisk("p.id", mod.RootDir, loadFolders);

            // Modify the texture — should NOT affect the fragment, since textures
            // are outside the def-loading pipeline.
            mod.WriteFile("Textures/icon.png", new byte[] { 0xAA, 0xBB, 0xCC, 0xDD });
            string after = ModlistFingerprint.BuildModFragmentFromDisk("p.id", mod.RootDir, loadFolders);

            Assert.Equal(before, after);
        }

        [Fact]
        public void LoadFolderReordering_ChangesFragment()
        {
            using var mod = new FakeModFolder();
            mod.WriteAbout("<ModMetaData />");
            mod.WriteFile("1.6/Defs/A.xml", "<Defs />");
            mod.WriteFile("Common/Defs/B.xml", "<Defs />");

            string a = ModlistFingerprint.BuildModFragmentFromDisk(
                "p.id", mod.RootDir,
                new List<string> { mod.LoadFolder("Common"), mod.LoadFolder("1.6") });
            string b = ModlistFingerprint.BuildModFragmentFromDisk(
                "p.id", mod.RootDir,
                new List<string> { mod.LoadFolder("1.6"), mod.LoadFolder("Common") });

            Assert.NotEqual(a, b);
        }
    }
}
