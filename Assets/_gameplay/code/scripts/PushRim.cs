using System.Collections.Generic;
using UnityEngine;

namespace Delirium.Wheelchair
{
    /// <summary>
    /// One wheel of the chair. Owns the wheel's angular velocity, which persists between frames.
    /// Gripping hands pull that velocity toward their own speed around the hub (with limited grip,
    /// so a fast chair can slip through a hand). Released wheels coast under rolling resistance.
    ///
    /// Optional "hold to keep moving": while gripping, how far the hand has turned the rim since the
    /// grab sets a cruise speed, so pushing and then holding keeps the chair rolling without more strokes.
    /// Returning the hand toward where it grabbed slows the cruise; pulling past it reverses.
    ///
    /// Sign convention: positive angular velocity = top of the wheel moving forward = chair rolling forward.
    /// Ticked by <see cref="WheelchairController"/>, never by itself.
    /// </summary>
    public class PushRim : MonoBehaviour
    {
        public static readonly List<PushRim> All = new List<PushRim>();

        [Header("Geometry")]
        [Tooltip("Centre of the wheel. Defaults to this transform.")]
        [SerializeField] Transform hub;

        [Tooltip("The mesh that spins. Its pivot must sit on the hub. Rotated about the chair's right axis.")]
        [SerializeField] Transform spinVisual;

        [Tooltip("Radius of the hand rim - where hands grip (m).")]
        [SerializeField] float rimRadius = 0.27f;

        [Tooltip("Radius of the tyre where it touches the ground (m). Converts wheel spin into chair speed.")]
        [SerializeField] float tireRadius = 0.30f;

        [Header("Grip")]
        [Tooltip("Multiplies how fast the hand drives the wheel. 1 = the rim moves exactly with your hand (realistic). " +
                 "2 = a push of the same arm speed rolls the chair twice as fast. A still hand still brakes at any value.")]
        [SerializeField] float pushGain = 1f;

        [Tooltip("How hard one gripping hand can speed up or brake the wheel (rad/s^2). Lower = more slip through the hand.")]
        [SerializeField] float gripAcceleration = 30f;

        [Tooltip("Smoothing on measured hand speed, filters tracking jitter (1/s). Higher = snappier, noisier.")]
        [SerializeField] float handSpeedSmoothing = 25f;

        [Tooltip("Measured hand speed around the hub is clamped to this (rad/s), so a tracking glitch can't fling the chair.")]
        [SerializeField] float maxHandAngularSpeed = 20f;

        [Header("Weight")]
        [Tooltip("How much slip (m/s at the rim) it takes before the hand has full purchase on the wheel. Higher = a gentle hand does almost nothing and a hard shove does everything.")]
        [SerializeField] float gripBiteSpeed = 0.8f;

        [Tooltip("Shape of that build-up. 1 = straight line, higher = more of the wheel's response saved for a real shove.")]
        [SerializeField] float gripBiteCurve = 1.5f;

        [Tooltip("A wheel moving slower than this (m/s on the ground) counts as stopped, and has to be broken loose.")]
        [SerializeField] float stictionSpeed = 0.08f;

        [Tooltip("How fast the hand must move along the rim (m/s) to break a stopped wheel loose. Below this it just scrubs.")]
        [SerializeField] float breakawaySpeed = 0.35f;

        [Tooltip("Rumble when a stopped wheel breaks loose, so the effort has a moment to it.")]
        [SerializeField] float breakawayRumble = 0.5f;

        [Header("Coasting")]
        [Tooltip("Constant deceleration while no hand grips (rad/s^2).")]
        [SerializeField] float rollingResistance = 0.6f;

        [Tooltip("Extra deceleration proportional to speed while no hand grips (1/s).")]
        [SerializeField] float linearDrag = 0.05f;

        [Tooltip("Hard cap on wheel spin (rad/s).")]
        [SerializeField] float maxAngularSpeed = 25f;

