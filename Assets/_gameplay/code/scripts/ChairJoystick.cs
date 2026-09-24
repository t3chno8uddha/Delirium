using UnityEngine;

namespace Delirium.Wheelchair
{
    public enum ChairHandedness
    {
        /// <summary>Right-handed player: the joystick sits on the LEFT armrest.</summary>
        RightHanded,
        /// <summary>Left-handed player: the joystick sits on the RIGHT armrest.</summary>
        LeftHanded
    }

    /// <summary>
    /// Powered joystick bolted to one armrest. An additional way to drive the chair, not a
    /// replacement: the rims stay grabbable, and the joystick only answers while it has power.
    ///
    /// Tilting is read from where the hand is relative to the stick's base, so the stick follows the
    /// hand rather than the hand following the stick. Output is written to the wheels as ground
    /// speeds, so the tyres still turn at the speed the chair actually moves.
    ///
    /// Ticked by <see cref="WheelchairController"/> after the hands and before the wheels.
    /// </summary>
    public class ChairJoystick : MonoBehaviour
    {
        [Header("Mounting")]
        [Tooltip("Optional. Leave both anchors empty to leave the joystick wherever it sits in the scene - for a cord-mounted stick the player positions themselves.")]
        [SerializeField] Transform leftAnchor;

        [Tooltip("Armrest mount on the RIGHT of the chair.")]
        [SerializeField] Transform rightAnchor;

        [Tooltip("Right-handed puts the joystick on the left armrest, and the other way round.")]
        [SerializeField] ChairHandedness handedness = ChairHandedness.RightHanded;

        [Tooltip("The joystick object that gets moved between anchors. Defaults to this transform.")]
        [SerializeField] Transform mounted;

        [Tooltip("The part that tilts. Its origin is the pivot the stick leans from.")]
        [SerializeField] Transform stickPivot;

        [Tooltip("Which of the pivot's own axes the stick points along. Y for a Unity-style upright, Z if the model came in Blender's Z-up without conversion.")]
        [SerializeField] Vector3 stickAxis = Vector3.up;

        [Tooltip("Local position on the anchor when mounted. Mirrored on X when the joystick swaps sides.")]
        [SerializeField] Vector3 mountOffsetPosition = Vector3.zero;

        [Tooltip("Local rotation on the anchor when mounted, in degrees.")]
        [SerializeField] Vector3 mountOffsetEuler = Vector3.zero;

        [Header("Power")]
        [Tooltip("Whether the joystick has power. Hook a battery to this later; it does nothing while off.")]
        [SerializeField] bool powered = true;

        [Header("Control")]
        [Tooltip("OFF (hand mode): hold activate and move your wrist, the stick follows your hand. ON (stick mode): hold activate and drive with the thumbstick, the stick follows your thumb.")]
        [SerializeField] bool stickMode = false;

        [Header("Grabbing")]
        [Tooltip("Optional. Only this hand can take the joystick. Leave empty to let either hand hold it.")]
        [SerializeField] RimHand allowedHand;

        [Tooltip("How close a hand must be to the top of the stick to take hold (m). Entering this range gives the haptic tick and pulls the visual hand onto the stick.")]
        [SerializeField] float grabDistance = 0.08f;

        [Tooltip("How far onto the stick the visual hand is drawn while hovering, before taking hold (0-1).")]
        [Range(0f, 1f)] [SerializeField] float hoverVisualSnap = 1f;

        [Tooltip("Tick felt when a hand comes into range of the stick.")]
        [SerializeField] float nearTickAmplitude = 0.12f;

        [SerializeField] float nearTickDuration = 0.03f;

        [Tooltip("The hold breaks if the hand gets this far from the stick's top (m).")]
        [SerializeField] float breakDistance = 0.22f;

        [Tooltip("Distance from the pivot to the top of the stick, where the hand holds it (m).")]
        [SerializeField] float stickLength = 0.09f;

        [Header("Response")]
        [Tooltip("How far the stick can lean (degrees). Leaning this far gives full output.")]
        [SerializeField] float maxTiltAngle = 30f;

