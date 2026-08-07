# Plan: Port Test Constructor Image Update Fix to Android

**Created:** 2026-08-07  
**Status:** NOT STARTED  
**Direction:** **Windows → Android**

**Target (Android) source root:** `E:\VLN_Project\CardioSimulator\app\src\main\java\com\example\cardiosimulator\`  
**Reference (Windows) source root:** `E:\VLN_Project\CardioSimulator\Win\src\`  

---

## 1. Background & Goals

When creating or editing a question of type "Image" in the Test Constructor, choosing a new image when an image was already assigned failed to update the visual display of the image.

### Cause
In `TestImageStore.Copy(sourcePath, questionId)`, image files were previously saved using a static filename format `<questionId>.<ext>` (e.g. `q101.png`). Overwriting the file on disk without changing the relative filename URI caused image caching mechanisms (e.g. WinUI's `BitmapImage` / Android image loaders like Coil/Glide) to return the cached previous image from memory instead of decoding the updated image file.

### Fix Applied on Windows
`TestImageStore.Copy` was updated to:
1. Clean up any previous image files for the given `questionId`.
2. Generate a unique relative filename format `${questionId}_${Guid.NewGuid().ToString("N")[..8]}${ext}` for each image update.
3. Save the new image under this unique URI, ensuring `q.ImagePath` changes and image caches fetch and decode the fresh image.

---

## 2. Part A: Test Image Storage on Android

- Check image storage implementation for test/question bank images on Android (e.g., in storage helpers or question repositories).
- Ensure image picking/copying logic generates a fresh unique filename (`<questionId>_<uniqueHash>.<ext>`) whenever a question image is updated or re-picked.
- Clean up old image files associated with `<questionId>` upon replacement.

---

## 3. Part B: Image View / Cache Management in Compose UI

- Verify image loading in Question Bank / Test Constructor UI (e.g. `AsyncImage` or `Bitmap` loader).
- With unique relative filenames saved in `ImagePath`, the image painter will receive a new file path/URI and refresh the image preview instantly.

---

## 4. Part C: Verification

### 4.1 Manual Verification Flow
1. Open Test Constructor -> Question Bank -> New Question.
2. Select question type "Image" ("Картинка").
3. Tap "Select Image" ("Выбрать картинку") and select `image1.png`.
   - Verify `image1` is displayed.
4. Tap "Select Image" ("Выбрать картинку") again and select `image2.png`.
   - Verify `image2` is updated and displayed immediately.
