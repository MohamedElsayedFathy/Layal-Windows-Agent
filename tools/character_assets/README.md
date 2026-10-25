# Character asset pipeline

Scripts used to cut Layal and her robot out of the concept sheet. They are kept for reproducibility
and are **not** part of the build or CI.

Requirements: `pip install rembg onnxruntime opencv-python-headless numpy pillow`
(rembg downloads the `isnet-anime` and `isnet-general-use` models on first use).

1. Put the concept sheet next to the scripts as `sheet.png` (1254x1254 reference sheet).
2. `python cut_layal.py`  - seated bust + six expression tiles (raw cutouts).
3. `python cut_robot.py` and `FACE=1 python cut_robot.py` - robot with a blank screen / with its original smile.
4. `python finalize.py` - writes the final PNGs to `src/Layal.Desktop/Assets/Characters`
   (paths inside the scripts are absolute for the original author's machine; adjust before use).

A higher-resolution sheet, or individual expression renders, will give a sharper result than the 160 px tiles.
