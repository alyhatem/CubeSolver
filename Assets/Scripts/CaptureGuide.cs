using System;
using UnityEngine;
using UnityEngine.XR.ARFoundation;
using UnityEngine.XR.ARSubsystems;
using TMPro;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using OpenCVForUnity.CoreModule;
using OpenCVForUnity.ImgprocModule;
using OpenCVForUnity.UnityUtils;
using OpenCVForUnity.UnityIntegration;

public class CaptureGuide : MonoBehaviour
{
    [Header("References")]
    public ARCameraManager arCameraManager;
    public TextMeshProUGUI hintText;

    [Header("Performance Settings")]
    public int frameSkipCount = 2; // Process every 3rd frame
    public int analysisWidth = 320; // Smaller resolution for real-time analysis
    public int analysisHeight = 240;

    private CubeProcessor realTimeProcessor;
    private int frameCounter = 0;
    private float lastProcessTime = 0f;
    private bool isProcessing = false;

    void Start()
    {
        // Initialize processor with dummy path (we'll provide Mat directly)
        realTimeProcessor = new CubeProcessor("");
        
        if (hintText != null)
            hintText.text = "Point camera at cube";
    }

    void Update()
    {
        if (arCameraManager == null || hintText == null || isProcessing)
            return;

        // Frame rate limiting - process every Nth frame
        frameCounter++;
        if (frameCounter < frameSkipCount)
            return;
        
        frameCounter = 0;

        // Time-based limiting - don't process more than 10 times per second
        if (Time.time - lastProcessTime < 0.1f)
            return;

        // ProcessCurrentFrame();
    }

    unsafe void ProcessCurrentFrame()
    {
        if (!arCameraManager.TryAcquireLatestCpuImage(out XRCpuImage cpuImage))
        {
            hintText.text = "Camera not ready";
            return;
        }

        isProcessing = true;
        lastProcessTime = Time.time;

        try
        {
            using (cpuImage)
            {
                // Convert AR image to texture with smaller resolution for performance
                var conversionParams = new XRCpuImage.ConversionParams
                {
                    inputRect = new RectInt(0, 0, cpuImage.width, cpuImage.height),
                    outputDimensions = new Vector2Int(analysisWidth, analysisHeight),
                    outputFormat = TextureFormat.RGBA32,
                    transformation = XRCpuImage.Transformation.MirrorX
                };

                int size = conversionParams.outputDimensions.x * conversionParams.outputDimensions.y * 4;
                var data = new NativeArray<byte>(size, Allocator.Temp);
                cpuImage.Convert(conversionParams, (System.IntPtr)data.GetUnsafePtr(), size);

                // Create texture and convert to Mat
                Texture2D frameTexture = new Texture2D(analysisWidth, analysisHeight, TextureFormat.RGBA32, false);
                frameTexture.LoadRawTextureData(data);
                frameTexture.Apply();
                data.Dispose();

                // Convert to OpenCV Mat
                Mat frameMat = ConvertTextureToMat(frameTexture);
                
                // Clean up texture immediately
                DestroyImmediate(frameTexture);

                if (frameMat != null)
                {
                    // Use CubeProcessor's simplified counting method
                    int contourCount = realTimeProcessor.ProcessImageForCounting(frameMat);
                    
                    // Update UI based on results
                    UpdateFeedback(contourCount);
                    
                    // Clean up Mat
                    frameMat.Dispose();
                }
            }
        }
        catch (Exception ex)
        {
            Debug.LogWarning($"[CaptureGuide] Frame processing error: {ex.Message}");
            hintText.text = "Processing...";
        }
        finally
        {
            isProcessing = false;
        }
    }

    private Mat ConvertTextureToMat(Texture2D texture)
    {
        try
        {
            // Convert Unity texture to OpenCV Mat
            Mat mat = new Mat(texture.height, texture.width, CvType.CV_8UC4);
            OpenCVMatUtils.Texture2DToMat(texture, mat);
            
            // Convert RGBA to BGR for processing
            Mat bgrMat = new Mat();
            Imgproc.cvtColor(mat, bgrMat, Imgproc.COLOR_RGBA2BGR);
            mat.Dispose();
            
            return bgrMat;
        }
        catch (Exception ex)
        {
            Debug.LogWarning($"[CaptureGuide] Mat conversion error: {ex.Message}");
            return null;
        }
    }

    private void UpdateFeedback(int contourCount)
    {
        if (hintText == null) return;

        switch (contourCount)
        {
            case 9:
                hintText.text = "✓ 9 stickers detected";
                hintText.color = Color.green;
                break;
            case 0:
                hintText.text = "No stickers detected";
                hintText.color = Color.red;
                break;
            default:
                hintText.text = $"{contourCount} stickers detected";
                hintText.color = Color.yellow;
                break;
        }
    }

    void OnDestroy()
    {
        // Clean up processor
        if (realTimeProcessor?.Resized != null)
        {
            realTimeProcessor.Resized.Dispose();
        }
    }
}