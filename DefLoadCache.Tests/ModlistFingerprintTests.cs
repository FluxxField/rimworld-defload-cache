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
    }
}
