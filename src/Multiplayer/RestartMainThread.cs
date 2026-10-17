using System;
using Godot;

namespace Retry;

internal static class RestartMainThread
{
    internal static void PostToMainThread(Action action)
    {
        var game = MegaCrit.Sts2.Core.Nodes.NGame.Instance;
        if (game == null)
        {
            GD.PrintErr($"{RetryMod.LogPrefix}restart raw packet dropped: NGame.Instance is unavailable");
            return;
        }

        Callable.From(action).CallDeferred();
    }
}
