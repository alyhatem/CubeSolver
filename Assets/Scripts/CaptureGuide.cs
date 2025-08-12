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

// Data structure to hold oriented bounding box information
public struct OrientedBoundingBox
{
    public Point center;
    public float angle; // Angle in degrees from OpenCV minAreaRect
}

public class CaptureGuide : MonoBehaviour
{
    [Header("References")]
    public ARCameraManager arCameraManager;
    public TextMeshProUGUI hintText;

    [Header("Performance Settings")]
    public int frameSkipCount = 2; // Process every 3rd frame
    public int analysisWidth = 640; // Higher resolution for better contour detection
    public int analysisHeight = 480;

    // 3D Tracking components
    private Mat cameraMatrix;
    private Mat distCoeffs;

    // Processing state
    private int frameCounter = 0;
    private float lastProcessTime = 0f;
    private bool isProcessing = false;

    
    // FPS tracking
    private int fpsFrameCount = 0;
    private float fpsLastTime = 0f;

    private Texture2D frameTexture;

    // Reusable CubeProcessor for performance optimization
    private CubeProcessor processor;

    // Center anchor visualization for arrow system
    public GameObject centerAnchorPrefab; // Drag ARMobileTemplateAssets/Prefabs cube here for center anchor
    private GameObject centerAnchor = null;
    private GameObject directionArrow = null;

    [Header("Coordinate Calibration")]
    public Vector2 coordinateOffset = Vector2.zero; // Manual offset to align markers with cube
    
    [Header("Adaptive Detection Thresholds")]
    [Range(0.0001f, 0.01f)]
    public float minStickerAreaPercent = 0.0008f; // Min sticker area as % of image (0.08%)
    [Range(0.01f, 0.2f)]
    public float maxStickerAreaPercent = 0.08f;   // Max sticker area as % of image (8%)
    
    [Header("Animated Arrow")]
    public GameObject animatedArrowPrefab; // Drag one of the arrow prefabs from Animation_Textures here
    
    // CPU image dimensions for proper scaling calculation
    private int cpuImageWidth = 0;
    private int cpuImageHeight = 0;


    void Start()
    {
        InitializeCameraMatrix();
        
        // Initialize reusable processor for performance
        processor = new CubeProcessor();

        // Center anchor visualization (no setup needed)

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

                // Store CPU image dimensions for proper scaling calculation
                cpuImageWidth = cpuImage.width;
                cpuImageHeight = cpuImage.height;
                
                // Log CPU image vs screen size for debugging boundary size mismatch
                Debug.Log($"[ProcessCurrentFrame] CPU Image size: {cpuImage.width}×{cpuImage.height}");
                Debug.Log($"[ProcessCurrentFrame] Screen size: {Screen.width}×{Screen.height}");
                Debug.Log($"[ProcessCurrentFrame] Texture size: {frameTexture.width}×{frameTexture.height}");
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
        
        // Apply adaptive threshold parameters from Unity Inspector
        processor.MinAreaPercent = minStickerAreaPercent;
        processor.MaxAreaPercent = maxStickerAreaPercent;
        
        processor.UpdateInputMat(inputMat);
        processor.ProcessImage(true);
        
        processingStopwatch.Stop();
        
        if (processor.SquareContours.Count >= 6 && processor.SquareContours.Count <= 9)
        {
            UpdateTrackingStatus($"Found {processor.SquareContours.Count} stickers ({processingStopwatch.ElapsedMilliseconds}ms)", true);
            
            // Draw oriented boundary using sticker contours
            DrawCubeBoundary(processor, true);
        }
        else
        {
            UpdateTrackingStatus($"Found {processor.SquareContours.Count} stickers ({processingStopwatch.ElapsedMilliseconds}ms)", false);
            
            // Hide boundary when outside 6-9 range
            DrawCubeBoundary(processor, false);
        }
    }

    private void UpdateTrackingStatus(string message, bool isTracking)
    {
        if (hintText == null) return;

        hintText.text = message;
        hintText.color = isTracking ? Color.green : Color.red;
    }

