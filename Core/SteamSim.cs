using Il2Cpp;
using Il2CppTLD.SteamTrain;
using UnityEngine;

namespace SteamRailway
{
    internal enum Realism { Simple, Medium }

    /// Everything that is saved for one locomotive.
    internal sealed class SteamSimulationState
    {
        public float BoilerWater { get; set; } = SteamSimulation.BoilerCapacity * SteamSimulation.StartBoilerFillFraction;   // litres
        public float TenderWater { get; set; } = SteamSimulation.TenderCapacity * SteamSimulation.StartTenderFillFraction;   // litres
        public float BoilerTemp { get; set; } = float.NaN;                         // °C, NaN = ambient on first run
        public float Regulator { get; set; }                                       // 0..1
        public int Reverser { get; set; }                                          // -1, 0, 1
        /// Which way the engine stands on its line: +1 chimney towards the line's end, -1 turned round.
        public int Facing { get; set; } = 1;
        public bool SteamBrake { get; set; } = true;
        public bool HandBrake { get; set; } = true;
        public bool Injector { get; set; }
        public bool Blower { get; set; }
        public bool Headlight { get; set; }                                        // lamps (need steam for the generator)
        public bool CabLight { get; set; }                                         // the small ceiling lamp in the cab
        public bool BedOpen { get; set; }                                          // the folding bed is unfolded
        public bool Pump { get; set; }
        public float SnowKg { get; set; }                                          // snow in the tender, melting
        public float SnowOnTop { get; set; }                                       // 0..1 snow settled on the roof, tank tops, running boards
        public float SnowOnFront { get; set; }                                     // 0..1 snow crust on the forward faces
        public float BoilerCondition { get; set; } = SteamSimulation.FullConditionPercentage;   // %
        public float ValveCondition { get; set; } = SteamSimulation.FullConditionPercentage;    // % safety valve
        public bool PlugBlown { get; set; }                                        // fusible plug melted
        public bool Exploded { get; set; }
        public float FireCoal { get; set; }                                        // obsolete (own coal fire)
        public float FireLevel { get; set; }                                       // 0 .. 1 how well the fire burns
        public bool FireLit { get; set; }
        public double LastGameHours { get; set; } = -1;
    }

    /// The coal steam locomotive model. The boiler runs on game time (so it keeps working while sleeping or
    /// passing time); motion runs on real time. Called once per frame from the patched SteamTrainEngine.UpdateEngine.
    internal sealed class SteamSimulation
    {
        // --- boiler
        public const float StartBoilerFillFraction = 0.7f;
        public const float StartTenderFillFraction = 0.5f;
        public const float FullConditionPercentage = 100f;
        public const float BoilerCapacity = 600f;       // L
        public const float TenderCapacity = 2000f;      // L
        const float HeatRate = 2.8f;                    // °C/game-min with a full fire at the nominal water level
        const float NominalWater = 420f;                // L
        const float CoolingPerMin = 0.004f;             // fraction of (T - ambient) lost per game minute
        const float PsiPerDegree = 2f;                  // gauge pressure = (T - 100) * 2
        public const float MaxPressure = 180f;          // safety valve
        const float SteamCoolingPerUnit = 6f;           // °C/min per unit of steam drawn (at nominal water)
        const float WaterPerSteamUnit = 1.5f;           // L/min per unit of steam drawn
        const float SafetyValveWaterPerMin = 0.5f;
        const float InjectorFlow = 6f;                  // L/min
        const float InjectorMinPsi = 40f;
        const float InjectorCooling = 0.5f;             // °C/min
        const float AutoFeedLevel = 0.7f;               // Simple mode keeps the boiler around 70 %
        const float LowWaterLevel = 0.05f;
        const float MaximumStepInGameMinutes = 5f;
        const double MinutesPerHourExact = 60.0;
        const float SecondsPerMinute = 60f;
        const float BlowerDraftBonus = 0.4f;
        const float ExhaustDraftGain = 0.3f;
        const float ExhaustDraftSpeedOffset = 0.2f;
        const float FireDoorOpenHeatFactor = 0.7f;
        const float MinimumWaterForHeatInLitres = 30f;
        const float SteamDrawBase = 0.3f;
        const float SteamDrawFullPressureInPsi = 60f;
        const float InjectorFullLevel = 0.98f;
        const float BoilingTemperatureInCelsius = 100f;
        const float SnowMeltStartTemperatureInCelsius = 40f;
        const float SnowMeltTemperatureRangeInCelsius = 110f;
        const float SnowMeltAmbientRangeInCelsius = 10f;
        const float ColdFirebox = 20f;
        const float FireboxTemperatureRange = 560f;
        const float FireboxDraftNormalization = 1.4f;
        const float FireOutLevel = 0.005f;
        const float DryExplosionPressureInPsi = 80f;
        const float FatigueExplosionPressureInPsi = 60f;
        const float ExplodedValveConditionLimit = 20f;
        const float ExplodedBoilerTemperatureInCelsius = 60f;
        const float TractionMinimumPressureInPsi = 5f;
        const float GravityInMetersPerSecondSquared = 9.81f;
        const float LineEndToleranceInMeters = 0.01f;
        const float MovementDirectionThreshold = 0.001f;
        const float TurnRoundMaximumSpeedInMetersPerSecond = 0.1f;
        const float ReverserMaximumSpeedInMetersPerSecond = 0.5f;
        const float RegulatorNotchesPerFull = 10f;
        const float PumpFullTankToleranceInLitres = 0.5f;
        const float PumpStoppedSpeedInMetersPerSecond = 0.1f;

