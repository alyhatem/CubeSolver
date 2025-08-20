// CubeFaceProcessor.cs
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEngine;
using OpenCVForUnity.CoreModule;
using OpenCVForUnity.ImgcodecsModule;
using OpenCVForUnity.ImgprocModule;

public class CubeProcessor : IDisposable
{
    public readonly string ImagePath; // Nullable for Mat-based construction
    public Mat Image;                 // original BGR
    public Mat Resized;               // 480×640 BGR
    public readonly List<MatOfPoint> SquareContours = new();
    public readonly List<MatOfPoint> RejectedContours = new();
    public readonly List<MatOfPoint> RecoveredContours = new();  // Contours recovered during processing
    public readonly List<Vector3> MeanLabValues = new();  // LAB color values for each sticker
    public Vector4 Boundary;          // (minX, minY, maxX, maxY) cube boundary
    
    // Adaptive threshold parameters for scale-independent detection
    public float MinAreaPercent = 0.0008f;  // Minimum sticker area as % of image (0.08%)
    public float MaxAreaPercent = 0.08f;    // Maximum sticker area as % of image (8%)

    private static readonly string[] FaceKeys = { "U", "R", "F", "D", "L", "B" };

    /// Reads face_U.jpg … face_B.jpg from Application.persistentDataPath.
    public static Dictionary<string, Mat> LoadFaces()
    {
        var faces = new Dictionary<string, Mat>();
        string basePath = Application.persistentDataPath;

        foreach (string key in FaceKeys)
        {
            string path = Path.Combine(basePath, $"face_{key}.jpg");
            if (!File.Exists(path)) continue;

            Mat img = Imgcodecs.imread(path, Imgcodecs.IMREAD_COLOR);
            if (!img.empty()) faces[key] = img;
        }

        return faces;    // expect Count == 6 after all captures
    }

    // Parameterless constructor for reusable instances
    public CubeProcessor()
    {
        ImagePath = null;
        Image = null;
        Resized = new Mat();
        // Don't initialize until UpdateInputMat is called
    }

    public CubeProcessor(string imagePath)
    {
        ImagePath = imagePath;
        Image = Imgcodecs.imread(ImagePath, Imgcodecs.IMREAD_COLOR);
        Resized = new Mat();
        Core.rotate(Image, Image, Core.ROTATE_90_CLOCKWISE);
        Imgproc.resize(Image, Resized, new Size(480, 640), 0, 0, Imgproc.INTER_AREA);
        Image.Dispose();
    }

    public CubeProcessor(Mat inputMat)
    {
        ImagePath = null; // No file path for Mat-based construction
        Core.rotate(inputMat, inputMat, Core.ROTATE_90_CLOCKWISE);
        Image = inputMat; // Store original Mat
        Resized = new Mat();
        Imgproc.resize(Image, Resized, new Size(480, 640), 0, 0, Imgproc.INTER_AREA);
        // Resized = Image.clone();
        Image.Dispose();
    }
    
    // Update reusable instance with new input Mat
    public void UpdateInputMat(Mat inputMat)
    {
        // Clear previous state
        ClearProcessingState();

        // Set new input - don't store reference to avoid disposal issues
        // Process directly into Resized Mat
        Core.rotate(inputMat, inputMat, Core.ROTATE_90_CLOCKWISE);

        Imgproc.resize(inputMat, Resized, new Size(480, 640), 0, 0, Imgproc.INTER_AREA);
        // Resized = inputMat;
        Image = null; // No reference to original
    }
    
    // Clear processing state for reuse
    private void ClearProcessingState()
    {
        SquareContours.Clear();
        RejectedContours.Clear();
        RecoveredContours.Clear();
        SortedContours.Clear();
        MeanLabValues.Clear();
        Boundary = Vector4.zero;
    }

    /* ---------- helpers ---------- */
    public static Point ContourCenter(MatOfPoint c)
    {
        Moments m = Imgproc.moments(c);
        if (Math.Abs(m.m00) < double.Epsilon) return new Point(0, 0);
        return new Point(m.m10 / m.m00, m.m01 / m.m00);
    }

