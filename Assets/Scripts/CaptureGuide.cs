using System;
using System.Collections.Generic;
using System.Linq;
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

    [Header("Billboard Rotation")]
    public bool lockOrientationToCamera = true; // Enable/disable billboard behavior
    public bool useCameraUp = true; // true = follow device roll; false = keep world-upright (Y up)
    [Range(0f, 0.5f)]
    public float rotationSmoothing = 0.1f; // 0=no smoothing, 0.1–0.2 recommended
    public Quaternion modelForwardAdjustment = Quaternion.identity; // set once if the prefab's "front" isn't +Z

    [Header("Twist Tracking")]
    public bool enableTwistTracking = true; // Enable/disable cube roll detection
    public bool invertRoll = false; // Flip sign if your observed direction is reversed
    [Range(0f, 0.5f)]
    public float rollSmoothing = 0.15f; // EMA on roll; 0=no smoothing
    [Range(10f, 90f)]
    public float maxRollJumpPerFrame = 45f; // Clamp sudden roll changes (degrees)
    [Range(1, 3)]
    public int minRowsForRoll = 1; // Require at least this many valid rows (1–3)

    // Twist tracking state
    private float rollDegSmoothed = 0f;
    private bool hasRoll = false;
    private float lastRollTime = 0f;

    [Header("Coast Window")]
    [Range(0.1f, 1.0f)]
    public float coastDuration = 0.35f; // Hold last good pose for 350ms when detection drops

    // Coast window tracking state
    private float lastSeenTime;
    private bool hasLock = false;
    private Vector3 lastWorldPos;
    private Quaternion lastWorldRot;

    [Header("Depth Estimation")]
    [Range(40f, 80f)]
    public float cubeSize = 57f; // Standard Rubik's cube size in millimeters
    [Range(0.1f, 0.9f)]
    public float depthSmoothing = 0.7f; // Exponential moving average for depth
    [Range(0.1f, 0.5f)]
    public float maxDepthVariance = 0.2f; // Maximum allowed gap variance (20%)

    // Camera intrinsics (transformed to 480×640 processed space)
    private float fx, fy, cx, cy;
    private bool intrinsicsInitialized = false;

    // Depth estimation state
    private float smoothedDepth = 1.0f; // Start with 1m default
    private bool hasValidDepth = false;

    // CPU image dimensions for proper scaling calculation
    private int cpuImageWidth = 0;
    private int cpuImageHeight = 0;


    void Start()
    {
        // Initialize reusable processor for performance
        processor = new CubeProcessor();

        // Initialize camera intrinsics for depth estimation
        InitializeCameraIntrinsics();

        if (hintText != null)
            hintText.text = "Point camera at cube face";
    }

    private void InitializeCameraIntrinsics()
    {
        if (arCameraManager == null)
        {
            Debug.LogWarning("[InitializeCameraIntrinsics] ARCameraManager is null, using fallback intrinsics");
            SetFallbackIntrinsics();
            return;
        }

        // Try to get camera intrinsics from AR Foundation
        if (arCameraManager.TryGetIntrinsics(out XRCameraIntrinsics intrinsics))
        {
            Debug.Log($"[InitializeCameraIntrinsics] Got AR intrinsics: fx={intrinsics.focalLength.x:F1}, fy={intrinsics.focalLength.y:F1}, " +
                     $"cx={intrinsics.principalPoint.x:F1}, cy={intrinsics.principalPoint.y:F1}, " +
                     $"resolution={intrinsics.resolution.x}x{intrinsics.resolution.y}");

            // Transform intrinsics from native space to our 480×640 processed space
            TransformIntrinsicsToProcessedSpace(intrinsics);
        }
        else
        {
            Debug.LogWarning("[InitializeCameraIntrinsics] Could not get AR intrinsics, using fallback");
            SetFallbackIntrinsics();
        }
    }

    private void TransformIntrinsicsToProcessedSpace(XRCameraIntrinsics nativeIntrinsics)
    {
        // Native camera parameters
        float nativeFx = nativeIntrinsics.focalLength.x;
        float nativeFy = nativeIntrinsics.focalLength.y;
        float nativeCx = nativeIntrinsics.principalPoint.x;
        float nativeCy = nativeIntrinsics.principalPoint.y;
        int nativeWidth = nativeIntrinsics.resolution.x;
        int nativeHeight = nativeIntrinsics.resolution.y;

        // Our processing pipeline:
        // 1. Native camera image → CPU image (same resolution)
        // 2. Apply MirrorX transformation  
        // 3. Rotate 90° CW
        // 4. Resize to 480×640

        // Step 1: Handle MirrorX (flip horizontally)
        // fx stays same, fy stays same
        // cx becomes (nativeWidth - cx), cy stays same
        float mirroredCx = nativeWidth - nativeCx;

        // Step 2: Handle 90° CW rotation
        // After rotation: width becomes height, height becomes width
        // New coordinates: x' = y, y' = width - x
        // So: fx' = fy, fy' = fx, cx' = cy, cy' = nativeWidth - mirroredCx
        float rotatedFx = nativeFy;
        float rotatedFy = nativeFx;
        float rotatedCx = nativeCy;
        float rotatedCy = nativeWidth - mirroredCx;
        int rotatedWidth = nativeHeight;  // After rotation
        int rotatedHeight = nativeWidth;

        // Step 3: Scale to 480×640 processing resolution
        float scaleX = 480f / rotatedWidth;
        float scaleY = 640f / rotatedHeight;

        // Apply scaling to intrinsics
        fx = rotatedFx * scaleX;
        fy = rotatedFy * scaleY;
        cx = rotatedCx * scaleX;
        cy = rotatedCy * scaleY;

        intrinsicsInitialized = true;

        Debug.Log($"[TransformIntrinsicsToProcessedSpace] Transformed to 480×640 space: " +
                 $"fx={fx:F1}, fy={fy:F1}, cx={cx:F1}, cy={cy:F1}");
        Debug.Log($"[TransformIntrinsicsToProcessedSpace] Transformation: {nativeWidth}×{nativeHeight} → " +
                 $"mirror → rotate → {rotatedWidth}×{rotatedHeight} → scale → 480×640");
    }

    private void SetFallbackIntrinsics()
    {
        // Fallback intrinsics for 480×640 space (typical mobile camera estimates)
        // Typical mobile camera has FOV ~60-70°, focal length should be larger than image size
        fx = 640f; // Focal length should be larger than the smaller dimension  
        fy = 640f; // Keep aspect ratio similar
        cx = 240f; // Center X (half of 480)
        cy = 320f; // Center Y (half of 640)

        intrinsicsInitialized = true;

        Debug.Log($"[SetFallbackIntrinsics] Using fallback intrinsics: fx={fx:F1}, fy={fy:F1}, cx={cx:F1}, cy={cy:F1}");
        Debug.Log($"[SetFallbackIntrinsics] Note: These are estimates for 480×640 processed space");
    }

    private (float dx_px, float dy_px, bool isValid) MeasureGridSpacing(List<MatOfPoint> sortedContours)
    {
        if (sortedContours.Count < 6)
        {
            // Debug.LogWarning($"[MeasureGridSpacing] Need at least 6 stickers for spacing measurement, found {sortedContours.Count}");
            return (0f, 0f, false);
        }

        // Extract sticker centers
        List<Point> centers = new List<Point>();
        foreach (MatOfPoint contour in sortedContours)
        {
            Point[] points = contour.toArray();
            double sumX = 0, sumY = 0;
            foreach (Point pt in points)
            {
                sumX += pt.x;
                sumY += pt.y;
            }
            Point center = new Point(sumX / points.Length, sumY / points.Length);
            centers.Add(center);
        }

        // Debug.Log($"[MeasureGridSpacing] Measuring spacing from {centers.Count} sticker centers");

        // Assume row-major ordering: centers are arranged as:
        // [0] [1] [2]    row 0
        // [3] [4] [5]    row 1  
        // [6] [7] [8]    row 2

        List<float> horizontalGaps = new List<float>();
        List<float> verticalGaps = new List<float>();

        // Measure horizontal gaps (within rows)
        if (centers.Count >= 3)
        {
            // Row 0: gaps between centers 0-1 and 1-2
            horizontalGaps.Add((float)Math.Abs(centers[1].x - centers[0].x));
            if (centers.Count >= 3) horizontalGaps.Add((float)Math.Abs(centers[2].x - centers[1].x));
        }
        if (centers.Count >= 6)
        {
            // Row 1: gaps between centers 3-4 and 4-5
            horizontalGaps.Add((float)Math.Abs(centers[4].x - centers[3].x));
            if (centers.Count >= 6) horizontalGaps.Add((float)Math.Abs(centers[5].x - centers[4].x));
        }
        if (centers.Count >= 9)
        {
            // Row 2: gaps between centers 6-7 and 7-8  
            horizontalGaps.Add((float)Math.Abs(centers[7].x - centers[6].x));
            horizontalGaps.Add((float)Math.Abs(centers[8].x - centers[7].x));
        }

        // Measure vertical gaps (within columns)
        if (centers.Count >= 4)
        {
            // Column 0: gap between centers 0-3
            verticalGaps.Add((float)Math.Abs(centers[3].y - centers[0].y));
            if (centers.Count >= 7) verticalGaps.Add((float)Math.Abs(centers[6].y - centers[3].y)); // 3-6
        }
        if (centers.Count >= 5)
        {
            // Column 1: gap between centers 1-4
            verticalGaps.Add((float)Math.Abs(centers[4].y - centers[1].y));
            if (centers.Count >= 8) verticalGaps.Add((float)Math.Abs(centers[7].y - centers[4].y)); // 4-7
        }
        if (centers.Count >= 6)
        {
            // Column 2: gap between centers 2-5
            verticalGaps.Add((float)Math.Abs(centers[5].y - centers[2].y));
            if (centers.Count >= 9) verticalGaps.Add((float)Math.Abs(centers[8].y - centers[5].y)); // 5-8
        }

        if (horizontalGaps.Count == 0 || verticalGaps.Count == 0)
        {
            Debug.LogWarning("[MeasureGridSpacing] Not enough gaps measured");
            return (0f, 0f, false);
        }

        // Robust averaging: use median filtering
        horizontalGaps.Sort();
        verticalGaps.Sort();

        float medianHorizontal = horizontalGaps[horizontalGaps.Count / 2];
        float medianVertical = verticalGaps[verticalGaps.Count / 2];

        // Filter outliers (>1.6× median)
        List<float> filteredHorizontal = horizontalGaps.Where(gap => gap <= medianHorizontal * 1.6f).ToList();
        List<float> filteredVertical = verticalGaps.Where(gap => gap <= medianVertical * 1.6f).ToList();

        if (filteredHorizontal.Count == 0 || filteredVertical.Count == 0)
        {
            Debug.LogWarning("[MeasureGridSpacing] All gaps filtered out as outliers");
            return (0f, 0f, false);
        }

        // Calculate final averages
        float dx_px = filteredHorizontal.Average();
        float dy_px = filteredVertical.Average();

        // Check variance (stability)
        float hVariance = filteredHorizontal.Count > 1 ?
            filteredHorizontal.Select(x => (x - dx_px) * (x - dx_px)).Average() : 0f;
        float vVariance = filteredVertical.Count > 1 ?
            filteredVertical.Select(x => (x - dy_px) * (x - dy_px)).Average() : 0f;

        float hStdDev = (float)Math.Sqrt(hVariance);
        float vStdDev = (float)Math.Sqrt(vVariance);

        float hCoefVar = dx_px > 0 ? hStdDev / dx_px : 1f; // Coefficient of variation
        float vCoefVar = dy_px > 0 ? vStdDev / dy_px : 1f;

        bool isStable = hCoefVar < maxDepthVariance && vCoefVar < maxDepthVariance;

        Debug.Log($"[MeasureGridSpacing] dx={dx_px:F1}px (cv={hCoefVar:F2}), dy={dy_px:F1}px (cv={vCoefVar:F2}), stable={isStable}");
        Debug.Log($"[MeasureGridSpacing] Raw gaps - H: [{string.Join(",", horizontalGaps.Select(x => x.ToString("F1")))}], " +
                 $"V: [{string.Join(",", verticalGaps.Select(x => x.ToString("F1")))}]");

        return (dx_px, dy_px, isStable);
    }

    private (float depth, bool isValid) EstimateDepthFromGrid(List<MatOfPoint> sortedContours)
    {
        if (!intrinsicsInitialized)
        {
            Debug.LogWarning("[EstimateDepthFromGrid] Camera intrinsics not initialized");
            return (smoothedDepth, false);
        }

        // Debug: Log camera intrinsics and cube size
        Debug.Log($"[EstimateDepthFromGrid] Camera intrinsics: fx={fx:F1}, fy={fy:F1}, cx={cx:F1}, cy={cy:F1}");
        Debug.Log($"[EstimateDepthFromGrid] Cube size from inspector: {cubeSize:F1}mm");

        // Measure grid spacing
        var (dx_px, dy_px, spacingValid) = MeasureGridSpacing(sortedContours);

        if (!spacingValid || dx_px <= 0 || dy_px <= 0)
        {
            Debug.LogWarning("[EstimateDepthFromGrid] Invalid grid spacing measurement");
            return (smoothedDepth, false);
        }

        // Physical gap between adjacent sticker centers (convert mm to meters)
        float physicalGapMm = cubeSize / 3f; // Gap in millimeters
        float physicalGapMeters = physicalGapMm / 1000f; // Convert to meters for depth calculation
        Debug.Log($"[EstimateDepthFromGrid] Physical gap: {cubeSize:F1}mm / 3 = {physicalGapMm:F1}mm = {physicalGapMeters:F6}m");

        // Depth estimates from horizontal and vertical spacing
        float Zx = fx * physicalGapMeters / dx_px;
        float Zy = fy * physicalGapMeters / dy_px;

        Debug.Log($"[EstimateDepthFromGrid] Depth calculation: fx={fx:F1} * {physicalGapMeters:F6}m / {dx_px:F1}px = {Zx:F3}m");
        Debug.Log($"[EstimateDepthFromGrid] Depth calculation: fy={fy:F1} * {physicalGapMeters:F6}m / {dy_px:F1}px = {Zy:F3}m");

        // Combine depth estimates
        float estimatedDepth;
        if (Zx > 0 && Zy > 0)
        {
            estimatedDepth = 0.5f * (Zx + Zy); // Average both estimates
            Debug.Log($"[EstimateDepthFromGrid] Combined depth estimate: Z={(estimatedDepth):F3}m");
        }
        else if (Zx > 0)
        {
            estimatedDepth = Zx;
            Debug.Log($"[EstimateDepthFromGrid] Using horizontal depth estimate: Z={estimatedDepth:F3}m");
        }
        else if (Zy > 0)
        {
            estimatedDepth = Zy;
            Debug.Log($"[EstimateDepthFromGrid] Using vertical depth estimate: Z={estimatedDepth:F3}m");
        }
        else
        {
            Debug.LogWarning("[EstimateDepthFromGrid] Both depth estimates invalid");
            return (smoothedDepth, false);
        }

        // Sanity check: reasonable depth range
        bool depthValid = estimatedDepth >= 0.15f && estimatedDepth <= 3.0f;

        if (!depthValid)
        {
            Debug.LogWarning($"[EstimateDepthFromGrid] Depth {estimatedDepth:F3}m outside valid range [0.15, 3.0]m");
            return (smoothedDepth, false);
        }

        Debug.Log($"[EstimateDepthFromGrid] Valid depth estimate: {estimatedDepth:F3}m");
        return (estimatedDepth, true);
    }

    private float ApplyDepthSmoothing(float newDepth)
    {
        if (!hasValidDepth)
        {
            // First valid depth - no smoothing needed
            smoothedDepth = newDepth;
            hasValidDepth = true;
            Debug.Log($"[ApplyDepthSmoothing] First valid depth: {smoothedDepth:F3}m");
            return smoothedDepth;
        }

        // Apply exponential moving average (EMA)
        float prevSmoothed = smoothedDepth;
        smoothedDepth = smoothedDepth * depthSmoothing + newDepth * (1f - depthSmoothing);

        Debug.Log($"[ApplyDepthSmoothing] Smoothed depth: {prevSmoothed:F3}m → {smoothedDepth:F3}m (raw: {newDepth:F3}m)");
        return smoothedDepth;
    }

    private Quaternion ComputeBillboardRotation(Vector3 worldPos, Camera cam)
    {
        // If billboard rotation is disabled, return current rotation or identity
        if (!lockOrientationToCamera)
        {
            return centerAnchor != null ? centerAnchor.transform.rotation : lastWorldRot;
        }

        // Safety check for camera
        if (cam == null)
        {
            Debug.LogWarning("[ComputeBillboardRotation] Camera is null, using current rotation");
            return centerAnchor != null ? centerAnchor.transform.rotation : Quaternion.identity;
        }

        // Compute direction from world position to camera
        Vector3 dir = (cam.transform.position - worldPos).normalized;

        // Safety check for zero distance
        if (dir.magnitude < 0.001f)
        {
            Debug.LogWarning("[ComputeBillboardRotation] Camera too close to anchor, using current rotation");
            return centerAnchor != null ? centerAnchor.transform.rotation : Quaternion.identity;
        }

        // Choose up vector based on useCameraUp setting
        Vector3 up = useCameraUp ? cam.transform.up : Vector3.up;

        // Compute target rotation to face the camera
        Quaternion targetRot = Quaternion.LookRotation(dir, up) * modelForwardAdjustment;

        // Apply smoothing if enabled
        if (rotationSmoothing > 0f && centerAnchor != null)
        {
            Quaternion currentRotation = centerAnchor.transform.rotation;
            return Quaternion.Slerp(currentRotation, targetRot, 1f - rotationSmoothing);
        }
        else
        {
            return targetRot;
        }
    }

    private bool TryComputeImageRowDirection(List<MatOfPoint> sortedContours, out Vector2 v_img)
    {
        v_img = Vector2.zero;

        if (sortedContours.Count < 3)
        {
            return false; // Need at least 3 stickers for any row analysis
        }

        // Get sticker centers using existing CubeProcessor method
        List<Vector2> centers = new List<Vector2>();
        foreach (MatOfPoint contour in sortedContours)
        {
            Point center = CubeProcessor.ContourCenter(contour);
            centers.Add(new Vector2((float)center.x, (float)center.y));
        }

        List<Vector2> rowDirs = new List<Vector2>();

        // Analyze each potential row (0-2, 3-5, 6-8)
        for (int row = 0; row < 3; row++)
        {
            int startIdx = row * 3;
            int endIdx = startIdx + 2; // Index of rightmost sticker in row

            // Check if we have enough stickers for this row
            if (endIdx >= centers.Count)
            {
                // If we don't have the full row, try using what we have
                if (startIdx + 1 < centers.Count)
                {
                    endIdx = startIdx + 1; // Use 2-point row direction
                }
                else
                {
                    continue; // Skip this row entirely
                }
            }

            // Compute row direction vector (left to right)
            Vector2 v_row = centers[endIdx] - centers[startIdx];

            // Skip very small vectors (degenerate rows)
            if (v_row.magnitude < 5f) // Minimum 5 pixels between stickers
            {
                continue;
            }

            // Normalize and add to list
            rowDirs.Add(v_row.normalized);
        }

        // Check if we have enough valid rows
        if (rowDirs.Count < minRowsForRoll)
        {
            return false;
        }

        // Outlier rejection by angle
        List<float> angles = new List<float>();
        foreach (Vector2 dir in rowDirs)
        {
            angles.Add(Mathf.Atan2(dir.y, dir.x) * Mathf.Rad2Deg);
        }

        // Compute circular median angle
        angles.Sort();
        float medianAngle = angles[angles.Count / 2];

        // Filter outliers (more than 25° from median)
        List<Vector2> filteredDirs = new List<Vector2>();
        for (int i = 0; i < rowDirs.Count; i++)
        {
            float angleDiff = Mathf.Abs(Mathf.DeltaAngle(angles[i], medianAngle));
            if (angleDiff <= 25f)
            {
                filteredDirs.Add(rowDirs[i]);
            }
        }

        if (filteredDirs.Count == 0)
        {
            return false; // All directions were outliers
        }

        // Average remaining unit vectors and renormalize
        Vector2 avgDir = Vector2.zero;
        foreach (Vector2 dir in filteredDirs)
        {
            avgDir += dir;
        }

        v_img = avgDir.normalized;
        return true;
    }

    private bool TryGetRollFromCentersDeg(List<MatOfPoint> sortedContours, out float rollDeg)
    {
        rollDeg = 0f;

        // Get robust row direction from image analysis
        if (!TryComputeImageRowDirection(sortedContours, out Vector2 v_img))
        {
            return false;
        }

        // Convert direction vector to angle in degrees
        rollDeg = Mathf.Atan2(v_img.y, v_img.x) * Mathf.Rad2Deg;

        // Apply invert flag if needed
        if (invertRoll)
        {
            rollDeg = -rollDeg;
        }

        // Normalize to (-180, 180] range
        while (rollDeg > 180f) rollDeg -= 360f;
        while (rollDeg <= -180f) rollDeg += 360f;

        return true;
    }

    void Update()
    {
        // FPS tracking for performance monitoring
        fpsFrameCount++;
        if (Time.time - fpsLastTime >= 1.0f)
        {
            float fps = fpsFrameCount / (Time.time - fpsLastTime);
            // Debug.Log($"[CaptureGuide] Overall FPS: {fps:F1}");
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
                // Debug.Log($"[ProcessCurrentFrame] CPU Image size: {cpuImage.width}×{cpuImage.height}");
                // Debug.Log($"[ProcessCurrentFrame] Screen size: {Screen.width}×{Screen.height}");
                // Debug.Log($"[ProcessCurrentFrame] Texture size: {frameTexture.width}×{frameTexture.height}");
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

        if (processor.SortedContours.Count >= 6 && processor.SortedContours.Count <= 9)
        {
            UpdateTrackingStatus($"Found {processor.SortedContours.Count} stickers ({processingStopwatch.ElapsedMilliseconds}ms)", true);

            // Draw oriented boundary using sticker contours
            DrawCubeBoundary(processor, true);
        }
        else
        {
            UpdateTrackingStatus($"Found {processor.SortedContours.Count} stickers ({processingStopwatch.ElapsedMilliseconds}ms)", false);

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
        // Debug.Log($"[DrawCubeBoundary] Called with {cubeProcessor.SortedContours.Count} contours, show={show}");

        if (show)
        {
            // Good frame (6-9 stickers detected)
            HandleGoodFrame(cubeProcessor);
        }
        else
        {
            // Bad frame (<6 or >9 stickers, or processing error)
            HandleBadFrame();
        }
    }

    private void HandleGoodFrame(CubeProcessor cubeProcessor)
    {
        if (centerAnchorPrefab == null)
        {
            Debug.LogWarning("[HandleGoodFrame] centerAnchorPrefab is not assigned! Please drag a cube prefab to centerAnchorPrefab field");
            return;
        }

        if (cubeProcessor.SortedContours.Count == 0)
        {
            Debug.LogWarning("[HandleGoodFrame] No contours available for center calculation");
            return;
        }

        try
        {
            // Get camera reference for coordinate conversion
            Camera camera = Camera.main ?? arCameraManager.GetComponent<Camera>();
            if (camera == null)
            {
                Debug.LogWarning("[HandleGoodFrame] No camera found for coordinate conversion");
                return;
            }

            // Compute world center with depth estimation  
            var (worldCenter, depthValid) = Get2DCentroidPositionWithDepth(cubeProcessor.SortedContours, camera);

            // Compute billboard rotation to face camera
            Quaternion billboardRot = ComputeBillboardRotation(worldCenter, camera);

            // Apply twist tracking if enabled
            Quaternion cubeRotation = billboardRot;
            if (enableTwistTracking && TryGetRollFromCentersDeg(cubeProcessor.SortedContours, out float rollDeg))
            {
                // Unwrap against last value to avoid 180° jumps
                if (hasRoll)
                {
                    float unwrappedRoll = rollDeg;
                    float diff = rollDeg - rollDegSmoothed;

                    // Handle wraparound
                    if (diff > 180f) unwrappedRoll -= 360f;
                    else if (diff < -180f) unwrappedRoll += 360f;

                    rollDeg = unwrappedRoll;
                }

                // Clamp per-frame delta to prevent sudden jumps
                if (hasRoll)
                {
                    float maxDelta = maxRollJumpPerFrame;
                    float delta = rollDeg - rollDegSmoothed;
                    delta = Mathf.Clamp(delta, -maxDelta, maxDelta);
                    rollDeg = rollDegSmoothed + delta;
                }

                // Apply EMA smoothing
                if (!hasRoll)
                {
                    rollDegSmoothed = rollDeg;
                    hasRoll = true;
                }
                else
                {
                    rollDegSmoothed = Mathf.Lerp(rollDegSmoothed, rollDeg, 1f - rollSmoothing);
                }

                lastRollTime = Time.time;

                // Apply twist about the view axis
                Vector3 viewAxis = (camera.transform.position - worldCenter).normalized;
                if (viewAxis.sqrMagnitude > 0.001f) // Safety check for zero distance
                {
                    Quaternion twist = Quaternion.AngleAxis(rollDegSmoothed, viewAxis);
                    cubeRotation = twist * billboardRot;

                    Debug.Log($"[HandleGoodFrame] Roll: raw={rollDeg:F1}°, smoothed={rollDegSmoothed:F1}°, applied twist");
                }
                else
                {
                    Debug.LogWarning("[HandleGoodFrame] View axis too small, skipping twist");
                }
            }
            else if (enableTwistTracking)
            {
                // Keep previous rollDegSmoothed (don't zero it mid-session)
                Debug.Log("[HandleGoodFrame] Twist tracking enabled but roll detection failed");
            }

            // If depth estimation fails, treat as bad frame (will trigger coast window)
            if (!depthValid && hasValidDepth)
            {
                Debug.LogWarning("[HandleGoodFrame] Depth estimation failed, treating as bad frame");
                HandleBadFrame();
                return;
            }

            // Store last good pose for coast window
            lastWorldPos = worldCenter;
            lastWorldRot = cubeRotation;
            lastSeenTime = Time.time;
            hasLock = true;

            // Ensure anchor active and update its transform
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

            Debug.Log($"[HandleGoodFrame] Updated pose: pos=({worldCenter.x:F3}, {worldCenter.y:F3}, {worldCenter.z:F3}), hasLock=true");
        }
        catch (System.Exception ex)
        {
            Debug.LogError($"[HandleGoodFrame] Error updating center anchor: {ex.Message}");
        }
    }

    private void HandleBadFrame()
    {
        if (hasLock && Time.time - lastSeenTime < coastDuration)
        {
            // Within coast window - keep anchor active with last good pose
            if (centerAnchor != null)
            {
                centerAnchor.SetActive(true);
                centerAnchor.transform.position = lastWorldPos;

                // Handle rotation during coast window
                Quaternion cubeRotation;
                if (lockOrientationToCamera)
                {
                    // Recompute billboard rotation each frame to continue facing camera
                    Camera camera = Camera.main ?? arCameraManager.GetComponent<Camera>();
                    Quaternion billboardRot = ComputeBillboardRotation(lastWorldPos, camera);

                    // Apply stored twist if twist tracking is enabled
                    if (enableTwistTracking && hasRoll)
                    {
                        Vector3 viewAxis = (camera.transform.position - lastWorldPos).normalized;
                        if (viewAxis.sqrMagnitude > 0.001f)
                        {
                            Quaternion twist = Quaternion.AngleAxis(rollDegSmoothed, viewAxis);
                            cubeRotation = twist * billboardRot;
                        }
                        else
                        {
                            cubeRotation = billboardRot;
                        }
                    }
                    else
                    {
                        cubeRotation = billboardRot;
                    }
                }
                else
                {
                    // Use stored rotation (no billboard behavior)
                    cubeRotation = lastWorldRot;
                }

                centerAnchor.transform.rotation = cubeRotation;

                // Debug.Log($"[HandleBadFrame] Coasting with last pose: pos=({lastWorldPos.x:F3}, {lastWorldPos.y:F3}, {lastWorldPos.z:F3}), time_remaining={(coastDuration - (Time.time - lastSeenTime)):F2}s");
            }
        }
        else
        {
            // Coast window expired or no lock - hide anchor and reset lock
            if (centerAnchor != null)
            {
                centerAnchor.SetActive(false);
                // Debug.Log("[HandleBadFrame] Coast window expired, hiding anchor");
            }

            // Reset hasLock only when coast window expires and anchor hidden
            if (hasLock)
            {
                hasLock = false;
                Debug.Log("[HandleBadFrame] Reset hasLock=false");
            }
        }
    }

    private Point CalculateStickerCentroid(List<MatOfPoint> sortedContours)
    {
        if (sortedContours.Count == 0)
        {
            Debug.LogWarning("[CalculateStickerCentroid] No contours provided, using center fallback");
            return new Point(240, 320); // Center of 480×640 processing resolution
        }

        // Calculate centroid of all sticker centers
        double totalX = 0, totalY = 0;
        int totalStickers = 0;

        foreach (MatOfPoint contour in sortedContours)
        {
            // Calculate centroid of each sticker
            Point[] points = contour.toArray();
            double stickerX = 0, stickerY = 0;

            foreach (Point pt in points)
            {
                stickerX += pt.x;
                stickerY += pt.y;
            }

            // Add this sticker's center to the overall centroid calculation
            totalX += stickerX / points.Length;
            totalY += stickerY / points.Length;
            totalStickers++;
        }

        // Calculate final centroid
        Point centroid = new Point(totalX / totalStickers, totalY / totalStickers);

        // Debug.Log($"[CalculateStickerCentroid] Calculated centroid from {totalStickers} stickers: ({centroid.x:F1}, {centroid.y:F1})");

        return centroid;
    }

    private (Vector3 worldCenter, bool depthValid) Get2DCentroidPositionWithDepth(List<MatOfPoint> sortedContours, Camera camera)
    {
        // Calculate centroid of all detected stickers
        Point centroid = CalculateStickerCentroid(sortedContours);

        // Debug.Log($"[Get2DCentroidPosition] Centroid in 480x640 space: ({centroid.x:F1}, {centroid.y:F1})");

        // Transform centroid to screen space
        Vector2 screenCenter = TransformCenterToScreenSpace(centroid);

        // Debug.Log($"[Get2DCentroidPosition] Screen center: ({screenCenter.x:F1}, {screenCenter.y:F1})");

        // Estimate depth from grid spacing
        var (estimatedDepth, depthValid) = EstimateDepthFromGrid(sortedContours);
        float anchorDepth;

        if (depthValid)
        {
            // Use estimated depth with smoothing
            anchorDepth = ApplyDepthSmoothing(estimatedDepth);
            Debug.Log($"[Get2DCentroidPosition] Using estimated depth: {anchorDepth:F3}m");
        }
        else
        {
            // Fallback to smoothed depth or default
            anchorDepth = hasValidDepth ? smoothedDepth : 1.0f;
            Debug.Log($"[Get2DCentroidPosition] Using fallback depth: {anchorDepth:F3}m (depthValid={depthValid}, hasValidDepth={hasValidDepth})");
        }

        // Convert 2D screen center to 3D world position
        Vector3 screenPoint = new Vector3(screenCenter.x, screenCenter.y, anchorDepth);
        Vector3 worldCenter = camera.ScreenToWorldPoint(screenPoint);

        Debug.Log($"[Get2DCentroidPosition] World center: {worldCenter} (depth: {anchorDepth:F3}m)");

        return (worldCenter, depthValid);
    }

    private Vector2 TransformCenterToScreenSpace(Point center)
    {
        // Debug.Log($"[TransformCenter] CPU Image size: {cpuImageWidth} x {cpuImageHeight}");
        // Debug.Log($"[TransformCenter] Screen size: {Screen.width} x {Screen.height}");
        // Debug.Log($"[TransformCenter] Center in 480x640 space: ({center.x:F1}, {center.y:F1})");

        // Calculate scale factors from processing resolution (480×640) to CPU image resolution  
        float scaleX = (float)cpuImageWidth / 480f;
        float scaleY = (float)cpuImageHeight / 640f;

        // Debug.Log($"[TransformCenter] Scale factors (CPU based): X={scaleX:F2}, Y={scaleY:F2}");

        // Scale from 480×640 to CPU image resolution
        float scaledX = (float)center.x * scaleX;
        float scaledY = (float)center.y * scaleY;

        // Now convert from CPU image coordinates to screen coordinates
        float cpuToScreenScaleX = (float)Screen.width / cpuImageWidth;
        float cpuToScreenScaleY = (float)Screen.height / cpuImageHeight;

        // Debug.Log($"[TransformCenter] CPU to Screen scale factors: X={cpuToScreenScaleX:F2}, Y={cpuToScreenScaleY:F2}");

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

        // Debug.Log($"[TransformCenter] Final screen center: ({unityX:F1}, {unityY:F1})");

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
        directionArrow.transform.localPosition = Vector3.up * 0.35f; // 28cm = 0.28m
        // directionArrow.transform.localPosition = Vector3.forward * 0.1f;

        // Scale the arrow for better visibility
        directionArrow.transform.localScale = new Vector3(0.12f, 0.06f, 0.1f);

        // Debug.Log("[CreateArrowChild] Created animated arrow as child of anchor, offset 28cm upwards");
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
