using AmongUs.GameOptions;
using MiraAPI.GameOptions;
using TownOfUs.Options;
using TownOfUs.Roles;

namespace TownOfUs.Modules.DraftMode;

public static class DraftPoolBuilder
{
    public static DraftRules BuildRules(int numPlayers)
    {
        DraftRolePool.ClearCaches();
        var options = OptionGroupSingleton<RoleOptions>.Instance;
        var buckets = new List<ushort[]>();
        if (options.UseRoleListForPool)
        {
            var slots = OptionGroupSingleton<RoleDraftRoleListOptions>.Instance.Slot;
            for (var i = 0; i < Math.Min(numPlayers, slots.Count); i++)
                buckets.Add(DraftRolePool.ResolveBucketToRoleNames(slots[i].Value.ToString())
                    .Select(DraftRolePool.ResolveRoleIdFromName).Where(id => id != 0).Distinct().OrderBy(id => id).ToArray());
        }
        else
        {
            var allowed = DraftRolePool.ResolveBucketToRoleNames(nameof(RoleListOption.Any))
                .Select(DraftRolePool.ResolveRoleIdFromName).Where(id => id != 0).Distinct().OrderBy(id => id).ToArray();
            for (var i = 0; i < numPlayers; i++) buckets.Add(allowed);
        }

        var ids = buckets.SelectMany(bucket => bucket).Distinct().OrderBy(id => id).ToArray();
        var roles = new List<DraftRuleRole>(ids.Length);
        foreach (var id in ids)
        {
            var role = MiscUtils.GetRegisteredRole((RoleTypes)id);
            if (role == null || !DraftRolePool.IsUsableRole(role)) continue;
            var faction = DraftRolePool.IsImpostorRole(role) ? DraftRuleFaction.Impostor :
                DraftRolePool.IsNeutralRoleId(id) ? DraftRuleFaction.Neutral : DraftRuleFaction.Crew;
            var alignment = GetAlignment(role, faction);
            var exclusive = ids.Where(other => other != id &&
                MiscUtils.GetRegisteredRole((RoleTypes)other) is { } otherRole && MiscUtils.AreRolesExclusive(role, otherRole)).ToArray();
            DraftApplier.GetTargetMasks(role, out var targetMask, out var requiredMask);
            roles.Add(new DraftRuleRole(id, (int)faction, (int)alignment, DraftRolePool.GetRoleCount(role),
                DraftRolePool.IsDoubleDraftRoleId(id) ? 2 : 1, DraftRolePool.GetRoleChance(role), targetMask, requiredMask, exclusive));
        }

        var factions = Enumerable.Repeat(new DraftRuleQuota(0, numPlayers), 3).ToArray();
        var alignments = Enumerable.Repeat(new DraftRuleQuota(0, numPlayers), 13).ToArray();
        if (options.UseRoleListForPool)
        {
            for (var faction = 0; faction < factions.Length; faction++)
            {
                var matching = roles.Where(role => role.Faction == faction).ToDictionary(role => role.Id, role => role.Weight);
                var capacity = 0;
                foreach (var bucket in buckets)
                {
                    var weight = 0;
                    foreach (var id in bucket) weight = Math.Max(weight, matching.GetValueOrDefault(id));
                    capacity += weight;
                }
                factions[faction] = new DraftRuleQuota(0, capacity);
            }
            for (var alignment = 0; alignment < alignments.Length; alignment++)
            {
                var matching = roles.Where(role => role.Alignment == alignment).ToDictionary(role => role.Id, role => role.Weight);
                var capacity = 0;
                foreach (var bucket in buckets)
                {
                    var weight = 0;
                    foreach (var id in bucket) weight = Math.Max(weight, matching.GetValueOrDefault(id));
                    capacity += weight;
                }
                alignments[alignment] = new DraftRuleQuota(0, capacity);
            }
        }
        else
        {
            var crew = OptionGroupSingleton<RoleDraftCrewOptions>.Instance;
            var imp = OptionGroupSingleton<RoleDraftImpOptions>.Instance;
            var neutral = OptionGroupSingleton<RoleDraftNeutOptions>.Instance;
            var impTarget = (int)imp.MaxImpostors.Value;
            var neutralTarget = (int)neutral.MaxNeutrals.Value;
            factions[(int)DraftRuleFaction.Impostor] = new DraftRuleQuota(impTarget, impTarget);
            factions[(int)DraftRuleFaction.Neutral] = new DraftRuleQuota(neutralTarget, neutralTarget);
            alignments[(int)DraftRuleAlignment.CrewInvestigative] = new DraftRuleQuota(0, (int)crew.MaxCrewInvestigative.Value);
            alignments[(int)DraftRuleAlignment.CrewKilling] = new DraftRuleQuota(0, (int)crew.MaxCrewKilling.Value);
            alignments[(int)DraftRuleAlignment.CrewProtective] = new DraftRuleQuota(0, (int)crew.MaxCrewProtective.Value);
            alignments[(int)DraftRuleAlignment.CrewPower] = new DraftRuleQuota(0, (int)crew.MaxCrewPower.Value);
            alignments[(int)DraftRuleAlignment.CrewSupport] = new DraftRuleQuota(0, (int)crew.MaxCrewSupport.Value);
            alignments[(int)DraftRuleAlignment.ImpConcealing] = new DraftRuleQuota(0, (int)imp.MaxImpConcealing.Value);
            alignments[(int)DraftRuleAlignment.ImpKilling] = new DraftRuleQuota(0, (int)imp.MaxImpKilling.Value);
            alignments[(int)DraftRuleAlignment.ImpPower] = new DraftRuleQuota(0, (int)imp.MaxImpPower.Value);
            alignments[(int)DraftRuleAlignment.ImpSupport] = new DraftRuleQuota(0, (int)imp.MaxImpSupport.Value);
            alignments[(int)DraftRuleAlignment.NeutralBenign] = new DraftRuleQuota(0, (int)neutral.MaxNeutBenign.Value);
            alignments[(int)DraftRuleAlignment.NeutralEvil] = new DraftRuleQuota(0, (int)neutral.MaxNeutEvil.Value);
            var killingTarget = (int)neutral.MaxNeutKilling.Value;
            alignments[(int)DraftRuleAlignment.NeutralKilling] = new DraftRuleQuota(killingTarget, killingTarget);
            alignments[(int)DraftRuleAlignment.NeutralOutlier] = new DraftRuleQuota(0, (int)neutral.MaxNeutOutlier.Value);
        }
        return new DraftRules(roles, buckets, factions, alignments,
            roleIds => DraftApplier.TryValidateTargets(roleIds, out _));
    }

