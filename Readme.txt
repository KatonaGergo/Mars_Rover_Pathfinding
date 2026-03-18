================================================================================
  MARS ROVER — Q-LEARNING AGENT
  Vadász Dénes Informatika Verseny 2026
================================================================================


────────────────────────────────────────────────────────────────────────────────
MI EZ A PROJEKT?
────────────────────────────────────────────────────────────────────────────────

Egy Mars-járó, amely önállóan gyűjti az ásványokat egy 50×50-es rácsos térképen, és
24 órás küldetési időkereten belül visszatér a bázisra. A járót egy
megerősítéses tanulási ügynök irányítja – több száz edzésmeneten keresztül
megtanulja, melyik stratégiát kövesse, majd a legjobban elsajátított eljárást alkalmazza annak érdekében, hogy
a lehető legtöbb ásványt szállítsa haza.
A projekt C# (.NET 8) nyelven íródott, külső gépi tanulási dependenciák nélkül.
Minden, a tanulási algoritmus, az útvonaltervezés, a fizikai szimuláció és
a terminál interfész a semmiből épült fel.

Translated with DeepL.com (free version)


────────────────────────────────────────────────────────────────────────────────
HOGYAN KELL FUTTATNI?
────────────────────────────────────────────────────────────────────────────────

Előfeltételek: A .NET 8 SDK telepítve.

  # Csomagok visszaállítása (csak az első alkalommal)
  dotnet restore

  # Futtatás alapértelmezett beállításokkal (a config.json fájl beolvasásával)
  dotnet run --project MarsRover.Console

  # Bármely paraméter felülírása a parancssorban
  dotnet run --project MarsRover.Console -- --map Map/mars_map_50x50.csv
  dotnet run --project MarsRover.Console -- --episodes 1000
  dotnet run --project MarsRover.Console -- --hours 24 --model mymodel

  # Print saved model info without running anything
  dotnet run --project MarsRover.Console -- --info

  # Print usage
  dotnet run --project MarsRover.Console -- --help

On first run, config.json is created automatically with default values.
Edit it to change permanent defaults without using CLI flags every time.


────────────────────────────────────────────────────────────────────────────────
CONFIGURATION (config.json)
────────────────────────────────────────────────────────────────────────────────

  mapPath     Path to the 50×50 map CSV.       Default: Map/mars_map_50x50.csv
  hours       Mission duration in hours.        Default: 24  (= 48 ticks)
  episodes    Training episode count.           Default: 500
  modelPath   Base name for saved model files.  Default: model

CLI flags override config.json. Load order: defaults → config.json → CLI.

Saved files:
  <modelPath>.qtable.json   Q-value table (the learned policy)
  <modelPath>.meta.json     Training metadata (episodes, best result, epsilon)
  results/run_TIMESTAMP.txt Auto-saved log of each deployment run


────────────────────────────────────────────────────────────────────────────────
PROJECT STRUCTURE
────────────────────────────────────────────────────────────────────────────────

  MarsRover.sln
  ├── MarsRover.Core/              Business logic — zero UI dependencies
  │   ├── Models/
  │   │   ├── TileType.cs          Map cell enum (Empty, Obstacle, Mineral, Start)
  │   │   ├── RoverAction.cs       Move/Mine/Standby + Direction + Speed enums
  │   │   ├── RoverState.cs        Full tick snapshot (position, battery, phase)
  │   │   └── SimulationLogEntry.cs  One record per tick for logging and replay
  │   ├── Simulation/
  │   │   ├── GameMap.cs           CSV loader, mineral tracker, passability checks
  │   │   ├── AStarPathfinder.cs   8-directional A* with diagonal passability
  │   │   ├── EnergyCalculator.cs  All battery math (move cost, solar, trip delta)
  │   │   └── SimulationEngine.cs  Tick-by-tick executor — applies actions, logs
  │   │
  │   ├── Algorithm_Records/
  │   │      ├── QLearningAgent.cs    Early pure Q-learning agent (superseded)
  │   │      ├── DQNAgent.cs          Neural network DQN experiment (superseded)
  │   │      ├── NeuralNetwork.cs     Feedforward net used by DQNAgent (superseded)
  │   │      └── ReplayBuffer.cs      Uniform replay buffer (superseded by PER)
  │   │
  │   └── Algorithm/
  │       ├── QTable.cs            Q-value store with JSON save/load
  │       ├── HybridAgent.cs       The main agent — Q-table strategy + A* navigation
  │       ├── SimulationRunner.cs  Training loop (parallel, PER, traces, checkpoints)
  │       ├── PrioritizedReplayBuffer.cs  Priority queue replay buffer
  │       ├── RewardCalculator.cs  Reward shaping constants and logic
  │       ├── ModelInfo.cs         Public record for saved model metadata
  │       └── EpisodeSnapshot.cs  Lightweight episode path record
  │
  ├── MarsRover.Console/           Terminal interface
  │   ├── Program.cs               Entry point, argument parsing
  │   ├── AppConfig.cs             Config file loader with CLI override support
  │   ├── ConsoleRunner.cs         Training + deployment pipeline, colour output
  │   ├── RunLogger.cs             Auto-saves run logs to results/
  │   └── config.json              Default configuration file
  │
  ├── MarsRover.UI/                Avalonia GUI (visual dashboard, separate project)
  │
  └── Map/
      └── mars_map_50x50.csv       The competition map


