using UdonSharp;
using UnityEngine;

/// <summary>Inspector settings for the editor baker.</summary>
[UdonBehaviourSyncMode(BehaviourSyncMode.None)]
public class DungeonGenVRCComponent : UdonSharpBehaviour
{
    [Header("Dungeon Layout")]
    [Range(1, 7)] public int floorLevel = 1;
    [Tooltip("Integer, text, or 'random'.")]
    public string seed = "random";
    [Tooltip("OFF: map preview. ON: 3D world with in-world control panel.")]
    public bool dungeonMode = true;

    [Header("Observed Floor Rules")]
    [Tooltip("Observed: F2 sometimes has one missing boundary cell. This probability is an adjustable estimate.")]
    [Range(0f, 1f)] public float f2MissingChance = .35f;
    [Tooltip("Observed: F4 sometimes has 1 to 3 missing boundary cells. This probability is an adjustable estimate.")]
    [Range(0f, 1f)] public float f4MissingChance = .50f;

    [Header("VRChat In-World Generator")]
    [Range(2, 12)] public int bakedVariants = 4;

    [Header("3D Geometry")]
    [Min(8f)] public float cellSpacing = 26f;
    [Min(4f)] public float roomSize = 20f;
    [Min(1f)] public float corridorWidth = 5f;
    [Min(2f)] public float wallHeight = 6f;
    [Min(.1f)] public float wallThickness = .8f;
    public bool generateCeilings = false;
}
