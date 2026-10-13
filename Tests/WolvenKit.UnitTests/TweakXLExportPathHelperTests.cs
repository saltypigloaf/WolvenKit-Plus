using System.IO;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using WolvenKit.App.Helpers;

namespace WolvenKit.UnitTests;

[TestClass]
public class TweakXLExportPathHelperTests
{
    [TestMethod]
    public void GetRelativePath_NormalTwoSegmentName_UsesFirstSegmentFolder()
    {
        var relativePath = TweakXLExportPathHelper.GetRelativePath("Character.ServiceSpecialistMale");

        Assert.AreEqual(
            Path.Combine("Character", "Character.ServiceSpecialistMale.yaml"),
            relativePath);
    }

    [TestMethod]
    public void GetRelativePath_MultiDotName_UsesOnlyFirstSegmentFolder()
    {
        var relativePath = TweakXLExportPathHelper.GetRelativePath("Some.Prefix.With.Many.Parts");

        Assert.AreEqual(
            Path.Combine("Some", "Some.Prefix.With.Many.Parts.yaml"),
            relativePath);
    }

    [TestMethod]
    public void GetRelativePath_NameWithoutDots_UsesWholeNameForFolderAndStem()
    {
        var relativePath = TweakXLExportPathHelper.GetRelativePath("SingleSegment");

        Assert.AreEqual(
            Path.Combine("SingleSegment", "SingleSegment.yaml"),
            relativePath);
    }

    [TestMethod]
    public void GetRelativePath_InvalidWindowsCharacters_ReplacesWithUnderscores()
    {
        var relativePath = TweakXLExportPathHelper.GetRelativePath("Bad:Folder.Some*Record?Name");

        Assert.AreEqual(
            Path.Combine("Bad_Folder", "Bad_Folder.Some_Record_Name.yaml"),
            relativePath);
    }

    [TestMethod]
    public void GetRelativePath_TrailingSpacesAndDots_TrimsPathSegments()
    {
        var relativePath = TweakXLExportPathHelper.GetRelativePath("Trailing .SpaceDot. ");

        Assert.AreEqual(
            Path.Combine("Trailing", "Trailing.SpaceDot.yaml"),
            relativePath);
    }

    [DataTestMethod]
    [DataRow("CON.Record", "_.Record.yaml")]
    [DataRow("prn.Record", "_.Record.yaml")]
    [DataRow("COM1.Record", "_.Record.yaml")]
    [DataRow("LPT9.Record", "_.Record.yaml")]
    public void GetRelativePath_ReservedWindowsNames_ReplacesReservedSegments(string recordName, string expectedFileName)
    {
        var relativePath = TweakXLExportPathHelper.GetRelativePath(recordName);

        Assert.AreEqual(Path.Combine("_", expectedFileName), relativePath);
    }

    [TestMethod]
    public void GetRelativePath_PathTraversalAttempts_RemainRelativeAndSanitized()
    {
        var relativePath = TweakXLExportPathHelper.GetRelativePath(@"../Secret.Record");

        Assert.AreEqual(Path.Combine("_", "_._._Secret.Record.yaml"), relativePath);
        Assert.IsFalse(Path.IsPathFullyQualified(relativePath));
        Assert.IsFalse(relativePath.Split(Path.DirectorySeparatorChar).Contains(".."));
    }

    [TestMethod]
    public void GetRelativePaths_CaseInsensitiveCollisions_AddsSuffixToLaterCollision()
    {
        var results = TweakXLExportPathHelper.GetRelativePaths(
        [
            new TweakXLExportPathRecord("Character.Record", 0x101),
            new TweakXLExportPathRecord("character.record", 0x202)
        ]);

        Assert.AreEqual(Path.Combine("Character", "Character.Record.yaml"), results[0].RelativePath);
        Assert.AreEqual(Path.Combine("character", "character.record_0x0000000000000202.yaml"), results[1].RelativePath);
    }

    [TestMethod]
    public void GetRelativePaths_CollisionSuffix_IsStableAndUsesRecordIdWhenAvailable()
    {
        TweakXLExportPathRecord[] records =
        [
            new("Ability.SomeAbilityName", 0xA),
            new("ability.someabilityname", 0xB)
        ];

        var firstRun = TweakXLExportPathHelper.GetRelativePaths(records);
        var secondRun = TweakXLExportPathHelper.GetRelativePaths(records);

        Assert.AreEqual(firstRun[1].RelativePath, secondRun[1].RelativePath);
        Assert.AreEqual(
            Path.Combine("ability", "ability.someabilityname_0x000000000000000B.yaml"),
            firstRun[1].RelativePath);
    }
}
