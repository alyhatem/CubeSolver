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

// Data structure to hold 3D pose information
public struct Pose3D
{
    public Vector3 position;    // Translation in Unity world space
    public Vector3 rotation;    // Rotation in Euler angles (degrees)
    public bool isValid;        // Whether this pose is reliable
    public float confidence;    // Confidence score (0-1)
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
    
    [Header("3D Pose Estimation")]
    [Range(45f, 70f)]
    public float cubeSize = 57f; // Standard Rubik's cube size in millimeters
    public bool use3DTracking = true; // Toggle between 2D and 3D tracking
    [Range(0.1f, 2.0f)]
    public float poseSmoothing = 0.8f; // Temporal smoothing factor for pose
    [Range(0.3f, 1.0f)]
    public float minPoseConfidence = 0.5f; // Minimum confidence to accept 3D pose
    
    // 3D tracking state
    private Pose3D lastValidPose;
    private bool hasPreviousPose = false;
    private Vector3[] cubeCorners3D; // 8 corners of the cube in 3D model space
    
    // CPU image dimensions for proper scaling calculation
    private int cpuImageWidth = 0;
    private int cpuImageHeight = 0;


    void Start()
    {
        InitializeCameraMatrix();
        
        // Initialize reusable processor for performance
        processor = new CubeProcessor();

        // Initialize 3D cube model
        InitializeCubeGeometry();

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

    private void InitializeCubeGeometry()
    {
        // Define 8 corners of a cube in 3D model space (centered at origin)
        // Standard Rubik's cube is 57mm, so half-size is 28.5mm
        float halfSize = cubeSize / 2f; // Convert to half-size for corner calculation
        
        // Define cube corners in millimeters (will convert to meters for Unity)
        // OpenCV coordinate system: X-right, Y-down, Z-forward
        cubeCorners3D = new Vector3[8]
        {
            // Front face (Z = +halfSize)
            new Vector3(-halfSize, -halfSize, halfSize),  // 0: Front-top-left
            new Vector3(halfSize, -halfSize, halfSize),   // 1: Front-top-right
            new Vector3(halfSize, halfSize, halfSize),    // 2: Front-bottom-right
            new Vector3(-halfSize, halfSize, halfSize),   // 3: Front-bottom-left
            
            // Back face (Z = -halfSize)
            new Vector3(-halfSize, -halfSize, -halfSize), // 4: Back-top-left
            new Vector3(halfSize, -halfSize, -halfSize),  // 5: Back-top-right
            new Vector3(halfSize, halfSize, -halfSize),   // 6: Back-bottom-right
            new Vector3(-halfSize, halfSize, -halfSize)   // 7: Back-bottom-left
        };
        
        // Convert from millimeters to meters for Unity world space
        for (int i = 0; i < cubeCorners3D.Length; i++)
        {
            cubeCorners3D[i] /= 1000f; // mm to meters
        }
        
        Debug.Log($"[InitializeCubeGeometry] Initialized cube geometry with size {cubeSize}mm ({cubeCorners3D.Length} corners)");
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

        if (!show)
        {
            // Hide anchor but don't destroy (cube temporarily lost)
            if (centerAnchor != null)
            {
                centerAnchor.SetActive(false);
                Debug.Log("[DrawCubeBoundary] Hiding center anchor (tracking failed or outside 6-9 range)");
            }
            return;
        }

        if (centerAnchorPrefab == null)
        {
            Debug.LogWarning("[DrawCubeBoundary] centerAnchorPrefab is not assigned! Please drag a cube prefab to centerAnchorPrefab field");
            return;
        }

        if (cubeProcessor.SquareContours.Count == 0)
        {
            Debug.LogWarning("[DrawCubeBoundary] No contours available for center calculation");
            return;
        }

        try
        {
            // Get camera reference for coordinate conversion
            Camera camera = Camera.main ?? arCameraManager.GetComponent<Camera>();
            if (camera == null)
            {
                Debug.LogWarning("[DrawCubeBoundary] No camera found for coordinate conversion");
                return;
            }

            Vector3 worldCenter;
            Quaternion cubeRotation;

            if (use3DTracking)
            {
                // Use full 3D pose estimation
                Pose3D estimatedPose = EstimateCubePose3D(cubeProcessor.SquareContours);
                
                if (estimatedPose.isValid)
                {
                    // Apply temporal smoothing
                    Pose3D smoothedPose = ApplyPoseSmoothing(estimatedPose);
                    
                    // Convert from camera space to world space
                    worldCenter = ConvertCameraPoseToWorldSpace(smoothedPose.position, camera);
                    cubeRotation = ConvertCameraRotationToWorldSpace(smoothedPose.rotation, camera);
                    
                    Debug.Log($"[DrawCubeBoundary] 3D Pose: world_pos=({worldCenter.x:F3}, {worldCenter.y:F3}, {worldCenter.z:F3}), confidence={smoothedPose.confidence:F2}");
                }
                else
                {
                    // Fallback to 2D tracking if 3D pose estimation fails
                    Debug.LogWarning("[DrawCubeBoundary] 3D pose estimation failed, falling back to 2D tracking");
                    var result = Get2DTrackingPose(cubeProcessor.SquareContours, camera);
                    worldCenter = result.position;
                    cubeRotation = result.rotation;
                }
            }
            else
            {
                // Use 2D+depth tracking (original method)
                var result = Get2DTrackingPose(cubeProcessor.SquareContours, camera);
                worldCenter = result.position;
                cubeRotation = result.rotation;
            }

            if (centerAnchor == null)
            {
                // CREATE ONCE on first detection
                CreatePersistentAnchor(worldCenter, cubeRotation);
            }
            else
            {
                // UPDATE EVERY FRAME during tracking
                UpdateAnchorTransform(worldCenter, cubeRotation);
            }
        }
        catch (System.Exception ex)
        {
            Debug.LogError($"[DrawCubeBoundary] Error updating center anchor: {ex.Message}");
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

    private List<Point> ExtractCubeCorners2D(List<MatOfPoint> squareContours)
    {
        if (squareContours.Count < 4)
        {
            Debug.LogWarning($"[ExtractCubeCorners2D] Need at least 4 stickers for corner detection, found {squareContours.Count}");
            return new List<Point>();
        }

        // Get centers of all detected stickers
        List<Point> stickerCenters = new List<Point>();
        foreach (MatOfPoint contour in squareContours)
        {
            // Calculate centroid of each sticker
            Point[] points = contour.toArray();
            double sumX = 0, sumY = 0;
            foreach (Point pt in points)
            {
                sumX += pt.x;
                sumY += pt.y;
            }
            Point center = new Point(sumX / points.Length, sumY / points.Length);
            stickerCenters.Add(center);
        }

        // Sort stickers to identify corner stickers (outermost positions)
        // For now, use a simple approach: find extreme points
        var corners = new List<Point>();
        
        if (stickerCenters.Count >= 4)
        {
            // Find 4 extreme corners: top-left, top-right, bottom-left, bottom-right
            var topLeft = stickerCenters.OrderBy(p => p.x + p.y).First();
            var topRight = stickerCenters.OrderBy(p => -p.x + p.y).First();
            var bottomLeft = stickerCenters.OrderBy(p => p.x - p.y).First();
            var bottomRight = stickerCenters.OrderBy(p => -p.x - p.y).First();
            
            corners.Add(topLeft);     // 0: Top-left
            corners.Add(topRight);    // 1: Top-right  
            corners.Add(bottomRight); // 2: Bottom-right
            corners.Add(bottomLeft);  // 3: Bottom-left
        }

        Debug.Log($"[ExtractCubeCorners2D] Extracted {corners.Count} corners from {stickerCenters.Count} stickers");
        return corners;
    }

    private Pose3D EstimateCubePose3D(List<MatOfPoint> squareContours)
    {
        var invalidPose = new Pose3D { isValid = false, confidence = 0f };

        if (!use3DTracking)
        {
            return invalidPose;
        }

        // Extract 2D corners from detected stickers
        List<Point> corners2D = ExtractCubeCorners2D(squareContours);
        if (corners2D.Count < 4)
        {
            Debug.LogWarning("[EstimateCubePose3D] Need at least 4 corners for pose estimation");
            return invalidPose;
        }

        try
        {
            // Convert 2D corners to OpenCV format
            MatOfPoint2f imagePoints = new MatOfPoint2f(corners2D.ToArray());
            
            // Use first 4 corners of the front face for pose estimation
            // Corresponds to cubeCorners3D indices: 0,1,2,3 (front face)
            MatOfPoint3f objectPoints = new MatOfPoint3f(
                cubeCorners3D[0], cubeCorners3D[1], cubeCorners3D[2], cubeCorners3D[3]
            );

            // Output vectors for pose
            Mat rvec = new Mat(); // Rotation vector (Rodrigues representation)
            Mat tvec = new Mat(); // Translation vector

            // Solve PnP to get pose estimation
            bool success = Calib3d.solvePnP(objectPoints, imagePoints, cameraMatrix, (MatOfDouble)distCoeffs, rvec, tvec);

            if (!success)
            {
                Debug.LogWarning("[EstimateCubePose3D] solvePnP failed");
                imagePoints.Dispose();
                objectPoints.Dispose();
                rvec.Dispose();
                tvec.Dispose();
                return invalidPose;
            }

            // Extract translation vector (position in camera space)
            double[] tvecArray = new double[3];
            tvec.get(0, 0, tvecArray);
            Vector3 cameraPose = new Vector3((float)tvecArray[0], (float)tvecArray[1], (float)tvecArray[2]);

            // Extract rotation vector and convert to Euler angles
            double[] rvecArray = new double[3];
            rvec.get(0, 0, rvecArray);
            Vector3 rotationVector = new Vector3((float)rvecArray[0], (float)rvecArray[1], (float)rvecArray[2]);

            // Convert rotation vector to Euler angles (simplified conversion)
            float rotMagnitude = rotationVector.magnitude;
            Vector3 eulerAngles = Vector3.zero;
            if (rotMagnitude > 0.001f)
            {
                Vector3 rotAxis = rotationVector.normalized;
                float angleDegrees = rotMagnitude * Mathf.Rad2Deg;
                
                // Convert Rodrigues rotation to Euler (simplified - just use magnitude as rotation around dominant axis)
                eulerAngles = rotAxis * angleDegrees;
            }

            // Calculate confidence based on how reasonable the pose is
            float distance = cameraPose.magnitude;
            float confidence = 1.0f;
            
            // Reduce confidence for unreasonable distances
            if (distance < 0.1f || distance > 3.0f) confidence *= 0.5f;
            
            // Reduce confidence for extreme rotations
            if (eulerAngles.magnitude > 180f) confidence *= 0.3f;

            var pose = new Pose3D
            {
                position = cameraPose,
                rotation = eulerAngles,
                isValid = confidence >= minPoseConfidence,
                confidence = confidence
            };

            Debug.Log($"[EstimateCubePose3D] Pose: pos=({cameraPose.x:F3}, {cameraPose.y:F3}, {cameraPose.z:F3}), " +
                     $"rot=({eulerAngles.x:F1}, {eulerAngles.y:F1}, {eulerAngles.z:F1}), confidence={confidence:F2}");

            // Clean up OpenCV objects
            imagePoints.Dispose();
            objectPoints.Dispose();
            rvec.Dispose();
            tvec.Dispose();

            return pose;
        }
        catch (Exception ex)
        {
            Debug.LogError($"[EstimateCubePose3D] Error during pose estimation: {ex.Message}");
            return invalidPose;
        }
    }

    private Pose3D ApplyPoseSmoothing(Pose3D newPose)
    {
        if (!newPose.isValid)
        {
            return newPose;
        }

        if (!hasPreviousPose)
        {
            // First valid pose - no smoothing needed
            lastValidPose = newPose;
            hasPreviousPose = true;
            return newPose;
        }

        // Apply temporal smoothing using lerp
        float smoothFactor = 1f - poseSmoothing; // Convert to immediate response factor
        
        Vector3 smoothedPosition = Vector3.Lerp(lastValidPose.position, newPose.position, smoothFactor);
        Vector3 smoothedRotation = Vector3.Lerp(lastValidPose.rotation, newPose.rotation, smoothFactor);
        
        var smoothedPose = new Pose3D
        {
            position = smoothedPosition,
            rotation = smoothedRotation,
            isValid = true,
            confidence = Mathf.Lerp(lastValidPose.confidence, newPose.confidence, smoothFactor)
        };

        lastValidPose = smoothedPose;
        return smoothedPose;
    }

    private Vector3 ConvertCameraPoseToWorldSpace(Vector3 cameraPose, Camera camera)
    {
        // Convert from camera space to world space
        // Camera space: X-right, Y-up, Z-forward (into screen)
        // World space: depends on camera orientation
        
        // Transform the position from camera space to world space
        Vector3 worldPosition = camera.transform.TransformPoint(cameraPose);
        
        return worldPosition;
    }

    private Quaternion ConvertCameraRotationToWorldSpace(Vector3 cameraEulerAngles, Camera camera)
    {
        // Convert camera space rotation to world space rotation
        Quaternion cameraRotation = Quaternion.Euler(cameraEulerAngles);
        Quaternion worldRotation = camera.transform.rotation * cameraRotation;
        
        return worldRotation;
    }

    private (Vector3 position, Quaternion rotation) Get2DTrackingPose(List<MatOfPoint> squareContours, Camera camera)
    {
        // Original 2D+depth tracking method (fallback)
        OrientedBoundingBox orientedBox = GetOrientedBoundingBox(squareContours);
        
        Debug.Log($"[Get2DTrackingPose] Center in 480x640 space: ({orientedBox.center.x:F1}, {orientedBox.center.y:F1}), Angle: {orientedBox.angle:F1}°");

        // Transform center to screen space
        Vector2 screenCenter = TransformCenterToScreenSpace(orientedBox.center);
        
        Debug.Log($"[Get2DTrackingPose] Screen center: ({screenCenter.x:F1}, {screenCenter.y:F1})");

        // Convert 2D screen center to 3D world position
        float anchorDepth = 1f; // 1 meter in front of camera for better AR visibility
        Vector3 screenPoint = new Vector3(screenCenter.x, screenCenter.y, anchorDepth);
        Vector3 worldCenter = camera.ScreenToWorldPoint(screenPoint);

        // Convert OpenCV angle to Unity rotation around camera's forward axis (Z-axis)
        float unityAngle = -orientedBox.angle; // Invert for Unity coordinate system
        Quaternion cubeRotation = Quaternion.AngleAxis(unityAngle, camera.transform.forward);
        
        Debug.Log($"[Get2DTrackingPose] World center: {worldCenter}, OpenCV angle: {orientedBox.angle:F1}°, Unity angle: {unityAngle:F1}°");

        return (worldCenter, cubeRotation);
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

    private void CreatePersistentAnchor(Vector3 position, Quaternion rotation)
    {
        if (centerAnchorPrefab == null)
        {
            Debug.LogWarning("[CreatePersistentAnchor] centerAnchorPrefab is not assigned! Please drag a cube prefab to centerAnchorPrefab field");
            return;
        }

        // Create the anchor once at the initial position
        centerAnchor = Instantiate(centerAnchorPrefab, position, rotation);
        
        // Set up initial properties that won't change
        centerAnchor.transform.localScale = Vector3.one * 0.08f; // 8cm cube for center anchor
        
        // Color it blue to distinguish from other objects
        Renderer renderer = centerAnchor.GetComponent<Renderer>();
        if (renderer != null)
        {
            renderer.material.color = Color.blue;
        }
        
        // Create animated arrow as child of the anchor
        CreateArrowChild();
        
        Debug.Log($"[CreatePersistentAnchor] Created persistent anchor at {position} with rotation {rotation}");
    }

    private void UpdateAnchorTransform(Vector3 position, Quaternion rotation)
    {
        if (centerAnchor == null)
        {
            Debug.LogWarning("[UpdateAnchorTransform] Anchor is null, cannot update transform");
            return;
        }
        
        // Update position and rotation smoothly
        centerAnchor.transform.position = position;
        centerAnchor.transform.rotation = rotation;
        
        // Ensure anchor is visible
        if (!centerAnchor.activeInHierarchy)
        {
            centerAnchor.SetActive(true);
        }
        
        // Debug.Log($"[UpdateAnchorTransform] Updated anchor to position {position}, rotation {rotation}");
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

    private void CreateArrowChild()
    {
        if (animatedArrowPrefab == null)
        {
            Debug.LogWarning("[CreateArrowChild] No animated arrow prefab assigned! Please drag an arrow prefab to the animatedArrowPrefab field.");
            return;
        }

        if (centerAnchor == null)
        {
            Debug.LogWarning("[CreateArrowChild] No center anchor to attach arrow to.");
            return;
        }

        // Clear any existing direction arrow
        if (directionArrow != null)
        {
            Destroy(directionArrow);
            directionArrow = null;
        }

        // Create the arrow at the anchor's position (will be offset via local position)
        directionArrow = Instantiate(animatedArrowPrefab, centerAnchor.transform.position, centerAnchor.transform.rotation);
        
        // Set as child of the anchor
        directionArrow.transform.SetParent(centerAnchor.transform);
        
        // Position the arrow 28cm above the anchor in world space (Y-axis)
        directionArrow.transform.localPosition = Vector3.up * 0.28f; // 28cm = 0.28m
        
        // Scale the arrow for better visibility
        directionArrow.transform.localScale = Vector3.one * 0.2f;
        
        Debug.Log("[CreateArrowChild] Created animated arrow as child of anchor, offset 28cm upwards");
    }

    private void ClearCenterAnchor()
    {
        if (centerAnchor != null)
        {
            Destroy(centerAnchor);
            centerAnchor = null;
            Debug.Log("[ClearCenterAnchor] Destroyed persistent center anchor");
        }
        
        if (directionArrow != null)
        {
            Destroy(directionArrow);
            directionArrow = null;
            Debug.Log("[ClearCenterAnchor] Destroyed direction arrow");
        }
    }

    public void ResetTracking()
    {
        Debug.Log("[ResetTracking] Manually resetting cube tracking - destroying persistent anchors");
        ClearCenterAnchor();
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