# ChemFlow.NET

[![.NET CI](https://github.com/yancsalmeida/ChemFlow.NET/actions/workflows/ci.yml/badge.svg)](https://github.com/yancsalmeida/ChemFlow.NET/actions/workflows/ci.yml)
[![.NET Version](https://img.shields.io/badge/.NET-8.0-512BD4.svg?logo=dotnet&logoColor=white)](https://dotnet.microsoft.com/)
[![SignalR](https://img.shields.io/badge/SignalR-WebSockets%20Streaming-512BD4?logo=dotnet&logoColor=white)](#real-time-telemetry--signalr-streaming)
[![Thermodynamics](https://img.shields.io/badge/EOS-Peng--Robinson%20(1976)-00599C.svg)](#thermodynamics--process-physics)
[![Native C11 FFI](https://img.shields.io/badge/C11_FFI-ReactorCore.C-00599C?logo=c)](https://github.com/yancsalmeida/ReactorCore.C)
[![Tests](https://img.shields.io/badge/Tests-127%20Passed%20(100%25)-2ea44f.svg)](#automated-testing)
[![Docker](https://img.shields.io/badge/Docker-ready-2496ED.svg?logo=docker&logoColor=white)](docker-compose.yml)
[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](LICENSE)

An enterprise-grade **Chemical Process Simulation, Stiff ODE Solver, and Distributed Software-in-the-Loop (SIL) Control Platform** built with **C# 12** and **.NET 8** following **Clean Architecture** principles.

It models multi-component non-ideal vapor-liquid equilibrium (Peng-Robinson EOS and Raoult's Law), rigorous MESH distillation (`Wang-Henke`), DAG flowsheet topology and balance closure, **6-State Extended Kalman Filtering (`SeriesParallelEkf6`) + Economic NMPC (`SeriesParallelEconomicNmpc`)**, native **L-stable `ROS3L(2)` stiff integration** backed by [`ReactorCore.C`](https://github.com/yancsalmeida/ReactorCore.C) (`libreactorcore.so` with C# fallback), **Distributed Modbus TCP SIL control** paired with [`ProcessTwin.Py`](https://github.com/yancsalmeida/ProcessTwin.Py) streaming live measurements into [`IndustrialTelemetry.Engine`](https://github.com/yancsalmeida/IndustrialTelemetry.Engine), real-time **SignalR WebSockets**, and **SQLite** persistence.

![ChemFlow.NET — Process Simulation & Thermodynamics Studio](docs/chemflow-studio-preview.png)

---

## Architecture (Clean Architecture, SIL & Historian Pipeline)

```mermaid
flowchart TD
    subgraph Client["Web Studio & Consumers"]
        Browser["Interactive Web Studio (wwwroot/index.html)"]
        WS["SignalR WebSocket Clients (/hubs/simulation)"]
    end

    subgraph API["ChemFlow.Api (.NET 8 Minimal APIs + Hosted SIL)"]
        Endpoints["REST Endpoints (/api/thermo, /api/simulations, /api/telemetry, /api/supervisory)"]
        SIL["SupervisoryModbusControllerService\n(EKF6 + e-NMPC over Modbus TCP MBAP)"]
        Pub["HttpHistorianTelemetryPublisher\n(POST /api/telemetry/ingest)"]
        Hub["SignalR Hub (CSTR, MESH & SIL Ticks)"]
    end

    subgraph Domain["ChemFlow.Domain"]
        Thermo["Peng-Robinson EOS, Raoult, Rachford-Rice, Cardano"]
        Units["Mixer, Heater, FlashDrum, CSTR, DistillationColumn"]
        Control["SeriesParallelEkf6 & SeriesParallelEconomicNmpc"]
        Native["NativeReactorCoreInterop (RK4, RKF45, SIMD Batch, Stiff ROS3L + C# Fallback)"]
        DAG["ProcessFlowsheet (Kahn Sort, Wegstein Tear Streams, DOT, Balance Closure)"]
    end

    subgraph External["Distributed Ecosystem"]
        C11["ReactorCore.C (libreactorcore.so FFI)"]
        Twin["ProcessTwin.Py PLC (Modbus TCP :5020)"]
        Hist["IndustrialTelemetry.Engine (:5100 -> PostgreSQL 16)"]
    end

    subgraph Infra["ChemFlow.Infrastructure"]
        Sqlite["SQLite + ModbusTcpClient (MBAP FC 0x01/0x03/0x05/0x06)"]
    end

    subgraph Tests["ChemFlow.Tests (xUnit)"]
        UnitTests["127 Automated Unit & API Integration Tests"]
    end

    Browser --> Endpoints
    Browser --> WS
    Endpoints --> Domain
    SIL --> Control
    SIL --> Infra
    SIL --> Hub
    SIL --> Pub
    Pub --> Hist
    Control --> Native
    Native -.->|"P/Invoke"| C11
    Infra <==>|"Modbus TCP"| Twin
    UnitTests -->|"Verifies"| API
    UnitTests -->|"Verifies"| Domain
```

### Project Structure

```text
ChemFlow.NET/
├── src/
│   ├── ChemFlow.Domain/           # Pure domain models, thermodynamics, unit operations, DAG engine & balance reports
│   ├── ChemFlow.Infrastructure/   # Relational SQLite persistence with schema migration (ADO.NET / Microsoft.Data.Sqlite)
│   └── ChemFlow.Api/              # ASP.NET Core Minimal API endpoints, topology/report queries & OpenAPI/Swagger
└── tests/
    └── ChemFlow.Tests/            # Automated unit & integration test suite (xUnit & WebApplicationFactory)
```

---

## Thermodynamics & Process Physics

### 1. Peng-Robinson Cubic Equation of State (PR-EOS 1976)
For non-ideal, high-pressure, and hydrocarbon phase equilibria, `PengRobinsonThermodynamicPackage` evaluates real-fluid behavior:
$$P = \frac{RT}{v - b} - \frac{a(T)}{v(v + b) + b(v - b)}$$

- **Cardano Analytical Cubic Solver:** Solves the cubic compressibility equation $Z^3 - (1-B)Z^2 + (A - 3B^2 - 2B)Z - (AB - B^2 - B^3) = 0$, discriminating between physical liquid ($Z_L$) and vapor ($Z_V$) roots.
- **Fugacity Coefficients ($\phi_i^L, \phi_i^V$):** Evaluates non-ideal component fugacities using van der Waals mixing rules:
  $$\ln \phi_i = \frac{b_i}{b}(Z - 1) - \ln(Z - B) - \frac{A}{2\sqrt{2}B} \left( \frac{2 \sum_j z_j a_{ij}}{a} - \frac{b_i}{b} \right) \ln\left( \frac{Z + (1+\sqrt{2})B}{Z + (1-\sqrt{2})B} \right)$$
- **Equilibrium Ratios:** Computes rigorous non-ideal $K$-values ($K_i = \phi_i^L / \phi_i^V$) converged via damped successive substitution.
- **Enthalpy Departure Functions:** Computes real fluid thermal enthalpy: $H(T, P) = H^{\text{ideal}}(T) + H^{\text{dep}}(T, P)$.

### 2. Antoine Vapor Pressure & Raoult's Law (Ideal VLE)
Saturation pressure $P_i^{\text{sat}}(T)$ is evaluated using NIST/DIPPR-style Antoine equations:
$$\log_{10}(P_i^{\text{sat}}) = A_i - \frac{B_i}{T + C_i}$$
Equilibrium $K$-values follow Raoult's Law ($K_i = P_i^{\text{sat}}(T)/P$), establishing fast analytical Bubble Point ($P_{\text{bubble}}, T_{\text{bubble}}$) and Dew Point ($P_{\text{dew}}, T_{\text{dew}}$) solvers.

### 3. Safeguarded Rachford-Rice Isothermal Flash
The isothermal vapor fraction $\beta = V / F$ is determined by solving the objective function:
$$f(\beta) = \sum_{i=1}^{N} \frac{z_i (K_i - 1)}{1 + \beta (K_i - 1)} = 0$$
using a safeguarded Newton-Raphson solver with automatic fallback to bisection root-finding upon boundary excursion. Subcooled liquid ($f(0) \le 0 \implies \beta = 0$) and superheated vapor ($f(1) \ge 0 \implies \beta = 1$) conditions are resolved analytically.

### 4. Unit Operations & Directed Acyclic Graph (DAG) Flowsheets
- **`MaterialStream`**: Tracks molar and mass flow rates ($\dot{m}$ in $\text{kg/s}$), temperature, pressure, mole fractions, phase state, vapor fraction $\beta$, and specific/total enthalpy.
- **`Mixer`**: Adiabatic multi-stream mixing with rigorous component mass conservation and enthalpy balance root-finding for outlet temperature.
- **`HeaterCooler`**: Sensible and phase-change thermal duty ($\dot{Q}$ in $\text{kW}$) calculation for target temperatures and pressures.
- **`FlashDrum`**: Isothermal/isobaric vapor-liquid phase separator.
- **`DistillationColumn` (Rigorous RadFrac / MESH Solver):** Multicomponent staged fractionation column solving the decoupled MESH (Material, Equilibrium, Summation, and Heat) equations via the **Wang-Henke Bubble Point (BP)** algorithm. Employs the **Thomas Algorithm (TDMA)** to invert tridiagonal component material balance matrices in $O(N)$ operations with guaranteed non-negative M-matrix property ($x_{j,i}^* \ge 0$). Enforces machine-precision overall component and total mass closure ($\Delta m / m < 10^{-14}$) via **Holland's $\theta$-Method**, calculates damped stage temperature profiles, and computes rigorous condenser cooling ($Q_C < 0$) and reboiler heating ($Q_R > 0$) enthalpy duties.
- **`Splitter`**: Stream divider partitioning a single inlet into multiple outlets according to fractional splits ($\sum \alpha_k = 1.0$), preserving $(T, P, z_i)$.
- **`ConversionReactor`**: Stoichiometric reactor with limiting-reactant validation, fractional conversion, and heat of reaction ($\Delta H_{\text{rxn}}^\circ$) energy balance.
- **`ContinuousStirredTankReactor`**: Dynamic CSTR unit operation with non-linear Arrhenius kinetics and jacket heat exchange ($UA(T - T_c)$), integrated via 4th-Order Runge-Kutta (RK4) powered natively by [**`ReactorCore.C`**](https://github.com/yancsalmeida/ReactorCore.C) (`libreactorcore.so`) through C-ABI P/Invoke.
- **`ProcessFlowsheet`**: Solves the execution sequence using **Kahn's Topological Sort Algorithm** ($O(V + E)$). Supports closed-loop recycles via **Tear Stream Cutting** and **Wegstein Bounded Secant Convergence Acceleration** ($q \in [-5.0, 0.0]$) with automatic feedback cycle identification.
- **Flowsheet Topology & Graphviz DOT (`ToDot()`):** Automatically compiles the process graph into Graphviz DOT syntax and JSON node/edge schemas for visualization, rendering tear/recycle loops with dashed purple edges.
- **Mass & Energy Balance Closure (`GenerateBalanceReport()`):** Verifies global conservation across plant boundary feeds and products ($\Delta m / m < 10^{-5}$, $\Delta H / H < 5 \times 10^{-4}$), component species conservation, and individual unit operation energy balance closure.

---

## REST API Specification

| Method | Endpoint | Description |
| :--- | :--- | :--- |
| `GET` | `/api/compounds` | Query registered chemical compounds and Antoine coefficients |
| `GET` | `/api/compounds/{id}` | Retrieve specific compound physical properties |
| `GET` | `/api/thermo/models` | List available thermodynamic packages (`Peng-Robinson` vs `Raoult`) |
| `POST` | `/api/thermo/flash` | Solve isothermal $(T, P)$ flash with selected thermodynamic package |
| `POST` | `/api/thermo/txy` | Generate binary $T\text{-}x\text{-}y$ phase equilibrium isobaric curves |
| `POST` | `/api/simulations/benchmark` | Execute and persist flash separation flowsheet (`Mixer` $\rightarrow$ `Heater` $\rightarrow$ `FlashDrum`) |
| `POST` | `/api/simulations/recycle` | Solve closed-loop recycle flowsheet (`Mixer` $\rightarrow$ `FlashDrum` $\rightarrow$ `Splitter` $\rightarrow$ `Mixer`) with Wegstein acceleration |
| `POST` | `/api/simulations/distillation` | Rigorous multicomponent staged distillation column (`Wang-Henke BP MESH` & `Thomas Algorithm`) returning stage profiles and duties |
| `POST` | `/api/simulations/series-parallel-plant` | Closed-loop Series-Parallel CSTR ($A\to B\to C,\ A\to D$) + dual distillation recycle convergence |
| `POST` | `/api/telemetry/cstr/step` | Single-step exothermic CSTR ODE integration via native C11 RK4 or managed fallback |
| `POST` | `/api/telemetry/cstr/series-parallel/step` | Single-step 6-state Series-Parallel CSTR integration with jacket temperature $T_j$ |
| `POST` | `/api/telemetry/cstr/series-parallel/profile` | Compares nominal selective regime vs thermal runaway trajectory |
| `POST` | `/api/telemetry/cstr/series-parallel/closed-loop` | 6-state EKF + Economic NMPC disturbance rejection (`useStiffRos3Solver` option) |
| `POST` | `/api/telemetry/cstr/series-parallel/solver-benchmark` | Benchmarks Explicit RK4 vs L-stable C11 `ROS3L(2)` under severe stiff thermal runaway |
| `GET` | `/api/supervisory/modbus/status` | Live Distributed SIL controller & Historian publisher telemetry (`Host`, `StepsExecuted`, `HistorianMeasurementsPublished`) |
| `POST` | `/api/supervisory/modbus/start` | Starts live C# EKF6 + e-NMPC Modbus TCP SIL loop (`PublishToHistorian` streaming into PostgreSQL) |
| `POST` | `/api/supervisory/modbus/disturbance` | Injects hot-feed temperature / target-$C_B$ / weight tuning mid-flight during live SIL control |
| `POST` | `/api/supervisory/modbus/stop` | Stops the Modbus TCP SIL supervisory control loop |
| `GET` | `/api/simulations` | Retrieve historical simulation runs from SQLite |
| `GET` | `/api/simulations/{id}` | Retrieve detailed stream results for a simulation run |
| `GET` | `/api/simulations/{id}/topology` | Export flowsheet graph topology (JSON or Graphviz DOT via `?format=dot`) |
| `GET` | `/api/simulations/{id}/report` | Retrieve mass and energy balance closure report (closure errors and conservation metrics) |

### Real-Time Telemetry & SignalR Streaming

ChemFlow.NET exposes an **ASP.NET Core SignalR Hub** mapped at `/hubs/simulation` supporting bidirectional real-time streaming:

| Hub Method / Event | Protocol | Description |
| :--- | :--- | :--- |
| `StreamCstrSimulation` | `IAsyncEnumerable<CstrTelemetryFrameDto>` | Dynamic CSTR time-series telemetry streaming at 5–10 Hz via native C11 FFI (`ReactorCore.C`). |
| `StreamSeriesParallelCstrSimulation` | `IAsyncEnumerable<SeriesParallelTelemetryFrameDto>` | Real-time 6-state Series-Parallel CSTR nominal vs runaway streaming. |
| `SupervisoryModbusTick` | Server-to-Client Push | Broadcasts real-time Modbus TCP SIL control steps, Modbus RTT, EKF6 residuals, and e-NMPC valve commands. |
| `SolveDistillationWithProgress` | Duplex RPC + Callback | Executes rigorous Wang-Henke MESH distillation column while streaming `OnDistillationIteration` progress. |

---

## Quickstart

### 1. Run via Docker Compose (Recommended)

Requires [Docker](https://docs.docker.com/get-docker/):

```bash
docker compose up -d --build
```

- **Interactive Web Studio:** [http://localhost:5080](http://localhost:5080)
- **OpenAPI / Swagger UI:** [http://localhost:5080/swagger](http://localhost:5080/swagger)

### 2. Run Locally via .NET CLI

Requires [.NET 8.0 SDK](https://dotnet.microsoft.com/download/dotnet/8.0):

```bash
# Clone the repository
git clone https://github.com/yancsalmeida/ChemFlow.NET.git
cd ChemFlow.NET

# Restore and Build
dotnet build ChemFlow.sln

# Run API server
dotnet run --project src/ChemFlow.Api
```

---

## Automated Testing

Execute the comprehensive xUnit test suite:

```bash
dotnet test ChemFlow.sln --configuration Release
```

```text
Passed!  - Failed:     0, Passed:   127, Skipped:     0, Total:   127
```

---

## Author

**Yan Almeida**
- LinkedIn: [linkedin.com/in/yancsa](https://www.linkedin.com/in/yancsa/)
- GitHub: [github.com/yancsalmeida](https://github.com/yancsalmeida)
