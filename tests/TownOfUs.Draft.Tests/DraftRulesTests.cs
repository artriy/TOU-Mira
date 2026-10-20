using TownOfUs.Modules.DraftMode;
using Xunit;

namespace TownOfUs.Draft.Tests;

public sealed class DraftRulesTests
{
    [Fact]
    public void AlternativesShareOneBucketRatherThanCreatingExtraSeats()
    {
        var rules = Rules(new[] { Crew(100, DraftRuleAlignment.CrewPower), Crew(101, DraftRuleAlignment.CrewPower), Crew(102, count: 2) },
            new ushort[][] { new ushort[] { 100, 101 }, new ushort[] { 102 } });

        Assert.Equal(new ushort[] { 102 }, rules.GetLegalCandidates(new ushort[] { 100 }, out var status));
        Assert.Equal(DraftFeasibility.Feasible, status);
        Assert.Equal(DraftFeasibility.Impossible, rules.FindCompletion(new ushort[] { 100, 101 }, out _));
    }

    [Fact]
    public void AcceptedRolesCanBeRematchedAcrossOverlappingBuckets()
    {
        var rules = Rules(new[] { Crew(100), Crew(101) },
            new ushort[][] { new ushort[] { 100, 101 }, new ushort[] { 100 } });

        Assert.Equal(new ushort[] { 101 }, rules.GetLegalCandidates(new ushort[] { 100 }, out _));
        Assert.Equal(DraftFeasibility.Feasible, rules.FindCompletion(new ushort[] { 100, 101 }, out var completion));
        Assert.Equal(new ushort[] { 100, 101 }, completion.OrderBy(id => id));
    }

    [Fact]
    public void RoleAmountsAllowCopiesUntilTheirConfiguredCapacity()
    {
        var roles = new[] { Crew(100, count: 2) };
        var rules = Rules(roles, new ushort[][] { new ushort[] { 100 }, new ushort[] { 100 } });

        Assert.Equal(new ushort[] { 100 }, rules.GetLegalCandidates(new ushort[] { 100 }, out _));
        Assert.Equal(DraftFeasibility.Feasible, rules.FindCompletion(new ushort[] { 100, 100 }, out var completion));
        Assert.Equal(new ushort[] { 100, 100 }, completion);

        var tooMany = Rules(roles, new ushort[][] { new ushort[] { 100 }, new ushort[] { 100 }, new ushort[] { 100 } });
        Assert.Equal(DraftFeasibility.Impossible, tooMany.FindCompletion(Array.Empty<ushort>(), out _));
    }

    [Fact]
    public void ExclusionOnEitherRoleBlocksThePair()
    {
        var roles = new[] { Crew(100, excludes: new ushort[] { 101 }), Crew(101), Crew(102), Crew(103) };
        var rules = Rules(roles, new ushort[][] { new ushort[] { 100, 102 }, new ushort[] { 101, 103 } });

        Assert.Equal(new ushort[] { 103 }, rules.GetLegalCandidates(new ushort[] { 100 }, out _));
        Assert.Equal(new ushort[] { 102 }, rules.GetLegalCandidates(new ushort[] { 101 }, out _));
        Assert.Equal(DraftFeasibility.Impossible, rules.FindCompletion(new ushort[] { 100, 101 }, out _));
    }

    [Fact]
    public void RequiredImpostorCountDoesNotShrinkAfterAnAcceptedPick()
    {
        var roles = new[] { Impostor(200), Impostor(201), Crew(100), Crew(101) };
        var factions = Quotas(3);
        factions[(int)DraftRuleFaction.Impostor] = new DraftRuleQuota(2, 2);
        var rules = Rules(roles, new ushort[][]
        {
            new ushort[] { 200, 201 }, new ushort[] { 200, 201 }, new ushort[] { 100, 101 }
        }, factions);

        Assert.Equal(new ushort[] { 201 }, rules.GetLegalCandidates(new ushort[] { 200, 100 }, out _));
        Assert.Equal(DraftFeasibility.Impossible, rules.FindCompletion(new ushort[] { 200, 100, 101 }, out _));
    }

    [Fact]
    public void SubtypeMaximumAppliesToDistinctRolesOfThatSubtype()
    {
        var roles = new[] { Crew(100, DraftRuleAlignment.CrewKilling), Crew(101, DraftRuleAlignment.CrewKilling), Crew(102) };
        var alignments = Quotas(13);
        alignments[(int)DraftRuleAlignment.CrewKilling] = new DraftRuleQuota(0, 1);
        var rules = Rules(roles, new ushort[][] { new ushort[] { 100, 101, 102 }, new ushort[] { 100, 101, 102 } }, alignments: alignments);

        Assert.Equal(new ushort[] { 102 }, rules.GetLegalCandidates(new ushort[] { 100 }, out _));
        Assert.Equal(DraftFeasibility.Impossible, rules.FindCompletion(new ushort[] { 100, 101 }, out _));
    }

