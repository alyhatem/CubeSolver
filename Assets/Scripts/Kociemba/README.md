# Kociemba Solver for Unity

This directory contains a C# implementation of the Kociemba two-phase algorithm for solving Rubik's cubes, specifically adapted for Unity.

## Source

This code is a fork of the Unity-compatible Kociemba solver from:
**https://github.com/Megalomatt/Kociemba/tree/Unity**

## Original Algorithm

The Kociemba algorithm is a two-phase algorithm for solving Rubik's cubes developed by Herbert Kociemba. It's one of the most efficient algorithms for finding optimal or near-optimal solutions.

## Files

- `K_Enums.cs` - Core enumerations for cube representation
- `K_Tools.cs` - Utility functions for serialization and cube validation
- `K_FaceCube.cs` - Facelet-level cube representation
- `K_CubieCube.cs` - Cubie-level cube representation
- `K_CoordCube.cs` - Coordinate-based cube representation with move tables
- `K_CoordCubeBuildTables.cs` - Table building functionality
- `K_Search.cs` - Fast solver using pre-built tables
- `K_SearchRunTime.cs` - Runtime solver that builds tables on-the-fly

## Usage

The solver expects cube state as a 54-character string representing the colors of each facelet in the order: U, R, F, D, L, B faces (9 stickers each).

## Integration

This implementation has been integrated into the Unity AR Rubik's cube solver with the following modifications:
- Face letter mapping changed from W,B,R,Y,G,O to U,R,F,D,L,B for compatibility
- Integrated with the cube classification and computer vision pipeline