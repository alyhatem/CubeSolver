using System;
using System.IO;
using System.Linq;
using UnityEngine;
using UnityEngine.XR.ARFoundation;
using UnityEngine.XR.ARSubsystems;
using TMPro;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using UnityEngine.UI;
using OpenCVForUnity.CoreModule;
using OpenCVForUnity.ImgprocModule;
using OpenCVForUnity.ImgcodecsModule;
using OpenCVForUnity.Calib3dModule;
using OpenCVForUnity.UnityUtils;
using OpenCVForUnity.UnityIntegration;
using OpenCVForUnity.VideoModule;

public class CaptureGuide : MonoBehaviour
{
    [Header("References")]
    public ARCameraManager arCameraManager;
    public TextMeshProUGUI hintText;
    public Material wireframeMaterial;

    [Header("Debug UI")]
    public RawImage debugImage; // Shows processed frames
    public RawImage debugImage1; // Shows contour visualization
    public bool showDebugUI = true; // Enable real-time debug display

    [Header("Performance Settings")]
    public int frameSkipCount = 2; // Process every 3rd frame
    public int analysisWidth = 640; // Higher resolution for better contour detection
    public int analysisHeight = 480;

    [Header("3D Tracking Settings")]
    public float minFaceArea = 5000f; // Minimum area for face detection
    public float maxReprojectionError = 8.0f; // Maximum error for pose estimation
    public bool showDebugInfo = true;
    public bool saveDebugImages = false; // Save intermediate processing images for debugging

    // 3D Tracking components
    private Mat cameraMatrix;
    private Mat distCoeffs;

    // Processing state
    private int frameCounter = 0;
    private float lastProcessTime = 0f;
    private bool isProcessing = false;
    private bool isCubeTracked = false;

    // Debug state
    private int debugFrameCounter = 0;
    private float lastDebugUpdateTime = 0f;
    
    // FPS tracking
    private int fpsFrameCount = 0;
    private float fpsLastTime = 0f;

    private Texture2D frameTexture;

    // Reusable CubeProcessor for performance optimization
    private CubeProcessor reusableCubeProcessor;

    // 3D model for single cube face (57mm standard size)
    private static readonly Point3[] FACE_3D_POINTS = {
        new Point3(-0.0285, -0.0285, 0), // bottom-left
        new Point3( 0.0285, -0.0285, 0), // bottom-right
        new Point3( 0.0285,  0.0285, 0), // top-right
        new Point3(-0.0285,  0.0285, 0)  // top-left
    };

    void Start()
    {
        InitializeCameraMatrix();
        
        // Initialize reusable processor for performance
        reusableCubeProcessor = new CubeProcessor();

        if (hintText != null)
            hintText.text = "Point camera at cube face";
    }

    private void InitializeCameraMatrix()
    {
        // Create placeholder camera matrix - will be updated with AR Foundation data
        cameraMatrix = Mat.eye(3, 3, CvType.CV_64FC1);

        // Try to get real camera parameters from AR Foundation
        UpdateCameraMatrixFromAR();

        // No distortion for now
        distCoeffs = Mat.zeros(4, 1, CvType.CV_64FC1);

        Debug.Log("[CaptureGuide] Camera matrix initialized");
    }

    private void UpdateCameraMatrixFromAR()
    {
        try
        {
            if (arCameraManager != null)
            {
                // For now, use estimated values based on typical mobile camera parameters
                // TODO: Implement proper AR Foundation camera intrinsics when available
                Debug.Log("[CaptureGuide] AR camera manager available, using estimated parameters");
            }

            // Fallback to estimated values
            float estimatedFx = analysisWidth * 0.8f; // Rough estimate
            float estimatedFy = analysisHeight * 0.8f;
            float estimatedCx = analysisWidth / 2.0f;
            float estimatedCy = analysisHeight / 2.0f;

            cameraMatrix.put(0, 0, estimatedFx);
            cameraMatrix.put(1, 1, estimatedFy);
            cameraMatrix.put(0, 2, estimatedCx);
            cameraMatrix.put(1, 2, estimatedCy);

            Debug.Log($"[CaptureGuide] Using estimated camera parameters: fx={estimatedFx:F1}, fy={estimatedFy:F1}");
        }
        catch (Exception ex)
        {
            Debug.LogWarning($"[CaptureGuide] Failed to get AR camera parameters: {ex.Message}");

            // Use basic fallback values
            cameraMatrix.put(0, 0, 800.0);
            cameraMatrix.put(1, 1, 800.0);
            cameraMatrix.put(0, 2, analysisWidth / 2.0);
            cameraMatrix.put(1, 2, analysisHeight / 2.0);
        }
    }

