using System.IO;
using IrdLibraryClient.IrdFormat;
using NUnit.Framework;

namespace Tests
{
    [TestFixture]
    public class IrdTests
    {
        [Test]
        public void ParsingTest()
        {
            var baseDir = TestContext.CurrentContext.TestDirectory;
            var testFiles = Directory.GetFiles(baseDir, "*.ird", SearchOption.AllDirectories);
            Assert.That(testFiles.Length, Is.GreaterThan(0));

            foreach (var file in testFiles)
            {
                var bytes = File.ReadAllBytes(file);
                Assert.That(() => IrdParser.Parse(bytes), Throws.Nothing);
            }
        }
    }
}
