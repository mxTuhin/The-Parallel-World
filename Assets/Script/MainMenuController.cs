using UnityEngine;
using UnityEngine.SceneManagement;

/// <summary>
/// Main menu for The Parallel World.
///
/// Shows two scene-load buttons:
///   • CPU Simulation   → FireSimCPU  (FireSimulationController — pure Burst)
///   • GPU Simulation   → FireSim     (FireSimulationControllerGPUCompute — fire on GPU)
///
/// Add this component to any GameObject in MainMenu.unity.
/// Both target scenes must be added to File ▶ Build Settings ▶ Scenes In Build.
///
/// Uses IMGUI so no Canvas or EventSystem setup is required.
/// </summary>
public class MainMenuController : MonoBehaviour
{
    [Header("Scene Names  (must match Build Settings exactly)")]
    [Tooltip("Scene name for the CPU-only simulation.")]
    public string cpuSceneName = "FireSimCPU";
    [Tooltip("Scene name for the CPU+GPU simulation.")]
    public string gpuSceneName = "FireSim";

    [Header("Layout")]
    [Tooltip("Width of the button panel in pixels.")]
    public float panelWidth  = 440f;
    public float panelHeight = 360f;

    // ── Styles ────────────────────────────────────────────────────────────────

    private GUIStyle _panelStyle;
    private GUIStyle _titleStyle;
    private GUIStyle _subtitleStyle;
    private GUIStyle _btnCpu;
    private GUIStyle _btnGpu;
    private GUIStyle _descStyle;
    private GUIStyle _footerStyle;
    private bool     _stylesBuilt;

    // ── OnGUI ─────────────────────────────────────────────────────────────────

    void OnGUI()
    {
        BuildStyles();

        // Centre the panel
        float x = (Screen.width  - panelWidth)  * 0.5f;
        float y = (Screen.height - panelHeight) * 0.5f;
        GUILayout.BeginArea(new Rect(x, y, panelWidth, panelHeight), _panelStyle);

        GUILayout.Space(18f);

        // ── Title ──────────────────────────────────────────────────────────
        GUILayout.Label("THE PARALLEL WORLD", _titleStyle);
        GUILayout.Label("Fire Simulation — Select Backend", _subtitleStyle);

        GUILayout.Space(24f);

        // ── CPU button ─────────────────────────────────────────────────────
        if (GUILayout.Button("  CPU Simulation", _btnCpu, GUILayout.Height(54f)))
            LoadScene(cpuSceneName);

        GUILayout.Label(
            "Fire spread: Unity Burst IJobParallelFor\n" +
            "Heatmap: CPU pixel loop\n" +
            "Benchmark marker: FireSim.Schedule+Complete  (full ms)",
            _descStyle);

        GUILayout.Space(14f);

        // ── GPU button ─────────────────────────────────────────────────────
        if (GUILayout.Button("  CPU + GPU Simulation", _btnGpu, GUILayout.Height(54f)))
            LoadScene(gpuSceneName);

        GUILayout.Label(
            "Fire spread: ComputeShader (FireSpreadKernel, GPU)\n" +
            "Heatmap: CSMain on GPU  |  Scan: BurningCellScanKernel\n" +
            "Benchmark marker: FireSim.Schedule+Complete  (~µs dispatch only)",
            _descStyle);

        GUILayout.Space(18f);

        GUILayout.Label(
            "Both scenes share the same benchmark harness (F5 / F6).\n" +
            "Run each scene separately and combine the CSVs.",
            _footerStyle);

        GUILayout.EndArea();
    }

    // ── Scene load ────────────────────────────────────────────────────────────

