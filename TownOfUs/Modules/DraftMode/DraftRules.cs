using System;
using System.Collections.Generic;
using System.Linq;

namespace TownOfUs.Modules.DraftMode;

public enum DraftFeasibility { Feasible, Impossible, SearchLimit }
public enum DraftRuleFaction { Crew, Impostor, Neutral }
public enum DraftRuleAlignment
{
    CrewInvestigative, CrewKilling, CrewProtective, CrewPower, CrewSupport,
    ImpConcealing, ImpKilling, ImpPower, ImpSupport,
    NeutralBenign, NeutralEvil, NeutralKilling, NeutralOutlier
}

public sealed class DraftRuleRole
{
    public ushort Id { get; }
    public int Faction { get; }
    public int Alignment { get; }
    public int MaxCount { get; }
    public int Weight { get; }
    public int Chance { get; }
    public int TargetMask { get; }
    public int RequiredTargetMask { get; }
    public IReadOnlyList<ushort> ExclusiveWith { get; }

    public DraftRuleRole(ushort id, int faction, int alignment, int maxCount, int weight = 1,
        int chance = 100, int targetMask = 0, int requiredTargetMask = 0, ushort[]? exclusiveWith = null)
    {
        Id = id;
        Faction = faction;
        Alignment = alignment;
        MaxCount = maxCount;
        Weight = weight;
        Chance = chance;
        TargetMask = targetMask;
        RequiredTargetMask = requiredTargetMask;
        ExclusiveWith = Array.AsReadOnly((ushort[])(exclusiveWith ?? Array.Empty<ushort>()).Clone());
    }
}

public readonly struct DraftRuleQuota
{
    public int Minimum { get; }
    public int Maximum { get; }
    public DraftRuleQuota(int minimum, int maximum) { Minimum = minimum; Maximum = maximum; }
}

public sealed class DraftRules
{
    private readonly DraftRuleRole[] _roles;
    private readonly Dictionary<ushort, int> _indices;
    private readonly int[][] _buckets;
    private readonly int[] _previousEquivalentBucket;
    private readonly int[][] _exclusiveIndices;
    private readonly DraftRuleQuota[] _factions;
    private readonly DraftRuleQuota[] _alignments;
    private readonly Func<IReadOnlyList<ushort>, bool>? _validateCompletion;
    private readonly bool _valid;
    public IReadOnlyList<DraftRuleRole> Roles { get; }
    public int BucketCount => _buckets.Length;
    public DraftRuleRole GetRole(ushort id) => _roles[_indices[id]];

    public DraftRules(IReadOnlyList<DraftRuleRole> roles, IReadOnlyList<ushort[]> buckets,
        DraftRuleQuota[] factionQuotas, DraftRuleQuota[] alignmentQuotas,
        Func<IReadOnlyList<ushort>, bool>? validateCompletion = null)
    {
        _roles = roles.OrderBy(role => role.Id).ToArray();
        Roles = Array.AsReadOnly(_roles);
        _indices = new Dictionary<ushort, int>();
        _valid = buckets.Count is > 0 and <= 15 && factionQuotas.Length == 3 && alignmentQuotas.Length == 13;
        for (var i = 0; i < _roles.Length; i++)
        {
            var role = _roles[i];
            if (role.Id == 0 || !_indices.TryAdd(role.Id, i) || role.Faction is < 0 or > 2 ||
                role.Alignment is < 0 or > 12 || role.MaxCount <= 0 || role.Weight is < 1 or > 2 || role.Chance <= 0)
                _valid = false;
        }
        _factions = (DraftRuleQuota[])factionQuotas.Clone();
        _alignments = (DraftRuleQuota[])alignmentQuotas.Clone();
        if (_factions.Concat(_alignments).Any(quota => quota.Minimum < 0 || quota.Maximum < quota.Minimum)) _valid = false;
        _buckets = new int[buckets.Count][];
        _previousEquivalentBucket = new int[buckets.Count];
        for (var bucket = 0; bucket < buckets.Count; bucket++)
        {
            _buckets[bucket] = buckets[bucket].Where(_indices.ContainsKey).Select(id => _indices[id]).Distinct().OrderBy(index => index).ToArray();
            if (_buckets[bucket].Length == 0) _valid = false;
            _previousEquivalentBucket[bucket] = -1;
            for (var previous = bucket - 1; previous >= 0; previous--)
                if (_buckets[previous].SequenceEqual(_buckets[bucket]))
                {
                    _previousEquivalentBucket[bucket] = previous;
                    break;
                }
        }
        var exclusive = new bool[_roles.Length, _roles.Length];
        for (var i = 0; i < _roles.Length; i++)
            foreach (var id in _roles[i].ExclusiveWith)
                if (_indices.TryGetValue(id, out var j)) exclusive[i, j] = exclusive[j, i] = true;
        _exclusiveIndices = new int[_roles.Length][];
        var excluded = new List<int>();
        for (var i = 0; i < _roles.Length; i++)
        {
            excluded.Clear();
            for (var j = 0; j < _roles.Length; j++) if (exclusive[i, j]) excluded.Add(j);
            _exclusiveIndices[i] = excluded.ToArray();
        }
        _validateCompletion = validateCompletion;
    }