────────────────────────────────────────────────────────────────────────────────
THE MAP FORMAT
────────────────────────────────────────────────────────────────────────────────

The map is a 50×50 CSV file. Each cell is one of:

  .   Passable Martian surface
  #   Obstacle (impassable rock)
  B   Blue mineral
  Y   Yellow mineral
  G   Green mineral
  S   Rover starting position (also the return base)

Competition map facts:
  Start position:   (34, 32)
  Total minerals:   390  (Blue: 179, Yellow: 98, Green: 113)
  Obstacles:        227
  All minerals reachable via A*


────────────────────────────────────────────────────────────────────────────────
PHYSICS MODEL
────────────────────────────────────────────────────────────────────────────────

Time
  One tick = 0.5 hours (one half-hour slot)
  One Martian sol = 48 ticks  (32 day ticks + 16 night ticks)
  Mission duration = 24 hours = 48 ticks

Battery
  Maximum battery:    100%
  Move cost:          K × speed²   where K = 2
    Slow   (1 cell/tick)  →  2×1²  =  2%/tick
    Normal (2 cells/tick) →  2×2²  =  8%/tick
    Fast   (3 cells/tick) →  2×3²  = 18%/tick
  Mining drain:       2%/tick
  Standby drain:      1%/tick
  Solar charge (day): +10%/tick  (applied simultaneously with drain)

Net battery per tick:
  Slow   day:  +10 - 2  = +8%     Slow   night:  0 - 2  = -2%
  Normal day:  +10 - 8  = +2%     Normal night:  0 - 8  = -8%
  Fast   day:  +10 - 18 = -8%     Fast   night:  0 - 18 = -18%
  Mine   day:  +10 - 2  = +8%     Mine   night:  0 - 2  = -2%

Movement
  Each Move action carries a list of 1–3 direction steps.
  The number of steps equals the speed (Slow=1, Normal=2, Fast=3).
  Steps in one tick can be in different directions (following A* path).
  Energy is charged once per tick based on speed, not per step.

Key rule — speed is capped to path length:
  If the target is 1 step away, the rover uses Slow regardless of battery.
  If 2 steps away, Normal is the ceiling. Fast only when 3+ steps remain.
  This prevents paying 18% (Fast) to move 1 cell when 2% (Slow) suffices.


────────────────────────────────────────────────────────────────────────────────
THE ALGORITHM — HYBRID Q-TABLE + A*
────────────────────────────────────────────────────────────────────────────────

Why hybrid?

  Pure Q-learning on a 50×50 grid needs to learn every (state, action) pair
  from scratch — including which direction to move each tick. With 48 ticks
  and a large state space it would need hundreds of thousands of episodes to
  converge, and it would still make suboptimal navigation decisions.

  A* already solves navigation optimally. There is no reason to learn
  something that has a perfect closed-form solution. So the Q-table is only
  asked to make strategic decisions, and A* handles everything below that.

What Q-table decides (4 actions):
  0 — SeekNearestMineral   Go to the closest reachable mineral
  1 — SeekBestMineral      Go to highest-value mineral within budget
  2 — ReturnToBase         Head home now
  3 — Explore              Move toward an uncollected area

What A* handles:
  Everything else. Once the Q-table picks a target, A* finds the exact
  shortest path, DequeueMove packs up to 3 steps per tick, and ChooseSpeed
  picks the right speed for each leg based on distance and battery.

State space (1,200 states):
  BatteryBucket (5) × IsDay (2) × DistBucket (5) × DirBucket (4)
                    × MineralsLeft (3) × UrgencyBucket (2)
  = 5 × 2 × 5 × 4 × 3 × 2 = 1,200

  This was reduced from an earlier 76,800-state design which never converged
  because with 15 decisions per episode and 1000 episodes = 15,000 updates,
  only 20% of states were ever visited. At 1,200 states every state is visited
  ~12× per episode — the table converges properly.

Why the agent scores well from episode 1:
  At epsilon=1.0 the Q-table picks a random strategy each decision point,
  but A* still finds optimal paths and the energy model still computes exact
  budgets. If the agent randomly picks "seek nearest" several times in a row
  (statistically likely — it is 1 of 4 choices) it runs the greedy-nearest
  algorithm, which achieves 18–21 minerals on this map. The Q-table's job is
  to learn when to deviate from greedy-nearest — heading home early, taking
  a higher-value detour — not to learn how to navigate at all.


