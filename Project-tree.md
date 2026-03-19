
```
Mars_Rover_Pathfinding
├─ Assignment
│  └─ VadászDénes_InformatikaVerseny2026_Programozoi.pdf
├─ config.json
├─ Map
│  ├─ mars_map_50x50.csv
│  ├─ oraimunkapalya.csv
│  ├─ test2.csv
│  ├─ test3.csv
│  └─ test_map-somewhatrandom.txt
├─ MarsRover.Console
│  ├─ AppConfig.cs
│  ├─ ConsoleRunner.cs
│  ├─ MarsRover.Console.csproj
│  ├─ Program.cs
│  ├─ RunLogger.cs
│  └─ TerminalUI.cs
├─ MarsRover.Core
│  ├─ Algorithm
│  │  ├─ Benchmarking.cs
│  │  ├─ EpisodeSnapshot.cs
│  │  ├─ HybridAgent.cs
│  │  ├─ ModelInfo.cs
│  │  ├─ OptimalMissionVerifier.cs
│  │  ├─ PrioritizedReplayBuffer.cs
│  │  ├─ QTable.cs
│  │  ├─ RewardCalculator.cs
│  │  ├─ SimulationRunner.cs
│  │  └─ TrainingContracts.cs
│  ├─ Algorithm_Records
│  │  ├─ DQNAgent.cs
│  │  ├─ NeuralNetwork.cs
│  │  ├─ QLearningAgent.cs
│  │  └─ ReplayBuffer.cs
│  ├─ MarsRover.Core.csproj
│  ├─ Models
│  │  ├─ RoverAction.cs
│  │  ├─ RoverState.cs
│  │  ├─ SimulationLogEntry.cs
│  │  └─ TileType.cs
│  ├─ Simulation
│  │  ├─ AStarPathfinder.cs
│  │  ├─ EnergyCalculator.cs
│  │  ├─ GameMap.cs
│  │  └─ SimulationEngine.cs
│  └─ Utils
│     ├─ MapGenerator.cs
│     └─ MissionLogger.cs
├─ MarsRover.Core.Tests
│  ├─ HybridAgentAndReplayTests.cs
│  └─ MarsRover.Core.Tests.csproj
├─ MarsRover.sln
├─ MarsRover.UI
│  ├─ App.axaml
│  ├─ App.axaml.cs
│  ├─ app.manifest
│  ├─ Assets
│  │  ├─ arrow-left.png
│  │  ├─ arrow-right.png
│  │  ├─ avalonia-logo.ico
│  │  ├─ BackgroundMusic.mp3
│  │  ├─ logo.ico
│  │  ├─ logo.png
│  │  ├─ MainScreenSound (FROM GTA Online).mp3
│  │  ├─ MarsBack.png
│  │  ├─ MarsSurface.png
│  │  ├─ MarsTheme.axaml
│  │  ├─ MenuScreenLogo.png
│  │  ├─ Models
│  │  │  ├─ blend
│  │  │  ├─ KékÁsvány.blend
│  │  │  ├─ Szikla.blend
│  │  │  ├─ SárgaÁsvány.blend
│  │  │  └─ ZöldÁsvány.blend
│  │  ├─ moon.png
│  │  ├─ realativo.png
│  │  ├─ SoundFXs
│  │  │  ├─ LaunchingMission.mkv
│  │  │  ├─ LoadingSound.mkv
│  │  │  ├─ RestrictedSound.mkv
│  │  │  ├─ StepBackSound.mkv
│  │  │  ├─ StepInSound.mkv
│  │  │  └─ SwitchSound.mkv
│  │  ├─ sun.png
│  │  └─ WhiteNoiseSpace.m4a
│  ├─ Controls
│  │  └─ MapCanvas.cs
│  ├─ MarsRover.UI.csproj
│  ├─ MarsRover.UI.sln
│  ├─ Program.cs
│  ├─ UiDisplaySettings.cs
│  ├─ ViewModels
│  │  └─ MainViewModel.cs
│  └─ Views
│     ├─ MainWindow.axaml
│     ├─ MainWindow.axaml.cs
│     ├─ MenuWindow.axaml
│     └─ MenuWindow.axaml.cs
├─ Project-tree.md
├─ Readme.txt
├─ Theoretical-Maximum-Calculator.txt
└─ tmp_build
   └─ Debug
      └─ net8.0
         ├─ libvlc
         │  ├─ win-x64
         │  │  ├─ hrtfs
         │  │  ├─ lua
         │  │  │  ├─ extensions
         │  │  │  ├─ http
         │  │  │  │  ├─ css
         │  │  │  │  │  └─ ui-lightness
         │  │  │  │  │     └─ images
         │  │  │  │  ├─ dialogs
         │  │  │  │  ├─ images
         │  │  │  │  ├─ js
         │  │  │  │  └─ requests
         │  │  │  ├─ intf
         │  │  │  │  └─ modules
         │  │  │  ├─ meta
         │  │  │  │  ├─ art
         │  │  │  │  └─ reader
         │  │  │  ├─ modules
         │  │  │  ├─ playlist
         │  │  │  └─ sd
         │  │  └─ plugins
         │  │     ├─ access
         │  │     ├─ access_output
         │  │     ├─ audio_filter
         │  │     ├─ audio_mixer
         │  │     ├─ audio_output
         │  │     ├─ codec
         │  │     ├─ control
         │  │     ├─ d3d11
         │  │     ├─ d3d9
         │  │     ├─ demux
         │  │     ├─ gui
         │  │     ├─ keystore
         │  │     ├─ logger
         │  │     ├─ lua
         │  │     ├─ meta_engine
         │  │     ├─ misc
         │  │     ├─ mux
         │  │     ├─ packetizer
         │  │     ├─ services_discovery
         │  │     ├─ spu
         │  │     ├─ stream_extractor
         │  │     ├─ stream_filter
         │  │     ├─ stream_out
         │  │     ├─ text_renderer
         │  │     ├─ video_chroma
         │  │     ├─ video_filter
         │  │     ├─ video_output
         │  │     ├─ video_splitter
         │  │     └─ visualization
         │  └─ win-x86
         │     ├─ hrtfs
         │     ├─ lua
         │     │  ├─ extensions
         │     │  ├─ http
         │     │  │  ├─ css
         │     │  │  │  └─ ui-lightness
         │     │  │  │     └─ images
         │     │  │  ├─ dialogs
         │     │  │  ├─ images
         │     │  │  ├─ js
         │     │  │  └─ requests
         │     │  ├─ intf
         │     │  │  └─ modules
         │     │  ├─ meta
         │     │  │  ├─ art
         │     │  │  └─ reader
         │     │  ├─ modules
         │     │  ├─ playlist
         │     │  └─ sd
         │     └─ plugins
         │        ├─ access
         │        ├─ access_output
         │        ├─ audio_filter
         │        ├─ audio_mixer
         │        ├─ audio_output
         │        ├─ codec
         │        ├─ control
         │        ├─ d3d11
         │        ├─ d3d9
         │        ├─ demux
         │        ├─ gui
         │        ├─ keystore
         │        ├─ logger
         │        ├─ lua
         │        ├─ meta_engine
         │        ├─ misc
         │        ├─ mux
         │        ├─ packetizer
         │        ├─ services_discovery
         │        ├─ spu
         │        ├─ stream_extractor
         │        ├─ stream_filter
         │        ├─ stream_out
         │        ├─ text_renderer
         │        ├─ video_chroma
         │        ├─ video_filter
         │        ├─ video_output
         │        ├─ video_splitter
         │        └─ visualization
         └─ runtimes
            ├─ linux-arm
            │  └─ native
            ├─ linux-arm64
            │  └─ native
            ├─ linux-musl-x64
            │  └─ native
            ├─ linux-x64
            │  └─ native
            ├─ osx
            │  └─ native
            ├─ win-arm64
            │  └─ native
            ├─ win-x64
            │  └─ native
            └─ win-x86
               └─ native

```