// Unity 2022 / VRChat Worlds editor-only dungeon baker.
// This script must stay inside an Editor folder.
using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEditor.Events;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.SceneManagement;
using UnityEngine.UI;
using UdonSharpEditor;
using VRC.SDK3.Components;
using VRC.Udon;

public sealed class DungeonGenVRCWorldBaker : EditorWindow
{
    private enum MapMode { DungeonMap, Dungeon }
    private const string PreviewRoot = "DG_Generated";
    private const string RuntimeRoot = "DG_RuntimeCollection";
    private const string MaterialFolder = "Assets/DungeonGenerator/Generated";
    // Observed in-game footprints and puzzle room counts (F1-F6).
    // F7 keeps the previous estimate until new in-game samples are available.
    private static readonly int[] Widths = { 4, 5, 5, 6, 6, 5, 6 };
    private static readonly int[] Heights = { 5, 5, 5, 5, 5, 6, 6 };
    private static readonly int[] PuzzleCounts = { 3, 3, 2, 4, 3, 3, 5 };
    private static readonly Vector2Int[] Directions =
        { Vector2Int.up, Vector2Int.right, Vector2Int.down, Vector2Int.left };
    private static readonly Vector2Int[][] Shapes =
    {
        new[] { Vector2Int.zero, Vector2Int.right, Vector2Int.up, Vector2Int.one },
        new[] { Vector2Int.zero, Vector2Int.right, Vector2Int.right * 2, Vector2Int.right * 3 },
        new[] { Vector2Int.zero, Vector2Int.right, 2 * Vector2Int.right },
        new[] { Vector2Int.zero, Vector2Int.right, Vector2Int.up },
        new[] { Vector2Int.zero, Vector2Int.right }
    };
    private static readonly string[] ShapeNames = { "2x2", "1x4", "1x3", "L", "1x2" };

    [Serializable]
    private sealed class Room
    {
        public int Id;
        public string Kind;
        public string Shape;
        public int Rotation;
        public List<Vector2Int> Cells = new List<Vector2Int>();
    }

    private struct Edge
    {
        public Vector2Int A, B;
        public Edge(Vector2Int a, Vector2Int b) { A = a; B = b; }
    }

    private sealed class Layout
    {
        public int Floor, Width, Height, Seed, PuzzleTarget;
        public Vector2Int Start, First, Blood;
        public readonly HashSet<Vector2Int> MissingCells = new HashSet<Vector2Int>();
        public readonly List<Edge> Edges = new List<Edge>();
        public readonly List<Room> Rooms = new List<Room>();
        public readonly Dictionary<Vector2Int, int> CellRoom =
            new Dictionary<Vector2Int, int>();
        public readonly HashSet<Vector2Int> Critical = new HashSet<Vector2Int>();
    }

    private int floor = 1;
    private string seedText = "random";
    private MapMode mode = MapMode.Dungeon;
    private int variants = 4;
    private float spacing = 26f, roomSize = 20f, corridorWidth = 5f;
    private float wallHeight = 6f, wallThickness = .8f;
    // Frequency is configurable because observations establish that missing
    // tiles sometimes occur, but not how often they occur.
    private float f2MissingChance = .35f, f4MissingChance = .50f;
    private bool ceiling;
    private int lastSeed;
    private readonly Dictionary<string, Material> materialCache =
        new Dictionary<string, Material>();

    [MenuItem("Tools/Dungeon Generator/VRChat World Baker")]
    public static void OpenWindow()
    {
        GetWindow<DungeonGenVRCWorldBaker>("DungeonGeneratorVRC").minSize =
            new Vector2(340f, 350f);
    }

    [MenuItem("Tools/Dungeon Generator/Create Component Controller")]
    public static void CreateComponentController()
    {
        GameObject existing = GameObject.Find("DungeonGenerator");
        if (existing != null)
        {
            Selection.activeGameObject = existing;
            return;
        }
        GameObject obj = new GameObject("DungeonGenerator");
        Undo.RegisterCreatedObjectUndo(obj, "Create DungeonGeneratorVRC component");
        obj.AddUdonSharpComponent<DungeonGenVRCComponent>();
        Selection.activeGameObject = obj;
    }

    [MenuItem("Tools/Dungeon Generator/Validate Observed Floor Rules")]
    public static void ValidateObservedFloorRules()
    {
        const int samplesPerFloor = 40;
        int tested = 0, f2Missing = 0, f4Missing = 0, f6P4 = 0;
        try
        {
            for (int testedFloor = 1; testedFloor <= 7; testedFloor++)
            {
                for (int i = 0; i < samplesPerFloor; i++)
                {
                    Layout layout = GenerateLayout(testedFloor,
                        unchecked(84271 + 1709 * i + testedFloor * 37), .35f, .50f);
                    VerifyTree(layout);

                    bool correctSize = testedFloor == 1
                        ? ((layout.Width == 4 && layout.Height == 5) ||
                           (layout.Width == 5 && layout.Height == 4))
                        : testedFloor == 4 || testedFloor == 5
                            ? ((layout.Width == 6 && layout.Height == 5) ||
                               (layout.Width == 5 && layout.Height == 6))
                            : (layout.Width == Widths[testedFloor - 1] &&
                               layout.Height == Heights[testedFloor - 1]);

                    if (!correctSize)
                        throw new InvalidOperationException(
                            "Unexpected dimensions at F" + testedFloor);

                    if (testedFloor == 6 &&
                        (layout.PuzzleTarget < 3 || layout.PuzzleTarget > 4))
                        throw new InvalidOperationException("F6 Puzzle count outside P3-P4.");

                    if (testedFloor == 2 && layout.MissingCells.Count == 1)
                        f2Missing++;
                    if (testedFloor == 4 && layout.MissingCells.Count > 0)
                        f4Missing++;
                    if (testedFloor == 6 && layout.PuzzleTarget == 4)
                        f6P4++;
                    tested++;
                }
            }
            Debug.Log("DungeonGeneratorVRC: validated " + tested +
                " floor layouts. F2 missing-one examples=" + f2Missing +
                ", F4 missing-edge examples=" + f4Missing +
                ", F6 P4 examples=" + f6P4 + ".");
        }
        catch (Exception ex)
        {
            Debug.LogException(ex);
            EditorUtility.DisplayDialog("Dungeon Generator",
                "Floor validation failed: " + ex.Message, "OK");
        }
    }

