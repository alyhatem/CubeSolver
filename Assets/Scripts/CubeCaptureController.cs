using System;
using System.IO;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.XR.ARFoundation;
using UnityEngine.XR.ARSubsystems;
using TMPro;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using System.Collections.Generic;
using System.Linq;                           // LINQ: Min, Max, Select…
using OpenCVForUnity.CoreModule;            // Mat, Point, Scalar…
using OpenCVForUnity.ImgprocModule;         // Imgproc.*
using OpenCVForUnity.ImgcodecsModule;       // Imgcodecs.imread
using OpenCVForUnity.UnityUtils;
using OpenCVForUnity.UnityIntegration;
using UnityEngine.Rendering.UI;   // add at top of the file for matToTexture2D
using Kociemba;                   // Kociemba solver integration
using UnityEngine.SceneManagement;         // Scene management for solver transition



public class CubeCaptureController : MonoBehaviour
{
    [Header("AR Input")]
    public ARCameraManager arCameraManager;

    [Header("UI Panels")]
    public GameObject capturePanel;

    [Header("UI Elements")]
    public TextMeshProUGUI captureGuideText;
    public Button captureButton;

    private readonly string[] faceKeys = { "U", "R", "F", "D", "L", "B" };
    private readonly string[] descriptiveFaceKeys = { "Top", "Right", "Front", "Bottom", "Left", "Back" };
    private int currentFaceIndex = 0;
    private Texture2D capturedTexture;
    private Texture2D fullImageForProcessing; // Store full image separately
    
    // Debug data storage
    private Dictionary<string, Mat> faceImages = new Dictionary<string, Mat>();
    private Dictionary<string, List<MatOfPoint>> faceSortedContours = new Dictionary<string, List<MatOfPoint>>();
    private Dictionary<string, List<MatOfPoint>> faceRejectedContours = new Dictionary<string, List<MatOfPoint>>();
    private Dictionary<string, List<MatOfPoint>> faceRecoveredContours = new Dictionary<string, List<MatOfPoint>>();
    
    // Color data storage for immediate processing
    private Dictionary<string, List<Vector3>> faceColorData = new Dictionary<string, List<Vector3>>();
    

    void Start()
    {
        captureButton.onClick.AddListener(OnCapturePressed);
        capturePanel.SetActive(true);;
        UpdateHint();
    }

    void UpdateHint()
    {
        if (currentFaceIndex < faceKeys.Length)
            captureGuideText.text = $"Show {descriptiveFaceKeys[currentFaceIndex]} Face";
    }

    unsafe void OnCapturePressed()
    {
        if (currentFaceIndex >= faceKeys.Length)
            return;

        if (!arCameraManager.TryAcquireLatestCpuImage(out XRCpuImage cpuImage))
        {
            Debug.LogWarning("Failed to acquire image.");
            return;
        }

        using (cpuImage)
        {
            var conversionParams = new XRCpuImage.ConversionParams
            {
                inputRect = new RectInt(0, 0, cpuImage.width, cpuImage.height),
                outputDimensions = new Vector2Int(cpuImage.width, cpuImage.height),
                outputFormat = TextureFormat.RGBA32,
                transformation = XRCpuImage.Transformation.MirrorX
            };

            int size = conversionParams.outputDimensions.x * conversionParams.outputDimensions.y * 4;
            var data = new NativeArray<byte>(size, Allocator.Temp);
            cpuImage.Convert(conversionParams, (System.IntPtr)data.GetUnsafePtr(), size);

            capturedTexture = new Texture2D(cpuImage.width, cpuImage.height, TextureFormat.RGBA32, false);
            capturedTexture.LoadRawTextureData(data);
            capturedTexture.Apply();
            data.Dispose();
        }

        // After texture is created and .Apply() is called

        // For processing: Use full rotated image (better for OpenCV detection)

        // Keep full image for processing, cropped for preview
        fullImageForProcessing = capturedTexture;

        // ShowReviewUI();
        if (capturedTexture == null || fullImageForProcessing == null)
            return;

        string faceKey = faceKeys[currentFaceIndex];
        string path = Path.Combine(Application.persistentDataPath, $"face_{faceKey}.jpg");

        // Save the FULL image for processing (not the cropped preview)
        byte[] jpgData = fullImageForProcessing.EncodeToJPG(95);
        File.WriteAllBytes(path, jpgData);
        Debug.Log($"Saved face {faceKey} to: {path} (full image: {fullImageForProcessing.width}x{fullImageForProcessing.height})");

        // Clean up both textures
        Destroy(capturedTexture);
        Destroy(fullImageForProcessing);
        capturedTexture = null;
        fullImageForProcessing = null;

        // IMMEDIATE PROCESSING: Run ProcessImage() on the just-saved image
        ProcessCapturedFace(faceKey, path);
    }