        // --- water supply
        public const float SnowCapacityKg = 300f;
        public const float SnowPerShovelKg = 10f;
        const float SnowMeltPerMin = 2f;                // kg/min when the boiler is at working temperature
        public const float PumpFlow = 15f;              // L/min
        public const float ShovelMinutesPerKg = 0.1f;   // game minutes to shovel 1 kg of snow
        public const float PumpMinPsi = 30f;
        /// Below PumpMinPsi the pump is worked by hand: slower, but it needs no steam.
        public const float HandPumpFlow = 4f;           // L/min
        const float PumpCooling = 1f;                   // °C/min (the pump runs on boiler steam)

        // --- motion (real seconds)
        public const float MaxSpeed = 15f;              // m/s
        const float MaxTractionAccel = 0.8f;            // m/s² at full pressure and full regulator
        /// The locomotive with its tender (the traction and brake figures are for it alone).
        public const float LocomotiveMassInKilograms = 70000f;
        const float RollingResistance = 0.05f;          // m/s²
        const float AirResistance = 0.002f;             // × v²
        const float SteamBrakeDecel = 1.5f;
        const float SteamBrakeMinPsi = 20f;
        const float HandBrakeDecel = 0.8f;

        public readonly SteamTrain Train;
        public readonly SteamSimulationState State;

        public float Pressure { get; private set; }
        public float Velocity { get; private set; }
        public bool SafetyValveOpen { get; private set; }
        public bool InjectorWorking { get; private set; }
        public bool PumpWorking { get; private set; }
        public float WaterSourceDistance { get; set; } = float.PositiveInfinity;
        public float FireHeat { get; private set; }     // 0 (cold) .. ~1.4 (roaring fire with draft)
        public float Draft { get; private set; } = 1f;

        // --- firebox: the game's own Fire (a hidden stove behind the fire hole). The player lights and feeds it
        // through the normal fire menus; its heat output drives the boiler and the draft makes it burn faster.
        public Fire Fire { get; set; }
        public WoodStove Stove { get; set; }
        const float FullFireTemp = 60f;                 // fire temperature increase (°C) of a full firebox (3 coal)
        const float FireResponseMin = 6f;               // minutes for the fire bed to follow the fire

