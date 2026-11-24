using System.Collections.Generic;
using Il2Cpp;
using UnityEngine;

namespace SteamRailway
{
    /// Wildlife and the train: animals bolt from a locomotive that comes close (from further off when it is moving) and
    /// from its whistle; one standing on the line when the engine runs into it is killed.
    internal static class TrainWildlife
    {
        // scaring
        const float ScareRadiusMovingInMeters = 30f;
        const float MovingSpeedInMetersPerSecond = 0.5f;
        const float WhistleScareRadiusInMeters = 140f;
        // running over: the engine's swept box, in its own frame
        const float StrikeHalfWidthInMeters = 1.7f;
        const float StrikeLowestInMeters = -1f;
        const float StrikeHighestInMeters = 3.5f;
        const float StrikeLengthMarginInMeters = 0.3f;
        const float StrikeMinimumSpeedInMetersPerSecond = 1.2f;
        const float StrikeDamage = 100000f;
        const float DefaultBodyFrontInMeters = 7.7f;
        const float DefaultBodyRearInMeters = 6.5f;
        const string StrikeSound = "Play_BodyHitHeavy";
        // the animal list is gathered now and then (a full scene search), checked every few frames
        const float AnimalListRefreshInSeconds = 2f;
        const float CheckIntervalInSeconds = 0.1f;

        static readonly List<BaseAi> s_Animals = new List<BaseAi>();
        static float s_NextRefresh;
        static float s_NextCheck;

        public static void Update(List<LiveTrain> trains)
        {
            float now = Time.time;
            if (now < s_NextCheck) return;
            s_NextCheck = now + CheckIntervalInSeconds;
            if (now >= s_NextRefresh)
            {
                s_NextRefresh = now + AnimalListRefreshInSeconds;
                s_Animals.Clear();
                foreach (var animal in Object.FindObjectsOfType<BaseAi>()) s_Animals.Add(animal);
            }
            if (s_Animals.Count == 0) return;
            for (int index = 0; index < trains.Count; index++)
            {
                var liveTrain = trains[index];
                if (liveTrain.Train == null || liveTrain.Simulation == null) continue;
                Check(liveTrain);
            }
        }

        static void Check(LiveTrain liveTrain)
        {
            var root = liveTrain.Train.transform;
            var trainPosition = root.position;
            float speed = Mathf.Abs(liveTrain.Simulation.Velocity);
            bool moving = speed > MovingSpeedInMetersPerSecond;
            bool whistling = liveTrain.Train.m_SteamWhistleOpen;
            // a standing, quiet engine does not frighten anything (a hunting wolf keeps its eyes on the player)
            if (!moving && !whistling) return;
            float scareRadius = whistling ? WhistleScareRadiusInMeters : ScareRadiusMovingInMeters;
            float front = liveTrain.BodyFrontInMeters > 0f ? liveTrain.BodyFrontInMeters : DefaultBodyFrontInMeters;
            float rear = liveTrain.BodyRearInMeters > 0f ? liveTrain.BodyRearInMeters : DefaultBodyRearInMeters;
            for (int index = 0; index < s_Animals.Count; index++)
            {
                var animal = s_Animals[index];
                if (animal == null || !animal.isActiveAndEnabled) continue;
                var mode = animal.GetAiMode();
                if (mode == AiMode.Dead || mode == AiMode.Disabled || mode == AiMode.ScriptedSequence) continue;
                var animalPosition = animal.transform.position;
                var offset = animalPosition - trainPosition;
                if (offset.sqrMagnitude > scareRadius * scareRadius) continue;

                if (speed >= StrikeMinimumSpeedInMetersPerSecond)
                {
                    var local = root.InverseTransformPoint(animalPosition);
                    if (Mathf.Abs(local.x) <= StrikeHalfWidthInMeters && local.y >= StrikeLowestInMeters && local.y <= StrikeHighestInMeters
                        && local.z <= front + StrikeLengthMarginInMeters && local.z >= -rear - StrikeLengthMarginInMeters)
                    {
                        Strike(animal);
                        continue;
                    }
                }
                if (mode == AiMode.Flee || mode == AiMode.Struggle) continue;
                try { animal.FleeFrom(trainPosition); }
                catch (System.Exception exception) { SteamRailwayMod.Debug($"Wildlife flee failed: {exception.Message}"); }
            }
        }

        static void Strike(BaseAi animal)
        {
            try
            {
                TrainSounds.Play(StrikeSound, animal.gameObject);
                animal.ApplyDamage(StrikeDamage, DamageSource.Unspecified, "");
                SteamRailwayMod.Debug($"Train struck {animal.name}");
            }
            catch (System.Exception exception) { SteamRailwayMod.Debug($"Wildlife strike failed: {exception.Message}"); }
        }
    }
}