    void ProcessCapturedFace(string faceKey, string imagePath)
    {
        Debug.Log($"🔍 [CubeCaptureController] Processing face {faceKey} immediately...");
        
        try
        {
            var processor = new CubeProcessor(imagePath);
            List<Vector3> labColors = processor.ProcessImage();
            
            if (labColors.Count == 9)
            {
                // SUCCESS: 9 stickers detected - store colors and auto-advance
                faceColorData[faceKey] = labColors;
                
                // Store debug data
                faceImages[faceKey] = processor.Resized.clone();
                faceSortedContours[faceKey] = new List<MatOfPoint>(processor.SortedContours);
                faceRejectedContours[faceKey] = new List<MatOfPoint>(processor.RejectedContours);
                faceRecoveredContours[faceKey] = new List<MatOfPoint>(processor.RecoveredContours);
                
                Debug.Log($"✅ [CubeCaptureController] Face {faceKey}: SUCCESS - 9 stickers detected, auto-advancing");
                
                // Auto-advance to next face
                currentFaceIndex++;
                UpdateHint();
                capturePanel.SetActive(true);;
                
                // Brief success feedback
                StartCoroutine(ShowSuccessFeedback($"{descriptiveFaceKeys[currentFaceIndex - 1]} Face Captured!"));
                
                // Check if all faces are complete
                if (currentFaceIndex == faceKeys.Length)
                {
                    ProcessAllStoredFaces();
                }
            }
            else
            {
                // FAILURE: Less than 9 stickers - show review panel
                Debug.Log($"⚠️ [CubeCaptureController] Face {faceKey}: Only {labColors.Count} stickers detected - showing review panel");

                // Brief failure feedback
                StartCoroutine(ShowFailureFeedback($"Only {labColors.Count} stickers detected. Retake"));
            }
        }
        catch (Exception ex)
        {
            Debug.LogError($"❌ [CubeCaptureController] Processing error for face {faceKey}: {ex.Message}");
            // Brief failure feedback
            StartCoroutine(ShowFailureFeedback("Processing Failed"));
        }
    }
    
    System.Collections.IEnumerator ShowSuccessFeedback(string message)
    {
        string originalText = captureGuideText.text;
        captureGuideText.text = message;
        yield return new WaitForSeconds(1.0f);
        captureGuideText.text = originalText;
    }

    System.Collections.IEnumerator ShowFailureFeedback(string message, bool restart = false)
    {
        string originalText = captureGuideText.text;
        Color originalTextColor = captureGuideText.color;
        captureGuideText.text = message;
        captureGuideText.color = Color.red;
        yield return new WaitForSeconds(1.5f);
        if (restart)
        {
            captureGuideText.color = originalTextColor;
            captureGuideText.text = "Show Top Face";
            RestartCaptureProcess();
            
        }
        else
        {
            captureGuideText.text = originalText;
            captureGuideText.color = originalTextColor;   
        }
    }
    