        public bool FireDoorOpen => Stove != null && Stove.m_Open;
        public bool FireBurning => Fire != null && Fire.IsBurning();
        /// Firebox temperature for the heat dial (deg C); it follows the fire's intensity slowly.
        public float FireboxTemp => float.IsNaN(m_FireboxTemp) ? TargetFireboxTemp : m_FireboxTemp;
        float TargetFireboxTemp => ColdFirebox + FireboxTemperatureRange * Mathf.Clamp01(State.FireLevel * Draft / FireboxDraftNormalization);
        float m_FireboxTemp = float.NaN;
        const float FireboxTempMinutes = 4f;              // time constant of the firebox temperature

        /// The turbo generator runs on boiler steam: the lamps light only with some pressure.
        public const float LampPressure = 15f;
        public bool LampsLit => State.Headlight && Pressure > LampPressure;

        public bool LockerOpen
        {
            get
            {
                var container = Train.m_Container;
                if (container == null) return false;
                if (container.m_OpenInProgress) return true;
                var panel = InterfaceManager.GetPanel<Panel_Container>();
                return panel != null && panel.IsEnabled() && panel.m_Container == container;
            }
        }
        public float BoilerLevel => State.BoilerWater / BoilerCapacity;
        public static Realism Mode => SteamRailwayMod.RealismMode;

        public SteamSimulation(SteamTrain train, SteamSimulationState state)
        {
            Train = train;
            State = state ?? new SteamSimulationState();
            if (State.Reverser == 0 && State.Regulator == 0) Velocity = 0f;
        }

        // ------------------------------------------------------------------ per frame

        public void Tick()
        {
            double hours = GameManager.GetTimeOfDayComponent().GetHoursPlayedNotPaused();
            if (State.LastGameHours < 0 || hours < State.LastGameHours) State.LastGameHours = hours;
            float gameMinutes = (float)((hours - State.LastGameHours) * MinutesPerHourExact);
            State.LastGameHours = hours;

            // Long time skips (sleep, pass time, loading) are simulated in steps so nothing overshoots.
            while (gameMinutes > 0f)
            {
                float step = Mathf.Min(gameMinutes, MaximumStepInGameMinutes);
                TickBoiler(step);
                SnowCover.Advance(State, step, Velocity);
                gameMinutes -= step;
            }
            TickMotion(Time.deltaTime);
            PushToEngine();
            Animator?.Update(this, Time.deltaTime);
            Audio?.Update(this);
            Smoke?.Update(this, Time.deltaTime);
            FireboxControl.UpdateEffects(this);
            BoilerRepair.Poll();
        }

        public LocomotiveAnimator Animator { get; set; }
        public TrainAudio Audio { get; set; }
        public LocoSmoke Smoke { get; set; }
        public FireEmbers Embers { get; set; }