        [Tooltip("Lean below this fraction of full is ignored.")]
        [Range(0f, 0.5f)] [SerializeField] float deadzone = 0.12f;

        [Tooltip("Ground speed at full forward lean (m/s).")]
        [SerializeField] float maxSpeed = 1.2f;

        [Tooltip("Turn rate at full sideways lean (deg/s).")]
        [SerializeField] float maxTurnRate = 60f;

        [Tooltip("How quickly the chair reaches the commanded speed (1/s). Lower feels heavier.")]
        [SerializeField] float responseRate = 4f;

        [Tooltip("Thumbstick or touchpad on the holding hand also drives the chair, so it can be nudged without moving your arm. 0 ignores it.")]
        [Range(0f, 1f)] [SerializeField] float thumbstickWeight = 1f;

        [Tooltip("Thumbstick movement below this is ignored.")]
        [Range(0f, 0.9f)] [SerializeField] float thumbstickDeadzone = 0.15f;

        [Header("Feedback")]
        [Tooltip("Continuous rumble while driving, scaled by how hard the stick is pushed.")]
        [SerializeField] float driveRumble = 0.08f;

        [SerializeField] bool logEvents = true;

        RimHand holdingHand;
        RimHand hoveringHand;
        Vector2 lastLean, lastThumb;
        float nextInputLogTime;
        Vector2 input;          // x = turn, y = forward, both -1..1
        float forwardSpeed, turnRate;
        Transform chair;

        public bool IsHeld => holdingHand != null;
        public bool IsDriving => IsHeld && powered && input.sqrMagnitude > 0f;
        public Vector2 Input => input;

        public bool Powered
        {
            get => powered;
            set => powered = value;
        }

        public ChairHandedness Handedness
        {
            get => handedness;
            set { handedness = value; ApplyMount(); }
        }

        Transform Mounted => mounted != null ? mounted : transform;
        Transform Pivot => stickPivot != null ? stickPivot : Mounted;
        Transform Anchor => handedness == ChairHandedness.RightHanded ? leftAnchor : rightAnchor;
        Vector3 StickUp
        {
            get
            {
                Vector3 axis = stickAxis.sqrMagnitude < 1e-6f ? Vector3.up : stickAxis.normalized;
                return Pivot.TransformDirection(axis);
            }
        }

        Vector3 StickTop => Pivot.position + StickUp * stickLength;

        public void Bind(Transform chairRoot)
        {
            chair = chairRoot;
            ApplyMount();
        }

        void OnValidate()
        {
            if (!Application.isPlaying) ApplyMount();
        }

        /// <summary>Parents the joystick to the armrest its handedness calls for.</summary>
        public void ApplyMount()
        {
            Transform anchor = Anchor;
            Transform target = Mounted;
            if (anchor == null || target == null || target == anchor) return;

            target.SetParent(anchor, false);

            // The armrests face each other, so the mount mirrors across the chair's centre line.
            Vector3 offset = mountOffsetPosition;
            Vector3 euler = mountOffsetEuler;
            if (handedness == ChairHandedness.LeftHanded)
            {
                offset.x = -offset.x;
                euler.y = -euler.y;
                euler.z = -euler.z;
            }

            target.localPosition = offset;
            target.localRotation = Quaternion.Euler(euler);
        }

        public void Tick(float dt, RimHand[] hands)
        {
            if (dt <= 0f) return;

            UpdateHold(hands);
            UpdateInput();
            UpdateStickVisual();

            float targetForward = powered ? input.y * maxSpeed : 0f;
            float targetTurn = powered ? input.x * maxTurnRate * Mathf.Deg2Rad : 0f;

            float follow = 1f - Mathf.Exp(-responseRate * dt);
            forwardSpeed = Mathf.Lerp(forwardSpeed, targetForward, follow);
            turnRate = Mathf.Lerp(turnRate, targetTurn, follow);

            if (holdingHand != null && powered && driveRumble > 0f)
            {
                float push = Mathf.Clamp01(input.magnitude);
                if (push > 0.01f) holdingHand.SendRumble(push * driveRumble, dt * 2f);
            }
        }

