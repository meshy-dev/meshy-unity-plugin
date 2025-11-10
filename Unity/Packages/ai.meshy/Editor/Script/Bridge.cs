using UnityEngine;
using UnityEditor;
using System.Linq;

public class MeshyBridgeWindow : EditorWindow
{
    private static MeshyBridge bridgeInstance;
    static bool isBridgeRunning => bridgeInstance != null;
    private GUIContent runButtonContent;
    private GUIContent stopButtonContent;

    [MenuItem("Meshy/Bridge")]
    public static void ShowWindow()
    {
        MeshyBridgeWindow window = GetWindow<MeshyBridgeWindow>("Meshy Bridge");
        window.minSize = new Vector2(250, 100);
        window.maxSize = new Vector2(400, 150);
    }

    private void OnEnable()
    {
        runButtonContent = new("Run Bridge");
        stopButtonContent = new("Bridge ON");
    }

    private void OnGUI()
    {
        EditorGUILayout.BeginVertical();
        GUILayout.Space(10);
        GUIStyle buttonStyle = new(GUI.skin.button) { fontSize = 14, fontStyle = FontStyle.Bold, fixedHeight = 40 };
        Color originalColor = GUI.backgroundColor;
        if (isBridgeRunning) GUI.backgroundColor = new Color(0.4f, 0.6f, 1.0f);
        GUIContent currentContent = isBridgeRunning ? stopButtonContent : runButtonContent;
        if (GUILayout.Button(currentContent, buttonStyle)) ToggleBridgeState();
        GUI.backgroundColor = originalColor;
        EditorGUILayout.EndVertical();
    }

    private void ToggleBridgeState()
    {
        if (isBridgeRunning) StopBridge();
        else StartBridge();
    }

    private static void StartBridge()
    {
	    if (bridgeInstance != null) return;
	    foreach (MeshyBridge existingBridge in FindObjectsByType<MeshyBridge>(FindObjectsInactive.Include, FindObjectsSortMode.None).ToList())
		    if (Application.isPlaying) Destroy(existingBridge.gameObject);
		    else DestroyImmediate(existingBridge.gameObject);
	        
	    GameObject go = new("MeshyBridge");
	    bridgeInstance = go.AddComponent<MeshyBridge>();
	    Debug.Log("Meshy Bridge started");
    }
    
    private static void StopBridge()
    {
        if (bridgeInstance == null) return;
        bridgeInstance.StopServer();
        DestroyImmediate(bridgeInstance.gameObject);
        bridgeInstance = null;
        Debug.Log("Meshy Bridge stopped");
    }
}

public static class MeshyBridgeCommands
{
    // This class can be used for command-line execution of bridge functions.
    // Example: Unity.exe -quit -batchmode -executeMethod MeshyBridgeCommands.Start
}