using System.Reflection;
using System.IO;
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
using YamlDotNet.RepresentationModel;

namespace WolvenKit.UnitTests;

[TestClass]
public class TweakXLRecordOverrideServiceTests
{
    [TestMethod]
    public void SerializeRecordOverride_KeepsNameForRecordWithoutProperties()
    {
        var service = new TweakXLRecordOverrideService(new Mock<ILocKeyService>().Object, CreateTweakDbService());
        var record = new TweakXL { ID = "UnitTests.Empty_inline0", Type = "gamedataCPOItemCategoryBase_Record" };
        var yaml = new YamlStream();
        using var reader = new StringReader(service.SerializeRecordOverride(record));
        yaml.Load(reader);
        var root = (YamlMappingNode)yaml.Documents[0].RootNode;
        Assert.AreEqual(1, root.Children.Count);
        var properties = (YamlMappingNode)root.Children[new YamlScalarNode("UnitTests.Empty_inline0")];
        Assert.AreEqual("gamedataCPOItemCategoryBase_Record", ((YamlScalarNode)properties.Children[new YamlScalarNode("$type")]).Value);
    }

    [TestMethod]
    public void SerializeRecordOverride_PreservesEmptyArrayPositions()
    {
        var service = new TweakXLRecordOverrideService(new Mock<ILocKeyService>().Object, CreateTweakDbService());
        var record = new TweakXL { ID = "UnitTests.Arrays", Type = "gamedataTest_Record" };
        record.Properties.Add("names", new CArray<CName> { "Before", CName.Empty, "After" });
        record.Properties.Add("refs", new CArray<TweakDBID> { "Items.Before", TweakDBID.Empty, "Items.After" });
        record.Properties.Add("locKeys", new CArray<gamedataLocKeyWrapper> { 1UL, 0UL, 2UL });
        var yaml = new YamlStream();
        using var reader = new StringReader(service.SerializeRecordOverride(record));
        yaml.Load(reader);
        var root = (YamlMappingNode)yaml.Documents[0].RootNode;
        var properties = (YamlMappingNode)root.Children[new YamlScalarNode("UnitTests.Arrays")];
        AssertSequence("names", "Before", "None", "After");
        AssertSequence("refs", "Items.Before", "None", "Items.After");
        AssertSequence("locKeys", "LocKey#1", "LocKey#0", "LocKey#2");

        void AssertSequence(string property, params string[] expected)
        {
            var sequence = (YamlSequenceNode)properties.Children[new YamlScalarNode(property)];
            Assert.AreEqual(expected.Length, sequence.Children.Count);
            for (var i = 0; i < expected.Length; i++)
                Assert.AreEqual(expected[i], ((YamlScalarNode)sequence.Children[i]).Value);
        }
    }

    [TestMethod]
    public void SerializeRecordOverride_StillWritesStandaloneFlatWithoutRecordWrapper()
    {
        var service = new TweakXLRecordOverrideService(new Mock<ILocKeyService>().Object, CreateTweakDbService());
        var flat = new TweakXL { ID = "UnitTests.Value", Value = (CInt32)42 };
        var yaml = new YamlStream();
        using var reader = new StringReader(service.SerializeRecordOverride(flat));
        yaml.Load(reader);
        var root = (YamlMappingNode)yaml.Documents[0].RootNode;
        Assert.AreEqual(1, root.Children.Count);
        Assert.AreEqual("42", ((YamlScalarNode)root.Children[new YamlScalarNode("UnitTests.Value")]).Value);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void SerializeRecordOverride_PreservesLocalizationKeysWithOrWithoutText(bool hasText)
    {
        const ulong key = 12345678901234567890UL;
        var locKeyService = new Mock<ILocKeyService>();
        if (hasText)
        {
            locKeyService.Setup(x => x.GetEntry(key))
                .Returns(new localizationPersistenceOnScreenEntry { PrimaryKey = key });
        }

        var service = new TweakXLRecordOverrideService(locKeyService.Object, CreateTweakDbService());
        var record = new TweakXL { ID = "UnitTests.Localization", Type = "gamedataScanningTooltip_Record" };
        record.Properties.Add("displayName", (gamedataLocKeyWrapper)key);
        record.Properties.Add("names", new CArray<gamedataLocKeyWrapper> { (gamedataLocKeyWrapper)key });

        var yaml = new YamlStream();
        using var reader = new StringReader(service.SerializeRecordOverride(record));
        yaml.Load(reader);
        var root = (YamlMappingNode)yaml.Documents[0].RootNode;
        var properties = (YamlMappingNode)root.Children[new YamlScalarNode("UnitTests.Localization")];
        Assert.AreEqual("LocKey#12345678901234567890", ((YamlScalarNode)properties.Children[new YamlScalarNode("displayName")]).Value);
        var names = (YamlSequenceNode)properties.Children[new YamlScalarNode("names")];
        Assert.AreEqual(1, names.Children.Count);
        Assert.AreEqual("LocKey#12345678901234567890", ((YamlScalarNode)names.Children[0]).Value);
    }

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
