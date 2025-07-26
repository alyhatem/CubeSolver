import cv2
import numpy as np

class CubeFaceProcessor:
    def __init__(self, image_path):
        self.image_path = image_path
        self.image = None
        self.resized = None
        self.square_contours = []
        self.sorted_contours = []
        self.rejected_contours = []
        self.mean_lab_values = []

    @staticmethod
    def contour_center(contour):
        M = cv2.moments(contour)
        if M["m00"] == 0:
            return np.array([0, 0])
        return np.array([int(M["m10"] / M["m00"]), int(M["m01"] / M["m00"])])

    def read_and_preprocess(self):
        self.image = cv2.imread(self.image_path)
        if self.image is None:
            raise FileNotFoundError(f"Image not found: {self.image_path}")
        self.resized = cv2.resize(self.image, (480, 640), interpolation=cv2.INTER_AREA)
        gray = cv2.cvtColor(self.resized, cv2.COLOR_BGR2GRAY)
        blurred = cv2.GaussianBlur(gray, (7, 7), 0)
        edges = cv2.Canny(blurred, 30, 60)
        kernel = np.ones((3, 3), np.uint8)
        dilated = cv2.dilate(edges, kernel, iterations=4)
        return dilated

    def detect_squares(self, dilated):
        contours, _ = cv2.findContours(dilated, cv2.RETR_TREE, cv2.CHAIN_APPROX_SIMPLE)
        for c in contours:
            # Approximate the contour to simplify it
            peri = cv2.arcLength(c, True)
            approx = cv2.approxPolyDP(c, 0.04 * peri, True)

            # Skip if the contour is too small to be meaningful
            if len(approx) < 4:
                continue

            rect = cv2.minAreaRect(c)
            w, h = rect[1]
            if w == 0 or h == 0:
                continue
            aspect_ratio = max(w, h) / min(w, h)
            area = w * h
            if 0.8 < aspect_ratio < 1.2 and 1000 < area < 10000:
                box = cv2.boxPoints(rect).astype(int)
                self.square_contours.append(box)
            else:
                box = cv2.boxPoints(rect).astype(int)
                self.rejected_contours.append(box)

        if not self.square_contours:
            raise ValueError(f"No valid contours detected in image: {self.image_path}")
        
    def prune_to_cube_boundary(self, gap_factor: float = 0.5) -> None:
        """
        Keep only square contours that lie inside the N×N grid and drop clear outliers.
        - Uses median sticker area to filter noisy contours.
        - Uses the spread of remaining centres to define the cube bounding box.
        - Removes any contour whose centre falls outside that box ± (gap_factor × median gap).
        Sets `self.boundary`  → (left, top, right, bottom).
        """
        if not self.square_contours:
            return                                   # nothing to do

        # --- 1. basic area filter -------------------------------------------------
        areas = [cv2.minAreaRect(c)[1][0] * cv2.minAreaRect(c)[1][1]
                for c in self.square_contours]
        med_area = np.median(areas)
        keep = [c for c, a in zip(self.square_contours, areas)
                if 0.6 * med_area <= a <= 1.4 * med_area]
        if not keep:                                # fallback – keep original
            keep = self.square_contours

        # --- 2. find bounding box of candidate centres ---------------------------
        centres = np.array([self.contour_center(c) for c in keep])
        min_x, max_x = centres[:, 0].min(), centres[:, 0].max()
        min_y, max_y = centres[:, 1].min(), centres[:, 1].max()

        # estimate median gap between rows / cols (ignoring zeros)
        gap_x = np.median(np.diff(np.unique(np.sort(centres[:, 0]))))
        gap_y = np.median(np.diff(np.unique(np.sort(centres[:, 1]))))
        gap_x = gap_x if gap_x > 0 else 1
        gap_y = gap_y if gap_y > 0 else 1
        tol_x, tol_y = gap_factor * gap_x, gap_factor * gap_y

        # --- 3. final spatial filter ---------------------------------------------
        filtered = []
        for c in keep:
            cx, cy = self.contour_center(c)
            if (min_x - tol_x) <= cx <= (max_x + tol_x) and \
            (min_y - tol_y) <= cy <= (max_y + tol_y):
                filtered.append(c)

        self.square_contours = filtered
        self.boundary = (min_x, min_y, max_x, max_y)

    def recover_missing_contours(self):
        if len(self.sorted_contours) >= 9:
            return

        accepted_centers = [self.contour_center(c) for c in self.sorted_contours]
        if len(accepted_centers) < 4:
            return

        accepted_centers = np.array(accepted_centers)
        min_x, max_x = accepted_centers[:, 0].min(), accepted_centers[:, 0].max()
        min_y, max_y = accepted_centers[:, 1].min(), accepted_centers[:, 1].max()

        step_x = (max_x - min_x) / 2
        step_y = (max_y - min_y) / 2

        grid_centers = []
        for i in range(3):
            for j in range(3):
                grid_centers.append(np.array([min_x + j * step_x, min_y + i * step_y]))

        threshold = 0.6 * min(step_x, step_y)

        # Find missing slots
        missing_slots = []
        for g in grid_centers:
            if all(np.linalg.norm(g - a) > threshold for a in accepted_centers):
                missing_slots.append(g)

        # Try to match rejected contours
        new_candidates = []
        for c in self.rejected_contours:
            center = self.contour_center(c)
            for m in missing_slots:
                if np.linalg.norm(center - m) < threshold:
                    approx = cv2.approxPolyDP(c, 0.04 * cv2.arcLength(c, True), True)
                    if len(approx) >= 4:
                        new_candidates.append((m, c))
                        break

        # Keep closest one per missing slot
        added = []
        for m in missing_slots:
            candidates = [(np.linalg.norm(self.contour_center(c) - m), c)
                          for gm, c in new_candidates if np.allclose(gm, m)]
            if candidates:
                _, best = min(candidates, key=lambda x: x[0])
                self.sorted_contours.append(best)
                added.append(best)

        # Re-sort the new list
        contour_data = [(c, self.contour_center(c)) for c in self.sorted_contours]
        contour_data.sort(key=lambda x: x[1][1])
        self.sorted_contours.clear()
        for i in range(0, len(contour_data), 3):
            row = contour_data[i:i+3]
            row.sort(key=lambda x: x[1][0])
            self.sorted_contours.extend([c for c, _ in row])

    def select_and_sort_contours(self):
        centers = [
            (c, self.contour_center(c))
            for c in self.square_contours
            if cv2.moments(c)["m00"] != 0
        ]
        all_centers = np.array([center for _, center in centers])
        avg_center = np.mean(all_centers, axis=0)
        distances = [(c, center, np.linalg.norm(center - avg_center)) for c, center in centers]
        distances.sort(key=lambda x: x[2])

        selected = distances[:9]
        areas = [cv2.minAreaRect(c)[1][0] * cv2.minAreaRect(c)[1][1] for c, _, _ in selected]
        avg_area = np.mean(areas)
        filtered = [
            (c, center) for c, center, _ in selected
            if 0.5 * avg_area < cv2.minAreaRect(c)[1][0] * cv2.minAreaRect(c)[1][1] < 1.5 * avg_area
        ]

        contour_data = [(c, self.contour_center(c)) for c, _ in filtered]
        contour_data.sort(key=lambda x: x[1][1])  # sort by Y

        self.sorted_contours.clear()
        for i in range(0, len(contour_data), 3):
            row = contour_data[i:i+3]
            row.sort(key=lambda x: x[1][0])  # sort by X
            self.sorted_contours.extend([c for c, _ in row])

    def compute_colors(self):
        self.mean_lab_values.clear()
        for contour in self.sorted_contours:
            mask = np.zeros(self.resized.shape[:2], dtype=np.uint8)
            cv2.drawContours(mask, [contour], -1, 255, -1)
            mean_bgr = cv2.mean(self.resized, mask=mask)[:3]
            # mean_hsv = cv2.cvtColor(np.uint8([[mean_bgr]]), cv2.COLOR_BGR2HSV)[0][0]
            mean_lab = cv2.cvtColor(np.uint8([[mean_bgr]]), cv2.COLOR_BGR2LAB)[0][0]
            # Convert to signed type first to avoid overflow
            lab = mean_lab.astype(np.int16)

            # Unscale channels
            L_true = lab[0] * 100 / 255.0
            A_true = lab[1] - 128
            B_true = lab[2] - 128

            true_lab = np.array([L_true, A_true, B_true], dtype=np.float32)
            self.mean_lab_values.append(true_lab)

            center = tuple(self.contour_center(contour))
            w, h     = cv2.minAreaRect(contour)[1]
            radius   = int(0.4 * min(w, h))

            # brighten or darken the fill colour
            b, g, r  = mean_bgr
            factor   = 1.4              # lighten dark stickers, darken bright ones
            fill     = tuple(int(np.clip(c * factor, 0, 255)) for c in (b, g, r))

            # draw filled circle + double outline (white then black)
            # cv2.circle(self.resized, center, radius,     fill,             -1)  # fill
            # cv2.circle(self.resized, center, radius,     (255, 255, 255),  2)   # inner white stroke
            # cv2.circle(self.resized, center, radius + 2, (0,   0,   0),    4)   # outer black stroke

    def process_image(self):
        dilated = self.read_and_preprocess()
        self.detect_squares(dilated)
        self.prune_to_cube_boundary()
        self.select_and_sort_contours()
        self.recover_missing_contours()
        self.compute_colors()
        return self.mean_lab_values


# Example usage:
if __name__ == "__main__":
    list_of_image_paths = [f"Media/6batch_{i}.jpg" for i in range(1, 7)]
    faces_data = []
    for path in list_of_image_paths:
        processor = CubeFaceProcessor(path)
        face_colors = processor.process_image()
        # cv2.imshow("Img", cv2.drawContours(processor.resized, processor.sorted_contours, -1, (0, 0, 255), 2))
        cv2.imshow("Img", processor.resized)
        cv2.waitKey(0)
        cv2.destroyAllWindows()
        faces_data.append(face_colors)

    faces_data = np.array(faces_data, dtype=np.int16)
    print(faces_data)