    private void OnGUI()
    {
        EditorGUILayout.LabelField("VRChat Dungeon Generator", EditorStyles.boldLabel);
        EditorGUILayout.HelpBox(
            "Editor generation saves geometry in the Scene. In-world buttons switch " +
            "between editor-baked variants; Udon cannot run this C# baker.",
            MessageType.Info);
        floor = EditorGUILayout.IntSlider("Floor", floor, 1, 7);
        seedText = EditorGUILayout.TextField("Seed", seedText);
        EditorGUILayout.HelpBox(
            "Observed: F1 4x5/5x4 P3, F2 5x5 P3, F3 5x5 P2, " +
            "F4 6x5/5x6 P4, F5 6x5/5x6 P3, F6 5x6 P3-4. " +
            "F7 remains provisional.", MessageType.None);
        f2MissingChance = EditorGUILayout.Slider(
            "F2 Missing 1 Tile Chance", f2MissingChance, 0f, 1f);
        f4MissingChance = EditorGUILayout.Slider(
            "F4 Missing 1-3 Edge Tiles Chance", f4MissingChance, 0f, 1f);
        mode = (MapMode)EditorGUILayout.EnumPopup("Mode", mode);
        variants = EditorGUILayout.IntSlider("Baked Variants", variants, 2, 12);
        spacing = EditorGUILayout.FloatField("Cell Spacing", spacing);
        roomSize = EditorGUILayout.FloatField("Room Size", roomSize);
        corridorWidth = EditorGUILayout.FloatField("Corridor Width", corridorWidth);
        wallHeight = EditorGUILayout.FloatField("Wall Height", wallHeight);
        wallThickness = EditorGUILayout.FloatField("Wall Thickness", wallThickness);
        ceiling = EditorGUILayout.Toggle("Generate Ceilings", ceiling);
        using (new EditorGUI.DisabledScope(EditorApplication.isPlayingOrWillChangePlaymode))
            if (GUILayout.Button("Generate / Regenerate", GUILayout.Height(35)))
                Bake();
        if (GUILayout.Button("Save Scene"))
            EditorSceneManager.SaveOpenScenes();
        EditorGUILayout.LabelField("Last Seed", lastSeed.ToString());
    }

    public static void BakeFromComponent(DungeonGenVRCComponent config)
    {
        if (config == null || EditorApplication.isPlayingOrWillChangePlaymode) return;
        DungeonGenVRCWorldBaker baker = CreateInstance<DungeonGenVRCWorldBaker>();
        try
        {
            baker.floor = config.floorLevel;
            baker.seedText = config.seed;
            baker.mode = config.dungeonMode ? MapMode.Dungeon : MapMode.DungeonMap;
            baker.variants = config.bakedVariants;
            baker.spacing = config.cellSpacing;
            baker.roomSize = config.roomSize;
            baker.corridorWidth = config.corridorWidth;
            baker.wallHeight = config.wallHeight;
            baker.wallThickness = config.wallThickness;
            baker.f2MissingChance = config.f2MissingChance;
            baker.f4MissingChance = config.f4MissingChance;
            baker.ceiling = config.generateCeilings;
            baker.Bake();
        }
        finally { DestroyImmediate(baker); }
    }

