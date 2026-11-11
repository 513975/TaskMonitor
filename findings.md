# Power Telemetry Findings

## Baseline before this implementation iteration

- `src/Sampling/PowerSampler.cs` reads Windows `Energy Meter` performance counters matching its
  `RAPL_Package..._<domain>` parser and reads `Power Meter\\Power\\_Total` when present.
- RAPL values are treated as milliwatts and converted to watts; no counter metadata validation is
  performed. Counter discovery retries when no provider is found, but does not periodically
  re-enumerate while at least one counter remains open.
- CPU package RAPL is marked measured. Per-core allocation uses PP0 or package watts apportioned by
  core CPU use; process watts use package watts apportioned by process CPU share. Both are estimates.
- Battery flow is a separate Windows battery-side measurement. During battery discharge, the
  sampler publishes it as an estimated system value. It does not treat charging power as system
  consumption.
- `GpuSampler` reads GPU utilization and temperature but contains no GPU watt field/provider.
  `PowerSnapshot` has no per-GPU power field. An exposed RAPL PP1 domain may appear as a generic
  “CPU graphics domain” component; it is not correlated to an adapter.
- Settings currently offer battery, CPU package, or system power as the taskbar source. The default
  battery source can show `0.0W` at battery idle, which is not whole-PC power.
- Existing docs already describe battery/system selection and distinguish estimated system power:
  `docs/architecture.md`, `docs/settings-plumbing.md`.

## Official interface research

- Microsoft documents EMI as a standardized Windows interface that drivers can implement; its
  existence does not imply that every consumer platform exposes a meter. Source:
  https://learn.microsoft.com/en-us/windows-hardware/drivers/powermeter/energy-meter-interface
- AMD's Ryzen Master Monitoring SDK provides read-only processor metrics including PPT in watts;
  AMD says its `GetCPUParameters` call should be limited to once per second. The page lists SDK
  version 3.1.1.5478, released July 1, 2026, for Windows 10/11 x64. Source:
  https://www.amd.com/en/developer/ryzen-master-monitoring-sdk.html
- NVIDIA NVML's `nvmlDeviceGetPowerUsage` reports GPU power in milliwatts, including associated
  circuitry such as memory, but returns NOT_SUPPORTED on unsupported devices and depends on power
  management support. On Ampere or newer GPUs (except GA100) it reports a 1-second average. NVML is
  installed with supported Windows NVIDIA drivers, but NVIDIA notes limited support for GeForce
  consumer GPUs, so the RTX 4060 Laptop result must be probed on the actual OEM/driver.
  Sources:
  https://docs.nvidia.com/deploy/nvml-api/latest/api/group__nvmlDeviceQueries.html
  https://docs.nvidia.com/deploy/nvml-api/nvml-api-reference.html
- AMD ADLX exposes separate GPU power and total board power metrics and explicit per-metric support
  checks. Source:
  https://gpuopen.com/manuals/adlx/adlx-sdk-references/adlx-interfaces/performance-monitoring/iadlxgpumetrics/
## User's RTX 4060 laptop screenshot

- Screenshot values: CPU Package 23.48 W, CPU IA cores 14.96 W, CPU GT cores 8.52 W, GPU 12.56 W,
  GPU TDP 0%, GPU 12VHPWR 9.55 W, battery connected to AC.
- In this sample, IA + GT exactly equals Package. AIDA64 staff explain CPU Package includes IA cores
  and, when available, GT/iGPU cores. The component rows must not be added to Package.
  Source: https://forums.aida64.com/topic/4853-cant-find-cpu-total-consumption/
- Intel's SDM says RAPL domains are platform-specific; client platforms may expose Package, PP0 and
  PP1, with PP1 potentially reflecting uncore devices. This supports separate domain readings but
  does not guarantee the same domains on every laptop.
  Source: https://www.intel.com/content/dam/www/public/us/en/documents/manuals/64-ia-32-architectures-software-developer-vol-3b-part-2-manual.pdf
