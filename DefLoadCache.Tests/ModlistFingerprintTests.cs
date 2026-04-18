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

            mod.WriteFile("1.6/Assemblies/foo.dll", new byte[] { 0xAA, 0xBB });
            string after = ModlistFingerprint.BuildModFragmentFromDisk(
                "test.versioned", mod.RootDir, loadFolders);

            Assert.NotEqual(before, after);
        }
    }
}