    private void Bake()
    {
        if (EditorApplication.isPlayingOrWillChangePlaymode) return;
        spacing = Mathf.Max(8f, spacing);
        roomSize = Mathf.Clamp(roomSize, 4f, spacing - 1f);
        corridorWidth = Mathf.Clamp(corridorWidth, 1f, roomSize * .75f);
        wallHeight = Mathf.Max(2f, wallHeight);
        wallThickness = Mathf.Clamp(wallThickness, .1f, roomSize * .15f);
        variants = Mathf.Clamp(variants, 2, 12);
        f2MissingChance = Mathf.Clamp01(f2MissingChance);
        f4MissingChance = Mathf.Clamp01(f4MissingChance);
        materialCache.Clear();

        try
        {
            int initial = ResolveSeed(seedText);
            int count = mode == MapMode.Dungeon ? variants : 1;
            Layout[] layouts = new Layout[count];
            int[] seeds = new int[count];
            for (int i = 0; i < count; i++)
            {
                seeds[i] = i == 0 ? initial : unchecked(initial + 7919 * i);
                layouts[i] = GenerateLayout(Mathf.Clamp(floor, 1, 7), seeds[i],
                    f2MissingChance, f4MissingChance);
                VerifyTree(layouts[i]);
            }
            lastSeed = initial;

            // Build only after validation; never touch the VRC Scene Descriptor.
            DeleteRoot(PreviewRoot);
            DeleteRoot(RuntimeRoot);
            string rootName = mode == MapMode.Dungeon ? RuntimeRoot : PreviewRoot;
            GameObject root = new GameObject(rootName);
            Undo.RegisterCreatedObjectUndo(root, "Bake DungeonGeneratorVRC");

            GameObject[] objects = new GameObject[count];
            Transform[] anchors = new Transform[count];
            Vector2Int forward = layouts[0].First - layouts[0].Start;
            Vector3 look = new Vector3(forward.x, 0f, forward.y);

            for (int i = 0; i < count; i++)
            {
                GameObject map = new GameObject("DG_Layout_" + i.ToString("D2") +
                    "_Seed_" + seeds[i]);
                map.transform.SetParent(root.transform, false);
                float grid = mode == MapMode.Dungeon ? spacing : 5f;
                map.transform.localPosition = mode == MapMode.Dungeon
                    ? new Vector3(-layouts[i].Start.x * grid, 0f,
                        -layouts[i].Start.y * grid) : Vector3.zero;

                foreach (Room room in layouts[i].Rooms) BuildRoom(map.transform, layouts[i], room);
                foreach (Edge edge in layouts[i].Edges)
                    if (layouts[i].CellRoom[edge.A] != layouts[i].CellRoom[edge.B])
                        BuildCorridor(map.transform, layouts[i], edge);

                GameObject anchor = new GameObject("DG_StartAnchor");
                anchor.transform.SetParent(map.transform, false);
                anchor.transform.localPosition = new Vector3(layouts[i].Start.x * grid,
                    .1f, layouts[i].Start.y * grid);
                Vector2Int step = layouts[i].First - layouts[i].Start;
                anchor.transform.localRotation =
                    Quaternion.LookRotation(new Vector3(step.x, 0f, step.y), Vector3.up);
                objects[i] = map;
                anchors[i] = anchor.transform;
                map.SetActive(i == 0);
            }

            if (mode == MapMode.Dungeon)
            {
                GameObject spawn = new GameObject("DG_StartSpawn");
                spawn.transform.SetParent(root.transform, false);
                spawn.transform.position = new Vector3(0f, .12f, 0f);
                spawn.transform.rotation = Quaternion.LookRotation(look, Vector3.up);
                SetWorldSpawn(spawn.transform);

                GameObject manager = new GameObject("DG_RuntimeManager");
                manager.transform.SetParent(root.transform, false);
                DungeonGenVRCRuntime runtime =
                    manager.AddUdonSharpComponent<DungeonGenVRCRuntime>();
                GameObject panel = CreatePanel(root.transform,
                    manager.GetComponent<UdonBehaviour>(), look, out Text status);
                runtime.variants = objects;
                runtime.startAnchors = anchors;
                runtime.variantSeeds = seeds;
                runtime.statusLabel = status;
                runtime.ApplyProxyModifications();
            }

            AssetDatabase.SaveAssets();
            EditorSceneManager.MarkSceneDirty(SceneManager.GetActiveScene());
            Selection.activeGameObject = root;
            Debug.Log("DungeonGeneratorVRC: baked " + count + " F" + floor +
                " layout(s); initial seed " + initial);
        }
        catch (Exception ex)
        {
            Debug.LogException(ex);
            EditorUtility.DisplayDialog("DungeonGeneratorVRC",
                "Generation failed: " + ex.Message, "OK");
        }
    }

    private static void DeleteRoot(string name)
    {
        GameObject root = GameObject.Find(name);
        if (root != null) Undo.DestroyObjectImmediate(root);
    }

    private static int ResolveSeed(string text)
    {
        string value = (text ?? "").Trim();
        if (string.Equals(value, "random", StringComparison.OrdinalIgnoreCase))
            return Guid.NewGuid().GetHashCode();
        if (int.TryParse(value, out int number)) return number;
        unchecked
        {
            uint hash = 2166136261;
            foreach (char ch in value) { hash ^= ch; hash *= 16777619; }
            return (int)hash;
        }
    }

    private static Vector2Int Rotate(Vector2Int p, int rotation)
    {
        switch (rotation & 3)
        {
            case 1: return new Vector2Int(-p.y, p.x);
            case 2: return new Vector2Int(-p.x, -p.y);
            case 3: return new Vector2Int(p.y, -p.x);
            default: return p;
        }
    }

    private static bool HasEdge(List<Edge> edges, Vector2Int a, Vector2Int b)
    {
        foreach (Edge edge in edges)
            if ((edge.A == a && edge.B == b) || (edge.A == b && edge.B == a))
                return true;
        return false;
    }

    private static bool ShapeConnected(List<Vector2Int> cells, List<Edge> edges)
    {
        HashSet<Vector2Int> group = new HashSet<Vector2Int>(cells);
        HashSet<Vector2Int> seen = new HashSet<Vector2Int> { cells[0] };
        Queue<Vector2Int> queue = new Queue<Vector2Int>();
        queue.Enqueue(cells[0]);
        while (queue.Count > 0)
        {
            Vector2Int cell = queue.Dequeue();
            foreach (Vector2Int d in Directions)
            {
                Vector2Int next = cell + d;
                if (group.Contains(next) && !seen.Contains(next) &&
                    HasEdge(edges, cell, next))
                { seen.Add(next); queue.Enqueue(next); }
            }
        }
        return seen.Count == cells.Count;
    }

    private static Layout GenerateLayout(int floor, int seed,
        float missingF2Chance, float missingF4Chance)
    {
        // Retry with deterministic attempt seeds when the observed special
        // room quota cannot fit on bonus branches or a gap disconnects the map.
        for (int attempt = 0; attempt < 256; attempt++)
        {
            Layout candidate = TryGenerateLayout(floor, seed, attempt,
                missingF2Chance, missingF4Chance);
            if (candidate != null) return candidate;
        }
        throw new InvalidOperationException(
            "Could not generate a connected dungeon with all required puzzle rooms.");
    }

