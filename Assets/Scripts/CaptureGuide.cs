using UnityEngine;
using TMPro;
using UnityEngine.XR.ARFoundation;
using UnityEngine.XR.ARSubsystems;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;

public class CaptureGuide : MonoBehaviour
{
    [Header("References")]
    public ARCameraManager arCameraManager;
    public TextMeshProUGUI hintText;

    [Header("Settings")]
    public float blurThreshold = 50f;
    public float maxTiltDegrees = 10f;

    void Start()
    {
        Input.gyro.enabled = true;
    }

    void Update()
    {
        if (arCameraManager == null || hintText == null)
            return;

        string message = "";

        if (IsTilted())
        {
            message = "Tilt phone";
        }
        else if (!TryCheckSharpness(out float sharpness))
        {
            message = ""; // can't read frame
        }
        else if (sharpness < blurThreshold)
        {
            message = "Too blurry";
        }
        else
        {
            message = "Align edges";
        }

        hintText.text = message;
    }

    bool IsTilted()
    {
        var r = Input.gyro.attitude.eulerAngles;
        float pitch = Mathf.DeltaAngle(r.x, 0);
        float roll = Mathf.DeltaAngle(r.z, 0);
        return Mathf.Abs(pitch) > maxTiltDegrees || Mathf.Abs(roll) > maxTiltDegrees;
    }

    unsafe bool TryCheckSharpness(out float sharpness)
    {
        sharpness = 0;

        if (!arCameraManager.TryAcquireLatestCpuImage(out XRCpuImage cpuImage))
            return false;

        using (cpuImage)
        {
            var conversionParams = new XRCpuImage.ConversionParams
            {
                inputRect = new RectInt(0, 0, cpuImage.width, cpuImage.height),
                outputDimensions = new Vector2Int(64, 64),
                outputFormat = TextureFormat.RGBA32,
                transformation = XRCpuImage.Transformation.None
            };

            int size = conversionParams.outputDimensions.x * conversionParams.outputDimensions.y * 4;
            var data = new NativeArray<byte>(size, Allocator.Temp);

            cpuImage.Convert(conversionParams, (System.IntPtr)data.GetUnsafePtr(), size);

            float sum = 0f, sumSq = 0f;
            int pixelCount = size / 4;

            for (int i = 0; i < size; i += 4)
            {
                float r = data[i] / 255f;
                float g = data[i + 1] / 255f;
                float b = data[i + 2] / 255f;
                float luma = 0.299f * r + 0.587f * g + 0.114f * b;

                sum += luma;
                sumSq += luma * luma;
            }

            float mean = sum / pixelCount;
            float variance = (sumSq / pixelCount) - (mean * mean);
            sharpness = variance * 1000f;

            data.Dispose();
            return true;
        }
    }
}
