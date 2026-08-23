using FFXIVClientStructs.FFXIV.Component.GUI;

namespace Rowena.Game;

/// <summary>Sends a game window the event a click on it would have sent.</summary>
/// <remarks>
/// The windows take their arguments as an array of typed values, and every one this plugin
/// has reason to send is a handful of integers. Framework thread only.
/// </remarks>
internal static class AddonCallbacks
{
    public static unsafe void Fire(AtkUnitBase* addon, params int[] arguments)
    {
        var values = stackalloc AtkValue[arguments.Length];

        for (var index = 0; index < arguments.Length; index++)
        {
            values[index].Type = AtkValueType.Int;
            values[index].Int = arguments[index];
        }

        addon->FireCallback((uint)arguments.Length, values, true);
    }
}