    private static Layout TryGenerateLayout(int floor, int seed, int attempt,
        float missingF2Chance, float missingF4Chance)
    {
        System.Random rng = new System.Random(unchecked(seed + attempt * 104729));
        Layout layout = new Layout();
        layout.Floor = floor;
        layout.Width = Widths[floor - 1];
        layout.Height = Heights[floor - 1];
        layout.Seed = seed;
        if ((floor == 1 || floor == 4 || floor == 5) && rng.Next(2) == 0)
        {
            int oldWidth = layout.Width;
            layout.Width = layout.Height;
            layout.Height = oldWidth;
        }
        layout.PuzzleTarget = floor == 6 ? rng.Next(3, 5) : PuzzleCounts[floor - 1];
        int w = layout.Width, h = layout.Height;
        int side = rng.Next(4);
        layout.Start = side == 0 ? new Vector2Int(0, rng.Next(h)) :
            side == 1 ? new Vector2Int(w - 1, rng.Next(h)) :
            side == 2 ? new Vector2Int(rng.Next(w), 0) :
                        new Vector2Int(rng.Next(w), h - 1);
        Vector2Int inward = side == 0 ? Vector2Int.right :
            side == 1 ? Vector2Int.left :
            side == 2 ? Vector2Int.up : Vector2Int.down;
        layout.First = layout.Start + inward;

        // Reserve a far perimeter cell for the terminal Blood Room.
        int far = -1;
        List<Vector2Int> bloodChoices = new List<Vector2Int>();
        for (int y = 0; y < h; y++) for (int x = 0; x < w; x++)
        {
            Vector2Int p = new Vector2Int(x, y);
            if (p == layout.Start || p == layout.First ||
                !(x == 0 || y == 0 || x == w - 1 || y == h - 1)) continue;
            int distance = Mathf.Abs(x - layout.Start.x) + Mathf.Abs(y - layout.Start.y);
            if (distance > far) { bloodChoices.Clear(); far = distance; }
            if (distance == far) bloodChoices.Add(p);
        }
        layout.Blood = bloodChoices[rng.Next(bloodChoices.Count)];

        int missingTarget = 0;
        if (floor == 2 && rng.NextDouble() < missingF2Chance)
            missingTarget = 1;
        else if (floor == 4 && rng.NextDouble() < missingF4Chance)
            missingTarget = rng.Next(1, 4);
        if (!ChooseMissingBoundaryCells(layout, rng, missingTarget))
            return null;

        // DFS spanning tree: Start is always a leaf and has exactly one exit.
        HashSet<Vector2Int> used = new HashSet<Vector2Int>
            { layout.Start, layout.First };
        layout.Edges.Add(new Edge(layout.Start, layout.First));
        Stack<Vector2Int> stack = new Stack<Vector2Int>();
        stack.Push(layout.First);
        while (stack.Count > 0)
        {
            Vector2Int p = stack.Peek();
            List<Vector2Int> neighbors = new List<Vector2Int>();
            foreach (Vector2Int d in Directions)
            {
                Vector2Int n = p + d;
                if (n.x >= 0 && n.y >= 0 && n.x < w && n.y < h &&
                    n != layout.Blood && !layout.MissingCells.Contains(n) &&
                    !used.Contains(n)) neighbors.Add(n);
            }
            if (neighbors.Count == 0) { stack.Pop(); continue; }
            Vector2Int chosen = neighbors[rng.Next(neighbors.Count)];
            used.Add(chosen);
            layout.Edges.Add(new Edge(p, chosen));
            stack.Push(chosen);
        }
        if (used.Count != w * h - layout.MissingCells.Count - 1)
            return null;

        // Attach Blood by exactly one edge.
        Dictionary<Vector2Int, int> distances = Distances(layout.Start, layout.Edges);
        int bestDepth = -1;
        Vector2Int bloodParent = Vector2Int.zero;
        foreach (Vector2Int d in Directions)
        {
            Vector2Int p = layout.Blood + d;
            if (p == layout.Start || !distances.ContainsKey(p)) continue;
            if (distances[p] > bestDepth)
            { bloodParent = p; bestDepth = distances[p]; }
        }
        if (bestDepth < 0) throw new InvalidOperationException("Blood cannot connect.");
        layout.Edges.Add(new Edge(bloodParent, layout.Blood));

        Dictionary<Vector2Int, Vector2Int> parent = new Dictionary<Vector2Int, Vector2Int>();
        Queue<Vector2Int> queue = new Queue<Vector2Int>();
        parent[layout.Start] = layout.Start;
        queue.Enqueue(layout.Start);
        while (queue.Count > 0)
        {
            Vector2Int current = queue.Dequeue();
            foreach (Vector2Int d in Directions)
            {
                Vector2Int next = current + d;
                if (parent.ContainsKey(next) || !HasEdge(layout.Edges, current, next)) continue;
                parent[next] = current;
                queue.Enqueue(next);
            }
        }
        List<Vector2Int> path = new List<Vector2Int>();
        for (Vector2Int at = layout.Blood; ; at = parent[at])
        { path.Add(at); if (at == layout.Start) break; }
        path.Reverse();
        foreach (Vector2Int p in path) layout.Critical.Add(p);
        Vector2Int fairy = path.Count > 2 ? path[path.Count / 2] : layout.First;

        Dictionary<Vector2Int, string> types = new Dictionary<Vector2Int, string>();
        types[layout.Start] = "Start";
        types[layout.Blood] = "Blood";
        if (fairy != layout.Start && fairy != layout.Blood) types[fairy] = "Fairy";

        List<Vector2Int> bonus = new List<Vector2Int>();
        for (int y = 0; y < h; y++) for (int x = 0; x < w; x++)
        {
            Vector2Int p = new Vector2Int(x, y);
            if (layout.MissingCells.Contains(p) ||
                layout.Critical.Contains(p) || types.ContainsKey(p)) continue;
            bonus.Add(p);
        }
        // Put special rooms on leaves first.
        bonus.Sort((a, b) => Degree(a, layout.Edges).CompareTo(Degree(b, layout.Edges)));
        int requiredBonus = layout.PuzzleTarget + (floor >= 3 ? 1 : 0) + 1;
        if (bonus.Count < requiredBonus) return null;
        int specialCount = layout.PuzzleTarget;
        int index = 0;
        for (; index < specialCount; index++) types[bonus[index]] = "Puzzle";
        if (floor >= 3 && index < bonus.Count) types[bonus[index++]] = "Trap";
        if (index < bonus.Count) types[bonus[index]] = "Miniboss";

        foreach (KeyValuePair<Vector2Int, string> pair in types)
            AddRoom(layout, pair.Value, "1x1", 0,
                new List<Vector2Int> { pair.Key });

        HashSet<Vector2Int> remaining = new HashSet<Vector2Int>();
        for (int y = 0; y < h; y++) for (int x = 0; x < w; x++)
        {
            Vector2Int p = new Vector2Int(x, y);
            if (!types.ContainsKey(p) && !layout.MissingCells.Contains(p))
                remaining.Add(p);
        }

        while (remaining.Count > 0)
        {
            List<Vector2Int> pivots = new List<Vector2Int>(remaining);
            pivots.Sort((a, b) => a.x != b.x ? a.x.CompareTo(b.x) : a.y.CompareTo(b.y));
            int bestScore = -1, bestShape = -1, bestRotation = 0;
            List<Vector2Int> bestCells = null;
            for (int shape = 0; shape < Shapes.Length; shape++)
            for (int rotation = 0; rotation < 4; rotation++)
            foreach (Vector2Int pivot in pivots)
            {
                List<Vector2Int> cells = new List<Vector2Int>();
                bool valid = true;
                foreach (Vector2Int offset in Shapes[shape])
                {
                    Vector2Int p = pivot + Rotate(offset, rotation);
                    if (!remaining.Contains(p)) { valid = false; break; }
                    cells.Add(p);
                }
                if (!valid || !ShapeConnected(cells, layout.Edges)) continue;
                int score = cells.Count * 12 + rng.Next(12);
                if (score <= bestScore) continue;
                bestScore = score; bestShape = shape;
                bestRotation = rotation; bestCells = cells;
            }
            if (bestCells == null)
            {
                Vector2Int only = pivots[rng.Next(pivots.Count)];
                bestCells = new List<Vector2Int> { only };
                AddRoom(layout, "Normal", "1x1", 0, bestCells);
            }
            else AddRoom(layout, "Normal", ShapeNames[bestShape], bestRotation, bestCells);
            foreach (Vector2Int p in bestCells) remaining.Remove(p);
        }
        return layout;
    }