    /* ---------- step 1: preprocess ---------- */
    public Mat ReadAndPreprocess()
    {
        Mat gray = new Mat();
        Imgproc.cvtColor(Resized, gray, Imgproc.COLOR_BGR2GRAY);

        Mat blurred = new Mat();
        Imgproc.GaussianBlur(gray, blurred, new Size(7, 7), 0);

        Mat edges = new Mat();
        Imgproc.Canny(blurred, edges, 30, 60);

        Mat kernel = Imgproc.getStructuringElement(Imgproc.MORPH_RECT, new Size(3, 3));
        Mat dilated = new Mat();
        Imgproc.dilate(edges, dilated, kernel, new Point(-1, -1), 4);

        // Clean up intermediate mats
        gray.Dispose();
        blurred.Dispose();
        edges.Dispose();
        kernel.Dispose();

        return dilated;   // caller disposes
    }

    /* ---------- step 2: detect squares ---------- */
    public void DetectSquares(Mat dilated)
    {
        List<MatOfPoint> contours = new();
        Mat hierarchy = new();
        Imgproc.findContours(dilated, contours, hierarchy,
                             Imgproc.RETR_TREE, Imgproc.CHAIN_APPROX_SIMPLE);

        // Calculate adaptive area thresholds based on image size
        float imageArea = Resized.rows() * Resized.cols(); // Total pixels in processed image
        float minStickerArea = imageArea * MinAreaPercent;  // Dynamic minimum threshold
        float maxStickerArea = imageArea * MaxAreaPercent;  // Dynamic maximum threshold

        // Debug log the calculated thresholds
        // Debug.Log($"[DetectSquares] Image size: {Resized.cols()}×{Resized.rows()}, Total area: {imageArea:F0}");
        // Debug.Log($"[DetectSquares] Adaptive thresholds: min={minStickerArea:F0} ({MinAreaPercent * 100:F2}%), max={maxStickerArea:F0} ({MaxAreaPercent * 100:F1}%)");
        // Debug.Log($"[DetectSquares] Found {contours.Count} total contours");

        int candidateCount = 0;
        int acceptedCount = 0;
        foreach (MatOfPoint c in contours)
        {
            double peri = Imgproc.arcLength(new MatOfPoint2f(c.toArray()), true);
            MatOfPoint2f approx = new();
            Imgproc.approxPolyDP(new MatOfPoint2f(c.toArray()), approx, 0.04 * peri, true);
            if (approx.total() < 4) continue;

            RotatedRect rect = Imgproc.minAreaRect(new MatOfPoint2f(c.toArray()));
            float w = (float)rect.size.width;
            float h = (float)rect.size.height;
            if (w <= 0 || h <= 0) continue;

            double aspect = Math.Max(w, h) / Math.Min(w, h);
            double area = w * h;
            
            // Determine acceptance with adaptive thresholds
            bool aspectOk = aspect > 0.8 && aspect < 1.2;
            bool areaOk = area > minStickerArea && area < maxStickerArea;
            bool accepted = aspectOk && areaOk;

            // Log first few candidates for debugging with detailed threshold info
            if (candidateCount < 8)
            {
                // Debug.Log($"  Candidate {candidateCount}: w={w:F1}, h={h:F1}, aspect={aspect:F2} {(aspectOk ? "✓" : "✗")}, " + 
                        //  $"area={area:F0} {(areaOk ? "✓" : "✗")} -> {(accepted ? "ACCEPT" : "REJECT")}");
            }
            candidateCount++;

            // Use original detected contour instead of artificial rectangle
            if (accepted)
            {
                SquareContours.Add(c);
                acceptedCount++;
            }
            else
            {
                RejectedContours.Add(c);
            }
        }

        // Debug.Log($"[DetectSquares] Results: {acceptedCount} accepted, {RejectedContours.Count} rejected out of {candidateCount} candidates");
        
        if (SquareContours.Count == 0)
            throw new Exception($"No valid contours detected in {ImagePath ?? "input Mat"} with adaptive thresholds [{minStickerArea:F0}-{maxStickerArea:F0}]");
        
        hierarchy.Dispose();
    }
    
