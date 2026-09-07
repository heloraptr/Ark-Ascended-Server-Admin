using ArkAscendedServerAdmin.Domain;

namespace ArkAscendedServerAdmin.Ini;

/// <summary>The pure shape of <see cref="ExtraOverride"/>: one <c>[Section] Key=Value</c> applied to one of the two files.</summary>
public sealed record IniOverrideSpec(IniFile File, string Section, string Key, string Value)
{
    public static IniOverrideSpec From(ExtraOverride o)
    {
        ArgumentNullException.ThrowIfNull(o);
        return new IniOverrideSpec(o.File, o.Section, o.Key, o.Value);
    }
}
