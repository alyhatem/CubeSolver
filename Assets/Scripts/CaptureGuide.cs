// using System;
// using System.IO;
// using System.Linq;
// using UnityEngine;
// using UnityEngine.XR.ARFoundation;
// using UnityEngine.XR.ARSubsystems;
// using TMPro;
// using Unity.Collections;
// using Unity.Collections.LowLevel.Unsafe;
// using UnityEngine.UI;
// using OpenCVForUnity.CoreModule;
// using OpenCVForUnity.ImgprocModule;
// using OpenCVForUnity.ImgcodecsModule;
// using OpenCVForUnity.Calib3dModule;
// using OpenCVForUnity.UnityUtils;
// using OpenCVForUnity.UnityIntegration;

// public class CaptureGuide : MonoBehaviour
// {
//     [Header("References")]
//     public ARCameraManager arCameraManager;
//     public TextMeshProUGUI hintText;
//     public Material wireframeMaterial;
    
//     [Header("Debug UI")]
//     public RawImage debugImage; // Shows processed frames
//     public RawImage debugImage1; // Shows contour visualization
//     public bool showDebugUI = true; // Enable real-time debug display

//     [Header("Performance Settings")]
//     public int frameSkipCount = 2; // Process every 3rd frame
//     public int analysisWidth = 640; // Higher resolution for better contour detection
//     public int analysisHeight = 480;

//     [Header("3D Tracking Settings")]
//     public float minFaceArea = 5000f; // Minimum area for face detection
//     public float maxReprojectionError = 8.0f; // Maximum error for pose estimation
//     public bool showDebugInfo = true;
//     public bool saveDebugImages = false; // Save intermediate processing images for debugging

//     // 3D Tracking components
//     private Mat cameraMatrix;
//     private Mat distCoeffs;
    
//     // Processing state
//     private int frameCounter = 0;
//     private float lastProcessTime = 0f;
//     private bool isProcessing = false;
//     private bool isCubeTracked = false;
    
//     // Debug state
//     private int debugFrameCounter = 0;
//     private float lastDebugUpdateTime = 0f;

//     private Texture2D frameTexture;
//     private byte[] jpgData;

//     // 3D model for single cube face (57mm standard size)
//     private static readonly Point3[] FACE_3D_POINTS = {
//         new Point3(-0.0285, -0.0285, 0), // bottom-left
//         new Point3( 0.0285, -0.0285, 0), // bottom-right
//         new Point3( 0.0285,  0.0285, 0), // top-right
//         new Point3(-0.0285,  0.0285, 0)  // top-left
//     };

//     void Start()
//     {
//         InitializeCameraMatrix();
        
//         if (hintText != null)
//             hintText.text = "Point camera at cube face";
//     }
    
//     private Texture2D RotateTexture90CW(Texture2D src)
//     {
//         int width = src.width;
//         int height = src.height;
//         Texture2D result = new Texture2D(height, width, src.format, false);
//         Color[] pixels = src.GetPixels();

//         for (int y = 0; y < height; y++)
//         {
//             for (int x = 0; x < width; x++)
//             {
//                 result.SetPixel(y, width - x - 1, pixels[y * width + x]);
//             }
//         }

//         result.Apply();
//         return result;
//     }

//     private void InitializeCameraMatrix()
//     {
//         // Create placeholder camera matrix - will be updated with AR Foundation data
//         cameraMatrix = Mat.eye(3, 3, CvType.CV_64FC1);

//         // Try to get real camera parameters from AR Foundation
//         UpdateCameraMatrixFromAR();

//         // No distortion for now
//         distCoeffs = Mat.zeros(4, 1, CvType.CV_64FC1);

//         Debug.Log("[CaptureGuide] Camera matrix initialized");
//     }
    
//     private void UpdateCameraMatrixFromAR()
//     {
//         try
//         {
//             if (arCameraManager != null)
//             {
//                 // For now, use estimated values based on typical mobile camera parameters
//                 // TODO: Implement proper AR Foundation camera intrinsics when available
//                 Debug.Log("[CaptureGuide] AR camera manager available, using estimated parameters");
//             }
            
//             // Fallback to estimated values
//             float estimatedFx = analysisWidth * 0.8f; // Rough estimate
//             float estimatedFy = analysisHeight * 0.8f;
//             float estimatedCx = analysisWidth / 2.0f;
//             float estimatedCy = analysisHeight / 2.0f;
            