- NVIDIA's official docs describe NVML's Windows library location and its limited support for newer
  GeForce consumer GPUs. `nvmlDeviceGetPowerUsage` returns GPU plus associated circuitry (e.g.
  memory) in mW; it may return NOT_SUPPORTED and on Ampere+ normally reports a 1-second average.
  RTX 4060 Laptop must be tested on the actual driver/OEM configuration.
  Sources:
  https://docs.nvidia.com/deploy/nvml-api/nvml-api-reference.html
  https://docs.nvidia.com/deploy/nvml-api/latest/api/group__nvmlDeviceQueries.html
- AIDA64's manual documents optional low-level kernel-driver, MSR, PCI, SMBus, and sensor operations
  and notes some can cause stability issues. Matching its access method therefore entails more than
  calling one generic Windows API.
  Source: https://www.aida64.com/user-manual/preferences/stability
- AIDA64 release discussion states RTX 3000/4000 GPU sensor monitoring was enhanced for 12VHPWR in
  v7.20. The screenshot label alone cannot establish whether the laptop has a physical 12VHPWR
  connector or whether this is a distinct non-overlapping power domain; do not add it to GPU watts.
  Sources:
  https://www.aida64.com/news/finalwire-aida64-v720-dark-mode-nvidia-4050-4060-4070-4080-4090
  https://aida64.com/user-manual/hardware-monitoring/external-applications/complete-sensor-value-list

## Publicly documented AIDA64 source paths

- AIDA64 documents a kernel driver and selectable low-level MSR, PCI, SMBus, and sensor operations.
  It also lists device-specific paths such as NVIDIA GPU SMBus access through ForceWare, SMBus via
  ACPI for Asus motherboards, Asus ATKEX, and Dell SMI. This is a collection of hardware-specific
  acquisition paths, not one Windows-wide sensor API.
  Source: https://www.aida64.com/user-manual/preferences/stability
- Intel documents RAPL energy status registers for package and subdomains. AIDA64's CPU Package
  value is confirmed by its staff to include IA and, when available, GT/iGPU. The likely source for
  those Intel CPU power rows is RAPL energy counters read through the low-level CPU path, with power
  derived from energy over elapsed time; AIDA64 does not publish a per-sensor implementation map,
  so the exact AIDA64 read path is not independently confirmed.
  Sources:
  https://cdrdv2-public.intel.com/874240/325462-090-sdm-vol-1-2abcd-3abcd-4.pdf
  https://forums.aida64.com/topic/4853-cant-find-cpu-total-consumption/
- AIDA64 staff explicitly stated that NVIDIA GPU power draw values were obtained through NVML API.
  That is a confirmed historical source path for GPU power draw; the later RTX 3000/4000 12VHPWR and
  input-rail readings were announced as enhanced GPU sensor monitoring, but AIDA64 has not publicly
  documented the exact transport/register path for each of those rows.
  Sources:
  https://forums.aida64.com/topic/3958-gpu-power-draw-and-power-state-nvml-api/
  https://www.aida64.com/news/finalwire-aida64-v720-dark-mode-nvidia-4050-4060-4070-4080-4090
- AIDA64's public FAQ cautions that sensor-chip register layouts are not standardized and readings
  can be inaccurate when a board mapping is wrong. Motherboard/OEM telemetry therefore needs
  chipset, EC, SMBus, or vendor-specific mappings and model-by-model validation.
  Source: https://www.aida64.com/support/faq
- AIDA64's Battery page exposes power-source/battery status; its public documentation describes the
  battery page but does not specify the exact Windows API for the screenshot's AC state. Treat it
  as OS/firmware-reported status unless a sensor dump establishes a more specific path.
  Source: https://www.aida64.com/user-manual/system-information/computer

## Design implications

- No single software API guarantees all component power sensors on arbitrary consumer hardware.
  Build capability-gated provider adapters and publish explicit unsupported reasons.
- Do not equate CPU package power with CPU core-only power, GPU chip power with total board power,
  battery discharge with AC input, or CPU+GPU sum with whole-system input.
- Validate source units, update interval, domain definition, and whether a reading is instantaneous or
  averaged for each provider.
- Keep driver and sensor-provider failures isolated from existing utilization/temperature samplers.
- Hardware validation needs real Intel and AMD CPUs, NVIDIA/AMD GPUs, integrated graphics cases,
  AC/battery transitions, and comparison instruments/tools.