    public DraftFeasibility FindCompletion(IReadOnlyList<ushort> accepted, out ushort[] completion, int nodeLimit = 200000)
    {
        var remainingNodes = Math.Max(0, nodeLimit);
        return FindCompletion(accepted, out completion, ref remainingNodes);
    }

    private DraftFeasibility FindCompletion(IReadOnlyList<ushort> accepted, out ushort[] completion, ref int remainingNodes)
    {
        completion = Array.Empty<ushort>();
        if (!_valid || accepted.Count > _buckets.Length) return DraftFeasibility.Impossible;
        var required = new int[_roles.Length];
        foreach (var id in accepted)
        {
            if (!_indices.TryGetValue(id, out var index) || ++required[index] > _roles[index].MaxCount)
                return DraftFeasibility.Impossible;
        }
        for (var i = 0; i < required.Length; i++)
            if (required[i] > 0)
                foreach (var j in _exclusiveIndices[i])
                    if (required[j] > 0 && (i != j || required[i] > 1))
                        return DraftFeasibility.Impossible;
        if (remainingNodes == 0) return DraftFeasibility.SearchLimit;
        var search = new CompletionSearch(this, required, remainingNodes);
        var found = search.Run();
        remainingNodes = Math.Max(0, remainingNodes - search.NodesVisited);
        if (found) { completion = search.Completion; return DraftFeasibility.Feasible; }
        return search.LimitReached ? DraftFeasibility.SearchLimit : DraftFeasibility.Impossible;
    }

    public List<ushort> GetLegalCandidates(IReadOnlyList<ushort> accepted, out DraftFeasibility feasibility, int nodeLimit = 200000)
    {
        var legal = new List<ushort>();
        var remainingNodes = Math.Max(0, nodeLimit);
        feasibility = FindCompletion(accepted, out var witness, ref remainingNodes);
        if (feasibility == DraftFeasibility.Impossible || accepted.Count == BucketCount) return legal;
        var counts = new Dictionary<ushort, int>();
        foreach (var id in accepted) counts[id] = counts.GetValueOrDefault(id) + 1;
        var proposal = new ushort[accepted.Count + 1];
        for (var i = 0; i < accepted.Count; i++) proposal[i] = accepted[i];
        var uncertain = feasibility == DraftFeasibility.SearchLimit;
        foreach (var role in _roles)
        {
            if (counts.GetValueOrDefault(role.Id) >= role.MaxCount) continue;
            var witnessCopies = 0;
            foreach (var id in witness) if (id == role.Id) witnessCopies++;
            if (witnessCopies > counts.GetValueOrDefault(role.Id))
            {
                legal.Add(role.Id);
                continue;
            }
            proposal[^1] = role.Id;
            if (remainingNodes == 0) { uncertain = true; continue; }
            var status = FindCompletion(proposal, out var candidateWitness, ref remainingNodes);
            if (status == DraftFeasibility.Feasible) { legal.Add(role.Id); witness = candidateWitness; }
            else if (status == DraftFeasibility.SearchLimit) uncertain = true;
        }
        feasibility = legal.Count > 0 ? DraftFeasibility.Feasible : uncertain ? DraftFeasibility.SearchLimit : DraftFeasibility.Impossible;
        return legal;
    }

