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