    /* ---------- step 3a: prune to cube boundary ---------- */
    public void PruneToCubeBoundary(float gapFactor = 0.5f)
    {
        if (SquareContours.Count == 0) return;

        // 1 – area filter around median
        List<double> areas = new ();
        foreach (var c in SquareContours)
        {
            RotatedRect r = Imgproc.minAreaRect(new MatOfPoint2f(c.toArray()));
            areas.Add(r.size.width * r.size.height);
        }
        double medArea = Median(areas);
        var keep = new List<MatOfPoint>();
        for (int i = 0; i < SquareContours.Count; ++i)
            if (areas[i] >= 0.6 * medArea && areas[i] <= 1.4 * medArea)
                keep.Add(SquareContours[i]);
        if (keep.Count == 0) keep = SquareContours;   // fallback

        // 2 – compute bounding box of centres
        var centres = new List<Point>();
        foreach (var c in keep) centres.Add(ContourCenter(c));
        double minX = centres.Min(p => p.x);
        double maxX = centres.Max(p => p.x);
        double minY = centres.Min(p => p.y);
        double maxY = centres.Max(p => p.y);

        double gapX = Median(Diffs(centres.Select(p => p.x)));
        double gapY = Median(Diffs(centres.Select(p => p.y)));
        if (gapX == 0) gapX = 1;
        if (gapY == 0) gapY = 1;
        double tolX = gapFactor * gapX;
        double tolY = gapFactor * gapY;

        // 3 – spatial keep
        SquareContours.Clear();
        foreach (var c in keep)
        {
            Point p = ContourCenter(c);
            if (p.x >= minX - tolX && p.x <= maxX + tolX &&
                p.y >= minY - tolY && p.y <= maxY + tolY)
                SquareContours.Add(c);
        }
        
        // Store boundary for recovery algorithm
        Boundary = new Vector4((float)minX, (float)minY, (float)maxX, (float)maxY);
        
        // Debug.Log($"[PruneToCubeBoundary] Kept {SquareContours.Count} contours within boundary ({minX:F1},{minY:F1}) to ({maxX:F1},{maxY:F1})");
    }

    /* ---------- step 3b: select up-to 9 and sort row-major ---------- */
    public List<MatOfPoint> SortedContours = new ();

    public void SelectAndSortContours()
    {
        // distance to cube centre
        var centrePairs = SquareContours
            .Select(c => (contour: c, ctr: ContourCenter(c)))
            .ToList();

        Point avg = new Point(
            centrePairs.Average(p => p.ctr.x),
            centrePairs.Average(p => p.ctr.y));

        var ordered = centrePairs
            .OrderBy(p => Distance(p.ctr, avg))
            .Take(9)                                      // closest 9
            .ToList();

        // drop obvious area outliers
        double meanArea = ordered.Average(p =>
        {
            RotatedRect r = Imgproc.minAreaRect(new MatOfPoint2f(p.contour.toArray()));
            return r.size.width * r.size.height;
        });
        ordered = ordered.Where(p =>
        {
            RotatedRect r = Imgproc.minAreaRect(new MatOfPoint2f(p.contour.toArray()));
            double a = r.size.width * r.size.height;
            return a >= 0.5 * meanArea && a <= 1.5 * meanArea;
        }).ToList();

        // row-major sort: first by Y, then by X in groups of 3 (matching Python)
        var sortedByY = ordered.OrderBy(p => p.ctr.y).ToList();
        
        SortedContours.Clear();
        for (int i = 0; i < sortedByY.Count; i += 3)
        {
            // Take up to 3 contours for this row and sort by X
            var row = sortedByY.Skip(i).Take(3).OrderBy(p => p.ctr.x);
            SortedContours.AddRange(row.Select(p => p.contour));
        }
            
        // Debug.Log($"[SelectAndSortContours] Selected {SortedContours.Count} contours from {SquareContours.Count} candidates");
        
        // Log grid positions for debugging (showing 3x3 layout)
        // Debug.Log("[SelectAndSortContours] 3x3 Grid Layout:");
        for (int i = 0; i < SortedContours.Count; i++)
        {
            Point center = ContourCenter(SortedContours[i]);
            int row = i / 3;
            int col = i % 3;
            string gridPos = row == 1 && col == 1 ? " ← CENTER" : "";
            // Debug.Log($"  Grid[{i}] Row:{row} Col:{col}: ({center.x:F1}, {center.y:F1}){gridPos}");
        }
    }

