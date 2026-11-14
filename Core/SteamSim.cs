using Il2Cpp;
using Il2CppTLD.SteamTrain;
using UnityEngine;

namespace SteamRailway
{
    internal enum Realism { Simple, Medium }

    /// Everything that is saved for one locomotive.
    internal sealed class SimState
    {
        public float BoilerWater { get; set; } = SteamSim.BoilerCapacity * 0.7f;   // litres
        public float TenderWater { get; set; } = SteamSim.TenderCapacity * 0.5f;   // litres
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
        public bool Pump { get; set; }
        public float SnowKg { get; set; }                                          // snow in the tender, melting
        public float BoilerCondition { get; set; } = 100f;                         // %
        public float ValveCondition { get; set; } = 100f;                          // % safety valve
        public bool PlugBlown { get; set; }                                        // fusible plug melted
        public bool Exploded { get; set; }
        public float FireCoal { get; set; }                                        // obsolete (own coal fire)
        public float FireLevel { get; set; }                                       // 0 .. 1 how well the fire burns
        public bool FireLit { get; set; }
        public double LastGameHours { get; set; } = -1;
    }

    /// The coal steam locomotive model. The boiler runs on game time (so it keeps working while sleeping or
    /// passing time); motion runs on real time. Called once per frame from the patched SteamTrainEngine.UpdateEngine.
    internal sealed class SteamSim
    {
        // --- boiler
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

        // --- water supply
        public const float SnowCapacityKg = 300f;
        public const float SnowPerShovelKg = 10f;
        const float SnowMeltPerMin = 2f;                // kg/min when the boiler is at working temperature
        public const float PumpFlow = 15f;              // L/min
        public const float ShovelMinutesPerKg = 0.1f;   // game minutes to shovel 1 kg of snow
        public const float PumpMinPsi = 30f;
        /// Below PumpMinPsi the pump is worked by hand: slower, but it needs no steam, so an empty boiler and an
        /// empty tank can always be refilled (otherwise firing a dry boiler for steam would burn it first).
        public const float HandPumpFlow = 4f;           // L/min
        const float PumpCooling = 1f;                   // °C/min (the pump runs on boiler steam)

        // --- motion (real seconds)
        public const float MaxSpeed = 15f;              // m/s
        const float MaxTractionAccel = 0.8f;            // m/s² at full pressure and full regulator
        const float RollingResistance = 0.05f;          // m/s²
        const float AirResistance = 0.002f;             // × v²
        const float SteamBrakeDecel = 1.5f;
        const float SteamBrakeMinPsi = 20f;
        const float HandBrakeDecel = 0.8f;

        public readonly SteamTrain Train;
        public readonly SimState S;

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
        /// Firebox temperature for the heat dial (deg C).
        /// Follows the fire's intensity slowly (the brick arch and the plates hold heat), so opening or closing the
        /// blower moves the dial gradually instead of in a jump.
        public float FireboxTemp => float.IsNaN(m_FireboxTemp) ? TargetFireboxTemp : m_FireboxTemp;
        float TargetFireboxTemp => 20f + 560f * Mathf.Clamp01(S.FireLevel * Draft / 1.4f);
        float m_FireboxTemp = float.NaN;
        const float FireboxTempMinutes = 4f;              // time constant of the firebox temperature

        /// The cab cupboard is open while its inventory panel shows it.
        /// The turbo generator runs on boiler steam: the lamps light only with some pressure.
        public const float LampPressure = 15f;
        public bool LampsLit => S.Headlight && Pressure > LampPressure;

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
        public float BoilerLevel => S.BoilerWater / BoilerCapacity;
        public static Realism Mode => SteamRailwayMod.RealismMode;

        public SteamSim(SteamTrain train, SimState state)
        {
            Train = train;
            S = state ?? new SimState();
            if (S.Reverser == 0 && S.Regulator == 0) Velocity = 0f;
        }

        // ------------------------------------------------------------------ per frame

        public void Tick()
        {
            double hours = GameManager.GetTimeOfDayComponent().GetHoursPlayedNotPaused();
            if (S.LastGameHours < 0 || hours < S.LastGameHours) S.LastGameHours = hours;
            float gameMinutes = (float)((hours - S.LastGameHours) * 60.0);
            S.LastGameHours = hours;

            // Long time skips (sleep, pass time, loading) are simulated in steps so nothing overshoots.
            while (gameMinutes > 0f)
            {
                float step = Mathf.Min(gameMinutes, 5f);
                TickBoiler(step);
                gameMinutes -= step;
            }
            TickMotion(Time.deltaTime);
            PushToEngine();
            Animator?.Update(this, Time.deltaTime);
            Audio?.Update(this);
            Smoke?.Update(this, Time.deltaTime);
            FireboxControl.UpdateEffects(this);
        }