    // Only the outer boundary may be missing. Never remove Start, the
    // first connected cell, or Blood. Reject cuts that isolate any cell
    // when Blood is held aside as the terminal leaf.
    private static bool ChooseMissingBoundaryCells(Layout layout, System.Random rng,
        int count)
    {
        if (count == 0) return true;
        List<Vector2Int> choices = new List<Vector2Int>();
        for (int y = 0; y < layout.Height; y++)
        for (int x = 0; x < layout.Width; x++)
        {
            Vector2Int p = new Vector2Int(x, y);
            if (p == layout.Start || p == layout.First || p == layout.Blood)
                continue;
            if (x == 0 || y == 0 || x == layout.Width - 1 || y == layout.Height - 1)
                choices.Add(p);
        }
        for (int i = choices.Count - 1; i > 0; i--)
        {
            int j = rng.Next(i + 1);
            Vector2Int tmp = choices[i]; choices[i] = choices[j]; choices[j] = tmp;
        }

        foreach (Vector2Int p in choices)
        {
            layout.MissingCells.Add(p);
            if (!IsRemainingGridConnected(layout))
                layout.MissingCells.Remove(p);
            if (layout.MissingCells.Count == count) return true;
        }
        return false;
    }

    private static bool IsRemainingGridConnected(Layout layout)
    {
        HashSet<Vector2Int> seen = new HashSet<Vector2Int>();
        Queue<Vector2Int> queue = new Queue<Vector2Int>();
        seen.Add(layout.Start);
        queue.Enqueue(layout.Start);

        while (queue.Count > 0)
        {
            Vector2Int p = queue.Dequeue();
            foreach (Vector2Int d in Directions)
            {
                Vector2Int next = p + d;
                if (next.x < 0 || next.y < 0 ||
                    next.x >= layout.Width || next.y >= layout.Height ||
                    next == layout.Blood || layout.MissingCells.Contains(next) ||
                    !seen.Add(next)) continue;
                queue.Enqueue(next);
            }
        }
        int usableWithoutBlood = layout.Width * layout.Height
            - layout.MissingCells.Count - 1;
        if (seen.Count != usableWithoutBlood) return false;

        foreach (Vector2Int direction in Directions)
        {
            Vector2Int neighbor = layout.Blood + direction;
            if (neighbor != layout.Start && seen.Contains(neighbor))
                return true;
        }
        return false;
    }

    private static Dictionary<Vector2Int, int> Distances(Vector2Int origin, List<Edge> edges)
    {
        Dictionary<Vector2Int, int> dist = new Dictionary<Vector2Int, int>();
        Queue<Vector2Int> queue = new Queue<Vector2Int>();
        dist[origin] = 0;
        queue.Enqueue(origin);
        while (queue.Count > 0)
        {
            Vector2Int current = queue.Dequeue();
            foreach (Vector2Int direction in Directions)
            {
                Vector2Int next = current + direction;
                if (dist.ContainsKey(next) || !HasEdge(edges, current, next)) continue;
                dist[next] = dist[current] + 1;
                queue.Enqueue(next);
            }
        }
        return dist;
    }

