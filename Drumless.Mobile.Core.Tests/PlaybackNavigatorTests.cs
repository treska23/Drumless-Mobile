using Drumless.Mobile.Core.Models;
using Drumless.Mobile.Core.Services;

namespace Drumless.Mobile.Core.Tests;

[TestClass]
public sealed class PlaybackNavigatorTests
{
    [TestMethod]
    public void Sequential_AdvancesAndStopsAtEnd()
    {
        var navigator = new PlaybackNavigator();
        navigator.SetQueue(["a", "b", "c"], "a");

        Assert.AreEqual("b", navigator.Next(automatic: true));
        Assert.AreEqual("c", navigator.Next(automatic: true));
        Assert.IsNull(navigator.Next(automatic: true));
        Assert.AreEqual("b", navigator.Previous());
    }

    [TestMethod]
    public void Single_StopsAutomaticallyButAllowsManualNext()
    {
        var navigator = new PlaybackNavigator { Mode = PlaybackMode.Single };
        navigator.SetQueue(["a", "b"], "a");

        Assert.IsNull(navigator.Next(automatic: true));
        Assert.AreEqual("b", navigator.Next(automatic: false));
    }

    [TestMethod]
    public void Shuffle_VisitsEachRemainingItemOnce()
    {
        var navigator = new PlaybackNavigator(new Random(42))
        {
            Mode = PlaybackMode.Shuffle
        };
        navigator.SetQueue(["a", "b", "c", "d"], "a");

        var visited = new List<string>();
        string? next;
        while ((next = navigator.Next(automatic: true)) is not null)
        {
            visited.Add(next);
        }

        CollectionAssert.AreEquivalent(new[] { "b", "c", "d" }, visited);
    }
}