        void TickBoiler(float minutes)
        {
            float ambient = GameManager.GetWeatherComponent().GetCurrentTemperatureWithoutHeatSources();
            if (float.IsNaN(State.BoilerTemp)) State.BoilerTemp = ambient;

            float draft = 1f;
            if (Mode == Realism.Medium && State.Blower) draft += BlowerDraftBonus;
            // A working engine pulls air through the fire with its exhaust; an open fire door wastes heat.
            draft += ExhaustDraftGain * State.Regulator * Mathf.Clamp01(Mathf.Abs(Velocity) / MaxSpeed + ExhaustDraftSpeedOffset);
            Draft = draft;
            TickFire(minutes, draft);
            if (float.IsNaN(m_FireboxTemp)) m_FireboxTemp = TargetFireboxTemp;
            m_FireboxTemp += (TargetFireboxTemp - m_FireboxTemp) * Mathf.Clamp01(minutes / FireboxTempMinutes);
            bool burning = FireBurning;
            FireHeat = State.FireLevel * draft * (FireDoorOpen ? FireDoorOpenHeatFactor : 1f);

            float waterFactor = NominalWater / Mathf.Max(State.BoilerWater, MinimumWaterForHeatInLitres);
            float deltaTime = FireHeat * HeatRate * waterFactor * minutes;
            deltaTime -= (State.BoilerTemp - ambient) * CoolingPerMin * minutes;

            // Steam drawn by the cylinders.
            float steam = 0f;
            if (Pressure > 0f && State.Regulator > 0f && State.Reverser != 0)
                steam = State.Regulator * (SteamDrawBase + Mathf.Abs(Velocity) / MaxSpeed) * Mathf.Clamp01(Pressure / SteamDrawFullPressureInPsi);
            deltaTime -= steam * SteamCoolingPerUnit * waterFactor * minutes;
            State.BoilerWater -= steam * WaterPerSteamUnit * minutes;

            // Feed water.
            bool feed = Mode == Realism.Simple ? BoilerLevel < AutoFeedLevel : State.Injector;
            InjectorWorking = feed && State.TenderWater > 0f && BoilerLevel < InjectorFullLevel;
            if (InjectorWorking)
            {
                float flow = Mathf.Min(InjectorFlow * minutes, State.TenderWater, BoilerCapacity - State.BoilerWater);
                State.TenderWater -= flow;
                State.BoilerWater += flow;
                deltaTime -= InjectorCooling * minutes;
            }

            // Snow in the tender melts with the boiler's waste heat (and above freezing, with the air).
            if (State.SnowKg > 0f)
            {
                float heat = Mathf.Clamp01((State.BoilerTemp - SnowMeltStartTemperatureInCelsius) / SnowMeltTemperatureRangeInCelsius) + Mathf.Clamp01(ambient / SnowMeltAmbientRangeInCelsius);
                float melt = Mathf.Min(State.SnowKg, SnowMeltPerMin * heat * minutes, TenderCapacity - State.TenderWater);
                State.SnowKg -= melt;
                State.TenderWater += melt;
            }

            State.BoilerTemp += deltaTime;

            // A blown plug or a burst boiler cannot hold steam.
            if (State.PlugBlown || State.Exploded) State.BoilerTemp = Mathf.Min(State.BoilerTemp, BoilingTemperatureInCelsius);

            // Safety valve: the boiler cannot go above the temperature of the valve's opening pressure.
            // A worn valve (Medium) opens late.
            float maxTemp = BoilingTemperatureInCelsius + ValveOpeningPressure / PsiPerDegree;
            SafetyValveOpen = State.BoilerTemp > maxTemp;
            if (SafetyValveOpen)
            {
                State.BoilerTemp = maxTemp;
                State.BoilerWater -= SafetyValveWaterPerMin * minutes;
                if (Mode == Realism.Medium) State.ValveCondition = Mathf.Max(0f, State.ValveCondition - ValveWearPerMin * minutes);
            }

            State.BoilerWater = Mathf.Max(0f, State.BoilerWater);
            Pressure = Mathf.Max(0f, (State.BoilerTemp - BoilingTemperatureInCelsius) * PsiPerDegree);

            if (Mode == Realism.Simple) TickSimpleFailures(burning);
            else TickMediumFailures(burning, ambient, minutes);
        }

        // ------------------------------------------------------------------ firebox

        void TickFire(float minutes, float draft)
        {
            float target = 0f;
            if (FireBurning)
            {
                target = Mathf.Clamp01(Fire.GetCurrentTempIncrease() / FullFireTemp);
                // Extra draft (blower, exhaust) burns the fuel faster.
                if (draft > 1f) Fire.ReduceDurationByTODSeconds((draft - 1f) * minutes * SecondsPerMinute);
            }
            State.FireLit = FireBurning;
            State.FireLevel += (target - State.FireLevel) * Mathf.Clamp01(minutes / FireResponseMin);
            // Only snap to zero when the fire is really out (per-frame steps are tiny, so a threshold would trap the rise).
            if (target <= 0f && State.FireLevel < FireOutLevel) State.FireLevel = 0f;
        }

