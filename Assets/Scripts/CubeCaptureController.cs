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
    [Header("References")]
    public ARCameraManager arCameraManager;
    public TextMeshProUGUI hintText;
    public Button captureButton;

    private readonly string[] faceKeys = { "U", "R", "F", "D", "L", "B" };
    private int currentFaceIndex = 0;

    void Start()
    {
        UpdateHint();
        captureButton.onClick.AddListener(CaptureCurrentFace);
    }

    void UpdateHint()
    {
        if (currentFaceIndex < faceKeys.Length)
            hintText.text = $"Capture face: {faceKeys[currentFaceIndex]}";
        else
            hintText.text = "All faces captured.";
    }

    public unsafe void CaptureCurrentFace()
    {
        if (!arCameraManager.TryAcquireLatestCpuImage(out XRCpuImage cpuImage))
        {
            Debug.LogWarning("Could not acquire camera image.");
            return;
        }

        using (cpuImage)
        {
            var conversionParams = new XRCpuImage.ConversionParams
            {
                inputRect = new RectInt(0, 0, cpuImage.width, cpuImage.height),
                outputDimensions = new Vector2Int(cpuImage.width, cpuImage.height),
                outputFormat = TextureFormat.RGB24,
                transformation = XRCpuImage.Transformation.MirrorX
            };

            int size = cpuImage.GetConvertedDataSize(conversionParams);
            var buffer = new NativeArray<byte>(size, Allocator.Temp);
            cpuImage.Convert(conversionParams, (System.IntPtr)buffer.GetUnsafePtr(), buffer.Length);

            Texture2D tex = new Texture2D(cpuImage.width, cpuImage.height, TextureFormat.RGB24, false);
            tex.LoadRawTextureData(buffer);
            tex.Apply();
            buffer.Dispose();

            byte[] jpgData = tex.EncodeToJPG(95);
            Destroy(tex);

            string faceKey = faceKeys[currentFaceIndex];
            string path = Path.Combine(Application.persistentDataPath, $"face_{faceKey}.jpg");
            File.WriteAllBytes(path, jpgData);
            Debug.Log($"Saved: {path}");

            currentFaceIndex++;
            UpdateHint();
        }
    }
}