//             cameraMatrix.put(0, 0, estimatedFx);
//             cameraMatrix.put(1, 1, estimatedFy);
//             cameraMatrix.put(0, 2, estimatedCx);
//             cameraMatrix.put(1, 2, estimatedCy);
            
//             Debug.Log($"[CaptureGuide] Using estimated camera parameters: fx={estimatedFx:F1}, fy={estimatedFy:F1}");
//         }
//         catch (Exception ex)
//         {
//             Debug.LogWarning($"[CaptureGuide] Failed to get AR camera parameters: {ex.Message}");
            
//             // Use basic fallback values
//             cameraMatrix.put(0, 0, 800.0);
//             cameraMatrix.put(1, 1, 800.0);
//             cameraMatrix.put(0, 2, analysisWidth / 2.0);
//             cameraMatrix.put(1, 2, analysisHeight / 2.0);
//         }
//     }

//     void Update()
//     {
//         if (arCameraManager == null || hintText == null || isProcessing)
//             return;

//         // Frame rate limiting - process every Nth frame
//         frameCounter++;
//         if (frameCounter < frameSkipCount)
//             return;
        
//         frameCounter = 0;

//         // Time-based limiting - don't process more than 10 times per second
//         if (Time.time - lastProcessTime < 0.1f)
//             return;

//         ProcessCurrentFrame();
//     }

//     unsafe void ProcessCurrentFrame()
//     {
//         if (!arCameraManager.TryAcquireLatestCpuImage(out XRCpuImage cpuImage))
//         {
//             hintText.text = "Camera not ready";
//             return;
//         }

//         isProcessing = true;
//         lastProcessTime = Time.time;

//         try
//         {
//             using (cpuImage)
//             {
//                 var conversionParams = new XRCpuImage.ConversionParams
//                 {
//                     inputRect = new RectInt(0, 0, cpuImage.width, cpuImage.height),
//                     outputDimensions = new Vector2Int(cpuImage.width, cpuImage.height),
//                     outputFormat = TextureFormat.RGBA32,
//                     transformation = XRCpuImage.Transformation.MirrorX
//                 };

//                 int size = conversionParams.outputDimensions.x * conversionParams.outputDimensions.y * 4;
//                 var data = new NativeArray<byte>(size, Allocator.Temp);
//                 cpuImage.Convert(conversionParams, (System.IntPtr)data.GetUnsafePtr(), size);

//                 frameTexture = new Texture2D(cpuImage.width, cpuImage.height, TextureFormat.RGBA32, false);
//                 frameTexture.LoadRawTextureData(data);
//                 frameTexture.Apply();
//                 data.Dispose();
//             }

//             frameTexture = RotateTexture90CW(frameTexture);
//             Mat frameMat = new Mat(frameTexture.height, frameTexture.width, CvType.CV_8UC4);
//             OpenCVMatUtils.Texture2DToMat(frameTexture, frameMat);
//             Destroy(frameTexture);

            

//         }
//         catch (Exception ex)
//         {
//             Debug.LogWarning($"[CaptureGuide] Frame processing error: {ex.Message}");
//             UpdateTrackingStatus("Processing error", false);
//         }
//         finally
//         {
//             isProcessing = false;
//         }
//     }
    
//     private void ProcessSingleFaceTracking(Mat inpuMat)
//     {
//         CubeProcessor processor = null;
        
//         try
//         {   
//             // Save debug frame if enabled
//             if (saveDebugImages)
//             {
//                 // SaveDebugImage(inputMat, $"input_frame_{debugFrameCounter}");
//             }
            
//             // Use existing robust 9-sticker detection system
//             processor = new CubeProcessor("");
//             int stickerCount = processor.ProcessImageForCounting(inputMat);
            
//             // Create debug visualization with contours for both saving and UI display
//             Mat debugMat = null;
//             if ((saveDebugImages || showDebugUI) && processor.Resized != null)
//             {
//                 debugMat = processor.Resized.clone();
                
//                 // Draw all detected contours in green
//                 if (processor.SquareContours != null && processor.SquareContours.Count > 0)
//                 {
//                     Imgproc.drawContours(debugMat, processor.SquareContours, -1, new Scalar(0, 255, 0), 2);
//                 }
                
