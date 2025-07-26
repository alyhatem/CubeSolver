import numpy as np
import cv2
from ColourExtraction import CubeFaceProcessor

class CubeClassifier:
    """
    Classifies stickers from LAB colors to facelet letters based on dynamically measured centers,
    using CIEDE2000 color difference for accurate perceptual matching.
    """

    # face_letters = ['U', 'R', 'F', 'D', 'L', 'B']
    face_letters = ['W', 'B', 'R', 'Y', 'G', 'O']

    def classify(self, stickers_lab: np.ndarray) -> str:
        """
        stickers_lab: 6x9x3 NumPy array of true LAB values (L∈[0,100], A/B∈[-128,127]).
        Returns: 54-character classification string.
        """
        result = []

        # Extract measured centers (index 4 of each face)
        centers = stickers_lab[:, 4, :]  # shape (6,3)

        for face_idx in range(6):
            for sticker_idx in range(9):
                lab = stickers_lab[face_idx, sticker_idx, :]
                # Compute CIEDE2000 distances to all centers
                dists = [self.ciede2000(lab, center) for center in centers]
                nearest_face_idx = int(np.argmin(dists))
                result.append(self.face_letters[nearest_face_idx])

        return ''.join(result)

    def ciede2000(self, lab1, lab2):
        """
        Computes CIEDE2000 color difference between two true LAB triplets.
        Based on the official CIEDE2000 formula.
        """
        # Extract channels
        L1, a1, b1 = lab1
        L2, a2, b2 = lab2

        avg_L = 0.5 * (L1 + L2)
        C1 = (a1**2 + b1**2) ** 0.5
        C2 = (a2**2 + b2**2) ** 0.5
        avg_C = 0.5 * (C1 + C2)

        G = 0.5 * (1 - (avg_C**7 / (avg_C**7 + 25**7))**0.5)
        a1p = (1 + G) * a1
        a2p = (1 + G) * a2

        C1p = (a1p**2 + b1**2) ** 0.5
        C2p = (a2p**2 + b2**2) ** 0.5
        avg_Cp = 0.5 * (C1p + C2p)

        h1p = np.degrees(np.arctan2(b1, a1p)) % 360
        h2p = np.degrees(np.arctan2(b2, a2p)) % 360

        delta_Lp = L2 - L1
        delta_Cp = C2p - C1p

        dhp = h2p - h1p
        if dhp > 180:
            dhp -= 360
        elif dhp < -180:
            dhp += 360

        delta_Hp = 2 * (C1p * C2p)**0.5 * np.sin(np.radians(dhp) / 2)

        avg_Lp = (L1 + L2) / 2
        avg_Hp = (h1p + h2p + 360) / 2 if abs(h1p - h2p) > 180 else (h1p + h2p) / 2

        T = (1
             - 0.17 * np.cos(np.radians(avg_Hp - 30))
             + 0.24 * np.cos(np.radians(2 * avg_Hp))
             + 0.32 * np.cos(np.radians(3 * avg_Hp + 6))
             - 0.20 * np.cos(np.radians(4 * avg_Hp - 63)))

        delta_ro = 30 * np.exp(-((avg_Hp - 275) / 25)**2)
        Rc = 2 * (avg_Cp**7 / (avg_Cp**7 + 25**7))**0.5
        Sl = 1 + (0.015 * (avg_Lp - 50)**2 / (20 + (avg_Lp - 50)**2)**0.5)
        Sc = 1 + 0.045 * avg_Cp
        Sh = 1 + 0.015 * avg_Cp * T
        Rt = -np.sin(np.radians(2 * delta_ro)) * Rc

        delta_E = ((delta_Lp / Sl)**2 +
                   (delta_Cp / Sc)**2 +
                   (delta_Hp / Sh)**2 +
                   Rt * (delta_Cp / Sc) * (delta_Hp / Sh))**0.5
        return delta_E


class CubeVisualiser:
    """
    Draws classification results on each detected sticker on the cube face image.
    """

    def __init__(self, image, contours, labels):
        """
        image: BGR image (already resized)
        contours: list of 9 contours in 3×3 grid order
        labels: 9-character string for the current face (e.g., 'RRRWGGGBY')
        """
        self.image = image.copy()
        self.contours = contours
        self.labels = labels

    def draw(self):
        """
        Returns a new image with labels drawn at the center of each sticker contour.
        """
        for i, contour in enumerate(self.contours):
            M = cv2.moments(contour)
            if M["m00"] == 0:
                continue
            cx = int(M["m10"] / M["m00"])
            cy = int(M["m01"] / M["m00"])
            label = self.labels[i]
            cv2.putText(
                self.image,
                label,
                (cx - 10, cy + 10),
                cv2.FONT_HERSHEY_SIMPLEX,
                1.2,
                (0, 0, 255),
                2,
                cv2.LINE_AA
            )
        return self.image

    def show(self, window_name="Visualised Cube Face"):
        """
        Displays the labeled image in a window.
        """
        cv2.imshow(window_name, self.draw())
        cv2.waitKey(0)
        cv2.destroyAllWindows()


# USAGE
list_of_image_paths = [f"Media/4batch_{i}.jpg" for i in range(1, 7)]

# Extract LAB color data for each face using CubeFaceProcessor
faces_data = []
for path in list_of_image_paths:
    processor = CubeFaceProcessor(path)
    face_colors = processor.process_image()
    faces_data.append(face_colors)
faces_data = np.array(faces_data, dtype=np.int16)

# Classify stickers into facelet letters
classifier = CubeClassifier()
cube_string = classifier.classify(faces_data)

for i, path in enumerate(list_of_image_paths):
    processor = CubeFaceProcessor(path)
    processor.process_image()
    face_labels = cube_string[i*9:(i+1)*9]
    visualiser = CubeVisualiser(processor.resized, processor.sorted_contours, face_labels)
    visualiser.show(f"Face {i}")