    void ProcessAllStoredFaces()
    {
        Debug.Log("🎯 [CubeCaptureController] All 6 faces captured and processed! Starting classification...");
        
        if (faceColorData.Count != 6)
        {
            Debug.LogError($"❌ [CubeCaptureController] Expected 6 faces, but only {faceColorData.Count} were successfully processed");
            return;
        }

        // ─── PHASE 2: COLOR CLASSIFICATION ───────────────────────
        Debug.Log("🎨 [CubeCaptureController] Starting color classification using stored data...");
        
        try
        {
            // Convert stored face color data to ordered list for classifier (U, R, F, D, L, B)
            var orderedFaceData = new List<List<Vector3>>();
            foreach (string faceId in faceKeys)
            {
                if (faceColorData.ContainsKey(faceId) && faceColorData[faceId].Count == 9)
                {
                    orderedFaceData.Add(faceColorData[faceId]);
                    
                    // Log face data for validation
                    var colors = faceColorData[faceId];
                    float avgL = colors.Average(c => c.x);
                    float avgA = colors.Average(c => c.y);
                    float avgB = colors.Average(c => c.z);
                    Debug.Log($"📊 [CubeCaptureController] Face {faceId}: 9 stickers, avg LAB({avgL:F1}, {avgA:F1}, {avgB:F1})");
                }
                else
                {
                    Debug.LogError($"❌ [CubeCaptureController] Face {faceId} missing or incomplete in stored data!");
                    return; // Cannot classify without complete data
                }
            }
            
            // Final summary using stored data
            int totalStickers = faceColorData.Values.Sum(face => face.Count);
            Debug.Log($"🎉 [CubeCaptureController] FINAL SUMMARY (from stored data):");
            Debug.Log($"   📊 Total faces processed: {faceColorData.Count}/6");
            Debug.Log($"   ✅ All faces successful (9 stickers each)");
            Debug.Log($"   🎨 Total stickers: {totalStickers}/54");
            
            // Perform classification
            var classifier = new CubeClassifier();
            string cubeString = classifier.Classify(orderedFaceData);
            
            // ─── CLASSIFICATION RESULTS ───────────────────────
            Debug.Log($"🎯 [CubeCaptureController] ✅ CLASSIFICATION COMPLETE!");
            Debug.Log($"📋 [CubeCaptureController] Cube String: {cubeString}");
            Debug.Log($"📏 [CubeCaptureController] Length: {cubeString.Length}/54 characters");
            
            // Display per-face classification
            for (int i = 0; i < 6; i++)
            {
                string faceId = faceKeys[i];
                string faceClassification = cubeString.Substring(i * 9, 9);
                Debug.Log($"   {faceId} face: {faceClassification}");
            }
            
            // Validate result length first
            if (cubeString.Length != 54)
            {
                Debug.LogError($"❌ [CubeCaptureController] Classification failed: Invalid length {cubeString.Length}");
                return;
            }
            
            // ─── PHASE 3: CUBE STRING VALIDATION ───────────────────────
            Debug.Log("🔍 [CubeCaptureController] Validating cube string before solving...");

            if (ValidateCubeString(cubeString))
            {
                Debug.Log("🏆 [CubeCaptureController] SUCCESS: Cube string validated - ready for solving!");

                // ─── PHASE 4: KOCIEMBA SOLVER ───────────────────────
                SolveCubeWithKociemba(cubeString);
            }
            else
            {
                Debug.LogError("❌ [CubeCaptureController] Cube string validation FAILED - invalid face distribution");
                Debug.LogError("   This indicates a classification error or incomplete cube capture");
                Debug.LogError("   Recommendation: Restart capture process and ensure good lighting/cube visibility");

                // Show error feedback to user before restarting
                StartCoroutine(ShowFailureFeedback("Classification failed. Retake all faces", true));
            }
        }
        catch (Exception ex)
        {
            Debug.LogError($"❌ [CubeCaptureController] Classification error: {ex.Message}");
        }

    }
    
    private bool ValidateCubeString(string cubeString)
    {
        if (string.IsNullOrEmpty(cubeString) || cubeString.Length != 54)
        {
            Debug.LogError($"❌ [CubeCaptureController] Invalid cube string length: {cubeString?.Length ?? 0}/54");
            return false;
        }
        
        // Count each face key in the cube string
        var faceCounts = new Dictionary<char, int>();
        foreach (char c in cubeString)
        {
            if (faceCounts.ContainsKey(c))
                faceCounts[c]++;
            else
                faceCounts[c] = 1;
        }
        
        // Check for exactly 9 of each expected face key
        char[] expectedFaces = { 'U', 'R', 'F', 'D', 'L', 'B' };
        bool isValid = true;
        
        Debug.Log("🔍 [CubeCaptureController] Cube string validation:");
        foreach (char face in expectedFaces)
        {
            int count = faceCounts.ContainsKey(face) ? faceCounts[face] : 0;
            string status = count == 9 ? "✅" : "❌";
            Debug.Log($"   {status} {face}: {count}/9 stickers");
            
            if (count != 9)
                isValid = false;
        }
        
        // Check for unexpected characters
        foreach (var kv in faceCounts)
        {
            if (!expectedFaces.Contains(kv.Key))
            {
                Debug.LogError($"❌ [CubeCaptureController] Unexpected character in cube string: '{kv.Key}' ({kv.Value} times)");
                isValid = false;
            }
        }
        
        if (isValid)
        {
            Debug.Log("✅ [CubeCaptureController] Cube string validation PASSED - exactly 9 of each face");
        }
        else
        {
            Debug.LogError("❌ [CubeCaptureController] Cube string validation FAILED - invalid face distribution");
        }
        
        return isValid;
    }
    
    private void RestartCaptureProcess()
    {
        Debug.Log("🔄 [CubeCaptureController] Restarting capture process - clearing all data...");
        
        // Reset capture state
        currentFaceIndex = 0;
        
        // Clear all stored data
        faceColorData.Clear();
        
        // Clean up debug data and dispose OpenCV Mats
        ClearDebugData();
        
        // Delete all saved face images from persistent storage
        DeleteAllSavedFaces();
        
        // Clean up any lingering textures
        CleanupTextures();
        
        // Reset UI to initial capture state
        // UpdateHint();
        capturePanel.SetActive(true);;
        
        Debug.Log("✅ [CubeCaptureController] Capture process restarted - ready for Face 1");
    }
    
