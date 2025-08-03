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



public class CubeCaptureController : MonoBehaviour
{
    [Header("AR Input")]
    public ARCameraManager arCameraManager;

    [Header("UI Panels")]
    public GameObject capturePanel;
    public GameObject reviewPanel;
    public GameObject debugPanel;

    [Header("UI Elements")]
    public TextMeshProUGUI hintText;
    public RawImage previewImage;
    public Button captureButton;
    public Button confirmButton;
    public Button retakeButton;

    [Header("Crop Settings")]
    public RectTransform gridOverlay; // Assign your UI overlay in Inspector
    public float cropPadding = 0.1f; // 10% padding

    [Header("Debug UI")]
    public TMP_Dropdown faceDropdown;
    public RawImage debugImage;
    public Button toggleButton;
    public TextMeshProUGUI toggleButtonText;

    private readonly string[] faceKeys = { "U", "R", "F", "D", "L", "B" };
    private int currentFaceIndex = 0;
    private Texture2D capturedTexture;
    private Texture2D fullImageForProcessing; // Store full image separately
    
    // Debug data storage
    private Dictionary<string, Mat> faceImages = new Dictionary<string, Mat>();
    private Dictionary<string, List<MatOfPoint>> faceSortedContours = new Dictionary<string, List<MatOfPoint>>();
    private Dictionary<string, List<MatOfPoint>> faceRejectedContours = new Dictionary<string, List<MatOfPoint>>();
    private Dictionary<string, List<MatOfPoint>> faceRecoveredContours = new Dictionary<string, List<MatOfPoint>>();
    private bool showContours = false;
    
    private string currentSelectedFace = "U";

    void Start()
    {
        captureButton.onClick.AddListener(OnCapturePressed);
        confirmButton.onClick.AddListener(OnConfirmPressed);
        retakeButton.onClick.AddListener(OnRetakePressed);
        
        // Initialize debug UI
        if (faceDropdown != null && toggleButton != null)
        {
            faceDropdown.onValueChanged.AddListener(OnFaceSelectionChanged);
            toggleButton.onClick.AddListener(OnToggleContours);
            if (toggleButtonText != null)
                toggleButtonText.text = "Show Contours"; // Initial state
        }
        
        ShowCaptureUI();
        UpdateHint();
    }

    void UpdateHint()
    {
        if (currentFaceIndex < faceKeys.Length)
            hintText.text = $"Show face: {faceKeys[currentFaceIndex]}";
        else
            hintText.text = "All faces captured.";
    }

    void ShowCaptureUI()
    {
        capturePanel.SetActive(true);
        reviewPanel.SetActive(false);
        debugPanel.SetActive(false);
    }

    void ShowReviewUI()
    {
        capturePanel.SetActive(false);
        reviewPanel.SetActive(true);
        debugPanel.SetActive(false);
    }

