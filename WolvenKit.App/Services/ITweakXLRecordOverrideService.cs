using WolvenKit.App.Models;
using WolvenKit.RED4.Types;

namespace WolvenKit.App.Services;

public interface ITweakXLRecordOverrideService
{
    TweakXL BuildRecordOverride(TweakDBID recordId);

    string SerializeRecordOverride(TweakXL txl);

    string SerializeRecordOverride(TweakXL txl, TweakXLSerializationDiagnostics diagnostics);
}
