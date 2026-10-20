using TownOfUs.Modules.DraftMode;
using Xunit;

namespace TownOfUs.Draft.Tests;

public sealed class DraftStartupTests
{
    [Fact]
    public void ExecutionerRequiresAnEligibleCrewTargetBeforeCommitment()
    {
        var executioner = new DraftStartupRole(0, 1, false, false);
        var powerCrew = new DraftStartupRole(2, 0, true, false);
        var eligibleCrew = new DraftStartupRole(3, 0, true, false);

        Assert.Equal(DraftStartupFailure.ExecutionerTarget,
            DraftStartupRules.ValidateTargets(new[] { executioner, powerCrew }, false, false, 0));
        Assert.Equal(DraftStartupFailure.None,
            DraftStartupRules.ValidateTargets(new[] { executioner, eligibleCrew }, false, false, 0));
    }

    [Fact]
    public void MultipleExecutionersCanShareOneTarget()
    {
        var executioner = new DraftStartupRole(0, 1, false, false);
        var target = new DraftStartupRole(3, 0, true, false);

        Assert.Equal(DraftStartupFailure.None,
            DraftStartupRules.ValidateTargets(new[] { executioner, executioner, target }, false, false, 0));
    }

    [Fact]
    public void CrewpostorCannotConsumeTheOnlyExecutionerTarget()
    {
        var executioner = new DraftStartupRole(0, 1, false, false);
        var target = new DraftStartupRole(11, 0, true, false);

        Assert.Equal(DraftStartupFailure.ExecutionerTarget,
            DraftStartupRules.ValidateTargets(new[] { executioner, target }, true, false, 0));
        Assert.Equal(DraftStartupFailure.None,
            DraftStartupRules.ValidateTargets(new[] { executioner, target, target }, true, false, 0));
    }

    [Fact]
    public void CrewpostorRequiresACrewmateAllowedToBecomeCrewpostor()
    {
        var loyalCrew = new DraftStartupRole(3, 0, true, false);
        var eligibleCrew = new DraftStartupRole(11, 0, true, false);

        Assert.Equal(DraftStartupFailure.CrewpostorTarget,
            DraftStartupRules.ValidateTargets(new[] { loyalCrew }, true, false, 0));
        Assert.Equal(DraftStartupFailure.None,
            DraftStartupRules.ValidateTargets(new[] { eligibleCrew }, true, false, 0));
    }

    [Fact]
    public void CrewOnlyFairyCannotTakeTheLastExecutionerTarget()
    {
        var executioner = new DraftStartupRole(0, 1, false, false);
        var fairy = new DraftStartupRole(0, 2, false, false);
        var executionerTarget = new DraftStartupRole(3, 0, true, false);
        var otherCrew = new DraftStartupRole(2, 0, true, false);

        Assert.Equal(DraftStartupFailure.FairyTarget,
            DraftStartupRules.ValidateTargets(new[] { executioner, fairy, executionerTarget }, false, false, 0));
        Assert.Equal(DraftStartupFailure.None,
            DraftStartupRules.ValidateTargets(new[] { executioner, fairy, executionerTarget, otherCrew }, false, false, 0));
    }

    [Fact]
    public void AnEitherFactionFairyCanUseEvilWithoutTakingRequiredCrew()
    {
        var executioner = new DraftStartupRole(0, 1, false, false);
        var fairy = new DraftStartupRole(0, 6, false, false);
        var crew = new DraftStartupRole(3, 0, true, false);
        var evil = new DraftStartupRole(4, 0, true, true);

        Assert.Equal(DraftStartupFailure.None,
            DraftStartupRules.ValidateTargets(new[] { executioner, fairy, crew, evil }, false, false, 0));
    }

    [Fact]
    public void EvilOnlyFairyNeedsAnEvilTarget()
    {
        var fairy = new DraftStartupRole(0, 4, false, false);
        var crew = new DraftStartupRole(3, 0, true, false);
        var evil = new DraftStartupRole(4, 0, true, true);

        Assert.Equal(DraftStartupFailure.FairyTarget,
            DraftStartupRules.ValidateTargets(new[] { fairy, crew }, false, false, 0));
        Assert.Equal(DraftStartupFailure.None,
            DraftStartupRules.ValidateTargets(new[] { fairy, evil }, false, false, 0));
    }

    [Fact]
    public void LoversCannotReuseAnExecutionerTarget()
    {
        var executioner = new DraftStartupRole(0, 1, false, false);
        var crew = new DraftStartupRole(3, 0, true, false);

        Assert.Equal(DraftStartupFailure.LoversPair,
            DraftStartupRules.ValidateTargets(new[] { executioner, crew, crew }, false, true, 0));
        Assert.Equal(DraftStartupFailure.None,
            DraftStartupRules.ValidateTargets(new[] { executioner, crew, crew, crew }, false, true, 0));
    }

    [Theory]
    [InlineData(2, 0, 0, true, false)]
    [InlineData(1, 0, 0, false, false)]
    [InlineData(2, 0, 100, false, false)]
    [InlineData(1, 1, 100, true, true)]
    [InlineData(0, 2, 100, false, false)]
    public void LoversRespectForcedFactionPreferences(int nonKillers, int killers, int percent,
        bool expectedPair, bool expectedMixed)
    {
        var selected = DraftStartupRules.TryChooseLoverFactions(nonKillers, killers, percent, 0, out var mixed);

        Assert.Equal(expectedPair, selected);
        if (selected) Assert.Equal(expectedMixed, mixed);
    }

    [Theory]
    [InlineData(49, true)]
    [InlineData(50, false)]
    public void LoversUseTheConfiguredChanceWhenBothPairTypesArePossible(int roll, bool expectedMixed)
    {
        Assert.True(DraftStartupRules.TryChooseLoverFactions(2, 1, 50, roll, out var mixed));
        Assert.Equal(expectedMixed, mixed);
    }

    [Theory]
    [InlineData(1, 1, 99, true)]
    [InlineData(2, 0, 0, false)]
    public void LoversUseTheAvailableLegalPairWhenTheOtherTypeIsImpossible(int nonKillers, int killers,
        int roll, bool expectedMixed)
    {
        Assert.True(DraftStartupRules.TryChooseLoverFactions(nonKillers, killers, 50, roll, out var mixed));
        Assert.Equal(expectedMixed, mixed);
    }
}
