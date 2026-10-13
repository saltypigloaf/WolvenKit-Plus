using System;
using System.IO;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Moq;
using WolvenKit.Common.Services;

namespace WolvenKit.UnitTests;

[TestClass]
public class TweakDBServiceTests
{
    [TestMethod]
    public async Task LoadDB_WhenCalledWithPath_SetsSourcePath()
    {
        var path = Path.Combine(Path.GetTempPath(), $"wolvenkit_tweakdb_{Guid.NewGuid():N}.bin");

        try
        {
            await File.WriteAllBytesAsync(path, [0x00, 0x00, 0x00, 0x00]);

            var service = CreateTweakDbService();

            await service.LoadDB(path);

            Assert.AreEqual(path, service.SourcePath);
            Assert.IsFalse(service.IsLoaded);
        }
        finally
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }

    private static TweakDBService CreateTweakDbService()
    {
        var hashService = new Mock<IHashService>();
        hashService.SetupGet(x => x.Loaded).Returns(Task.CompletedTask);

        return new TweakDBService(hashService.Object);
    }
}