        [Header("Hold to keep moving")]
        [Tooltip("Hand travel around the rim (degrees, since the grab) that is ignored, so tracking wobble doesn't creep the chair.")]
        [SerializeField] float cruiseDeadzoneAngle = 6f;

        [Tooltip("Hand travel around the rim (degrees, since the grab) that gives full cruise speed. Further travel doesn't add more.")]
        [SerializeField] float cruiseFullAngle = 45f;

        [Tooltip("Ground speed (m/s) the wheel cruises at when held at full angle.")]
        [SerializeField] float cruiseMaxGroundSpeed = 1.2f;

        [Header("Debug")]
        [Tooltip("Log grip starts/ends and, while gripped, hand and wheel speeds a few times a second.")]
        [SerializeField] bool logSpeeds = true;

        [Tooltip("Seconds between speed log lines while gripped.")]
        [SerializeField] float speedLogInterval = 0.25f;

        [Tooltip("Draw the rim and tyre rings for this wheel even when it isn't selected.")]
        [SerializeField] bool alwaysDrawGizmos = true;

        [Tooltip("While gripped, draw where each hand grabbed, where it is now, and how much of the cruise angle it has used.")]
        [SerializeField] bool drawGripArcs = false;

        [Tooltip("Tyre ring, ground contact point and rolling-speed arrow. The rim ring and hub are always drawn.")]
        [SerializeField] bool drawTireGizmos = false;

        [Tooltip("Scene-view text next to the wheel: spin, ground speed and grip state.")]
        [SerializeField] bool drawLabels = false;

        [Tooltip("Draw this wheel's hub, axle and ground contact INSIDE THE HEADSET (real objects, not gizmos).")]
        [SerializeField] bool drawRuntimeLines = false;

        class Grip
        {
            public Vector3 previousLocal;   // hand position in chair space last tick
            public float handOmega;         // smoothed hand speed around the hub (rad/s)
            public float heldAngle;         // signed rim travel since the grab, clamped (degrees)
            public float targetOmega;       // what this hand is pulling the wheel toward (rad/s)
            public bool scrubbing;          // pushing a stopped wheel, but not hard enough to move it
        }

        readonly Dictionary<RimHand, Grip> grips = new Dictionary<RimHand, Grip>();
        Transform chair;
        float nextSpeedLogTime;
        bool warnedUnbound;
        LineRenderer hubLine, axleLine, rimLine;

        public float AngularVelocity { get; private set; }
        public float GroundSpeed => AngularVelocity * tireRadius;
        public float RimRadius => rimRadius;
        public bool IsGripped => grips.Count > 0;
        public Vector3 HubPosition => Hub.position;

        Transform Hub => hub != null ? hub : transform;
        Vector3 Axle => ChairForGizmos().right;

        public void Bind(Transform chairRoot) => chair = chairRoot;

        void OnEnable()
        {
            All.Add(this);
            SetRuntimeLinesEnabled(drawRuntimeLines);
        }

        void OnDisable()
        {
            All.Remove(this);
            grips.Clear();
            SetRuntimeLinesEnabled(false);
        }

        void Start()
        {
            if (drawRuntimeLines)
            {
                hubLine = DebugLines.Create($"{name}_hub_line");
                axleLine = DebugLines.Create($"{name}_axle_line");
                rimLine = DebugLines.Create($"{name}_rim_marker", 0.003f);
            }

            if (chair == null)
                Debug.LogError($"[PushRim {name}] Not bound to a chair. Assign this wheel as Left Rim or Right Rim on WheelchairController.", this);
            if (spinVisual == null)
                Debug.LogWarning($"[PushRim {name}] Spin Visual is empty - the wheel will work but won't visibly turn.", this);
            if (rimRadius >= tireRadius)
                Debug.LogWarning($"[PushRim {name}] Rim Radius ({rimRadius}) is not smaller than Tire Radius ({tireRadius}). Check the gizmo rings.", this);
        }