    private sealed class CompletionSearch
    {
        private readonly DraftRules _rules;
        private readonly int[] _required;
        private readonly int[] _counts;
        private readonly int[] _excluded;
        private readonly int[] _remainingRequiredFactions = new int[3];
        private readonly int[] _remainingRequiredAlignments = new int[13];
        private int _remainingRequiredCount;
        private readonly int[] _factionCounts = new int[3];
        private readonly int[] _alignmentCounts = new int[13];
        private readonly int[] _chosen;
        private readonly int _nodeLimit;
        private int _nodes;
        public bool LimitReached { get; private set; }
        public int NodesVisited => _nodes;
        public ushort[] Completion { get; }

        public CompletionSearch(DraftRules rules, int[] required, int nodeLimit)
        {
            _rules = rules;
            _required = required;
            _nodeLimit = nodeLimit;
            _counts = new int[rules._roles.Length];
            _excluded = new int[rules._roles.Length];
            _chosen = Enumerable.Repeat(-1, rules.BucketCount).ToArray();
            for (var i = 0; i < required.Length; i++)
            {
                var role = rules._roles[i];
                _remainingRequiredCount += required[i];
                _remainingRequiredFactions[role.Faction] += required[i] * role.Weight;
                _remainingRequiredAlignments[role.Alignment] += required[i] * role.Weight;
            }
            Completion = new ushort[rules.BucketCount];
        }

        public bool Run() => Visit(0);

        private bool CanAdd(int index)
        {
            var role = _rules._roles[index];
            var reservedCopy = _counts[index] < _required[index] ? role.Weight : 0;
            return _counts[index] < role.MaxCount && _excluded[index] == 0 &&
                _factionCounts[role.Faction] + role.Weight + _remainingRequiredFactions[role.Faction] - reservedCopy <= _rules._factions[role.Faction].Maximum &&
                _alignmentCounts[role.Alignment] + role.Weight + _remainingRequiredAlignments[role.Alignment] - reservedCopy <= _rules._alignments[role.Alignment].Maximum;
        }

