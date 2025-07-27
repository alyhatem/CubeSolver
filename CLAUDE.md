# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project Overview

This is a Unity AR mobile application that helps users solve Rubik's cubes by capturing and analyzing the cube faces using computer vision. The app uses AR Foundation for camera capture and OpenCV for Unity for image processing.

**Unity Version**: 2022.3.10f1
**Target Platforms**: iOS and Android (AR-enabled devices)

## Build Commands

### Building the Project
- **Open in Unity**: Open the project folder in Unity Editor 2022.3.10f1 or compatible
- **Build for iOS**: File → Build Settings → iOS → Build (requires Xcode for final deployment)
- **Build for Android**: File → Build Settings → Android → Build (requires Android SDK)

### Development Workflow
- **Play Mode Testing**: Use Unity Editor Play Mode with AR Simulation enabled in XR → XR Simulation
- **Device Testing**: Build and deploy to AR-capable device for full camera functionality
- **Scene**: Primary scene is `Assets/Scenes/CubeCapture.unity`

## Core Architecture

### AR Capture Pipeline
The application follows a sequential face capture workflow:

1. **CubeCaptureController** (`Assets/Scripts/CubeCaptureController.cs`):
   - Manages the main AR capture state machine
   - Handles UI transitions between capture/review modes
   - Orchestrates the 6-face capture sequence (U, R, F, D, L, B)
   - Processes AR camera images with cropping and rotation

2. **CaptureGuide** (`Assets/Scripts/CaptureGuide.cs`):
   - Provides real-time capture feedback
   - Monitors device tilt using gyroscope
   - Calculates image sharpness to prevent blurry captures
   - Displays contextual hints to guide user

3. **CubeProcessor** (`Assets/Scripts/CubeProcessor.cs`):
   - Core computer vision processing for captured images
   - Detects square contours representing cube stickers
   - Applies spatial filtering and sorting for 3x3 grid identification
   - Uses area-based filtering to eliminate false positives

### Key Dependencies
- **AR Foundation 5.1.4**: Camera management and AR functionality
- **OpenCV for Unity**: Computer vision processing (imported as local asset)
- **XR Interaction Toolkit 3.0.4**: AR interaction handling
- **Universal Render Pipeline**: Optimized rendering for mobile AR

### Image Processing Flow
```
AR Camera → CPU Image Acquisition → Texture2D Conversion → 
90° Rotation → Crop to Grid Overlay → Save to Persistent Storage →
OpenCV Processing (Resize → Blur → Edge Detection → Contour Detection → 
Area Filtering → Spatial Pruning → Grid Sorting)
```

### Data Storage
- Captured images saved to `Application.persistentDataPath` as `face_{key}.jpg`
- Face keys follow standard Rubik's cube notation: U (Up), R (Right), F (Front), D (Down), L (Left), B (Back)

## Development Notes

### AR Requirements
- Device must support ARCore (Android) or ARKit (iOS)
- Camera permissions required at runtime
- Good lighting conditions needed for reliable contour detection

### OpenCV Integration
- OpenCV for Unity is included as a local asset in `Assets/OpenCVForUnity/`
- Critical classes: `Mat`, `Imgproc`, `Imgcodecs`, `MatOfPoint`
- Image format conversion handled via `OpenCVMatUtils.MatToTexture2D()`

### UI Architecture
- Two main UI states: Capture Panel and Review Panel
- Grid overlay provides visual guide for cube alignment during capture
- TextMeshPro used for all text rendering
- Raw Image component displays captured/processed images

### Memory Management
- Texture2D objects must be explicitly destroyed to prevent memory leaks
- OpenCV Mat objects require proper disposal
- NativeArray usage in AR image processing requires Dispose() calls

### Testing Considerations
- AR functionality requires physical device testing
- Computer vision accuracy depends on lighting and cube visibility
- Grid alignment critical for proper contour detection
- Consider various cube colors and surface reflectivity during testing