    void ShowDebugUI()
    {
        capturePanel.SetActive(false);
        reviewPanel.SetActive(false);
        debugPanel.SetActive(true);
    }   
    private Texture2D RotateTexture90CW(Texture2D src)
    {
        int width = src.width;
        int height = src.height;
        Texture2D result = new Texture2D(height, width, src.format, false);
        Color[] pixels = src.GetPixels();

        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                result.SetPixel(y, width - x - 1, pixels[y * width + x]);
            }
        }

        result.Apply();
        return result;
    }

    private UnityEngine.Rect GetCropRect()
    {
        // Get overlay corners in world space (Vector3[])
        Vector3[] corners = new Vector3[4];
        gridOverlay.GetWorldCorners(corners);
        // Debug.Log("overlayCorners: " + corners[0] + "," + corners[1] + corners[2] + "," + corners[3]);

        // Convert to screen space (Vector2)
        Vector2 min = new Vector2(float.MaxValue, float.MaxValue);
        Vector2 max = new Vector2(float.MinValue, float.MinValue);

        foreach (Vector3 corner in corners)
        {
            Vector2 screenPoint = RectTransformUtility.WorldToScreenPoint(null, corner);
            min.x = Mathf.Min(min.x, screenPoint.x);
            min.y = Mathf.Min(min.y, screenPoint.y);
            max.x = Mathf.Max(max.x, screenPoint.x);
            max.y = Mathf.Max(max.y, screenPoint.y);
        }
        // Debug.Log("min.x: " + min.x);
        // Debug.Log("min.y: " + min.y);
        // Debug.Log("max.x: " + max.x);
        // Debug.Log("max.y: " + max.y);
        // Apply padding (10% inward from edges)
        float padX = (max.x - min.x) * cropPadding * 0.5f;
        float padY = (max.y - min.y) * cropPadding * 0.5f;
        // Debug.Log("padX: " + padX);
        // Debug.Log("padY: " + padY);
        // Debug.Log("RectX Pad: " + (min.x + padX));
        // Debug.Log("RectY Pad: " + (Screen.height - max.y + padY));
        // Debug.Log("RectWidth Pad: " + ((max.x - min.x) - padX * 2));
        // Debug.Log("RectHeight Pad: " + ((max.y - min.y) - padY * 2));

        // Debug.Log("RectX: " + min.x);
        // Debug.Log("RectY: " + (Screen.height - max.y));
        // Debug.Log("RectWidth: " + (max.x - min.x));
        // Debug.Log("RectHeight: " + (max.y - min.y));

        return new UnityEngine.Rect(min.x, Screen.height - max.y, max.x - min.x, max.y - min.y);
    }

    private Texture2D CropTexture(Texture2D src, UnityEngine.Rect cropRect)
    {
        // Convert screen coordinates to texture coordinates
        int x = Mathf.FloorToInt(cropRect.x * src.width / Screen.width);
        int y = Mathf.FloorToInt(cropRect.y * src.height / Screen.height);
        int width = Mathf.FloorToInt(cropRect.width * src.width / Screen.width);
        int height = Mathf.FloorToInt(cropRect.height * src.height / Screen.height);
        // Debug.Log("src width, height: " + src.width + "," + src.height);
        // Debug.Log("Screen width, height: " + Screen.width + "," + Screen.height);
        // Debug.Log("x, y, w, h: " + x + ", " + y + ", " + width + ", " + height);
        
        // Clamp to texture dimensions
        x = Mathf.Clamp(x, 0, src.width - 1);
        y = Mathf.Clamp(y, 0, src.height - 1);
        width = Mathf.Clamp(width, 1, src.width - x);
        height = Mathf.Clamp(height, 1, src.height - y);
        // Debug.Log("Clamped x, y, w, h: " + x + ", " + y + ", " + width + ", " + height);
        
        // Extract pixels
        Color[] pixels = src.GetPixels(x, y, width, height);
        Texture2D cropped = new Texture2D(width, height);
        cropped.SetPixels(pixels);
        cropped.Apply();
        return cropped;
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
        capturedTexture = RotateTexture90CW(capturedTexture);

        // For processing: Use full rotated image (better for OpenCV detection)
        // For preview: Show cropped version
        UnityEngine.Rect cropRect = GetCropRect();
        Texture2D croppedForPreview = CropTexture(capturedTexture, cropRect);
        
        // Keep full image for processing, cropped for preview
        fullImageForProcessing = capturedTexture;
        capturedTexture = croppedForPreview; // This shows in preview
        
        // // Crop to overlay guide
        // Rect cropRect = GetCropRect();
        // Texture2D cropped = CropTexture(capturedTexture, cropRect);
        // Destroy(capturedTexture); // Free the rotated version

        // //  Rotate the texture
        // Texture2D rotated = RotateTexture90CW(cropped);
        // Destroy(cropped); // Free the original

        // // Set the final cropped texture
        // capturedTexture = rotated;

        previewImage.texture = capturedTexture;
        previewImage.rectTransform.sizeDelta = new Vector2(capturedTexture.width, capturedTexture.height);
        ShowReviewUI();
    }

    void OnConfirmPressed()
    {
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

        currentFaceIndex++;
        UpdateHint();
        ShowCaptureUI();

        if (currentFaceIndex == faceKeys.Length)        // all six faces captured
        {
            Debug.Log("🎯 [CubeCaptureController] All 6 faces captured! Starting complete processing...");
            
            var faces     = CubeProcessor.LoadFaces(); 
            var processors = new Dictionary<string, CubeProcessor>();
            var allFaceResults = new Dictionary<string, List<Vector3>>();

            Debug.Log($"📁 [CubeCaptureController] Loaded {faces.Count} face images for processing");

            foreach (var kv in faces)
            {
                Debug.Log($"🔍 [CubeCaptureController] Processing face {kv.Key}...");
                
                var proc = new CubeProcessor(Path.Combine(Application.persistentDataPath,
                                                        $"face_{kv.Key}.jpg"));

                // Process the complete pipeline and extract LAB colors
                List<Vector3> labColors = proc.ProcessImage();
                allFaceResults[kv.Key] = labColors;

                // ─── face summary ───────────────────────
                string status = labColors.Count == 9 ? "✅ SUCCESS" : "⚠️  PARTIAL";
                Debug.Log($"📊 [CubeCaptureController] Face {kv.Key}: {status} - {proc.SortedContours.Count} contours, {labColors.Count} colors");
                
                // Quick LAB statistics for this face
                if (labColors.Count > 0)
                {
                    float avgL = labColors.Average(c => c.x);
                    float avgA = labColors.Average(c => c.y);
                    float avgB = labColors.Average(c => c.z);
                    Debug.Log($"    Average LAB: ({avgL:F1}, {avgA:F1}, {avgB:F1})");
                }
                
                // Store debug data instead of showing preview
                faceImages[kv.Key] = proc.Resized.clone();
                faceSortedContours[kv.Key] = new List<MatOfPoint>(proc.SortedContours);
                faceRejectedContours[kv.Key] = new List<MatOfPoint>(proc.RejectedContours);
                faceRecoveredContours[kv.Key] = new List<MatOfPoint>(proc.RecoveredContours);
                
                processors[kv.Key] = proc;
            }

            // ─── final summary ───────────────────────
            int totalStickers = allFaceResults.Values.Sum(face => face.Count);
            int successfulFaces = allFaceResults.Values.Count(face => face.Count == 9);
            
            Debug.Log($"🎉 [CubeCaptureController] FINAL SUMMARY:");
            Debug.Log($"   📊 Total faces processed: {allFaceResults.Count}/6");
            Debug.Log($"   ✅ Successful faces (9 stickers): {successfulFaces}/6");
            Debug.Log($"   🎨 Total stickers detected: {totalStickers}/54");
            
            // Always show debug UI after processing all 6 faces
            ShowDebugUI();
            UpdateDebugDisplay(); // Initialize the display
            
            if (successfulFaces == 6 && totalStickers == 54)
            {
                Debug.Log($"   🏆 PERFECT! Complete cube analysis ready for solving!");
                
                // ─── PHASE 2: COLOR CLASSIFICATION ───────────────────────
                Debug.Log("🎨 [CubeCaptureController] Starting color classification...");
                
                try
                {
                    // Convert face results to ordered list for classifier (U, R, F, D, L, B)
                    var orderedFaceData = new List<List<Vector3>>();
                    foreach (string faceId in faceKeys)
                    {
                        if (allFaceResults.ContainsKey(faceId) && allFaceResults[faceId].Count == 9)
                        {
                            orderedFaceData.Add(allFaceResults[faceId]);
                        }
                        else
                        {
                            Debug.LogError($"❌ [CubeCaptureController] Face {faceId} missing or incomplete!");
                            return; // Cannot classify without complete data
                        }
                    }
                    
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
                    
                    // Validate result
                    if (cubeString.Length == 54)
                    {
                        Debug.Log("🏆 [CubeCaptureController] SUCCESS: Ready for cube solving!");
                        
                        // ─── PHASE 3: KOCIEMBA SOLVER ───────────────────────
                        SolveCubeWithKociemba(cubeString);
                    }
                    else
                    {
                        Debug.LogError($"❌ [CubeCaptureController] Classification failed: Invalid length {cubeString.Length}");
                    }
                }
                catch (Exception ex)
                {
                    Debug.LogError($"❌ [CubeCaptureController] Classification error: {ex.Message}");
                }
            }
            else
            {
                Debug.LogWarning($"   ⚠️  Incomplete data - may need to retake some faces");
                Debug.LogWarning("   🔄 Skipping classification until all faces are complete");
            }

            Debug.Log("📱 [CubeCaptureController] Initial contour extraction finished for all faces");
        }

    }

    void OnRetakePressed()
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
        ShowCaptureUI();
    }

    // Debug UI Methods
    private void OnFaceSelectionChanged(int value)
    {
        currentSelectedFace = faceKeys[value];
        UpdateDebugDisplay();
    }

    private void OnToggleContours()
    {
        showContours = !showContours;
        if (toggleButtonText != null)
            toggleButtonText.text = showContours ? "Show Original" : "Show Contours";
        UpdateDebugDisplay();
    }

    private void UpdateDebugDisplay()
    {
        if (!faceImages.ContainsKey(currentSelectedFace) || debugImage == null) return;
        
        Mat displayImage;
        if (showContours)
        {
            // Create copy and draw color-coded contours
            displayImage = faceImages[currentSelectedFace].clone();
            
            // Draw rejected contours in red (BGR format: 0,0,255 = red)
            if (faceRejectedContours.ContainsKey(currentSelectedFace))
            {
                foreach (var contour in faceRejectedContours[currentSelectedFace])
                {
                    if (contour != null && contour.total() > 0)
                        Imgproc.drawContours(displayImage, new List<MatOfPoint> { contour }, -1, new Scalar(0, 0, 255), 2);
                }
            }
            
            // Draw recovered contours in blue (BGR format: 255,0,0 = blue)
            if (faceRecoveredContours.ContainsKey(currentSelectedFace))
            {
                foreach (var contour in faceRecoveredContours[currentSelectedFace])
                {
                    if (contour != null && contour.total() > 0)
                        Imgproc.drawContours(displayImage, new List<MatOfPoint> { contour }, -1, new Scalar(255, 0, 0), 2);
                }
            }
            
            // Draw accepted contours in green (BGR format: 0,255,0 = green)
            if (faceSortedContours.ContainsKey(currentSelectedFace))
            {
                foreach (var contour in faceSortedContours[currentSelectedFace])
                {
                    if (contour != null && contour.total() > 0)
                        Imgproc.drawContours(displayImage, new List<MatOfPoint> { contour }, -1, new Scalar(0, 255, 0), 2);
                }
            }
        }
        else
        {
            // Show original image - create a copy to avoid modifying stored data
            displayImage = faceImages[currentSelectedFace].clone();
        }
        
        // Convert BGR to RGB for Unity display
        Mat rgbImage = new Mat();
        Imgproc.cvtColor(displayImage, rgbImage, Imgproc.COLOR_BGR2RGB);
        
        // Convert to texture and display
        Texture2D tex = new Texture2D(rgbImage.cols(), rgbImage.rows(), TextureFormat.RGBA32, false);
        OpenCVMatUtils.MatToTexture2D(rgbImage, tex);
        debugImage.texture = tex;
        
        // Clean up temporary mats
        if (showContours || !showContours) // Always dispose displayImage since we're cloning now
            displayImage.Dispose();
        rgbImage.Dispose();
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
}