        void UpdateHold(RimHand[] hands)
        {
            if (holdingHand != null)
            {
                bool lost = !holdingHand.isActiveAndEnabled
                            || !holdingHand.IsActivating
                            || holdingHand.GrippedRim != null
                            || Vector3.Distance(holdingHand.TrackedPosition, StickTop) > breakDistance;

                if (lost)
                {
                    holdingHand.SuppressRimGrabs = false;
                    holdingHand.ExternalSnapActive = false;
                    if (logEvents) Debug.Log($"[ChairJoystick] released by {holdingHand.name}", this);
                    holdingHand = null;
                }
                else
                {
                    holdingHand.ExternalSnapPoint = StickTop;
                    holdingHand.ExternalSnapActive = true;
                }
                return;
            }

            foreach (RimHand hand in hands)
            {
                if (hand == null || !hand.isActiveAndEnabled || hand.GrippedRim != null) continue;
                if (allowedHand != null && hand != allowedHand) continue;

                float distance = Vector3.Distance(hand.TrackedPosition, StickTop);
                bool inRange = distance <= grabDistance;

                // A hand hovering the stick shouldn't also be magnetised to a rim behind it.
                hand.SuppressRimGrabs = inRange;

                // Same cue as the rims: in range means a squeeze would take it, so the hand
                // settles onto the stick and the player feels a tick.
                if (inRange)
                {
                    hand.ExternalSnapPoint = StickTop;
                    hand.ExternalSnapActive = true;

                    if (hoveringHand != hand)
                    {
                        hoveringHand = hand;
                        hand.SendRumble(nearTickAmplitude, nearTickDuration);
                    }
                }
                else
                {
                    hand.ExternalSnapActive = false;
                    if (hoveringHand == hand) hoveringHand = null;
                }

                if (inRange && hand.IsActivating)
                {
                    holdingHand = hand;
                    hand.SendRumble(0.4f, 0.05f);
                    if (logEvents) Debug.Log($"[ChairJoystick] held by {hand.name} in {(stickMode ? "stick" : "hand")} mode (powered {powered})", this);
                    if (stickMode && !hand.HasThumbstick)
                        Debug.LogWarning($"[ChairJoystick] {hand.name} has no Thumbstick Action assigned, so the thumb can't steer. " +
                                         "Assign it on the RimHand (XRI Left/Right Locomotion, Move).", hand);
                    return;
                }
            }
        }

        void UpdateInput()
        {
            if (holdingHand == null)
            {
                input = Vector2.zero;
                return;
            }

            // Where the hand sits relative to the stick's base, in the chair's frame.
            Vector3 lean = holdingHand.TrackedPosition - Pivot.position;
            Vector3 up = StickUp;

            float tilt = Vector3.Angle(up, lean);
            float amount = Mathf.Clamp01(tilt / Mathf.Max(maxTiltAngle, 1f));

            Vector3 flat = Vector3.ProjectOnPlane(lean, up);
            if (flat.sqrMagnitude < 1e-8f) { input = Vector2.zero; return; }

            Transform reference = chair != null ? chair : Pivot;
            Vector3 direction = flat.normalized;
            Vector2 raw = new Vector2(Vector3.Dot(direction, reference.right), Vector3.Dot(direction, reference.forward));

            Vector2 leanInput = Vector2.zero;
            if (amount >= deadzone)
            {
                // Rescale so the stick starts moving from zero at the edge of the deadzone.
                float scaled = Mathf.InverseLerp(deadzone, 1f, amount);
                leanInput = raw.normalized * scaled;
            }

            // The thumb on the stick's own pad counts too, so small corrections don't need the arm.
            Vector2 raw_thumb = holdingHand.Thumbstick;
            Vector2 thumb = raw_thumb.magnitude < thumbstickDeadzone
                ? Vector2.zero
                : raw_thumb.normalized * Mathf.InverseLerp(thumbstickDeadzone, 1f, raw_thumb.magnitude);

            // Hand mode drives from the wrist, stick mode from the thumb. One or the other,
            // so the two can't fight over the same output.
            input = stickMode
                ? Vector2.ClampMagnitude(thumb * thumbstickWeight, 1f)
                : Vector2.ClampMagnitude(leanInput, 1f);

            lastLean = leanInput;
            lastThumb = raw_thumb;

            if (logEvents && Time.time >= nextInputLogTime)
            {
                nextInputLogTime = Time.time + 0.25f;
                Debug.Log($"[ChairJoystick] {(stickMode ? "STICK" : "HAND")} mode: lean {leanInput}, thumb {raw_thumb} " +
                          $"(after deadzone {thumb}) -> input {input}, powered {powered}, " +
                          $"thumbstick assigned {holdingHand.HasThumbstick}", this);
            }
        }

