using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

/// <summary>
/// Classifies stickers from LAB colors to facelet letters based on dynamically measured centers,
/// using CIEDE2000 color difference for accurate perceptual matching.
/// Translated from ColourClassification.py with exact mathematical precision.
/// </summary>
public class CubeClassifier
{
    // Face letters mapping: Up, Right, Front, Down, Left, Back (Kociemba format)
    private static readonly string[] FaceLetters = { "U", "R", "F", "D", "L", "B" };
    
    // Debug flags
    private int debugSampleCount = 0;

    /// <summary>
    /// Classifies stickers from LAB colors to facelet letters.
    /// </summary>
    /// <param name="stickersLab">6 faces × 9 stickers × LAB values (L∈[0,100], A/B∈[-128,127])</param>
    /// <returns>54-character classification string</returns>
    public string Classify(List<List<Vector3>> stickersLab)
    {
        if (stickersLab == null || stickersLab.Count != 6)
            throw new ArgumentException("stickersLab must contain exactly 6 faces");

        foreach (var face in stickersLab)
        {
            if (face == null || face.Count != 9)
                throw new ArgumentException("Each face must contain exactly 9 stickers");
        }

        Debug.Log("[CubeClassifier] Starting classification of 54 stickers...");

        // Extract measured centers (index 4 of each face - center sticker)
        var centers = ExtractCenters(stickersLab);
        
        Debug.Log("[CubeClassifier] Extracted face centers:");
        for (int i = 0; i < centers.Count; i++)
        {
            var center = centers[i];
            Debug.Log($"  Face {i} ({FaceLetters[i]}): LAB({center.x:F1}, {center.y:F1}, {center.z:F1})");
        }

        var result = new List<string>();

        // Classify each sticker
        for (int faceIdx = 0; faceIdx < 6; faceIdx++)
        {
            for (int stickerIdx = 0; stickerIdx < 9; stickerIdx++)
            {
                Vector3 lab = stickersLab[faceIdx][stickerIdx];
                
                // Compute CIEDE2000 distances to all centers
                var distances = new List<float>();
                foreach (var center in centers)
                {
                    float distance = CIEDE2000(lab, center);
                    distances.Add(distance);
                }

                // Find nearest center
                int nearestFaceIdx = FindMinIndex(distances);
                string assignedLetter = FaceLetters[nearestFaceIdx];
                result.Add(assignedLetter);
                
                // Debug logging for first few stickers
                if (debugSampleCount < 5)
                {
                    Debug.Log($"[CubeClassifier] Sticker Face{faceIdx}[{stickerIdx}] LAB({lab.x:F1},{lab.y:F1},{lab.z:F1}) → {assignedLetter} (distances: {string.Join(",", distances.Select(d => d.ToString("F1")))})");
                    debugSampleCount++;
                }
            }
        }

        string cubeString = string.Join("", result);
        Debug.Log($"[CubeClassifier] ✅ Classification complete: {cubeString.Length} characters");
        return cubeString;
    }

    /// <summary>
    /// Extracts center stickers (index 4) from each face.
    /// Python equivalent: centers = stickers_lab[:, 4, :]
    /// </summary>
    private List<Vector3> ExtractCenters(List<List<Vector3>> stickersLab)
    {
        var centers = new List<Vector3>();
        for (int faceIdx = 0; faceIdx < 6; faceIdx++)
        {
            centers.Add(stickersLab[faceIdx][4]); // Center sticker
        }
        return centers;
    }

    /// <summary>
    /// Finds the index of the minimum value in a list.
    /// Python equivalent: np.argmin(distances)
    /// </summary>
    private int FindMinIndex(List<float> values)
    {
        float minValue = values.Min();
        return values.IndexOf(minValue);
    }

