using System;
using System.Collections.Generic;
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
    private CubeProcessor processor;

    // Boundary visualization using cube prefabs
    public GameObject cubePrefab; // Drag ARMobileTemplateAssets/Prefabs cube here
    private List<GameObject> boundaryMarkers = new List<GameObject>();

    [Header("Coordinate Calibration")]
    public Vector2 coordinateOffset = Vector2.zero; // Manual offset to align markers with cube

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
        processor = new CubeProcessor();

        // Boundary visualization now uses Debug.DrawLine (no setup needed)

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
        
        processor.UpdateInputMat(inputMat);
        processor.ProcessImage(true);
        
        processingStopwatch.Stop();
        
        if (processor.SquareContours.Count >= 6)
        {
            // Extract boundary data for drawing
            Vector4 boundary = processor.Boundary;
            UpdateTrackingStatus($"Found {processor.SquareContours.Count} stickers ({processingStopwatch.ElapsedMilliseconds}ms)", true);
            
            // Draw boundary rectangle
            DrawCubeBoundary(boundary, true);
        }
        else
        {
            UpdateTrackingStatus($"Found {processor.SquareContours.Count} stickers ({processingStopwatch.ElapsedMilliseconds}ms)", false);
            
            // Hide boundary rectangle when tracking fails
            // DrawCubeBoundary(Vector4.zero, false);
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

    // Boundary visualization now uses cube prefab instantiation

    private void DrawCubeBoundary(Vector4 boundary, bool show)
    {
        Debug.Log($"[DrawCubeBoundary] Called with boundary=({boundary.x:F1}, {boundary.y:F1}, {boundary.z:F1}, {boundary.w:F1}), show={show}");

        // Clean up existing boundary markers
        ClearBoundaryMarkers();

        if (!show)
        {
            Debug.Log("[DrawCubeBoundary] Not showing boundary (tracking failed)");
            return;
        }

        if (cubePrefab == null)
        {
            Debug.LogWarning("[DrawCubeBoundary] cubePrefab is not assigned! Please drag ARMobileTemplateAssets/Prefabs cube to cubePrefab field");
            return;
        }

        // Transform boundary coordinates to screen space
        Vector2[] screenCorners = TransformBoundaryToScreenSpace(boundary);
        
        Debug.Log($"[DrawCubeBoundary] Screen corners: [{screenCorners[0]}, {screenCorners[1]}, {screenCorners[2]}, {screenCorners[3]}]");

        // Convert screen coordinates to world space for cube instantiation
        Camera camera = Camera.main ?? arCameraManager.GetComponent<Camera>();
        if (camera == null)
        {
            Debug.LogWarning("[DrawCubeBoundary] No camera found for coordinate conversion");
            return;
        }

        // Convert 2D screen coords to 3D world positions (closer depth for AR)
        float cubeDepth = 1f; // 1 meter in front of camera for better AR visibility
        Vector3[] worldCorners = new Vector3[4];
        
        for (int i = 0; i < 4; i++)
        {
            Vector3 screenPoint = new Vector3(screenCorners[i].x, screenCorners[i].y, cubeDepth);
            worldCorners[i] = camera.ScreenToWorldPoint(screenPoint);
        }

        Debug.Log($"[DrawCubeBoundary] World corners: [{worldCorners[0]}, {worldCorners[1]}, {worldCorners[2]}, {worldCorners[3]}]");

        // Instantiate small cube markers at each corner
        for (int i = 0; i < 4; i++)
        {
            GameObject marker = Instantiate(cubePrefab, worldCorners[i], Quaternion.identity);
            
            // Scale down to be small markers
            marker.transform.localScale = Vector3.one * 0.05f; // 5cm cubes
            
            // Optional: Color them green for successful tracking
            Renderer renderer = marker.GetComponent<Renderer>();
            if (renderer != null)
            {
                renderer.material.color = Color.green;
            }
            
            boundaryMarkers.Add(marker);
        }

        Debug.Log($"[DrawCubeBoundary] Instantiated {boundaryMarkers.Count} cube markers at boundary corners");
    }

    private void ClearBoundaryMarkers()
    {
        foreach (GameObject marker in boundaryMarkers)
        {
            if (marker != null)
            {
                Destroy(marker);
            }
        }
        boundaryMarkers.Clear();
    }

    private Vector2[] TransformBoundaryToScreenSpace(Vector4 boundary)
    {
        // Boundary is now in 480×640 resized image space (after rotation and resize)
        float minX = boundary.x;
        float minY = boundary.y; 
        float maxX = boundary.z;
        float maxY = boundary.w;

        Debug.Log($"[TransformBoundary] Input boundary (480×640 space): ({minX:F1}, {minY:F1}, {maxX:F1}, {maxY:F1})");
        Debug.Log($"[TransformBoundary] Screen size: {Screen.width} x {Screen.height}");

        // Calculate scale factors from processing resolution (480×640) to screen resolution
        float scaleX = (float)Screen.width / 480f;
        float scaleY = (float)Screen.height / 640f;
        
        Debug.Log($"[TransformBoundary] Scale factors: X={scaleX:F2}, Y={scaleY:F2}");

        // Scale boundary from 480×640 to full screen resolution
        float scaledMinX = minX * scaleX;
        float scaledMaxX = maxX * scaleX;
        float scaledMinY = minY * scaleY;
        float scaledMaxY = maxY * scaleY;

        Debug.Log($"[TransformBoundary] After scaling (screen resolution): ({scaledMinX:F1}, {scaledMinY:F1}, {scaledMaxX:F1}, {scaledMaxY:F1})");

        // Apply coordinate system conversion from OpenCV (Y-down) to Unity (Y-up)
        // OpenCV: origin top-left, Y increases downward
        // Unity: origin bottom-left, Y increases upward
        float unityMinX = scaledMinX;
        float unityMaxX = scaledMaxX;
        // FLIP Y-AXIS: Screen.height - openCV_y converts from top-left to bottom-left origin
        float unityMinY = Screen.height - scaledMaxY; // OpenCV maxY becomes Unity minY (bottom)
        float unityMaxY = Screen.height - scaledMinY; // OpenCV minY becomes Unity maxY (top)

        Debug.Log($"[TransformBoundary] After Y-flip (Unity coords): ({unityMinX:F1}, {unityMinY:F1}, {unityMaxX:F1}, {unityMaxY:F1})");

        // Apply calibration offset for fine-tuning alignment
        unityMinX += coordinateOffset.x;
        unityMaxX += coordinateOffset.x;
        unityMinY += coordinateOffset.y;
        unityMaxY += coordinateOffset.y;

        // Clamp to screen bounds for safety
        float clampedMinX = Mathf.Clamp(unityMinX, 0, Screen.width);
        float clampedMaxX = Mathf.Clamp(unityMaxX, 0, Screen.width);
        float clampedMinY = Mathf.Clamp(unityMinY, 0, Screen.height);
        float clampedMaxY = Mathf.Clamp(unityMaxY, 0, Screen.height);

        Debug.Log($"[TransformBoundary] Final coords with offset ({coordinateOffset.x:F1}, {coordinateOffset.y:F1}): ({clampedMinX:F1}, {clampedMinY:F1}, {clampedMaxX:F1}, {clampedMaxY:F1})");

        // Create screen space rectangle corners (Unity coordinate system)
        Vector2[] corners = new Vector2[4];
        
        // Rectangle corners in Unity screen space (bottom-left origin, Y-up)
        corners[0] = new Vector2(clampedMinX, clampedMinY); // Bottom-left
        corners[1] = new Vector2(clampedMaxX, clampedMinY); // Bottom-right
        corners[2] = new Vector2(clampedMaxX, clampedMaxY); // Top-right  
        corners[3] = new Vector2(clampedMinX, clampedMaxY); // Top-left

        return corners;
    }

    void OnDestroy()
    {
        // Clean up reusable processor
        processor?.Dispose();
        processor = null;
        
        // Clean up boundary markers
        ClearBoundaryMarkers();
        
        Debug.Log("[CaptureGuide] Cleaned up reusable processor and boundary markers");
    }

}