        public void ExtinguishFire()
        {
            if (FireBurning) Fire.TurnOff();
            State.FireLit = false;
            State.FireLevel = 0f;
        }

        // ------------------------------------------------------------------ failures

        const float ValveWearPerMin = 0.5f;             // % per game minute of venting
        const float ValveLateOpeningMax = 0.7f;         // a dead valve opens at +70 % pressure
        const float OverpressureStrainPerMin = 2f;      // boiler % per minute at double pressure
        const float PlugLevel = 0.15f;
        const float PlugTemp = 115f;
        const float PlugDamage = 20f;
        const float ExplosionPressure = 260f;

        public float ValveOpeningPressure => Mode == Realism.Simple
            ? MaxPressure
            : MaxPressure * (1f + (1f - State.ValveCondition / FullConditionPercentage) * ValveLateOpeningMax);

        /// Simple mode: running the boiler dry just drops the fire.
        void TickSimpleFailures(bool burning)
        {
            if (BoilerLevel < LowWaterLevel && burning)
            {
                ExtinguishFire();
                CabControls.Message(Localizer.Translate("Kazanda su kalmadı: ateş söndü.", "The boiler ran dry: the fire went out."));
            }
        }

        void TickMediumFailures(bool burning, float ambient, float minutes)
        {
            if (State.Exploded)
            {
                if (burning) ExtinguishFire();
                return;
            }

            // Water from a blown plug pours into the firebox and keeps the fire out.
            if (State.PlugBlown && burning)
            {
                ExtinguishFire();
                CabControls.Message(Localizer.Translate("Eriyen tapa onarılmadan ocak yanmaz.", "The firebox won't burn until the fusible plug is replaced."));
            }

            if (Pressure > MaxPressure)
                State.BoilerCondition -= (Pressure - MaxPressure) / MaxPressure * OverpressureStrainPerMin * minutes;

            // Fusible plug: melts before the crown sheet is exposed.
            if (!State.PlugBlown && BoilerLevel < PlugLevel && State.BoilerTemp > PlugTemp && burning)
            {
                State.PlugBlown = true;
                State.BoilerCondition -= PlugDamage;
                State.BoilerTemp = BoilingTemperatureInCelsius;
                Pressure = 0f;
                ExtinguishFire();
                CabControls.Message(Localizer.Translate("Eriyen tapa eridi! Buhar ocağa boşaldı ve ateşi söndürdü.", "The fusible plug melted! Steam poured into the firebox and put the fire out."));
                SteamRailwayMod.Log.Warning("Fusible plug blown.");
                return;
            }

            bool overpressure = Pressure > ExplosionPressure;
            bool dry = BoilerLevel < LowWaterLevel && Pressure > DryExplosionPressureInPsi;
            bool fatigued = State.BoilerCondition <= 0f && Pressure > FatigueExplosionPressureInPsi;
            if (overpressure || dry || fatigued) Explode(ambient);
            State.BoilerCondition = Mathf.Clamp(State.BoilerCondition, 0f, FullConditionPercentage);
        }

        void Explode(float ambient)
        {
            State.Exploded = true;
            State.BoilerCondition = 0f;
            State.ValveCondition = Mathf.Min(State.ValveCondition, ExplodedValveConditionLimit);
            State.BoilerWater = 0f;
            State.BoilerTemp = Mathf.Max(ambient, ExplodedBoilerTemperatureInCelsius);
            State.Regulator = 0f;
            Pressure = 0f;
            Velocity = 0f;
            ExtinguishFire();
            SteamRailwayMod.Log.Warning("Boiler explosion.");
            BoilerExplosion.Play(this);
        }

