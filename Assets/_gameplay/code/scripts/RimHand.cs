using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.XR.Interaction.Toolkit.Inputs.Haptics;
using UnityEngine.XR.Interaction.Toolkit.Interactors;

namespace Delirium.Wheelchair
{
    /// <summary>
    /// One hand's relationship with the push rims: reads grip, finds the nearest rim, grabs,
    /// releases, and handles haptics. Magnetism and the proximity haptic only start once the hand is
    /// close enough that squeezing would actually grab. The visual hand is drawn toward the rim inside
    /// that zone and sits on it (fully, by default) whenever a grab is possible. The visual never affects the physics - rims always read the
    /// real tracked position.
    ///
    /// Ticked by <see cref="WheelchairController"/> before the rims.
    /// </summary>
    public class RimHand : MonoBehaviour
    {
        [Header("References")]
        [Tooltip("The tracked controller transform (the one with the TrackedPoseDriver).")]
        [SerializeField] Transform trackedController;

        [Tooltip("Hand model root. Must NOT be a child of the tracked controller - make it a sibling.")]
        [SerializeField] Transform visualHand;

        [Tooltip("Optional. Point on the hand that should sit on the rim (child of the tracked controller). Uses the controller origin if empty.")]
        [SerializeField] Transform gripPoint;

        [Tooltip("Grip (middle/ring/little finger). Either this or Pinch Action can hold a rim.")]
        [SerializeField] InputActionProperty gripAction;

        [Tooltip("Trigger (index finger pinch). Either this or Grip Action can hold a rim - use the same action the hand animator reads for Trigger.")]
        [SerializeField] InputActionProperty pinchAction;

        [Tooltip("Thumbstick or touchpad on this controller. Read by anything this hand holds that wants an analogue input, like the chair's joystick.")]
        [SerializeField] InputActionProperty thumbstickAction;

        [SerializeField] HapticImpulsePlayer haptics;

        [Tooltip("Optional XRI interactor on this hand. It can't select anything while this hand holds a rim, and a hand already holding an XRI object won't grab rims.")]
        [SerializeField] XRBaseInteractor interactor;

        [Tooltip("The player's head. Used to tell whether they're looking at the wheel; grabbing gets more forgiving when they aren't.")]
        [SerializeField] Transform head;

        [Header("Zones (metres from the rim)")]
        [Tooltip("Squeezing grip within this distance grabs the rim. Entering it gives the haptic tick and starts the magnet.")]
        [SerializeField] float grabDistance = 0.07f;

        [Tooltip("Grab distance used when the player isn't looking at the wheel. Reaching down blind should still find the rim.")]
        [SerializeField] float blindGrabDistance = 0.45f;

        [Tooltip("Angle between where the player looks and the rim, beyond which the grab counts as blind (degrees).")]
        [SerializeField] float blindLookAngle = 50f;

        [Tooltip("Angle within which the player counts as looking right at the rim (degrees).")]
        [SerializeField] float lookingAtAngle = 25f;

        [Tooltip("While gripping, the grab breaks if the real hand gets this far from the rim. Between grab distance and this, the grip loosens gradually, so a hand sliding off the rim neither pushes nor brakes much.")]
        [SerializeField] float breakDistance = 0.18f;

        [Tooltip("While the hand is in grab range (the haptic tick has fired), how far the visual hand moves onto the rim before gripping. 1 = sits on the rim, 0 = stays on the controller.")]
        [Range(0f, 1f)] [SerializeField] float inRangeVisualSnap = 1f;

        [Header("Grip input")]
        [Tooltip("How far grip or trigger must be squeezed to take hold.")]
        [SerializeField] float gripPressThreshold = 0.55f;

        [Tooltip("How far it must be let go to release. Lower than the press threshold, so a held finger doesn't flicker.")]
        [SerializeField] float gripReleaseThreshold = 0.35f;

        [Tooltip("If on, a hand already squeezing grip grabs the rim as soon as it enters grab distance. If off, grip must be pressed inside the zone.")]
        [SerializeField] bool grabOnEnterWhileHeld = false;

