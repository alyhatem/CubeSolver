using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using TMPro;
using UnityEngine.SceneManagement;

public class CubeSolverController : MonoBehaviour
{
    [Header("UI References")]
    public TextMeshProUGUI instructionText;
    public Button previousButton;
    public Button nextButton;
    public Image previousButtonImage;
    public Image nextButtonImage;
    
    [Header("AR Integration")]
    public CaptureGuide captureGuide; // Reference to CaptureGuide for arrow positioning

    [Header("Button Colors")]
    private readonly Color enabledColor = new Color(1f, 1f, 1f, 1f); // White (255,255,255,255)
    private readonly Color disabledColor = new Color(130f/255f, 130f/255f, 130f/255f, 1f); // Gray (130,130,130,255)

    // Static data for receiving solution from CubeCaptureController
    public static string SolutionString = "";

    // Solution management
    private string[] solutionMoves;
    private int currentStepIndex = 0;

    // Move translation dictionary
    private readonly Dictionary<string, string> moveTranslations = new Dictionary<string, string>
    {
        // Basic clockwise moves
        {"U", "Turn Top face Clockwise"},
        {"R", "Turn Right face Clockwise"},
        {"F", "Turn Front face Clockwise"},
        {"D", "Turn Bottom face Clockwise"},
        {"L", "Turn Left face Clockwise"},
        {"B", "Turn Back face Clockwise"},
        
        // Prime (anti-clockwise) moves
        {"U'", "Turn Top face Anti-Clockwise"},
        {"R'", "Turn Right face Anti-Clockwise"},
        {"F'", "Turn Front face Anti-Clockwise"},
        {"D'", "Turn Bottom face Anti-Clockwise"},
        {"L'", "Turn Left face Anti-Clockwise"},
        {"B'", "Turn Back face Anti-Clockwise"},
        
        // Double moves
        {"U2", "Turn Top face Twice"},
        {"R2", "Turn Right face Twice"},
        {"F2", "Turn Front face Twice"},
        {"D2", "Turn Bottom face Twice"},
        {"L2", "Turn Left face Twice"},
        {"B2", "Turn Back face Twice"}
    };

    void Start()
    {
        Debug.Log($"[CubeSolverController] Starting solver UI with solution: '{SolutionString}'");
        
        // Validate solution exists
        if (string.IsNullOrEmpty(SolutionString))
        {
            Debug.LogError("[CubeSolverController] No solution provided! Returning to capture scene.");
            instructionText.text = "No solution available. Please try again.";
            return;
        }
        
        // Parse solution into individual moves
        ParseSolution();
        
        // Initialize UI
        SetupUI();
        
        // Initialize AR integration
        InitializeARIntegration();
        
        // Display first step
        UpdateDisplay();
    }