//                 // Draw rejected contours in red
//                 if (processor.RejectedContours != null && processor.RejectedContours.Count > 0)
//                 {
//                     Imgproc.drawContours(debugMat, processor.RejectedContours, -1, new Scalar(0, 0, 255), 1);
//                 }
                
//                 // Draw sorted contours (final result) in blue with numbers
//                 if (processor.SortedContours != null && processor.SortedContours.Count > 0)
//                 {
//                     for (int i = 0; i < processor.SortedContours.Count; i++)
//                     {
//                         Point center = CubeProcessor.ContourCenter(processor.SortedContours[i]);
//                         Imgproc.circle(debugMat, center, 10, new Scalar(255, 0, 0), -1);
//                         Imgproc.putText(debugMat, i.ToString(), center, Imgproc.FONT_HERSHEY_SIMPLEX, 1, new Scalar(255, 255, 255), 2);
//                     }
//                 }
                
//                 // Save debug images if enabled
//                 if (saveDebugImages)
//                 {
//                     SaveDebugImage(processor.Resized, $"processed_frame_{debugFrameCounter}");
//                     SaveDebugImage(debugMat, $"contours_frame_{debugFrameCounter}");
//                 }
//             }
            
//             // Update real-time debug display
//             if (showDebugUI)
//             {
//                 UpdateDebugDisplay(processor?.Resized, debugMat);
//             }
            
//             // Clean up debug mat
//             if (debugMat != null)
//             {
//                 debugMat.Dispose();
//             }
            
//             // Log detailed processing results
//             Debug.Log($"[CaptureGuide] Frame {debugFrameCounter}: Detected {stickerCount} stickers");
//             Debug.Log($"  Initial contours: {processor.SquareContours?.Count ?? 0}");
//             Debug.Log($"  Rejected contours: {processor.RejectedContours?.Count ?? 0}");
//             Debug.Log($"  Recovered contours: {processor.RecoveredContours?.Count ?? 0}");
            
//             if (stickerCount == 9)
//             {
//                 // Extract face corners from the 9 detected stickers
//                 Point[] faceCorners = ExtractFaceCornersFromStickers(processor);
                
//                 if (faceCorners != null && faceCorners.Length == 4)
//                 {
//                     // Estimate 3D pose using extrapolated face boundary
//                     Vector3 facePosition;
//                     Quaternion faceRotation;
                    
//                     if (EstimateFacePose(faceCorners, out facePosition, out faceRotation))
//                     {
//                         // Update wireframe visualization
                        
//                         isCubeTracked = true;
//                         UpdateTrackingStatus($"Tracking 9/9 stickers - Cube face locked!", true);
                        
//                         if (showDebugInfo)
//                         {
//                             Debug.Log($"[CaptureGuide] Face tracked from 9 stickers at position: {facePosition}, rotation: {faceRotation.eulerAngles}");
//                         }
//                     }
//                     else
//                     {
//                         isCubeTracked = false;
//                         UpdateTrackingStatus("Pose estimation failed", false);
//                     }
//                 }
//                 else
//                 {
//                     isCubeTracked = false;
//                     UpdateTrackingStatus("Face boundary extraction failed", false);
//                 }
//             }
//             else
//             {
//                 isCubeTracked = false;
                
//                 // Log processing pipeline details for troubleshooting
//                 if (showDebugInfo)
//                 {
//                     if (processor?.SquareContours != null && processor.SquareContours.Count == 0)
//                     {
//                         Debug.LogWarning($"[CaptureGuide] No contours detected at all - possible edge detection failure");
//                     }
//                     else if (processor?.SquareContours != null && processor.SquareContours.Count < stickerCount)
//                     {
//                         Debug.LogWarning($"[CaptureGuide] Contour mismatch: {processor.SquareContours.Count} contours but {stickerCount} final stickers");
//                     }
//                 }
                