        public LocomotiveAnimator Animator { get; set; }
        public TrainAudio Audio { get; set; }
        public LocoSmoke Smoke { get; set; }

        void TickBoiler(float minutes)
        {
            float ambient = GameManager.GetWeatherComponent().GetCurrentTemperatureWithoutHeatSources();
            if (float.IsNaN(S.BoilerTemp)) S.BoilerTemp = ambient;

            float draft = 1f;
            if (Mode == Realism.Medium && S.Blower) draft += 0.4f;
            // A working engine pulls air through the fire with its exhaust; an open fire door wastes heat.
            draft += 0.3f * S.Regulator * Mathf.Clamp01(Mathf.Abs(Velocity) / MaxSpeed + 0.2f);
            Draft = draft;
            TickFire(minutes, draft);
            if (float.IsNaN(m_FireboxTemp)) m_FireboxTemp = TargetFireboxTemp;
            m_FireboxTemp += (TargetFireboxTemp - m_FireboxTemp) * Mathf.Clamp01(minutes / FireboxTempMinutes);
            bool burning = FireBurning;
            FireHeat = S.FireLevel * draft * (FireDoorOpen ? 0.7f : 1f);

            float waterFactor = NominalWater / Mathf.Max(S.BoilerWater, 30f);
            float deltaTime = FireHeat * HeatRate * waterFactor * minutes;
            deltaTime -= (S.BoilerTemp - ambient) * CoolingPerMin * minutes;

            // Steam drawn by the cylinders.
            float steam = 0f;
            if (Pressure > 0f && S.Regulator > 0f && S.Reverser != 0)
                steam = S.Regulator * (0.3f + Mathf.Abs(Velocity) / MaxSpeed) * Mathf.Clamp01(Pressure / 60f);
            deltaTime -= steam * SteamCoolingPerUnit * waterFactor * minutes;
            S.BoilerWater -= steam * WaterPerSteamUnit * minutes;

            // Feed water.
            bool feed = Mode == Realism.Simple ? BoilerLevel < AutoFeedLevel : S.Injector;
            InjectorWorking = feed && S.TenderWater > 0f && BoilerLevel < 0.98f;
            if (InjectorWorking)
            {
                float flow = Mathf.Min(InjectorFlow * minutes, S.TenderWater, BoilerCapacity - S.BoilerWater);
                S.TenderWater -= flow;
                S.BoilerWater += flow;
                deltaTime -= InjectorCooling * minutes;
            }

            // Snow in the tender melts with the boiler's waste heat (and above freezing, with the air).
            if (S.SnowKg > 0f)
            {
                float heat = Mathf.Clamp01((S.BoilerTemp - 40f) / 110f) + Mathf.Clamp01(ambient / 10f);
                float melt = Mathf.Min(S.SnowKg, SnowMeltPerMin * heat * minutes, TenderCapacity - S.TenderWater);
                S.SnowKg -= melt;
                S.TenderWater += melt;
            }

            S.BoilerTemp += deltaTime;

            // A blown plug or a burst boiler cannot hold steam.
            if (S.PlugBlown || S.Exploded) S.BoilerTemp = Mathf.Min(S.BoilerTemp, 100f);

            // Safety valve: the boiler cannot go above the temperature of the valve's opening pressure.
            // A worn valve (Medium) opens late.
            float maxTemp = 100f + ValveOpeningPressure / PsiPerDegree;
            SafetyValveOpen = S.BoilerTemp > maxTemp;
            if (SafetyValveOpen)
            {
                S.BoilerTemp = maxTemp;
                S.BoilerWater -= SafetyValveWaterPerMin * minutes;
                if (Mode == Realism.Medium) S.ValveCondition = Mathf.Max(0f, S.ValveCondition - ValveWearPerMin * minutes);
            }

            S.BoilerWater = Mathf.Max(0f, S.BoilerWater);
            Pressure = Mathf.Max(0f, (S.BoilerTemp - 100f) * PsiPerDegree);

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
                if (draft > 1f) Fire.ReduceDurationByTODSeconds((draft - 1f) * minutes * 60f);
            }
            S.FireLit = FireBurning;
            S.FireLevel += (target - S.FireLevel) * Mathf.Clamp01(minutes / FireResponseMin);
            // Only snap to zero when the fire is really out (per-frame steps are tiny, so a threshold would trap the rise).
            if (target <= 0f && S.FireLevel < 0.005f) S.FireLevel = 0f;
        }