    void Update()
    {
        // FPS tracking for performance monitoring
        fpsFrameCount++;
        if (Time.time - fpsLastTime >= 1.0f)
        {
            float fps = fpsFrameCount / (Time.time - fpsLastTime);
            Debug.Log($"[CaptureGuide] Overall FPS: {fps:F1}");
            fpsFrameCount = 0;
            fpsLastTime = Time.time;
        }

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

        ProcessCurrentFrame();
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

                frameTexture = new Texture2D(cpuImage.width, cpuImage.height, TextureFormat.RGBA32, false);
                frameTexture.LoadRawTextureData(data);
                frameTexture.Apply();
                data.Dispose();
            }
            
            // Use 'using' to ensure frameMat is always disposed
            using (Mat frameMat = new Mat(frameTexture.height, frameTexture.width, CvType.CV_8UC4))
            {
                OpenCVMatUtils.Texture2DToMat(frameTexture, frameMat);
                Destroy(frameTexture);
                TrackCube(frameMat);
            } // frameMat automatically disposed here
        }
        catch (Exception ex)
        {
            Debug.LogWarning($"[CaptureGuide] Frame processing error: {ex.Message}");
            UpdateTrackingStatus("Processing error", false);
        }
        finally
        {
            isProcessing = false;
        }
    }

    private void TrackCube(Mat inputMat)
    {
        // Use reusable processor for performance - eliminates per-frame allocation overhead
        var processingStopwatch = System.Diagnostics.Stopwatch.StartNew();
        
        reusableCubeProcessor.UpdateInputMat(inputMat);
        reusableCubeProcessor.ProcessImage(true);
        
        processingStopwatch.Stop();
        
        if (reusableCubeProcessor.SquareContours.Count >= 6)
        {
            UpdateTrackingStatus($"Found {reusableCubeProcessor.SquareContours.Count} stickers ({processingStopwatch.ElapsedMilliseconds}ms)", true);
        }
        else
        {
            UpdateTrackingStatus($"Found {reusableCubeProcessor.SquareContours.Count} stickers ({processingStopwatch.ElapsedMilliseconds}ms)", false);
        }
    }

    private bool EstimateFacePose(Point[] imageCorners, out Vector3 position, out Quaternion rotation)
    {
        position = Vector3.zero;
        rotation = Quaternion.identity;

        try
        {
            // Create MatOfPoint3f for 3D object points
            MatOfPoint3f objectPoints = new MatOfPoint3f();
            objectPoints.fromArray(FACE_3D_POINTS);

            // Create MatOfPoint2f for 2D image points
            MatOfPoint2f imagePoints = new MatOfPoint2f();
            imagePoints.fromArray(imageCorners);

            // Solve PnP to get pose
            Mat rvec = new Mat();
            Mat tvec = new Mat();

            // Create MatOfDouble for distortion coefficients
            MatOfDouble distCoeffsMat = new MatOfDouble();
            distCoeffsMat.fromArray(new double[] { 0, 0, 0, 0 });

            bool success = Calib3d.solvePnP(objectPoints, imagePoints, cameraMatrix, distCoeffsMat, rvec, tvec);

            distCoeffsMat.Dispose();

            if (success)
            {
                // Convert OpenCV pose to Unity coordinates
                double[] tvecArray = new double[3];
                double[] rvecArray = new double[3];
                tvec.get(0, 0, tvecArray);
                rvec.get(0, 0, rvecArray);

                // Convert to Unity coordinate system
                position = new Vector3((float)tvecArray[0], -(float)tvecArray[1], (float)tvecArray[2]);
                rotation = OpenCVARUtils.ConvertRvecToRot(rvecArray);

                // Transform to Unity's coordinate system (OpenCV uses right-handed, Unity uses left-handed)
                position.z = -position.z;
                rotation = new Quaternion(-rotation.x, rotation.y, -rotation.z, rotation.w);
            }

            // Clean up
            objectPoints.Dispose();
            imagePoints.Dispose();
            rvec.Dispose();
            tvec.Dispose();

            return success;
        }
        catch (Exception ex)
        {
            Debug.LogWarning($"[CaptureGuide] Pose estimation error: {ex.Message}");
            return false;
        }
    }

    private void UpdateTrackingStatus(string message, bool isTracking)
    {
        if (hintText == null) return;

        hintText.text = message;
        hintText.color = isTracking ? Color.green : Color.red;

        if (showDebugInfo)
        {
            // Debug.Log($"[CaptureGuide] {message}");
        }
    }

    void OnDestroy()
    {
        // Clean up reusable processor
        reusableCubeProcessor?.Dispose();
        reusableCubeProcessor = null;
        Debug.Log("[CaptureGuide] Cleaned up reusable processor");
    }

}