//                 // Provide helpful feedback based on sticker count
//                 if (stickerCount == 0)
//                 {
//                     if (processor?.SquareContours?.Count > 0)
//                         UpdateTrackingStatus($"Found {processor.SquareContours.Count} shapes but no valid stickers", false);
//                     else
//                         UpdateTrackingStatus("No stickers detected - point at cube", false);
//                 }
//                 else if (stickerCount < 6)
//                     UpdateTrackingStatus($"Detected {stickerCount}/9 stickers - move closer", false);
//                 else if (stickerCount < 9)
//                     UpdateTrackingStatus($"Detected {stickerCount}/9 stickers - better lighting needed", false);
//                 else
//                     UpdateTrackingStatus($"Too many stickers detected ({stickerCount}) - check lighting", false);
//             }
//         }
//         catch (Exception ex)
//         {
//             Debug.LogWarning($"[CaptureGuide] Tracking error: {ex.Message}");
//             UpdateTrackingStatus("Tracking error", false);
//         }
//         finally
//         {
//             // Clean up processor resources
//             if (processor?.Resized != null)
//             {
//                 processor.Resized.Dispose();
//             }
//         }
//     }

//     private Point[] ExtractFaceCornersFromStickers(CubeProcessor processor)
//     {
//         try
//         {
//             if (processor.SortedContours.Count != 9)
//             {
//                 Debug.LogWarning($"[CaptureGuide] Expected 9 stickers but got {processor.SortedContours.Count}");
//                 return null;
//             }
            
//             // Get center points of all 9 stickers (sorted in row-major order)
//             Point[] stickerCenters = new Point[9];
//             for (int i = 0; i < 9; i++)
//             {
//                 stickerCenters[i] = CubeProcessor.ContourCenter(processor.SortedContours[i]);
//             }
            
//             // Log sticker positions for debugging
//             if (showDebugInfo)
//             {
//                 Debug.Log("[CaptureGuide] Sticker grid positions:");
//                 for (int i = 0; i < 9; i++)
//                 {
//                     Debug.Log($"  Sticker {i}: ({stickerCenters[i].x:F1}, {stickerCenters[i].y:F1})");
//                 }
//             }
            
//             // Extract corner stickers from 3x3 grid:
//             // 0 1 2
//             // 3 4 5  
//             // 6 7 8
//             Point[] cornerStickers = {
//                 stickerCenters[0], // top-left
//                 stickerCenters[2], // top-right
//                 stickerCenters[8], // bottom-right  
//                 stickerCenters[6]  // bottom-left
//             };
            
//             // Extrapolate face boundary beyond sticker centers
//             Point[] faceBoundary = ExtrapolateFaceBoundary(cornerStickers);
            
//             if (showDebugInfo && faceBoundary != null)
//             {
//                 Debug.Log("[CaptureGuide] Extrapolated face boundary:");
//                 for (int i = 0; i < 4; i++)
//                 {
//                     Debug.Log($"  Corner {i}: ({faceBoundary[i].x:F1}, {faceBoundary[i].y:F1})");
//                 }
//             }
            
//             return faceBoundary;
//         }
//         catch (Exception ex)
//         {
//             Debug.LogWarning($"[CaptureGuide] Face corner extraction error: {ex.Message}");
//             return null;
//         }
//     }
    
//     private Point[] ExtrapolateFaceBoundary(Point[] cornerStickers)
//     {
//         try
//         {
//             if (cornerStickers.Length != 4) return null;
            
//             // Calculate sticker spacing from 3x3 grid layout
//             // Top edge spacing: top-right - top-left  
//             double topSpacingX = cornerStickers[1].x - cornerStickers[0].x;
//             // Bottom edge spacing: bottom-right - bottom-left
//             double bottomSpacingX = cornerStickers[2].x - cornerStickers[3].x;
//             // Average horizontal spacing between corner stickers (spans 2 sticker gaps)
//             double avgHorizontalSpacing = (topSpacingX + bottomSpacingX) / 2.0;
            
//             // Left edge spacing: bottom-left - top-left
//             double leftSpacingY = cornerStickers[3].y - cornerStickers[0].y;
//             // Right edge spacing: bottom-right - top-right  
//             double rightSpacingY = cornerStickers[2].y - cornerStickers[1].y;
//             // Average vertical spacing between corner stickers (spans 2 sticker gaps)
//             double avgVerticalSpacing = (leftSpacingY + rightSpacingY) / 2.0;
            
//             // Calculate individual sticker spacing (corner-to-corner spans 2 gaps)
//             double stickerSpacingX = avgHorizontalSpacing / 2.0;
//             double stickerSpacingY = avgVerticalSpacing / 2.0;
            