        /// <summary>Nearest point on the rim ring to a world position, and the 3D distance to it.</summary>
        public Vector3 ClosestRimPoint(Vector3 worldPosition, out float distance)
        {
            Vector3 centre = Hub.position;
            Vector3 axle = Axle;

            Vector3 radial = Vector3.ProjectOnPlane(worldPosition - centre, axle);
            if (radial.sqrMagnitude < 1e-6f)
                radial = Vector3.ProjectOnPlane(Vector3.up, axle);

            Vector3 point = centre + radial.normalized * rimRadius;
            distance = Vector3.Distance(worldPosition, point);
            return point;
        }

        public void BeginGrip(RimHand hand, Vector3 handWorldPosition)
        {
            if (chair == null)
            {
                Debug.LogError($"[PushRim {name}] {hand.name} grabbed, but this wheel isn't bound to a chair, so the grip is ignored.", this);
                return;
            }

            // Start the hand's measured speed at the wheel's current speed, so grabbing a rolling
            // wheel doesn't register as an instant stop. If the hand really is still, the smoothing
            // brings it to zero within a few frames and the wheel brakes through the grip.
            grips[hand] = new Grip
            {
                previousLocal = chair.InverseTransformPoint(handWorldPosition),
                handOmega = AngularVelocity,
                heldAngle = 0f,
                targetOmega = AngularVelocity
            };

            if (logSpeeds)
                Debug.Log($"[PushRim {name}] grip BEGIN by {hand.name} (wheel {AngularVelocity:0.00} rad/s, ground {GroundSpeed:0.00} m/s)", this);
        }

        public void EndGrip(RimHand hand)
        {
            if (grips.Remove(hand) && logSpeeds)
                Debug.Log($"[PushRim {name}] grip END by {hand.name} (wheel {AngularVelocity:0.00} rad/s, ground {GroundSpeed:0.00} m/s)", this);
        }

        /// <summary>What a gripping hand wants the wheel to do, minus what it's doing (rad/s). Zero if not gripping.</summary>
        public float SlipFor(RimHand hand)
        {
            return grips.TryGetValue(hand, out Grip grip) ? grip.targetOmega - AngularVelocity : 0f;
        }

        /// <summary>Signed rim travel since the grab for a gripping hand (degrees). Zero if not gripping.</summary>
        public float HeldAngleFor(RimHand hand)
        {
            return grips.TryGetValue(hand, out Grip grip) ? grip.heldAngle : 0f;
        }

        public void SetGroundSpeed(float metresPerSecond)
        {
            AngularVelocity = Mathf.Clamp(metresPerSecond / tireRadius, -maxAngularSpeed, maxAngularSpeed);
        }

        public void ScaleSpeed(float factor) => AngularVelocity *= factor;