    private static int Degree(Vector2Int p, List<Edge> edges)
    {
        int count = 0;
        foreach (Edge edge in edges)
            if (edge.A == p || edge.B == p) count++;
        return count;
    }

    private static void AddRoom(Layout layout, string kind, string shape,
        int rotation, List<Vector2Int> cells)
    {
        Room room = new Room();
        room.Id = layout.Rooms.Count;
        room.Kind = kind; room.Shape = shape; room.Rotation = rotation;
        room.Cells.AddRange(cells);
        layout.Rooms.Add(room);
        foreach (Vector2Int p in cells) layout.CellRoom[p] = room.Id;
    }

    private static void VerifyTree(Layout layout)
    {
        int cells = layout.Width * layout.Height - layout.MissingCells.Count;
        if (layout.CellRoom.Count != cells || layout.Edges.Count != cells - 1)
            throw new InvalidOperationException("Incorrect occupied cell/edge count.");

        foreach (Vector2Int p in layout.MissingCells)
        {
            if (layout.CellRoom.ContainsKey(p) ||
                !(p.x == 0 || p.y == 0 ||
                  p.x == layout.Width - 1 || p.y == layout.Height - 1))
                throw new InvalidOperationException("Invalid missing edge cell.");
        }

        int puzzles = 0;
        foreach (Room room in layout.Rooms)
            if (room.Kind == "Puzzle") puzzles++;
        if (puzzles != layout.PuzzleTarget)
            throw new InvalidOperationException("Incorrect number of Puzzle rooms.");

        HashSet<string> rooms = new HashSet<string>();
        int startDegree = 0, bloodDegree = 0;
        foreach (Edge edge in layout.Edges)
        {
            if (!layout.CellRoom.ContainsKey(edge.A) ||
                !layout.CellRoom.ContainsKey(edge.B))
                throw new InvalidOperationException("Connection to missing cell.");
            int a = layout.CellRoom[edge.A], b = layout.CellRoom[edge.B];
            if (a == b) continue;
            string key = Mathf.Min(a, b) + "/" + Mathf.Max(a, b);
            if (!rooms.Add(key)) throw new InvalidOperationException("Duplicate room edge.");
            if (edge.A == layout.Start || edge.B == layout.Start) startDegree++;
            if (edge.A == layout.Blood || edge.B == layout.Blood) bloodDegree++;
        }
        if (rooms.Count != layout.Rooms.Count - 1 ||
            startDegree != 1 || bloodDegree != 1)
            throw new InvalidOperationException("Invalid room tree.");
        if (layout.Floor != 2 && layout.Floor != 4 &&
            layout.MissingCells.Count != 0)
            throw new InvalidOperationException("Unexpected missing cells.");
        if (layout.Floor == 2 && layout.MissingCells.Count > 1)
            throw new InvalidOperationException("F2 exceeds missing tile limit.");
        if (layout.Floor == 4 && layout.MissingCells.Count > 3)
            throw new InvalidOperationException("F4 exceeds missing tile limit.");
    }

    private static Color ColorFor(string kind)
    {
        switch (kind)
        {
            case "Start": return new Color(.06f, .65f, .05f);
            case "Blood": return Color.red;
            case "Fairy": return new Color(1f, .50f, .75f);
            case "Puzzle": return new Color(.59f, .27f, .85f);
            case "Trap": return new Color(1f, .49f, .12f);
            case "Miniboss": return Color.yellow;
            default: return new Color(.60f, .30f, .04f);
        }
    }

    private Material GetMaterial(Color color)
    {
        if (!AssetDatabase.IsValidFolder(MaterialFolder))
            AssetDatabase.CreateFolder("Assets/DungeonGenerator", "Generated");
        string key = ColorUtility.ToHtmlStringRGBA(color);
        if (materialCache.TryGetValue(key, out Material cached) && cached != null)
            return cached;
        string path = MaterialFolder + "/DG_" + key + ".mat";
        Material mat = AssetDatabase.LoadAssetAtPath<Material>(path);
        if (mat == null)
        {
            Shader shader = Shader.Find("Standard");
            if (shader == null) shader = Shader.Find("Universal Render Pipeline/Lit");
            if (shader == null) throw new InvalidOperationException("No suitable shader.");
            mat = new Material(shader);
            mat.color = color;
            AssetDatabase.CreateAsset(mat, path);
        }
        materialCache[key] = mat;
        return mat;
    }

    private void Cube(Transform parent, string name, Vector3 position,
        Vector3 size, Color color)
    {
        GameObject obj = GameObject.CreatePrimitive(PrimitiveType.Cube);
        obj.name = name;
        obj.transform.SetParent(parent, false);
        obj.transform.localPosition = position;
        obj.transform.localScale = size;
        obj.GetComponent<Renderer>().sharedMaterial = GetMaterial(color);
    }