//             // Extrapolation factor: extend beyond sticker center by ~0.6 sticker spacing 
//             // This should reach approximately to the cube face edge
//             double extrapolationFactor = 0.6;
//             double dx = stickerSpacingX * extrapolationFactor;
//             double dy = stickerSpacingY * extrapolationFactor;
            
//             // Create face boundary points by extending outward from corner stickers
//             Point[] faceBoundary = {
//                 new Point(cornerStickers[0].x - dx, cornerStickers[0].y - dy), // top-left face corner
//                 new Point(cornerStickers[1].x + dx, cornerStickers[1].y - dy), // top-right face corner  
//                 new Point(cornerStickers[2].x + dx, cornerStickers[2].y + dy), // bottom-right face corner
//                 new Point(cornerStickers[3].x - dx, cornerStickers[3].y + dy)  // bottom-left face corner
//             };
            
//             // Validate that result forms reasonable square shape
//             if (!IsExtrapolatedBoundaryValid(faceBoundary, stickerSpacingX, stickerSpacingY))
//             {
//                 Debug.LogWarning("[CaptureGuide] Extrapolated boundary failed validation");
//                 return null;
//             }
            
//             return faceBoundary;
//         }
//         catch (Exception ex)
//         {
//             Debug.LogWarning($"[CaptureGuide] Face boundary extrapolation error: {ex.Message}");
//             return null;
//         }
//     }
    
//     private bool IsExtrapolatedBoundaryValid(Point[] boundary, double expectedSpacingX, double expectedSpacingY)
//     {
//         if (boundary.Length != 4) return false;
        
//         try
//         {
//             // Calculate face dimensions from extrapolated boundary
//             double faceWidth = (boundary[1].x - boundary[0].x + boundary[2].x - boundary[3].x) / 2.0;
//             double faceHeight = (boundary[3].y - boundary[0].y + boundary[2].y - boundary[1].y) / 2.0;
            
//             // Expected face dimensions: 3 stickers + 2*extrapolation
//             double expectedWidth = expectedSpacingX * 2.0 + 2 * (expectedSpacingX * 0.6);
//             double expectedHeight = expectedSpacingY * 2.0 + 2 * (expectedSpacingY * 0.6);
            
//             // Check if dimensions are within reasonable range (±50% tolerance)
//             double widthRatio = Math.Abs(faceWidth - expectedWidth) / expectedWidth;
//             double heightRatio = Math.Abs(faceHeight - expectedHeight) / expectedHeight;
            
//             bool isValid = widthRatio < 0.5 && heightRatio < 0.5;
            
//             if (showDebugInfo)
//             {
//                 Debug.Log($"[CaptureGuide] Boundary validation - Width: {faceWidth:F1} (expected {expectedWidth:F1}, ratio {widthRatio:F2}), Height: {faceHeight:F1} (expected {expectedHeight:F1}, ratio {heightRatio:F2}), Valid: {isValid}");
//             }
            
//             return isValid;
//         }
//         catch (Exception ex)
//         {
//             Debug.LogWarning($"[CaptureGuide] Boundary validation error: {ex.Message}");
//             return false;
//         }
//     }
    
//     private bool EstimateFacePose(Point[] imageCorners, out Vector3 position, out Quaternion rotation)
//     {
//         position = Vector3.zero;
//         rotation = Quaternion.identity;
        
//         try
//         {
//             // Create MatOfPoint3f for 3D object points
//             MatOfPoint3f objectPoints = new MatOfPoint3f();
//             objectPoints.fromArray(FACE_3D_POINTS);
            
//             // Create MatOfPoint2f for 2D image points
//             MatOfPoint2f imagePoints = new MatOfPoint2f();
//             imagePoints.fromArray(imageCorners);
            
//             // Solve PnP to get pose
//             Mat rvec = new Mat();
//             Mat tvec = new Mat();
            
//             // Create MatOfDouble for distortion coefficients
//             MatOfDouble distCoeffsMat = new MatOfDouble();
//             distCoeffsMat.fromArray(new double[] {0, 0, 0, 0});
            
//             bool success = Calib3d.solvePnP(objectPoints, imagePoints, cameraMatrix, distCoeffsMat, rvec, tvec);
            
//             distCoeffsMat.Dispose();
            
//             if (success)
//             {
//                 // Convert OpenCV pose to Unity coordinates
//                 double[] tvecArray = new double[3];
//                 double[] rvecArray = new double[3];
//                 tvec.get(0, 0, tvecArray);
//                 rvec.get(0, 0, rvecArray);
                
