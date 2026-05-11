using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.SceneManagement;

/// <summary>
/// Draws a small "← Menu" button in the top-left corner of the screen.
/// Add this to any simulation scene so the player can return to MainMenu.
///
/// Uses Tools ▶ Parallel World ▶ Add Back-To-Menu Button to add automatically,
/// or just drop this component on any GameObject in the scene.
///
/// Uses the new Input System (UnityEngine.InputSystem.Keyboard) — compatible
/// with projects set to "Input System Package (New)" in Player Settings.
/// </summary>
public class BackToMenuButton : MonoBehaviour
{
    [Tooltip("Build index of the main menu scene.  0 = first scene in Build Settings.")]
    public int menuSceneIndex = 0;

    [Tooltip("Keyboard shortcut to return to menu (in addition to the GUI button).")]
    public Key menuKey = Key.Escape;

    private GUIStyle _btnStyle;
    private bool     _styleBuilt;

    void Update()
    {
        // Keyboard.current is null if no keyboard device is present (e.g. mobile)
        if (Keyboard.current != null && Keyboard.current[menuKey].wasPressedThisFrame)
            GoToMenu();
    }

    void OnGUI()
    {
        BuildStyle();

        // Small button anchored to top-left
        if (GUI.Button(new Rect(12, 12, 110, 28), "← Menu", _btnStyle))
            GoToMenu();
    }

    void GoToMenu()
    {
        if (menuSceneIndex >= 0 && menuSceneIndex < SceneManager.sceneCountInBuildSettings)
            SceneManager.LoadScene(menuSceneIndex);
        else
            Debug.LogWarning($"[BackToMenu] Scene index {menuSceneIndex} is not in Build Settings.");
    }

    void BuildStyle()
    {
        if (_styleBuilt) return;
        _styleBuilt = true;

        var bg  = MakeTex(new Color(0.10f, 0.10f, 0.12f, 0.88f));
        var hov = MakeTex(new Color(0.20f, 0.20f, 0.24f, 0.92f));

        _btnStyle = new GUIStyle(GUI.skin.button)
        {
            fontSize  = 11,
            fontStyle = FontStyle.Bold,
            normal    = { background = bg,  textColor = new Color(0.9f, 0.9f, 0.9f) },
            hover     = { background = hov, textColor = Color.white },
            active    = { background = hov, textColor = Color.white }
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