    private void BuildRoom(Transform parent, Layout layout, Room room)
    {
        GameObject go = new GameObject("Room_" + room.Id + "_" + room.Kind +
            "_" + room.Shape + "_R" + (90 * room.Rotation));
        go.transform.SetParent(parent, false);
        Transform holder = go.transform;
        float grid = mode == MapMode.Dungeon ? spacing : 5f;
        float size = mode == MapMode.Dungeon ? roomSize : 4f;
        float thickness = .65f;
        Color floorColor = mode == MapMode.Dungeon
            ? Color.Lerp(new Color(.38f, .36f, .34f), ColorFor(room.Kind), .12f)
            : ColorFor(room.Kind);
        HashSet<Vector2Int> members = new HashSet<Vector2Int>(room.Cells);
        foreach (Vector2Int cell in room.Cells)
        {
            Vector3 pos = new Vector3(cell.x * grid, 0f, cell.y * grid);
            Cube(holder, "Floor_" + cell, pos + Vector3.down * thickness * .5f,
                new Vector3(size, thickness, size), floorColor);
            if (mode == MapMode.Dungeon && ceiling)
                Cube(holder, "Ceiling_" + cell,
                    pos + Vector3.up * (wallHeight + wallThickness * .5f),
                    new Vector3(size, wallThickness, size), new Color(.24f, .24f, .26f));

            if (mode != MapMode.Dungeon) continue;
            foreach (Vector2Int direction in Directions)
            {
                if (members.Contains(cell + direction)) continue;
                bool hasDoor = HasEdge(layout.Edges, cell, cell + direction);
                MakeWall(holder, cell, direction, hasDoor ? corridorWidth : 0f);
            }
        }

        // Connect internal tile floors with fixed-size bridges. The L corner
        // remains within the footprint, and no tile grows outside the grid.
        foreach (Vector2Int cell in room.Cells)
        {
            Vector2Int right = cell + Vector2Int.right;
            Vector2Int up = cell + Vector2Int.up;
            float gap = grid - size;
            if (members.Contains(right))
                Cube(holder, "Bridge_X_" + cell,
                    new Vector3((cell.x + .5f) * grid, -thickness * .5f, cell.y * grid),
                    new Vector3(gap + .02f, thickness, size), floorColor);
            if (members.Contains(up))
                Cube(holder, "Bridge_Z_" + cell,
                    new Vector3(cell.x * grid, -thickness * .5f, (cell.y + .5f) * grid),
                    new Vector3(size, thickness, gap + .02f), floorColor);
            if (members.Contains(right) && members.Contains(up) &&
                members.Contains(cell + Vector2Int.one))
                Cube(holder, "BridgeCorner_" + cell,
                    new Vector3((cell.x + .5f) * grid, -thickness * .5f,
                        (cell.y + .5f) * grid),
                    new Vector3(gap + .02f, thickness, gap + .02f), floorColor);

            if (mode != MapMode.Dungeon) continue;
            // Perimeter wall sections on the two long sides of each tile bridge.
            if (members.Contains(right))
                for (int side = -1; side <= 1; side += 2)
                {
                    Vector2Int outward = side < 0 ? Vector2Int.down : Vector2Int.up;
                    if (members.Contains(cell + outward) &&
                        members.Contains(right + outward)) continue;
                    Cube(holder, "SeamWallX_" + cell + "_" + side,
                        new Vector3((cell.x + .5f) * grid, wallHeight * .5f,
                            cell.y * grid + side * size * .5f),
                        new Vector3(gap + wallThickness, wallHeight, wallThickness),
                        new Color(.24f, .24f, .26f));
                }
            if (members.Contains(up))
                for (int side = -1; side <= 1; side += 2)
                {
                    Vector2Int outward = side < 0 ? Vector2Int.left : Vector2Int.right;
                    if (members.Contains(cell + outward) &&
                        members.Contains(up + outward)) continue;
                    Cube(holder, "SeamWallZ_" + cell + "_" + side,
                        new Vector3(cell.x * grid + side * size * .5f,
                            wallHeight * .5f, (cell.y + .5f) * grid),
                        new Vector3(wallThickness, wallHeight, gap + wallThickness),
                        new Color(.24f, .24f, .26f));
                }
        }
    }

    private void MakeWall(Transform parent, Vector2Int cell,
        Vector2Int direction, float doorWidth)
    {
        bool vertical = direction.x != 0;
        float x = cell.x * spacing + direction.x * roomSize * .5f;
        float z = cell.y * spacing + direction.y * roomSize * .5f;
        int pieces = doorWidth > 0f ? 2 : 1;
        float len = pieces == 1 ? roomSize : (roomSize - doorWidth) * .5f;
        Color color = new Color(.24f, .24f, .26f);
        for (int i = 0; i < pieces; i++)
        {
            float offset = pieces == 1 ? 0f :
                (i == 0 ? -1f : 1f) * (doorWidth + len) * .5f;
            Vector3 position = vertical
                ? new Vector3(x, wallHeight * .5f, z + offset)
                : new Vector3(x + offset, wallHeight * .5f, z);
            Vector3 scale = vertical
                ? new Vector3(wallThickness, wallHeight, len)
                : new Vector3(len, wallHeight, wallThickness);
            Cube(parent, "Wall_" + cell + "_" + direction + "_" + i,
                position, scale, color);
        }
    }

    private void BuildCorridor(Transform parent, Layout layout, Edge edge)
    {
        GameObject go = new GameObject("Corridor_" + edge.A + "_" + edge.B);
        go.transform.SetParent(parent, false);
        float grid = mode == MapMode.Dungeon ? spacing : 5f;
        Vector3 midpoint = new Vector3((edge.A.x + edge.B.x) * grid * .5f, 0f,
            (edge.A.y + edge.B.y) * grid * .5f);
        bool alongX = edge.A.x != edge.B.x;
        Color color = new Color(.55f, .55f, .55f);
        if (layout.Critical.Contains(edge.A) && layout.Critical.Contains(edge.B))
            color = edge.A == layout.Blood || edge.B == layout.Blood
                ? Color.red :
                edge.A == layout.Start || edge.B == layout.Start
                    ? color : Color.black;

        float length = mode == MapMode.Dungeon ? grid - roomSize + .05f : grid;
        float width = mode == MapMode.Dungeon ? corridorWidth : 1.4f;
        Cube(go.transform, "PassageFloor",
            midpoint + Vector3.down * .30f,
            alongX ? new Vector3(length, .6f, width)
                   : new Vector3(width, .6f, length), color);
        if (mode != MapMode.Dungeon) return;

        for (int side = -1; side <= 1; side += 2)
        {
            Vector3 pos = alongX
                ? midpoint + new Vector3(0f, wallHeight * .5f,
                    side * (width + wallThickness) * .5f)
                : midpoint + new Vector3(
                    side * (width + wallThickness) * .5f, wallHeight * .5f, 0f);
            Cube(go.transform, "PassageWall_" + side, pos,
                alongX ? new Vector3(length, wallHeight, wallThickness)
                       : new Vector3(wallThickness, wallHeight, length),
                new Color(.24f, .24f, .26f));
        }
        if (ceiling)
            Cube(go.transform, "PassageCeiling",
                midpoint + Vector3.up * (wallHeight + wallThickness * .5f),
                alongX ? new Vector3(length, wallThickness, width)
                       : new Vector3(width, wallThickness, length),
                new Color(.24f, .24f, .26f));
    }