────────────────────────────────────────────────────────────────────────────────
TRAINING PIPELINE
────────────────────────────────────────────────────────────────────────────────

Four improvements over vanilla Q-learning:

  1. ELIGIBILITY TRACES (Q-λ, λ=0.7)
     The Q-table decision at tick 1 earns a mineral reward that only arrives
     at tick 9 (after 8 steps of A* navigation). Standard Q-learning only
     updates the decision tick. Eligibility traces broadcast the TD error
     backward through all recent decisions, weighted by recency (λ^n).
     λ=0: pure Q-learning. λ=1: full Monte Carlo. λ=0.7: best of both.

  2. PRIORITIZED EXPERIENCE REPLAY (PER)
     Transitions are sampled proportionally to |TD error|^0.6 instead of
     uniformly. High-surprise transitions — unexpected minerals, battery
     deaths, failed returns — dominate the replay batch and are learned
     from more often. Zero-surprise transitions of already-learned states
     are sampled rarely, saving compute.

  3. LEARNING RATE DECAY
     Alpha starts at 0.3 (fast early learning) and decays toward 0.02 over
     training. Prevents late-stage oscillation where correct Q-values get
     overwritten by noisy outlier transitions after the policy has converged.

  4. PARALLELIZATION
     Episodes are collected in batches of 4 using Parallel.For. Each thread
     has its own agent and map clone; all threads read the shared Q-table
     but none write during collection. After all threads finish, a serial
     merge phase applies eligibility traces and fills the PER buffer.
     Result: approximately 3.5× throughput on 4 threads.

Best-checkpoint saving:
  The Q-table is cloned every time a new best mineral count is achieved
  during training. The saved model is the best-ever snapshot, not the final
  one. If training degrades in later episodes (which happens with PER as
  rare bad transitions get replayed) the best policy is preserved.
  The deployment simulation uses the in-memory best table directly —
  no disk round-trip, no chance of serialisation mismatch.

Resume:
  Running again with the same model path picks up where training left off.
  Epsilon is restored from the saved metadata and continues decaying.


────────────────────────────────────────────────────────────────────────────────
RESULTS — COMPETITION MAP
────────────────────────────────────────────────────────────────────────────────

  Theoretical maximum (every mineral adjacent, ideal geometry):  23 minerals
  Real-map optimum (A* exact, corrected speed-to-distance):      21 minerals
  Our trained agent:                                             21 minerals

  Agent vs real-map optimum:   100%
  Agent vs physics ceiling:     91%

The agent achieves the real-map optimum. The gap from 23 to 21 is entirely
due to map geometry — only 5 minerals exist within 3 steps of start, and
the next cluster is 7 steps away. The physics ceiling of 23 assumes infinite
adjacent minerals which this map does not have.
────────────────────────────────────────────────────────────────────────────────
REWARD SHAPING
────────────────────────────────────────────────────────────────────────────────

  Event                   Reward
  ─────────────────────   ──────
  Mineral collected       +150
  Returned to base        +300
  Battery died            -300
  Failed to return        -200  (only if battery alive — avoids double penalty)
  Critical battery (<5%)   -50
  Low battery (<10%)       -15
  Night move               -0.5
  Standby during day        -2
  Each mineral at end      +10  (terminal bonus, scaled)


────────────────────────────────────────────────────────────────────────────────
WHAT WAS TRIED AND DISCARDED
────────────────────────────────────────────────────────────────────────────────

These files remain in the repo as a record of the development process:

  QLearningAgent.cs
    Pure tabular Q-learning over the full (x, y, battery, direction) state
    space. The state space was too large to converge in a reasonable number
    of episodes. Replaced by HybridAgent with reduced strategic state space.

  DQNAgent.cs + NeuralNetwork.cs
    Deep Q-Network with a feedforward neural net replacing the Q-table.
    Architecture: 14 input floats → 128 hidden → 64 hidden → 4 outputs.
    Slower to converge than the hybrid tabular approach on this problem size
    and more sensitive to hyperparameter choices. Superseded by HybridAgent.

  ReplayBuffer.cs
    Uniform-sampling replay buffer. Replaced by PrioritizedReplayBuffer
    which samples high-surprise transitions more often.


────────────────────────────────────────────────────────────────────────────────
DEVELOPMENT ENVIRONMENT
────────────────────────────────────────────────────────────────────────────────

  Language:   C# (.NET 8)
  IDE:        Visual Studio 2022
  GUI:        Avalonia 11 (separate MarsRover.UI project, not required to run)

================================================================================