//                 // Convert to Unity coordinate system
//                 position = new Vector3((float)tvecArray[0], -(float)tvecArray[1], (float)tvecArray[2]);
//                 rotation = OpenCVARUtils.ConvertRvecToRot(rvecArray);
                
//                 // Transform to Unity's coordinate system (OpenCV uses right-handed, Unity uses left-handed)
//                 position.z = -position.z;
//                 rotation = new Quaternion(-rotation.x, rotation.y, -rotation.z, rotation.w);
//             }
            
//             // Clean up
//             objectPoints.Dispose();
//             imagePoints.Dispose();
//             rvec.Dispose();
//             tvec.Dispose();
            
//             return success;
//         }
//         catch (Exception ex)
//         {
//             Debug.LogWarning($"[CaptureGuide] Pose estimation error: {ex.Message}");
//             return false;
//         }
//     }
    
//     private void UpdateTrackingStatus(string message, bool isTracking)
//     {
//         if (hintText == null) return;
        
//         hintText.text = message;
//         hintText.color = isTracking ? Color.green : Color.red;
        
//         if (showDebugInfo)
//         {
//             Debug.Log($"[CaptureGuide] {message}");
//         }
//     }

//     private void SaveDebugImage(Mat mat, string filename)
//     {
//         try
//         {
//             string debugPath = Path.Combine(Application.persistentDataPath, "debug");
//             if (!Directory.Exists(debugPath))
//                 Directory.CreateDirectory(debugPath);
            
//             string fullPath = Path.Combine(debugPath, $"{filename}.jpg");
//             Imgcodecs.imwrite(fullPath, mat);
//             Debug.Log($"[CaptureGuide] Saved debug image: {fullPath}");
//         }
//         catch (Exception ex)
//         {
//             Debug.LogWarning($"[CaptureGuide] Failed to save debug image: {ex.Message}");
//         }
//     }
    
//     private Texture2D ConvertMatToTexture(Mat mat)
//     {
//         try
//         {
//             if (mat == null || mat.empty())
//             {
//                 Debug.LogWarning("[CaptureGuide] Cannot convert null or empty Mat to texture");
//                 return null;
//             }
            
//             // Ensure Mat is in correct format (BGR for OpenCV to Unity conversion)
//             Mat displayMat = new Mat();
//             if (mat.channels() == 3)
//             {
//                 // Convert BGR to RGB for Unity display
//                 Imgproc.cvtColor(mat, displayMat, Imgproc.COLOR_BGR2RGB);
//             }
//             else if (mat.channels() == 1)
//             {
//                 // Convert grayscale to RGB
//                 Imgproc.cvtColor(mat, displayMat, Imgproc.COLOR_GRAY2RGB);
//             }
//             else
//             {
//                 Debug.LogWarning($"[CaptureGuide] Unsupported Mat format: {mat.channels()} channels");
//                 return null;
//             }
            
//             // Fix rotation issue - rotate by 90 degrees clockwise to match expected orientation
//             Mat rotatedMat = new Mat();
//             Core.rotate(displayMat, rotatedMat, Core.ROTATE_90_CLOCKWISE);
//             displayMat.Dispose();
            
//             // Create texture with rotated dimensions
//             Texture2D texture = new Texture2D(rotatedMat.cols(), rotatedMat.rows(), TextureFormat.RGB24, false);
//             OpenCVMatUtils.MatToTexture2D(rotatedMat, texture);
            
//             rotatedMat.Dispose();
//             return texture;
//         }
//         catch (Exception ex)
//         {
//             Debug.LogWarning($"[CaptureGuide] Mat to texture conversion error: {ex.Message}");
//             return null;
//         }
//     }
    
//     private void UpdateDebugDisplay(Mat inputMat, Mat contourMat = null)
//     {
//         if (!showDebugUI) return;
        
//         // Limit debug display updates to ~5 FPS for performance
//         if (Time.time - lastDebugUpdateTime < 0.2f) return;
//         lastDebugUpdateTime = Time.time;
        
//         try
//         {
//             // Update input frame display
//             if (debugImage != null && inputMat != null)
//             {
//                 Texture2D inputTexture = ConvertMatToTexture(inputMat);
//                 if (inputTexture != null)
//                 {
//                     // Clean up previous texture
//                     if (debugImage.texture != null)
//                         DestroyImmediate(debugImage.texture);
                    