        [Header("Visual")]
        [Tooltip("How quickly the visual hand follows its target offset (1/s).")]
        [SerializeField] float visualFollowSpeed = 30f;

        [Header("Haptics")]
        [SerializeField] float nearTickAmplitude = 0.12f;
        [SerializeField] float nearTickDuration = 0.03f;
        [SerializeField] float grabAmplitude = 0.45f;
        [SerializeField] float grabDuration = 0.06f;
        [Tooltip("Rumble strength while the rim slips through the hand at full slip speed.")]
        [SerializeField] float slipAmplitude = 0.35f;
        [Tooltip("Rim surface slip (m/s) that produces full slip rumble.")]
        [SerializeField] float slipSpeedForFullRumble = 1.5f;

        [Header("Debug")]
        [Tooltip("Log grip presses, grabs, releases and setup problems to the Console.")]
        [SerializeField] bool logEvents = true;

        [Tooltip("Draw a line from the hand to the nearest rim point INSIDE THE HEADSET (a real object, not a gizmo).")]
        [SerializeField] bool drawRuntimeLine = true;

        [Tooltip("Scene-view text next to the hand: grip value, range and nearest rim.")]
        [SerializeField] bool drawLabels = false;

        static readonly Color FarColour = new Color(0.6f, 0.6f, 0.6f, 0.6f);
        static readonly Color GrabZoneColour = Color.cyan;
        static readonly Color GrippedColour = Color.green;
        static readonly Color NoRimColour = Color.red;

        PushRim grippedRim;
        PushRim nearRim;
        bool gripHeld;
        bool pinchHeld;

        Vector3 visualOffsetPosition;
        Quaternion visualOffsetRotation;
        Vector3 visualShift;

        // Debug state, refreshed every Tick.
        PushRim debugNearestRim;
        Vector3 debugNearestPoint;
        float debugNearestDistance = float.MaxValue;
        bool warnedNoAction, warnedNoRims, warnedNoTicks;
        string holdSource = "none";
        int lastTickFrame = -1;
        LineRenderer debugLine;

        public Vector3 TrackedPosition => gripPoint != null ? gripPoint.position : trackedController.position;
        public PushRim GrippedRim => grippedRim;
        /// <summary>Whichever of grip or trigger is squeezed harder (0-1).</summary>
        public float GripValue { get; private set; }

        /// <summary>True while grip or trigger is squeezed past the threshold, whatever the hand is near.</summary>
        public bool IsHolding => gripHeld;

        /// <summary>True while the trigger alone (activate) is squeezed past the threshold.</summary>
        public bool IsActivating => pinchHeld;

        /// <summary>Trigger value on its own (0-1).</summary>
        public float ActivateValue { get; private set; }

        /// <summary>Set by anything else this hand can hold (the joystick), to keep rims from competing for it.</summary>
        public bool SuppressRimGrabs { get; set; }

        /// <summary>Whether a thumbstick action is actually assigned on this hand.</summary>
        public bool HasThumbstick => thumbstickAction.action != null;

        /// <summary>Thumbstick / touchpad on this controller (-1..1 each axis).</summary>
        public Vector2 Thumbstick => thumbstickAction.action != null ? thumbstickAction.action.ReadValue<Vector2>() : Vector2.zero;

        /// <summary>While true, the visual hand is drawn to ExternalSnapPoint instead of a rim.</summary>
        public bool ExternalSnapActive { get; set; }

        /// <summary>Where the visual hand should sit when ExternalSnapActive - set every frame by whatever is claiming the hand.</summary>
        public Vector3 ExternalSnapPoint { get; set; }

        /// <summary>How far onto an external target the hand is drawn before it takes hold (0-1).</summary>
        public float InRangeVisualSnap => inRangeVisualSnap;

        /// <summary>Grab range in use this frame, widened when the player isn't looking at the rim.</summary>
        public float CurrentGrabDistance { get; private set; }

        /// <summary>How firmly this hand holds its rim (0-1). Full within grab distance, fading to nothing at break distance.</summary>
        public float GripStrength { get; private set; }

