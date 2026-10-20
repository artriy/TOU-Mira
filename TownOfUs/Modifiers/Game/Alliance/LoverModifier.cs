using System.Collections;
using HarmonyLib;
using InnerNet;
using MiraAPI.GameEnd;
using MiraAPI.GameOptions;
using MiraAPI.Modifiers;
using MiraAPI.Roles;
using MiraAPI.Utilities;
using Reactor.Networking.Attributes;
using Reactor.Utilities;
using TownOfUs.GameOver;
using TownOfUs.Interfaces;
using TownOfUs.Modifiers.Neutral;
using TownOfUs.Modules.DraftMode;
using TownOfUs.Options;
using TownOfUs.Options.Modifiers;
using TownOfUs.Options.Modifiers.Alliance;
using TownOfUs.Roles;
using TownOfUs.Roles.Neutral;
using TownOfUs.Roles.Other;
using UnityEngine;
using Random = System.Random;

namespace TownOfUs.Modifiers.Game.Alliance;

public sealed class LoverModifier : AllianceGameModifier, IWikiDiscoverable, IAssignableTargets
{
    public override ModifierUiConfiguration Configuration => new(
        TownOfUsColors.Lover,
        TmpSpriteUtils.CreateSpriteAsset(TouModifierIcons.Lover.LoadAsset(),
            "TouMira.Modifier.Alliance.Lover", 1.45f));
    public override string IdPart => "Lover";
    public override string ModifierName => MiraLocaleManager.Get($"TownOfUsMira.Modifier.{IdPart}");
    public override string IntroInfo => LoverString();
    public bool LoverDisconnected { get; internal set; }
    public string LoverDcString { get; internal set; } = " ";

    public override string GetDescription()
    {
        return LoverString();
    }

    public string GetAdvancedDescription()
    {
        return MiraLocaleManager.Get($"TownOfUsMira.Modifier.{IdPart}.WikiDescription")
            .Replace("<symbol>", "<color=#FF66CCFF>♥</color>") + MiscUtils.AppendOptionsText(GetType());
    }

    public string LoverString()
    {
        return LoverDisconnected || OtherLover == null ? LoverDcString : MiraLocaleManager.Get($"TownOfUsMira.Modifier.{IdPart}Info")
            .Replace("<player>", OtherLover.Data.PlayerName);
    }

    public override string Symbol => "♥";
    public override float IntroSize => 3f;
    public override Color FreeplayFileColor => new Color32(220, 220, 220, 255);

    public override bool DoesTasks =>
        (OtherLover == null || OtherLover.IsCrewmate()) &&
        Player.IsCrewmate() && !ForceDisableTasks; // Lovers do tasks if they are not lovers with an Evil

    public bool ForceDisableTasks;

    public override bool HideOnUi => false;
    public override LoadableAsset<Sprite>? ModifierIcon => TouModifierIcons.Lover;

    public PlayerControl? OtherLover { get; set; }

    public override int CustomAmount =>
        (int)OptionGroupSingleton<AllianceModifierOptions>.Instance.LoversChance.Value != 0 ? 2 : 0;

    public override int CustomChance => (int)OptionGroupSingleton<AllianceModifierOptions>.Instance.LoversChance.Value;
    public int Priority { get; set; } = 4;

    public void AssignTargets()
    {
        if (!RoleOptions.IsClassicRoleAssignment)
        {
            return;
        }

        foreach (var lover in PlayerControl.AllPlayerControls.ToArray().Where(x => x.HasModifier<LoverModifier>())
                     .ToList())
        {
            lover.RpcRemoveModifier<LoverModifier>();
        }

        Random random = new();
        if (random.Next(100) >= (int)OptionGroupSingleton<AllianceModifierOptions>.Instance.LoversChance.Value) return;

        var loveOpt = OptionGroupSingleton<LoversOptions>.Instance;
        var players = PlayerControl.AllPlayerControls.ToArray()
            .Where(x => !x.HasDied() && !x.Data.Disconnected &&
                !x.HasModifier<ExecutionerTargetModifier>() && !x.HasModifier<AllianceGameModifier>() &&
                !SpectatorRole.TrackedSpectators.Contains(x.PlayerId) &&
                (x.Data.Role is not IUnlovable unlovable || !unlovable.IsUnlovable) &&
                (loveOpt.NeutralLovers || !x.IsNeutral())).ToList();
        var killers = players.Where(x => x.IsImpostor() ||
            loveOpt.NeutralLovers && x.Is(RoleAlignment.NeutralKilling)).ToList();
        var nonKillers = players.Except(killers).ToList();
        var killerPercent = (int)loveOpt.LovingImpPercent;
        if (!DraftStartupRules.TryChooseLoverFactions(nonKillers.Count, killers.Count, killerPercent,
                random.Next(100), out var mixedPair))
        {
            if (OptionGroupSingleton<RoleOptions>.Instance.CurrentRoleDistribution() is RoleDistribution.Draft)
                DraftApplier.Abort("Draft cancelled: Lovers cannot form a distinct pair with Killer Probability and Neutral Lovers. Change the role list or Lovers options.");
            else
                Error("Lovers cannot form a distinct pair with the configured factions.");
            return;
        }
        var first = nonKillers[random.Next(nonKillers.Count)];
        nonKillers.Remove(first);
        var partners = mixedPair ? killers : nonKillers;
        RpcSetOtherLover(first, partners[random.Next(partners.Count)]);
    }