    private void DrawCubeBoundary(CubeProcessor cubeProcessor, bool show)
    {
        Debug.Log($"[DrawCubeBoundary] Called with {cubeProcessor.SquareContours.Count} contours, show={show}");

        // Clean up existing center anchor
        ClearCenterAnchor();

        if (!show)
        {
            Debug.Log("[DrawCubeBoundary] Not showing center anchor (tracking failed or outside 6-9 range)");
            return;
        }

        if (centerAnchorPrefab == null)
        {
            Debug.LogWarning("[DrawCubeBoundary] centerAnchorPrefab is not assigned! Please drag ARMobileTemplateAssets/Prefabs cube to centerAnchorPrefab field");
            return;
        }

        if (cubeProcessor.SquareContours.Count == 0)
        {
            Debug.LogWarning("[DrawCubeBoundary] No contours available for center calculation");
            return;
        }

        try
        {
            // Get oriented bounding box (center and angle) from all sticker contours
            OrientedBoundingBox orientedBox = GetOrientedBoundingBox(cubeProcessor.SquareContours);
            
            Debug.Log($"[DrawCubeBoundary] Center in 480x640 space: ({orientedBox.center.x:F1}, {orientedBox.center.y:F1}), Angle: {orientedBox.angle:F1}°");

            // Transform center to screen space
            Vector2 screenCenter = TransformCenterToScreenSpace(orientedBox.center);
            
            Debug.Log($"[DrawCubeBoundary] Screen center: ({screenCenter.x:F1}, {screenCenter.y:F1})");

            // Convert screen coordinates to world space for anchor instantiation
            Camera camera = Camera.main ?? arCameraManager.GetComponent<Camera>();
            if (camera == null)
            {
                Debug.LogWarning("[DrawCubeBoundary] No camera found for coordinate conversion");
                return;
            }

            // Convert 2D screen center to 3D world position
            float anchorDepth = 1f; // 1 meter in front of camera for better AR visibility
            Vector3 screenPoint = new Vector3(screenCenter.x, screenCenter.y, anchorDepth);
            Vector3 worldCenter = camera.ScreenToWorldPoint(screenPoint);

            Debug.Log($"[DrawCubeBoundary] World center: {worldCenter}");

            // Convert OpenCV angle to Unity rotation around camera's forward axis (Z-axis)
            // OpenCV angle is typically -90° to 0°, we need to adjust for Unity coordinate system
            float unityAngle = -orientedBox.angle; // Invert for Unity coordinate system
            Quaternion cubeRotation = Quaternion.AngleAxis(unityAngle, Vector3.forward);
            
            Debug.Log($"[DrawCubeBoundary] OpenCV angle: {orientedBox.angle:F1}°, Unity angle: {unityAngle:F1}°");

            // Instantiate center anchor with cube rotation
            centerAnchor = Instantiate(centerAnchorPrefab, worldCenter, cubeRotation);
            
            // Scale down to be a distinctive but visible anchor
            centerAnchor.transform.localScale = Vector3.one * 0.08f; // 8cm cube for center anchor
            
            // Color it blue to distinguish from corner markers
            Renderer renderer = centerAnchor.GetComponent<Renderer>();
            if (renderer != null)
            {
                renderer.material.color = Color.blue;
            }

            Debug.Log($"[DrawCubeBoundary] Instantiated center anchor at world position: {worldCenter}");

            // Create animated arrow at anchor position first, then set local offset
            // Use cube rotation directly for the animated arrow
            directionArrow = CreateAnimatedArrow(worldCenter, cubeRotation, 2.0f); // Scale 2.0 for big arrow
            
            // Set as child FIRST, then use local positioning
            directionArrow.transform.SetParent(centerAnchor.transform);
            
            // Set local position to hover above the anchor (15cm up in local space)
            directionArrow.transform.localPosition = Vector3.up * 0.15f;

            Debug.Log($"[DrawCubeBoundary] Created animated arrow as child of anchor with local offset {Vector3.up * 0.15f}");
        }
        catch (System.Exception ex)
        {
            Debug.LogError($"[DrawCubeBoundary] Error creating center anchor: {ex.Message}");
        }
    }