        public void Tick(float dt, bool holdToKeepMoving)
        {
            if (chair == null)
            {
                if (!warnedUnbound)
                {
                    warnedUnbound = true;
                    Debug.LogError($"[PushRim {name}] Tick called but no chair is bound.", this);
                }
                return;
            }
            if (dt <= 0f) return;

            if (grips.Count > 0)
            {
                // Everything is measured in chair space, so the chair's own movement never
                // counts as hand movement - only the player's real arm does.
                Vector3 hubLocal = chair.InverseTransformPoint(Hub.position);
                Vector3 axleLocal = Vector3.right;
                float smoothing = 1f - Mathf.Exp(-handSpeedSmoothing * dt);
                float velocityChange = 0f;
                bool logNow = logSpeeds && Time.time >= nextSpeedLogTime;

                foreach (KeyValuePair<RimHand, Grip> entry in grips)
                {
                    Grip grip = entry.Value;
                    Vector3 current = chair.InverseTransformPoint(entry.Key.TrackedPosition);

                    Vector3 previousRadial = Vector3.ProjectOnPlane(grip.previousLocal - hubLocal, axleLocal);
                    Vector3 radial = Vector3.ProjectOnPlane(current - hubLocal, axleLocal);
                    Vector3 radialDir = radial.sqrMagnitude > 1e-6f ? radial.normalized : Vector3.up;

                    // Direction a point on the rim moves when the wheel spins positively (top goes forward).
                    Vector3 tangent = Vector3.Cross(axleLocal, radialDir);

                    Vector3 handVelocity = (current - grip.previousLocal) / dt;
                    float rawOmega = Vector3.Dot(handVelocity, tangent) / rimRadius;
                    float omega = Mathf.Clamp(rawOmega * pushGain, -maxHandAngularSpeed, maxHandAngularSpeed);

                    grip.handOmega = Mathf.Lerp(grip.handOmega, omega, smoothing);
                    grip.previousLocal = current;

                    // Track how far the hand has carried the rim since grabbing. Positive angle about
                    // the axle is the same direction as positive wheel spin.
                    if (previousRadial.sqrMagnitude > 1e-6f && radial.sqrMagnitude > 1e-6f)
                        grip.heldAngle += Vector3.SignedAngle(previousRadial, radial, axleLocal);
                    grip.heldAngle = Mathf.Clamp(grip.heldAngle, -cruiseFullAngle, cruiseFullAngle);

                    grip.targetOmega = holdToKeepMoving ? CombineWithCruise(grip) : grip.handOmega;

                    float slip = grip.targetOmega - AngularVelocity;

                    // A stopped wheel takes a real shove to break loose. Anything gentler scrubs.
                    bool stopped = Mathf.Abs(GroundSpeed) < stictionSpeed;
                    bool tooWeak = stopped && Mathf.Abs(grip.targetOmega) * rimRadius < breakawaySpeed;

                    if (tooWeak)
                    {
                        if (!grip.scrubbing && logSpeeds)
                            Debug.Log($"[PushRim {name}] {entry.Key.name} is pushing too gently to break the wheel loose " +
                                      $"({Mathf.Abs(grip.targetOmega) * rimRadius:0.00} m/s of {breakawaySpeed:0.00} needed)", this);
                        grip.scrubbing = true;
                        continue;
                    }

                    if (grip.scrubbing)
                    {
                        // It just gave way: a kick, so the effort reads as effort.
                        grip.scrubbing = false;
                        if (breakawayRumble > 0f) entry.Key.SendRumble(breakawayRumble, 0.05f);
                    }

                    // The harder the hand outruns the wheel, the more of the wheel it actually gets.
                    // A slow hand slides over the rim; a shove bites.
                    float bite = Mathf.Pow(Mathf.Clamp01(Mathf.Abs(slip) * rimRadius / Mathf.Max(gripBiteSpeed, 0.01f)), gripBiteCurve);

                    // Each hand drags the wheel toward its target, limited by grip strength. A hand
                    // drifting off the rim loosens its hold, so pulling away after a fast push
                    // doesn't brake the wheel on the way out - it behaves like letting go.
                    float maxStep = gripAcceleration * entry.Key.GripStrength * bite * dt;
                    velocityChange += Mathf.Clamp(slip, -maxStep, maxStep);

                    if (logNow)
                        Debug.Log($"[PushRim {name}] {entry.Key.name}: hand speed {handVelocity.magnitude:0.00} m/s " +
                                  $"(along rim {Vector3.Dot(handVelocity, tangent):0.00} m/s), hand omega raw {rawOmega:0.00} x gain {pushGain:0.00}, " +
                                  $"smoothed {grip.handOmega:0.00} rad/s, held {grip.heldAngle:0}deg, grip {entry.Key.GripStrength:P0}{(grip.scrubbing ? " SCRUBBING" : "")} -> target {grip.targetOmega:0.00} rad/s | wheel {AngularVelocity:0.00} rad/s, ground {GroundSpeed:0.00} m/s", this);
                }

                if (logNow)
                    nextSpeedLogTime = Time.time + speedLogInterval;

                AngularVelocity += velocityChange;
            }
            else
            {
                float deceleration = (rollingResistance + Mathf.Abs(AngularVelocity) * linearDrag) * dt;
                AngularVelocity = Mathf.MoveTowards(AngularVelocity, 0f, deceleration);
            }

            // Don't let the wheel creep along below the speed it takes to break it loose.
            if (!IsGripped && Mathf.Abs(GroundSpeed) < stictionSpeed * 0.5f)
                AngularVelocity = 0f;

            AngularVelocity = Mathf.Clamp(AngularVelocity, -maxAngularSpeed, maxAngularSpeed);

            if (spinVisual != null)
                spinVisual.Rotate(Axle, AngularVelocity * Mathf.Rad2Deg * dt, Space.World);

            UpdateRuntimeLines();
        }

