# [Development of Electromechanical Device and Self-Adaptive Serious Game for Hand Rehabilitation] — Code Annex

This repository is an annex to the TFG (Trabajo de Fin de Grado) thesis *"[Development of Electromechanical Device and Self-Adaptive Serious Game for Hand Rehabilitation]"*. It contains the source code for a rhythm-game rehabilitation tool with a Dynamic Difficulty Adjustment (DDA) system, developed in Unity (C#), with supporting MATLAB analysis scripts and Arduino firmware.

## About this repository

This is a **code annex, not the full Unity project**. Only the scripts are included here; the complete Unity project (scenes, assets, prefabs, packages, build settings, etc.) is kept locally and is not part of this repository. The folder structure of the repository mirrors the structure the scripts had inside the original project, so their organization and relationships remain clear even outside the full project context.

A short note on the comments: some explanatory comments in this codebase were added or refined with AI assistance to make the logic easier to follow for a reader encountering it for the first time. All comments describing what a script, function, or line does have been preserved; development-only notes (e.g. references to bugs that were fixed, or code that was later removed) were cleaned up before publishing this annex.

## Repository structure

```
unity/
└── Assets/
    └── Scripts/
        ├── DDAfiles/        DDA layer: PI controller, rule-based controller,
        │                    difficulty mapping/authority, session recording,
        │                    tuning HUDs, event bus and types
        ├── UI/               Menu system: main menu, settings, pause,
        │                    device connection/diagnostics screens
        └── *.cs              Core gameplay: note spawning/movement/scoring,
                            input handling, audio feedback, hit detection
firmware/
└── firmware_rehab_device.ino   Arduino firmware for the force-sensing device
matlab/
└── Diagnostics/         Offline analysis scripts: control-loop diagnostics,
                         force/timing characterization, cross-player comparisons
```

## Key components

- **`Assets/Scripts/DDAfiles/`** — the difficulty-adjustment system itself: a model-free PI controller (`PIDifficultyController.cs`), arbitrated by `DifficultyAuthority.cs`. Designed so the whole folder can be deleted and the base game still compiles and runs on its Inspector defaults.
- **`Assets/Scripts/UI/`** — the menu system, built from code, including patient/testing profile management and device connection screens.
- **`firmware/`** — the Arduino sketch for the force-sensing rehabilitation device, reporting per-finger normalized force over serial.
- **`matlab/Diagnostics/`** — the offline analysis pipeline used to evaluate controller performance from recorded sessions (stabilization, bias, drift-trend statistics, population-level aggregation across players).

## Notes for readers

Class- and method-level comments throughout the code explain the reasoning behind non-obvious design decisions (e.g. why the PI controller has no derivative term, why difficulty is expressed as a step count rather than a [0,1] fraction, why the DDA and game layers communicate only through events). These are the same explanations that inform the corresponding sections of the thesis and are a good starting point for understanding any given script in more depth.