    public List<CustomButtonWikiDescription> Abilities { get; } = [];

    public override int GetAmountPerGame()
    {
        return 0;
    }

    public override int GetAssignmentChance()
    {
        return 0;
    }

    public override void OnActivate()
    {
        if (!Player.AmOwner)
        {
            return;
        }

        HudManager.Instance.Chat.gameObject.SetActive(true);
        var buttonArray = new []
            { TouChatAssets.LoveChatIdle.LoadAsset(), TouChatAssets.LoveChatHover.LoadAsset(), TouChatAssets.LoveChatOpen.LoadAsset()};
        HudManager.Instance.Chat.chatButton.transform.Find("Inactive").GetComponent<SpriteRenderer>().sprite = buttonArray[0];
        HudManager.Instance.Chat.chatButton.transform.Find("Active").GetComponent<SpriteRenderer>().sprite = buttonArray[1];
        HudManager.Instance.Chat.chatButton.transform.Find("Selected").GetComponent<SpriteRenderer>().sprite = buttonArray[2];
        if (TutorialManager.InstanceExists && OtherLover == null && Player.AmOwner && Player.IsHost() &&
            AmongUsClient.Instance.GameState != InnerNetClient.GameStates.Started)
        {
            Coroutines.Start(SetTutorialTarget(this, Player));
        }
    }

    private static IEnumerator SetTutorialTarget(LoverModifier loverMod, PlayerControl localPlr)
    {
        yield return new WaitForSeconds(0.01f);
        var options = OptionGroupSingleton<LoversOptions>.Instance;
        var players = PlayerControl.AllPlayerControls.ToArray()
            .Where(x => x != localPlr && !x.HasDied() && !x.Data.Disconnected &&
                !x.HasModifier<ExecutionerTargetModifier>() && !x.HasModifier<AllianceGameModifier>() &&
                (x.Data.Role is not IUnlovable unlovable || !unlovable.IsUnlovable) &&
                (options.NeutralLovers || !x.IsNeutral())).ToList();
        var localKiller = localPlr.IsImpostor() || options.NeutralLovers && localPlr.Is(RoleAlignment.NeutralKilling);
        var killers = players.Where(x => x.IsImpostor() ||
            options.NeutralLovers && x.Is(RoleAlignment.NeutralKilling)).ToList();
        var nonKillers = players.Except(killers).ToList();
        Random random = new();
        var partners = !localKiller && random.Next(100) < (int)options.LovingImpPercent ? killers : nonKillers;
        if (partners.Count == 0)
        {
            Error("Tutorial Lovers have no eligible partner.");
            yield break;
        }
        var randomTarget = partners[random.Next(partners.Count)];

        var sourceModifier = randomTarget.AddModifier<LoverModifier>();
        yield return new WaitForSeconds(0.01f);
        sourceModifier!.OtherLover = localPlr;
        sourceModifier.LoverDcString = MiraLocaleManager.Get("TownOfUsMira.Modifier.LoverInfoDisconnected")
            .Replace("<player>", localPlr.Data.PlayerName);
        loverMod!.OtherLover = randomTarget;
        loverMod.LoverDcString = MiraLocaleManager.Get("TownOfUsMira.Modifier.LoverInfoDisconnected")
            .Replace("<player>", randomTarget.Data.PlayerName);
    }

    public override void OnDeactivate()
    {
        if (!Player.AmOwner)
        {
            return;
        }

        HudManager.Instance.Chat.gameObject.SetActive(false);
        if (TutorialManager.InstanceExists)
        {
            var players = ModifierUtils.GetPlayersWithModifier<LoverModifier>().ToList();
            players.Do(x => x.RpcRemoveModifier<LoverModifier>());
        }
    }