//                     debugImage.texture = inputTexture;
//                     debugImage.SetNativeSize();
//                 }
//             }
            
//             // Update contour visualization display
//             if (debugImage1 != null && contourMat != null)
//             {
//                 Texture2D contourTexture = ConvertMatToTexture(contourMat);
//                 if (contourTexture != null)
//                 {
//                     // Clean up previous texture
//                     if (debugImage1.texture != null)
//                         DestroyImmediate(debugImage1.texture);
                    
//                     debugImage1.texture = contourTexture;
//                     debugImage1.SetNativeSize();
//                 }
//             }
//         }
//         catch (Exception ex)
//         {
//             Debug.LogWarning($"[CaptureGuide] Debug display update error: {ex.Message}");
//         }
//     }
    
//     private bool ValidateFrameQuality(Mat mat)
//     {
//         try
//         {
//             // Check basic properties
//             if (mat == null || mat.empty())
//             {
//                 Debug.LogWarning("[CaptureGuide] Frame validation failed: Mat is null or empty");
//                 return false;
//             }
            
//             // Check dimensions
//             if (mat.rows() < 100 || mat.cols() < 100)
//             {
//                 Debug.LogWarning($"[CaptureGuide] Frame validation failed: Too small ({mat.cols()}x{mat.rows()})");
//                 return false;
//             }
            
//             // Check for reasonable contrast by computing standard deviation
//             Mat grayMat = new Mat();
//             if (mat.channels() == 3)
//             {
//                 Imgproc.cvtColor(mat, grayMat, Imgproc.COLOR_BGR2GRAY);
//             }
//             else if (mat.channels() == 1)
//             {
//                 grayMat = mat.clone();
//             }
//             else
//             {
//                 Debug.LogWarning($"[CaptureGuide] Frame validation failed: Unexpected channel count ({mat.channels()})");
//                 return false;
//             }
            
//             // Calculate mean and standard deviation
//             Scalar meanScalar = Core.mean(grayMat);
//             double mean = meanScalar.val[0];
            
//             // Calculate standard deviation manually
//             Mat meanMat = new Mat(grayMat.size(), grayMat.type(), meanScalar);
//             Mat diffMat = new Mat();
//             Core.absdiff(grayMat, meanMat, diffMat);
//             Core.multiply(diffMat, diffMat, diffMat);
//             Scalar varianceScalar = Core.mean(diffMat);
//             double stdDev = Math.Sqrt(varianceScalar.val[0]);
            
//             // Clean up
//             grayMat.Dispose();
//             meanMat.Dispose();
//             diffMat.Dispose();
            
//             // Check for reasonable contrast (std deviation should be > 15 for good edge detection)
//             if (stdDev < 10)
//             {
//                 Debug.LogWarning($"[CaptureGuide] Frame validation failed: Low contrast (std dev: {stdDev:F1})");
//                 return false;
//             }
            
//             // Check for reasonable brightness (not too dark or too bright)
//             if (mean < 20 || mean > 235)
//             {
//                 Debug.LogWarning($"[CaptureGuide] Frame validation failed: Extreme brightness (mean: {mean:F1})");
//                 return false;
//             }
            
//             if (showDebugInfo)
//             {
//                 Debug.Log($"[CaptureGuide] Frame quality: mean={mean:F1}, std={stdDev:F1} ✓");
//             }
            
//             return true;
//         }
//         catch (Exception ex)
//         {
//             Debug.LogWarning($"[CaptureGuide] Frame validation error: {ex.Message}");
//             return false;
//         }
//     }

//     void OnDestroy()
//     {
//         // Clean up OpenCV resources
//         if (cameraMatrix != null)
//         {
//             cameraMatrix.Dispose();
//         }
//         if (distCoeffs != null)
//         {
//             distCoeffs.Dispose();
//         }
        
//         // Clean up debug textures
//         if (debugImage != null && debugImage.texture != null)
//         {
//             DestroyImmediate(debugImage.texture);
//         }
//         if (debugImage1 != null && debugImage1.texture != null)
//         {
//             DestroyImmediate(debugImage1.texture);
//         }
        
//         Debug.Log("[CaptureGuide] Cleanup complete");
//     }
// }