        void Awake()
        {
            if (trackedController == null)
                LogError("Tracked Controller is not assigned. This hand will do nothing.");

            if (visualHand != null && trackedController != null)
            {
                if (visualHand.IsChildOf(trackedController))
                    LogWarning($"Visual Hand '{visualHand.name}' is a child of the tracked controller. Move it out (sibling under camera_offset) or the magnet/snap will fight the tracking.");

                Quaternion inverse = Quaternion.Inverse(trackedController.rotation);
                visualOffsetPosition = inverse * (visualHand.position - trackedController.position);
                visualOffsetRotation = inverse * visualHand.rotation;
            }

            if (drawRuntimeLine)
                CreateDebugLine();
        }

        void OnEnable()
        {
            if (gripAction.action != null && !gripAction.action.enabled)
                gripAction.action.Enable();

            if (pinchAction.action != null && !pinchAction.action.enabled)
                pinchAction.action.Enable();

            if (thumbstickAction.action != null && !thumbstickAction.action.enabled)
                thumbstickAction.action.Enable();

            // Placing the visual right before render uses the freshest tracked pose.
            Application.onBeforeRender += UpdateVisual;
        }

        void OnDisable()
        {
            Application.onBeforeRender -= UpdateVisual;
            Release("hand disabled");
            nearRim = null;
            if (debugLine != null) debugLine.enabled = false;
        }

        void Update()
        {
            // Catches the most common setup mistake: this hand isn't in WheelchairController's Hands list.
            if (!warnedNoTicks && Time.frameCount > 10 && Time.frameCount - lastTickFrame > 5)
            {
                warnedNoTicks = true;
                LogWarning("Tick() is not being called. Add this hand to the Hands list on WheelchairController.");
            }
        }

        public void Tick()
        {
            lastTickFrame = Time.frameCount;
            if (trackedController == null) return;

            // Either hand shape holds the rim: a full grip with the lower fingers, or an index pinch.
            float grip = gripAction.action != null ? gripAction.action.ReadValue<float>() : 0f;
            float pinch = pinchAction.action != null ? pinchAction.action.ReadValue<float>() : 0f;

            if (gripAction.action == null && pinchAction.action == null && !warnedNoAction)
            {
                warnedNoAction = true;
                LogError("Neither Grip Action nor Pinch Action has an action assigned - this hand can't hold anything.");
            }

            GripValue = Mathf.Max(grip, pinch);
            ActivateValue = pinch;
            pinchHeld = pinchHeld ? pinch > gripReleaseThreshold : pinch > gripPressThreshold;
            holdSource = GripValue <= 0.01f ? "none"
                       : Mathf.Approximately(grip, pinch) ? "grip+pinch"
                       : grip > pinch ? "grip" : "pinch";

            bool wasHeld = gripHeld;
            gripHeld = wasHeld ? GripValue > gripReleaseThreshold : GripValue > gripPressThreshold;
            bool pressedThisFrame = gripHeld && !wasHeld;
            bool releasedThisFrame = !gripHeld && wasHeld;

            Vector3 handPosition = TrackedPosition;
            FindNearestRim(handPosition);
            CurrentGrabDistance = GrabDistanceFor(debugNearestPoint);

            if (PushRim.All.Count == 0 && !warnedNoRims)
            {
                warnedNoRims = true;
                LogError("No PushRim components are registered. Are the wheels active and do they have PushRim?");
            }

            if (pressedThisFrame)
                Log($"hold PRESSED via {holdSource} (grip {grip:0.00}, pinch {pinch:0.00}) - {DescribeNearest()}");
            if (releasedThisFrame)
                Log($"hold released (grip {grip:0.00}, pinch {pinch:0.00})");

            if (grippedRim != null)
            {
                grippedRim.ClosestRimPoint(handPosition, out float gripDistance);

                float fadeStart = Mathf.Min(grabDistance, breakDistance);
                GripStrength = breakDistance > fadeStart
                    ? 1f - Mathf.InverseLerp(fadeStart, breakDistance, gripDistance)
                    : 1f;

                if (!gripHeld) Release("hand let go");
                else if (gripDistance > breakDistance) Release($"hand pulled {gripDistance:0.000} m from rim (break distance {breakDistance:0.000})");
                else if (!grippedRim.isActiveAndEnabled) Release("rim disabled");
                else
                {
                    float slip = Mathf.Abs(grippedRim.SlipFor(this)) * grippedRim.RimRadius;
                    float amplitude = Mathf.Clamp01(slip / slipSpeedForFullRumble) * slipAmplitude;
                    if (amplitude > 0.01f)
                        Pulse(amplitude, Time.deltaTime * 2f);
                }
                return;
            }

            bool holdingXRIObject = (interactor != null && interactor.hasSelection) || SuppressRimGrabs;
            if (holdingXRIObject && pressedThisFrame)
                Log("not grabbing a rim: the XRI interactor is already holding something");

            // "Near" means a squeeze right now would grab. Haptics and magnetism key off this.
            PushRim newNear = !holdingXRIObject && debugNearestDistance <= CurrentGrabDistance ? debugNearestRim : null;
            if (newNear != null && newNear != nearRim)
            {
                Pulse(nearTickAmplitude, nearTickDuration);
                Log($"in grab range of {newNear.name} ({debugNearestDistance:0.000} m)");
            }
            nearRim = newNear;

            bool wantsGrab = pressedThisFrame || (grabOnEnterWhileHeld && gripHeld);

            if (nearRim != null && wantsGrab)
                Grab(nearRim, handPosition);
            else if (pressedThisFrame && !holdingXRIObject && debugNearestRim != null)
                Log($"hold pressed but too far to grab: {debugNearestDistance:0.000} m from {debugNearestRim.name} (grab distance {CurrentGrabDistance:0.000})");
        }

