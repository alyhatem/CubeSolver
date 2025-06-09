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

        Texture2D rotated = RotateTexture90CW(capturedTexture);
        previewImage.texture = rotated;
        previewImage.rectTransform.sizeDelta = new Vector2(rotated.width, rotated.height);
        Destroy(capturedTexture);
        capturedTexture = rotated;
        

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