    [Fact]
    public void BroadNeutralBucketAllowsItsKillingMember()
    {
        var roles = new[] { Neutral(300, DraftRuleAlignment.NeutralKilling), Neutral(301, DraftRuleAlignment.NeutralBenign), Crew(100) };
        var rules = Rules(roles, new ushort[][] { new ushort[] { 300, 301 }, new ushort[] { 100 } });

        Assert.Equal(new ushort[] { 100, 300, 301 }, rules.GetLegalCandidates(Array.Empty<ushort>(), out _));
        Assert.Equal(new ushort[] { 100 }, rules.GetLegalCandidates(new ushort[] { 300 }, out _));
    }

    [Fact]
    public void JointlyImpossibleQuotasDoNotApproveAnyFallback()
    {
        var factions = Quotas(3);
        factions[1] = new DraftRuleQuota(2, 2);
        factions[2] = new DraftRuleQuota(1, 1);
        var rules = Rules(new[] { Impostor(200), Impostor(201), Neutral(300), Crew(100) },
            new ushort[][] { new ushort[] { 100, 200, 201, 300 }, new ushort[] { 100, 200, 201, 300 } }, factions);

        Assert.Equal(DraftFeasibility.Impossible, rules.FindCompletion(Array.Empty<ushort>(), out _));
        Assert.Empty(rules.GetLegalCandidates(Array.Empty<ushort>(), out var status));
        Assert.Equal(DraftFeasibility.Impossible, status);
    }

    [Fact]
    public void DoubleDraftRoleConsumesWeightedQuotaButOnlyOneInitialPlayer()
    {
        var vampire = new DraftRuleRole(300, 2, (int)DraftRuleAlignment.NeutralKilling, 1, weight: 2);
        var factions = Quotas(3);
        factions[2] = new DraftRuleQuota(2, 2);
        var alignments = Quotas(13);
        alignments[(int)DraftRuleAlignment.NeutralKilling] = new DraftRuleQuota(2, 2);
        var buckets = new ushort[][] { new ushort[] { 300 }, new ushort[] { 100 } };
        var rules = Rules(new[] { vampire, Crew(100) }, buckets, factions, alignments);

        Assert.Equal(DraftFeasibility.Feasible, rules.FindCompletion(new ushort[] { 300 }, out var completion));
        Assert.Equal(new ushort[] { 100, 300 }, completion.OrderBy(id => id));
        factions[2] = new DraftRuleQuota(0, 1);
        Assert.Equal(DraftFeasibility.Impossible, Rules(new[] { vampire, Crew(100) }, buckets, factions).FindCompletion(Array.Empty<ushort>(), out _));
    }

    [Fact]
    public void ARequiredTargetMustBeAnotherAssignedPlayer()
    {
        var executioner = new DraftRuleRole(300, 2, (int)DraftRuleAlignment.NeutralEvil, 1, requiredTargetMask: 1);
        var target = new DraftRuleRole(100, 0, (int)DraftRuleAlignment.CrewSupport, 1, targetMask: 1);
        var rules = Rules(new[] { executioner, target, Crew(101) },
            new ushort[][] { new ushort[] { 300 }, new ushort[] { 100, 101 } });

        Assert.Equal(new ushort[] { 100 }, rules.GetLegalCandidates(new ushort[] { 300 }, out _));
        Assert.Equal(DraftFeasibility.Impossible, rules.FindCompletion(new ushort[] { 300, 101 }, out _));

        var self = new DraftRuleRole(400, 2, (int)DraftRuleAlignment.NeutralBenign, 1, targetMask: 1, requiredTargetMask: 1);
        Assert.Equal(DraftFeasibility.Impossible,
            Rules(new[] { self }, new ushort[][] { new ushort[] { 400 } }).FindCompletion(Array.Empty<ushort>(), out _));
    }

    [Fact]
    public void OffListAndExhaustedRolesNeverBecomeCandidates()
    {
        var rules = Rules(new[] { Crew(100), Crew(101) }, new ushort[][] { new ushort[] { 100 } });

        Assert.Equal(new ushort[] { 100 }, rules.GetLegalCandidates(Array.Empty<ushort>(), out _));
        Assert.Equal(DraftFeasibility.Impossible, rules.FindCompletion(new ushort[] { 101 }, out _));
        Assert.Equal(DraftFeasibility.Impossible, rules.FindCompletion(new ushort[] { 999 }, out _));
        Assert.Empty(rules.GetLegalCandidates(new ushort[] { 100 }, out _));
    }

