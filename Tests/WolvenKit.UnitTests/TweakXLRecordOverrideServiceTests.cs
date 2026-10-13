using System.Reflection;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Moq;
using WolvenKit.App.Models;
using WolvenKit.App.Services;
using WolvenKit.Common.Services;
using WolvenKit.Core.Extensions;
using WolvenKit.RED4.TweakDB;
using WolvenKit.RED4.Types;
using YamlDotNet.Serialization;

namespace WolvenKit.UnitTests;

[TestClass]
public class TweakXLRecordOverrideServiceTests
{
    [TestMethod]
    public void BuildRecordOverride_MatchesExistingSingleRecordYaml()
    {
        var recordName = "UnitTests.Quality";
        var recordId = (TweakDBID)recordName;
        var originalDb = SetTweakDb(CreateTweakDb(recordName));

        try
        {
            var tweakDbService = CreateTweakDbService();
            var locKeyService = new Mock<ILocKeyService>().Object;
            var service = new TweakXLRecordOverrideService(locKeyService, tweakDbService);

            var txl = service.BuildRecordOverride(recordId);
            var legacyTxl = BuildRecordOverrideUsingLegacyBrowserLogic(recordId);

            Assert.AreEqual(recordId, txl.ID);
            Assert.AreEqual("gamedataQuality_Record", txl.Type.ToString());
            Assert.AreEqual(legacyTxl.Properties.Count, txl.Properties.Count);
            Assert.AreEqual(
                SerializeUsingLegacyBrowserLogic(legacyTxl, locKeyService, tweakDbService),
                service.SerializeRecordOverride(txl));
        }
        finally
        {
            SetTweakDb(originalDb);
        }
    }

    private static TweakDB CreateTweakDb(string recordName)
    {
        var db = new TweakDB();
        db.Add(recordName, new gamedataQuality_Record
        {
            Name = "UnitTestQuality",
            Value = 7
        });

        return db;
    }

    private static TweakDBService CreateTweakDbService()
    {
        var hashService = new Mock<IHashService>();
        hashService.SetupGet(x => x.Loaded).Returns(Task.CompletedTask);

        return new TweakDBService(hashService.Object);
    }

    private static TweakDB SetTweakDb(TweakDB db)
    {
        var field = typeof(TweakDBService).GetField("s_tweakDb", BindingFlags.NonPublic | BindingFlags.Static).NotNull();
        var originalDb = ((TweakDB?)field.GetValue(null)).NotNull();
        field.SetValue(null, db);

        return originalDb;
    }

    private static TweakXL BuildRecordOverrideUsingLegacyBrowserLogic(TweakDBID recordId)
    {
        var txl = new TweakXL
        {
            ID = recordId
        };

        var baseRecord = TweakDBService.GetRecord(recordId).NotNull();
        txl.Type = TweakDBService.TryGetType(recordId, out var type)
            ? "gamedata" + type.Name[8..^7] + "_Record"
            : "gamedata_Record";

        baseRecord.GetPropertyNames().ForEach(name => txl.Properties.Add(name, baseRecord.GetProperty(name).NotNull()));

        return txl;
    }

    private static string SerializeUsingLegacyBrowserLogic(TweakXL txl, ILocKeyService locKeyService, ITweakDBService tweakDbService)
    {
        var serializer = new SerializerBuilder()
            .WithTypeConverter(new TweakXLYamlTypeConverter(locKeyService, tweakDbService))
            .WithIndentedSequences()
            .Build();

        return serializer.Serialize(new TweakXLFile { txl });
    }
}