        void SetRuntimeLinesEnabled(bool enabled)
        {
            if (hubLine != null) hubLine.enabled = enabled;
            if (axleLine != null) axleLine.enabled = enabled;
            if (rimLine != null) rimLine.enabled = enabled;
        }

        void UpdateRuntimeLines()
        {
            if (!drawRuntimeLines || hubLine == null) return;
            SetRuntimeLinesEnabled(true);

            Vector3 centre = Hub.position;
            Vector3 axle = Axle;
            Vector3 up = Vector3.ProjectOnPlane(Vector3.up, axle).normalized;
            Color state = IsGripped ? Color.green : new Color(0.3f, 1f, 0.3f, 0.8f);

            // Straight down from the hub to where the tyre meets the ground: this wheel's pivot.
            DebugLines.Set(hubLine, centre, centre - up * tireRadius, Color.magenta);

            // The axle the wheel turns about.
            DebugLines.Set(axleLine, centre - axle * 0.12f, centre + axle * 0.12f, Color.red);

            // Front of the rim, so the grabbable ring's size is visible in the headset.
            DebugLines.Set(rimLine, centre, centre + Vector3.Cross(axle, up).normalized * rimRadius, state);
        }

        float CombineWithCruise(Grip grip)
        {
            float magnitude = Mathf.InverseLerp(cruiseDeadzoneAngle, cruiseFullAngle, Mathf.Abs(grip.heldAngle));
            float cruiseOmega = Mathf.Sign(grip.heldAngle) * magnitude * cruiseMaxGroundSpeed / tireRadius;

            // A moving hand always wins when it pushes against the cruise (pulling back to slow down),
            // or when it's pushing harder than the cruise would. Otherwise the held angle keeps the wheel going.
            bool opposing = cruiseOmega != 0f && Mathf.Sign(grip.handOmega) != Mathf.Sign(cruiseOmega) && Mathf.Abs(grip.handOmega) > 0.2f;
            if (opposing || Mathf.Abs(grip.handOmega) > Mathf.Abs(cruiseOmega))
                return grip.handOmega;
            return cruiseOmega;
        }

        // ---------------------------------------------------------------- debug

        Transform ChairForGizmos()
        {
            if (chair != null) return chair;
            WheelchairController controller = GetComponentInParent<WheelchairController>();
            return controller != null ? controller.transform : transform;
        }

        void OnDrawGizmos()
        {
            if (alwaysDrawGizmos) DrawWheelGizmos();
        }

        void OnDrawGizmosSelected()
        {
            if (!alwaysDrawGizmos) DrawWheelGizmos();
        }

        void DrawWheelGizmos()
        {
            Transform chairRoot = ChairForGizmos();
            Vector3 centre = Hub.position;
            Vector3 axle = chairRoot.right;
            Vector3 up = Vector3.ProjectOnPlane(Vector3.up, axle).normalized;
            Vector3 forward = Vector3.Cross(axle, up);

            const int segments = 40;

            // The grabbable ring, and the hub it turns around.
            DrawRing(centre, up, forward, rimRadius, segments, IsGripped ? Color.green : new Color(0.3f, 1f, 0.3f, 0.8f));
            Gizmos.color = Color.magenta;
            Gizmos.DrawWireSphere(centre, 0.03f);
            Gizmos.color = Color.red;
            Gizmos.DrawLine(centre - axle * 0.1f, centre + axle * 0.1f);

            if (drawTireGizmos)
            {
                DrawRing(centre, up, forward, tireRadius, segments, Color.gray);

                // Ground contact point: shows whether the tyre radius reaches the floor.
                Gizmos.color = Color.gray;
                Gizmos.DrawWireSphere(centre - up * tireRadius, 0.01f);
            }

            if (!Application.isPlaying) return;

            if (drawGripArcs) DrawGripArcs(up, forward, axle);

            // Rolling direction and speed, drawn at the contact point.
            if (drawTireGizmos && Mathf.Abs(GroundSpeed) > 0.001f)
            {
                Gizmos.color = GroundSpeed > 0f ? Color.cyan : Color.magenta;
                Vector3 contact = centre - up * tireRadius;
                Gizmos.DrawLine(contact, contact + forward * GroundSpeed * 0.5f);
            }

#if UNITY_EDITOR
            if (drawLabels)
                UnityEditor.Handles.Label(centre + up * (tireRadius + 0.06f),
                $"{name}\n{AngularVelocity:0.00} rad/s\n{GroundSpeed:0.00} m/s\n{(IsGripped ? $"gripped x{grips.Count}, held {MaxHeldAngle():0}deg" : "free")}");
#endif
        }