    [Fact]
    public void EmptyBucketsAreImpossibleRatherThanPlaceholderRoles()
    {
        var rules = Rules(new[] { Crew(100) }, new ushort[][] { Array.Empty<ushort>() });

        Assert.Equal(DraftFeasibility.Impossible, rules.FindCompletion(Array.Empty<ushort>(), out _));
        Assert.Empty(rules.GetLegalCandidates(Array.Empty<ushort>(), out _));
    }

    [Fact]
    public void ResourceLimitIsNotReportedAsAnImpossibleConfiguration()
    {
        var rules = Rules(new[] { Crew(100) }, new ushort[][] { new ushort[] { 100 } });

        Assert.Equal(DraftFeasibility.SearchLimit, rules.FindCompletion(Array.Empty<ushort>(), out _, nodeLimit: 1));
        Assert.Equal(DraftFeasibility.Feasible, rules.FindCompletion(Array.Empty<ushort>(), out _));
    }

    [Fact]
    public void FifteenIdenticalBucketsCanUseTheConfiguredRoleCapacity()
    {
        var rules = Rules(new[] { Crew(100, count: 15) }, Enumerable.Range(0, 15).Select(_ => new ushort[] { 100 }).ToArray());

        Assert.Equal(DraftFeasibility.Feasible, rules.FindCompletion(Array.Empty<ushort>(), out var completion));
        Assert.Equal(Enumerable.Repeat((ushort)100, 15), completion);
    }

    [Fact]
    public void BroadFifteenPlayerPoolPreservesLateOrderedAcceptedRolesWithinTheOfferBudget()
    {
        var roles = Enumerable.Range(0, 75).Select(index =>
        {
            var faction = index % 3;
            var alignment = faction == 0 ? DraftRuleAlignment.CrewSupport :
                faction == 1 ? DraftRuleAlignment.ImpKilling :
                index % 6 == 2 ? DraftRuleAlignment.NeutralKilling : DraftRuleAlignment.NeutralBenign;
            return new DraftRuleRole((ushort)(400 + index), faction, (int)alignment, 1);
        }).ToArray();
        var buckets = Enumerable.Range(0, 15).Select(_ => roles.Select(role => role.Id).ToArray()).ToArray();
        var factions = Enumerable.Repeat(new DraftRuleQuota(0, 15), 3).ToArray();
        factions[1] = new DraftRuleQuota(3, 3);
        factions[2] = new DraftRuleQuota(2, 2);
        var alignments = Enumerable.Repeat(new DraftRuleQuota(0, 15), 13).ToArray();
        alignments[(int)DraftRuleAlignment.NeutralKilling] = new DraftRuleQuota(1, 1);
        var rules = new DraftRules(roles, buckets, factions, alignments);

        Assert.Equal(roles.Select(role => role.Id),
            rules.GetLegalCandidates(Array.Empty<ushort>(), out var initialStatus, nodeLimit: 5000));
        Assert.Equal(DraftFeasibility.Feasible, initialStatus);
        var accepted = new ushort[] { 474, 473, 472, 471, 470 };
        var expected = roles.Where(role => role.Faction != 2 && !accepted.Contains(role.Id)).Select(role => role.Id);
        Assert.Equal(expected, rules.GetLegalCandidates(accepted, out var status, nodeLimit: 5000));
        Assert.Equal(DraftFeasibility.Feasible, status);
        Assert.Equal(DraftFeasibility.Feasible, rules.FindCompletion(accepted, out var completion, nodeLimit: 5000));
        Assert.True(ContainsAccepted(completion, accepted));
        Assert.True(ValidAssignment(completion, roles, factions, alignments));
    }

    [Fact]
    public void ProvenForcedCandidateSurvivesAnExhaustedOfferSearchBudget()
    {
        var rules = Rules(new[] { Crew(100), Crew(101), Crew(102) },
            new ushort[][] { new ushort[] { 100 }, new ushort[] { 101, 102 } });

        var candidates = rules.GetLegalCandidates(Array.Empty<ushort>(), out var status, nodeLimit: 4);

        Assert.Equal(DraftFeasibility.Feasible, status);
        Assert.Contains((ushort)100, candidates);
        foreach (var candidate in candidates)
            Assert.Equal(DraftFeasibility.Feasible, rules.FindCompletion(new[] { candidate }, out _));
    }

