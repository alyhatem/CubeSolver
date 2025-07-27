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
using OpenCVForUnity.UnityIntegration;   // add at top of the file for matToTexture2D



public class CubeCaptureController : MonoBehaviour
{
    [Header("AR Input")]
    public ARCameraManager arCameraManager;

    [Header("UI Panels")]
    public GameObject capturePanel;
    public GameObject reviewPanel;

    [Header("UI Elements")]
    public TextMeshProUGUI hintText;
    public RawImage previewImage;
    public Button captureButton;
    public Button confirmButton;
    public Button retakeButton;

    [Header("Crop Settings")]
    public RectTransform gridOverlay; // Assign your UI overlay in Inspector
    public float cropPadding = 0.1f; // 10% padding

    private readonly string[] faceKeys = { "U", "R", "F", "D", "L", "B" };
    private int currentFaceIndex = 0;
    private Texture2D capturedTexture;
    private Texture2D fullImageForProcessing; // Store full image separately

    void Start()
    {
        captureButton.onClick.AddListener(OnCapturePressed);
        confirmButton.onClick.AddListener(OnConfirmPressed);
        retakeButton.onClick.AddListener(OnRetakePressed);
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
    }

    void ShowReviewUI()
    {
        capturePanel.SetActive(false);
        reviewPanel.SetActive(true);
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
                
                foreach (var c in proc.SortedContours)
                    Imgproc.drawContours(proc.Resized, new List<MatOfPoint> { c },
                                        -1, new Scalar(0, 0, 255), 2);

                Texture2D tex = new Texture2D(proc.Resized.cols(), proc.Resized.rows(),
                                            TextureFormat.RGBA32, false);
                OpenCVMatUtils.MatToTexture2D(proc.Resized, tex);
                previewImage.texture = tex;      // reuse the RawImage already in your scene
                previewImage.rectTransform.sizeDelta =
                new Vector2(proc.Resized.cols(), proc.Resized.rows());
                processors[kv.Key] = proc;
                ShowReviewUI();
            }

            // ─── final summary ───────────────────────
            int totalStickers = allFaceResults.Values.Sum(face => face.Count);
            int successfulFaces = allFaceResults.Values.Count(face => face.Count == 9);
            
            Debug.Log($"🎉 [CubeCaptureController] FINAL SUMMARY:");
            Debug.Log($"   📊 Total faces processed: {allFaceResults.Count}/6");
            Debug.Log($"   ✅ Successful faces (9 stickers): {successfulFaces}/6");
            Debug.Log($"   🎨 Total stickers detected: {totalStickers}/54");
            
            if (successfulFaces == 6 && totalStickers == 54)
            {
                Debug.Log($"   🏆 PERFECT! Complete cube analysis ready for solving!");
            }
            else
            {
                Debug.LogWarning($"   ⚠️  Incomplete data - may need to retake some faces");
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
}