        /// <summary>Releases any hold this joystick has on the hands. Call before disabling the chair.</summary>
        void OnDisable()
        {
            if (holdingHand != null)
            {
                holdingHand.SuppressRimGrabs = false;
                holdingHand.ExternalSnapActive = false;
                holdingHand = null;
            }
            if (hoveringHand != null)
            {
                hoveringHand.SuppressRimGrabs = false;
                hoveringHand.ExternalSnapActive = false;
                hoveringHand = null;
            }
        }

        void UpdateStickVisual()
        {
            if (stickPivot == null) return;

            Transform reference = chair != null ? chair : stickPivot.parent;
            if (reference == null) return;

            Vector3 lean = reference.right * input.x + reference.forward * input.y;
            Quaternion rest = stickPivot.parent != null ? Quaternion.identity : stickPivot.rotation;

            if (lean.sqrMagnitude < 1e-6f)
            {
                stickPivot.localRotation = Quaternion.Slerp(stickPivot.localRotation, rest, 0.4f);
                return;
            }

            Vector3 localLean = stickPivot.parent.InverseTransformDirection(lean);
            Vector3 localAxis = stickAxis.sqrMagnitude < 1e-6f ? Vector3.up : stickAxis.normalized;
            Vector3 tiltAxis = Vector3.Cross(localAxis, localLean);
            if (tiltAxis.sqrMagnitude < 1e-8f) return;

            Quaternion tilt = Quaternion.AngleAxis(input.magnitude * maxTiltAngle, tiltAxis.normalized);
            stickPivot.localRotation = Quaternion.Slerp(stickPivot.localRotation, tilt, 0.4f);
        }

        /// <summary>Ground speeds this joystick is asking the two wheels for.</summary>
        public void GetWheelSpeeds(float wheelbase, out float left, out float right)
        {
            float half = turnRate * wheelbase * 0.5f;
            left = forwardSpeed + half;
            right = forwardSpeed - half;
        }

        void OnDrawGizmos()
        {
            Transform pivot = Pivot;
            if (pivot == null) return;

#if UNITY_EDITOR
            if (Application.isPlaying && IsHeld)
                UnityEditor.Handles.Label(StickTop + Vector3.up * 0.05f,
                    $"lean {lastLean}\nthumb {lastThumb}\ninput {input}");
#endif

            Gizmos.color = IsHeld ? Color.green : new Color(0.3f, 1f, 0.3f, 0.6f);
            Gizmos.DrawLine(pivot.position, StickTop);
            Gizmos.DrawSphere(StickTop, 0.008f);
            Gizmos.DrawWireSphere(StickTop, grabDistance);

            if (leftAnchor != null && rightAnchor != null)
            {
                Gizmos.color = new Color(1f, 1f, 1f, 0.4f);
                Gizmos.DrawWireCube(leftAnchor.position, Vector3.one * 0.04f);
                Gizmos.DrawWireCube(rightAnchor.position, Vector3.one * 0.04f);
                Gizmos.color = Color.yellow;
                Gizmos.DrawWireCube(Anchor.position, Vector3.one * 0.05f);
            }
        }
    }
}