        /// <summary>
        /// Looking straight at a rim asks for precision; reaching for it blind doesn't.
        /// The range widens as the player's gaze turns away from the wheel.
        /// </summary>
        float GrabDistanceFor(Vector3 rimPoint)
        {
            if (head == null || debugNearestRim == null) return grabDistance;

            Vector3 toRim = rimPoint - head.position;
            if (toRim.sqrMagnitude < 1e-6f) return grabDistance;

            float angle = Vector3.Angle(head.forward, toRim);
            float blindness = Mathf.InverseLerp(lookingAtAngle, blindLookAngle, angle);
            return Mathf.Lerp(grabDistance, Mathf.Max(blindGrabDistance, grabDistance), blindness);
        }

        void FindNearestRim(Vector3 handPosition)
        {
            debugNearestRim = null;
            debugNearestDistance = float.MaxValue;

            foreach (PushRim rim in PushRim.All)
            {
                Vector3 point = rim.ClosestRimPoint(handPosition, out float distance);
                if (distance < debugNearestDistance)
                {
                    debugNearestDistance = distance;
                    debugNearestRim = rim;
                    debugNearestPoint = point;
                }
            }
        }

        void Grab(PushRim rim, Vector3 handPosition)
        {
            grippedRim = rim;
            GripStrength = 1f;
            rim.BeginGrip(this, handPosition);

            if (interactor != null)
                interactor.allowSelect = false;

            Pulse(grabAmplitude, grabDuration);
            Log($"GRABBED {rim.name} at {debugNearestDistance:0.000} m");
        }

        void Release(string reason)
        {
            if (grippedRim == null) return;

            PushRim rim = grippedRim;
            grippedRim.EndGrip(this);
            grippedRim = null;
            GripStrength = 0f;

            if (interactor != null)
                interactor.allowSelect = true;

            Log($"released {rim.name}: {reason}");
        }

