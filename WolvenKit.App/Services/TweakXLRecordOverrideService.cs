using System;
using WolvenKit.App.Models;
using WolvenKit.Common.Services;
using WolvenKit.Core.Extensions;
using WolvenKit.RED4.Types;
using YamlDotNet.Serialization;

namespace WolvenKit.App.Services;

public class TweakXLRecordOverrideService : ITweakXLRecordOverrideService
{
    private readonly ILocKeyService _locKeyService;
    private readonly ITweakDBService _tweakDBService;

    public TweakXLRecordOverrideService(ILocKeyService locKeyService, ITweakDBService tweakDBService)
    {
        _locKeyService = locKeyService;
        _tweakDBService = tweakDBService;
    }

    public TweakXL BuildRecordOverride(TweakDBID recordId)
    {
        var txl = new TweakXL
        {
            ID = recordId
        };

        var baseRecord = TweakDBService.GetRecord(recordId).NotNull();
        var recordTypeName = TweakDBService.TryGetType(recordId, out var type)
            ? type.Name[8..^7]
            : null;

        txl.Type = "gamedata" + recordTypeName + "_Record";

        baseRecord.GetPropertyNames().ForEach(name => txl.Properties.Add(name, baseRecord.GetProperty(name).NotNull()));

        return txl;
    }

    public string SerializeRecordOverride(TweakXL txl) =>
        SerializeRecordOverride(txl, new TweakXLSerializationDiagnostics());

    public string SerializeRecordOverride(TweakXL txl, TweakXLSerializationDiagnostics diagnostics)
    {
        ArgumentNullException.ThrowIfNull(diagnostics);

        var serializer = new SerializerBuilder()
            .WithTypeConverter(new TweakXLYamlTypeConverter(_locKeyService, _tweakDBService, diagnostics))
            .WithIndentedSequences()
            .Build();

        return serializer.Serialize(new TweakXLFile { txl });
    }
}