- For this specific RTX 4060 laptop, first enumerate Windows Energy Meter domains and query the
  installed NVIDIA NVML power API. Compare both live readings to AIDA64 under iGPU/dGPU idle and load
  before choosing a default provider or claiming equivalence.
- The user reports that their installed general-purpose hardware monitor does not expose the specific
  power readings they need; do not plan on a generic monitoring library as the source of those
  readings. Validate direct Windows and vendor telemetry on the target device instead.
- For this specific RTX 4060 laptop, first enumerate Windows Energy Meter domains and query the
  supported NVIDIA driver telemetry APIs. Compare with AIDA64 under iGPU/dGPU idle and load before
  choosing a provider or claiming equivalence.

## First implementation iteration (2026-11-10)

- Removed Windows `Power Meter\\Power\\_Total`, battery-backed system estimates, their rolling
  stats, and utilization-proportional per-core/per-process watt estimates.
- Kept CPU Package as its own RAPL value, available RAPL child domains as separate rows, and
  battery charge/discharge as battery-side flow. The CPU detail still shows per-core utilization.
- Added a dynamic NVML reader using only the NVIDIA driver's `nvml.dll`. It runs on a dedicated
  worker, refreshes device inventory at a low cadence, and reports each NVML UUID separately.
  NVML devices are intentionally not guessed onto DXCore GPU tabs via index or name ordering.
- NVML remains optional and has not been tested against the user's actual GeForce RTX 4060 laptop.
  The API may be unsupported by a given GeForce/OEM driver; the UI then reports unavailable.
- Build validation: Debug succeeded after fixes with 0 warnings and 0 errors. Real-hardware support,
  NVML DLL discovery on the user's driver, AIDA64 comparison, integrated-graphics power, and idle
  resource impact remain unverified.
- NVML enumeration now tolerates a missing UUID export, reports the exact missing required entry,
  and preserves a per-device unavailable row when one handle cannot be opened. The project opts
  out of Prefer32Bit so a 64-bit Windows run can load the matching driver library.
- Read-only review confirmed the current coverage boundary: NVIDIA NVML only for discrete GPU
  power. There is no Intel iGPU-specific provider or AMD GPU/CPU provider in this iteration; RAPL
  PP1 can appear only when the Windows Energy Meter exposes that CPU graphics domain. Actual GPU
  matching against AIDA64, dGPU idle behavior, sleep/resume, and resource cost await the two
  target laptops.

## Second implementation iteration (2026-11-10)

- Independent NVML review found no confirmed ABI, unit-conversion, or candidate-path defect.
  NVIDIA's current documentation lists the same standard driver library locations used by the
  implementation and recommends UUID/PCI identity over enumeration order. NVML still has limited
  support on GeForce consumer devices, so actual RTX 4060 support remains a hardware check.
- When a driver omits UUID, the GPU row now explicitly marks the ordinal identity as temporary,
  including unsupported/error states. The power page states that NVIDIA GPU rows use NVML,
  Intel RAPL graphics domains may appear separately below when exposed by Windows, and AMD GPU
  power is not yet integrated. The unavailable notice is scoped specifically to CPU Package so
  available GPU readings do not conflict with a generic "no power sensor" message.
- RAPL instances now re-enumerate once per minute on the hardware-poll worker. This addresses
  provider/counter changes while avoiding enumeration on every 1-second sample.
- Read-only probe of the current development machine found these Windows Energy Meter instances:
  `RAPL_Package0_PKG`, `RAPL_Package0_PP0`, `RAPL_Package0_PP1`, and
  `RAPL_Package0_DRAM`. One 1.1-second probe sampled approximately 2.63 W Package, 0.97 W PP0,
  0.95 W PP1, and 0.41 W DRAM. This confirms those counters are accessible on the current
  machine, but a single sample under an uncontrolled workload is not an accuracy comparison with
  AIDA64 and does not establish which GPU adapter PP1 corresponds to.
- WMI hardware identity queries were denied in the current sandbox, so the exact laptop, CPU, GPU,
  Windows build, and driver versions remain unconfirmed from this session.

## CPU Package interruption investigation (2026-11-11)

- The 1-minute RAPL inventory scan previously disposed and recreated every counter even when the
  instance list had not changed. This resets PerformanceCounter baselines and can introduce a
  short Package gap on each scan.