    /* ---------- small helpers ---------- */
    static double Distance(Point a, Point b) =>
        Math.Sqrt((a.x - b.x) * (a.x - b.x) + (a.y - b.y) * (a.y - b.y));

    static double Median(IEnumerable<double> src)
    {
        var arr = src.OrderBy(v => v).ToArray();
        int n = arr.Length;
        return n % 2 == 1 ? arr[n / 2] : 0.5 * (arr[n / 2 - 1] + arr[n / 2]);
    }
    static IEnumerable<double> Diffs(IEnumerable<double> sortedVals)
    {
        var arr = sortedVals.OrderBy(v => v).ToArray();
        for (int i = 1; i < arr.Length; ++i) yield return arr[i] - arr[i - 1];
    }

    /* ---------- step 3c: recover missing contours ---------- */
    public void RecoverMissingContours()
    {
        if (SortedContours.Count >= 9)
        {
            // Debug.Log("[RecoverMissingContours] Already have 9 contours, skipping recovery");
            return;
        }
        
        // Debug.Log($"[RecoverMissingContours] Starting with {SortedContours.Count} contours, attempting recovery...");

        var acceptedCenters = SortedContours.Select(ContourCenter).ToArray();
        if (acceptedCenters.Length < 4)
            return;

        // Calculate grid bounds
        double minX = acceptedCenters.Min(p => p.x);
        double maxX = acceptedCenters.Max(p => p.x);
        double minY = acceptedCenters.Min(p => p.y);
        double maxY = acceptedCenters.Max(p => p.y);

        double stepX = (maxX - minX) / 2.0;
        double stepY = (maxY - minY) / 2.0;

        // Generate expected 3x3 grid centers
        var gridCenters = new List<Point>();
        for (int i = 0; i < 3; i++)
        {
            for (int j = 0; j < 3; j++)
            {
                gridCenters.Add(new Point(minX + j * stepX, minY + i * stepY));
            }
        }

        double threshold = 0.6 * Math.Min(stepX, stepY);

        // Find missing slots
        var missingSlots = new List<Point>();
        foreach (var gridCenter in gridCenters)
        {
            bool hasNearbyContour = acceptedCenters.Any(ac => 
                Distance(gridCenter, ac) <= threshold);
            
            if (!hasNearbyContour)
                missingSlots.Add(gridCenter);
        }

        // Try to match rejected contours to missing slots
        var newCandidates = new List<(Point slot, MatOfPoint contour)>();
        foreach (var contour in RejectedContours)
        {
            Point center = ContourCenter(contour);
            foreach (var slot in missingSlots)
            {
                if (Distance(center, slot) < threshold)
                {
                    // Additional validation - check if it's reasonably square-like
                    double peri = Imgproc.arcLength(new MatOfPoint2f(contour.toArray()), true);
                    MatOfPoint2f approx = new MatOfPoint2f();
                    Imgproc.approxPolyDP(new MatOfPoint2f(contour.toArray()), approx, 0.04 * peri, true);
                    
                    if (approx.total() >= 4)
                    {
                        newCandidates.Add((slot, contour));
                        break;
                    }
                    approx.Dispose();
                }
            }
        }

        // Keep closest contour for each missing slot
        var added = new List<MatOfPoint>();
        foreach (var slot in missingSlots)
        {
            var candidates = newCandidates
                .Where(nc => Distance(nc.slot, slot) < 0.1) // Same slot
                .Select(nc => new { distance = Distance(ContourCenter(nc.contour), slot), contour = nc.contour })
                .ToList();

            if (candidates.Any())
            {
                var best = candidates.OrderBy(c => c.distance).First();
                SortedContours.Add(best.contour);
                added.Add(best.contour);
                RecoveredContours.Add(best.contour); // Track recovered contours
            }
        }

        // Debug.Log($"[RecoverMissingContours] Found {missingSlots.Count} missing slots, added {added.Count} recovered contours");
        
        // Re-sort the complete list in row-major order
        if (added.Any())
        {
            var contourData = SortedContours
                .Select(c => new { contour = c, center = ContourCenter(c) })
                .OrderBy(cd => cd.center.y)  // Sort by Y first
                .ToList();

            SortedContours.Clear();
            
            // Group into rows and sort each row by X
            for (int i = 0; i < contourData.Count; i += 3)
            {
                var row = contourData.Skip(i).Take(3).OrderBy(cd => cd.center.x);
                foreach (var item in row)
                    SortedContours.Add(item.contour);
            }
            
            // Debug.Log($"[RecoverMissingContours] Re-sorted grid with {SortedContours.Count} total contours");
        }
    }

