
using TownOfUs.Options;
using MiraAPI.Utilities;
using MiraAPI.GameOptions;
using MiraAPI.Hud;
using TownOfUs.Buttons;
using UnityEngine;


namespace TownOfUs.Modules.DraftMode;

public sealed class DraftShuffleButton : TownOfUsButton
{
    public static void Show()
    {
        CustomButtonSingleton<DraftShuffleButton>.Instance.Disabled = false;
    }

    public static void Hide()
    {
        CustomButtonSingleton<DraftShuffleButton>.Instance.Disabled = true;
    }

    public static void ShowAndReset()
    {
        Show();
        CustomButtonSingleton<DraftShuffleButton>.Instance.SetUses(
            (int)OptionGroupSingleton<RoleOptions>.Instance.ShufflesPerPlayer.Value);
    }

    public static void HideAndReset()
    {
        Hide();
        CustomButtonSingleton<DraftShuffleButton>.Instance.SetUses(
            (int)OptionGroupSingleton<RoleOptions>.Instance.ShufflesPerPlayer.Value);
    }

    public override string Name => MiraLocaleManager.Get("TouDraftShuffleButton", "Shuffle");
    public override float InitialCooldown => 0.001f;
    public override float Cooldown => 0.001f;
    public override int MaxUses => (int)OptionGroupSingleton<RoleOptions>.Instance.ShufflesPerPlayer.Value;
    public override bool ZeroIsInfinite { get; set; } = true;
    public override ButtonLocation Location => ButtonLocation.BottomRight;
    public override Color TextOutlineColor => TownOfUsColors.Impostor;

    public override LoadableAsset<Sprite> Sprite => TouImpAssets.TraitorSelect;

    public override bool Disabled { get; set; } = true;
    public override bool Enabled(RoleBehaviour? role)
    {
        return DraftManager.IsDraftActive && !Disabled && MaxUses > 0 ;
    }

    public override bool CanUse()
    {
        var state = DraftManager.GetStateForPlayer(PlayerControl.LocalPlayer.PlayerId);
        return state != null && state.IsPickingNow && !state.HasPicked && !state.IsWithdrawn &&
               DraftScreenController.Instance != null && DraftScreenController.Instance.CanRequestShuffle &&
               DraftManager.IsDraftActive && !Disabled && MaxUses > 0 && UsesLeft > 0;
    }

    protected override void OnClick()
    {
        if (!DraftManager.IsDraftActive) return;
        DraftNetworkHelper.RequestShuffle();
    }

}