        void TickMotion(float deltaTime)
        {
            if (deltaTime <= 0f) return;
            float acceleration = 0f;

            float traction = MaxTractionAccel * State.Regulator * Mathf.Clamp01(Pressure / MaxPressure) * State.Reverser * MassShare;
            if (Pressure < TractionMinimumPressureInPsi) traction = 0f;
            acceleration += traction;

            // Gravity along the track (+Z of the train is forward).
            acceleration -= GravityInMetersPerSecondSquared * Train.transform.forward.y;

            float nextVelocity = Velocity;
            float resistance = RollingResistance + AirResistance * nextVelocity * nextVelocity;
            float brake = 0f;
            if (State.SteamBrake && Pressure >= SteamBrakeMinPsi) brake += SteamBrakeDecel;
            if (State.HandBrake) brake += HandBrakeDecel;
            brake *= MassShare;   // only the engine has brakes

            nextVelocity += acceleration * deltaTime;
            float slowdown = (resistance + brake) * deltaTime;
            if (Mathf.Abs(nextVelocity) <= slowdown) nextVelocity = 0f;
            else nextVelocity -= Mathf.Sign(nextVelocity) * slowdown;

            // Ends of the line (the engine's velocity is + chimney first; along the line it is times Facing).
            var splineController = Train.m_SplineController;
            if (splineController != null && splineController.Spline != null)
            {
                float lineVelocity = nextVelocity * FacingSign;
                if (splineController.Position <= LineEndToleranceInMeters && lineVelocity < 0f) nextVelocity = 0f;
                if (splineController.Position >= splineController.Spline.Length - LineEndToleranceInMeters && lineVelocity > 0f) nextVelocity = 0f;
            }
            Velocity = Mathf.Clamp(nextVelocity, -MaxSpeed, MaxSpeed);
        }

        public int FacingSign => State.Facing < 0 ? -1 : 1;

        /// Speed along the line (+ towards the line's end).
        public float LineVelocity => Velocity * FacingSign;

        /// The game's own code moves the train and drives the cockpit needles from the engine state, so the
        /// simulation feeds those values.
        void PushToEngine()
        {
            var engineState = Train.m_Engine.CurrentState;
            float lineVelocity = LineVelocity;
            engineState.m_Velocity = lineVelocity;
            // the game reads this sign as the way the train stands on the line, not as the sign of its speed
            engineState.m_LastVelocitySign = FacingSign;
            engineState.m_SteamPressure = Pressure;
            engineState.m_BrakesActive = State.SteamBrake || State.HandBrake;
            engineState.m_ThrottlePosition = State.Reverser < 0 ? Il2CppTLD.SteamTrain.ThrottlePosition.Reverse
                : State.Reverser > 0 ? Il2CppTLD.SteamTrain.ThrottlePosition.Low : Il2CppTLD.SteamTrain.ThrottlePosition.Neutral;

            var splineController = Train.m_SplineController;
            if (splineController != null && Mathf.Abs(lineVelocity) > MovementDirectionThreshold)
            {
                var movementDirection = lineVelocity < 0f ? Il2CppFluffyUnderware.Curvy.Controllers.MovementDirection.Backward
                                     : Il2CppFluffyUnderware.Curvy.Controllers.MovementDirection.Forward;
                if (splineController.MovementDirection != movementDirection) splineController.MovementDirection = movementDirection;
            }
            EnforceFacing();
        }

        /// The engine faces the way it stands (Facing), whatever direction it runs; applied before and after the
        /// game's own train code every frame.
        public void EnforceFacing()
        {
            var splineController = Train.m_SplineController;
            if (splineController == null) return;
            var axis = FacingSign > 0 ? Il2CppFluffyUnderware.Curvy.OrientationAxisEnum.Forward
                                      : Il2CppFluffyUnderware.Curvy.OrientationAxisEnum.Backward;
            if (splineController.OrientationAxis != axis) splineController.OrientationAxis = axis;
            if (!splineController.IgnoreDirection) splineController.IgnoreDirection = true;
            if (splineController.LockRotation) splineController.LockRotation = false;
            // the game sets the direction of travel from the facing every frame: in reverse it must run backwards
            float level = LineVelocity;
            if (Mathf.Abs(level) > MovementDirectionThreshold)
            {
                var movementDirection = level < 0f ? Il2CppFluffyUnderware.Curvy.Controllers.MovementDirection.Backward
                                  : Il2CppFluffyUnderware.Curvy.Controllers.MovementDirection.Forward;
                if (splineController.MovementDirection != movementDirection) splineController.MovementDirection = movementDirection;
            }
        }

