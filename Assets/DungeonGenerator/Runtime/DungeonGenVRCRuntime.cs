using UdonSharp;
using UnityEngine;
using UnityEngine.UI;
using VRC.SDKBase;
using VRC.Udon.Common.Interfaces;

/// <summary>
/// Udon runtime control: choose among editor-baked variants and sync the
/// selected index to everyone, including late joiners. No runtime C# baking.
/// </summary>
[UdonBehaviourSyncMode(BehaviourSyncMode.Manual)]
public class DungeonGenVRCRuntime : UdonSharpBehaviour
{
    public GameObject[] variants;
    public Transform[] startAnchors;
    public int[] variantSeeds;
    public Text statusLabel;

    [UdonSynced] private int chosenVariant;
    [UdonSynced] private int generationRevision;
    private int appliedRevision = -1;
    private int appliedVariant = -1;

    private void Start()
    {
        ApplyLayout(false);
    }

    // Unity Button.onClick -> UdonBehaviour.SendCustomEvent("GenerateNext").
    public void GenerateNext()
    {
        if (variants == null || variants.Length < 2) return;
        SendCustomNetworkEvent(NetworkEventTarget.Owner, nameof(GenerateOwned));
    }

    // Only the owner chooses the next layout; sync a small integer for everyone.
    public void GenerateOwned()
    {
        if (!Networking.IsOwner(gameObject)) return;
        if (variants == null || variants.Length < 2) return;
        int step = Random.Range(1, variants.Length);
        chosenVariant = (chosenVariant + step) % variants.Length;
        generationRevision++;
        ApplyLayout(true);
        RequestSerialization();
    }

    public override void OnDeserialization()
    {
        ApplyLayout(true);
    }

    private void ApplyLayout(bool teleportOnChange)
    {
        if (variants == null || variants.Length == 0) return;
        int index = chosenVariant;
        if (index < 0 || index >= variants.Length) index = 0;

        for (int i = 0; i < variants.Length; i++)
            if (variants[i] != null)
                variants[i].SetActive(i == index);

        bool changed = appliedRevision >= 0 &&
            (generationRevision != appliedRevision || index != appliedVariant);
        appliedRevision = generationRevision;
        appliedVariant = index;

        if (statusLabel != null)
        {
            string info = "Dungeon " + (index + 1) + "/" + variants.Length;
            if (variantSeeds != null && index < variantSeeds.Length)
                info += " | Seed " + variantSeeds[index];
            statusLabel.text = info;
        }

        if (teleportOnChange && changed && startAnchors != null &&
            index < startAnchors.Length && startAnchors[index] != null)
        {
            VRCPlayerApi player = Networking.LocalPlayer;
            if (Utilities.IsValid(player))
                player.TeleportTo(startAnchors[index].position,
                    startAnchors[index].rotation);
        }
    }
}