- The sampler also read all RAPL domains in one unguarded loop. One child-domain exception
  (PP1/DRAM) escaped to the outer handler, which released every counter and delayed the entire
  source for 10 seconds, including Package.
- The inventory scan now preserves unchanged handles, leaves existing handles intact when a scan
  transiently returns no RAPL instances, and updates added/removed instances without rebuilding
  the entire set. Each counter opens and retries independently; read exceptions and repeated
  invalid samples no longer tear down unrelated domains.
- For brief Package counter interruptions, the latest good Package reading is retained for at most
  three seconds and labeled "暂时沿用上次读数" with its original measurement time. Stale samples
  are excluded from Package history, averages, peak, and energy integration.
- A second code review caught an unconditional `ReleaseCounters()` still at the start of the
  inventory routine, which made the intended reconciliation ineffective. It was removed before
  final validation. The CPU taskbar row now also shows `--` during the brief stale fallback, since
  that compact UI cannot label the value as stale.

## CPU+GPU headline and GPU history (2026-11-11)

- The detail-page headline now has a separate, limited meaning: CPU Package plus all currently
  fresh and measured NVML GPU readings. It excludes battery power and is not a whole-system value.
  If CPU Package is stale/unavailable, NVML is stale, or any enumerated GPU row is unsupported or
  failed, the headline remains unavailable rather than summing a partial set.
- The GPU chart line uses that same complete NVML aggregate, sampled into the existing bounded
  history queue; unsupported/stale samples become gaps. Per-device GPU readings remain listed
  separately below. Intel RAPL PP1 is not included in this sum because it may already be part of
  CPU Package.

## Automatic taskbar source and integrated-GPU semantics (2026-11-11)

- The Windows battery-state result already includes `AcOnLine`, independently of the charge/discharge
  flags. Automatic taskbar selection now follows `AcOnLine`: online selects core total, and confirmed
  offline requires a present, discharging battery before selecting battery-side flow. Unknown AC
  state or unavailable source data renders `--`.
- This specifically handles hybrid-power laptops: `Discharging` while AC remains online does not
  switch the taskbar to battery power.
- DXCore `IsIntegrated` and `InstanceLuid` are now retained in `GpuInfo`. Confirmed iGPU-only Intel
  topology uses RAPL CPU Package as core total; PP1 is not added because it may already be contained
  in Package. A detected discrete adapter requires a fresh, measured NVML row for every distinct
  DXCore discrete adapter. Unknown topology or count mismatch leaves the aggregate unavailable.
- Automatic is the default, with manual Battery, CPU Package and Core Total overrides retained.
  Legacy `System` settings still migrate to the default. UI, YAML, architecture, gotcha and settings
  plumbing descriptions were updated.
- Build and real-device validation for this change are pending.

## Detail statistics and power-chart audit (2026-11-11)

- Intel RAPL exposes distinct Package and PP0 power domains; depending on the processor platform, Package may also include integrated graphics and other uncore domains. They are different scopes and are not expected to match or be added. PP0 is the CPU cores domain; Package is the broader package domain.
- Screenshot chart root cause: `PowerHistoryChart.GetPlotRect` reserves 42 DIP on the left, but `DrawGrid` right-aligns watt labels at x=0. Those labels are drawn outside the control and clipped, leaving the reserved axis gutter blank. Keep a readable gutter and move the label anchor inside it; `HitTest` and series use the same plot rectangle.
- Current statistics row is CPU Package rolling average, rolling peak, and Package-only accumulated energy. The requested core energy must be a new accumulator based on the topology-validated `CoreTotalWatts`, not the existing Package energy.
- CPU and GPU peak statistics should be documented as a rolling max of the bounded 60-sample histories (30/60/120 seconds at 500/1000/2000 ms); GPU peak is the complete NVML GPU aggregate history and may be unavailable when any NVML device lacks a valid reading.
- Core energy should use trapezoidal integration of fresh measured CoreTotal samples. If topology or a source becomes unavailable/stale, keep the accumulated Wh but clear the previous sample baseline so the gap is never extrapolated.
- Power sampling disable/re-enable currently skips adding nulls to chart histories; the existing plotted data and rolling peaks can appear current after a long pause. Re-enable should clear the bounded display histories/peak window and integration baselines, while preserving session accumulated energy.
