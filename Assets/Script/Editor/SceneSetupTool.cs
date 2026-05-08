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
}