    private void ClearDebugData()
    {
        // Dispose OpenCV Mats to prevent memory leaks
        foreach (var mat in faceImages.Values)
            mat?.Dispose();
        
        // Clear all debug data dictionaries
        faceImages.Clear();
        faceSortedContours.Clear();
        faceRejectedContours.Clear();
        faceRecoveredContours.Clear();
        
        Debug.Log("🗑️ [CubeCaptureController] Debug data cleared and Mats disposed");
    }
    
    private void DeleteAllSavedFaces()
    {
        int deletedCount = 0;
        foreach (string faceKey in faceKeys)
        {
            string path = Path.Combine(Application.persistentDataPath, $"face_{faceKey}.jpg");
            if (File.Exists(path))
            {
                try
                {
                    File.Delete(path);
                    deletedCount++;
                    Debug.Log($"🗑️ [CubeCaptureController] Deleted saved image: {path}");
                }
                catch (Exception ex)
                {
                    Debug.LogWarning($"⚠️ [CubeCaptureController] Could not delete file {path}: {ex.Message}");
                }
            }
        }
        Debug.Log($"🗑️ [CubeCaptureController] Deleted {deletedCount} saved face images");
    }
    
    private void CleanupTextures()
    {
        if (capturedTexture != null)
        {
            Destroy(capturedTexture);
            capturedTexture = null;
        }
        
        if (fullImageForProcessing != null)
        {
            Destroy(fullImageForProcessing);
            fullImageForProcessing = null;
        }
        
        Debug.Log("🗑️ [CubeCaptureController] Textures cleaned up");
    }

    // ─── KOCIEMBA SOLVER INTEGRATION ─────────────────────────────────────────
    private void SolveCubeWithKociemba(string cubeString)
    {
        Debug.Log("🧮 [CubeCaptureController] Starting Kociemba solver...");
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        
        try
        {
            // Run Kociemba solver with runtime table building
            string info;
            string solution = SearchRunTime.solution(cubeString, out info, 
                maxDepth: 24, timeOut: 10000, useSeparator: true, buildTables: false);
            
            stopwatch.Stop();
            
            // Check if solver succeeded
            if (solution.StartsWith("Error"))
            {
                Debug.LogError($"❌ [CubeCaptureController] Solver failed: {solution}");
                Debug.LogError($"   Solver info: {info}");
            }
            else
            {
                Debug.Log($"🎯 [CubeCaptureController] ✅ SOLVE COMPLETE!");
                Debug.Log($"⏱️  Solve time: {stopwatch.ElapsedMilliseconds}ms");
                Debug.Log($"🔄 Solution moves: {solution}");
                Debug.Log($"📋 Move count: {CountMoves(solution)}");
                Debug.Log($"ℹ️  Solver info: {info}");
                
                // Display formatted solution
                Debug.Log("📝 [CubeCaptureController] SOLUTION BREAKDOWN:");
                string[] parts = solution.Split(new string[] { ". " }, System.StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length == 2)
                {
                    Debug.Log($"   Phase 1: {parts[0].Trim()}");
                    Debug.Log($"   Phase 2: {parts[1].Trim()}");
                }
                else
                {
                    Debug.Log($"   Complete: {solution.Trim()}");
                }
                
                // ─── TRANSITION TO SOLVER UI ───────────────────────
                Debug.Log("🎬 [CubeCaptureController] Transitioning to solver UI...");
                TransitionToSolverScene(solution);
            }
        }
        catch (System.Exception ex)
        {
            stopwatch.Stop();
            Debug.LogError($"❌ [CubeCaptureController] Solver exception: {ex.Message}");
            Debug.LogError($"   Stack trace: {ex.StackTrace}");
        }
    }
    
    private int CountMoves(string solution)
    {
        if (string.IsNullOrEmpty(solution)) return 0;
        
        // Remove separators and split by spaces
        string cleanSolution = solution.Replace(". ", " ").Trim();
        if (string.IsNullOrEmpty(cleanSolution)) return 0;
        
        string[] moves = cleanSolution.Split(new char[] { ' ' }, System.StringSplitOptions.RemoveEmptyEntries);
        return moves.Length;
    }
    
    private void TransitionToSolverScene(string solution)
    {
        try
        {
            // Store solution in static variable for solver scene
            CubeSolverController.SolutionString = solution;
            
            Debug.Log($"[CubeCaptureController] Solution stored: '{solution}'");
            Debug.Log("[CubeCaptureController] Loading CubeSolve scene...");
            
            // Load the solver scene
            SceneManager.LoadScene("CubeSolve");
        }
        catch (Exception ex)
        {
            Debug.LogError($"❌ [CubeCaptureController] Failed to transition to solver scene: {ex.Message}");
            Debug.LogError("   Make sure 'CubeSolve' scene exists and is added to Build Settings");
        }
    }
}
