using System.IO;
using StarterAssets;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.SceneManagement;

/// <summary>
/// Menu: Tools ▶ Parallel World ▶ Setup Top-Down Shooter Scene
///
/// Automates the entire scene-setup checklist:
///  1. Adds PlayerMovementController + PlayerShooterController to the CharacterController object
///  2. Creates a FirePoint child transform on the player
///  3. Adds CameraController to Main Camera and wires the player target
///  4. Creates BulletPrefab asset (sphere, trigger collider, non-kinematic Rigidbody)
///  5. Creates EnemyPrefab asset  (capsule, kinematic Rigidbody, red URP material)
///  6. Places BulletPool and EnemyManager GameObjects in the scene
///  7. Wires all serialized references automatically
///  8. Marks the scene dirty so Ctrl+S saves everything
///
/// Safe to run multiple times — existing components and prefabs are reused.
/// </summary>
public static class SceneSetupTool
{
    private const string PrefabFolder = "Assets/Prefab";

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

            var bulletPrefabComp = CreateOrGetBulletPrefab();
            var enemyPrefabGo    = CreateOrGetEnemyPrefab();

            PlaceBulletPool(bulletPrefabComp);
            PlaceEnemyManager(enemyPrefabGo, player.transform);
        }

        EditorSceneManager.MarkSceneDirty(SceneManager.GetActiveScene());

        if (ok)
            Debug.Log("[Setup] ✔ Scene setup complete! Press Ctrl+S to save.");
    }

    private const string StarterAssetsInputActionsPath =
        "Assets/StarterAssets/InputSystem/StarterAssets.inputactions";

    // ─── Step 1 : Player ─────────────────────────────────────────────────────

    private static GameObject SetupPlayer()
    {
        var cc = Object.FindAnyObjectByType<CharacterController>();
        if (cc == null) return null;

        GameObject go = cc.gameObject;

        // Input System components — must exist before PlayerMovementController reads from them
        AddIfMissing<StarterAssetsInputs>(go);
        SetupPlayerInput(go);

        AddIfMissing<PlayerMovementController>(go);
        AddIfMissing<PlayerShooterController>(go);

        // Create FirePoint child if absent
        Transform firePoint = go.transform.Find("FirePoint");
        if (firePoint == null)
        {
            var fp = new GameObject("FirePoint");
            fp.transform.SetParent(go.transform, false);
            fp.transform.localPosition = new Vector3(0f, 0.5f, 0.6f);
            firePoint = fp.transform;
        }

        // Wire FirePoint into PlayerShooterController
        SetSerializedField(go.GetComponent<PlayerShooterController>(), "firePoint", firePoint);

        Debug.Log($"[Setup] ✔ Player configured on '{go.name}'.");
        return go;
    }

    private static void SetupPlayerInput(GameObject go)
    {
        var inputActionsAsset = AssetDatabase.LoadAssetAtPath<InputActionAsset>(StarterAssetsInputActionsPath);
        if (inputActionsAsset == null)
        {
            Debug.LogWarning($"[Setup] ⚠ Could not find InputActions at '{StarterAssetsInputActionsPath}'. " +
                             "PlayerInput will not be wired automatically.");
            return;
        }

        var playerInput = AddIfMissing<PlayerInput>(go);

        // Wire actions asset and set notification mode via SerializedObject so it sticks in the editor
        var so = new SerializedObject(playerInput);
        so.FindProperty("m_Actions").objectReferenceValue = inputActionsAsset;
        // SendMessages = 0, Broadcast = 1, InvokeUnityEvents = 2, InvokeCSharpEvents = 3
        so.FindProperty("m_NotificationBehavior").enumValueIndex = 0;
        so.ApplyModifiedPropertiesWithoutUndo();

        Debug.Log("[Setup] ✔ PlayerInput wired with StarterAssets.inputactions (Send Messages).");
    }

    // ─── Step 2 : Camera ─────────────────────────────────────────────────────

    private static void SetupCamera(Transform playerTransform)
    {
        Camera cam = Camera.main;
        if (cam == null)
        {
            Debug.LogWarning("[Setup] ⚠ No Main Camera found (tag must be 'MainCamera'). Skipping camera setup.");
            return;
        }

        var ctrl = AddIfMissing<CameraController>(cam.gameObject);
        SetSerializedField(ctrl, "target", playerTransform);

        Debug.Log($"[Setup] ✔ CameraController added to '{cam.name}'. Offset will be captured from its current editor position.");
    }

    // ─── Step 3 : Bullet Prefab ───────────────────────────────────────────────

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

        // Sphere primitive
        var go = GameObject.CreatePrimitive(PrimitiveType.Sphere);
        go.name = "BulletPrefab";
        go.transform.localScale = Vector3.one * 0.15f;

        // Replace default SphereCollider with trigger variant
        Object.DestroyImmediate(go.GetComponent<SphereCollider>());
        var col = go.AddComponent<SphereCollider>();
        col.isTrigger = true;

        // Non-kinematic Rigidbody — velocity-driven, Y locked so bullets fly flat
        var rb = go.AddComponent<Rigidbody>();
        rb.useGravity              = false;
        rb.linearDamping           = 0f;
        rb.angularDamping          = 0f;
        rb.interpolation           = RigidbodyInterpolation.Interpolate;
        rb.collisionDetectionMode  = CollisionDetectionMode.ContinuousDynamic;
        rb.constraints             = RigidbodyConstraints.FreezePositionY
                                   | RigidbodyConstraints.FreezeRotation;

        go.AddComponent<BulletController>();

        var prefabAsset = PrefabUtility.SaveAsPrefabAsset(go, path);
        Object.DestroyImmediate(go);

        Debug.Log($"[Setup] ✔ BulletPrefab created at {path}");
        return prefabAsset.GetComponent<BulletController>();
    }

    // ─── Step 4 : Enemy Prefab ────────────────────────────────────────────────

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

        // Capsule primitive (already has a CapsuleCollider — keep it, non-trigger)
        var go = GameObject.CreatePrimitive(PrimitiveType.Capsule);
        go.name = "EnemyPrefab";

        // Red URP material
        const string matPath = PrefabFolder + "/EnemyMaterial.mat";
        Material mat = AssetDatabase.LoadAssetAtPath<Material>(matPath);
        if (mat == null)
        {
            Shader urpLit = Shader.Find("Universal Render Pipeline/Lit");
            mat = new Material(urpLit != null ? urpLit : Shader.Find("Diffuse"))
            {
                color = new Color(0.85f, 0.15f, 0.15f)
            };
            AssetDatabase.CreateAsset(mat, matPath);
        }
        go.GetComponent<MeshRenderer>().sharedMaterial = mat;

        // Kinematic Rigidbody — Burst job drives position, physics still fires triggers
        var rb = go.AddComponent<Rigidbody>();
        rb.isKinematic = true;
        rb.useGravity  = false;

        go.AddComponent<EnemyController>();
        go.AddComponent<EnemyHealth>();

        var prefabAsset = PrefabUtility.SaveAsPrefabAsset(go, path);
        Object.DestroyImmediate(go);

        Debug.Log($"[Setup] ✔ EnemyPrefab created at {path}");
        return prefabAsset;
    }

    // ─── Step 5 : BulletPool scene object ─────────────────────────────────────

    private static void PlaceBulletPool(BulletController bulletPrefabComp)
    {
        var existing = Object.FindAnyObjectByType<BulletPool>();
        BulletPool pool;
        if (existing != null)
        {
            pool = existing;
            Debug.Log("[Setup] ✔ BulletPool already in scene, updating references.");
        }
        else
        {
            pool = new GameObject("BulletPool").AddComponent<BulletPool>();
        }

        SetSerializedField(pool, "bulletPrefab", bulletPrefabComp);
    }

    // ─── Step 6 : EnemyManager scene object ───────────────────────────────────

    private static void PlaceEnemyManager(GameObject enemyPrefabGo, Transform playerTransform)
    {
        var existing = Object.FindAnyObjectByType<EnemyManager>();
        EnemyManager mgr;
        if (existing != null)
        {
            mgr = existing;
            Debug.Log("[Setup] ✔ EnemyManager already in scene, updating references.");
        }
        else
        {
            mgr = new GameObject("EnemyManager").AddComponent<EnemyManager>();
        }

        SetSerializedField(mgr, "enemyPrefab",      enemyPrefabGo);
        SetSerializedField(mgr, "playerTransform",  playerTransform);
    }

    // ─── Helpers ──────────────────────────────────────────────────────────────

    private static T AddIfMissing<T>(GameObject go) where T : Component
    {
        var c = go.GetComponent<T>();
        return c != null ? c : go.AddComponent<T>();
    }

    private static void SetSerializedField(Object target, string fieldName, Object value)
    {
        var so = new SerializedObject(target);
        SerializedProperty prop = so.FindProperty(fieldName);
        if (prop == null)
        {
            Debug.LogWarning($"[Setup] ⚠ Property '{fieldName}' not found on {target.GetType().Name}. " +
                             "Verify the field name matches the script.");
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
