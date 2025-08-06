using UnityEngine;

/// <summary>
/// Visualizes a single cube face as a wireframe square using LineRenderer components.
/// Used for 3D tracking proof of concept.
/// </summary>
public class SingleFaceWireframe : MonoBehaviour
{
    [Header("Wireframe Settings")]
    public Material wireframeMaterial;
    public Color wireframeColor = Color.green;
    public float lineWidth = 0.02f; // Increased from 0.005f for better visibility
    public bool enableDebugSpheres = true;
    
    private LineRenderer[] edgeLines = new LineRenderer[4];
    private GameObject[] debugSpheres = new GameObject[4];
    private bool isInitialized = false;
    
    // 3D coordinates for a single cube face (scaled up 10x for better visibility)
    private static readonly Vector3[] FACE_CORNERS_LOCAL = {
        new Vector3(-0.285f, -0.285f, 0f),  // bottom-left corner (28.5cm)
        new Vector3( 0.285f, -0.285f, 0f),  // bottom-right corner  
        new Vector3( 0.285f,  0.285f, 0f),  // top-right corner
        new Vector3(-0.285f,  0.285f, 0f)   // top-left corner
    };
    
    void Start()
    {
        CreateWireframeLines();
    }
    
    private void CreateWireframeLines()
    {
        for (int i = 0; i < 4; i++)
        {
            // Create LineRenderer for each edge of the square
            GameObject lineObj = new GameObject($"FaceEdge_{i}");
            lineObj.transform.SetParent(transform);
            
            LineRenderer line = lineObj.AddComponent<LineRenderer>();
            line.material = wireframeMaterial != null ? wireframeMaterial : CreateARCompatibleMaterial();
            line.startColor = wireframeColor;
            line.endColor = wireframeColor;
            line.startWidth = lineWidth;
            line.endWidth = lineWidth;
            line.positionCount = 2;
            line.useWorldSpace = true;
            line.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            line.receiveShadows = false;
            
            // Ensure proper rendering order for AR
            line.sortingOrder = 100;
            
            edgeLines[i] = line;
        }
        
        // Create debug spheres if enabled
        if (enableDebugSpheres)
        {
            CreateDebugSpheres();
        }
        
        isInitialized = true;
        Debug.Log("[SingleFaceWireframe] Wireframe initialized with 4 edges and debug spheres");
        
        // Initially hide the wireframe
        SetVisible(false);
    }
    
    private void CreateDebugSpheres()
    {
        for (int i = 0; i < 4; i++)
        {
            GameObject sphere = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            sphere.name = $"DebugCorner_{i}";
            sphere.transform.SetParent(transform);
            sphere.transform.localScale = Vector3.one * 0.05f; // 5cm spheres
            
            // Make spheres bright red for visibility
            Renderer renderer = sphere.GetComponent<Renderer>();
            if (renderer != null)
            {
                renderer.material = CreateARCompatibleMaterial();
                renderer.material.color = Color.red;
            }
            
            debugSpheres[i] = sphere;
            sphere.SetActive(false); // Initially hidden
        }
    }
    
    private Material CreateARCompatibleMaterial()
    {
        // Use Unlit/Color shader for better AR compatibility
        Material mat = new Material(Shader.Find("Unlit/Color"));
        mat.color = wireframeColor;
        
        // Ensure it renders on top
        mat.renderQueue = 3000;
        
        return mat;
    }
    
    /// <summary>
    /// Updates the wireframe to match the detected face pose
    /// </summary>
    /// <param name="faceCenter">Center position of the detected face in world space</param>
    /// <param name="faceRotation">Rotation of the detected face</param>
    public void UpdateWireframe(Vector3 faceCenter, Quaternion faceRotation)
    {
        if (!isInitialized) return;
        
        // Enhanced debug logging
        float distanceFromCamera = Vector3.Distance(faceCenter, Camera.main.transform.position);
        Debug.Log($"[SingleFaceWireframe] Updating wireframe at position: {faceCenter}, rotation: {faceRotation.eulerAngles}, distance: {distanceFromCamera:F2}m");
        
        // Calculate world positions of face corners
        Vector3[] worldCorners = new Vector3[4];
        for (int i = 0; i < 4; i++)
        {
            // Transform local face corners to world space
            worldCorners[i] = faceCenter + faceRotation * FACE_CORNERS_LOCAL[i];
        }
        
        // Debug log corner positions
        for (int i = 0; i < 4; i++)
        {
            Debug.Log($"[SingleFaceWireframe] Corner {i}: {worldCorners[i]}");
        }
        
        // Update LineRenderer positions to form square
        for (int i = 0; i < 4; i++)
        {
            int nextCorner = (i + 1) % 4;
            edgeLines[i].SetPosition(0, worldCorners[i]);
            edgeLines[i].SetPosition(1, worldCorners[nextCorner]);
        }
        
        // Update debug spheres if enabled
        if (enableDebugSpheres && debugSpheres[0] != null)
        {
            for (int i = 0; i < 4; i++)
            {
                debugSpheres[i].transform.position = worldCorners[i];
                debugSpheres[i].SetActive(true);
            }
        }
        
        // Add Debug.DrawLine fallback for Scene View visualization
        for (int i = 0; i < 4; i++)
        {
            int nextCorner = (i + 1) % 4;
            Debug.DrawLine(worldCorners[i], worldCorners[nextCorner], Color.cyan, 0.1f);
        }
        
        // Make wireframe visible
        SetVisible(true);
        
        Debug.Log("[SingleFaceWireframe] Wireframe updated and made visible");
    }
    
    /// <summary>
    /// Show or hide the wireframe
    /// </summary>
    public void SetVisible(bool visible)
    {
        if (!isInitialized) return;
        
        Debug.Log($"[SingleFaceWireframe] Setting wireframe visibility: {visible}");
        
        foreach (var line in edgeLines)
        {
            if (line != null)
            {
                line.enabled = visible;
                line.gameObject.SetActive(visible);
            }
        }
        
        // Also control debug spheres visibility
        if (enableDebugSpheres)
        {
            foreach (var sphere in debugSpheres)
            {
                if (sphere != null)
                    sphere.SetActive(visible);
            }
        }
    }
    
    /// <summary>
    /// Update wireframe color
    /// </summary>
    public void SetColor(Color color)
    {
        wireframeColor = color;
        
        if (!isInitialized) return;
        
        foreach (var line in edgeLines)
        {
            if (line != null)
            {
                line.startColor = color;
                line.endColor = color;
            }
        }
    }
    
    void OnDestroy()
    {
        // Clean up LineRenderer objects
        foreach (var line in edgeLines)
        {
            if (line != null && line.gameObject != null)
                DestroyImmediate(line.gameObject);
        }
        
        // Clean up debug spheres
        foreach (var sphere in debugSpheres)
        {
            if (sphere != null)
                DestroyImmediate(sphere);
        }
    }
}