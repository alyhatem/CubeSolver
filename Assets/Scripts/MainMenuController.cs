using UnityEngine;
using UnityEngine.SceneManagement;

public class MainMenuController : MonoBehaviour
{
    /// <summary>
    /// Load the CubeCapture scene when the Start button is pressed
    /// </summary>
    public void LoadCubeCaptureScene()
    {
        SceneManager.LoadScene("CubeCapture");
    }

    /// <summary>
    /// Quit the application (useful for build versions)
    /// </summary>
    public void QuitApplication()
    {
        Application.Quit();
        
        // For testing in Unity Editor
        #if UNITY_EDITOR
        UnityEditor.EditorApplication.isPlaying = false;
        #endif
    }
}