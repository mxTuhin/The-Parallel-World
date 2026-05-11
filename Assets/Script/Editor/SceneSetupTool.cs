using System.IO;
using StarterAssets;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.SceneManagement;

/// <summary>
/// Menu: Tools ▶ Parallel World
///
/// Two commands:
///
///   Setup Top-Down Shooter Scene
///     Automates the full scene-setup checklist for the gameplay systems:
///     player, camera, bullet prefab, enemy prefab, BulletPool, EnemyManager.
///
///   Wire Fire System
///     Wires whichever fire simulation controller is in the scene (CPU or CPU+GPU)
///     into FireZoneController, FireParticleVisualizer, PlayerFireInteraction, and
///     optionally SimulationBenchmark.
///
/// Both tools are safe to run multiple times — existing components/prefabs are reused.
/// After running, press Ctrl+S to save.
/// </summary>
public static class SceneSetupTool
{
    private const string PrefabFolder = "Assets/Prefab";

    // ─────────────────────────────────────────────────────────────────────────
    // Wire Fire System
    // ─────────────────────────────────────────────────────────────────────────

    [MenuItem("Tools/Parallel World/Wire Fire System", false, 2)]
    public static void WireFireSystem()
    {
        // 1. Find whichever simulation controller is in the scene.
        //    GPU controller is preferred; CPU controller is the fallback.
        MonoBehaviour fireSim = Object.FindAnyObjectByType<FireSimulationControllerGPUCompute>()
                             as MonoBehaviour;
        if (fireSim == null)
            fireSim = Object.FindAnyObjectByType<FireSimulationController>();

        if (fireSim == null)
        {
            Debug.LogError("[FireSetup] ✖ No fire simulation controller found in the scene. " +
                           "Add FireSimulationController (CPU) or " +
                           "FireSimulationControllerGPUCompute (CPU+GPU) first, then run this tool.");
            return;
        }

        bool isGPU = fireSim is FireSimulationControllerGPUCompute;
        Debug.Log($"[FireSetup] Using {(isGPU ? "CPU+GPU" : "CPU")} controller: '{fireSim.name}'.");

        // 2. Derive grid plane transform from the sim's targetRenderer field
        Transform gridPlane = null;
        {
            var so   = new SerializedObject(fireSim);
            var prop = so.FindProperty("targetRenderer");
            if (prop?.objectReferenceValue is Renderer rend)
                gridPlane = rend.transform;
            else
                Debug.LogWarning("[FireSetup] ⚠ targetRenderer not assigned on the fire sim. " +
                                 "FireZoneController.gridPlane will need manual assignment.");
        }

        // 3. Create / reuse a FireSystemRoot container
        GameObject root = GameObject.Find("FireSystemRoot") ?? new GameObject("FireSystemRoot");

        // 4. FireZoneController  (field: fireSimMono — MonoBehaviour, any IFireSimulation)
        var fzc = AddIfMissing<FireZoneController>(root);
        SetSerializedField(fzc, "fireSimMono", fireSim);
        if (gridPlane != null)
            SetSerializedField(fzc, "gridPlane", gridPlane);
        Debug.Log("[FireSetup] ✔ FireZoneController configured.");

        // 5. FireParticleVisualizer  (field: fireSimMono)
        var fpv = AddIfMissing<FireParticleVisualizer>(root);
        SetSerializedField(fpv, "fireSimMono", fireSim);
        SetSerializedField(fpv, "fireZone",    fzc);

        var particleMat = AssetDatabase.LoadAssetAtPath<Material>("Assets/Materials/ParticleMaterial.mat");
        if (particleMat != null)
            SetSerializedField(fpv, "particleMaterial", particleMat);
        else
            Debug.LogWarning("[FireSetup] ⚠ Assets/Materials/ParticleMaterial.mat not found — " +
                             "assign it manually on FireParticleVisualizer.");
        Debug.Log("[FireSetup] ✔ FireParticleVisualizer configured.");

        // 6. Player — PlayerHealth + PlayerFireInteraction
        var cc = Object.FindAnyObjectByType<CharacterController>();
        if (cc != null)
        {
            var playerGo    = cc.gameObject;
            var health      = AddIfMissing<PlayerHealth>(playerGo);
            var interaction = AddIfMissing<PlayerFireInteraction>(playerGo);
            SetSerializedField(interaction, "fireZone",     fzc);
            SetSerializedField(interaction, "playerHealth", health);
            Debug.Log($"[FireSetup] ✔ PlayerHealth + PlayerFireInteraction added to '{playerGo.name}'.");
        }
        else
        {
            Debug.LogWarning("[FireSetup] ⚠ No CharacterController found — " +
                             "PlayerHealth / PlayerFireInteraction not wired.  " +
                             "Add them to the Player manually.");
        }

        // 7. SimulationBenchmark — wire if present in the scene
        var benchmark = Object.FindAnyObjectByType<SimulationBenchmark>();
        if (benchmark != null)
        {
            SetSerializedField(benchmark, "fireSimMono",  fireSim);
            var em = Object.FindAnyObjectByType<EnemyManager>();
            if (em != null)
                SetSerializedField(benchmark, "enemyManager", em);
            Debug.Log("[FireSetup] ✔ SimulationBenchmark wired.");
        }

        EditorSceneManager.MarkSceneDirty(SceneManager.GetActiveScene());
        Debug.Log("[FireSetup] ✔ Fire system wiring complete! Press Ctrl+S to save.");
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Setup Top-Down Shooter Scene
    // ─────────────────────────────────────────────────────────────────────────

    [MenuItem("Tools/Parallel World/Setup Top-Down Shooter Scene", false, 1)]
    public static void SetupScene()
    {
        bool ok = true;

        var player = SetupPlayer();
        if (player == null)
        {
            Debug.LogError("[Setup] ✖ Could not find a GameObject with CharacterController. " +
                           "Make sure your PlayerCharacter is in the scene before running this tool.");
            ok = false;
        }

        if (ok)
        {
            SetupCamera(player.transform);
            var bulletComp = CreateOrGetBulletPrefab();
            var enemyGo    = CreateOrGetEnemyPrefab();
            PlaceBulletPool(bulletComp);
            PlaceEnemyManager(enemyGo, player.transform);
        }

        EditorSceneManager.MarkSceneDirty(SceneManager.GetActiveScene());
        if (ok)
            Debug.Log("[Setup] ✔ Scene setup complete! Press Ctrl+S to save.");
    }

    private const string StarterAssetsInputActionsPath =
        "Assets/StarterAssets/InputSystem/StarterAssets.inputactions";

    // ── Step 1: Player ────────────────────────────────────────────────────────

    private static GameObject SetupPlayer()
    {
        var cc = Object.FindAnyObjectByType<CharacterController>();
        if (cc == null) return null;

        var go = cc.gameObject;

        AddIfMissing<StarterAssetsInputs>(go);
        SetupPlayerInput(go);
        AddIfMissing<PlayerMovementController>(go);
        AddIfMissing<PlayerShooterController>(go);

        Transform firePoint = go.transform.Find("FirePoint");
        if (firePoint == null)
        {
            var fp = new GameObject("FirePoint");
            fp.transform.SetParent(go.transform, false);
            fp.transform.localPosition = new Vector3(0f, 0.5f, 0.6f);
            firePoint = fp.transform;
        }

        SetSerializedField(go.GetComponent<PlayerShooterController>(), "firePoint", firePoint);
        Debug.Log($"[Setup] ✔ Player configured on '{go.name}'.");
        return go;
    }

    private static void SetupPlayerInput(GameObject go)
    {
        var asset = AssetDatabase.LoadAssetAtPath<InputActionAsset>(StarterAssetsInputActionsPath);
        if (asset == null)
        {
            Debug.LogWarning($"[Setup] ⚠ InputActions not found at '{StarterAssetsInputActionsPath}'.");
            return;
        }

        var pi = AddIfMissing<PlayerInput>(go);
        var so = new SerializedObject(pi);
        so.FindProperty("m_Actions").objectReferenceValue = asset;
        so.FindProperty("m_NotificationBehavior").enumValueIndex = 0; // SendMessages
        so.ApplyModifiedPropertiesWithoutUndo();
        Debug.Log("[Setup] ✔ PlayerInput wired.");
    }

    // ── Step 2: Camera ────────────────────────────────────────────────────────

    private static void SetupCamera(Transform playerTransform)
    {
        Camera cam = Camera.main;
        if (cam == null) { Debug.LogWarning("[Setup] ⚠ No Main Camera found."); return; }

        var ctrl = AddIfMissing<CameraController>(cam.gameObject);
        SetSerializedField(ctrl, "target", playerTransform);
        Debug.Log($"[Setup] ✔ CameraController added to '{cam.name}'.");
    }

    // ── Step 3: Bullet prefab ─────────────────────────────────────────────────

    private static BulletController CreateOrGetBulletPrefab()
    {
        EnsureFolder(PrefabFolder);
        const string path = PrefabFolder + "/BulletPrefab.prefab";

        var existing = AssetDatabase.LoadAssetAtPath<GameObject>(path);
        if (existing != null && existing.GetComponent<BulletController>() != null)
        {
            Debug.Log("[Setup] ✔ BulletPrefab already exists, reusing.");
            return existing.GetComponent<BulletController>();
        }

        var go = GameObject.CreatePrimitive(PrimitiveType.Sphere);
        go.name = "BulletPrefab";
        go.transform.localScale = Vector3.one * 0.15f;

        Object.DestroyImmediate(go.GetComponent<SphereCollider>());
        var col = go.AddComponent<SphereCollider>();
        col.isTrigger = true;

        var rb = go.AddComponent<Rigidbody>();
        rb.useGravity             = false;
        rb.linearDamping          = 0f;
        rb.angularDamping         = 0f;
        rb.interpolation          = RigidbodyInterpolation.Interpolate;
        rb.collisionDetectionMode = CollisionDetectionMode.ContinuousDynamic;
        rb.constraints            = RigidbodyConstraints.FreezePositionY | RigidbodyConstraints.FreezeRotation;

        go.AddComponent<BulletController>();
        var asset = PrefabUtility.SaveAsPrefabAsset(go, path);
        Object.DestroyImmediate(go);

        Debug.Log($"[Setup] ✔ BulletPrefab created at {path}");
        return asset.GetComponent<BulletController>();
    }

    // ── Step 4: Enemy prefab ──────────────────────────────────────────────────

    private static GameObject CreateOrGetEnemyPrefab()
    {
        EnsureFolder(PrefabFolder);
        const string path = PrefabFolder + "/EnemyPrefab.prefab";

        var existing = AssetDatabase.LoadAssetAtPath<GameObject>(path);
        if (existing != null && existing.GetComponent<EnemyController>() != null)
        {
            Debug.Log("[Setup] ✔ EnemyPrefab already exists, reusing.");
            return existing;
        }

        var go = GameObject.CreatePrimitive(PrimitiveType.Capsule);
        go.name = "EnemyPrefab";

        const string matPath = PrefabFolder + "/EnemyMaterial.mat";
        Material mat = AssetDatabase.LoadAssetAtPath<Material>(matPath);
        if (mat == null)
        {
            Shader s = Shader.Find("Universal Render Pipeline/Lit") ?? Shader.Find("Diffuse");
            mat = new Material(s) { color = new Color(0.85f, 0.15f, 0.15f) };
            AssetDatabase.CreateAsset(mat, matPath);
        }
        go.GetComponent<MeshRenderer>().sharedMaterial = mat;

        var rb = go.AddComponent<Rigidbody>();
        rb.isKinematic = true;
        rb.useGravity  = false;

        go.AddComponent<EnemyController>();
        go.AddComponent<EnemyHealth>();

        var asset = PrefabUtility.SaveAsPrefabAsset(go, path);
        Object.DestroyImmediate(go);

        Debug.Log($"[Setup] ✔ EnemyPrefab created at {path}");
        return asset;
    }

    // ── Step 5: BulletPool ────────────────────────────────────────────────────

    private static void PlaceBulletPool(BulletController bulletPrefab)
    {
        var existing = Object.FindAnyObjectByType<BulletPool>();
        var pool     = existing ?? new GameObject("BulletPool").AddComponent<BulletPool>();
        SetSerializedField(pool, "bulletPrefab", bulletPrefab);
        if (existing == null) Debug.Log("[Setup] ✔ BulletPool created.");
    }

    // ── Step 6: EnemyManager ─────────────────────────────────────────────────

    private static void PlaceEnemyManager(GameObject enemyPrefab, Transform playerTransform)
    {
        var existing = Object.FindAnyObjectByType<EnemyManager>();
        var mgr      = existing ?? new GameObject("EnemyManager").AddComponent<EnemyManager>();
        SetSerializedField(mgr, "enemyPrefab",     enemyPrefab);
        SetSerializedField(mgr, "playerTransform", playerTransform);
        if (existing == null) Debug.Log("[Setup] ✔ EnemyManager created.");
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static T AddIfMissing<T>(GameObject go) where T : Component
        => go.GetComponent<T>() ?? go.AddComponent<T>();

    private static void SetSerializedField(Object target, string fieldName, Object value)
    {
        var so   = new SerializedObject(target);
        var prop = so.FindProperty(fieldName);
        if (prop == null)
        {
            Debug.LogWarning($"[Setup] ⚠ Property '{fieldName}' not found on " +
                             $"{target.GetType().Name}.  Verify the field name.");
            return;
        }
        prop.objectReferenceValue = value;
        so.ApplyModifiedPropertiesWithoutUndo();
    }

    private static void EnsureFolder(string path)
    {
        if (AssetDatabase.IsValidFolder(path)) return;
        string parent = Path.GetDirectoryName(path)!.Replace('\\', '/');
        string folder = Path.GetFileName(path);
        AssetDatabase.CreateFolder(parent, folder);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Create CPU Simulation Scene
    //
    // Duplicates FireSim.unity → FireSimCPU.unity, then:
    //   • Removes FireSimulationControllerGPUCompute
    //   • Adds    FireSimulationController (CPU) with identical grid settings
    //   • Rewires fireSimMono on FireZoneController, FireParticleVisualizer,
    //     SimulationBenchmark to point at the new CPU controller
    //   • Saves FireSimCPU.unity
    // ─────────────────────────────────────────────────────────────────────────

    private const string GpuScenePath = "Assets/Scenes/FireSim.unity";
    private const string CpuScenePath = "Assets/Scenes/FireSimCPU.unity";

    [MenuItem("Tools/Parallel World/Create CPU Simulation Scene", false, 10)]
    public static void CreateCpuScene()
    {
        // 1. Verify source scene exists
        if (!File.Exists(Path.GetFullPath(GpuScenePath).Replace('/', '\\')))
        {
            Debug.LogError($"[FireSetup] ✖ Source scene not found at {GpuScenePath}");
            return;
        }

        // 2. Copy GPU scene → CPU scene (overwrites silently — safe to re-run)
        if (!AssetDatabase.CopyAsset(GpuScenePath, CpuScenePath))
        {
            // CopyAsset returns false if destination already exists — delete and retry
            AssetDatabase.DeleteAsset(CpuScenePath);
            if (!AssetDatabase.CopyAsset(GpuScenePath, CpuScenePath))
            {
                Debug.LogError("[FireSetup] ✖ Failed to copy FireSim.unity → FireSimCPU.unity.");
                return;
            }
        }
        AssetDatabase.Refresh();
        Debug.Log($"[FireSetup] Copied {GpuScenePath} → {CpuScenePath}");

        // 3. Ask editor to save current work before switching scenes
        if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo())
        {
            Debug.LogWarning("[FireSetup] Scene save cancelled by user. Aborting CPU scene creation.");
            return;
        }

        // 4. Open the new CPU scene
        var cpuScene = EditorSceneManager.OpenScene(CpuScenePath, OpenSceneMode.Single);

        // 5. Find the GPU controller — it was copied verbatim from FireSim.unity
        var gpuCtrl = Object.FindAnyObjectByType<FireSimulationControllerGPUCompute>();
        if (gpuCtrl == null)
        {
            Debug.LogError("[FireSetup] ✖ FireSimulationControllerGPUCompute not found in the copied scene.");
            return;
        }

        GameObject simGo = gpuCtrl.gameObject;

        // 6. Read every setting we need to carry over to the CPU controller
        int           w                 = gpuCtrl.width;
        int           h                 = gpuCtrl.height;
        Renderer      targetRenderer    = gpuCtrl.targetRenderer;
        FilterMode    filterMode        = gpuCtrl.filterMode;
        bool          showHeatmap       = gpuCtrl.showHeatmapVisuals;
        var           matDefs           = gpuCtrl.materialDefinitions;
        var           zones             = gpuCtrl.zones;
        int           coreCount         = gpuCtrl.coreCount;
        float         diffusionRate     = gpuCtrl.diffusionRate;
        float         burnRate          = gpuCtrl.burnRate;
        float         heatMax           = gpuCtrl.heatMax;
        var           startFireCells    = gpuCtrl.startFireCells;

        // 7. Destroy GPU controller BEFORE adding CPU controller
        //    (both implement IFireSimulation — Unity allows only one of each type)
        Object.DestroyImmediate(gpuCtrl);

        // 8. Add FireSimulationController (CPU) to the same GameObject
        var cpuCtrl = simGo.AddComponent<FireSimulationController>();

        // Use SerializedObject so Unity's undo + dirty system is respected
        var so = new SerializedObject(cpuCtrl);
        so.FindProperty("width")              .intValue                = w;
        so.FindProperty("height")             .intValue                = h;
        so.FindProperty("targetRenderer")     .objectReferenceValue    = targetRenderer;
        so.FindProperty("filterMode")         .enumValueIndex          = (int)filterMode;
        so.FindProperty("heatMax")            .floatValue              = heatMax;
        so.FindProperty("coreCount")          .intValue                = coreCount;
        so.FindProperty("diffusionRate")      .floatValue              = diffusionRate;
        so.FindProperty("burnRate")           .floatValue              = burnRate;

        // materialDefinitions array
        var matsProp = so.FindProperty("materialDefinitions");
        matsProp.arraySize = matDefs?.Length ?? 0;
        for (int i = 0; i < (matDefs?.Length ?? 0); i++)
            matsProp.GetArrayElementAtIndex(i).objectReferenceValue = matDefs[i];

        // zones array
        var zonesProp = so.FindProperty("zones");
        zonesProp.arraySize = zones?.Length ?? 0;
        for (int i = 0; i < (zones?.Length ?? 0); i++)
        {
            var elem = zonesProp.GetArrayElementAtIndex(i);
            var src  = zones[i];
            elem.FindPropertyRelative("normalizedRect").rectValue    = src.normalizedRect;
            elem.FindPropertyRelative("materialIndex") .intValue     = src.materialIndex;
        }

        // startFireCells array
        var sfcProp = so.FindProperty("startFireCells");
        sfcProp.arraySize = startFireCells?.Length ?? 0;
        for (int i = 0; i < (startFireCells?.Length ?? 0); i++)
            sfcProp.GetArrayElementAtIndex(i).vector2IntValue = startFireCells[i];

        so.ApplyModifiedPropertiesWithoutUndo();
        Debug.Log($"[FireSetup] ✔ FireSimulationController (CPU) added to '{simGo.name}' " +
                  $"({w}×{h}, {matDefs?.Length ?? 0} materials, {zones?.Length ?? 0} zones).");

        // 9. Rewire fireSimMono on all consumer scripts
        var fzc  = Object.FindAnyObjectByType<FireZoneController>();
        var fpv  = Object.FindAnyObjectByType<FireParticleVisualizer>();
        var bench = Object.FindAnyObjectByType<SimulationBenchmark>();

        if (fzc != null)
        {
            SetSerializedField(fzc, "fireSimMono", cpuCtrl);
            Debug.Log("[FireSetup] ✔ FireZoneController.fireSimMono → CPU controller.");
        }
        if (fpv != null)
        {
            SetSerializedField(fpv, "fireSimMono", cpuCtrl);
            Debug.Log("[FireSetup] ✔ FireParticleVisualizer.fireSimMono → CPU controller.");
        }
        if (bench != null)
        {
            SetSerializedField(bench, "fireSimMono", cpuCtrl);
            Debug.Log("[FireSetup] ✔ SimulationBenchmark.fireSimMono → CPU controller.");
        }

        // 10. Save the new CPU scene
        EditorSceneManager.SaveScene(cpuScene, CpuScenePath);
        AssetDatabase.Refresh();

        Debug.Log("[FireSetup] ✔ FireSimCPU.unity created and saved. " +
                  "Add it to File ▶ Build Settings ▶ Scenes In Build.");
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Create Main Menu Scene
    //
    // Creates Assets/Scenes/MainMenu.unity with:
    //   • Main Camera
    //   • Directional Light
    //   • GameObject "MainMenu" carrying MainMenuController
    // ─────────────────────────────────────────────────────────────────────────

    private const string MainMenuScenePath = "Assets/Scenes/MainMenu.unity";

    [MenuItem("Tools/Parallel World/Create Main Menu Scene", false, 11)]
    public static void CreateMainMenuScene()
    {
        // Ask editor to save current work before switching
        if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo())
        {
            Debug.LogWarning("[FireSetup] Scene save cancelled. Aborting.");
            return;
        }

        // Create a new empty scene
        var menuScene = EditorSceneManager.NewScene(
            NewSceneSetup.DefaultGameObjects,   // adds Camera + Directional Light
            NewSceneMode.Single);

        // Add the MainMenuController
        var menuGo = new GameObject("MainMenu");
        menuGo.AddComponent<MainMenuController>();

        // Move it to the scene root
        SceneManager.MoveGameObjectToScene(menuGo, menuScene);

        // Camera: orthographic, top-down friendly background colour
        var cam = Object.FindAnyObjectByType<Camera>();
        if (cam != null)
        {
            cam.clearFlags       = CameraClearFlags.SolidColor;
            cam.backgroundColor  = new Color(0.04f, 0.04f, 0.06f, 1f);
            cam.orthographic     = false;
        }

        // Save
        EnsureFolder("Assets/Scenes");
        EditorSceneManager.SaveScene(menuScene, MainMenuScenePath);
        AssetDatabase.Refresh();

        Debug.Log("[FireSetup] ✔ MainMenu.unity created at Assets/Scenes/MainMenu.unity.\n" +
                  "Next steps:\n" +
                  "  1. File ▶ Build Settings → add MainMenu, FireSimCPU, FireSim (in that order).\n" +
                  "  2. Set MainMenu as index 0 (it loads first).\n" +
                  "  3. Optionally add a Back button in each sim scene (SceneManager.LoadScene(0)).");
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Add Back-to-Menu button helper
    // Adds a tiny "← Menu" IMGUI button to the current scene via BackToMenuButton.
    // ─────────────────────────────────────────────────────────────────────────

    [MenuItem("Tools/Parallel World/Add Back-To-Menu Button (current scene)", false, 12)]
    public static void AddBackToMenuButton()
    {
        var existing = Object.FindAnyObjectByType<BackToMenuButton>();
        if (existing != null)
        {
            Debug.Log("[FireSetup] BackToMenuButton already exists in this scene.");
            return;
        }

        var go = new GameObject("BackToMenuButton");
        go.AddComponent<BackToMenuButton>();
        EditorSceneManager.MarkSceneDirty(SceneManager.GetActiveScene());
        Debug.Log("[FireSetup] ✔ BackToMenuButton added. Press Ctrl+S to save.");
    }
}