    private static DraftRuleAlignment GetAlignment(RoleBehaviour role, DraftRuleFaction faction) => role.GetRoleAlignment() switch
    {
        RoleAlignment.CrewmateInvestigative => DraftRuleAlignment.CrewInvestigative,
        RoleAlignment.CrewmateKilling => DraftRuleAlignment.CrewKilling,
        RoleAlignment.CrewmateProtective => DraftRuleAlignment.CrewProtective,
        RoleAlignment.CrewmatePower => DraftRuleAlignment.CrewPower,
        RoleAlignment.CrewmateSupport => DraftRuleAlignment.CrewSupport,
        RoleAlignment.ImpostorConcealing => DraftRuleAlignment.ImpConcealing,
        RoleAlignment.ImpostorKilling => DraftRuleAlignment.ImpKilling,
        RoleAlignment.ImpostorPower => DraftRuleAlignment.ImpPower,
        RoleAlignment.ImpostorSupport => DraftRuleAlignment.ImpSupport,
        RoleAlignment.NeutralBenign => DraftRuleAlignment.NeutralBenign,
        RoleAlignment.NeutralEvil => DraftRuleAlignment.NeutralEvil,
        RoleAlignment.NeutralKilling => DraftRuleAlignment.NeutralKilling,
        RoleAlignment.NeutralOutlier => DraftRuleAlignment.NeutralOutlier,
        _ => faction == DraftRuleFaction.Impostor ? DraftRuleAlignment.ImpSupport :
            faction == DraftRuleFaction.Neutral ? DraftRuleAlignment.NeutralOutlier : DraftRuleAlignment.CrewSupport
    };
}