        public void ExtinguishFire()
        {
            if (FireBurning) Fire.TurnOff();
            S.FireLit = false;
            S.FireLevel = 0f;
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
            : MaxPressure * (1f + (1f - S.ValveCondition / 100f) * ValveLateOpeningMax);

        /// Simple mode: running the boiler dry just drops the fire.
        void TickSimpleFailures(bool burning)
        {
            if (BoilerLevel < LowWaterLevel && burning)
            {
                ExtinguishFire();
                CabControls.Message(L.T("Kazanda su kalmadı: ateş söndü.", "The boiler ran dry: the fire went out."));
            }
        }

        void TickMediumFailures(bool burning, float ambient, float minutes)
        {
            if (S.Exploded)
            {
                if (burning) ExtinguishFire();
                return;
            }

            // Water from a blown plug pours into the firebox and keeps the fire out.
            if (S.PlugBlown && burning)
            {
                ExtinguishFire();
                CabControls.Message(L.T("Eriyen tapa onarılmadan ocak yanmaz.", "The firebox won't burn until the fusible plug is replaced."));
            }

            if (Pressure > MaxPressure)
                S.BoilerCondition -= (Pressure - MaxPressure) / MaxPressure * OverpressureStrainPerMin * minutes;

            // Fusible plug: melts before the crown sheet is exposed.
            if (!S.PlugBlown && BoilerLevel < PlugLevel && S.BoilerTemp > PlugTemp && burning)
            {
                S.PlugBlown = true;
                S.BoilerCondition -= PlugDamage;
                S.BoilerTemp = 100f;
                Pressure = 0f;
                ExtinguishFire();
                CabControls.Message(L.T("Eriyen tapa eridi! Buhar ocağa boşaldı ve ateşi söndürdü.", "The fusible plug melted! Steam poured into the firebox and put the fire out."));
                SteamRailwayMod.Log.Warning("Fusible plug blown.");
                return;
            }

            bool overpressure = Pressure > ExplosionPressure;
            bool dry = BoilerLevel < LowWaterLevel && Pressure > 80f;
            bool fatigued = S.BoilerCondition <= 0f && Pressure > 60f;
            if (overpressure || dry || fatigued) Explode(ambient);
            S.BoilerCondition = Mathf.Clamp(S.BoilerCondition, 0f, 100f);
        }

        void Explode(float ambient)
        {
            S.Exploded = true;
            S.BoilerCondition = 0f;
            S.ValveCondition = Mathf.Min(S.ValveCondition, 20f);
            S.BoilerWater = 0f;
            S.BoilerTemp = Mathf.Max(ambient, 60f);
            S.Regulator = 0f;
            Pressure = 0f;
            Velocity = 0f;
            ExtinguishFire();
            SteamRailwayMod.Log.Warning("Boiler explosion.");
            BoilerExplosion.Play(this);
        }

        void TickMotion(float deltaTime)
        {
            if (deltaTime <= 0f) return;
            float firstValue = 0f;

            float traction = MaxTractionAccel * S.Regulator * Mathf.Clamp01(Pressure / MaxPressure) * S.Reverser;
            if (Pressure < 5f) traction = 0f;
            firstValue += traction;

            // Gravity along the track (+Z of the train is forward).
            firstValue -= 9.81f * Train.transform.forward.y;

            float value = Velocity;
            float resist = RollingResistance + AirResistance * value * value;
            float brake = 0f;
            if (S.SteamBrake && Pressure >= SteamBrakeMinPsi) brake += SteamBrakeDecel;
            if (S.HandBrake) brake += HandBrakeDecel;

            value += firstValue * deltaTime;
            float slow = (resist + brake) * deltaTime;
            if (Mathf.Abs(value) <= slow) value = 0f;
            else value -= Mathf.Sign(value) * slow;

            // Ends of the line (velocity is the engine's own: + chimney first; along the line it is v * Facing).
            var splineController = Train.m_SplineController;
            if (splineController != null && splineController.Spline != null)
            {
                float lineV = value * FacingSign;
                if (splineController.Position <= 0.01f && lineV < 0f) value = 0f;
                if (splineController.Position >= splineController.Spline.Length - 0.01f && lineV > 0f) value = 0f;
            }
            Velocity = Mathf.Clamp(value, -MaxSpeed, MaxSpeed);
        }

        public int FacingSign => S.Facing < 0 ? -1 : 1;

        /// Speed along the line (+ towards the line's end).
        public float LineVelocity => Velocity * FacingSign;

        /// The game's own code moves the train along the spline with the engine's velocity and drives the
        /// cockpit needles from its pressure, so the simulation feeds those values.
        void PushToEngine()
        {
            var engineState = Train.m_Engine.CurrentState;
            float lineV = LineVelocity;
            engineState.m_Velocity = lineV;
            // The game reads this sign as the way the train stands on the line (it turns the model round for -1),
            // so it is the engine's facing, not the sign of its speed.
            engineState.m_LastVelocitySign = FacingSign;
            engineState.m_SteamPressure = Pressure;
            engineState.m_BrakesActive = S.SteamBrake || S.HandBrake;
            engineState.m_ThrottlePosition = S.Reverser < 0 ? Il2CppTLD.SteamTrain.ThrottlePosition.Reverse
                : S.Reverser > 0 ? Il2CppTLD.SteamTrain.ThrottlePosition.Low : Il2CppTLD.SteamTrain.ThrottlePosition.Neutral;

            var splineController = Train.m_SplineController;
            if (splineController != null && Mathf.Abs(lineV) > 0.001f)
            {
                var dir = lineV < 0f ? Il2CppFluffyUnderware.Curvy.Controllers.MovementDirection.Backward
                                     : Il2CppFluffyUnderware.Curvy.Controllers.MovementDirection.Forward;
                if (splineController.MovementDirection != dir) splineController.MovementDirection = dir;
            }
            EnforceFacing();
        }

        /// The engine faces the way it stands (Facing), whatever direction it runs; the game's own train code
        /// also turns and locks the rotation, so this is applied before and after it every frame.
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
            if (Mathf.Abs(level) > 0.001f)
            {
                var dir = level < 0f ? Il2CppFluffyUnderware.Curvy.Controllers.MovementDirection.Backward
                                  : Il2CppFluffyUnderware.Curvy.Controllers.MovementDirection.Forward;
                if (splineController.MovementDirection != dir) splineController.MovementDirection = dir;
            }
        }

