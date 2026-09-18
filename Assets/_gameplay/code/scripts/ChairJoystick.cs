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
        [Tooltip("Armrest mount on the LEFT of the chair.")]
        [SerializeField] Transform leftAnchor;

        [Tooltip("Armrest mount on the RIGHT of the chair.")]
        [SerializeField] Transform rightAnchor;

        [Tooltip("Right-handed puts the joystick on the left armrest, and the other way round.")]
        [SerializeField] ChairHandedness handedness = ChairHandedness.RightHanded;

        [Tooltip("The joystick object that gets moved between anchors. Defaults to this transform.")]
        [SerializeField] Transform mounted;

        [Tooltip("The part that tilts. Its origin is the pivot the stick leans from.")]
        [SerializeField] Transform stickPivot;

        [Header("Power")]
        [Tooltip("Whether the joystick has power. Hook a battery to this later; it does nothing while off.")]
        [SerializeField] bool powered = true;

        [Header("Grabbing")]
        [Tooltip("How close a hand must be to the top of the stick to take hold (m).")]
        [SerializeField] float grabDistance = 0.08f;

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

        [Header("Feedback")]
        [Tooltip("Continuous rumble while driving, scaled by how hard the stick is pushed.")]
        [SerializeField] float driveRumble = 0.08f;

        [SerializeField] bool logEvents = true;

        RimHand holdingHand;
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
        Vector3 StickTop => Pivot.position + Pivot.up * stickLength;

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
            target.localPosition = Vector3.zero;
            target.localRotation = Quaternion.identity;
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
                            || !holdingHand.IsHolding
                            || holdingHand.GrippedRim != null
                            || Vector3.Distance(holdingHand.TrackedPosition, StickTop) > breakDistance;

                if (lost)
                {
                    holdingHand.SuppressRimGrabs = false;
                    if (logEvents) Debug.Log($"[ChairJoystick] released by {holdingHand.name}", this);
                    holdingHand = null;
                }
                return;
            }

            foreach (RimHand hand in hands)
            {
                if (hand == null || !hand.isActiveAndEnabled || hand.GrippedRim != null) continue;

                float distance = Vector3.Distance(hand.TrackedPosition, StickTop);
                bool inRange = distance <= grabDistance;

                // A hand hovering the stick shouldn't also be magnetised to a rim behind it.
                hand.SuppressRimGrabs = inRange;

                if (inRange && hand.IsHolding)
                {
                    holdingHand = hand;
                    hand.SendRumble(0.4f, 0.05f);
                    if (logEvents) Debug.Log($"[ChairJoystick] held by {hand.name} (powered {powered})", this);
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
            Vector3 up = Pivot.up;

            float tilt = Vector3.Angle(up, lean);
            float amount = Mathf.Clamp01(tilt / Mathf.Max(maxTiltAngle, 1f));

            Vector3 flat = Vector3.ProjectOnPlane(lean, up);
            if (flat.sqrMagnitude < 1e-8f) { input = Vector2.zero; return; }

            Transform reference = chair != null ? chair : Pivot;
            Vector3 direction = flat.normalized;
            Vector2 raw = new Vector2(Vector3.Dot(direction, reference.right), Vector3.Dot(direction, reference.forward));

            if (amount < deadzone) { input = Vector2.zero; return; }

            // Rescale so the stick starts moving from zero at the edge of the deadzone.
            float scaled = Mathf.InverseLerp(deadzone, 1f, amount);
            input = raw.normalized * scaled;
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

            Quaternion tilt = Quaternion.AngleAxis(input.magnitude * maxTiltAngle, Vector3.Cross(Vector3.up, stickPivot.parent.InverseTransformDirection(lean)).normalized);
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

            Gizmos.color = IsHeld ? Color.green : new Color(0.3f, 1f, 0.3f, 0.6f);
            Gizmos.DrawLine(pivot.position, StickTop);
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