    /* ---------- step 4: extract colors ---------- */
    public void ComputeColors()
    {
        MeanLabValues.Clear();
        // Debug.Log($"[ComputeColors] Extracting colors from {SortedContours.Count} contours...");
        
        int stickerIndex = 0;
        foreach (var contour in SortedContours)
        {
            // Create mask for this contour
            using (Mat mask = Mat.zeros(Resized.rows(), Resized.cols(), CvType.CV_8UC1))
            {
                var contours = new List<MatOfPoint> { contour };
                Imgproc.drawContours(mask, contours, -1, new Scalar(255), -1);

                // Calculate mean BGR color within the mask
                Scalar meanBgr = Core.mean(Resized, mask);
                
                // Validate mask area to ensure we're processing a real sticker
                double maskArea = Core.countNonZero(mask);
                if (maskArea < 100) // Minimum reasonable sticker area
                {
                    Debug.LogWarning($"[ComputeColors] Sticker {stickerIndex}: Very small mask area ({maskArea} pixels) - possible invalid contour");
                }
                
                // Convert BGR to LAB
                using (Mat bgrMat = new Mat(1, 1, CvType.CV_8UC3, meanBgr))
                using (Mat labMat = new Mat())
                {
                    Imgproc.cvtColor(bgrMat, labMat, Imgproc.COLOR_BGR2Lab);
                    
                    // Extract LAB values and convert to proper scale
                    // LAB Mat will be CV_8UC3, so read as bytes first
                    byte[] labArray = new byte[3];
                    labMat.get(0, 0, labArray);
                    
                    // Convert to signed type first to avoid overflow (matching Python exactly)
                    short L_raw = (short)labArray[0];
                    short A_raw = (short)labArray[1]; 
                    short B_raw = (short)labArray[2];
                    
                    // Unscale channels (matching Python exactly)
                    float L_true = L_raw * 100.0f / 255.0f;    // L: 0-100
                    float A_true = A_raw - 128;                 // A: -128 to +127  
                    float B_true = B_raw - 128;                 // B: -128 to +127
                    
                    Vector3 labColor = new Vector3(L_true, A_true, B_true);
                    
                    // Validate LAB values are reasonable
                    bool isValidLab = L_true >= 0 && L_true <= 100 && 
                                     A_true >= -128 && A_true <= 127 && 
                                     B_true >= -128 && B_true <= 127 &&
                                     !float.IsNaN(L_true) && !float.IsNaN(A_true) && !float.IsNaN(B_true) &&
                                     !float.IsInfinity(L_true) && !float.IsInfinity(A_true) && !float.IsInfinity(B_true);
                    
                    if (!isValidLab)
                    {
                        Debug.LogError($"[ComputeColors] Sticker {stickerIndex}: Invalid LAB values - L:{L_true:F1} A:{A_true:F1} B:{B_true:F1}");
                    }
                    
                    // SPECIAL VALIDATION FOR CENTER STICKERS (index 4)
                    bool isCenterSticker = (stickerIndex == 4);
                    if (isCenterSticker)
                    {
                        Debug.Log($"[ComputeColors] 🎯 CENTER STICKER {stickerIndex}: LAB({L_true:F1}, {A_true:F1}, {B_true:F1}), Area:{maskArea}px");
                        
                        if (!isValidLab)
                        {
                            Debug.LogError($"[ComputeColors] ❌ CRITICAL: Center sticker has invalid LAB values! This will break classification.");
                        }
                        
                        // Check for extremely suspicious center values
                        if (maskArea < 500)
                        {
                            Debug.LogWarning($"[ComputeColors] ⚠️  CENTER WARNING: Very small mask area ({maskArea}px) - possible contour detection error");
                        }
                        
                        if (L_true < 5 || L_true > 95)
                        {
                            Debug.LogWarning($"[ComputeColors] ⚠️  CENTER WARNING: Extreme lightness L={L_true:F1} - check for shadows/highlights");
                        }
                    }
                    
                    // Check for suspicious pure white/neutral values
                    if (L_true > 99 && Math.Abs(A_true) < 1 && Math.Abs(B_true) < 1)
                    {
                        string centerWarning = isCenterSticker ? " ⚠️  CENTER AFFECTED!" : "";
                        Debug.LogWarning($"[ComputeColors] Sticker {stickerIndex}: Suspicious pure white/neutral LAB({L_true:F1}, {A_true:F1}, {B_true:F1}){centerWarning}");
                    }
                    
                    Debug.Log($"[ComputeColors] Sticker {stickerIndex}: LAB({L_true:F1}, {A_true:F1}, {B_true:F1}), BGR({meanBgr.val[0]:F1}, {meanBgr.val[1]:F1}, {meanBgr.val[2]:F1}), Area:{maskArea}px");
                    
                    MeanLabValues.Add(labColor);
                    
                    // Log detailed color information
                    Point center = ContourCenter(contour);
                    Debug.Log($"  Sticker[{stickerIndex}] at ({center.x:F1},{center.y:F1}): LAB({L_true:F1}, {A_true:F1}, {B_true:F1})");
                    stickerIndex++;
                }
            }
        }
        
        Debug.Log($"[ComputeColors] Extracted {MeanLabValues.Count} LAB color values");
        
        // Summary of color range for validation
        if (MeanLabValues.Count > 0)
        {
            float minL = MeanLabValues.Min(c => c.x);
            float maxL = MeanLabValues.Max(c => c.x);
            float minA = MeanLabValues.Min(c => c.y);
            float maxA = MeanLabValues.Max(c => c.y);
            float minB = MeanLabValues.Min(c => c.z);
            float maxB = MeanLabValues.Max(c => c.z);
            Debug.Log($"[ComputeColors] LAB ranges - L:[{minL:F1}-{maxL:F1}] A:[{minA:F1}-{maxA:F1}] B:[{minB:F1}-{maxB:F1}]");
        }
    }

