using System.IO;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.XR.ARFoundation;
using UnityEngine.XR.ARSubsystems;
using TMPro;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;

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

    private Rect GetCropRect()
    {
        // Get overlay corners in world space (Vector3[])
        Vector3[] corners = new Vector3[4];
        gridOverlay.GetWorldCorners(corners);
        Debug.Log("overlayCorners: " + corners[0] + "," + corners[1] + corners[2] + "," + corners[3]);

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
        Debug.Log("min.x: " + min.x);
        Debug.Log("min.y: " + min.y);
        Debug.Log("max.x: " + max.x);
        Debug.Log("max.y: " + max.y);
        // Apply padding (10% inward from edges)
        float padX = (max.x - min.x) * cropPadding * 0.5f;
        float padY = (max.y - min.y) * cropPadding * 0.5f;
        // Debug.Log("padX: " + padX);
        // Debug.Log("padY: " + padY);
        Debug.Log("RectX Pad: " + (min.x + padX));
        Debug.Log("RectY Pad: " + (Screen.height - max.y + padY));
        Debug.Log("RectWidth Pad: " + ((max.x - min.x) - padX * 2));
        Debug.Log("RectHeight Pad: " + ((max.y - min.y) - padY * 2));

        // Debug.Log("RectX: " + min.x);
        // Debug.Log("RectY: " + (Screen.height - max.y));
        // Debug.Log("RectWidth: " + (max.x - min.x));
        // Debug.Log("RectHeight: " + (max.y - min.y));

        return new Rect(min.x, Screen.height - max.y, max.x - min.x, max.y - min.y);
    }

    private Texture2D CropTexture(Texture2D src, Rect cropRect)
    {
        // Convert screen coordinates to texture coordinates
        int x = Mathf.FloorToInt(cropRect.x * src.width / Screen.width);
        int y = Mathf.FloorToInt(cropRect.y * src.height / Screen.height);
        int width = Mathf.FloorToInt(cropRect.width * src.width / Screen.width);
        int height = Mathf.FloorToInt(cropRect.height * src.height / Screen.height);
        Debug.Log("src width, height: " + src.width + "," + src.height);
        Debug.Log("Screen width, height: " + Screen.width + "," + Screen.height);
        Debug.Log("x, y, w, h: " + x + ", " + y + ", " + width + ", " + height);
        
        // Clamp to texture dimensions
        x = Mathf.Clamp(x, 0, src.width - 1);
        y = Mathf.Clamp(y, 0, src.height - 1);
        width = Mathf.Clamp(width, 1, src.width - x);
        height = Mathf.Clamp(height, 1, src.height - y);
        Debug.Log("Clamped x, y, w, h: " + x + ", " + y + ", " + width + ", " + height);
        
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

        // Now crop the rotated texture
        Rect cropRect = GetCropRect();  // still in screen space
        Texture2D cropped = CropTexture(capturedTexture, cropRect);
        Destroy(capturedTexture);

        capturedTexture = cropped;
        
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
        if (capturedTexture == null)
            return;

        string faceKey = faceKeys[currentFaceIndex];
        string path = Path.Combine(Application.persistentDataPath, $"face_{faceKey}.jpg");
        byte[] jpgData = capturedTexture.EncodeToJPG(95);
        File.WriteAllBytes(path, jpgData);
        Debug.Log($"Saved face {faceKey} to: {path}");

        Destroy(capturedTexture);
        capturedTexture = null;

        currentFaceIndex++;
        UpdateHint();
        ShowCaptureUI();
    }

    void OnRetakePressed()
    {
        if (capturedTexture != null)
        {
            Destroy(capturedTexture);
            capturedTexture = null;
        }
        ShowCaptureUI();
    }
}