    private OrientedBoundingBox GetOrientedBoundingBox(List<MatOfPoint> squareContours)
    {
        // Combine all contour points into a single list (like Python: all points from all contours)
        List<Point> allPoints = new List<Point>();
        
        foreach (MatOfPoint contour in squareContours)
        {
            Point[] contourPoints = contour.toArray();
            allPoints.AddRange(contourPoints);
        }

        if (allPoints.Count < 3)
        {
            Debug.LogWarning("[GetOrientedBoundingBox] Not enough points for oriented bounding box");
            // Return center of screen as fallback with no rotation
            return new OrientedBoundingBox 
            { 
                center = new Point(240, 320), // Center of 480×640 processing resolution
                angle = 0f // No rotation fallback
            };
        }

        // Create MatOfPoint2f from all combined points (minAreaRect requires MatOfPoint2f)
        MatOfPoint2f allPointsContour = new MatOfPoint2f(allPoints.ToArray());

        // Get oriented bounding rectangle (equivalent to cv2.minAreaRect)
        RotatedRect orientedRect = Imgproc.minAreaRect(allPointsContour);
        
        Debug.Log($"[GetOrientedBoundingBox] RotatedRect - center: ({orientedRect.center.x:F1}, {orientedRect.center.y:F1}), size: ({orientedRect.size.width:F1}, {orientedRect.size.height:F1}), angle: {orientedRect.angle:F1}°");

        allPointsContour.Dispose();
        
        return new OrientedBoundingBox 
        { 
            center = orientedRect.center, 
            angle = (float)orientedRect.angle 
        };
    }

    private Vector2 TransformCenterToScreenSpace(Point center)
    {
        Debug.Log($"[TransformCenter] CPU Image size: {cpuImageWidth} x {cpuImageHeight}");
        Debug.Log($"[TransformCenter] Screen size: {Screen.width} x {Screen.height}");
        Debug.Log($"[TransformCenter] Center in 480x640 space: ({center.x:F1}, {center.y:F1})");

        // Calculate scale factors from processing resolution (480×640) to CPU image resolution  
        float scaleX = (float)cpuImageWidth / 480f;
        float scaleY = (float)cpuImageHeight / 640f;
        
        Debug.Log($"[TransformCenter] Scale factors (CPU based): X={scaleX:F2}, Y={scaleY:F2}");

        // Scale from 480×640 to CPU image resolution
        float scaledX = (float)center.x * scaleX;
        float scaledY = (float)center.y * scaleY;

        // Now convert from CPU image coordinates to screen coordinates
        float cpuToScreenScaleX = (float)Screen.width / cpuImageWidth;
        float cpuToScreenScaleY = (float)Screen.height / cpuImageHeight;
        
        Debug.Log($"[TransformCenter] CPU to Screen scale factors: X={cpuToScreenScaleX:F2}, Y={cpuToScreenScaleY:F2}");

        // Apply CPU image to screen scaling
        scaledX *= cpuToScreenScaleX;
        scaledY *= cpuToScreenScaleY;

        // Apply coordinate system conversion from OpenCV (Y-down) to Unity (Y-up)
        float unityX = scaledX;
        float unityY = Screen.height - scaledY; // Y-flip

        // Apply calibration offset
        unityX += coordinateOffset.x;
        unityY += coordinateOffset.y;

        // Clamp to screen bounds
        unityX = Mathf.Clamp(unityX, 0, Screen.width);
        unityY = Mathf.Clamp(unityY, 0, Screen.height);

        Debug.Log($"[TransformCenter] Final screen center: ({unityX:F1}, {unityY:F1})");

        return new Vector2(unityX, unityY);
    }

    private GameObject CreateAnimatedArrow(Vector3 position, Quaternion rotation, float scale = 0.2f)
    {
        if (animatedArrowPrefab == null)
        {
            Debug.LogWarning("[CreateAnimatedArrow] No animated arrow prefab assigned! Please drag an arrow prefab to the animatedArrowPrefab field.");
            return null;
        }
        
        // Instantiate the animated arrow prefab
        GameObject arrow = Instantiate(animatedArrowPrefab, position, rotation);
        
        // Scale the arrow for better visibility
        arrow.transform.localScale = Vector3.one * scale;
        
        Debug.Log($"[CreateAnimatedArrow] Created animated arrow at {position} with scale {scale}");
        
        return arrow;
    }

    private void ClearCenterAnchor()
    {
        if (centerAnchor != null)
        {
            Destroy(centerAnchor);
            centerAnchor = null;
        }
        
        if (directionArrow != null)
        {
            Destroy(directionArrow);
            directionArrow = null;
        }
    }

    void OnDestroy()
    {
        // Clean up reusable processor
        processor?.Dispose();
        processor = null;
        
        // Clean up center anchor
        ClearCenterAnchor();
        
        Debug.Log("[CaptureGuide] Cleaned up reusable processor and center anchor");
    }

}