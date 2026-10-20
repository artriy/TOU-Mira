namespace TownOfUs.Modules.DraftMode;

public readonly record struct DraftStartupRole(int TargetMask, int RequiredMask, bool CanLove, bool Killer);

public enum DraftStartupFailure
{
    None,
    ExecutionerTarget,
    FairyTarget,
    CrewpostorTarget,
    LoversPair
}

public static class DraftStartupRules
{
    public static DraftStartupFailure ValidateTargets(IReadOnlyList<DraftStartupRole> roles,
        bool crewpostor, bool lovers, int killerPercent)
    {
        var executioner = false;
        var fairyRequirement = 0;
        var executionerTargets = 0;
        var fairyCrew = 0;
        var fairyEvil = 0;
        var crewpostorTargets = 0;
        var loverKillers = 0;
        var loverNonKillers = 0;
        foreach (var role in roles)
        {
            executioner |= role.RequiredMask == 1;
            if (role.RequiredMask > 1) fairyRequirement = role.RequiredMask;
            if ((role.TargetMask & 1) != 0) executionerTargets++;
            if ((role.TargetMask & 2) != 0) fairyCrew++;
            if ((role.TargetMask & 4) != 0) fairyEvil++;
            if ((role.TargetMask & 8) != 0) crewpostorTargets++;
            if (!role.CanLove) continue;
            if (role.Killer) loverKillers++;
            else loverNonKillers++;
        }
        var crewReservation = crewpostor ? 1 : 0;
        if (executioner && executionerTargets < crewReservation + 1)
            return DraftStartupFailure.ExecutionerTarget;
        if (crewpostor && crewpostorTargets == 0) return DraftStartupFailure.CrewpostorTarget;
        if (fairyRequirement != 0)
        {
            var crewAvailable = fairyCrew - crewReservation;
            if (executioner && executionerTargets == fairyCrew) crewAvailable--;
            var canCrew = (fairyRequirement & 2) != 0 && crewAvailable > 0;
            var canEvil = (fairyRequirement & 4) != 0 && fairyEvil > 0;
            if (!canCrew && !canEvil) return DraftStartupFailure.FairyTarget;
        }
        if (lovers && !TryChooseLoverFactions(loverNonKillers - crewReservation - (executioner ? 1 : 0),
                loverKillers, killerPercent, 0, out _))
            return DraftStartupFailure.LoversPair;
        return DraftStartupFailure.None;
    }

    public static bool TryChooseLoverFactions(int nonKillers, int killers, int killerPercent,
        int roll, out bool mixedPair)
    {
        var canMixedPair = killers > 0 && nonKillers > 0 && killerPercent > 0;
        var canCrewPair = nonKillers > 1 && killerPercent < 100;
        mixedPair = canMixedPair && (!canCrewPair || roll < killerPercent);
        return canMixedPair || canCrewPair;
    }
}
