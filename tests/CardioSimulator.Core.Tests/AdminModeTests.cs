using System.Collections.Generic;
using System.Linq;
using CardioSimulator.Core.Domain;
using Xunit;

namespace CardioSimulator.Core.Tests;

/// <summary>
/// Covers the runtime screen-visibility filter behind the Full edition's User/Admin role
/// (<see cref="ModeVisibility"/>). See <c>AppViewModel.VisibleOperatingModes</c>.
/// </summary>
public class AdminModeTests
{
    private static IReadOnlyList<OperatingModeModel> AllModes() =>
        OperatingModes.All.Select(m => new OperatingModeModel(m)).ToList();

    [Fact]
    public void Visible_InAdmin_ReturnsEveryMode_EvenWhenHidden()
    {
        var all = AllModes();
        var hidden = new HashSet<OperatingMode> { OperatingMode.Constructor, OperatingMode.TestConstructor };

        var visible = ModeVisibility.Visible(all, hidden, AppRole.Admin);

        Assert.Equal(all.Select(m => m.Id), visible.Select(m => m.Id));
    }

    [Fact]
    public void Visible_InUser_DropsHiddenModes()
    {
        var all = AllModes();
        var hidden = new HashSet<OperatingMode> { OperatingMode.Constructor, OperatingMode.TestConstructor };

        var visible = ModeVisibility.Visible(all, hidden, AppRole.User).Select(m => m.Id).ToList();

        Assert.DoesNotContain(OperatingMode.Constructor, visible);
        Assert.DoesNotContain(OperatingMode.TestConstructor, visible);
        Assert.Contains(OperatingMode.Testing, visible);
    }

    [Fact]
    public void Visible_InUser_AlwaysRetainsTeaching_EvenIfMarkedHidden()
    {
        var all = AllModes();
        // Teaching is not hideable; a stale/forced entry in the hidden set must not remove it.
        var hidden = new HashSet<OperatingMode> { OperatingMode.Teaching };

        var visible = ModeVisibility.Visible(all, hidden, AppRole.User).Select(m => m.Id).ToList();

        Assert.Contains(OperatingMode.Teaching, visible);
    }

    [Fact]
    public void Visible_InUser_WithNothingHidden_ReturnsFullList()
    {
        var all = AllModes();

        var visible = ModeVisibility.Visible(all, new HashSet<OperatingMode>(), AppRole.User);

        Assert.Equal(all.Select(m => m.Id), visible.Select(m => m.Id));
    }

    [Fact]
    public void IsHideable_IsFalse_OnlyForTeaching()
    {
        Assert.False(OperatingMode.Teaching.IsHideable());
        foreach (var mode in OperatingModes.All.Where(m => m != OperatingMode.Teaching))
        {
            Assert.True(mode.IsHideable());
        }
    }
}