        private bool Visit(int depth)
        {
            if (++_nodes > _nodeLimit) { LimitReached = true; return false; }
            var remaining = _chosen.Length - depth;
            var deficit = _remainingRequiredCount;
            if (deficit > remaining) return false;
            if (remaining == 0)
            {
                for (var i = 0; i < _factionCounts.Length; i++) if (_factionCounts[i] < _rules._factions[i].Minimum) return false;
                for (var i = 0; i < _alignmentCounts.Length; i++) if (_alignmentCounts[i] < _rules._alignments[i].Minimum) return false;
                for (var i = 0; i < _chosen.Length; i++) Completion[i] = _rules._roles[_chosen[i]].Id;
                if (!TargetsPossible(false)) return false;
                return _rules._validateCompletion == null || _rules._validateCompletion(Completion);
            }

            var bucketToFill = -1;
            var smallest = int.MaxValue;
            Span<int> factionCapacity = stackalloc int[3];
            Span<int> alignmentCapacity = stackalloc int[13];
            Span<int> requiredCapacity = stackalloc int[_required.Length];
            Span<int> factionWeights = stackalloc int[3];
            Span<int> alignmentWeights = stackalloc int[13];
            factionCapacity.Clear();
            alignmentCapacity.Clear();
            requiredCapacity.Clear();
            for (var bucket = 0; bucket < _chosen.Length; bucket++)
            {
                if (_chosen[bucket] >= 0) continue;
                var previous = _rules._previousEquivalentBucket[bucket];
                var floor = previous >= 0 && _chosen[previous] >= 0 ? _chosen[previous] : 0;
                var count = 0;
                factionWeights.Clear();
                alignmentWeights.Clear();
                foreach (var index in _rules._buckets[bucket])
                {
                    if (index < floor || !CanAdd(index)) continue;
                    if (deficit == remaining && _counts[index] >= _required[index]) continue;
                    count++;
                    var role = _rules._roles[index];
                    factionWeights[role.Faction] = Math.Max(factionWeights[role.Faction], role.Weight);
                    alignmentWeights[role.Alignment] = Math.Max(alignmentWeights[role.Alignment], role.Weight);
                    requiredCapacity[index]++;
                }
                if (count == 0) return false;
                for (var i = 0; i < 3; i++) factionCapacity[i] += factionWeights[i];
                for (var i = 0; i < 13; i++) alignmentCapacity[i] += alignmentWeights[i];
                if ((previous < 0 || _chosen[previous] >= 0) && count < smallest)
                { bucketToFill = bucket; smallest = count; }
            }
            var capacity = 0;
            Span<int> factionRoleCapacity = stackalloc int[3];
            Span<int> alignmentRoleCapacity = stackalloc int[13];
            Span<int> factionSeats = stackalloc int[3];
            Span<int> alignmentSeats = stackalloc int[13];
            factionSeats.Clear();
            alignmentSeats.Clear();
            factionRoleCapacity.Clear();
            alignmentRoleCapacity.Clear();
            for (var i = 0; i < _required.Length; i++)
            {
                if (_required[i] - _counts[i] > requiredCapacity[i]) return false;
                var role = _rules._roles[i];
                var copies = Math.Min(requiredCapacity[i], role.MaxCount - _counts[i]);
                capacity += copies;
                factionRoleCapacity[role.Faction] += copies * role.Weight;
                alignmentRoleCapacity[role.Alignment] += copies * role.Weight;
                factionSeats[role.Faction] += copies;
                alignmentSeats[role.Alignment] += copies;
            }
            if (capacity < remaining) return false;
            capacity = 0;
            for (var i = 0; i < 3; i++)
                capacity += Math.Min(factionSeats[i], _rules._factions[i].Maximum - _factionCounts[i]);
            if (capacity < remaining) return false;
            capacity = 0;
            for (var i = 0; i < 13; i++)
                capacity += Math.Min(alignmentSeats[i], _rules._alignments[i].Maximum - _alignmentCounts[i]);
            if (capacity < remaining) return false;
            for (var i = 0; i < 3; i++)
                if (_factionCounts[i] + Math.Min(factionCapacity[i], factionRoleCapacity[i]) < _rules._factions[i].Minimum) return false;
            for (var i = 0; i < 13; i++)
                if (_alignmentCounts[i] + Math.Min(alignmentCapacity[i], alignmentRoleCapacity[i]) < _rules._alignments[i].Minimum) return false;
            if (!TargetsPossible(true)) return false;
            if (bucketToFill < 0) return false;

            var prior = _rules._previousEquivalentBucket[bucketToFill];
            var minimumIndex = prior >= 0 ? _chosen[prior] : 0;
            // Keep role order consistent with equivalent-bucket ordering.
            foreach (var index in _rules._buckets[bucketToFill])
            {
                if (index < minimumIndex || !CanAdd(index) || deficit == remaining && _counts[index] >= _required[index]) continue;
                var role = _rules._roles[index];
                var requiredCopy = _counts[index] < _required[index];
                if (requiredCopy)
                {
                    _remainingRequiredCount--;
                    _remainingRequiredFactions[role.Faction] -= role.Weight;
                    _remainingRequiredAlignments[role.Alignment] -= role.Weight;
                }
                foreach (var other in _rules._exclusiveIndices[index]) _excluded[other]++;
                _chosen[bucketToFill] = index;
                _counts[index]++;
                _factionCounts[role.Faction] += role.Weight;
                _alignmentCounts[role.Alignment] += role.Weight;
                if (Visit(depth + 1)) return true;
                foreach (var other in _rules._exclusiveIndices[index]) _excluded[other]--;
                if (requiredCopy)
                {
                    _remainingRequiredCount++;
                    _remainingRequiredFactions[role.Faction] += role.Weight;
                    _remainingRequiredAlignments[role.Alignment] += role.Weight;
                }
                _alignmentCounts[role.Alignment] -= role.Weight;
                _factionCounts[role.Faction] -= role.Weight;
                _counts[index]--;
                _chosen[bucketToFill] = -1;
                if (LimitReached) return false;
            }
            return false;
        }


        private bool TargetsPossible(bool allowFuture)
        {
            for (var index = 0; index < _counts.Length; index++)
            {
                var role = _rules._roles[index];
                if ((_counts[index] == 0 && _required[index] == 0) || role.RequiredTargetMask == 0) continue;
                var found = false;
                for (var target = 0; target < _counts.Length && !found; target++)
                {
                    if ((_rules._roles[target].TargetMask & role.RequiredTargetMask) == 0) continue;
                    if (_counts[target] > (target == index ? 1 : 0)) { found = true; break; }
                    if (!allowFuture || !CanAdd(target)) continue;
                    for (var bucket = 0; bucket < _chosen.Length; bucket++)
                        if (_chosen[bucket] < 0 && Array.IndexOf(_rules._buckets[bucket], target) >= 0) { found = true; break; }
                }
                if (!found) return false;
            }
            return true;
        }
    }
}