    /* ---------- main processing pipeline ---------- */
    public List<Vector3> ProcessImage(bool realTime = false)
    {
        string source = ImagePath ?? "input Mat";
        // Debug.Log($"[ProcessImage] Starting processing pipeline for {source}");
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        
        Mat dilated = ReadAndPreprocess();
        DetectSquares(dilated);
        PruneToCubeBoundary();
        SelectAndSortContours();
        RecoverMissingContours();
        if (!realTime)
        {
            ComputeColors();
            Debug.Log($"[ProcessImage] ✅ Result: {MeanLabValues.Count} stickers with LAB colors extracted");
            // Final validation
            if (MeanLabValues.Count == 9)
            {
                Debug.Log("[ProcessImage] ✅ SUCCESS: Found exactly 9 stickers (complete 3x3 grid)");
            }
            else
            {
                Debug.LogWarning($"[ProcessImage] ⚠️  WARNING: Expected 9 stickers, got {MeanLabValues.Count}");
            }
        }
        
        
        dilated.Dispose(); // Clean up
        stopwatch.Stop();
        
        // Debug.Log($"[ProcessImage] ✅ Pipeline complete in {stopwatch.ElapsedMilliseconds}ms");

        if (realTime)
            return new List<Vector3>(); // Empty list indicates no colors
        else
            return MeanLabValues; // Full color data
    }
    
    /* ---------- disposal ---------- */
    private bool disposed = false;
    
    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }
    
    protected virtual void Dispose(bool disposing)
    {
        if (!disposed)
        {
            if (disposing)
            {
                // Dispose managed Mat objects
                Image?.Dispose();
                Resized?.Dispose();
                
                // Clear collections to help GC
                SquareContours?.Clear();
                RejectedContours?.Clear();
                RecoveredContours?.Clear();
                SortedContours?.Clear();
                MeanLabValues?.Clear();
                
                Debug.Log("[CubeProcessor] Disposed - all Mats cleaned up");
            }
            
            disposed = true;
        }
    }
    
    // Finalizer in case Dispose isn't called
    ~CubeProcessor()
    {
        Dispose(false);
    }
    
}
