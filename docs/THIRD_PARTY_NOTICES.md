# Third-party software notices

Layal's Windows application dependency graph currently includes the following NuGet packages.
Resolved versions for the build are recorded in the generated CycloneDX SBOM.

## Emoji.Wpf

- Package: Emoji.Wpf version 0.3.4
- License declared by upstream package metadata: WTFPL
- Copyright: Copyright (C) 2017-2023 Sam Hocevar
- Project/source: https://github.com/samhocevar/emoji.wpf
- Package metadata: https://www.nuget.org/packages/Emoji.Wpf/0.3.4
- License text: licenses/WTFPL.txt

## Stfu

- Package: Stfu (transitive dependency; see the generated SBOM for the resolved version)
- License declared by upstream package metadata: WTFPL
- Copyright: Copyright (C) 2017-2024 Sam Hocevar
- Package metadata: https://www.nuget.org/packages/Stfu
- License text: licenses/WTFPL.txt

## JeremyAnsel.HLSL.Targets

- Package: JeremyAnsel.HLSL.Targets (transitive dependency; see the generated SBOM for the resolved version)
- License declared by upstream package metadata: MIT
- Copyright: Copyright (c) 2019-2022 Jérémy Ansel
- Project/source: https://github.com/JeremyAnsel/JeremyAnsel.HLSL.Targets
- Package metadata: https://www.nuget.org/packages/JeremyAnsel.HLSL.Targets
- License text: licenses/MIT-JeremyAnsel.HLSL.Targets.txt

## System.Numerics.Tensors

- Package: System.Numerics.Tensors version 9.0.0 (transitive dependency; confirmed in the CI-generated SBOM)
- License declared by upstream: MIT
- Project/source: https://github.com/dotnet/runtime
- Package metadata: https://www.nuget.org/packages/System.Numerics.Tensors/9.0.0
- License text: licenses/MIT-SystemNumericsTensors.txt

These notices capture license declarations published by the package sources. They are an inventory aid, not a legal opinion or certification of compatibility for every intended use or distribution model. Human review is still required for project licensing, included assets, and final distribution.

## NAudio.WinMM

- Package: NAudio.WinMM version 3.3.0 (direct dependency)
- License declared by upstream: MIT
- Project/source: https://github.com/naudio/NAudio
- Package metadata: https://www.nuget.org/packages/NAudio.WinMM/3.3.0
- License text: licenses/MIT-NAudio.txt

## NAudio.Core

- Package: NAudio.Core version 3.3.0 (transitive dependency)
- License declared by upstream: MIT
- Project/source: https://github.com/naudio/NAudio
- Package metadata: https://www.nuget.org/packages/NAudio.Core/3.3.0
- License text: licenses/MIT-NAudio.txt

## NAudio.Midi

- Package: NAudio.Midi version 3.3.0 (transitive dependency)
- License declared by upstream: MIT
- Project/source: https://github.com/naudio/NAudio
- Package metadata: https://www.nuget.org/packages/NAudio.Midi/3.3.0
- License text: licenses/MIT-NAudio.txt


## OpenCvSharp4

- Package: OpenCvSharp4 version 4.13.0.20260627 (direct dependency)
- License declared by upstream NuGet metadata: Apache-2.0
- Project/source: https://github.com/shimat/opencvsharp
- Package metadata: https://www.nuget.org/packages/OpenCvSharp4/4.13.0.20260627
- License text: licenses/Apache-2.0-OpenCvSharp.txt

## OpenCvSharp4.runtime.win

- Package: OpenCvSharp4.runtime.win version 4.13.0.20260627 (direct Windows x64 native runtime dependency)
- License declared by upstream NuGet metadata: Apache-2.0
- Project/source: https://github.com/shimat/opencvsharp
- Package metadata: https://www.nuget.org/packages/OpenCvSharp4.runtime.win/4.13.0.20260627
- License text: licenses/Apache-2.0-OpenCvSharp.txt

These upstream license declarations are recorded for notice completeness; project-level redistribution and legal review remain pending.


## Runtime-downloaded local AI components (not bundled with Layal)

These components are downloaded to the current user's LocalAppData directory only after the user accepts the local AI setup prompt. Their binaries and model weights are not bundled in the Layal installer or source package.

- **Ollama for Windows** — official installer: https://ollama.com/download. The installer is launched only after Windows Authenticode reports a valid signature whose subject contains “Ollama”. The installer is downloaded over HTTPS; Layal does not embed a provider/API key.
- **Qwen3.5 2B** — pulled by Ollama from the official Ollama model library: https://ollama.com/library/qwen3.5:2b. The upstream model page currently identifies the Qwen3.5 model license as Apache-2.0. The model is downloaded at first setup, not distributed in the application package.
- **whisper.cpp Windows x64 CLI v1.9.1** — https://github.com/ggml-org/whisper.cpp/releases/tag/v1.9.1. The pinned binary archive is checked against its expected byte length and SHA-256 before extraction.
- **Whisper base model weights** — https://huggingface.co/ggerganov/whisper.cpp. The pinned model file is checked against its expected byte length and SHA-1 before use.
- **Piper speech engine** — https://github.com/rhasspy/piper/releases/tag/2023.11.14-2. The pinned Windows x64 release archive is checked against its expected byte length and extracted with path-traversal/symlink checks. The upstream release API does not publish a SHA-256 digest for this archive, so the current implementation does not claim cryptographic archive-hash verification.
- **Piper Arabic voice ar_JO-kareem-medium and English voice en_US-lessac-medium** — https://huggingface.co/rhasspy/piper-voices. The ONNX voice files are verified against upstream SHA-256 digests; each model's JSON configuration is size-checked and validated before use. The voice collection declares MIT licensing, and the Arabic voice is Jordanian Arabic rather than Egyptian Arabic.

Upstream notices and model terms apply to the downloaded components separately from Layal. This inventory is informational and does not constitute a legal opinion or rights clearance for every use case.