    [Fact]
    public void SmallDraftsMatchAnIndependentExhaustiveAssignmentOracle()
    {
        var random = new Random(8319);
        var roles = new[]
        {
            Crew(100, count: 2), Crew(101, DraftRuleAlignment.CrewKilling),
            Impostor(200, new ushort[] { 300 }),
            new DraftRuleRole(300, 2, (int)DraftRuleAlignment.NeutralKilling, 1, weight: 2)
        };
        for (var scenario = 0; scenario < 160; scenario++)
        {
            var factions = Enumerable.Range(0, 3).Select(_ => RandomQuota(random)).ToArray();
            var alignments = Quotas(13);
            alignments[(int)DraftRuleAlignment.CrewKilling] = new DraftRuleQuota(0, random.Next(2));
            var buckets = Enumerable.Range(0, 3).Select(_ => roles.Where(_ => random.Next(3) != 0).Select(role => role.Id).ToArray()).ToArray();
            var accepted = Enumerable.Range(0, random.Next(4)).Select(_ => roles[random.Next(roles.Length)].Id).ToArray();
            var witnesses = EnumerateAssignments(buckets).Where(assignment => ValidAssignment(assignment, roles, factions, alignments)).ToList();
            var possible = witnesses.Where(assignment => ContainsAccepted(assignment, accepted)).ToList();
            var rules = Rules(roles, buckets, factions, alignments);

            var actual = rules.FindCompletion(accepted, out var completion);
            Assert.True((possible.Count > 0) == (actual == DraftFeasibility.Feasible), $"Completion disagreement in scenario {scenario}");
            if (actual == DraftFeasibility.Feasible)
            {
                Assert.True(ValidAssignment(completion, roles, factions, alignments));
                Assert.True(ContainsAccepted(completion, accepted));
                for (var bucket = 0; bucket < buckets.Length; bucket++) Assert.Contains(completion[bucket], buckets[bucket]);
            }
            var expectedCandidates = accepted.Length == buckets.Length ? Array.Empty<ushort>() : roles
                .Where(role => witnesses.Any(assignment => ContainsAccepted(assignment, accepted.Append(role.Id).ToArray())))
                .Select(role => role.Id).OrderBy(id => id).ToArray();
            Assert.Equal(expectedCandidates, rules.GetLegalCandidates(accepted, out _).OrderBy(id => id));
        }
    }

    private static DraftRuleRole Crew(ushort id, DraftRuleAlignment alignment = DraftRuleAlignment.CrewSupport, int count = 1, ushort[]? excludes = null) =>
        new(id, (int)DraftRuleFaction.Crew, (int)alignment, count, exclusiveWith: excludes);

    private static DraftRuleRole Impostor(ushort id, ushort[]? excludes = null) =>
        new(id, (int)DraftRuleFaction.Impostor, (int)DraftRuleAlignment.ImpSupport, 1, exclusiveWith: excludes);

    private static DraftRuleRole Neutral(ushort id, DraftRuleAlignment alignment = DraftRuleAlignment.NeutralEvil) =>
        new(id, (int)DraftRuleFaction.Neutral, (int)alignment, 1);

    private static DraftRuleQuota[] Quotas(int count) => Enumerable.Range(0, count).Select(_ => new DraftRuleQuota(0, 30)).ToArray();

    private static DraftRules Rules(DraftRuleRole[] roles, ushort[][] buckets, DraftRuleQuota[]? factions = null, DraftRuleQuota[]? alignments = null) =>
        new(roles, buckets, factions ?? Quotas(3), alignments ?? Quotas(13));

    private static DraftRuleQuota RandomQuota(Random random)
    {
        var maximum = random.Next(5);
        return new DraftRuleQuota(random.Next(maximum + 1), maximum);
    }

    private static IEnumerable<ushort[]> EnumerateAssignments(ushort[][] buckets)
    {
        var assignments = new List<ushort[]> { Array.Empty<ushort>() };
        foreach (var bucket in buckets)
            assignments = assignments.SelectMany(prefix => bucket.Select(id => prefix.Append(id).ToArray())).ToList();
        return assignments;
    }

    private static bool ContainsAccepted(IReadOnlyList<ushort> assignment, IReadOnlyList<ushort> accepted) =>
        accepted.GroupBy(id => id).All(group => assignment.Count(id => id == group.Key) >= group.Count());

    private static bool ValidAssignment(IReadOnlyList<ushort> assignment, DraftRuleRole[] roles, DraftRuleQuota[] factions, DraftRuleQuota[] alignments)
    {
        var counts = assignment.GroupBy(id => id).ToDictionary(group => group.Key, group => group.Count());
        var factionCounts = new int[3];
        var alignmentCounts = new int[13];
        foreach (var id in assignment)
        {
            var role = roles.Single(role => role.Id == id);
            if (counts[id] > role.MaxCount) return false;
            if (role.ExclusiveWith.Any(excluded => counts.TryGetValue(excluded, out var count) && (excluded != id || count > 1))) return false;
            factionCounts[role.Faction] += role.Weight;
            alignmentCounts[role.Alignment] += role.Weight;
        }
        for (var index = 0; index < factions.Length; index++)
            if (factionCounts[index] < factions[index].Minimum || factionCounts[index] > factions[index].Maximum) return false;
        for (var index = 0; index < alignments.Length; index++)
            if (alignmentCounts[index] < alignments[index].Minimum || alignmentCounts[index] > alignments[index].Maximum) return false;
        return true;
    }
}