        void UpdateVisual()
        {
            if (trackedController == null) return;

            Vector3 handPosition = TrackedPosition;

            if (visualHand != null)
            {
                Vector3 basePosition = trackedController.position + trackedController.rotation * visualOffsetPosition;
                Quaternion baseRotation = trackedController.rotation * visualOffsetRotation;

                Vector3 targetShift = Vector3.zero;
                PushRim rim = grippedRim != null ? grippedRim : nearRim;

                if (ExternalSnapActive)
                {
                    // Something else owns this hand (the joystick); it decides where the hand sits.
                    targetShift = ExternalSnapPoint - handPosition;
                }
                else if (rim != null)
                {
                    Vector3 rimPoint = rim.ClosestRimPoint(handPosition, out _);

                    // Same rule as the haptics: if a squeeze would grab, the hand looks like it's on the rim.
                    float pull = grippedRim != null ? 1f : inRangeVisualSnap;

                    targetShift = (rimPoint - handPosition) * pull;
                }

                float follow = 1f - Mathf.Exp(-visualFollowSpeed * Time.deltaTime);
                visualShift = Vector3.Lerp(visualShift, targetShift, follow);

                visualHand.SetPositionAndRotation(basePosition + visualShift, baseRotation);
            }

            UpdateDebugLine(handPosition);
        }

        /// <summary>Haptics on this hand, for other chair parts it can hold.</summary>
        public void SendRumble(float amplitude, float duration) => Pulse(amplitude, duration);

        void Pulse(float amplitude, float duration)
        {
            if (haptics != null)
                haptics.SendHapticImpulse(amplitude, duration);
        }

        // ---------------------------------------------------------------- debug

        Color CurrentStateColour()
        {
            if (grippedRim != null) return GrippedColour;
            if (debugNearestRim == null) return NoRimColour;
            if (debugNearestDistance <= CurrentGrabDistance) return GrabZoneColour;
            return FarColour;
        }

        string DescribeNearest()
        {
            if (debugNearestRim == null) return "no rims registered";
            string zone = debugNearestDistance <= CurrentGrabDistance ? $"can grab (range {CurrentGrabDistance:0.00})" : $"out of range (range {CurrentGrabDistance:0.00})";
            return $"nearest rim {debugNearestRim.name} at {debugNearestDistance:0.000} m ({zone})";
        }

        void CreateDebugLine() => debugLine = DebugLines.Create($"{name}_rim_debug_line");

        void UpdateDebugLine(Vector3 handPosition)
        {
            if (debugLine == null) return;

            debugLine.enabled = drawRuntimeLine;
            if (!drawRuntimeLine) return;

            if (debugNearestRim == null)
            {
                // Nothing to point at - don't leave a stub floating in front of the player.
                debugLine.enabled = false;
                return;
            }

            Vector3 end = grippedRim != null
                ? grippedRim.ClosestRimPoint(handPosition, out _)
                : debugNearestPoint;

            DebugLines.Set(debugLine, handPosition, end, CurrentStateColour());
        }

        void OnDrawGizmos()
        {
            if (trackedController == null) return;

            Vector3 handPosition = TrackedPosition;

            // Grab zone around the hand: inner ring is the looking-at range, outer is the blind range.
            Gizmos.color = GrabZoneColour;
            Gizmos.DrawWireSphere(handPosition, Application.isPlaying ? CurrentGrabDistance : grabDistance);
            Gizmos.color = new Color(0f, 1f, 1f, 0.25f);
            Gizmos.DrawWireSphere(handPosition, blindGrabDistance);

            if (!Application.isPlaying) return;

            Gizmos.color = CurrentStateColour();
            if (debugNearestRim != null)
            {
                Gizmos.DrawLine(handPosition, debugNearestPoint);
                Gizmos.DrawSphere(debugNearestPoint, 0.012f);
            }

            if (gripHeld)
                Gizmos.DrawSphere(handPosition, 0.02f);

#if UNITY_EDITOR
            if (drawLabels)
                UnityEditor.Handles.Label(handPosition + Vector3.up * 0.08f,
                $"{name}\nhold {GripValue:0.00} ({holdSource}){(gripHeld ? " HELD" : "")}\n" +
                (grippedRim != null ? $"GRIPPING {grippedRim.name} ({GripStrength:P0})" : DescribeNearest()));
#endif
        }

        void Log(string message)
        {
            if (logEvents) Debug.Log($"[RimHand {name}] {message}", this);
        }

        void LogWarning(string message) => Debug.LogWarning($"[RimHand {name}] {message}", this);
        void LogError(string message) => Debug.LogError($"[RimHand {name}] {message}", this);
    }
}