    private static void SetWorldSpawn(Transform spawn)
    {
        VRCSceneDescriptor descriptor =
            UnityEngine.Object.FindObjectOfType<VRCSceneDescriptor>();
        if (descriptor == null)
        { Debug.LogWarning("VRC Scene Descriptor not found. Assign DG_StartSpawn manually."); return; }
        SerializedObject so = new SerializedObject(descriptor);
        SerializedProperty spawns = so.FindProperty("spawns");
        if (spawns == null || !spawns.isArray)
        { Debug.LogWarning("Assign DG_StartSpawn in the VRC Scene Descriptor."); return; }
        Undo.RecordObject(descriptor, "Set Dungeon Spawn");
        spawns.arraySize = 1;
        spawns.GetArrayElementAtIndex(0).objectReferenceValue = spawn;
        so.ApplyModifiedProperties();
        EditorUtility.SetDirty(descriptor);
    }

    private static GameObject CreatePanel(Transform root, UdonBehaviour target,
        Vector3 forward, out Text status)
    {
        GameObject panel = new GameObject("DG_ControlPanel",
            typeof(RectTransform), typeof(Canvas),
            typeof(CanvasScaler), typeof(GraphicRaycaster));
        panel.transform.SetParent(root, false);
        panel.layer = 0;
        panel.transform.localPosition = -forward * 5f + Vector3.up * 2f;
        panel.transform.localRotation = Quaternion.LookRotation(forward, Vector3.up);
        panel.transform.localScale = Vector3.one * .006f;
        RectTransform rect = panel.GetComponent<RectTransform>();
        rect.sizeDelta = new Vector2(400f, 240f);
        panel.GetComponent<Canvas>().renderMode = RenderMode.WorldSpace;
        panel.AddComponent<VRCUiShape>();
        BoxCollider collider = panel.GetComponent<BoxCollider>();
        if (collider == null) collider = panel.AddComponent<BoxCollider>();
        collider.isTrigger = true;
        collider.size = new Vector3(400f, 240f, 1f);

        if (UnityEngine.Object.FindObjectOfType<EventSystem>() == null)
        {
            GameObject es = new GameObject("DG_EventSystem");
            es.transform.SetParent(root, false);
            es.AddComponent<EventSystem>();
            es.AddComponent<StandaloneInputModule>();
        }

        RectTransform background = UiRect(panel.transform, "Background",
            Vector2.zero, new Vector2(400f, 240f));
        Image bg = background.gameObject.AddComponent<Image>();
        bg.color = new Color(.05f, .07f, .10f, .95f);
        bg.raycastTarget = false;

        UiText(panel.transform, "Title", "DUNGEON GENERATOR",
            new Vector2(0f, 78f), new Vector2(380f, 44f), 25, Color.white);
        status = UiText(panel.transform, "Status", "Dungeon 1 / ?",
            new Vector2(0f, 17f), new Vector2(375f, 40f), 19, Color.cyan);
        RectTransform buttonRect = UiRect(panel.transform, "GenerateButton",
            new Vector2(0f, -65f), new Vector2(308f, 65f));
        Image image = buttonRect.gameObject.AddComponent<Image>();
        image.color = new Color(.13f, .44f, .68f);
        Button button = buttonRect.gameObject.AddComponent<Button>();
        button.targetGraphic = image;
        UiText(buttonRect, "Caption", "GENERATE NEXT", Vector2.zero,
            new Vector2(290f, 58f), 23, Color.white);
        UnityEventTools.AddStringPersistentListener(button.onClick,
            target.SendCustomEvent, "GenerateNext");
        return panel;
    }

    private static RectTransform UiRect(Transform parent, string name,
        Vector2 position, Vector2 dimensions)
    {
        GameObject obj = new GameObject(name, typeof(RectTransform));
        obj.layer = 0;
        obj.transform.SetParent(parent, false);
        RectTransform rect = obj.GetComponent<RectTransform>();
        rect.anchorMin = rect.anchorMax = new Vector2(.5f, .5f);
        rect.sizeDelta = dimensions;
        rect.anchoredPosition = position;
        rect.localScale = Vector3.one;
        return rect;
    }

    private static Text UiText(Transform parent, string name, string text,
        Vector2 pos, Vector2 size, int fontSize, Color color)
    {
        RectTransform rect = UiRect(parent, name, pos, size);
        Text label = rect.gameObject.AddComponent<Text>();
        label.text = text;
        label.font = Resources.GetBuiltinResource<Font>("Arial.ttf");
        label.fontSize = fontSize;
        label.fontStyle = FontStyle.Bold;
        label.alignment = TextAnchor.MiddleCenter;
        label.color = color;
        label.raycastTarget = false;
        return label;
    }
}
