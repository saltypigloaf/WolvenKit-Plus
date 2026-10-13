using System;
using System.Collections.Generic;
using System.Linq;

namespace WolvenKit.App.Models;

/// <summary>
/// Collects distinct value types encountered during one serialization/export operation.
/// Snapshots are sorted and detached; this collector must not be shared between exports.
/// </summary>
public sealed class TweakXLSerializationDiagnostics
{
    private readonly HashSet<string> _fallbackValueTypes = new(StringComparer.Ordinal);
    private readonly HashSet<string> _unsupportedValueTypes = new(StringComparer.Ordinal);

    public IReadOnlyList<string> FallbackValueTypes => _fallbackValueTypes.Order(StringComparer.Ordinal).ToArray();

    public IReadOnlyList<string> UnsupportedValueTypes => _unsupportedValueTypes.Order(StringComparer.Ordinal).ToArray();

    internal void RecordFallback(Type type) => _fallbackValueTypes.Add(type.FullName ?? type.Name);

    internal void RecordUnsupported(Type? type) => _unsupportedValueTypes.Add(type?.FullName ?? type?.Name ?? "<null>");
}
