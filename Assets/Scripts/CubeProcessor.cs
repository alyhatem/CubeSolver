// CubeFaceProcessor.cs
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEngine;
using OpenCVForUnity.CoreModule;
using OpenCVForUnity.ImgcodecsModule;
using OpenCVForUnity.ImgprocModule;

public class CubeProcessor
{
    public readonly string ImagePath;
    public Mat Image;                 // original BGR
    public Mat Resized;               // 480×640 BGR
    public readonly List<MatOfPoint> SquareContours = new();
    public readonly List<MatOfPoint> RejectedContours = new();

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

    public CubeProcessor(string imagePath)
    {
        ImagePath = imagePath;
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
        Image = Imgcodecs.imread(ImagePath, Imgcodecs.IMREAD_COLOR);
        if (Image.empty())
            throw new FileNotFoundException($"Image not found: {ImagePath}");

        Resized = new Mat();
        Imgproc.resize(Image, Resized, new Size(480, 640), 0, 0, Imgproc.INTER_AREA);

        Mat gray = new Mat();
        Imgproc.cvtColor(Resized, gray, Imgproc.COLOR_BGR2GRAY);

        Mat blurred = new Mat();
        Imgproc.GaussianBlur(gray, blurred, new Size(7, 7), 0);

        Mat edges = new Mat();
        Imgproc.Canny(blurred, edges, 30, 60);

        Mat kernel = Imgproc.getStructuringElement(Imgproc.MORPH_RECT, new Size(3, 3));
        Mat dilated = new Mat();
        Imgproc.dilate(edges, dilated, kernel, new Point(-1, -1), 4);

        return dilated;   // caller disposes
    }

    /* ---------- step 2: detect squares ---------- */
    public void DetectSquares(Mat dilated)
    {
        List<MatOfPoint> contours = new();
        Mat hierarchy = new();
        Imgproc.findContours(dilated, contours, hierarchy,
                             Imgproc.RETR_TREE, Imgproc.CHAIN_APPROX_SIMPLE);

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

            MatOfPoint box = new(new Point[4]);
            Imgproc.boxPoints(rect, new MatOfPoint2f(box.toArray()));
            box = new MatOfPoint(new MatOfPoint2f(box.toArray()).toArray());

            if (aspect > 0.8 && aspect < 1.2 && area > 1000 && area < 10000)
                SquareContours.Add(box);
            else
                RejectedContours.Add(box);
        }

        if (SquareContours.Count == 0)
            throw new Exception($"No valid contours detected in {ImagePath}");
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

        // row-major sort: first by Y, then by X in groups of 3
        SortedContours = ordered
            .OrderBy(p => p.ctr.y)
            .ThenBy(p => p.ctr.x)
            .Select(p => p.contour)
            .ToList();
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
    
}