    void ParseSolution()
    {
        // Remove phase separator and clean up solution string
        string cleanSolution = SolutionString.Replace(". ", " ").Trim();
        
        // Split into individual moves
        solutionMoves = cleanSolution.Split(new char[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
        
        Debug.Log($"[CubeSolverController] Parsed {solutionMoves.Length} moves: {string.Join(", ", solutionMoves)}");
    }

    void SetupUI()
    {
        // Attach button listeners
        previousButton.onClick.AddListener(OnPreviousPressed);
        nextButton.onClick.AddListener(OnNextPressed);
        
        // Initialize step index
        currentStepIndex = 0;
        
        Debug.Log("[CubeSolverController] UI setup complete");
    }

    void InitializeARIntegration()
    {
        if (captureGuide != null)
        {
            // Enable solver mode in CaptureGuide
            captureGuide.solverModeEnabled = true;
            Debug.Log("[CubeSolverController] AR integration initialized - solver mode enabled");
        }
        else
        {
            Debug.LogWarning("[CubeSolverController] CaptureGuide reference not assigned! Please assign it in the inspector for AR arrow guidance.");
        }
    }

    void UpdateDisplay()
    {
        // Update instruction text and arrow position
        if (currentStepIndex < solutionMoves.Length)
        {
            string currentMove = solutionMoves[currentStepIndex];
            string instruction = TranslateMove(currentMove);
            instructionText.text = instruction;
            
            // Update AR arrow position for current move
            UpdateArrowForCurrentMove(currentMove);
            
            Debug.Log($"[CubeSolverController] Step {currentStepIndex + 1}/{solutionMoves.Length}: {currentMove} → {instruction}");
        }
        else
        {
            instructionText.text = "Congratulations! Your cube is solved!";
            
            // Hide arrow when solution is complete
            if (captureGuide != null && captureGuide.solverModeEnabled)
            {
                // Could add a method to hide arrow or show completion indicator
                Debug.Log("[CubeSolverController] Solution complete - consider hiding arrow");
            }
            
            Debug.Log("[CubeSolverController] Solution complete!");
        }
        
        // Update button states and colors
        UpdateButtonStates();
    }

    void UpdateButtonStates()
    {
        // Previous button: Gray at first step, White otherwise
        bool isPreviousEnabled = currentStepIndex > 0;
        previousButton.interactable = isPreviousEnabled;
        previousButtonImage.color = isPreviousEnabled ? enabledColor : disabledColor;
        
        // Next button: Gray at last step, White otherwise  
        bool isNextEnabled = currentStepIndex < solutionMoves.Length;
        nextButton.interactable = isNextEnabled;
        nextButtonImage.color = isNextEnabled ? enabledColor : disabledColor;
        
        Debug.Log($"[CubeSolverController] Button states - Previous: {(isPreviousEnabled ? "Enabled" : "Disabled")}, Next: {(isNextEnabled ? "Enabled" : "Disabled")}");
    }

    void UpdateArrowForCurrentMove(string move)
    {
        if (captureGuide != null && captureGuide.solverModeEnabled)
        {
            captureGuide.SetArrowForMove(move);
            Debug.Log($"[CubeSolverController] Updated arrow for move: {move}");
        }
        else if (captureGuide == null)
        {
            Debug.LogWarning("[CubeSolverController] Cannot update arrow - CaptureGuide reference is null");
        }
        else if (!captureGuide.solverModeEnabled)
        {
            Debug.LogWarning("[CubeSolverController] Cannot update arrow - solver mode not enabled in CaptureGuide");
        }
    }

    string TranslateMove(string move)
    {
        if (moveTranslations.ContainsKey(move))
        {
            return moveTranslations[move];
        }
        else
        {
            Debug.LogWarning($"[CubeSolverController] Unknown move: '{move}', using literal translation");
            return $"Perform move: {move}";
        }
    }

    void OnPreviousPressed()
    {
        if (currentStepIndex > 0)
        {
            currentStepIndex--;
            UpdateDisplay();
            Debug.Log($"[CubeSolverController] Previous pressed - moved to step {currentStepIndex + 1}");
        }
    }

    void OnNextPressed()
    {
        if (currentStepIndex < solutionMoves.Length)
        {
            currentStepIndex++;
            UpdateDisplay();
            Debug.Log($"[CubeSolverController] Next pressed - moved to step {currentStepIndex + 1}");
        }
    }

    // Public methods for testing or external control
    public int GetCurrentStep() { return currentStepIndex; }
    public int GetTotalSteps() { return solutionMoves?.Length ?? 0; }
    public string GetCurrentMove() 
    { 
        return currentStepIndex < solutionMoves.Length ? solutionMoves[currentStepIndex] : ""; 
    }
    
    /// <summary>
    /// Test method to manually set the arrow for a specific move.
    /// Useful for testing the AR guidance system without going through the full solution.
    /// </summary>
    /// <param name="testMove">Move to test (e.g., "U", "R'", "F2")</param>
    public void TestArrowForMove(string testMove)
    {
        if (captureGuide != null)
        {
            Debug.Log($"[CubeSolverController] Testing arrow for move: {testMove}");
            captureGuide.SetArrowForMove(testMove);
        }
        else
        {
            Debug.LogWarning("[CubeSolverController] Cannot test arrow - CaptureGuide reference is null");
        }
    }
}