        /// Turns the engine round on its line (a U-turn: wye, loop or turntable). Only while standing.
        public bool TurnRound()
        {
            if (Mathf.Abs(Velocity) > 0.1f) return false;
            S.Facing = -FacingSign;
            S.Reverser = 0;
            Velocity = 0f;
            EnforceFacing();
            return true;
        }

        // ------------------------------------------------------------------ controls

        public void RegulatorStep(float delta) => S.Regulator = Mathf.Clamp01(Mathf.Round((S.Regulator + delta) * 10f) / 10f);

        public void SetReverser(int dir)
        {
            // The reverser may only be thrown while (nearly) stopped.
            if (Mathf.Abs(Velocity) > 0.5f && dir != 0 && dir != S.Reverser) return;
            S.Reverser = dir;
        }

        public float TenderSpace => TenderCapacity - S.TenderWater;

        public void PourInWater(float litres) => S.TenderWater = Mathf.Min(TenderCapacity, S.TenderWater + litres);

        public void AddSnow(float kilograms) => S.SnowKg = Mathf.Min(SnowCapacityKg, S.SnowKg + Mathf.Max(0f, kilograms));

        /// Why the steam pump cannot run now (null = it can).
        public static string PumpError(SteamSim simulation)
        {
            if (simulation.TenderSpace <= 0.5f) return L.T("Su tankı dolu.", "The water tank is full.");
            if (Mathf.Abs(simulation.Velocity) >= 0.1f) return L.T("Pompa için tren durmalı.", "The train must be stopped to pump.");
            simulation.WaterSourceDistance = WaterSources.NearestDistance(simulation.Train.transform.position);
            if (simulation.WaterSourceDistance > WaterSources.HoseLength)
                return L.T($"Hortum {WaterSources.HoseLength:0} m içinde açık suya (buz deliği, akarsu) ulaşmıyor.",
                           $"The hose can't reach open water (an ice hole or flowing water) within {WaterSources.HoseLength:0} m.");
            return null;
        }

        /// Steam pump when there is enough pressure, otherwise the hand pump.
        public bool PumpBySteam => Pressure >= PumpMinPsi;

        /// Water pumped in through the transfer menu (game time has already passed); the steam pump cools the boiler.
        public float PumpIn(float litres, bool bySteam)
        {
            litres = Mathf.Min(litres, TenderSpace);
            S.TenderWater += litres;
            if (!bySteam) return litres;
            float minutes = litres / PumpFlow;
            float waterFactor = NominalWater / Mathf.Max(S.BoilerWater, 30f);
            S.BoilerTemp -= PumpCooling * waterFactor * minutes;
            return litres;
        }

        public void RestoreMotion(float velocity) => Velocity = velocity;
    }
}