        /// Turns the engine round on its line (a U-turn: wye, loop or turntable). Only while standing.
        public bool TurnRound()
        {
            if (Mathf.Abs(Velocity) > TurnRoundMaximumSpeedInMetersPerSecond) return false;
            // coupled wagons hang on the engine's line direction: they would be left running the wrong way
            if (TrailingMassInKilograms > 0f)
            {
                CabControls.Message(Localizer.Translate("Bağlı vagonla ters dönülemez: önce vagonu ayır.", "You can't turn round with a wagon coupled: uncouple it first."));
                return false;
            }
            State.Facing = -FacingSign;
            State.Reverser = 0;
            Velocity = 0f;
            EnforceFacing();
            return true;
        }

        // ------------------------------------------------------------------ controls

        public void RegulatorStep(float delta) => State.Regulator = Mathf.Clamp01(Mathf.Round((State.Regulator + delta) * RegulatorNotchesPerFull) / RegulatorNotchesPerFull);

        public void SetReverser(int reverserPosition)
        {
            // the reverser may only be thrown while (nearly) stopped
            if (Mathf.Abs(Velocity) > ReverserMaximumSpeedInMetersPerSecond && reverserPosition != 0 && reverserPosition != State.Reverser) return;
            State.Reverser = reverserPosition;
        }

        public float TenderSpace => TenderCapacity - State.TenderWater;

        public void PourInWater(float litres) => State.TenderWater = Mathf.Min(TenderCapacity, State.TenderWater + litres);

        public void AddSnow(float kilograms) => State.SnowKg = Mathf.Min(SnowCapacityKg, State.SnowKg + Mathf.Max(0f, kilograms));

        /// Why the steam pump cannot run now (null = it can).
        public static string PumpError(SteamSimulation simulation)
        {
            if (simulation.TenderSpace <= PumpFullTankToleranceInLitres) return Localizer.Translate("Su tankı dolu.", "The water tank is full.");
            if (Mathf.Abs(simulation.Velocity) >= PumpStoppedSpeedInMetersPerSecond) return Localizer.Translate("Pompa için tren durmalı.", "The train must be stopped to pump.");
            simulation.WaterSourceDistance = WaterSources.NearestDistance(simulation.Train.transform.position);
            if (simulation.WaterSourceDistance > WaterSources.HoseLength)
                return Localizer.Translate($"Hortum {WaterSources.HoseLength:0} m içinde açık suya (buz deliği, akarsu) ulaşmıyor.",
                           $"The hose can't reach open water (an ice hole or flowing water) within {WaterSources.HoseLength:0} m.");
            return null;
        }

        /// Steam pump when there is enough pressure, otherwise the hand pump.
        public bool PumpBySteam => Pressure >= PumpMinPsi;

        /// Water pumped in through the transfer menu (game time has already passed); the steam pump cools the boiler.
        public float PumpIn(float litres, bool bySteam)
        {
            litres = Mathf.Min(litres, TenderSpace);
            State.TenderWater += litres;
            if (!bySteam) return litres;
            float minutes = litres / PumpFlow;
            float waterFactor = NominalWater / Mathf.Max(State.BoilerWater, MinimumWaterForHeatInLitres);
            State.BoilerTemp -= PumpCooling * waterFactor * minutes;
            return litres;
        }

        public void RestoreMotion(float velocity) => Velocity = velocity;

        /// The wagons coupled to this engine (Wagons sets it every frame): a heavier train gathers speed and
        /// stops more slowly.
        public float TrailingMassInKilograms { get; set; }

        float MassShare => LocomotiveMassInKilograms / (LocomotiveMassInKilograms + Mathf.Max(0f, TrailingMassInKilograms));
    }
}