    /// <summary>
    /// Computes CIEDE2000 color difference between two LAB triplets.
    /// Based on the official CIEDE2000 formula with exact Python translation.
    /// Enhanced with increased hue sensitivity for better red/orange distinction.
    /// </summary>
    /// <param name="lab1">First LAB color (Vector3: x=L, y=A, z=B)</param>
    /// <param name="lab2">Second LAB color (Vector3: x=L, y=A, z=B)</param>
    /// <returns>CIEDE2000 color difference value</returns>
    public float CIEDE2000(Vector3 lab1, Vector3 lab2)
    {
        // Extract channels
        float L1 = lab1.x, a1 = lab1.y, b1 = lab1.z;
        float L2 = lab2.x, a2 = lab2.y, b2 = lab2.z;

        float avg_L = 0.5f * (L1 + L2);
        float C1 = Mathf.Sqrt(a1 * a1 + b1 * b1);
        float C2 = Mathf.Sqrt(a2 * a2 + b2 * b2);
        float avg_C = 0.5f * (C1 + C2);

        // Calculate G factor
        float avg_C7 = Mathf.Pow(avg_C, 7);
        float G = 0.5f * (1 - Mathf.Sqrt(avg_C7 / (avg_C7 + Mathf.Pow(25, 7))));
        
        float a1p = (1 + G) * a1;
        float a2p = (1 + G) * a2;

        float C1p = Mathf.Sqrt(a1p * a1p + b1 * b1);
        float C2p = Mathf.Sqrt(a2p * a2p + b2 * b2);
        float avg_Cp = 0.5f * (C1p + C2p);

        // Calculate hue angles (in degrees) - match Python's % 360 behavior exactly
        float h1p = (Mathf.Atan2(b1, a1p) * Mathf.Rad2Deg) % 360f;
        if (h1p < 0) h1p += 360f; // Handle negative modulo in C#
        
        float h2p = (Mathf.Atan2(b2, a2p) * Mathf.Rad2Deg) % 360f;
        if (h2p < 0) h2p += 360f; // Handle negative modulo in C#

        // Calculate differences
        float delta_Lp = L2 - L1;
        float delta_Cp = C2p - C1p;

        // Calculate hue difference
        float dhp = h2p - h1p;
        if (dhp > 180f)
            dhp -= 360f;
        else if (dhp < -180f)
            dhp += 360f;

        float delta_Hp = 2 * Mathf.Sqrt(C1p * C2p) * Mathf.Sin(dhp * Mathf.Deg2Rad / 2);

        // Calculate average values
        float avg_Lp = (L1 + L2) / 2;
        float avg_Hp = Mathf.Abs(h1p - h2p) > 180 ? (h1p + h2p + 360) / 2 : (h1p + h2p) / 2;
        
        // Ensure avg_Hp stays within [0, 360) range (matching Python behavior)
        if (avg_Hp >= 360) avg_Hp -= 360;

        // Calculate T factor
        float T = 1
                 - 0.17f * Mathf.Cos((avg_Hp - 30) * Mathf.Deg2Rad)
                 + 0.24f * Mathf.Cos(2 * avg_Hp * Mathf.Deg2Rad)
                 + 0.32f * Mathf.Cos((3 * avg_Hp + 6) * Mathf.Deg2Rad)
                 - 0.20f * Mathf.Cos((4 * avg_Hp - 63) * Mathf.Deg2Rad);

        // Calculate rotation and weighting factors
        float delta_ro = 30 * Mathf.Exp(-Mathf.Pow((avg_Hp - 275) / 25, 2));
        float avg_Cp7 = Mathf.Pow(avg_Cp, 7);
        float Rc = 2 * Mathf.Sqrt(avg_Cp7 / (avg_Cp7 + Mathf.Pow(25, 7)));
        
        float Sl = 1 + (0.015f * Mathf.Pow(avg_Lp - 50, 2) / Mathf.Sqrt(20 + Mathf.Pow(avg_Lp - 50, 2)));
        float Sc = 1 + 0.045f * avg_Cp;
        float Sh = 1 + 0.015f * avg_Cp * T;
        float Rt = -Mathf.Sin(2 * delta_ro * Mathf.Deg2Rad) * Rc;

        // Custom weighting factors for improved cube color classification
        float kL = 1.0f;  // Lightness weight (standard)
        float kC = 1.0f;  // Chroma weight (standard)
        float kH = 2.0f;  // Hue weight (increased for better red/orange distinction)

        // Calculate final CIEDE2000 difference with enhanced hue sensitivity
        float delta_E = Mathf.Sqrt(
            Mathf.Pow(delta_Lp / (kL * Sl), 2) +
            Mathf.Pow(delta_Cp / (kC * Sc), 2) +
            Mathf.Pow(delta_Hp / (kH * Sh), 2) +
            Rt * (delta_Cp / (kC * Sc)) * (delta_Hp / (kH * Sh))
        );

        return delta_E;
    }
}