    public override void OnMeetingStart()
    {
        base.OnMeetingStart();
        if (!Player.AmOwner)
        {
            return;
        }

        var buttonArray = new Sprite[]
        {
            TouChatAssets.NormalChatIdle.LoadAsset(), TouChatAssets.NormalChatHover.LoadAsset(),
            TouChatAssets.NormalChatOpen.LoadAsset()
        };
        HudManager.Instance.Chat.chatButton.transform.Find("Inactive").GetComponent<SpriteRenderer>().sprite =
            buttonArray[0];
        HudManager.Instance.Chat.chatButton.transform.Find("Active").GetComponent<SpriteRenderer>().sprite =
            buttonArray[1];
        HudManager.Instance.Chat.chatButton.transform.Find("Selected").GetComponent<SpriteRenderer>().sprite =
            buttonArray[2];
    }

    public override bool? DidWin(GameOverReason reason)
    {
        if (reason == CustomGameOver.GameOverReason<LoverGameOver>())
        {
            return true;
        }

        // Co-win: if the Jester is a Lover and gets voted out (NeutralGameOver winner),
        // both Lovers should be marked as winners.
        if (reason == CustomGameOver.GameOverReason<NeutralGameOver>())
        {
            var winners = EndGameResult.CachedWinners;
            if (winners != null && winners.Count == 1)
            {
                var winner = winners[0];
                var winnerRole = RoleManager.Instance.GetRole(winner.RoleWhenAlive);
                if (winnerRole is JesterRole)
                {
                    var winnerName = winner.PlayerName;
                    if (winnerName == Player.Data.PlayerName ||
                        (OtherLover != null && winnerName == OtherLover.Data.PlayerName))
                    {
                        return true;
                    }
                }
            }
        }

        return null;
    }

    public static bool WinConditionMet(LoverModifier[] lovers)
    {
        var bothLoversAlive = Helpers.GetAlivePlayers().Count(x => x.HasModifier<LoverModifier>()) >= 2;

        return Helpers.GetAlivePlayers().Count <= 3 && lovers.Length == 2 && bothLoversAlive;
    }

    public void OnRoundStart()
    {
        if (!Player.AmOwner)
        {
            return;
        }

        HudManager.Instance.Chat.SetVisible(true);
        var buttonArray = new []
                { TouChatAssets.LoveChatIdle.LoadAsset(), TouChatAssets.LoveChatHover.LoadAsset(), TouChatAssets.LoveChatOpen.LoadAsset()};
        HudManager.Instance.Chat.chatButton.transform.Find("Inactive").GetComponent<SpriteRenderer>().sprite = buttonArray[0];
        HudManager.Instance.Chat.chatButton.transform.Find("Active").GetComponent<SpriteRenderer>().sprite = buttonArray[1];
        HudManager.Instance.Chat.chatButton.transform.Find("Selected").GetComponent<SpriteRenderer>().sprite = buttonArray[2];
    }

    public PlayerControl? GetOtherLover()
    {
        return OtherLover;
    }

    /// <summary>
    /// Debug helper (Freeplay/Tutorial) to force-assign a pair of players as Lovers.
    /// </summary>
    public static void DebugSetLovers(PlayerControl loverA, PlayerControl loverB, bool clearExisting = true)
    {
        if (loverA == null || loverB == null || loverA == loverB)
        {
            return;
        }

        if (clearExisting)
        {
            foreach (var player in PlayerControl.AllPlayerControls.ToArray()
                         .Where(x => x != null && x.HasModifier<LoverModifier>()).ToList())
            {
                player.RpcRemoveModifier<LoverModifier>();
            }
        }

        // Uses the existing RPC path to ensure both sides are wired correctly.
        RpcSetOtherLover(loverA, loverB);
    }

    [MethodRpc((uint)TownOfUsRpc.SetOtherLover)]
    private static void RpcSetOtherLover(PlayerControl player, PlayerControl target)
    {
        if (PlayerControl.AllPlayerControls.ToArray().Where(x => x.HasModifier<LoverModifier>()).ToList().Count > 0)
        {
            Error("RpcSetOtherLover - Lovers Already Spawned!");
            return;
        }

        var targetModifier = target.AddModifier<LoverModifier>();
        var sourceModifier = player.AddModifier<LoverModifier>();
        targetModifier!.OtherLover = player;
        targetModifier.LoverDcString = MiraLocaleManager.Get("TownOfUsMira.Modifier.LoverInfoDisconnected")
            .Replace("<player>", player.Data.PlayerName);
        sourceModifier!.OtherLover = target;
        sourceModifier.LoverDcString = MiraLocaleManager.Get("TownOfUsMira.Modifier.LoverInfoDisconnected")
            .Replace("<player>", target.Data.PlayerName);
        if (!player.IsCrewmate() || !target.IsCrewmate())
        {
            targetModifier.ForceDisableTasks = true;
            sourceModifier.ForceDisableTasks = true;
        }
    }
}