        void DrawGripArcs(Vector3 up, Vector3 forward, Vector3 axle)
        {
            Vector3 centre = Hub.position;

            foreach (KeyValuePair<RimHand, Grip> entry in grips)
            {
                Grip grip = entry.Value;

                Vector3 handPoint = ClosestRimPoint(entry.Key.TrackedPosition, out _);
                Vector3 currentRadial = handPoint - centre;
                Vector3 startRadial = Quaternion.AngleAxis(-grip.heldAngle, axle) * currentRadial;

                // Where the hand grabbed, and where it is now.
                Gizmos.color = Color.white;
                Gizmos.DrawWireSphere(centre + startRadial, 0.02f);
                Gizmos.color = Color.green;
                Gizmos.DrawSphere(handPoint, 0.02f);

                // The arc the hand has carried the rim through since grabbing.
                Gizmos.color = Mathf.Abs(grip.heldAngle) >= cruiseFullAngle - 0.5f ? Color.red : Color.yellow;
                int steps = Mathf.Max(2, Mathf.CeilToInt(Mathf.Abs(grip.heldAngle) / 5f));
                Vector3 previous = centre + startRadial;
                for (int i = 1; i <= steps; i++)
                {
                    Vector3 next = centre + Quaternion.AngleAxis(grip.heldAngle * i / steps, axle) * startRadial;
                    Gizmos.DrawLine(previous, next);
                    previous = next;
                }

                // Where full cruise would be reached from the grab point, both ways.
                Gizmos.color = new Color(1f, 0f, 0f, 0.4f);
                Gizmos.DrawLine(centre, centre + Quaternion.AngleAxis(cruiseFullAngle, axle) * startRadial);
                Gizmos.DrawLine(centre, centre + Quaternion.AngleAxis(-cruiseFullAngle, axle) * startRadial);

#if UNITY_EDITOR
                UnityEditor.Handles.Label(handPoint + up * 0.06f,
                    $"{entry.Key.name}\nheld {grip.heldAngle:0}deg / {cruiseFullAngle:0}\n" +
                    $"hand {grip.handOmega:0.00} -> target {grip.targetOmega:0.00} rad/s\ngrip {entry.Key.GripStrength:P0}");
#endif
            }
        }

        float MaxHeldAngle()
        {
            float best = 0f;
            foreach (Grip grip in grips.Values)
                if (Mathf.Abs(grip.heldAngle) > Mathf.Abs(best)) best = grip.heldAngle;
            return best;
        }

        static void DrawRing(Vector3 centre, Vector3 a, Vector3 b, float radius, int segments, Color colour)
        {
            Gizmos.color = colour;
            Vector3 previous = centre + a * radius;
            for (int i = 1; i <= segments; i++)
            {
                float angle = i / (float)segments * Mathf.PI * 2f;
                Vector3 next = centre + (a * Mathf.Cos(angle) + b * Mathf.Sin(angle)) * radius;
                Gizmos.DrawLine(previous, next);
                previous = next;
            }
        }
    }
}
