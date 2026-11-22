using System.Collections.Generic;
using UnityEngine;

namespace SteamRailway
{
    /// Ready-made bends for the track editor: a circular arc of a chosen radius and angle, entered and left through
    /// easement curves (the curvature rises and falls evenly), so a train never meets a sudden change of direction.
    internal static class CurveTemplates
    {
        public static readonly float[] Radii = { 60f, 100f, 150f, 250f, 400f };
        public const float MinAngle = 5f;
        public const float MaxAngle = 180f;
        public const float AngleStep = 5f;
        public const float FastAngleStep = 15f;
        const float EasementLength = 40f;
        const float PointSpacing = 1f;

        /// The bend's points in the ground plane (start first) and the direction it leaves in.
        /// turnRight: the bend turns to the right of the starting direction.
        public static List<Vector3> Build(Vector3 start, Vector3 startDirection, float radius, float angleDegrees, bool turnRight, out Vector3 endDirection, out float length)
        {
            float angle = angleDegrees * Mathf.Deg2Rad;
            float easement = Mathf.Min(Mathf.Min(EasementLength, 0.5f * radius), 0.5f * angle * radius);
            float arcLength = angle * radius - easement;
            length = arcLength + 2f * easement;

            float turnSign = turnRight ? 1f : -1f;
            var points = new List<Vector3> { start };
            var direction = startDirection;
            var position = start;
            int stepCount = Mathf.Max(2, Mathf.CeilToInt(length / PointSpacing));
            float step = length / stepCount;
            for (int index = 0; index < stepCount; index++)
            {
                float distanceAlong = (index + 0.5f) * step;
                float curvature = CurvatureAt(distanceAlong, length, easement) / radius;
                position += direction * step;
                direction = Quaternion.AngleAxis(turnSign * curvature * step * Mathf.Rad2Deg, Vector3.up) * direction;
                points.Add(position);
            }
            endDirection = direction;
            return points;
        }

        /// 0 .. 1 of the full curvature: rising along the first easement, level on the arc, falling along the last.
        static float CurvatureAt(float distanceAlong, float length, float easement)
        {
            if (easement <= 0f) return 1f;
            if (distanceAlong < easement) return distanceAlong / easement;
            if (distanceAlong > length - easement) return (length - distanceAlong) / easement;
            return 1f;
        }
    }
}