    void LoadScene(string sceneName)
    {
        if (string.IsNullOrEmpty(sceneName)) return;

        // GetBuildIndexByScenePath needs the full asset path.
        // Try the standard Scenes folder first, then fall back to name-only load.
        int buildIndex = SceneUtility.GetBuildIndexByScenePath(
            $"Assets/Scenes/{sceneName}.unity");

        if (buildIndex >= 0)
        {
            SceneManager.LoadScene(buildIndex);
            return;
        }

        // Fall back: let Unity resolve by scene name (works when the scene is in
        // Build Settings regardless of folder).  Unity logs its own error if not found.
        int total = SceneManager.sceneCountInBuildSettings;
        if (total == 0)
        {
            Debug.LogError(
                $"[MainMenu] No scenes registered in Build Settings.\n" +
                $"Go to File ▶ Build Settings and add MainMenu, FireSimCPU, FireSim.");
            return;
        }

        // SceneManager.LoadScene by name is the simplest cross-version API.
        SceneManager.LoadScene(sceneName);
    }

    // ── Style builder ─────────────────────────────────────────────────────────

    void BuildStyles()
    {
        if (_stylesBuilt) return;
        _stylesBuilt = true;

        // Dark semi-transparent panel
        var panelBg = MakeTex(new Color(0.06f, 0.06f, 0.08f, 0.95f));

        _panelStyle = new GUIStyle(GUI.skin.box)
        {
            padding = new RectOffset(24, 24, 0, 16),
            normal  = { background = panelBg, textColor = Color.white }
        };

        _titleStyle = new GUIStyle(GUI.skin.label)
        {
            fontSize  = 22,
            fontStyle = FontStyle.Bold,
            alignment = TextAnchor.MiddleCenter,
            normal    = { textColor = new Color(1f, 0.82f, 0.2f) }
        };

        _subtitleStyle = new GUIStyle(GUI.skin.label)
        {
            fontSize  = 12,
            alignment = TextAnchor.MiddleCenter,
            normal    = { textColor = new Color(0.75f, 0.75f, 0.75f) }
        };

        // CPU button — cool blue
        var cpuBg  = MakeTex(new Color(0.12f, 0.28f, 0.52f, 1f));
        var cpuHov = MakeTex(new Color(0.18f, 0.38f, 0.68f, 1f));

        _btnCpu = new GUIStyle(GUI.skin.button)
        {
            fontSize  = 16,
            fontStyle = FontStyle.Bold,
            alignment = TextAnchor.MiddleLeft,
            padding   = new RectOffset(16, 0, 0, 0),
            normal    = { background = cpuBg,  textColor = Color.white },
            hover     = { background = cpuHov, textColor = Color.white },
            active    = { background = cpuHov, textColor = Color.white }
        };

        // GPU button — warm orange
        var gpuBg  = MakeTex(new Color(0.52f, 0.22f, 0.06f, 1f));
        var gpuHov = MakeTex(new Color(0.70f, 0.32f, 0.08f, 1f));

        _btnGpu = new GUIStyle(GUI.skin.button)
        {
            fontSize  = 16,
            fontStyle = FontStyle.Bold,
            alignment = TextAnchor.MiddleLeft,
            padding   = new RectOffset(16, 0, 0, 0),
            normal    = { background = gpuBg,  textColor = Color.white },
            hover     = { background = gpuHov, textColor = Color.white },
            active    = { background = gpuHov, textColor = Color.white }
        };

        _descStyle = new GUIStyle(GUI.skin.label)
        {
            fontSize = 10,
            wordWrap = true,
            normal   = { textColor = new Color(0.65f, 0.65f, 0.65f) },
            padding  = new RectOffset(4, 0, 2, 0)
        };

        _footerStyle = new GUIStyle(GUI.skin.label)
        {
            fontSize  = 10,
            wordWrap  = true,
            alignment = TextAnchor.MiddleCenter,
            normal    = { textColor = new Color(0.5f, 0.5f, 0.5f) }
        };
    }

    static Texture2D MakeTex(Color c)
    {
        var t = new Texture2D(1, 1);
        t.SetPixel(0, 0, c);
        t.Apply();
        return t;
    }
}
